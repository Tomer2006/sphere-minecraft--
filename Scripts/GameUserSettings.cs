using Godot;

namespace SphereMinecraft;

/// <summary>
/// Persistent options stored under <c>user://user_settings.cfg</c> and applied at menu and gameplay startup.
/// </summary>
public static class GameUserSettings
{
	private const string FilePath = "user://user_settings.cfg";
	private const string Section = "game";

	private const float DefaultMouseSensitivity = 0.14f;
	private const float MinMouseSensitivity = 0.02f;
	private const float MaxMouseSensitivity = 0.45f;

	/// <summary>0 = off, 1 = 2×, 2 = 4×, 3 = 8×.</summary>
	public const int MinMsaa3D = 0;
	public const int MaxMsaa3D = 3;

	/// <summary>0 = none, 1 = FXAA, 2 = SMAA 1x.</summary>
	public const int MinScreenSpaceAa = 0;
	public const int MaxScreenSpaceAa = 2;

	public static float MouseSensitivity { get; set; } = DefaultMouseSensitivity;
	public static float MasterVolumeLinear { get; set; } = 1f;
	public static bool Fullscreen { get; set; }

	/// <summary>MSAA level index; see <see cref="MinMsaa3D"/> / <see cref="MaxMsaa3D"/>.</summary>
	public static int GraphicsMsaa3D { get; set; } = 2;

	/// <summary>Screen-space AA mode index; see <see cref="MinScreenSpaceAa"/> / <see cref="MaxScreenSpaceAa"/>.</summary>
	public static int GraphicsScreenSpaceAa { get; set; } = 1;

	public static bool GraphicsUseTaa { get; set; }

	/// <summary>When true, voxel atlas uses linear + anisotropic filtering; when false, nearest (pixel-crisp).</summary>
	public static bool GraphicsVoxelLinearTextures { get; set; } = true;

	/// <summary>When true, turns off the sun and uses flat ambient so the world looks evenly lit (no directional shading).</summary>
	public static bool GraphicsDisableLighting { get; set; }

	public static void Load()
	{
		ConfigFile cfg = new();
		if (cfg.Load(FilePath) != Error.Ok)
		{
			return;
		}

		MouseSensitivity = ReadFloat(cfg, "mouse_sensitivity", MouseSensitivity);
		MasterVolumeLinear = ReadFloat(cfg, "master_volume_linear", MasterVolumeLinear);
		Fullscreen = (bool)cfg.GetValue(Section, "fullscreen", Fullscreen).AsBool();
		GraphicsMsaa3D = ReadInt(cfg, "graphics_msaa_3d", GraphicsMsaa3D);
		GraphicsScreenSpaceAa = ReadInt(cfg, "graphics_screen_space_aa", GraphicsScreenSpaceAa);
		GraphicsUseTaa = (bool)cfg.GetValue(Section, "graphics_use_taa", GraphicsUseTaa).AsBool();
		GraphicsVoxelLinearTextures = (bool)cfg.GetValue(Section, "graphics_voxel_linear_textures", GraphicsVoxelLinearTextures).AsBool();
		GraphicsDisableLighting = (bool)cfg.GetValue(Section, "graphics_disable_lighting", GraphicsDisableLighting).AsBool();

		MouseSensitivity = Mathf.Clamp(MouseSensitivity, MinMouseSensitivity, MaxMouseSensitivity);
		MasterVolumeLinear = Mathf.Clamp(MasterVolumeLinear, 0f, 1f);
		GraphicsMsaa3D = Mathf.Clamp(GraphicsMsaa3D, MinMsaa3D, MaxMsaa3D);
		GraphicsScreenSpaceAa = Mathf.Clamp(GraphicsScreenSpaceAa, MinScreenSpaceAa, MaxScreenSpaceAa);
	}

	public static void Save()
	{
		ConfigFile cfg = new();
		cfg.SetValue(Section, "mouse_sensitivity", MouseSensitivity);
		cfg.SetValue(Section, "master_volume_linear", MasterVolumeLinear);
		cfg.SetValue(Section, "fullscreen", Fullscreen);
		cfg.SetValue(Section, "graphics_msaa_3d", GraphicsMsaa3D);
		cfg.SetValue(Section, "graphics_screen_space_aa", GraphicsScreenSpaceAa);
		cfg.SetValue(Section, "graphics_use_taa", GraphicsUseTaa);
		cfg.SetValue(Section, "graphics_voxel_linear_textures", GraphicsVoxelLinearTextures);
		cfg.SetValue(Section, "graphics_disable_lighting", GraphicsDisableLighting);
		cfg.Save(FilePath);
	}

	public static void ApplyAudio()
	{
		int masterBus = AudioServer.GetBusIndex("Master");
		if (masterBus < 0)
		{
			return;
		}

		float linear = Mathf.Clamp(MasterVolumeLinear, 0f, 1f);
		AudioServer.SetBusVolumeDb(masterBus, linear <= 0.0001f ? -80f : Mathf.LinearToDb(linear));
	}

	public static void ApplyWindowMode()
	{
		DisplayServer.WindowSetMode(
			Fullscreen ? DisplayServer.WindowMode.ExclusiveFullscreen : DisplayServer.WindowMode.Windowed);
	}

	/// <summary>
	/// Applies MSAA, screen-space AA, and TAA on the root window viewport (Forward+).
	/// </summary>
	public static void ApplyGraphics()
	{
		if (Engine.GetMainLoop() is not SceneTree tree)
		{
			return;
		}

		Window window = tree.Root;
		window.Msaa3D = GraphicsMsaa3D switch
		{
			1 => Viewport.Msaa.Msaa2X,
			2 => Viewport.Msaa.Msaa4X,
			3 => Viewport.Msaa.Msaa8X,
			_ => Viewport.Msaa.Disabled
		};

		window.ScreenSpaceAA = GraphicsScreenSpaceAa switch
		{
			1 => Viewport.ScreenSpaceAAEnum.Fxaa,
			2 => Viewport.ScreenSpaceAAEnum.Smaa,
			_ => Viewport.ScreenSpaceAAEnum.Disabled
		};

		window.UseTaa = GraphicsUseTaa;
		window.UseOcclusionCulling = true;
	}

	public static BaseMaterial3D.TextureFilterEnum GetPlanetBlockTextureFilter() =>
		GraphicsVoxelLinearTextures
			? BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic
			: BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;

	private static bool lightingDefaultsCaptured;
	private static bool defaultSunVisible = true;
	private static float defaultSunLightEnergy = 1f;
	private static Environment.AmbientSource defaultAmbientSource = Environment.AmbientSource.Sky;
	private static Color defaultAmbientColor = new(0.08f, 0.09f, 0.12f, 1f);
	private static float defaultAmbientEnergy = 0.5f;
	private static float defaultAmbientSkyContribution = 0.08f;

	/// <summary>
	/// Applies flat ambient / sun off when <see cref="GraphicsDisableLighting"/> is set. Looks for
	/// <c>WorldEnvironment</c> and <c>Sun</c> under the gameplay root (e.g. main scene <c>Main</c>).
	/// </summary>
	public static void ApplySceneLighting()
	{
		if (Engine.GetMainLoop() is not SceneTree tree)
		{
			return;
		}

		ApplySceneLightingToRoot(tree.CurrentScene);
	}

	private static void ApplySceneLightingToRoot(Node? root)
	{
		if (root == null)
		{
			return;
		}

		WorldEnvironment? worldEnv = root.GetNodeOrNull<WorldEnvironment>("WorldEnvironment");
		DirectionalLight3D? sun = root.GetNodeOrNull<DirectionalLight3D>("Sun");
		Environment? env = worldEnv?.Environment;
		if (env == null)
		{
			return;
		}

		if (!lightingDefaultsCaptured)
		{
			defaultAmbientSource = env.AmbientLightSource;
			defaultAmbientColor = env.AmbientLightColor;
			defaultAmbientEnergy = env.AmbientLightEnergy;
			defaultAmbientSkyContribution = env.AmbientLightSkyContribution;
			if (sun != null)
			{
				defaultSunVisible = sun.Visible;
				defaultSunLightEnergy = sun.LightEnergy;
			}

			lightingDefaultsCaptured = true;
		}

		if (GraphicsDisableLighting)
		{
			if (sun != null)
			{
				sun.Visible = false;
			}

			env.AmbientLightSource = Environment.AmbientSource.Color;
			env.AmbientLightColor = Colors.White;
			env.AmbientLightEnergy = 1f;
			env.AmbientLightSkyContribution = 0f;
		}
		else
		{
			if (sun != null)
			{
				sun.Visible = defaultSunVisible;
				sun.LightEnergy = defaultSunLightEnergy;
			}

			env.AmbientLightSource = defaultAmbientSource;
			env.AmbientLightColor = defaultAmbientColor;
			env.AmbientLightEnergy = defaultAmbientEnergy;
			env.AmbientLightSkyContribution = defaultAmbientSkyContribution;
		}
	}

	public static void ApplyToPlayer(PlanetPlayer player)
	{
		player.MouseSensitivity = Mathf.Clamp(MouseSensitivity, MinMouseSensitivity, MaxMouseSensitivity);
	}

	private static float ReadFloat(ConfigFile cfg, string key, float fallback)
	{
		Variant v = cfg.GetValue(Section, key, fallback);
		return v.VariantType switch
		{
			Variant.Type.Float => (float)v.AsDouble(),
			Variant.Type.Int => (float)v.AsInt32(),
			_ => fallback
		};
	}

	private static int ReadInt(ConfigFile cfg, string key, int fallback)
	{
		Variant v = cfg.GetValue(Section, key, fallback);
		return v.VariantType switch
		{
			Variant.Type.Int => v.AsInt32(),
			Variant.Type.Float => Mathf.RoundToInt((float)v.AsDouble()),
			_ => fallback
		};
	}
}
