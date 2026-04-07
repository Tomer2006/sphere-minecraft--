using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace SphereMinecraft;

public static class SaveGameManager
{
	public const string SaveDirectory = "user://saves";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true
	};

	public static string? PendingLoadSlotId { get; set; }
	public static string? CurrentSlotId { get; set; }
	public static string? CurrentSaveName { get; set; }
	public static NewGameOptions? PendingNewGameOptions { get; private set; }

	public static bool HasAnySave()
	{
		return ListSaveSlots().Count > 0;
	}

	public static List<SaveSlotInfo> ListSaveSlots()
	{
		EnsureSaveDirectory();

		List<SaveSlotInfo> slots = [];
		string absoluteDirectory = ProjectSettings.GlobalizePath(SaveDirectory);
		using DirAccess? directory = DirAccess.Open(absoluteDirectory);
		if (directory is null)
		{
			return slots;
		}

		directory.ListDirBegin();
		while (true)
		{
			string fileName = directory.GetNext();
			if (string.IsNullOrEmpty(fileName))
			{
				break;
			}

			if (directory.CurrentIsDir() || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			string slotId = fileName[..^5];
			if (!TryLoadGame(slotId, out SaveGameData? saveData) || saveData is null)
			{
				continue;
			}

			slots.Add(new SaveSlotInfo
			{
				SlotId = slotId,
				SaveName = string.IsNullOrWhiteSpace(saveData.SaveName) ? slotId : saveData.SaveName,
				SavedAtUtc = saveData.SavedAtUtc
			});
		}

		directory.ListDirEnd();

		return slots
			.OrderByDescending(slot => slot.SavedAtUtc)
			.ThenByDescending(slot => slot.SlotId)
			.ToList();
	}

	public static bool TryLoadGame(string slotId, out SaveGameData? saveData)
	{
		saveData = null;
		string slotPath = GetSlotPath(slotId);
		RuntimeLog.Info(RuntimeLogChannel.Save, $"Attempting to load save slot {slotId} from {ProjectSettings.GlobalizePath(slotPath)}");

		if (!FileAccess.FileExists(slotPath))
		{
			RuntimeLog.Warning(RuntimeLogChannel.Save, $"Load skipped because save slot {slotId} does not exist.");
			return false;
		}

		using FileAccess? file = FileAccess.Open(slotPath, FileAccess.ModeFlags.Read);
		if (file is null)
		{
			RuntimeLog.Error(RuntimeLogChannel.Save, $"Failed to open save slot {slotId} for reading.");
			return false;
		}

		try
		{
			saveData = JsonSerializer.Deserialize<SaveGameData>(file.GetAsText(), JsonOptions);
			if (saveData is null)
			{
				RuntimeLog.Warning(RuntimeLogChannel.Save, $"Save slot {slotId} deserialized to null.");
				return false;
			}

			saveData.SlotId = slotId;
			RuntimeLog.Info(RuntimeLogChannel.Save,
				$"Loaded save slot {slotId}. Name={saveData.SaveName}, Version={saveData.Version}, SavedAtUtc={saveData.SavedAtUtc}");
			return true;
		}
		catch (Exception exception)
		{
			RuntimeLog.Error(RuntimeLogChannel.Save, "Failed to load save file: " + exception.Message);
			return false;
		}
	}

	public static bool TrySaveGame(SaveGameData saveData, out string slotId, bool forceNewSlot = false)
	{
		EnsureSaveDirectory();

		slotId = forceNewSlot || string.IsNullOrWhiteSpace(CurrentSlotId)
			? CreateSlotId()
			: CurrentSlotId!;

		saveData.Version = 1;
		saveData.SlotId = slotId;
		saveData.SavedAtUtc = DateTime.UtcNow.ToString("O");
		if (string.IsNullOrWhiteSpace(saveData.SaveName))
		{
			saveData.SaveName = string.IsNullOrWhiteSpace(CurrentSaveName)
				? "Save " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
				: CurrentSaveName!;
		}

		using FileAccess? file = FileAccess.Open(GetSlotPath(slotId), FileAccess.ModeFlags.Write);
		if (file is null)
		{
			RuntimeLog.Error(RuntimeLogChannel.Save, $"Failed to open save slot {slotId} for writing.");
			return false;
		}

		try
		{
			file.StoreString(JsonSerializer.Serialize(saveData, JsonOptions));
			CurrentSlotId = slotId;
			CurrentSaveName = saveData.SaveName;
			RuntimeLog.Info(RuntimeLogChannel.Save,
				$"Saved slot {slotId}. Name={saveData.SaveName}, WorldBlocks={saveData.World.Blocks.Count}, RemovedCells={saveData.World.RemovedCells.Count}");
			return true;
		}
		catch (Exception exception)
		{
			RuntimeLog.Error(RuntimeLogChannel.Save, "Failed to save game: " + exception.Message);
			return false;
		}
	}

	public static void DeleteSave(string slotId)
	{
		string slotPath = GetSlotPath(slotId);
		if (!FileAccess.FileExists(slotPath))
		{
			RuntimeLog.Warning(RuntimeLogChannel.Save, $"Delete skipped because slot {slotId} was not found.");
			return;
		}

		RuntimeLog.Info(RuntimeLogChannel.Save, $"Deleting save slot {slotId}.");
		DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(slotPath));

		if (CurrentSlotId == slotId)
		{
			CurrentSlotId = null;
			CurrentSaveName = null;
		}

		if (PendingLoadSlotId == slotId)
		{
			PendingLoadSlotId = null;
		}
	}

	public static void BeginNewGame(NewGameOptions? options = null)
	{
		PendingLoadSlotId = null;
		CurrentSlotId = null;
		CurrentSaveName = options?.SaveName;
		PendingNewGameOptions = options ?? NewGameOptions.CreateDefault();
		RuntimeLog.Info(RuntimeLogChannel.Save,
			$"BeginNewGame called. SaveName={CurrentSaveName ?? "<unnamed>"}, Seed={PendingNewGameOptions.WorldSeed}, BaseRadius={PendingNewGameOptions.BaseRadiusInBlocks}");
	}

	public static void BeginLoadSlot(string slotId)
	{
		PendingLoadSlotId = slotId;
		PendingNewGameOptions = null;
		CurrentSaveName = null;
		RuntimeLog.Info(RuntimeLogChannel.Save, $"BeginLoadSlot called for slot {slotId}.");
	}

	public static NewGameOptions? ConsumePendingNewGame()
	{
		NewGameOptions? options = PendingNewGameOptions;
		PendingNewGameOptions = null;
		RuntimeLog.Info(RuntimeLogChannel.Save,
			options == null
				? "ConsumePendingNewGame returned null."
				: $"ConsumePendingNewGame returned SaveName={options.SaveName}, Seed={options.WorldSeed}.");
		return options;
	}

	private static void EnsureSaveDirectory()
	{
		DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(SaveDirectory));
	}

	private static string GetSlotPath(string slotId)
	{
		return $"{SaveDirectory}/{slotId}.json";
	}

	private static string CreateSlotId()
	{
		return "save_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fffffff");
	}
}

public sealed class SaveSlotInfo
{
	public string SlotId { get; set; } = "";
	public string SaveName { get; set; } = "";
	public string SavedAtUtc { get; set; } = "";

	public string GetDisplayText()
	{
		string fallback = string.IsNullOrWhiteSpace(SaveName) ? SlotId : SaveName;
		if (!DateTime.TryParse(SavedAtUtc, out DateTime parsed))
		{
			return fallback;
		}

		return $"{fallback}  |  {parsed.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
	}
}

public sealed class SaveGameData
{
	public int Version { get; set; } = 1;
	public string SlotId { get; set; } = "";
	public string SaveName { get; set; } = "";
	public string SavedAtUtc { get; set; } = "";
	public WorldSaveData World { get; set; } = new();
	public PlayerSaveData Player { get; set; } = new();
}

public sealed class NewGameOptions
{
	public string SaveName { get; set; } = "New World";
	public int BaseRadiusInBlocks { get; set; } = 50;
	public float HeightVariationInBlocks { get; set; } = 2.5f;
	public int WorldSeed { get; set; } = 1337;
	public Vector3Save DistortionOptimizedRotationEuler { get; set; } = new();

	public static NewGameOptions CreateDefault()
	{
		return new NewGameOptions();
	}
}

public sealed class WorldSaveData
{
	public int BaseRadiusInBlocks { get; set; }
	public float HeightVariationInBlocks { get; set; }
	public float NoiseScale { get; set; }
	public int WorldSeed { get; set; }
	public Vector3Save DistortionOptimizedRotationEuler { get; set; } = new();
	public float BlockSize { get; set; }
	public bool UseDebugColors { get; set; }
	public bool CullFacesAgainstNeighborBlocks { get; set; }
	public int ChunkSizeInCells { get; set; }
	public List<BlockEntrySave> Blocks { get; set; } = [];
	public List<CellSave> RemovedCells { get; set; } = [];
}

public sealed class PlayerSaveData
{
	public Vector3Save Position { get; set; } = new();
	public Vector3Save Velocity { get; set; } = new();
	public Vector3Save DesiredForward { get; set; } = new();
	public float Pitch { get; set; }
	public int SelectedBlockType { get; set; } = (int)VoxelBlockType.Grass;
	public int SelectedHotbarSlot { get; set; }
	public List<PlayerInventorySlotSave> InventorySlots { get; set; } = [];
}

public sealed class PlayerInventorySlotSave
{
	public int BlockType { get; set; }
	public int Count { get; set; }
}

public sealed class BlockEntrySave
{
	public CellSave Cell { get; set; } = new();
	public int BlockType { get; set; }
}

public sealed class CellSave
{
	public int Face { get; set; }
	public int Radius { get; set; }
	public int U { get; set; }
	public int V { get; set; }

	public static CellSave FromCell(PlanetCellId cell)
	{
		return new CellSave
		{
			Face = cell.Face,
			Radius = cell.Radius,
			U = cell.U,
			V = cell.V
		};
	}

	public PlanetCellId ToCell()
	{
		return new PlanetCellId(Face, Radius, U, V);
	}
}

public sealed class Vector3Save
{
	public float X { get; set; }
	public float Y { get; set; }
	public float Z { get; set; }

	public static Vector3Save FromVector3(Vector3 value)
	{
		return new Vector3Save
		{
			X = value.X,
			Y = value.Y,
			Z = value.Z
		};
	}

	public Vector3 ToVector3()
	{
		return new Vector3(X, Y, Z);
	}
}
