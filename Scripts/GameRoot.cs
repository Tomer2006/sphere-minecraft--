using System.Text;
using Godot;

namespace SphereMinecraft;

public partial class GameRoot : Node3D
{
	private const string MainMenuScenePath = "res://Scenes/main_menu.tscn";

	/// <summary>
	/// After chunk generation finishes, keep the loading overlay visible at least this long so it is
	/// never skipped when <see cref="PlanetVoxelWorld.IsInitialChunkLoadInProgress"/> goes false in the same frame.
	/// </summary>
	private const ulong MinWorldEntrySplashVisibleMs = 400;

	[Export] public NodePath WorldPath { get; set; } = new("World");
	[Export] public NodePath PlayerPath { get; set; } = new("Player");

	private PlanetVoxelWorld? world;
	private PlanetPlayer? player;
	private CanvasLayer? loadingLayer;
	private Label? loadingTitleLabel;
	private ProgressBar? loadingProgressBar;
	private Label? loadingStatusLabel;
	private CanvasLayer? pauseMenuLayer;
	private PanelContainer? pauseMenuPanel;
	private Label? saveStatusLabel;
	private bool pauseMenuVisible;
	private bool loadingScreenVisible;
	private bool pendingPostLoadPlayerPlacement;
	private bool _worldEntrySplashActive;
	private ulong _worldEntrySplashReleaseAtMs;
	private int _agentDbgUpdateLoadingTicks;
	private int _agentDbgNullWorldTicks;
	private bool _agentDbgLastSetLoadingVisible;

	private CanvasLayer? technicalDebugHudLayer;
	private Label? technicalDebugHudLabel;
	private bool technicalDebugHudVisible;
	private readonly StringBuilder technicalDebugHudBuilder = new(1024);
	private float technicalDebugFpsSmoothed;
	private int technicalDebugFpsFrameCount;
	private float technicalDebugFpsAccum;

	public override void _Ready()
	{
		ProcessMode = ProcessModeEnum.Always;
		RuntimeLog.Info(RuntimeLogChannel.Session, $"GameRoot ready. WorldPath={WorldPath}, PlayerPath={PlayerPath}");
		GameUserSettings.Load();
		GameUserSettings.ApplyAudio();
		GameUserSettings.ApplyGraphics();
		GameUserSettings.ApplySceneLighting();
		BuildLoadingScreen();
		BuildPauseMenu();
		BuildTechnicalDebugHud();
		CallDeferred(nameof(InitializeSession));
	}

	public override void _Process(double delta)
	{
		float dt = (float)delta;
		UpdateLoadingScreen();
		UpdateTechnicalDebugHud(dt);
	}

	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F3 })
		{
			technicalDebugHudVisible = !technicalDebugHudVisible;
			if (technicalDebugHudLayer != null)
			{
				technicalDebugHudLayer.Visible = technicalDebugHudVisible;
			}

			GetViewport().SetInputAsHandled();
			return;
		}

		if (@event is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo)
		{
			return;
		}

		if (loadingScreenVisible)
		{
			return;
		}

		if (keyEvent.Keycode == Key.Escape)
		{
			RuntimeLog.Info(RuntimeLogChannel.Session, "Escape pressed. Toggling pause menu.");
			TogglePauseMenu();
			GetViewport().SetInputAsHandled();
			return;
		}

		if (keyEvent.Keycode == Key.F5)
		{
			RuntimeLog.Info(RuntimeLogChannel.Save, "F5 pressed. Saving current game.");
			SaveCurrentGame("Game saved.");
			GetViewport().SetInputAsHandled();
		}
	}

	public override void _Notification(int what)
	{
		if (what == NotificationWMCloseRequest)
		{
			RuntimeLog.Info(RuntimeLogChannel.Session, "Window close requested. Attempting autosave.");
			SaveCurrentGame();
		}
	}

	private void InitializeSession()
	{
		RuntimeLog.Info(RuntimeLogChannel.Session, "Initializing gameplay session.");
		world = GetNodeOrNull<PlanetVoxelWorld>(WorldPath);
		player = GetNodeOrNull<PlanetPlayer>(PlayerPath);

		if (world is null || player is null)
		{
			RuntimeLog.Error(RuntimeLogChannel.Session, "GameRoot could not find the world or player.");
			return;
		}

		player.SetGameplayEnabled(false);
		pendingPostLoadPlayerPlacement = true;
		_worldEntrySplashActive = true;
		_worldEntrySplashReleaseAtMs = 0;
		SetLoadingScreenVisible(true);
		#region agent log
		AgentDebugLog.Write("B", "GameRoot.cs:InitializeSession", "after SetLoadingScreenVisible(true), scheduling Finish",
			new { loadingLayerVisible = loadingLayer?.Visible ?? false, loadingScreenVisible });
		#endregion
		CallDeferred(nameof(FinishInitializeSession));
	}

	private void FinishInitializeSession()
	{
		if (world is null || player is null)
		{
			return;
		}

		#region agent log
		AgentDebugLog.Write("B", "GameRoot.cs:FinishInitializeSession", "enter",
			new
			{
				beforeLoad = true,
				isInitialChunkLoadInProgress = world.IsInitialChunkLoadInProgress,
				initialTotal = world.InitialChunkLoadTotalCount,
				loadingLayerVisible = loadingLayer?.Visible ?? false
			});
		#endregion

		if (!string.IsNullOrWhiteSpace(SaveGameManager.PendingLoadSlotId) &&
			SaveGameManager.TryLoadGame(SaveGameManager.PendingLoadSlotId!, out SaveGameData? saveData) &&
			saveData is not null)
		{
			RuntimeLog.Info(RuntimeLogChannel.Session,
				$"Loading existing save. Slot={saveData.SlotId}, Name={saveData.SaveName}, SavedAtUtc={saveData.SavedAtUtc}");
			SaveGameManager.CurrentSlotId = saveData.SlotId;
			SaveGameManager.CurrentSaveName = saveData.SaveName;
			world.LoadFromSave(saveData.World);
			PlanetPlayerPersistence.ApplySaveData(player, saveData.Player);
			UpdateSaveStatus("Loaded " + saveData.SaveName + ".");
		}
		else
		{
			NewGameOptions options = SaveGameManager.ConsumePendingNewGame() ?? NewGameOptions.CreateDefault();
			SaveGameManager.CurrentSaveName = string.IsNullOrWhiteSpace(options.SaveName) ? "New World" : options.SaveName;
			RuntimeLog.Info(RuntimeLogChannel.Session,
				$"Starting new world. Name={SaveGameManager.CurrentSaveName}, Seed={options.WorldSeed}, BaseRadius={options.BaseRadiusInBlocks}, HeightVariation={options.HeightVariationInBlocks:0.00}");

			world.BaseRadiusInBlocks = options.BaseRadiusInBlocks;
			world.HeightVariationInBlocks = options.HeightVariationInBlocks;
			world.WorldSeed = options.WorldSeed;
			world.DistortionOptimizedRotationEuler = options.DistortionOptimizedRotationEuler.ToVector3();
			world.GeneratePlanet();

			UpdateSaveStatus("Created " + SaveGameManager.CurrentSaveName + ". Press F5 to create a save.");
		}

		GameUserSettings.ApplyToPlayer(player);

		SaveGameManager.PendingLoadSlotId = null;
		SetPauseMenuVisible(false);
		#region agent log
		AgentDebugLog.Write("A", "GameRoot.cs:FinishInitializeSession", "before UpdateLoadingScreen after load",
			new
			{
				isInitialChunkLoadInProgress = world.IsInitialChunkLoadInProgress,
				initialTotal = world.InitialChunkLoadTotalCount,
				loadingLayerVisible = loadingLayer?.Visible ?? false
			});
		#endregion
		UpdateLoadingScreen();
		#region agent log
		AgentDebugLog.Write("A", "GameRoot.cs:FinishInitializeSession", "after UpdateLoadingScreen",
			new
			{
				isInitialChunkLoadInProgress = world.IsInitialChunkLoadInProgress,
				loadingLayerVisible = loadingLayer?.Visible ?? false,
				loadingScreenVisible
			});
		#endregion
	}

	private void BuildLoadingScreen()
	{
		loadingLayer = new CanvasLayer
		{
			Name = "LoadingScreen",
			Visible = false,
			ProcessMode = ProcessModeEnum.Always
		};
		AddChild(loadingLayer);

		ColorRect dim = new()
		{
			Name = "LoadingDim",
			Color = new Color(0.03f, 0.05f, 0.08f, 1f),
			AnchorRight = 1f,
			AnchorBottom = 1f,
			MouseFilter = Control.MouseFilterEnum.Stop
		};
		loadingLayer.AddChild(dim);

		PanelContainer panel = new()
		{
			Name = "LoadingPanel",
			CustomMinimumSize = new Vector2(420f, 0f),
			AnchorLeft = 0.5f,
			AnchorTop = 0.5f,
			AnchorRight = 0.5f,
			AnchorBottom = 0.5f,
			OffsetLeft = -210f,
			OffsetTop = -90f,
			OffsetRight = 210f,
			OffsetBottom = 90f,
			MouseFilter = Control.MouseFilterEnum.Stop
		};
		loadingLayer.AddChild(panel);

		MarginContainer margin = new();
		margin.AddThemeConstantOverride("margin_left", 24);
		margin.AddThemeConstantOverride("margin_top", 24);
		margin.AddThemeConstantOverride("margin_right", 24);
		margin.AddThemeConstantOverride("margin_bottom", 24);
		panel.AddChild(margin);

		VBoxContainer layout = new();
		layout.AddThemeConstantOverride("separation", 12);
		margin.AddChild(layout);

		loadingTitleLabel = new Label
		{
			Text = "Loading Planet",
			HorizontalAlignment = HorizontalAlignment.Center
		};
		layout.AddChild(loadingTitleLabel);

		loadingStatusLabel = new Label
		{
			Text = "Preparing world...",
			HorizontalAlignment = HorizontalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.WordSmart
		};
		layout.AddChild(loadingStatusLabel);

		loadingProgressBar = new ProgressBar
		{
			MinValue = 0,
			MaxValue = 100,
			Value = 0,
			ShowPercentage = false,
			CustomMinimumSize = new Vector2(360f, 20f)
		};
		layout.AddChild(loadingProgressBar);
		#region agent log
		AgentDebugLog.Write("D", "GameRoot.cs:BuildLoadingScreen", "loading layer created",
			new { layer = loadingLayer?.Name, visible = loadingLayer?.Visible, layerValue = loadingLayer?.Layer });
		#endregion
	}

	private void BuildPauseMenu()
	{
		pauseMenuLayer = new CanvasLayer
		{
			Name = "PauseMenu",
			Visible = false,
			ProcessMode = ProcessModeEnum.Always
		};
		AddChild(pauseMenuLayer);

		ColorRect dim = new()
		{
			Name = "Dim",
			Color = new Color(0.04f, 0.06f, 0.10f, 0.68f),
			AnchorRight = 1f,
			AnchorBottom = 1f,
			MouseFilter = Control.MouseFilterEnum.Stop
		};
		pauseMenuLayer.AddChild(dim);

		pauseMenuPanel = new PanelContainer
		{
			Name = "PausePanel",
			CustomMinimumSize = new Vector2(360f, 0f),
			AnchorLeft = 0.5f,
			AnchorTop = 0.5f,
			AnchorRight = 0.5f,
			AnchorBottom = 0.5f,
			OffsetLeft = -180f,
			OffsetTop = -170f,
			OffsetRight = 180f,
			OffsetBottom = 170f,
			MouseFilter = Control.MouseFilterEnum.Stop
		};
		pauseMenuLayer.AddChild(pauseMenuPanel);

		MarginContainer margin = new()
		{
		};
		margin.AddThemeConstantOverride("margin_left", 24);
		margin.AddThemeConstantOverride("margin_top", 24);
		margin.AddThemeConstantOverride("margin_right", 24);
		margin.AddThemeConstantOverride("margin_bottom", 24);
		pauseMenuPanel.AddChild(margin);

		VBoxContainer layout = new()
		{
		};
		layout.AddThemeConstantOverride("separation", 12);
		margin.AddChild(layout);

		Label title = new()
		{
			Text = "Voxel Planet",
			HorizontalAlignment = HorizontalAlignment.Center
		};
		layout.AddChild(title);

		Button resumeButton = new() { Text = "Resume" };
		resumeButton.Pressed += () => SetPauseMenuVisible(false);
		layout.AddChild(resumeButton);

		Button saveButton = new() { Text = "Save" };
		saveButton.Pressed += () => SaveCurrentGame("Game saved.");
		layout.AddChild(saveButton);

		Button saveNewButton = new() { Text = "Save New Slot" };
		saveNewButton.Pressed += () => SaveCurrentGame("Created new save.", true);
		layout.AddChild(saveNewButton);

		Button saveQuitButton = new() { Text = "Save And Quit To Menu" };
		saveQuitButton.Pressed += SaveAndQuitToMenu;
		layout.AddChild(saveQuitButton);

		Button quitButton = new() { Text = "Quit Desktop" };
		quitButton.Pressed += QuitDesktop;
		layout.AddChild(quitButton);

		saveStatusLabel = new Label
		{
			Text = "",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			HorizontalAlignment = HorizontalAlignment.Center
		};
		layout.AddChild(saveStatusLabel);
	}

	private void BuildTechnicalDebugHud()
	{
		technicalDebugHudLayer = new CanvasLayer
		{
			Name = "TechnicalDebugHud",
			Visible = false,
			Layer = 120,
			ProcessMode = ProcessModeEnum.Always
		};
		AddChild(technicalDebugHudLayer);

		PanelContainer panel = new()
		{
			Name = "TechnicalDebugPanel",
			MouseFilter = Control.MouseFilterEnum.Ignore,
			AnchorLeft = 0f,
			AnchorTop = 0f,
			OffsetLeft = 12f,
			OffsetTop = 12f,
			OffsetRight = 420f,
			OffsetBottom = 420f
		};
		panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
		{
			BgColor = new Color(0.02f, 0.04f, 0.06f, 0.78f),
			BorderColor = new Color(0.25f, 0.45f, 0.55f, 0.5f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
			CornerRadiusTopLeft = 6,
			CornerRadiusTopRight = 6,
			CornerRadiusBottomRight = 6,
			CornerRadiusBottomLeft = 6,
			ContentMarginLeft = 10,
			ContentMarginTop = 8,
			ContentMarginRight = 10,
			ContentMarginBottom = 8
		});
		technicalDebugHudLayer.AddChild(panel);

		technicalDebugHudLabel = new Label
		{
			MouseFilter = Control.MouseFilterEnum.Ignore,
			AutowrapMode = TextServer.AutowrapMode.Off
		};
		technicalDebugHudLabel.AddThemeColorOverride("font_color", new Color(0.88f, 0.94f, 0.98f, 1f));
		technicalDebugHudLabel.AddThemeFontSizeOverride("font_size", 13);
		panel.AddChild(technicalDebugHudLabel);
	}

	private void UpdateTechnicalDebugHud(float delta)
	{
		if (!technicalDebugHudVisible || technicalDebugHudLabel == null)
		{
			return;
		}

		if (delta > 1e-6f)
		{
			technicalDebugFpsAccum += 1f / delta;
			technicalDebugFpsFrameCount++;
			if (technicalDebugFpsFrameCount >= 20)
			{
				technicalDebugFpsSmoothed = technicalDebugFpsAccum / technicalDebugFpsFrameCount;
				technicalDebugFpsAccum = 0f;
				technicalDebugFpsFrameCount = 0;
			}
		}

		StringBuilder b = technicalDebugHudBuilder;
		b.Clear();
		b.AppendLine("Sphere Minecraft — F3 technical");
		b.AppendLine(
			$"FPS (~20 fr avg): {(technicalDebugFpsSmoothed > 0.5f ? technicalDebugFpsSmoothed : 1f / Mathf.Max(delta, 1e-4f)):F1}  Frame ms: {delta * 1000f:F2}");
		b.AppendLine($"Process frame: {Engine.GetProcessFrames()}");
		Viewport vp = GetViewport();
		b.AppendLine(
			$"MSAA 3D: {vp.Msaa3D}  SSAA: {vp.ScreenSpaceAA}  TAA: {vp.UseTaa}  Occlusion: {vp.UseOcclusionCulling}");
		Vector2I win = DisplayServer.WindowGetSize();
		b.AppendLine($"Window: {win.X}×{win.Y}  Paused: {GetTree().Paused}  Loading: {loadingScreenVisible}");

		if (world != null)
		{
			b.AppendLine("--- World ---");
			b.AppendLine($"Seed: {world.WorldSeed}  Base R: {world.BaseRadiusInBlocks}  H±: {world.HeightVariationInBlocks:F2}");
			b.AppendLine($"Block: {world.BlockSize:F2}  Chunk cells: {world.ChunkSizeInCells}  Face res: {world.DebugFaceResolutionCells}");
			b.AppendLine(
				$"Chunks loaded: {world.DebugLoadedChunkCount}  Active targets: {world.DebugActiveRenderChunkCount}  Queue: {world.DebugQueuedChunkBuildCount}");
			int src = world.DebugStreamingRadiusChunk;
			b.AppendLine($"Stream radius chunk: {(src == int.MinValue ? "n/a" : src.ToString())}");
			b.AppendLine($"Initial load busy: {world.IsInitialChunkLoadInProgress}");
		}

		if (player != null)
		{
			b.AppendLine("--- Player ---");
			player.AppendTechnicalDebugHud(b, world);
		}

		if (!string.IsNullOrWhiteSpace(SaveGameManager.CurrentSaveName))
		{
			b.AppendLine("--- Save ---");
			b.AppendLine($"Name: {SaveGameManager.CurrentSaveName}");
			b.AppendLine($"Slot: {SaveGameManager.CurrentSlotId ?? "<none>"}");
		}

		technicalDebugHudLabel.Text = b.ToString();
	}

	private void TogglePauseMenu()
	{
		SetPauseMenuVisible(!pauseMenuVisible);
	}

	private void SetPauseMenuVisible(bool visible)
	{
		if (loadingScreenVisible && visible)
		{
			return;
		}

		pauseMenuVisible = visible;
		RuntimeLog.Info(RuntimeLogChannel.Session, $"Pause menu visibility changed. Visible={visible}");

		if (pauseMenuLayer != null)
		{
			pauseMenuLayer.Visible = visible;
		}

		GetTree().Paused = visible;
		Input.MouseMode = visible || player?.IsInventoryOpen == true
			? Input.MouseModeEnum.Visible
			: Input.MouseModeEnum.Captured;
	}

	private void UpdateLoadingScreen()
	{
		if (world == null)
		{
			#region agent log
			_agentDbgNullWorldTicks++;
			if (_agentDbgNullWorldTicks <= 12)
			{
				AgentDebugLog.Write("E", "GameRoot.cs:UpdateLoadingScreen", "world null early return",
					new { tick = _agentDbgNullWorldTicks });
			}

			#endregion
			return;
		}

		bool chunksLoading = world.IsInitialChunkLoadInProgress;
		if (_worldEntrySplashActive)
		{
			if (!chunksLoading)
			{
				if (_worldEntrySplashReleaseAtMs == 0)
				{
					_worldEntrySplashReleaseAtMs = Time.GetTicksMsec() + MinWorldEntrySplashVisibleMs;
				}
				else if (Time.GetTicksMsec() >= _worldEntrySplashReleaseAtMs)
				{
					_worldEntrySplashActive = false;
				}
			}
		}

		bool showLoadingOverlay = chunksLoading || _worldEntrySplashActive;
		#region agent log
		_agentDbgUpdateLoadingTicks++;
		if (_agentDbgUpdateLoadingTicks <= 45 || _agentDbgLastSetLoadingVisible != showLoadingOverlay)
		{
			AgentDebugLog.Write("A", "GameRoot.cs:UpdateLoadingScreen", "tick",
				new
				{
					tick = _agentDbgUpdateLoadingTicks,
					chunksLoading,
					showLoadingOverlay,
					initialTotal = world.InitialChunkLoadTotalCount,
					layerVisibleBefore = loadingLayer?.Visible ?? false,
					loadingScreenVisible
				});
		}

		_agentDbgLastSetLoadingVisible = showLoadingOverlay;
		#endregion
		SetLoadingScreenVisible(showLoadingOverlay);

		if (loadingProgressBar != null)
		{
			loadingProgressBar.Value = world.InitialChunkLoadProgress * 100f;
		}

		if (loadingStatusLabel != null)
		{
			int total = world.InitialChunkLoadTotalCount;
			int complete = world.InitialChunkLoadCompletedCount;
			loadingStatusLabel.Text = total <= 0
				? "Preparing world..."
				: $"Building planet chunks {complete}/{total}";
		}

		if (player != null)
		{
			if (!chunksLoading && pendingPostLoadPlayerPlacement)
			{
				player.PlaceOnPlanetSurfaceTop();
				pendingPostLoadPlayerPlacement = false;
			}

			player.SetGameplayEnabled(!showLoadingOverlay);
		}

		if (!showLoadingOverlay && !pauseMenuVisible)
		{
			Input.MouseMode = player?.IsInventoryOpen == true
				? Input.MouseModeEnum.Visible
				: Input.MouseModeEnum.Captured;
		}
	}

	private void SetLoadingScreenVisible(bool visible)
	{
		#region agent log
		if (loadingScreenVisible != visible)
		{
			AgentDebugLog.Write("D", "GameRoot.cs:SetLoadingScreenVisible", "visibility change",
				new { visible, layerWasVisible = loadingLayer?.Visible ?? false });
		}
		#endregion
		loadingScreenVisible = visible;
		if (loadingLayer != null)
		{
			loadingLayer.Visible = visible;
		}

		if (visible)
		{
			Input.MouseMode = Input.MouseModeEnum.Visible;
		}
	}

	private bool SaveCurrentGame(string? statusMessage = null, bool forceNewSlot = false)
	{
		if (world is null || player is null)
		{
			RuntimeLog.Warning(RuntimeLogChannel.Save, "Save requested before world/player were ready.");
			return false;
		}

		RuntimeLog.Info(RuntimeLogChannel.Save,
			$"Saving game. ForceNewSlot={forceNewSlot}, CurrentSlot={SaveGameManager.CurrentSlotId ?? "<none>"}, SaveName={SaveGameManager.CurrentSaveName ?? "<unnamed>"}");
		bool saved = SaveGameManager.TrySaveGame(new SaveGameData
		{
			SaveName = SaveGameManager.CurrentSaveName ?? "",
			World = world.CreateSaveData(),
			Player = PlanetPlayerPersistence.CreateSaveData(player)
		}, out string slotId, forceNewSlot);

		RuntimeLog.Info(RuntimeLogChannel.Save,
			saved
				? $"Save completed. Slot={slotId}, SaveName={SaveGameManager.CurrentSaveName ?? "<unnamed>"}"
				: "Save failed.");

		UpdateSaveStatus(saved
			? (statusMessage ?? "Game saved.") + " [" + slotId + "]"
			: "Save failed.");

		return saved;
	}

	private void SaveAndQuitToMenu()
	{
		RuntimeLog.Info(RuntimeLogChannel.Session, "Save-and-quit to menu requested.");
		SaveCurrentGame("Game saved.");
		SaveGameManager.PendingLoadSlotId = null;
		GetTree().Paused = false;
		GetTree().ChangeSceneToFile(MainMenuScenePath);
	}

	private void QuitDesktop()
	{
		RuntimeLog.Info(RuntimeLogChannel.Session, "Quit desktop requested.");
		SaveCurrentGame("Game saved.");
		GetTree().Paused = false;
		GetTree().Quit();
	}

	private void UpdateSaveStatus(string message)
	{
		if (saveStatusLabel != null)
		{
			saveStatusLabel.Text = message;
		}
	}
}
