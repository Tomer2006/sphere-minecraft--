using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace SphereMinecraft;

public partial class PlanetVoxelWorld : Node3D
{
    private const int AtlasTileSize = 48;
    private const int AtlasGridSize = 2;
    private const int AtlasPaddingPixels = 2;
    private const int DefaultFarShellResolution = 40;
    private const int MaxCompletedBuildsPerFrame = 4;

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
    private readonly HashSet<ChunkKey> activeCollisionChunks = [];
    private readonly object worldDataLock = new();

    private Node3D? chunkRoot;
    private MeshInstance3D? farShellMesh;
    private PlanetPlayer? trackedPlayer;
    private FastNoiseLite? noise;
    private StandardMaterial3D? planetMaterial;
    private Texture2D? atlasTexture;
    private ChunkAnchor? currentAnchor;
    private int faceResolution = 100;
    private int buildRevisionCounter;

    [ExportGroup("References")]
    [Export] public NodePath PlayerPath { get; set; } = new("../Player");

    [ExportGroup("Planet Shape")]
    [Export] public int BaseRadiusInBlocks { get; set; } = 50;
    [Export] public float HeightVariationInBlocks { get; set; } = 2.5f;
    [Export] public float NoiseScale { get; set; } = 1.8f;
    [Export] public int WorldSeed { get; set; } = 1337;
    [Export(PropertyHint.Range, "0,1,0.01")] public float FaceCoordinateUniformity { get; set; } = 0.4f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float PolynomialWarpBias { get; set; } = 0.6f;
    [Export] public Vector3 DistortionOptimizedRotationEuler { get; set; } = Vector3.Zero;
    [Export(PropertyHint.Range, "0,1,0.01")] public float LocalCellDeformation { get; set; } = 0.9f;
    [Export] public float BlockSize { get; set; } = 1f;
    [Export] public bool GenerateOnReady { get; set; }

    [ExportGroup("Chunk Streaming")]
    [Export(PropertyHint.Range, "8,64,1")] public int ChunkSizeInCells { get; set; } = 24;
    [Export(PropertyHint.Range, "1,16,1")] public int ActiveRenderChunkRadius { get; set; } = 6;
    [Export(PropertyHint.Range, "1,8,1")] public int ActiveCollisionChunkRadius { get; set; } = 2;
    [Export(PropertyHint.Range, "2,64,1")] public int SurfaceShellDepthInBlocks { get; set; } = 12;
    [Export(PropertyHint.Range, "1,12,1")] public int ExtraOutwardBlocks { get; set; } = 2;
    [Export(PropertyHint.Range, "8,96,1")] public int FarShellFaceResolution { get; set; } = DefaultFarShellResolution;

    [ExportGroup("Rendering")]
    [Export] public bool UseDebugColors { get; set; }
    [Export] public bool CullFacesAgainstNeighborBlocks { get; set; } = true;

    public Vector3 PlanetCenter => GlobalPosition;

    public float ApproximateSurfaceRadius => (BaseRadiusInBlocks + HeightVariationInBlocks + 1f) * BlockSize;

    public override void _Ready()
    {
        EnsureRuntimeNodes();

        if (GenerateOnReady)
        {
            CallDeferred(nameof(GeneratePlanet));
        }
    }

    public override void _Process(double delta)
    {
        ApplyCompletedBuilds();
        UpdateStreaming();
    }

    public void GeneratePlanet()
    {
        lock (worldDataLock)
        {
            placedBlocks.Clear();
            removedCells.Clear();
            defaultColumnHeights.Clear();
        }

        RebuildPlanet();
    }

    public void LoadFromSave(WorldSaveData data)
    {
        BaseRadiusInBlocks = Mathf.Max(2, data.BaseRadiusInBlocks);
        HeightVariationInBlocks = Mathf.Max(0f, data.HeightVariationInBlocks);
        NoiseScale = Mathf.Max(0.01f, data.NoiseScale);
        WorldSeed = data.WorldSeed;
        FaceCoordinateUniformity = Mathf.Clamp(data.FaceCoordinateUniformity, 0f, 1f);
        PolynomialWarpBias = Mathf.Clamp(data.PolynomialWarpBias, 0f, 1f);
        DistortionOptimizedRotationEuler = data.DistortionOptimizedRotationEuler.ToVector3();
        LocalCellDeformation = Mathf.Clamp(data.LocalCellDeformation, 0f, 1f);
        BlockSize = Mathf.Max(0.1f, data.BlockSize);
        UseDebugColors = data.UseDebugColors;
        CullFacesAgainstNeighborBlocks = data.CullFacesAgainstNeighborBlocks;
        ChunkSizeInCells = Mathf.Max(8, data.ChunkSizeInCells);

        lock (worldDataLock)
        {
            placedBlocks.Clear();
            removedCells.Clear();
            defaultColumnHeights.Clear();

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
            FaceCoordinateUniformity = FaceCoordinateUniformity,
            PolynomialWarpBias = PolynomialWarpBias,
            DistortionOptimizedRotationEuler = Vector3Save.FromVector3(DistortionOptimizedRotationEuler),
            LocalCellDeformation = LocalCellDeformation,
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
            if (placedBlocks.TryGetValue(cell, out VoxelBlockType placedType))
            {
                return placedType != VoxelBlockType.Air;
            }

            if (removedCells.Contains(cell))
            {
                return false;
            }
        }

        return IsDefaultSolid(cell);
    }

    public void PlaceBlock(PlanetCellId cell, VoxelBlockType blockType)
    {
        if (!IsValidCell(cell))
        {
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

                if (IsDefaultSolid(cell) && GetDefaultBlockType(cell) == blockType)
                {
                    placedBlocks.Remove(cell);
                }
                else
                {
                    placedBlocks[cell] = blockType;
                }
            }
        }

        InvalidateChunksAround(cell);
    }

    public void RemoveBlock(PlanetCellId cell)
    {
        if (!IsValidCell(cell))
        {
            return;
        }

        lock (worldDataLock)
        {
            RemoveBlockInternal(cell);
        }

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

        if (collider is not StaticBody3D staticBody ||
            !chunkBodiesById.TryGetValue(staticBody.GetInstanceId(), out PlanetChunk? chunk) ||
            faceIndex < 0 ||
            faceIndex >= chunk.RaycastTriangles.Count)
        {
            return false;
        }

        targetCell = chunk.RaycastTriangles[faceIndex].Cell;
        return HasBlock(targetCell);
    }

    public bool TryGetPlaceCell(CollisionObject3D? collider, int faceIndex, Vector3 position, Vector3 normal, out PlanetCellId targetCell)
    {
        targetCell = default;

        if (collider is not StaticBody3D staticBody ||
            !chunkBodiesById.TryGetValue(staticBody.GetInstanceId(), out PlanetChunk? chunk) ||
            faceIndex < 0 ||
            faceIndex >= chunk.RaycastTriangles.Count)
        {
            return false;
        }

        RaycastTriangleInfo hitInfo = chunk.RaycastTriangles[faceIndex];
        return TryGetAdjacentCell(hitInfo.Cell, hitInfo.Face, out targetCell);
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
        ClearAllChunks();
        BuildFarShell();
        UpdateStreaming(force: true);
    }

    private void EnsureRuntimeNodes()
    {
        chunkRoot ??= GetNodeOrNull<Node3D>("PlanetChunks");
        if (chunkRoot == null)
        {
            chunkRoot = new Node3D { Name = "PlanetChunks" };
            AddChild(chunkRoot);
        }

        farShellMesh ??= GetNodeOrNull<MeshInstance3D>("FarShell");
        if (farShellMesh == null)
        {
            farShellMesh = new MeshInstance3D { Name = "FarShell" };
            AddChild(farShellMesh);
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

        planetMaterial.AlbedoTexture = atlasTexture;
    }

    private Texture2D BuildTextureAtlas()
    {
        atlasUvRects.Clear();

        int atlasSize = AtlasTileSize * AtlasGridSize;
        Image image = Image.CreateEmpty(atlasSize, atlasSize, false, Image.Format.Rgba8);
        image.Fill(new Color(0f, 0f, 0f, 0f));

        PaintAtlasTile(image, VoxelBlockType.Grass, 0, new Color(0.30f, 0.58f, 0.27f), new Color(0.43f, 0.76f, 0.31f));
        PaintAtlasTile(image, VoxelBlockType.Dirt, 1, new Color(0.42f, 0.27f, 0.18f), new Color(0.54f, 0.35f, 0.22f));
        PaintAtlasTile(image, VoxelBlockType.Stone, 2, new Color(0.42f, 0.45f, 0.48f), new Color(0.62f, 0.64f, 0.68f));
        atlasUvRects[VoxelBlockType.Air] = atlasUvRects[VoxelBlockType.Stone];

        return ImageTexture.CreateFromImage(image);
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

        float atlasSize = AtlasTileSize * AtlasGridSize;
        float tileSpan = AtlasTileSize / atlasSize;
        float uvPadding = AtlasPaddingPixels / atlasSize;
        atlasUvRects[blockType] = new Rect2(
            new Vector2(tileX * tileSpan + uvPadding, tileY * tileSpan + uvPadding),
            new Vector2(tileSpan - uvPadding * 2f, tileSpan - uvPadding * 2f));
    }

    private void UpdateStreaming(bool force = false)
    {
        trackedPlayer ??= ResolvePlayer();
        if (trackedPlayer == null)
        {
            return;
        }

        ChunkAnchor nextAnchor = GetAnchorFromWorldPosition(trackedPlayer.GlobalPosition);
        if (!force && currentAnchor.HasValue && currentAnchor.Value.Equals(nextAnchor))
        {
            return;
        }

        currentAnchor = nextAnchor;
        SyncActiveChunks(nextAnchor);
    }

    private void InvalidateChunksAround(PlanetCellId editedCell)
    {
        WorldSnapshot snapshot = CreateWorldSnapshot();
        HashSet<ChunkKey> targets = [GetChunkKey(editedCell)];

        foreach (CellFace face in Enum.GetValues<CellFace>())
        {
            if (TryGetAdjacentCell(editedCell, face, out PlanetCellId adjacentCell))
            {
                targets.Add(GetChunkKey(adjacentCell));
            }
        }

        foreach (ChunkKey key in targets)
        {
            bool wantsCollision = activeCollisionChunks.Contains(key);
            RequestChunkBuild(key, wantsCollision, snapshot);
        }
    }

    private void SyncActiveChunks(ChunkAnchor anchor, bool buildImmediately = false)
    {
        HashSet<ChunkKey> nextRenderChunks = [];
        HashSet<ChunkKey> nextCollisionChunks = [];
        int chunkSize = Mathf.Max(8, ChunkSizeInCells);

        for (int du = -ActiveRenderChunkRadius; du <= ActiveRenderChunkRadius; du++)
        {
            for (int dv = -ActiveRenderChunkRadius; dv <= ActiveRenderChunkRadius; dv++)
            {
                WrappedColumn wrappedColumn = WrapColumn(anchor.Face, anchor.U + du * chunkSize, anchor.V + dv * chunkSize);
                int surfaceHeight = GetDefaultColumnHeight(wrappedColumn.Face, wrappedColumn.U, wrappedColumn.V);
                int minRadius = Mathf.Max(0, surfaceHeight - SurfaceShellDepthInBlocks);
                int maxRadiusExclusive = surfaceHeight + ExtraOutwardBlocks;

                for (int radius = minRadius; radius < maxRadiusExclusive; radius += chunkSize)
                {
                    ChunkKey key = new(wrappedColumn.Face, radius / chunkSize, wrappedColumn.U / chunkSize, wrappedColumn.V / chunkSize);
                    nextRenderChunks.Add(key);

                    if (Mathf.Abs(du) <= ActiveCollisionChunkRadius && Mathf.Abs(dv) <= ActiveCollisionChunkRadius)
                    {
                        nextCollisionChunks.Add(key);
                    }
                }
            }
        }

        WorldSnapshot snapshot = CreateWorldSnapshot();

        foreach (ChunkKey staleKey in activeRenderChunks)
        {
            if (!nextRenderChunks.Contains(staleKey))
            {
                DestroyChunk(staleKey);
            }
        }

        activeRenderChunks.Clear();
        activeCollisionChunks.Clear();

        foreach (ChunkKey key in nextRenderChunks)
        {
            activeRenderChunks.Add(key);
            bool wantsCollision = nextCollisionChunks.Contains(key);
            if (wantsCollision)
            {
                activeCollisionChunks.Add(key);
            }

            if (buildImmediately)
            {
                BuildChunkImmediate(key, wantsCollision, snapshot);
            }
            else
            {
                RequestChunkBuild(key, wantsCollision, snapshot);
            }
        }
    }

    private void ClearAllChunks()
    {
        foreach (PlanetChunk chunk in chunks.Values)
        {
            chunk.Root.QueueFree();
        }

        chunks.Clear();
        chunkBodiesById.Clear();
        latestRequestedRevisions.Clear();
        activeRenderChunks.Clear();
        activeCollisionChunks.Clear();

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
        return chunk;
    }

    private void DestroyChunk(ChunkKey key)
    {
        if (!chunks.TryGetValue(key, out PlanetChunk? chunk))
        {
            return;
        }

        chunkBodiesById.Remove(chunk.CollisionBody.GetInstanceId());
        latestRequestedRevisions.Remove(key);
        chunk.Root.QueueFree();
        chunks.Remove(key);
    }

    private void BuildFarShell()
    {
        if (farShellMesh == null)
        {
            return;
        }

        int resolution = Mathf.Max(8, FarShellFaceResolution);
        List<Vector3> vertices = [];
        List<Vector3> normals = [];
        List<Vector2> uvs = [];
        List<Color> colors = [];
        List<int> indices = [];
        Rect2 atlasRect = GetAtlasRect(VoxelBlockType.Grass);
        Color color = Colors.White;
        float radius = ApproximateSurfaceRadius;

        for (int face = 0; face < FaceNormals.Length; face++)
        {
            for (int u = 0; u < resolution; u++)
            {
                for (int v = 0; v < resolution; v++)
                {
                    Vector3 d00 = GetAdjustedSphericalDirection(face, GetFarShellCoordinate(u, resolution), GetFarShellCoordinate(v, resolution));
                    Vector3 d10 = GetAdjustedSphericalDirection(face, GetFarShellCoordinate(u + 1, resolution), GetFarShellCoordinate(v, resolution));
                    Vector3 d11 = GetAdjustedSphericalDirection(face, GetFarShellCoordinate(u + 1, resolution), GetFarShellCoordinate(v + 1, resolution));
                    Vector3 d01 = GetAdjustedSphericalDirection(face, GetFarShellCoordinate(u, resolution), GetFarShellCoordinate(v + 1, resolution));

                    AddQuad(
                        d00 * radius,
                        d10 * radius,
                        d11 * radius,
                        d01 * radius,
                        atlasRect,
                        color,
                        new RaycastTriangleInfo(default, CellFace.Outward),
                        vertices,
                        normals,
                        uvs,
                        colors,
                        indices,
                        null,
                        null);
                }
            }
        }

        ArrayMesh mesh = new();
        Godot.Collections.Array arrays = [];
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, planetMaterial);
        farShellMesh.Mesh = mesh;
    }

    private static float GetFarShellCoordinate(int index, int resolution)
    {
        return (index / (float)resolution) * 2f - 1f;
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
                    FaceCoordinateUniformity,
                    PolynomialWarpBias,
                    DistortionOptimizedRotationEuler,
                    LocalCellDeformation,
                    BlockSize,
                    faceResolution,
                    ChunkSizeInCells,
                    SurfaceShellDepthInBlocks,
                    ExtraOutwardBlocks,
                    CullFacesAgainstNeighborBlocks,
                    UseDebugColors));
        }
    }

    private void RequestChunkBuild(ChunkKey key, bool wantsCollision, WorldSnapshot snapshot)
    {
        int revision = ++buildRevisionCounter;
        latestRequestedRevisions[key] = revision;

        if (chunks.TryGetValue(key, out PlanetChunk? chunk))
        {
            chunk.RequestedRevision = revision;
            chunk.WantsCollision = wantsCollision;
        }

        ChunkBuildRequest request = new(key, wantsCollision, revision, snapshot, GetAtlasRect(VoxelBlockType.Grass), atlasUvRects);
        _ = Task.Run(() =>
        {
            ChunkBuildResult result = BuildChunk(request);
            completedBuilds.Enqueue(result);
        });
    }

    private void BuildChunkImmediate(ChunkKey key, bool wantsCollision, WorldSnapshot snapshot)
    {
        int revision = ++buildRevisionCounter;
        latestRequestedRevisions[key] = revision;

        ChunkBuildRequest request = new(key, wantsCollision, revision, snapshot, GetAtlasRect(VoxelBlockType.Grass), atlasUvRects);
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
    }

    private void ApplyChunkBuildResult(ChunkBuildResult result)
    {
        if (!activeRenderChunks.Contains(result.Key))
        {
            return;
        }

        if (latestRequestedRevisions.TryGetValue(result.Key, out int latestRevision) && latestRevision > result.Revision)
        {
            return;
        }

        PlanetChunk chunk = GetOrCreateChunk(result.Key);
        if (chunk.RequestedRevision > result.Revision)
        {
            return;
        }

        chunk.RequestedRevision = result.Revision;
        chunk.WantsCollision = result.WantsCollision;
        chunk.RaycastTriangles.Clear();

        if (result.Vertices.Length == 0)
        {
            DestroyChunk(result.Key);
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

        if (result.WantsCollision)
        {
            chunk.RaycastTriangles.AddRange(result.RaycastTriangles);
            chunk.CollisionShape.Shape = new ConcavePolygonShape3D
            {
                Data = result.CollisionFaces
            };
            chunkBodiesById[chunk.CollisionBody.GetInstanceId()] = chunk;
        }
        else
        {
            chunk.CollisionShape.Shape = null;
            chunkBodiesById.Remove(chunk.CollisionBody.GetInstanceId());
        }
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
            return ChunkBuildResult.Empty(key, request.Revision, request.WantsCollision);
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
            request.WantsCollision,
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
            request.WantsCollision ? collisionFaces : null,
            request.WantsCollision ? raycastTriangles : null);
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
        List<Vector3>? collisionFaces,
        List<RaycastTriangleInfo>? raycastTriangles)
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

        collisionFaces.Add(c);
        collisionFaces.Add(b);
        collisionFaces.Add(a);
        raycastTriangles.Add(hitInfo);

        collisionFaces.Add(a);
        collisionFaces.Add(c);
        collisionFaces.Add(d);
        raycastTriangles.Add(hitInfo);

        collisionFaces.Add(d);
        collisionFaces.Add(c);
        collisionFaces.Add(a);
        raycastTriangles.Add(hitInfo);
    }

    private void RemoveBlockInternal(PlanetCellId cell)
    {
        if (!HasBlock(cell))
        {
            return;
        }

        bool defaultSolid = IsDefaultSolid(cell);
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
        WrappedColumn column = ProjectDirectionToColumn((worldPosition - PlanetCenter).Normalized(), faceResolution);
        return new ChunkAnchor(column.Face, column.U, column.V);
    }

    private static WrappedColumn ProjectDirectionToColumn(Vector3 direction, int resolution)
    {
        return ProjectCubePointToColumnStatic(direction, resolution);
    }

    private static WrappedColumn WrapColumn(int face, int u, int v, int faceResolution)
    {
        float s = (u + 0.5f) / faceResolution * 2f - 1f;
        float t = (v + 0.5f) / faceResolution * 2f - 1f;
        return ProjectCubePointToColumnStatic(GetCubePoint(face, s, t), faceResolution);
    }

    private WrappedColumn WrapColumn(int face, int u, int v)
    {
        return WrapColumn(face, u, v, faceResolution);
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

    private bool TryGetAdjacentCell(PlanetCellId cell, CellFace face, out PlanetCellId adjacentCell)
    {
        return TryGetAdjacentCellStatic(cell, face, faceResolution, out adjacentCell);
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

        Vector3 direction = GetDirectionForCellCenterStatic(face, u, v, config.FaceResolution, config.FaceCoordinateUniformity, config.PolynomialWarpBias);
        Vector3 rotated = ApplyNoiseRotationStatic(direction, config.NoiseScale, config.DistortionOptimizedRotationEuler);
        float sample = noiseSource.GetNoise3D(rotated.X, rotated.Y, rotated.Z);
        int height = Mathf.Max(1, config.BaseRadiusInBlocks + Mathf.RoundToInt(sample * config.HeightVariationInBlocks));
        cache[key] = height;
        return height;
    }

    private int GetDefaultColumnHeight(int face, int u, int v)
    {
        lock (worldDataLock)
        {
            long key = ((long)face << 42) | ((long)u << 21) | (uint)v;
            if (defaultColumnHeights.TryGetValue(key, out int cached))
            {
                return cached;
            }

            Vector3 direction = GetDirectionForCellCenterStatic(face, u, v, faceResolution, FaceCoordinateUniformity, PolynomialWarpBias);
            Vector3 rotated = ApplyNoiseRotationStatic(direction, NoiseScale, DistortionOptimizedRotationEuler);
            int height = Mathf.Max(1, BaseRadiusInBlocks + Mathf.RoundToInt(noise!.GetNoise3D(rotated.X, rotated.Y, rotated.Z) * HeightVariationInBlocks));
            defaultColumnHeights[key] = height;
            return height;
        }
    }

    private static Vector3 GetDirectionForCellCenterStatic(int face, int u, int v, int resolution, float uniformity, float warpBias)
    {
        float s = GetWarpedFaceCoordinateStatic(u + 0.5f, resolution, uniformity, warpBias);
        float t = GetWarpedFaceCoordinateStatic(v + 0.5f, resolution, uniformity, warpBias);
        return GetAdjustedSphericalDirection(face, s, t);
    }

    private static Vector3[] GetCellCornersStatic(PlanetCellId cell, PlanetGenerationConfig config)
    {
        float s0 = GetWarpedFaceCoordinateStatic(cell.U, config.FaceResolution, config.FaceCoordinateUniformity, config.PolynomialWarpBias);
        float s1 = GetWarpedFaceCoordinateStatic(cell.U + 1, config.FaceResolution, config.FaceCoordinateUniformity, config.PolynomialWarpBias);
        float t0 = GetWarpedFaceCoordinateStatic(cell.V, config.FaceResolution, config.FaceCoordinateUniformity, config.PolynomialWarpBias);
        float t1 = GetWarpedFaceCoordinateStatic(cell.V + 1, config.FaceResolution, config.FaceCoordinateUniformity, config.PolynomialWarpBias);

        Vector3 d00 = GetSharedCornerDirectionStatic(cell.Face, s0, t0, config.LocalCellDeformation);
        Vector3 d10 = GetSharedCornerDirectionStatic(cell.Face, s1, t0, config.LocalCellDeformation);
        Vector3 d11 = GetSharedCornerDirectionStatic(cell.Face, s1, t1, config.LocalCellDeformation);
        Vector3 d01 = GetSharedCornerDirectionStatic(cell.Face, s0, t1, config.LocalCellDeformation);
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
            FaceCoordinateUniformity,
            PolynomialWarpBias,
            DistortionOptimizedRotationEuler,
            LocalCellDeformation,
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

    private static float GetWarpedFaceCoordinateStatic(float gridPosition, int resolution, float uniformity, float warpBias)
    {
        float raw = (gridPosition / resolution) * 2f - 1f;
        float cubicWarp = raw + (raw * raw * raw - raw) * Mathf.Clamp(warpBias, 0f, 1f);
        return Mathf.Clamp(Mathf.Lerp(raw, cubicWarp, Mathf.Clamp(uniformity, 0f, 1f)), -1f, 1f);
    }

    private static Vector3 GetAdjustedSphericalDirection(int face, float s, float t)
    {
        Vector3 cubePoint = GetCubePoint(face, s, t);
        float x = cubePoint.X;
        float y = cubePoint.Y;
        float z = cubePoint.Z;
        float x2 = x * x;
        float y2 = y * y;
        float z2 = z * z;

        Vector3 spherical = new(
            x * Mathf.Sqrt(Mathf.Max(0f, 1f - 0.5f * y2 - 0.5f * z2 + (y2 * z2) / 3f)),
            y * Mathf.Sqrt(Mathf.Max(0f, 1f - 0.5f * z2 - 0.5f * x2 + (z2 * x2) / 3f)),
            z * Mathf.Sqrt(Mathf.Max(0f, 1f - 0.5f * x2 - 0.5f * y2 + (x2 * y2) / 3f)));

        return spherical.Normalized();
    }

    private static Vector3 GetSharedCornerDirectionStatic(int face, float s, float t, float deformation)
    {
        Vector3 ascDirection = GetAdjustedSphericalDirection(face, s, t);
        if (deformation >= 0.999f)
        {
            return ascDirection;
        }

        return GetCubePoint(face, s, t).Normalized().Slerp(ascDirection, Mathf.Clamp(deformation, 0f, 1f)).Normalized();
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

    private Rect2 GetAtlasRect(VoxelBlockType blockType)
    {
        return GetAtlasRectStatic(blockType, atlasUvRects);
    }

    private bool IsDefaultSolid(PlanetCellId cell)
    {
        return cell.Radius < GetDefaultColumnHeight(cell.Face, cell.U, cell.V);
    }

    private VoxelBlockType GetDefaultBlockType(PlanetCellId cell)
    {
        int surfaceRadius = GetDefaultColumnHeight(cell.Face, cell.U, cell.V) - 1;
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
        float FaceCoordinateUniformity,
        float PolynomialWarpBias,
        Vector3 DistortionOptimizedRotationEuler,
        float LocalCellDeformation,
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
        bool WantsCollision,
        int Revision,
        WorldSnapshot Snapshot,
        Rect2 DefaultAtlasRect,
        IReadOnlyDictionary<VoxelBlockType, Rect2> AtlasRects);

    private readonly record struct ChunkBuildResult(
        ChunkKey Key,
        int Revision,
        bool WantsCollision,
        Vector3[] Vertices,
        Vector3[] Normals,
        Vector2[] Uvs,
        Color[] Colors,
        int[] Indices,
        Vector3[] CollisionFaces,
        RaycastTriangleInfo[] RaycastTriangles)
    {
        public static ChunkBuildResult Empty(ChunkKey key, int revision, bool wantsCollision)
        {
            return new ChunkBuildResult(key, revision, wantsCollision, [], [], [], [], [], [], []);
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
        public bool WantsCollision { get; set; }
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
