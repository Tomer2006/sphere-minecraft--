using System.Collections.Generic;
using Godot;

namespace SphereMinecraft;

public partial class MainMenu : Control
{
	private const string GameScenePath = "res://Scenes/main.tscn";

	private readonly RandomNumberGenerator random = new();

	private Button? continueButton;
	private Label? subtitleLabel;
	private Label? saveCountLabel;
	private VBoxContainer? introContent;
	private ScrollContainer? saveScroll;
	private VBoxContainer? saveList;
	private VBoxContainer? homeContent;
	private VBoxContainer? settingsContent;
	private VBoxContainer? createWorldContent;
	private LineEdit? worldNameInput;
	private SpinBox? baseRadiusInput;
	private SpinBox? heightVariationInput;
	private SpinBox? worldSeedInput;
	private Label? createWorldStatusLabel;

	private HSlider? mouseSensitivitySlider;
	private Label? mouseSensitivityValueLabel;
	private HSlider? masterVolumeSlider;
	private Label? masterVolumeValueLabel;
	private CheckButton? fullscreenCheck;
	private OptionButton? msaaOption;
	private OptionButton? screenSpaceAaOption;
	private CheckButton? taaCheck;
	private CheckButton? voxelLinearTexturesCheck;
	private bool syncingSettingsUi;

	public override void _Ready()
	{
		random.Randomize();
		BuildMenu();
		GameUserSettings.Load();
		RefreshSettingsUiFromStore();
		GameUserSettings.ApplyAudio();
		GameUserSettings.ApplyWindowMode();
		GameUserSettings.ApplyGraphics();
		ResetNewWorldForm();
		ShowIntroScreen();
		RefreshState();
	}

	private void BuildMenu()
	{
		AnchorRight = 1f;
		AnchorBottom = 1f;
		MouseFilter = MouseFilterEnum.Stop;

		ColorRect background = new()
		{
			Color = new Color(0.05f, 0.07f, 0.10f, 1f),
			AnchorRight = 1f,
			AnchorBottom = 1f
		};
		AddChild(background);

		PanelContainer panel = new()
		{
			AnchorLeft = 0.5f,
			AnchorTop = 0.5f,
			AnchorRight = 0.5f,
			AnchorBottom = 0.5f,
			OffsetLeft = -380f,
			OffsetTop = -310f,
			OffsetRight = 380f,
			OffsetBottom = 310f
		};
		panel.AddThemeStyleboxOverride("panel", CreatePanelStyle(
			new Color(0.08f, 0.10f, 0.14f, 0.96f),
			new Color(0.34f, 0.44f, 0.52f, 0.38f),
			24,
			1));
		AddChild(panel);

		MarginContainer margin = new();
		margin.AddThemeConstantOverride("margin_left", 32);
		margin.AddThemeConstantOverride("margin_top", 30);
		margin.AddThemeConstantOverride("margin_right", 32);
		margin.AddThemeConstantOverride("margin_bottom", 30);
		panel.AddChild(margin);

		VBoxContainer layout = new();
		layout.AddThemeConstantOverride("separation", 18);
		margin.AddChild(layout);

		Label title = new()
		{
			Text = "Sphere Minecraft",
			HorizontalAlignment = HorizontalAlignment.Center
		};
		title.AddThemeFontSizeOverride("font_size", 30);
		layout.AddChild(title);

		subtitleLabel = new Label
		{
			Text = "",
			HorizontalAlignment = HorizontalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.WordSmart
		};
		subtitleLabel.AddThemeColorOverride("font_color", new Color(0.82f, 0.88f, 0.92f, 0.92f));
		layout.AddChild(subtitleLabel);

		introContent = new VBoxContainer();
		introContent.AddThemeConstantOverride("separation", 18);
		layout.AddChild(introContent);

		BuildIntroContent(introContent);

		homeContent = new VBoxContainer();
		homeContent.Visible = false;
		homeContent.AddThemeConstantOverride("separation", 18);
		layout.AddChild(homeContent);

		BuildHomeContent(homeContent);

		settingsContent = new VBoxContainer
		{
			Visible = false
		};
		settingsContent.AddThemeConstantOverride("separation", 16);
		layout.AddChild(settingsContent);

		BuildSettingsContent(settingsContent);

		createWorldContent = new VBoxContainer
		{
			Visible = false
		};
		createWorldContent.AddThemeConstantOverride("separation", 16);
		layout.AddChild(createWorldContent);

		BuildCreateWorldContent(createWorldContent);
	}

	private void BuildHomeContent(VBoxContainer parent)
	{
		HBoxContainer actionsRow = new();
		actionsRow.AddThemeConstantOverride("separation", 12);
		parent.AddChild(actionsRow);

		continueButton = new Button
		{
			Text = "Continue Latest Save",
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(0f, 46f)
		};
		StylePrimaryButton(continueButton);
		continueButton.Pressed += ContinueLatestSave;
		actionsRow.AddChild(continueButton);

		Button newGameButton = new()
		{
			Text = "New Game",
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(0f, 46f)
		};
		StyleSecondaryButton(newGameButton);
		newGameButton.Pressed += ShowCreateWorldScreen;
		actionsRow.AddChild(newGameButton);

		HBoxContainer savesHeader = new();
		savesHeader.AddThemeConstantOverride("separation", 8);
		parent.AddChild(savesHeader);

		Label savesLabel = new()
		{
			Text = "Saves",
			HorizontalAlignment = HorizontalAlignment.Left,
			SizeFlagsHorizontal = SizeFlags.ExpandFill
		};
		savesLabel.AddThemeFontSizeOverride("font_size", 20);
		savesHeader.AddChild(savesLabel);

		saveCountLabel = new Label
		{
			Text = "",
			HorizontalAlignment = HorizontalAlignment.Right
		};
		saveCountLabel.AddThemeColorOverride("font_color", new Color(0.71f, 0.78f, 0.82f, 0.82f));
		savesHeader.AddChild(saveCountLabel);

		PanelContainer savesPanel = new();
		savesPanel.AddThemeStyleboxOverride("panel", CreatePanelStyle(
			new Color(0.10f, 0.13f, 0.18f, 0.86f),
			new Color(0.34f, 0.44f, 0.52f, 0.28f),
			18,
			1));
		parent.AddChild(savesPanel);

		MarginContainer savesMargin = new();
		savesMargin.AddThemeConstantOverride("margin_left", 14);
		savesMargin.AddThemeConstantOverride("margin_top", 14);
		savesMargin.AddThemeConstantOverride("margin_right", 14);
		savesMargin.AddThemeConstantOverride("margin_bottom", 14);
		savesPanel.AddChild(savesMargin);

		VBoxContainer savesPanelLayout = new();
		savesPanelLayout.AddThemeConstantOverride("separation", 10);
		savesMargin.AddChild(savesPanelLayout);

		Label savesHint = new()
		{
			Text = "Load any slot, or delete saves you no longer need.",
			AutowrapMode = TextServer.AutowrapMode.WordSmart
		};
		savesHint.AddThemeColorOverride("font_color", new Color(0.74f, 0.80f, 0.86f, 0.78f));
		savesPanelLayout.AddChild(savesHint);

		saveScroll = new ScrollContainer
		{
			CustomMinimumSize = new Vector2(0f, 230f),
			SizeFlagsVertical = SizeFlags.ExpandFill
		};
		saveScroll.Resized += QueueSaveListWidthRefresh;
		savesPanelLayout.AddChild(saveScroll);

		saveList = new VBoxContainer();
		saveList.AddThemeConstantOverride("separation", 10);
		saveList.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		saveScroll.AddChild(saveList);

		HBoxContainer footerRow = new();
		parent.AddChild(footerRow);

		Button quitButton = new()
		{
			Text = "Quit",
			CustomMinimumSize = new Vector2(0f, 42f)
		};
		StyleSecondaryButton(quitButton);
		quitButton.Pressed += () => GetTree().Quit();
		footerRow.AddChild(quitButton);
	}

	private void BuildIntroContent(VBoxContainer parent)
	{
		Control spacerTop = new()
		{
			CustomMinimumSize = new Vector2(0f, 36f)
		};
		parent.AddChild(spacerTop);

		Label introTitle = new()
		{
			Text = "Main Menu",
			HorizontalAlignment = HorizontalAlignment.Center
		};
		introTitle.AddThemeFontSizeOverride("font_size", 24);
		parent.AddChild(introTitle);

		Label introHint = new()
		{
			Text = "Play opens the world manager. Settings and quit stay here on the front screen.",
			HorizontalAlignment = HorizontalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.WordSmart
		};
		introHint.AddThemeColorOverride("font_color", new Color(0.78f, 0.84f, 0.88f, 0.82f));
		parent.AddChild(introHint);

		Control spacerMiddle = new()
		{
			CustomMinimumSize = new Vector2(0f, 20f)
		};
		parent.AddChild(spacerMiddle);

		Button playButton = new()
		{
			Text = "Play",
			CustomMinimumSize = new Vector2(0f, 54f)
		};
		StylePrimaryButton(playButton);
		playButton.Pressed += ShowHomeScreen;
		parent.AddChild(playButton);

		Button settingsButton = new()
		{
			Text = "Settings",
			CustomMinimumSize = new Vector2(0f, 54f)
		};
		StyleSecondaryButton(settingsButton);
		settingsButton.Pressed += ShowSettingsScreen;
		parent.AddChild(settingsButton);

		Button quitButton = new()
		{
			Text = "Quit",
			CustomMinimumSize = new Vector2(0f, 54f)
		};
		StyleDangerButton(quitButton);
		quitButton.Pressed += () => GetTree().Quit();
		parent.AddChild(quitButton);
	}

	private void BuildSettingsContent(VBoxContainer parent)
	{
		Label sectionTitle = new()
		{
			Text = "Settings",
			HorizontalAlignment = HorizontalAlignment.Center
		};
		sectionTitle.AddThemeFontSizeOverride("font_size", 24);
		parent.AddChild(sectionTitle);

		Label settingsHint = new()
		{
			Text = "Changes save automatically. Mouse, volume, display, and 3D AA apply immediately. Block texture filtering applies when you load a world.",
			HorizontalAlignment = HorizontalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.WordSmart
		};
		settingsHint.AddThemeColorOverride("font_color", new Color(0.78f, 0.84f, 0.88f, 0.82f));
		parent.AddChild(settingsHint);

		PanelContainer panel = new();
		panel.AddThemeStyleboxOverride("panel", CreatePanelStyle(
			new Color(0.10f, 0.13f, 0.18f, 0.90f),
			new Color(0.34f, 0.44f, 0.52f, 0.30f),
			18,
			1));
		parent.AddChild(panel);

		MarginContainer margin = new();
		margin.AddThemeConstantOverride("margin_left", 18);
		margin.AddThemeConstantOverride("margin_top", 18);
		margin.AddThemeConstantOverride("margin_right", 18);
		margin.AddThemeConstantOverride("margin_bottom", 18);
		panel.AddChild(margin);

		VBoxContainer formLayout = new();
		formLayout.AddThemeConstantOverride("separation", 14);
		margin.AddChild(formLayout);

		mouseSensitivityValueLabel = new Label
		{
			CustomMinimumSize = new Vector2(48f, 0f),
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Center
		};
		mouseSensitivityValueLabel.AddThemeColorOverride("font_color", new Color(0.84f, 0.91f, 0.94f, 0.92f));
		mouseSensitivitySlider = new HSlider
		{
			MinValue = 0.02,
			MaxValue = 0.45,
			Step = 0.01,
			SizeFlagsHorizontal = SizeFlags.ExpandFill
		};
		mouseSensitivitySlider.ValueChanged += OnMouseSensitivitySliderChanged;
		HBoxContainer mouseRow = new();
		mouseRow.AddThemeConstantOverride("separation", 10);
		mouseRow.AddChild(mouseSensitivitySlider);
		mouseRow.AddChild(mouseSensitivityValueLabel);
		formLayout.AddChild(CreateFieldBlock("Mouse sensitivity", mouseRow));

		masterVolumeValueLabel = new Label
		{
			CustomMinimumSize = new Vector2(44f, 0f),
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Center
		};
		masterVolumeValueLabel.AddThemeColorOverride("font_color", new Color(0.84f, 0.91f, 0.94f, 0.92f));
		masterVolumeSlider = new HSlider
		{
			MinValue = 0.0,
			MaxValue = 1.0,
			Step = 0.01,
			SizeFlagsHorizontal = SizeFlags.ExpandFill
		};
		masterVolumeSlider.ValueChanged += OnMasterVolumeSliderChanged;
		HBoxContainer volumeRow = new();
		volumeRow.AddThemeConstantOverride("separation", 10);
		volumeRow.AddChild(masterVolumeSlider);
		volumeRow.AddChild(masterVolumeValueLabel);
		formLayout.AddChild(CreateFieldBlock("Master volume", volumeRow));

		fullscreenCheck = new CheckButton
		{
			Text = "Exclusive fullscreen"
		};
		fullscreenCheck.Toggled += OnFullscreenToggled;
		formLayout.AddChild(CreateFieldBlock("Display", fullscreenCheck));

		msaaOption = CreateGraphicsOptionButton();
		msaaOption.AddItem("Disabled", 0);
		msaaOption.AddItem("2× MSAA", 1);
		msaaOption.AddItem("4× MSAA", 2);
		msaaOption.AddItem("8× MSAA", 3);
		msaaOption.ItemSelected += OnMsaaItemSelected;
		formLayout.AddChild(CreateFieldBlock("3D MSAA (geometry edges)", msaaOption));

		screenSpaceAaOption = CreateGraphicsOptionButton();
		screenSpaceAaOption.AddItem("None", 0);
		screenSpaceAaOption.AddItem("FXAA", 1);
		screenSpaceAaOption.AddItem("SMAA 1×", 2);
		screenSpaceAaOption.ItemSelected += OnScreenSpaceAaItemSelected;
		formLayout.AddChild(CreateFieldBlock("Screen-space antialiasing", screenSpaceAaOption));

		taaCheck = new CheckButton
		{
			Text = "Temporal AA (TAA) — Forward+; can blur or ghost slightly"
		};
		taaCheck.Toggled += OnTaaToggled;
		formLayout.AddChild(CreateFieldBlock("Temporal AA", taaCheck));

		voxelLinearTexturesCheck = new CheckButton
		{
			Text = "Smooth block textures (linear + anisotropic filtering)"
		};
		voxelLinearTexturesCheck.Toggled += OnVoxelLinearTexturesToggled;
		formLayout.AddChild(CreateFieldBlock("Block textures", voxelLinearTexturesCheck));

		HBoxContainer actions = new();
		actions.AddThemeConstantOverride("separation", 12);
		parent.AddChild(actions);

		Button backButton = new()
		{
			Text = "Back",
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(0f, 46f)
		};
		StyleSecondaryButton(backButton);
		backButton.Pressed += ShowIntroScreen;
		actions.AddChild(backButton);
	}

	private void BuildCreateWorldContent(VBoxContainer parent)
	{
		Label sectionTitle = new()
		{
			Text = "Create World",
			HorizontalAlignment = HorizontalAlignment.Center
		};
		sectionTitle.AddThemeFontSizeOverride("font_size", 24);
		parent.AddChild(sectionTitle);

		Label hint = new()
		{
			Text = "Name the world and choose the wrapped-cube planet settings before you start.",
			HorizontalAlignment = HorizontalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.WordSmart
		};
		hint.AddThemeColorOverride("font_color", new Color(0.78f, 0.84f, 0.88f, 0.82f));
		parent.AddChild(hint);

		PanelContainer formPanel = new();
		formPanel.AddThemeStyleboxOverride("panel", CreatePanelStyle(
			new Color(0.10f, 0.13f, 0.18f, 0.90f),
			new Color(0.34f, 0.44f, 0.52f, 0.30f),
			18,
			1));
		parent.AddChild(formPanel);

		MarginContainer formMargin = new();
		formMargin.AddThemeConstantOverride("margin_left", 18);
		formMargin.AddThemeConstantOverride("margin_top", 18);
		formMargin.AddThemeConstantOverride("margin_right", 18);
		formMargin.AddThemeConstantOverride("margin_bottom", 18);
		formPanel.AddChild(formMargin);

		VBoxContainer formLayout = new();
		formLayout.AddThemeConstantOverride("separation", 12);
		formMargin.AddChild(formLayout);

		worldNameInput = new LineEdit
		{
			PlaceholderText = "World name"
		};
		formLayout.AddChild(CreateFieldBlock("World Name", worldNameInput));

		baseRadiusInput = CreateSpinBox(6, 5000, 50, 1, true);
		formLayout.AddChild(CreateFieldBlock("Planet Base Size Radius In Blocks", baseRadiusInput));

		heightVariationInput = CreateSpinBox(0, 64, 2.5f, 0.1f, false);
		formLayout.AddChild(CreateFieldBlock("Height Variation In Blocks", heightVariationInput));

		Label mappingLabel = new()
		{
			Text = "Pure Adjusted Spherical Cube (ASC): standard cube-to-sphere projection on each face.",
			AutowrapMode = TextServer.AutowrapMode.WordSmart
		};
		mappingLabel.AddThemeColorOverride("font_color", new Color(0.84f, 0.91f, 0.94f, 0.92f));
		formLayout.AddChild(CreateFieldBlock("Spherical Mapping Technique", mappingLabel));

		HBoxContainer seedRow = new();
		seedRow.AddThemeConstantOverride("separation", 10);

		worldSeedInput = CreateSpinBox(-999999999, 999999999, 1337, 1, true);
		worldSeedInput.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		seedRow.AddChild(worldSeedInput);

		Button randomSeedButton = new()
		{
			Text = "Randomize",
			CustomMinimumSize = new Vector2(110f, 0f)
		};
		StyleSecondaryButton(randomSeedButton);
		randomSeedButton.Pressed += RandomizeSeed;
		seedRow.AddChild(randomSeedButton);

		formLayout.AddChild(CreateFieldBlock("Planet Seed", seedRow));

		createWorldStatusLabel = new Label
		{
			Text = "",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			Visible = false
		};
		createWorldStatusLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.52f, 0.55f, 1f));
		parent.AddChild(createWorldStatusLabel);

		HBoxContainer actions = new();
		actions.AddThemeConstantOverride("separation", 12);
		parent.AddChild(actions);

		Button backButton = new()
		{
			Text = "Back",
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(0f, 46f)
		};
		StyleSecondaryButton(backButton);
		backButton.Pressed += ShowHomeScreen;
		actions.AddChild(backButton);

		Button createButton = new()
		{
			Text = "Create World",
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(0f, 46f)
		};
		StylePrimaryButton(createButton);
		createButton.Pressed += StartNewGameFromForm;
		actions.AddChild(createButton);
	}

	private void RefreshState()
	{
		List<SaveSlotInfo> saves = SaveGameManager.ListSaveSlots();

		if (continueButton != null)
		{
			continueButton.Disabled = saves.Count == 0;
		}

		if (introContent?.Visible == true && subtitleLabel != null)
		{
			subtitleLabel.Text = saves.Count > 0
				? "Start from the front screen, then open your worlds."
				: "Start from the front screen, then create your first world.";
		}
		else if (homeContent?.Visible == true && subtitleLabel != null)
		{
			subtitleLabel.Text = saves.Count > 0
				? "Continue loads the newest save.\nYou can also pick any save from the list below."
				: "No saves found yet.\nStart a new world and save from the pause menu or with F5.";
		}

		if (saveCountLabel != null)
		{
			saveCountLabel.Text = saves.Count == 1 ? "1 slot" : $"{saves.Count} slots";
		}

		RebuildSaveList(saves);
		QueueSaveListWidthRefresh();
	}

	private void RebuildSaveList(List<SaveSlotInfo> saves)
	{
		if (saveList is null)
		{
			return;
		}

		foreach (Node child in saveList.GetChildren())
		{
			child.QueueFree();
		}

		if (saves.Count == 0)
		{
			PanelContainer emptyCard = new();
			emptyCard.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			emptyCard.AddThemeStyleboxOverride("panel", CreatePanelStyle(
				new Color(0.12f, 0.14f, 0.18f, 0.92f),
				new Color(0.30f, 0.38f, 0.46f, 0.25f),
				16,
				1));
			saveList.AddChild(emptyCard);

			MarginContainer emptyMargin = new();
			emptyMargin.AddThemeConstantOverride("margin_left", 16);
			emptyMargin.AddThemeConstantOverride("margin_top", 16);
			emptyMargin.AddThemeConstantOverride("margin_right", 16);
			emptyMargin.AddThemeConstantOverride("margin_bottom", 16);
			emptyCard.AddChild(emptyMargin);

			Label emptyLabel = new()
			{
				Text = "No saves yet.\nCreate a new world, then save from the pause menu or with F5.",
				HorizontalAlignment = HorizontalAlignment.Center,
				AutowrapMode = TextServer.AutowrapMode.WordSmart
			};
			emptyLabel.AddThemeColorOverride("font_color", new Color(0.78f, 0.82f, 0.88f, 0.9f));
			emptyMargin.AddChild(emptyLabel);
			return;
		}

		for (int index = 0; index < saves.Count; index++)
		{
			SaveSlotInfo save = saves[index];
			bool isLatest = index == 0;

			PanelContainer rowPanel = new();
			rowPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			rowPanel.AddThemeStyleboxOverride("panel", CreatePanelStyle(
				isLatest
					? new Color(0.12f, 0.16f, 0.21f, 0.96f)
					: new Color(0.12f, 0.14f, 0.18f, 0.92f),
				isLatest
					? new Color(0.42f, 0.56f, 0.68f, 0.45f)
					: new Color(0.30f, 0.38f, 0.46f, 0.25f),
				16,
				1));
			saveList.AddChild(rowPanel);

			MarginContainer rowMargin = new();
			rowMargin.AddThemeConstantOverride("margin_left", 14);
			rowMargin.AddThemeConstantOverride("margin_top", 12);
			rowMargin.AddThemeConstantOverride("margin_right", 14);
			rowMargin.AddThemeConstantOverride("margin_bottom", 12);
			rowPanel.AddChild(rowMargin);

			VBoxContainer card = new();
			card.AddThemeConstantOverride("separation", 10);
			rowMargin.AddChild(card);

			HBoxContainer topRow = new();
			topRow.AddThemeConstantOverride("separation", 10);
			card.AddChild(topRow);

			Label info = new()
			{
				Text = save.GetDisplayText(),
				SizeFlagsHorizontal = SizeFlags.ExpandFill,
				VerticalAlignment = VerticalAlignment.Center,
				AutowrapMode = TextServer.AutowrapMode.WordSmart
			};
			info.AddThemeColorOverride("font_color", new Color(0.94f, 0.97f, 0.98f, 0.96f));
			topRow.AddChild(info);

			if (isLatest)
			{
				Label latestLabel = new()
				{
					Text = "LATEST",
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center,
					CustomMinimumSize = new Vector2(72f, 28f)
				};
				latestLabel.AddThemeFontSizeOverride("font_size", 12);
				latestLabel.AddThemeColorOverride("font_color", new Color(0.88f, 0.93f, 0.98f, 1f));

				PanelContainer badge = new();
				badge.AddThemeStyleboxOverride("panel", CreatePanelStyle(
					new Color(0.28f, 0.40f, 0.54f, 0.95f),
					new Color(0.50f, 0.65f, 0.82f, 0.35f),
					12,
					0));
				badge.AddChild(latestLabel);
				topRow.AddChild(badge);
			}

			HBoxContainer actionRow = new();
			actionRow.Alignment = BoxContainer.AlignmentMode.End;
			actionRow.AddThemeConstantOverride("separation", 8);
			card.AddChild(actionRow);

			Button loadButton = new()
			{
				Text = "Load",
				CustomMinimumSize = new Vector2(96f, 36f)
			};
			StylePrimaryButton(loadButton);
			loadButton.Pressed += () => LoadSave(save.SlotId);
			actionRow.AddChild(loadButton);

			Button deleteButton = new()
			{
				Text = "Delete",
				CustomMinimumSize = new Vector2(96f, 36f)
			};
			StyleDangerButton(deleteButton);
			deleteButton.Pressed += () => DeleteSave(save.SlotId);
			actionRow.AddChild(deleteButton);
		}
	}

	private void ShowHomeScreen()
	{
		if (introContent != null)
		{
			introContent.Visible = false;
		}

		if (homeContent != null)
		{
			homeContent.Visible = true;
		}

		if (settingsContent != null)
		{
			settingsContent.Visible = false;
		}

		if (createWorldContent != null)
		{
			createWorldContent.Visible = false;
		}

		RefreshState();
	}

	private void ShowIntroScreen()
	{
		if (introContent != null)
		{
			introContent.Visible = true;
		}

		if (homeContent != null)
		{
			homeContent.Visible = false;
		}

		if (settingsContent != null)
		{
			settingsContent.Visible = false;
		}

		if (createWorldContent != null)
		{
			createWorldContent.Visible = false;
		}

		RefreshState();
	}

	private void ShowSettingsScreen()
	{
		if (introContent != null)
		{
			introContent.Visible = false;
		}

		if (homeContent != null)
		{
			homeContent.Visible = false;
		}

		if (settingsContent != null)
		{
			settingsContent.Visible = true;
		}

		if (createWorldContent != null)
		{
			createWorldContent.Visible = false;
		}

		if (subtitleLabel != null)
		{
			subtitleLabel.Text = "Adjust the game options here.";
		}

		RefreshSettingsUiFromStore();
	}

	private void RefreshSettingsUiFromStore()
	{
		syncingSettingsUi = true;
		try
		{
			if (mouseSensitivitySlider != null)
			{
				mouseSensitivitySlider.Value = GameUserSettings.MouseSensitivity;
			}

			if (masterVolumeSlider != null)
			{
				masterVolumeSlider.Value = GameUserSettings.MasterVolumeLinear;
			}

			if (fullscreenCheck != null)
			{
				fullscreenCheck.ButtonPressed = GameUserSettings.Fullscreen;
			}

			if (msaaOption != null)
			{
				msaaOption.Selected = GameUserSettings.GraphicsMsaa3D;
			}

			if (screenSpaceAaOption != null)
			{
				screenSpaceAaOption.Selected = GameUserSettings.GraphicsScreenSpaceAa;
			}

			if (taaCheck != null)
			{
				taaCheck.ButtonPressed = GameUserSettings.GraphicsUseTaa;
			}

			if (voxelLinearTexturesCheck != null)
			{
				voxelLinearTexturesCheck.ButtonPressed = GameUserSettings.GraphicsVoxelLinearTextures;
			}
		}
		finally
		{
			syncingSettingsUi = false;
		}

		UpdateSettingsValueLabels();
	}

	private void UpdateSettingsValueLabels()
	{
		if (mouseSensitivityValueLabel != null && mouseSensitivitySlider != null)
		{
			mouseSensitivityValueLabel.Text = $"{mouseSensitivitySlider.Value:0.00}";
		}

		if (masterVolumeValueLabel != null && masterVolumeSlider != null)
		{
			masterVolumeValueLabel.Text = $"{Mathf.RoundToInt((float)masterVolumeSlider.Value * 100f)}%";
		}
	}

	private void OnMouseSensitivitySliderChanged(double value)
	{
		if (syncingSettingsUi)
		{
			return;
		}

		GameUserSettings.MouseSensitivity = (float)value;
		UpdateSettingsValueLabels();
		GameUserSettings.Save();
	}

	private void OnMasterVolumeSliderChanged(double value)
	{
		if (syncingSettingsUi)
		{
			return;
		}

		GameUserSettings.MasterVolumeLinear = (float)value;
		UpdateSettingsValueLabels();
		GameUserSettings.ApplyAudio();
		GameUserSettings.Save();
	}

	private void OnFullscreenToggled(bool pressed)
	{
		if (syncingSettingsUi)
		{
			return;
		}

		GameUserSettings.Fullscreen = pressed;
		GameUserSettings.ApplyWindowMode();
		GameUserSettings.Save();
	}

	private void OnMsaaItemSelected(long index)
	{
		if (syncingSettingsUi)
		{
			return;
		}

		GameUserSettings.GraphicsMsaa3D = (int)index;
		GameUserSettings.ApplyGraphics();
		GameUserSettings.Save();
	}

	private void OnScreenSpaceAaItemSelected(long index)
	{
		if (syncingSettingsUi)
		{
			return;
		}

		GameUserSettings.GraphicsScreenSpaceAa = (int)index;
		GameUserSettings.ApplyGraphics();
		GameUserSettings.Save();
	}

	private void OnTaaToggled(bool pressed)
	{
		if (syncingSettingsUi)
		{
			return;
		}

		GameUserSettings.GraphicsUseTaa = pressed;
		GameUserSettings.ApplyGraphics();
		GameUserSettings.Save();
	}

	private void OnVoxelLinearTexturesToggled(bool pressed)
	{
		if (syncingSettingsUi)
		{
			return;
		}

		GameUserSettings.GraphicsVoxelLinearTextures = pressed;
		GameUserSettings.Save();
	}

	private void ShowCreateWorldScreen()
	{
		if (introContent != null)
		{
			introContent.Visible = false;
		}

		if (homeContent != null)
		{
			homeContent.Visible = false;
		}

		if (settingsContent != null)
		{
			settingsContent.Visible = false;
		}

		if (createWorldContent != null)
		{
			createWorldContent.Visible = true;
		}

		if (subtitleLabel != null)
		{
			subtitleLabel.Text = "Set the world name, wrapped cube radius, terrain height, and seed.";
		}

		SetCreateWorldStatus("");
		worldNameInput?.GrabFocus();
	}

	private void ResetNewWorldForm()
	{
		if (worldNameInput != null)
		{
			worldNameInput.Text = "New World";
		}

		if (baseRadiusInput != null)
		{
			baseRadiusInput.Value = 50;
		}

		if (heightVariationInput != null)
		{
			heightVariationInput.Value = 2.5f;
		}

		if (worldSeedInput != null)
		{
			worldSeedInput.Value = 1337;
		}

		SetCreateWorldStatus("");
	}

	private void StartNewGameFromForm()
	{
		if (worldNameInput is null ||
			baseRadiusInput is null ||
			heightVariationInput is null ||
			worldSeedInput is null)
		{
			return;
		}

		string saveName = worldNameInput.Text.Trim();
		if (string.IsNullOrWhiteSpace(saveName))
		{
			SetCreateWorldStatus("World name is required.");
			return;
		}

		SaveGameManager.BeginNewGame(new NewGameOptions
		{
			SaveName = saveName,
			BaseRadiusInBlocks = Mathf.RoundToInt((float)baseRadiusInput.Value),
			HeightVariationInBlocks = (float)heightVariationInput.Value,
			WorldSeed = Mathf.RoundToInt((float)worldSeedInput.Value),
			DistortionOptimizedRotationEuler = new Vector3Save()
		});
		GetTree().ChangeSceneToFile(GameScenePath);
	}

	private void RandomizeSeed()
	{
		if (worldSeedInput == null)
		{
			return;
		}

		worldSeedInput.Value = random.RandiRange(-999999999, 999999999);
	}

	private void SetCreateWorldStatus(string message)
	{
		if (createWorldStatusLabel == null)
		{
			return;
		}

		createWorldStatusLabel.Text = message;
		createWorldStatusLabel.Visible = !string.IsNullOrWhiteSpace(message);
	}

	private void ContinueLatestSave()
	{
		List<SaveSlotInfo> saves = SaveGameManager.ListSaveSlots();
		if (saves.Count == 0)
		{
			RefreshState();
			return;
		}

		LoadSave(saves[0].SlotId);
	}

	private void LoadSave(string slotId)
	{
		SaveGameManager.BeginLoadSlot(slotId);
		GetTree().ChangeSceneToFile(GameScenePath);
	}

	private void DeleteSave(string slotId)
	{
		SaveGameManager.DeleteSave(slotId);
		RefreshState();
	}

	private void QueueSaveListWidthRefresh()
	{
		CallDeferred(nameof(RefreshSaveListWidth));
	}

	private void RefreshSaveListWidth()
	{
		if (saveScroll is null || saveList is null)
		{
			return;
		}

		float viewportWidth = saveScroll.Size.X;
		VScrollBar? verticalScrollBar = saveScroll.GetVScrollBar();
		if (verticalScrollBar != null && verticalScrollBar.Visible)
		{
			viewportWidth -= verticalScrollBar.Size.X;
		}

		saveList.CustomMinimumSize = new Vector2(Mathf.Max(0f, viewportWidth), saveList.CustomMinimumSize.Y);
	}

	private static SpinBox CreateSpinBox(double minValue, double maxValue, double value, double step, bool rounded)
	{
		return new SpinBox
		{
			MinValue = minValue,
			MaxValue = maxValue,
			Value = value,
			Step = step,
			Rounded = rounded,
			SelectAllOnFocus = true,
			SizeFlagsHorizontal = SizeFlags.ExpandFill
		};
	}

	private static OptionButton CreateGraphicsOptionButton() =>
		new()
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(160f, 0f)
		};

	private static VBoxContainer CreateFieldBlock(string labelText, Control control)
	{
		VBoxContainer field = new();
		field.AddThemeConstantOverride("separation", 6);

		Label label = new()
		{
			Text = labelText
		};
		label.AddThemeColorOverride("font_color", new Color(0.82f, 0.88f, 0.92f, 0.92f));
		field.AddChild(label);
		field.AddChild(control);
		return field;
	}

	private static StyleBoxFlat CreatePanelStyle(Color bgColor, Color borderColor, int cornerRadius, int borderWidth)
	{
		return new StyleBoxFlat
		{
			BgColor = bgColor,
			BorderColor = borderColor,
			CornerRadiusTopLeft = cornerRadius,
			CornerRadiusTopRight = cornerRadius,
			CornerRadiusBottomRight = cornerRadius,
			CornerRadiusBottomLeft = cornerRadius,
			BorderWidthLeft = borderWidth,
			BorderWidthTop = borderWidth,
			BorderWidthRight = borderWidth,
			BorderWidthBottom = borderWidth
		};
	}

	private static void StylePrimaryButton(Button button)
	{
		button.AddThemeStyleboxOverride("normal", CreatePanelStyle(
			new Color(0.30f, 0.46f, 0.62f, 0.96f),
			new Color(0.48f, 0.62f, 0.78f, 0.55f),
			14,
			1));
		button.AddThemeStyleboxOverride("hover", CreatePanelStyle(
			new Color(0.36f, 0.52f, 0.70f, 0.98f),
			new Color(0.56f, 0.70f, 0.86f, 0.62f),
			14,
			1));
		button.AddThemeStyleboxOverride("pressed", CreatePanelStyle(
			new Color(0.22f, 0.36f, 0.50f, 1f),
			new Color(0.44f, 0.58f, 0.74f, 0.45f),
			14,
			1));
		button.AddThemeColorOverride("font_color", new Color(0.96f, 0.97f, 0.99f, 1f));
	}

	private static void StyleSecondaryButton(Button button)
	{
		button.AddThemeStyleboxOverride("normal", CreatePanelStyle(
			new Color(0.15f, 0.18f, 0.23f, 0.96f),
			new Color(0.36f, 0.44f, 0.52f, 0.40f),
			14,
			1));
		button.AddThemeStyleboxOverride("hover", CreatePanelStyle(
			new Color(0.19f, 0.23f, 0.29f, 0.98f),
			new Color(0.46f, 0.56f, 0.65f, 0.50f),
			14,
			1));
		button.AddThemeStyleboxOverride("pressed", CreatePanelStyle(
			new Color(0.12f, 0.15f, 0.19f, 1f),
			new Color(0.34f, 0.42f, 0.50f, 0.35f),
			14,
			1));
	}

	private static void StyleDangerButton(Button button)
	{
		button.AddThemeStyleboxOverride("normal", CreatePanelStyle(
			new Color(0.28f, 0.14f, 0.15f, 0.94f),
			new Color(0.66f, 0.30f, 0.32f, 0.40f),
			14,
			1));
		button.AddThemeStyleboxOverride("hover", CreatePanelStyle(
			new Color(0.34f, 0.17f, 0.18f, 0.98f),
			new Color(0.80f, 0.36f, 0.39f, 0.52f),
			14,
			1));
		button.AddThemeStyleboxOverride("pressed", CreatePanelStyle(
			new Color(0.22f, 0.10f, 0.11f, 1f),
			new Color(0.70f, 0.32f, 0.34f, 0.35f),
			14,
			1));
	}
}
