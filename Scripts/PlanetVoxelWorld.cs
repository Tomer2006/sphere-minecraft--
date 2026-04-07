using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace SphereMinecraft;

public partial class PlanetVoxelWorld : Node3D
{
	private const int AtlasTileSize = 48;
	private const int AtlasGridSize = 2;
	private const int AtlasPaddingPixels = 2;
	private const string BlockTexturesPath = "res://Assets/Textures/Blocks/";
	private const int MaxCompletedBuildsPerFrame = 4;
	private const int MaxQueuedChunkDispatchesPerFrame = 50;
	private const int MaxConcurrentChunkBuilds = 50;

	private static readonly Vector3[] FaceNormals =
	[
		Vector3.Right,
		Vector3.Left,
		Vector3.Up,
		Vector3.Down,
		Vector3.Back,
		Vector3.Forward
	];

	private static readonly Vector3[] FaceAxisU =
	[
		new Vector3(0f, 0f, -1f),
		new Vector3(0f, 0f, 1f),
		Vector3.Right,
		Vector3.Right,
		Vector3.Right,
		Vector3.Left
	];

	private static readonly Vector3[] FaceAxisV =
	[
		Vector3.Up,
		Vector3.Up,
		new Vector3(0f, 0f, -1f),
		Vector3.Forward,
		Vector3.Up,
		Vector3.Up
	];

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
	[Export] public bool AlwaysLoadWholePlanet { get; set; } = true;
	[Export(PropertyHint.Range, "1,16,1")] public int ActiveRenderChunkRadius { get; set; } = 6;
	[Export(PropertyHint.Range, "2,64,1")] public int SurfaceShellDepthInBlocks { get; set; } = 12;
	[Export(PropertyHint.Range, "1,12,1")] public int ExtraOutwardBlocks { get; set; } = 2;
	[ExportGroup("Rendering")]
	[Export] public bool UseDebugColors { get; set; }
	[Export] public bool CullFacesAgainstNeighborBlocks { get; set; } = true;

	public Vector3 PlanetCenter => GlobalPosition;

	public float ApproximateSurfaceRadius => (BaseRadiusInBlocks + HeightVariationInBlocks + 1f) * BlockSize;

	public bool IsInitialChunkLoadInProgress => pendingInitialLoadChunks.Count > 0;

	public float InitialChunkLoadProgress =>
		initialLoadTargetChunkCount <= 0
			? 1f
			: Mathf.Clamp(1f - pendingInitialLoadChunks.Count / (float)initialLoadTargetChunkCount, 0f, 1f);

	public int InitialChunkLoadCompletedCount => Mathf.Max(0, initialLoadTargetChunkCount - pendingInitialLoadChunks.Count);

	public int InitialChunkLoadTotalCount => initialLoadTargetChunkCount;

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
		UpdateStreaming(force: true, buildImmediately: !AlwaysLoadWholePlanet);
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
			TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps
		};
	}

	private Texture2D BuildTextureAtlas()
	{
		atlasUvRects.Clear();
		RuntimeLog.Info(RuntimeLogChannel.World, "Building block texture atlas from res://Assets/Textures/Blocks/.");

		int atlasSize = AtlasTileSize * AtlasGridSize;
		Image image = Image.CreateEmpty(atlasSize, atlasSize, false, Image.Format.Rgba8);
		image.Fill(new Color(0f, 0f, 0f, 0f));

		AddBlockTextureToAtlas(image, VoxelBlockType.Grass, 0, "grass.png", new Color(0.30f, 0.58f, 0.27f), new Color(0.43f, 0.76f, 0.31f));
		AddBlockTextureToAtlas(image, VoxelBlockType.Dirt, 1, "dirt.png", new Color(0.42f, 0.27f, 0.18f), new Color(0.54f, 0.35f, 0.22f));
		AddBlockTextureToAtlas(image, VoxelBlockType.Stone, 2, "stone.png", new Color(0.42f, 0.45f, 0.48f), new Color(0.62f, 0.64f, 0.68f));
		atlasUvRects[VoxelBlockType.Air] = atlasUvRects[VoxelBlockType.Stone];

		return ImageTexture.CreateFromImage(image);
	}

	private void AddBlockTextureToAtlas(
		Image image,
		VoxelBlockType blockType,
		int tileIndex,
		string fileName,
		Color fallbackBase,
		Color fallbackAccent)
	{
		if (TryBlitBlockTextureTile(image, tileIndex, fileName))
		{
			RegisterAtlasTileUvRect(blockType, tileIndex);
			return;
		}

		RuntimeLog.Warning(RuntimeLogChannel.World, $"Block texture missing or unreadable: {BlockTexturesPath}{fileName}. Using procedural tile.");
		PaintAtlasTile(image, blockType, tileIndex, fallbackBase, fallbackAccent);
	}

	private static bool TryBlitBlockTextureTile(Image atlas, int tileIndex, string fileName)
	{
		string path = BlockTexturesPath + fileName;
		if (!ResourceLoader.Exists(path))
		{
			return false;
		}

		if (ResourceLoader.Load(path) is not Texture2D texture)
		{
			return false;
		}

		Image? source = texture.GetImage();
		if (source == null)
		{
			return false;
		}

		source.Convert(Image.Format.Rgba8);
		if (source.GetWidth() != AtlasTileSize || source.GetHeight() != AtlasTileSize)
		{
			source.Resize(AtlasTileSize, AtlasTileSize, Image.Interpolation.Nearest);
		}

		int tileX = tileIndex % AtlasGridSize;
		int tileY = tileIndex / AtlasGridSize;
		int pixelStartX = tileX * AtlasTileSize;
		int pixelStartY = tileY * AtlasTileSize;
		atlas.BlitRect(source, new Rect2I(0, 0, AtlasTileSize, AtlasTileSize), new Vector2I(pixelStartX, pixelStartY));
		return true;
	}

	private void RegisterAtlasTileUvRect(VoxelBlockType blockType, int tileIndex)
	{
		int tileX = tileIndex % AtlasGridSize;
		int tileY = tileIndex / AtlasGridSize;
		float atlasSizeF = AtlasTileSize * AtlasGridSize;
		float tileSpan = AtlasTileSize / atlasSizeF;
		float uvPadding = AtlasPaddingPixels / atlasSizeF;
		atlasUvRects[blockType] = new Rect2(
			new Vector2(tileX * tileSpan + uvPadding, tileY * tileSpan + uvPadding),
			new Vector2(tileSpan - uvPadding * 2f, tileSpan - uvPadding * 2f));
	}

	private void PaintAtlasTile(Image image, VoxelBlockType blockType, int tileIndex, Color baseColor, Color accentColor)
	{
		int tileX = tileIndex % AtlasGridSize;
		int tileY = tileIndex / AtlasGridSize;
		int pixelStartX = tileX * AtlasTileSize;
		int pixelStartY = tileY * AtlasTileSize;

		for (int localY = 0; localY < AtlasTileSize; localY++)
		{
			for (int localX = 0; localX < AtlasTileSize; localX++)
			{
				float stripe = Mathf.Abs(Mathf.Sin((localX + 1) * 0.41f + (localY + 1) * 0.23f + tileIndex * 1.71f));
				float grain = Mathf.Abs(Mathf.Sin((localX + 3) * 1.13f + tileIndex * 0.7f) * Mathf.Cos((localY + 5) * 1.37f));
				float mask = Mathf.Clamp(stripe * 0.65f + grain * 0.35f, 0f, 1f);
				Color color = baseColor.Lerp(accentColor, mask);

				int edgeDistance = Mathf.Min(Mathf.Min(localX, localY), Mathf.Min(AtlasTileSize - 1 - localX, AtlasTileSize - 1 - localY));
				if (edgeDistance < AtlasPaddingPixels)
				{
					color = color.Lerp(baseColor, 0.45f);
				}

				image.SetPixel(pixelStartX + localX, pixelStartY + localY, color);
			}
		}

		RegisterAtlasTileUvRect(blockType, tileIndex);
	}

	private void UpdateStreaming(bool force = false, bool buildImmediately = false)
	{
		if (AlwaysLoadWholePlanet)
		{
			bool hasMissingCollision = HasLoadedChunkWithoutCollision();
			if (!force && activeRenderChunks.Count > 0 && !hasMissingCollision)
			{
				return;
			}

			SyncAllChunks(buildImmediately || hasMissingCollision);
			return;
		}

		trackedPlayer ??= ResolvePlayer();
		if (trackedPlayer == null)
		{
			RuntimeLog.Warning(RuntimeLogChannel.World, "UpdateStreaming skipped because no player was resolved.");
			return;
		}

		ChunkAnchor nextAnchor = GetAnchorFromWorldPosition(trackedPlayer.GlobalPosition);
		if (!force && currentAnchor.HasValue && currentAnchor.Value.Equals(nextAnchor))
		{
			return;
		}

		currentAnchor = nextAnchor;
		RuntimeLog.Info(RuntimeLogChannel.World,
			$"Streaming anchor updated to {FormatAnchor(nextAnchor)} from player position {RuntimeLog.FormatVector(trackedPlayer.GlobalPosition)}. Force={force}, BuildImmediately={buildImmediately}");
		SyncActiveChunks(nextAnchor, buildImmediately);
	}

	private void SyncAllChunks(bool buildImmediately = false)
	{
		HashSet<ChunkKey> nextRenderChunks = [];
		int chunkSize = Mathf.Max(8, ChunkSizeInCells);
		int faceChunkCount = Mathf.CeilToInt(faceResolution / (float)chunkSize);
		int maxRadiusChunk = GetMaxPlanetRadiusChunk(chunkSize);

		for (int face = 0; face < FaceNormals.Length; face++)
		{
			for (int uChunk = 0; uChunk < faceChunkCount; uChunk++)
			{
				for (int vChunk = 0; vChunk < faceChunkCount; vChunk++)
				{
					for (int radiusChunk = 0; radiusChunk <= maxRadiusChunk; radiusChunk++)
					{
						nextRenderChunks.Add(new ChunkKey(face, radiusChunk, uChunk, vChunk));
					}
				}
			}
		}

		BeginInitialChunkLoad(nextRenderChunks, buildImmediately);
		ApplyChunkSet(nextRenderChunks, buildImmediately, "all-planet");
	}

	private void InvalidateChunksAround(PlanetCellId editedCell)
	{
		WorldSnapshot snapshot = CreateWorldSnapshot();
		HashSet<ChunkKey> targets = [GetChunkKey(editedCell)];

		foreach (CellFace face in Enum.GetValues<CellFace>())
		{
			if (TryGetAdjacentCellStatic(editedCell, face, faceResolution, out PlanetCellId adjacentCell))
			{
				targets.Add(GetChunkKey(adjacentCell));
			}
		}

		RuntimeLog.Info(RuntimeLogChannel.Chunk, $"Invalidating {targets.Count} chunk(s) around edited cell {editedCell}.");

		foreach (ChunkKey key in targets)
		{
			if (AlwaysLoadWholePlanet)
			{
				BuildChunkImmediate(key, snapshot);
			}
			else
			{
				RequestChunkBuild(key, snapshot);
			}
		}
	}

	private bool TryGetRaycastHitInfo(CollisionObject3D? collider, int faceIndex, out RaycastTriangleInfo hitInfo)
	{
		hitInfo = default;

		if (collider is not StaticBody3D staticBody ||
			!chunkBodiesById.TryGetValue(staticBody.GetInstanceId(), out PlanetChunk? chunk) ||
			faceIndex < 0 ||
			faceIndex >= chunk.RaycastTriangles.Count)
		{
			return false;
		}

		hitInfo = chunk.RaycastTriangles[faceIndex];
		return true;
	}

	private bool TryResolveCellFromWorldPoint(Vector3 worldPoint, bool expectSolid, out PlanetCellId resolvedCell)
	{
		resolvedCell = default;

		Vector3 local = worldPoint - PlanetCenter;
		float distance = local.Length();
		if (distance <= 0.0001f)
		{
			return false;
		}

		WrappedColumn column = ProjectCubePointToColumnStatic(local / distance, faceResolution);
		int baseRadius = Mathf.Max(0, Mathf.FloorToInt(distance / Mathf.Max(0.0001f, BlockSize)));

		for (int radiusOffset = -1; radiusOffset <= 1; radiusOffset++)
		{
			int radius = baseRadius + radiusOffset;
			if (radius < 0)
			{
				continue;
			}

			PlanetCellId candidate = new(column.Face, radius, column.U, column.V);
			if (CellMatchesResolvedState(candidate, expectSolid))
			{
				resolvedCell = candidate;
				return true;
			}

			if (TryResolveNeighborCell(candidate, CellFace.NegativeU, expectSolid, out resolvedCell) ||
				TryResolveNeighborCell(candidate, CellFace.PositiveU, expectSolid, out resolvedCell) ||
				TryResolveNeighborCell(candidate, CellFace.NegativeV, expectSolid, out resolvedCell) ||
				TryResolveNeighborCell(candidate, CellFace.PositiveV, expectSolid, out resolvedCell))
			{
				return true;
			}
		}

		return false;
	}

	private bool TryResolveNeighborCell(PlanetCellId originCell, CellFace face, bool expectSolid, out PlanetCellId resolvedCell)
	{
		resolvedCell = default;
		if (!TryGetAdjacentCellStatic(originCell, face, faceResolution, out PlanetCellId adjacentCell))
		{
			return false;
		}

		if (!CellMatchesResolvedState(adjacentCell, expectSolid))
		{
			return false;
		}

		resolvedCell = adjacentCell;
		return true;
	}

	private bool CellMatchesResolvedState(PlanetCellId cell, bool expectSolid)
	{
		return IsValidCell(cell) && HasBlock(cell) == expectSolid;
	}

	private float GetHitResolveEpsilon()
	{
		return Mathf.Clamp(BlockSize * 0.1f, 0.02f, 0.25f);
	}

	private void SyncActiveChunks(ChunkAnchor anchor, bool buildImmediately = false)
	{
		HashSet<ChunkKey> nextRenderChunks = [];
		int chunkSize = Mathf.Max(8, ChunkSizeInCells);

		for (int du = -ActiveRenderChunkRadius; du <= ActiveRenderChunkRadius; du++)
		{
			for (int dv = -ActiveRenderChunkRadius; dv <= ActiveRenderChunkRadius; dv++)
			{
				WrappedColumn wrappedColumn = WrapColumn(anchor.Face, anchor.U + du * chunkSize, anchor.V + dv * chunkSize, faceResolution);
				int surfaceHeight;
				lock (worldDataLock)
				{
					surfaceHeight = GetDefaultColumnHeightUnlocked(wrappedColumn.Face, wrappedColumn.U, wrappedColumn.V);
				}
				int minRadius = Mathf.Max(0, surfaceHeight - SurfaceShellDepthInBlocks);
				int maxRadiusExclusive = surfaceHeight + ExtraOutwardBlocks;
				int minRadiusChunk = minRadius / chunkSize;
				int maxRadiusChunk = Mathf.Max(minRadiusChunk, Mathf.Max(0, maxRadiusExclusive - 1) / chunkSize);

				for (int radiusChunk = minRadiusChunk; radiusChunk <= maxRadiusChunk; radiusChunk++)
				{
					ChunkKey key = new(wrappedColumn.Face, radiusChunk, wrappedColumn.U / chunkSize, wrappedColumn.V / chunkSize);
					nextRenderChunks.Add(key);
				}
			}
		}

		ApplyChunkSet(nextRenderChunks, buildImmediately, FormatAnchor(anchor));
	}

	private void ApplyChunkSet(
		HashSet<ChunkKey> nextRenderChunks,
		bool buildImmediately,
		string scopeLabel)
	{
		WorldSnapshot snapshot = CreateWorldSnapshot();
		int staleChunkCount = 0;

		foreach (ChunkKey staleKey in activeRenderChunks)
		{
			if (!nextRenderChunks.Contains(staleKey))
			{
				staleChunkCount++;
				DestroyChunk(staleKey);
			}
		}

		activeRenderChunks.Clear();

		foreach (ChunkKey key in nextRenderChunks)
		{
			activeRenderChunks.Add(key);

			if (buildImmediately)
			{
				BuildChunkImmediate(key, snapshot);
			}
			else
			{
				RequestChunkBuild(key, snapshot);
			}
		}

		RuntimeLog.Info(RuntimeLogChannel.Chunk,
			$"Chunk sync complete for {scopeLabel}. Chunks={nextRenderChunks.Count}, CollisionChunks={nextRenderChunks.Count}, Destroyed={staleChunkCount}, BuildImmediately={buildImmediately}, AlwaysLoadWholePlanet={AlwaysLoadWholePlanet}");
	}

	private void BeginInitialChunkLoad(HashSet<ChunkKey> chunkKeys, bool buildImmediately)
	{
		pendingInitialLoadChunks.Clear();
		initialLoadTargetChunkCount = chunkKeys.Count;

		if (buildImmediately)
		{
			return;
		}

		foreach (ChunkKey key in chunkKeys)
		{
			pendingInitialLoadChunks.Add(key);
		}
	}

	private int GetMaxPlanetRadiusChunk(int chunkSize)
	{
		int maxRadiusExclusive = Mathf.CeilToInt(BaseRadiusInBlocks + HeightVariationInBlocks) + ExtraOutwardBlocks;
		return Mathf.Max(0, Mathf.Max(0, maxRadiusExclusive - 1) / chunkSize);
	}

	private bool HasLoadedChunkWithoutCollision()
	{
		foreach (PlanetChunk chunk in chunks.Values)
		{
			if (chunk.MeshInstance.Mesh == null)
			{
				continue;
			}

			if (chunk.CollisionShape.Shape == null)
			{
				RuntimeLog.Warning(RuntimeLogChannel.Chunk,
					$"Detected loaded chunk without collision in always-load mode: {FormatChunkKey(chunk.Key)}");
				return true;
			}
		}

		return false;
	}

	private void ClearAllChunks()
	{
		RuntimeLog.Info(RuntimeLogChannel.Chunk,
			$"Clearing all chunks. ExistingChunks={chunks.Count}, CollisionBodies={chunkBodiesById.Count}, PendingBuilds={completedBuilds.Count}");
		foreach (PlanetChunk chunk in chunks.Values)
		{
			chunk.Root.QueueFree();
		}

		chunks.Clear();
		chunkBodiesById.Clear();
		latestRequestedRevisions.Clear();
		activeRenderChunks.Clear();
		queuedChunkBuilds.Clear();

		while (completedBuilds.TryDequeue(out _))
		{
		}
	}

	private ChunkKey GetChunkKey(PlanetCellId cell)
	{
		int chunkSize = Mathf.Max(8, ChunkSizeInCells);
		return new ChunkKey(cell.Face, cell.Radius / chunkSize, cell.U / chunkSize, cell.V / chunkSize);
	}

	private PlanetChunk GetOrCreateChunk(ChunkKey key)
	{
		if (chunks.TryGetValue(key, out PlanetChunk? existingChunk))
		{
			return existingChunk;
		}

		Node3D root = new()
		{
			Name = $"Chunk_{key.Face}_{key.RadiusChunk}_{key.UChunk}_{key.VChunk}"
		};
		chunkRoot!.AddChild(root);

		MeshInstance3D meshInstance = new() { Name = "Mesh" };
		root.AddChild(meshInstance);

		StaticBody3D collisionBody = new() { Name = "Collider" };
		root.AddChild(collisionBody);

		CollisionShape3D collisionShape = new() { Name = "CollisionShape3D" };
		collisionBody.AddChild(collisionShape);

		PlanetChunk chunk = new(key, root, meshInstance, collisionBody, collisionShape);
		chunks[key] = chunk;
		RuntimeLog.Info(RuntimeLogChannel.Chunk, $"Created chunk node {FormatChunkKey(key)}.");
		return chunk;
	}

	private void DestroyChunk(ChunkKey key)
	{
		if (!chunks.TryGetValue(key, out PlanetChunk? chunk))
		{
			return;
		}

		RuntimeLog.Info(RuntimeLogChannel.Chunk, $"Destroying chunk {FormatChunkKey(key)}.");
		chunkBodiesById.Remove(chunk.CollisionBody.GetInstanceId());
		latestRequestedRevisions.Remove(key);
		chunk.Root.QueueFree();
		chunks.Remove(key);
	}

	private WorldSnapshot CreateWorldSnapshot()
	{
		lock (worldDataLock)
		{
			return new WorldSnapshot(
				new Dictionary<PlanetCellId, VoxelBlockType>(placedBlocks),
				new HashSet<PlanetCellId>(removedCells),
				new PlanetGenerationConfig(
					BaseRadiusInBlocks,
					HeightVariationInBlocks,
					NoiseScale,
					WorldSeed,
					DistortionOptimizedRotationEuler,
					BlockSize,
					faceResolution,
					ChunkSizeInCells,
					SurfaceShellDepthInBlocks,
					ExtraOutwardBlocks,
					CullFacesAgainstNeighborBlocks,
					UseDebugColors));
		}
	}

	private void RequestChunkBuild(ChunkKey key, WorldSnapshot snapshot)
	{
		int revision = ++buildRevisionCounter;
		latestRequestedRevisions[key] = revision;

		if (chunks.TryGetValue(key, out PlanetChunk? chunk))
		{
			chunk.RequestedRevision = revision;
		}

		RuntimeLog.Info(RuntimeLogChannel.Chunk,
			$"Scheduling chunk build {FormatChunkKey(key)}. Revision={revision}, CollisionEnabled=true");
		ChunkBuildRequest request = new(key, revision, snapshot, atlasUvRects);
		queuedChunkBuilds.Enqueue(request);
	}

	private void BuildChunkImmediate(ChunkKey key, WorldSnapshot snapshot)
	{
		int revision = ++buildRevisionCounter;
		latestRequestedRevisions[key] = revision;

		RuntimeLog.Info(RuntimeLogChannel.Chunk,
			$"Building chunk immediately {FormatChunkKey(key)}. Revision={revision}, CollisionEnabled=true");
		ChunkBuildRequest request = new(key, revision, snapshot, atlasUvRects);
		ApplyChunkBuildResult(BuildChunk(request));
	}

	private void ApplyCompletedBuilds()
	{
		int processed = 0;
		while (processed < MaxCompletedBuildsPerFrame && completedBuilds.TryDequeue(out ChunkBuildResult result))
		{
			processed++;
			ApplyChunkBuildResult(result);
		}

		if (processed > 0)
		{
			RuntimeLog.Info(RuntimeLogChannel.Chunk,
				$"Applied {processed} completed chunk build(s) this frame. RemainingQueued={completedBuilds.Count}");
		}
	}

	private void DispatchQueuedChunkBuilds()
	{
		int dispatched = 0;
		while (dispatched < MaxQueuedChunkDispatchesPerFrame &&
			   activeQueuedChunkBuilds < MaxConcurrentChunkBuilds &&
			   queuedChunkBuilds.Count > 0)
		{
			ChunkBuildRequest request = queuedChunkBuilds.Dequeue();
			if (!activeRenderChunks.Contains(request.Key))
			{
				continue;
			}

			if (latestRequestedRevisions.TryGetValue(request.Key, out int latestRevision) && latestRevision != request.Revision)
			{
				continue;
			}

			dispatched++;
			Interlocked.Increment(ref activeQueuedChunkBuilds);
			_ = Task.Run(() =>
			{
				try
				{
					ChunkBuildResult result = BuildChunk(request);
					completedBuilds.Enqueue(result);
				}
				finally
				{
					Interlocked.Decrement(ref activeQueuedChunkBuilds);
				}
			});
		}

		if (dispatched > 0)
		{
			RuntimeLog.InfoEverySeconds(RuntimeLogChannel.Chunk, "chunk-dispatch", 0.25,
				() => $"Dispatched {dispatched} chunk build(s). RemainingQueued={queuedChunkBuilds.Count}, ActiveBackgroundBuilds={activeQueuedChunkBuilds}, InitialLoadRemaining={pendingInitialLoadChunks.Count}");
		}
	}

	private void ApplyChunkBuildResult(ChunkBuildResult result)
	{
		if (!activeRenderChunks.Contains(result.Key))
		{
			RuntimeLog.Warning(RuntimeLogChannel.Chunk,
				$"Discarded chunk build because chunk is no longer active: {FormatChunkKey(result.Key)} revision={result.Revision}");
			return;
		}

		if (latestRequestedRevisions.TryGetValue(result.Key, out int latestRevision) && latestRevision > result.Revision)
		{
			RuntimeLog.Warning(RuntimeLogChannel.Chunk,
				$"Discarded stale chunk build for {FormatChunkKey(result.Key)}. Revision={result.Revision}, Latest={latestRevision}");
			return;
		}

		PlanetChunk chunk = GetOrCreateChunk(result.Key);
		if (chunk.RequestedRevision > result.Revision)
		{
			RuntimeLog.Warning(RuntimeLogChannel.Chunk,
				$"Discarded chunk build because chunk already has newer requested revision. Chunk={FormatChunkKey(result.Key)}, Revision={result.Revision}, Requested={chunk.RequestedRevision}");
			return;
		}

		chunk.RequestedRevision = result.Revision;
		chunk.RaycastTriangles.Clear();

		if (result.Vertices.Length == 0)
		{
			RuntimeLog.Warning(RuntimeLogChannel.Chunk,
				$"Chunk build returned no geometry for {FormatChunkKey(result.Key)}. Destroying chunk.");
			DestroyChunk(result.Key);
			MarkInitialChunkLoadComplete(result.Key);
			return;
		}

		ArrayMesh mesh = new();
		Godot.Collections.Array arrays = [];
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = result.Vertices;
		arrays[(int)Mesh.ArrayType.Normal] = result.Normals;
		arrays[(int)Mesh.ArrayType.TexUV] = result.Uvs;
		arrays[(int)Mesh.ArrayType.Color] = result.Colors;
		arrays[(int)Mesh.ArrayType.Index] = result.Indices;
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		mesh.SurfaceSetMaterial(0, planetMaterial);
		chunk.MeshInstance.Mesh = mesh;

		chunk.RaycastTriangles.AddRange(result.RaycastTriangles);
		chunk.CollisionShape.Shape = new ConcavePolygonShape3D
		{
			BackfaceCollision = true,
			Data = result.CollisionFaces
		};
		chunkBodiesById[chunk.CollisionBody.GetInstanceId()] = chunk;
		MarkInitialChunkLoadComplete(result.Key);

		RuntimeLog.Info(RuntimeLogChannel.Chunk,
			$"Applied chunk build {FormatChunkKey(result.Key)}. Revision={result.Revision}, Vertices={result.Vertices.Length}, Indices={result.Indices.Length}, CollisionFaces={result.CollisionFaces.Length}, RaycastTriangles={result.RaycastTriangles.Length}, CollisionEnabled=true");
	}

	private void MarkInitialChunkLoadComplete(ChunkKey key)
	{
		if (!pendingInitialLoadChunks.Remove(key))
		{
			return;
		}

		if (pendingInitialLoadChunks.Count == 0)
		{
			RuntimeLog.Info(RuntimeLogChannel.World,
				$"Initial planet chunk load complete. TotalChunks={initialLoadTargetChunkCount}");
		}
	}

	private void ResetInitialChunkLoadState()
	{
		pendingInitialLoadChunks.Clear();
		initialLoadTargetChunkCount = 0;
	}

	private static ChunkBuildResult BuildChunk(ChunkBuildRequest request)
	{
		ChunkKey key = request.Key;
		PlanetGenerationConfig config = request.Snapshot.Config;
		int chunkSize = Mathf.Max(8, config.ChunkSizeInCells);
		int radiusStart = key.RadiusChunk * chunkSize;
		int radiusEnd = radiusStart + chunkSize;
		int uStart = key.UChunk * chunkSize;
		int uEnd = Mathf.Min(config.FaceResolution, uStart + chunkSize);
		int vStart = key.VChunk * chunkSize;
		int vEnd = Mathf.Min(config.FaceResolution, vStart + chunkSize);

		if (uStart >= config.FaceResolution || vStart >= config.FaceResolution)
		{
			return ChunkBuildResult.Empty(key, request.Revision);
		}

		List<Vector3> vertices = [];
		List<Vector3> normals = [];
		List<Vector2> uvs = [];
		List<Color> colors = [];
		List<int> indices = [];
		List<Vector3> collisionFaces = [];
		List<RaycastTriangleInfo> raycastTriangles = [];
		Dictionary<long, int> localHeightCache = [];
		FastNoiseLite localNoise = CreateLocalNoise(config);

		for (int radius = radiusStart; radius < radiusEnd; radius++)
		{
			for (int u = uStart; u < uEnd; u++)
			{
				for (int v = vStart; v < vEnd; v++)
				{
					PlanetCellId cell = new(key.Face, radius, u, v);
					if (!HasBlockSnapshot(cell, request.Snapshot, localHeightCache, localNoise))
					{
						continue;
					}

					AddVisibleFacesForCellSnapshot(cell, request, localHeightCache, localNoise, vertices, normals, uvs, colors, indices, collisionFaces, raycastTriangles);
				}
			}
		}

		return new ChunkBuildResult(
			key,
			request.Revision,
			vertices.ToArray(),
			normals.ToArray(),
			uvs.ToArray(),
			colors.ToArray(),
			indices.ToArray(),
			collisionFaces.ToArray(),
			raycastTriangles.ToArray());
	}

	private static void AddVisibleFacesForCellSnapshot(
		PlanetCellId cell,
		ChunkBuildRequest request,
		Dictionary<long, int> localHeightCache,
		FastNoiseLite noiseSource,
		List<Vector3> vertices,
		List<Vector3> normals,
		List<Vector2> uvs,
		List<Color> colors,
		List<int> indices,
		List<Vector3> collisionFaces,
		List<RaycastTriangleInfo> raycastTriangles)
	{
		Vector3[] corners = GetCellCornersStatic(cell, request.Snapshot.Config);
		Rect2 atlasRect = GetAtlasRectStatic(GetBlockTypeSnapshot(cell, request.Snapshot, localHeightCache, noiseSource), request.AtlasRects);
		Color vertexColor = request.Snapshot.Config.UseDebugColors ? GetDebugColor(cell) : Colors.White;

		AddFaceIfVisibleSnapshot(cell, CellFace.Outward, [corners[4], corners[5], corners[6], corners[7]], atlasRect, vertexColor, request, localHeightCache, noiseSource, vertices, normals, uvs, colors, indices, collisionFaces, raycastTriangles);
		AddFaceIfVisibleSnapshot(cell, CellFace.Inward, [corners[0], corners[3], corners[2], corners[1]], atlasRect, vertexColor, request, localHeightCache, noiseSource, vertices, normals, uvs, colors, indices, collisionFaces, raycastTriangles);
		AddFaceIfVisibleSnapshot(cell, CellFace.PositiveU, [corners[1], corners[2], corners[6], corners[5]], atlasRect, vertexColor, request, localHeightCache, noiseSource, vertices, normals, uvs, colors, indices, collisionFaces, raycastTriangles);
		AddFaceIfVisibleSnapshot(cell, CellFace.NegativeU, [corners[0], corners[4], corners[7], corners[3]], atlasRect, vertexColor, request, localHeightCache, noiseSource, vertices, normals, uvs, colors, indices, collisionFaces, raycastTriangles);
		AddFaceIfVisibleSnapshot(cell, CellFace.PositiveV, [corners[3], corners[7], corners[6], corners[2]], atlasRect, vertexColor, request, localHeightCache, noiseSource, vertices, normals, uvs, colors, indices, collisionFaces, raycastTriangles);
		AddFaceIfVisibleSnapshot(cell, CellFace.NegativeV, [corners[0], corners[1], corners[5], corners[4]], atlasRect, vertexColor, request, localHeightCache, noiseSource, vertices, normals, uvs, colors, indices, collisionFaces, raycastTriangles);
	}

	private static void AddFaceIfVisibleSnapshot(
		PlanetCellId cell,
		CellFace face,
		Vector3[] quad,
		Rect2 atlasRect,
		Color vertexColor,
		ChunkBuildRequest request,
		Dictionary<long, int> localHeightCache,
		FastNoiseLite noiseSource,
		List<Vector3> vertices,
		List<Vector3> normals,
		List<Vector2> uvs,
		List<Color> colors,
		List<int> indices,
		List<Vector3> collisionFaces,
		List<RaycastTriangleInfo> raycastTriangles)
	{
		if (request.Snapshot.Config.CullFacesAgainstNeighborBlocks &&
			TryGetAdjacentCellStatic(cell, face, request.Snapshot.Config.FaceResolution, out PlanetCellId neighbor) &&
			HasBlockSnapshot(neighbor, request.Snapshot, localHeightCache, noiseSource))
		{
			return;
		}

		AddQuad(
			quad[0],
			quad[1],
			quad[2],
			quad[3],
			atlasRect,
			vertexColor,
			new RaycastTriangleInfo(cell, face),
			vertices,
			normals,
			uvs,
			colors,
			indices,
			collisionFaces,
			raycastTriangles);
	}

	private static void AddQuad(
		Vector3 a,
		Vector3 b,
		Vector3 c,
		Vector3 d,
		Rect2 atlasRect,
		Color vertexColor,
		RaycastTriangleInfo hitInfo,
		List<Vector3> vertices,
		List<Vector3> normals,
		List<Vector2> uvs,
		List<Color> colors,
		List<int> indices,
		List<Vector3> collisionFaces,
		List<RaycastTriangleInfo> raycastTriangles)
	{
		int start = vertices.Count;
		Vector3 faceNormal = (b - a).Cross(c - a).Normalized();
		float minU = atlasRect.Position.X;
		float minV = atlasRect.Position.Y;
		float maxU = atlasRect.Position.X + atlasRect.Size.X;
		float maxV = atlasRect.Position.Y + atlasRect.Size.Y;

		vertices.Add(a);
		vertices.Add(b);
		vertices.Add(c);
		vertices.Add(d);

		normals.Add(faceNormal);
		normals.Add(faceNormal);
		normals.Add(faceNormal);
		normals.Add(faceNormal);

		uvs.Add(new Vector2(minU, maxV));
		uvs.Add(new Vector2(maxU, maxV));
		uvs.Add(new Vector2(maxU, minV));
		uvs.Add(new Vector2(minU, minV));

		colors.Add(vertexColor);
		colors.Add(vertexColor);
		colors.Add(vertexColor);
		colors.Add(vertexColor);

		indices.Add(start + 0);
		indices.Add(start + 1);
		indices.Add(start + 2);
		indices.Add(start + 0);
		indices.Add(start + 2);
		indices.Add(start + 3);

		if (collisionFaces == null || raycastTriangles == null)
		{
			return;
		}

		collisionFaces.Add(a);
		collisionFaces.Add(b);
		collisionFaces.Add(c);
		raycastTriangles.Add(hitInfo);

		collisionFaces.Add(a);
		collisionFaces.Add(c);
		collisionFaces.Add(d);
		raycastTriangles.Add(hitInfo);
	}

	private void RemoveBlockInternal(PlanetCellId cell)
	{
		if (!HasBlockUnlocked(cell))
		{
			return;
		}

		bool defaultSolid = IsDefaultSolidUnlocked(cell);
		if (placedBlocks.ContainsKey(cell))
		{
			placedBlocks.Remove(cell);

			if (defaultSolid)
			{
				removedCells.Add(cell);
			}
		}
		else if (defaultSolid)
		{
			removedCells.Add(cell);
		}
	}

	private PlanetPlayer? ResolvePlayer()
	{
		if (!PlayerPath.IsEmpty)
		{
			return GetNodeOrNull<PlanetPlayer>(PlayerPath);
		}

		return GetNodeOrNull<PlanetPlayer>("../Player");
	}

	private ChunkAnchor GetAnchorFromWorldPosition(Vector3 worldPosition)
	{
		WrappedColumn column = ProjectCubePointToColumnStatic((worldPosition - PlanetCenter).Normalized(), faceResolution);
		return new ChunkAnchor(column.Face, column.U, column.V);
	}

	private static WrappedColumn WrapColumn(int face, int u, int v, int faceResolution)
	{
		float s = (u + 0.5f) / faceResolution * 2f - 1f;
		float t = (v + 0.5f) / faceResolution * 2f - 1f;
		return ProjectCubePointToColumnStatic(GetCubePoint(face, s, t), faceResolution);
	}

	private static string FormatChunkKey(ChunkKey key)
	{
		return $"face={key.Face}, radiusChunk={key.RadiusChunk}, uChunk={key.UChunk}, vChunk={key.VChunk}";
	}

	private static string FormatAnchor(ChunkAnchor anchor)
	{
		return $"face={anchor.Face}, u={anchor.U}, v={anchor.V}";
	}

	private static WrappedColumn ProjectCubePointToColumnStatic(Vector3 cubePoint, int resolution)
	{
		float absoluteX = Mathf.Abs(cubePoint.X);
		float absoluteY = Mathf.Abs(cubePoint.Y);
		float absoluteZ = Mathf.Abs(cubePoint.Z);

		int face = absoluteX >= absoluteY && absoluteX >= absoluteZ
			? (cubePoint.X >= 0f ? 0 : 1)
			: absoluteY >= absoluteZ
				? (cubePoint.Y >= 0f ? 2 : 3)
				: (cubePoint.Z >= 0f ? 4 : 5);

		float dominant = Mathf.Max(absoluteX, Mathf.Max(absoluteY, absoluteZ));
		float s = cubePoint.Dot(FaceAxisU[face]) / dominant;
		float t = cubePoint.Dot(FaceAxisV[face]) / dominant;
		int wrappedU = Mathf.Clamp(Mathf.FloorToInt(((s + 1f) * 0.5f) * resolution), 0, resolution - 1);
		int wrappedV = Mathf.Clamp(Mathf.FloorToInt(((t + 1f) * 0.5f) * resolution), 0, resolution - 1);
		return new WrappedColumn(face, wrappedU, wrappedV);
	}

	private static bool TryGetAdjacentCellStatic(PlanetCellId cell, CellFace face, int faceResolution, out PlanetCellId adjacentCell)
	{
		adjacentCell = default;
		switch (face)
		{
			case CellFace.Outward:
				adjacentCell = new PlanetCellId(cell.Face, cell.Radius + 1, cell.U, cell.V);
				return true;
			case CellFace.Inward:
				if (cell.Radius == 0)
				{
					return false;
				}

				adjacentCell = new PlanetCellId(cell.Face, cell.Radius - 1, cell.U, cell.V);
				return true;
			case CellFace.NegativeU:
			case CellFace.PositiveU:
			case CellFace.NegativeV:
			case CellFace.PositiveV:
				WrappedColumn wrapped = face switch
				{
					CellFace.NegativeU => WrapColumn(cell.Face, cell.U - 1, cell.V, faceResolution),
					CellFace.PositiveU => WrapColumn(cell.Face, cell.U + 1, cell.V, faceResolution),
					CellFace.NegativeV => WrapColumn(cell.Face, cell.U, cell.V - 1, faceResolution),
					CellFace.PositiveV => WrapColumn(cell.Face, cell.U, cell.V + 1, faceResolution),
					_ => throw new ArgumentOutOfRangeException(nameof(face), face, null)
				};
				adjacentCell = new PlanetCellId(wrapped.Face, cell.Radius, wrapped.U, wrapped.V);
				return true;
			default:
				return false;
		}
	}

	private static bool HasBlockSnapshot(PlanetCellId cell, WorldSnapshot snapshot, Dictionary<long, int> localHeightCache, FastNoiseLite noiseSource)
	{
		if (cell.Face < 0 || cell.Face >= FaceNormals.Length || cell.U < 0 || cell.U >= snapshot.Config.FaceResolution || cell.V < 0 || cell.V >= snapshot.Config.FaceResolution || cell.Radius < 0)
		{
			return false;
		}

		if (snapshot.PlacedBlocks.TryGetValue(cell, out VoxelBlockType placed))
		{
			return placed != VoxelBlockType.Air;
		}

		if (snapshot.RemovedCells.Contains(cell))
		{
			return false;
		}

		return cell.Radius < GetDefaultColumnHeightStatic(cell.Face, cell.U, cell.V, snapshot.Config, localHeightCache, noiseSource);
	}

	private static VoxelBlockType GetBlockTypeSnapshot(PlanetCellId cell, WorldSnapshot snapshot, Dictionary<long, int> localHeightCache, FastNoiseLite noiseSource)
	{
		if (snapshot.PlacedBlocks.TryGetValue(cell, out VoxelBlockType placed))
		{
			return placed;
		}

		if (snapshot.RemovedCells.Contains(cell))
		{
			return VoxelBlockType.Air;
		}

		int surfaceRadius = GetDefaultColumnHeightStatic(cell.Face, cell.U, cell.V, snapshot.Config, localHeightCache, noiseSource) - 1;
		if (cell.Radius == surfaceRadius)
		{
			return VoxelBlockType.Grass;
		}

		if (cell.Radius >= surfaceRadius - 2)
		{
			return VoxelBlockType.Dirt;
		}

		return VoxelBlockType.Stone;
	}

	private static int GetDefaultColumnHeightStatic(int face, int u, int v, PlanetGenerationConfig config, Dictionary<long, int> cache, FastNoiseLite noiseSource)
	{
		long key = ((long)face << 42) | ((long)u << 21) | (uint)v;
		if (cache.TryGetValue(key, out int cached))
		{
			return cached;
		}

		Vector3 direction = GetDirectionForCellCenterStatic(face, u, v, config.FaceResolution);
		Vector3 rotated = ApplyNoiseRotationStatic(direction, config.NoiseScale, config.DistortionOptimizedRotationEuler);
		float sample = noiseSource.GetNoise3D(rotated.X, rotated.Y, rotated.Z);
		int height = Mathf.Max(1, config.BaseRadiusInBlocks + Mathf.RoundToInt(sample * config.HeightVariationInBlocks));
		cache[key] = height;
		return height;
	}

	private int GetDefaultColumnHeightUnlocked(int face, int u, int v)
	{
		long key = ((long)face << 42) | ((long)u << 21) | (uint)v;
		if (defaultColumnHeights.TryGetValue(key, out int cached))
		{
			return cached;
		}

		Vector3 direction = GetDirectionForCellCenterStatic(face, u, v, faceResolution);
		Vector3 rotated = ApplyNoiseRotationStatic(direction, NoiseScale, DistortionOptimizedRotationEuler);
		int height = Mathf.Max(1, BaseRadiusInBlocks + Mathf.RoundToInt(noise!.GetNoise3D(rotated.X, rotated.Y, rotated.Z) * HeightVariationInBlocks));
		defaultColumnHeights[key] = height;
		return height;
	}

	private static Vector3 GetDirectionForCellCenterStatic(int face, int u, int v, int resolution)
	{
		float s = GetRawFaceCoordinateStatic(u + 0.5f, resolution);
		float t = GetRawFaceCoordinateStatic(v + 0.5f, resolution);
		return GetCubePoint(face, s, t).Normalized();
	}

	private static Vector3[] GetCellCornersStatic(PlanetCellId cell, PlanetGenerationConfig config)
	{
		float s0 = GetRawFaceCoordinateStatic(cell.U, config.FaceResolution);
		float s1 = GetRawFaceCoordinateStatic(cell.U + 1, config.FaceResolution);
		float t0 = GetRawFaceCoordinateStatic(cell.V, config.FaceResolution);
		float t1 = GetRawFaceCoordinateStatic(cell.V + 1, config.FaceResolution);

		Vector3 d00 = GetCubePoint(cell.Face, s0, t0).Normalized();
		Vector3 d10 = GetCubePoint(cell.Face, s1, t0).Normalized();
		Vector3 d11 = GetCubePoint(cell.Face, s1, t1).Normalized();
		Vector3 d01 = GetCubePoint(cell.Face, s0, t1).Normalized();
		float innerRadius = cell.Radius * config.BlockSize;
		float outerRadius = (cell.Radius + 1) * config.BlockSize;

		return
		[
			d00 * innerRadius,
			d10 * innerRadius,
			d11 * innerRadius,
			d01 * innerRadius,
			d00 * outerRadius,
			d10 * outerRadius,
			d11 * outerRadius,
			d01 * outerRadius
		];
	}

	private Vector3[] GetCellCorners(PlanetCellId cell)
	{
		PlanetGenerationConfig config = new(
			BaseRadiusInBlocks,
			HeightVariationInBlocks,
			NoiseScale,
			WorldSeed,
			DistortionOptimizedRotationEuler,
			BlockSize,
			faceResolution,
			ChunkSizeInCells,
			SurfaceShellDepthInBlocks,
			ExtraOutwardBlocks,
			CullFacesAgainstNeighborBlocks,
			UseDebugColors);
		Vector3[] local = GetCellCornersStatic(cell, config);
		for (int i = 0; i < local.Length; i++)
		{
			local[i] += PlanetCenter;
		}

		return local;
	}

	private static float GetRawFaceCoordinateStatic(float gridPosition, int resolution)
	{
		return Mathf.Clamp((gridPosition / resolution) * 2f - 1f, -1f, 1f);
	}

	private static Vector3 GetCubePoint(int face, float s, float t)
	{
		return FaceNormals[face] + FaceAxisU[face] * s + FaceAxisV[face] * t;
	}

	private static Vector3 ApplyNoiseRotationStatic(Vector3 direction, float noiseScale, Vector3 rotationDegrees)
	{
		Vector3 radians = rotationDegrees * (Mathf.Pi / 180f);
		Vector3 rotated = direction.Rotated(Vector3.Right, radians.X);
		rotated = rotated.Rotated(Vector3.Up, radians.Y);
		rotated = rotated.Rotated(Vector3.Back, radians.Z);
		return rotated * Mathf.Max(0.01f, noiseScale);
	}

	private static FastNoiseLite CreateLocalNoise(PlanetGenerationConfig config)
	{
		FastNoiseLite localNoise = new()
		{
			Seed = config.WorldSeed,
			NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
			FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
			FractalOctaves = 4,
			FractalLacunarity = 2f,
			FractalGain = 0.5f,
			Frequency = Mathf.Max(0.01f, config.NoiseScale / Mathf.Max(1f, config.BaseRadiusInBlocks))
		};
		return localNoise;
	}

	private static Rect2 GetAtlasRectStatic(VoxelBlockType blockType, IReadOnlyDictionary<VoxelBlockType, Rect2> atlasRects)
	{
		return atlasRects.TryGetValue(blockType, out Rect2 rect) ? rect : atlasRects[VoxelBlockType.Stone];
	}

	private static Color GetDebugColor(PlanetCellId cell)
	{
		return cell.Face switch
		{
			0 => new Color(0.95f, 0.76f, 0.76f),
			1 => new Color(0.74f, 0.88f, 0.96f),
			2 => new Color(0.98f, 0.96f, 0.71f),
			3 => new Color(0.86f, 0.78f, 0.98f),
			4 => new Color(0.76f, 0.95f, 0.84f),
			_ => new Color(1f, 0.86f, 0.74f)
		};
	}

	private bool HasBlockUnlocked(PlanetCellId cell)
	{
		if (!IsValidCell(cell))
		{
			return false;
		}

		if (placedBlocks.TryGetValue(cell, out VoxelBlockType placedType))
		{
			return placedType != VoxelBlockType.Air;
		}

		if (removedCells.Contains(cell))
		{
			return false;
		}

		return IsDefaultSolidUnlocked(cell);
	}

	private bool IsDefaultSolidUnlocked(PlanetCellId cell)
	{
		return cell.Radius < GetDefaultColumnHeightUnlocked(cell.Face, cell.U, cell.V);
	}

	private VoxelBlockType GetDefaultBlockTypeUnlocked(PlanetCellId cell)
	{
		int surfaceRadius = GetDefaultColumnHeightUnlocked(cell.Face, cell.U, cell.V) - 1;
		if (cell.Radius == surfaceRadius)
		{
			return VoxelBlockType.Grass;
		}

		if (cell.Radius >= surfaceRadius - 2)
		{
			return VoxelBlockType.Dirt;
		}

		return VoxelBlockType.Stone;
	}

	private bool IsValidCell(PlanetCellId cell)
	{
		return cell.Face >= 0 &&
			   cell.Face < FaceNormals.Length &&
			   cell.Radius >= 0 &&
			   cell.U >= 0 &&
			   cell.U < faceResolution &&
			   cell.V >= 0 &&
			   cell.V < faceResolution;
	}

	private readonly record struct ChunkKey(int Face, int RadiusChunk, int UChunk, int VChunk);
	private readonly record struct WrappedColumn(int Face, int U, int V);
	private readonly record struct ChunkAnchor(int Face, int U, int V);
	private readonly record struct RaycastTriangleInfo(PlanetCellId Cell, CellFace Face);
	private readonly record struct PlanetGenerationConfig(
		int BaseRadiusInBlocks,
		float HeightVariationInBlocks,
		float NoiseScale,
		int WorldSeed,
		Vector3 DistortionOptimizedRotationEuler,
		float BlockSize,
		int FaceResolution,
		int ChunkSizeInCells,
		int SurfaceShellDepthInBlocks,
		int ExtraOutwardBlocks,
		bool CullFacesAgainstNeighborBlocks,
		bool UseDebugColors = false);

	private readonly record struct WorldSnapshot(
		Dictionary<PlanetCellId, VoxelBlockType> PlacedBlocks,
		HashSet<PlanetCellId> RemovedCells,
		PlanetGenerationConfig Config);

	private readonly record struct ChunkBuildRequest(
		ChunkKey Key,
		int Revision,
		WorldSnapshot Snapshot,
		IReadOnlyDictionary<VoxelBlockType, Rect2> AtlasRects);

	private readonly record struct ChunkBuildResult(
		ChunkKey Key,
		int Revision,
		Vector3[] Vertices,
		Vector3[] Normals,
		Vector2[] Uvs,
		Color[] Colors,
		int[] Indices,
		Vector3[] CollisionFaces,
		RaycastTriangleInfo[] RaycastTriangles)
	{
		public static ChunkBuildResult Empty(ChunkKey key, int revision)
		{
			return new ChunkBuildResult(key, revision, [], [], [], [], [], [], []);
		}
	}

	private sealed class PlanetChunk
	{
		public PlanetChunk(ChunkKey key, Node3D root, MeshInstance3D meshInstance, StaticBody3D collisionBody, CollisionShape3D collisionShape)
		{
			Key = key;
			Root = root;
			MeshInstance = meshInstance;
			CollisionBody = collisionBody;
			CollisionShape = collisionShape;
		}

		public ChunkKey Key { get; }
		public Node3D Root { get; }
		public MeshInstance3D MeshInstance { get; }
		public StaticBody3D CollisionBody { get; }
		public CollisionShape3D CollisionShape { get; }
		public List<RaycastTriangleInfo> RaycastTriangles { get; } = [];
		public int RequestedRevision { get; set; }
	}

	private enum CellFace
	{
		Outward,
		Inward,
		NegativeU,
		PositiveU,
		NegativeV,
		PositiveV
	}
}
