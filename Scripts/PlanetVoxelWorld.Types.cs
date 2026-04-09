using System.Collections.Generic;
using Godot;

namespace SphereMinecraft;

public partial class PlanetVoxelWorld
{
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
		RaycastTriangleInfo[] RaycastTriangles)
	{
		public static ChunkBuildResult Empty(ChunkKey key, int revision)
		{
			return new ChunkBuildResult(key, revision, [], [], [], [], [], []);
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
