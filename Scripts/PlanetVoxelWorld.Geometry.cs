using System;
using System.Collections.Generic;
using Godot;

namespace SphereMinecraft;

public partial class PlanetVoxelWorld
{
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
				switch (face)
				{
					case CellFace.PositiveU:
						if (cell.U < faceResolution - 1)
						{
							adjacentCell = new PlanetCellId(cell.Face, cell.Radius, cell.U + 1, cell.V);
							return true;
						}

						break;
					case CellFace.NegativeU:
						if (cell.U > 0)
						{
							adjacentCell = new PlanetCellId(cell.Face, cell.Radius, cell.U - 1, cell.V);
							return true;
						}

						break;
					case CellFace.PositiveV:
						if (cell.V < faceResolution - 1)
						{
							adjacentCell = new PlanetCellId(cell.Face, cell.Radius, cell.U, cell.V + 1);
							return true;
						}

						break;
					case CellFace.NegativeV:
						if (cell.V > 0)
						{
							adjacentCell = new PlanetCellId(cell.Face, cell.Radius, cell.U, cell.V - 1);
							return true;
						}

						break;
				}

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

	/// <summary>
	/// Double-precision normalize so shared edges match across chunks and cube-face seams (avoids single-precision cracks).
	/// </summary>
	private static Vector3 NormalizedCubeDirection(int face, float s, float t)
	{
		double x = FaceNormals[face].X + FaceAxisU[face].X * s + FaceAxisV[face].X * t;
		double y = FaceNormals[face].Y + FaceAxisU[face].Y * s + FaceAxisV[face].Y * t;
		double z = FaceNormals[face].Z + FaceAxisU[face].Z * s + FaceAxisV[face].Z * t;
		double len = Math.Sqrt(x * x + y * y + z * z);
		if (len < 1e-15)
		{
			return Vector3.Zero;
		}

		float inv = (float)(1.0 / len);
		return new Vector3((float)x * inv, (float)y * inv, (float)z * inv);
	}

	private static Vector3[] GetCellCornersStatic(PlanetCellId cell, PlanetGenerationConfig config)
	{
		float s0 = GetRawFaceCoordinateStatic(cell.U, config.FaceResolution);
		float s1 = GetRawFaceCoordinateStatic(cell.U + 1, config.FaceResolution);
		float t0 = GetRawFaceCoordinateStatic(cell.V, config.FaceResolution);
		float t1 = GetRawFaceCoordinateStatic(cell.V + 1, config.FaceResolution);

		Vector3 d00 = NormalizedCubeDirection(cell.Face, s0, t0);
		Vector3 d10 = NormalizedCubeDirection(cell.Face, s1, t0);
		Vector3 d11 = NormalizedCubeDirection(cell.Face, s1, t1);
		Vector3 d01 = NormalizedCubeDirection(cell.Face, s0, t1);
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
}
