using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Godot;

namespace SphereMinecraft;

public partial class PlanetVoxelWorld : Node3D
{
	private readonly Dictionary<PlanetCellId, VoxelBlockType> placedBlocks = [];
	private readonly HashSet<PlanetCellId> removedCells = [];
	private readonly Dictionary<long, int> defaultColumnHeights = [];
	private readonly Dictionary<ChunkKey, PlanetChunk> chunks = [];
	private readonly Dictionary<ulong, PlanetChunk> chunkBodiesById = [];
	private readonly Dictionary<ChunkKey, int> latestRequestedRevisions = [];
	private readonly Dictionary<VoxelBlockType, Rect2> atlasUvRects = [];
	private readonly ConcurrentQueue<ChunkBuildResult> completedBuilds = new();
	private readonly HashSet<ChunkKey> activeRenderChunks = [];
	private readonly Queue<ChunkBuildRequest> queuedChunkBuilds = new();
	private readonly HashSet<ChunkKey> pendingInitialLoadChunks = [];
	private readonly object worldDataLock = new();

	private Node3D? chunkRoot;
	private PlanetPlayer? trackedPlayer;
	private FastNoiseLite? noise;
	private StandardMaterial3D? planetMaterial;
	private Texture2D? atlasTexture;
	private ChunkAnchor? currentAnchor;
	private int faceResolution = 100;
	private int buildRevisionCounter;
	private int activeQueuedChunkBuilds;
	private int initialLoadTargetChunkCount;
	private int lastStreamingPlayerRadiusChunk = int.MinValue;
	private bool isInitialChunkLoadActive;

	[ExportGroup("References")]
	[Export] public NodePath PlayerPath { get; set; } = new("../Player");

	[ExportGroup("Planet Shape")]
	[Export] public int BaseRadiusInBlocks { get; set; } = 50;
	[Export] public float HeightVariationInBlocks { get; set; } = 2.5f;
	[Export] public float NoiseScale { get; set; } = 1.8f;
	[Export] public int WorldSeed { get; set; } = 1337;
	[Export] public Vector3 DistortionOptimizedRotationEuler { get; set; } = Vector3.Zero;
	[Export] public float BlockSize { get; set; } = 1f;
	[Export] public bool GenerateOnReady { get; set; }

	[ExportGroup("Chunk Streaming")]
	[Export(PropertyHint.Range, "8,64,1")] public int ChunkSizeInCells { get; set; } = 24;
	/// <summary>
	/// Surface (face U/V) streaming: BFS steps across adjacent chunk footprints on the cube, including
	/// face edges. Paired with <see cref="ActiveRenderRadiusChunkLoadRadius"/> to define the active
	/// <see cref="ChunkKey"/> set.
	/// </summary>
	[Export(PropertyHint.Range, "1,32,1")]
	public int ActiveRenderChunkLoadRadius { get; set; } = 5;

	/// <summary>
	/// Radial streaming: chunk layers above and below the player's radius chunk (along
	/// <see cref="PlanetCellId.Radius"/>), combined with the surface radius to form cubic chunks only.
	/// </summary>
	[Export(PropertyHint.Range, "1,64,1")]
	public int ActiveRenderRadiusChunkLoadRadius { get; set; } = 4;

	[Export(PropertyHint.Range, "2,64,1")] public int SurfaceShellDepthInBlocks { get; set; } = 12;
	[Export(PropertyHint.Range, "1,12,1")] public int ExtraOutwardBlocks { get; set; } = 2;
	[ExportGroup("Rendering")]
	[Export] public bool UseDebugColors { get; set; }
	[Export] public bool CullFacesAgainstNeighborBlocks { get; set; } = true;

	public Vector3 PlanetCenter => GlobalPosition;

	public float ApproximateSurfaceRadius => (BaseRadiusInBlocks + HeightVariationInBlocks + 1f) * BlockSize;

	public bool IsInitialChunkLoadInProgress => isInitialChunkLoadActive && pendingInitialLoadChunks.Count > 0;

	public float InitialChunkLoadProgress =>
		initialLoadTargetChunkCount <= 0
			? 1f
			: Mathf.Clamp(1f - pendingInitialLoadChunks.Count / (float)initialLoadTargetChunkCount, 0f, 1f);

	public int InitialChunkLoadCompletedCount => Mathf.Max(0, initialLoadTargetChunkCount - pendingInitialLoadChunks.Count);

	public int InitialChunkLoadTotalCount => initialLoadTargetChunkCount;

	/// <summary>Diagnostics for debug HUD (F3).</summary>
	public int DebugLoadedChunkCount => chunks.Count;

	public int DebugActiveRenderChunkCount => activeRenderChunks.Count;
	public int DebugQueuedChunkBuildCount => queuedChunkBuilds.Count;
	public int DebugFaceResolutionCells => faceResolution;
	public int DebugStreamingRadiusChunk => lastStreamingPlayerRadiusChunk;

	public override void _Ready()
	{
		EnsureRuntimeNodes();
		RuntimeLog.Info(RuntimeLogChannel.World,
			$"PlanetVoxelWorld ready. GenerateOnReady={GenerateOnReady}, BaseRadius={BaseRadiusInBlocks}, HeightVariation={HeightVariationInBlocks:0.00}, Seed={WorldSeed}");

		if (GenerateOnReady)
		{
			CallDeferred(nameof(GeneratePlanet));
		}
	}

	public override void _Process(double _)
	{
		ApplyCompletedBuilds();
		DispatchQueuedChunkBuilds();
		UpdateStreaming();
	}

	public void RefreshStreamingAroundPlayer(bool buildImmediately = false)
	{
		trackedPlayer = ResolvePlayer();
		UpdateStreaming(force: true, buildImmediately: buildImmediately);
	}

	public void GeneratePlanet()
	{
		RuntimeLog.Info(RuntimeLogChannel.World,
			$"GeneratePlanet called. BaseRadius={BaseRadiusInBlocks}, HeightVariation={HeightVariationInBlocks:0.00}, NoiseScale={NoiseScale:0.00}, Seed={WorldSeed}");
		lock (worldDataLock)
		{
			placedBlocks.Clear();
			removedCells.Clear();
		}

		RebuildPlanet();
	}

	public void LoadFromSave(WorldSaveData data)
	{
		RuntimeLog.Info(RuntimeLogChannel.World,
			$"Loading world from save. BaseRadius={data.BaseRadiusInBlocks}, HeightVariation={data.HeightVariationInBlocks:0.00}, NoiseScale={data.NoiseScale:0.00}, Seed={data.WorldSeed}, Blocks={data.Blocks.Count}, RemovedCells={data.RemovedCells.Count}");
		BaseRadiusInBlocks = Mathf.Max(2, data.BaseRadiusInBlocks);
		HeightVariationInBlocks = Mathf.Max(0f, data.HeightVariationInBlocks);
		NoiseScale = Mathf.Max(0.01f, data.NoiseScale);
		WorldSeed = data.WorldSeed;
		DistortionOptimizedRotationEuler = data.DistortionOptimizedRotationEuler.ToVector3();
		BlockSize = Mathf.Max(0.1f, data.BlockSize);
		UseDebugColors = data.UseDebugColors;
		CullFacesAgainstNeighborBlocks = data.CullFacesAgainstNeighborBlocks;
		ChunkSizeInCells = Mathf.Max(8, data.ChunkSizeInCells);

		lock (worldDataLock)
		{
			placedBlocks.Clear();
			removedCells.Clear();

			foreach (BlockEntrySave entry in data.Blocks)
			{
				placedBlocks[entry.Cell.ToCell()] = (VoxelBlockType)entry.BlockType;
			}

			foreach (CellSave entry in data.RemovedCells)
			{
				removedCells.Add(entry.ToCell());
			}
		}

		RebuildPlanet();
	}

	public WorldSaveData CreateSaveData()
	{
		List<BlockEntrySave> blockSaves = [];
		List<CellSave> removedCellSaves = [];

		lock (worldDataLock)
		{
			foreach ((PlanetCellId cell, VoxelBlockType blockType) in placedBlocks)
			{
				if (blockType == VoxelBlockType.Air)
				{
					continue;
				}

				blockSaves.Add(new BlockEntrySave
				{
					Cell = CellSave.FromCell(cell),
					BlockType = (int)blockType
				});
			}

			foreach (PlanetCellId cell in removedCells)
			{
				removedCellSaves.Add(CellSave.FromCell(cell));
			}
		}

		return new WorldSaveData
		{
			BaseRadiusInBlocks = BaseRadiusInBlocks,
			HeightVariationInBlocks = HeightVariationInBlocks,
			NoiseScale = NoiseScale,
			WorldSeed = WorldSeed,
			DistortionOptimizedRotationEuler = Vector3Save.FromVector3(DistortionOptimizedRotationEuler),
			BlockSize = BlockSize,
			UseDebugColors = UseDebugColors,
			CullFacesAgainstNeighborBlocks = CullFacesAgainstNeighborBlocks,
			ChunkSizeInCells = ChunkSizeInCells,
			Blocks = blockSaves,
			RemovedCells = removedCellSaves
		};
	}

	public bool HasBlock(PlanetCellId cell)
	{
		if (!IsValidCell(cell))
		{
			return false;
		}

		lock (worldDataLock)
		{
			return HasBlockUnlocked(cell);
		}
	}

	public bool TryGetBlockType(PlanetCellId cell, out VoxelBlockType blockType)
	{
		blockType = VoxelBlockType.Air;
		if (!IsValidCell(cell))
		{
			return false;
		}

		lock (worldDataLock)
		{
			if (!HasBlockUnlocked(cell))
			{
				return false;
			}

			if (placedBlocks.TryGetValue(cell, out VoxelBlockType placed))
			{
				blockType = placed;
				return true;
			}

			blockType = GetDefaultBlockTypeUnlocked(cell);
			return true;
		}
	}

	public void PlaceBlock(PlanetCellId cell, VoxelBlockType blockType)
	{
		if (!IsValidCell(cell))
		{
			RuntimeLog.Warning(RuntimeLogChannel.World, $"PlaceBlock ignored for invalid cell {cell}.");
			return;
		}

		lock (worldDataLock)
		{
			if (blockType == VoxelBlockType.Air)
			{
				RemoveBlockInternal(cell);
			}
			else
			{
				removedCells.Remove(cell);

				if (IsDefaultSolidUnlocked(cell) && GetDefaultBlockTypeUnlocked(cell) == blockType)
				{
					placedBlocks.Remove(cell);
				}
				else
				{
					placedBlocks[cell] = blockType;
				}
			}
		}

		RuntimeLog.Info(RuntimeLogChannel.World, $"Placed block {blockType} at {cell}.");
		InvalidateChunksAround(cell);
	}

	public void RemoveBlock(PlanetCellId cell)
	{
		if (!IsValidCell(cell))
		{
			RuntimeLog.Warning(RuntimeLogChannel.World, $"RemoveBlock ignored for invalid cell {cell}.");
			return;
		}

		lock (worldDataLock)
		{
			RemoveBlockInternal(cell);
		}

		RuntimeLog.Info(RuntimeLogChannel.World, $"Removed block at {cell}.");
		InvalidateChunksAround(cell);
	}

	public Aabb GetCellAabb(PlanetCellId cell)
	{
		Vector3[] corners = GetCellCorners(cell);
		Vector3 min = corners[0];
		Vector3 max = corners[0];

		for (int index = 1; index < corners.Length; index++)
		{
			min = min.Min(corners[index]);
			max = max.Max(corners[index]);
		}

		return new Aabb(min, max - min);
	}

	public bool TryGetBreakCell(CollisionObject3D? collider, int faceIndex, Vector3 position, Vector3 normal, out PlanetCellId targetCell)
	{
		targetCell = default;

		if (TryGetRaycastHitInfo(collider, faceIndex, out RaycastTriangleInfo hitInfo))
		{
			targetCell = hitInfo.Cell;
			if (HasBlock(targetCell))
			{
				return true;
			}
		}

		return TryResolveCellFromWorldPoint(position - normal * GetHitResolveEpsilon(), expectSolid: true, out targetCell);
	}

	public bool TryGetPlaceCell(CollisionObject3D? collider, int faceIndex, Vector3 position, Vector3 normal, out PlanetCellId targetCell)
	{
		targetCell = default;

		if (TryGetRaycastHitInfo(collider, faceIndex, out RaycastTriangleInfo hitInfo) &&
			TryGetAdjacentCellStatic(hitInfo.Cell, hitInfo.Face, faceResolution, out targetCell) &&
			!HasBlock(targetCell))
		{
			return true;
		}

		return TryResolveCellFromWorldPoint(position + normal * GetHitResolveEpsilon(), expectSolid: false, out targetCell);
	}

	private void RebuildPlanet()
	{
		EnsureRuntimeNodes();
		ConfigureNoise();
		ConfigureMaterial();

		lock (worldDataLock)
		{
			defaultColumnHeights.Clear();
		}

		faceResolution = Mathf.Max(2, BaseRadiusInBlocks * 2);
		currentAnchor = null;
		RuntimeLog.Info(RuntimeLogChannel.World,
			$"Rebuilding planet. FaceResolution={faceResolution}, ChunkSize={ChunkSizeInCells}, SurfaceShellDepth={SurfaceShellDepthInBlocks}, ExtraOutwardBlocks={ExtraOutwardBlocks}");
		ResetInitialChunkLoadState();
		ClearAllChunks();
		// Always queue chunk builds after a rebuild. Using buildImmediately here forced every active
		// chunk through BuildChunkImmediate in one frame, which caused severe hitches on large worlds.
		UpdateStreaming(force: true, buildImmediately: false);
	}

	private void EnsureRuntimeNodes()
	{
		chunkRoot ??= GetNodeOrNull<Node3D>("PlanetChunks");
		if (chunkRoot == null)
		{
			chunkRoot = new Node3D { Name = "PlanetChunks" };
			AddChild(chunkRoot);
		}

		Node? farShellNode = GetNodeOrNull<Node>("FarShell");
		if (farShellNode != null)
		{
			farShellNode.QueueFree();
		}
	}

	private void ConfigureNoise()
	{
		noise ??= new FastNoiseLite();
		noise.Seed = WorldSeed;
		noise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
		noise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
		noise.FractalOctaves = 4;
		noise.FractalLacunarity = 2f;
		noise.FractalGain = 0.5f;
		noise.Frequency = Mathf.Max(0.01f, NoiseScale / Mathf.Max(1f, BaseRadiusInBlocks));
		RuntimeLog.Info(RuntimeLogChannel.World,
			$"Configured noise. Seed={noise.Seed}, Frequency={noise.Frequency:0.0000}, NoiseScale={NoiseScale:0.00}");
	}

	private void ConfigureMaterial()
	{
		atlasTexture ??= BuildTextureAtlas();

		planetMaterial ??= new StandardMaterial3D
		{
			AlbedoTexture = atlasTexture,
			VertexColorUseAsAlbedo = true,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled,
			Roughness = 1f,
			Metallic = 0f,
			TextureFilter = GameUserSettings.GetPlanetBlockTextureFilter()
		};
	}
}
