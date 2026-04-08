using System.Collections.Generic;
using Godot;

namespace SphereMinecraft;

public partial class PlanetVoxelWorld
{
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
		indices.Add(start + 2);
		indices.Add(start + 1);
		indices.Add(start + 0);
		indices.Add(start + 3);
		indices.Add(start + 2);

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
}
