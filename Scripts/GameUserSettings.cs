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

	public static float MouseSensitivity { get; set; } = DefaultMouseSensitivity;
	public static float MasterVolumeLinear { get; set; } = 1f;
	public static bool Fullscreen { get; set; }

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

		MouseSensitivity = Mathf.Clamp(MouseSensitivity, MinMouseSensitivity, MaxMouseSensitivity);
		MasterVolumeLinear = Mathf.Clamp(MasterVolumeLinear, 0f, 1f);
	}

	public static void Save()
	{
		ConfigFile cfg = new();
		cfg.SetValue(Section, "mouse_sensitivity", MouseSensitivity);
		cfg.SetValue(Section, "master_volume_linear", MasterVolumeLinear);
		cfg.SetValue(Section, "fullscreen", Fullscreen);
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
}
