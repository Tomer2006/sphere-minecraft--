using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace SphereMinecraft;

public partial class PlanetVoxelWorld
{
	private const int MaxCompletedBuildsPerFrame = 12;
	private const int MaxQueuedChunkDispatchesPerFrame = 48;
	private const int MaxConcurrentChunkBuilds = 32;

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
			if (activeRenderChunks.Contains(key))
			{
				BuildChunkImmediate(key, snapshot);
			}
			else
			{
				RequestChunkBuild(key, snapshot);
			}
		}
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
		lastStreamingPlayerRadiusChunk = int.MinValue;

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

	private bool IsChunkReady(ChunkKey key)
	{
		return chunks.TryGetValue(key, out PlanetChunk? chunk) &&
			chunk.MeshInstance.Mesh != null &&
			chunk.CollisionShape.Shape != null;
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
		List<ChunkBuildResult> batch = [];
		while (completedBuilds.TryDequeue(out ChunkBuildResult result))
		{
			batch.Add(result);
		}

		if (batch.Count == 0)
		{
			return;
		}

		batch.Sort((a, b) => CompareChunkBuildPriority(a.Key, b.Key));

		int toApply = Math.Min(MaxCompletedBuildsPerFrame, batch.Count);
		for (int i = 0; i < toApply; i++)
		{
			ApplyChunkBuildResult(batch[i]);
		}

		for (int i = toApply; i < batch.Count; i++)
		{
			completedBuilds.Enqueue(batch[i]);
		}

		if (toApply > 0)
		{
			RuntimeLog.Info(RuntimeLogChannel.Chunk,
				$"Applied {toApply} completed chunk build(s) this frame (near-player first). RemainingQueued={completedBuilds.Count}");
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

		Vector3[] collisionPacked = PackCollisionTriangleVerticesFromIndexedMesh(result.Vertices, result.Indices);
		int triCountFromIndices = result.Indices.Length / 3;
		if (result.RaycastTriangles.Length != triCountFromIndices)
		{
			RuntimeLog.Warning(RuntimeLogChannel.Chunk,
				$"Chunk {FormatChunkKey(result.Key)}: raycast tri count {result.RaycastTriangles.Length} != mesh tri count {triCountFromIndices}; collision disabled for this chunk.");
			chunk.CollisionShape.Shape = null;
		}
		else if (TryBuildConcaveCollisionShape(
			         collisionPacked,
			         result.RaycastTriangles,
			         out ConcavePolygonShape3D? concaveShape,
			         out RaycastTriangleInfo[] filteredRaycast))
		{
			chunk.RaycastTriangles.AddRange(filteredRaycast);
			chunk.CollisionShape.Shape = concaveShape;
		}
		else
		{
			chunk.CollisionShape.Shape = null;
			if (collisionPacked.Length > 0)
			{
				RuntimeLog.Warning(RuntimeLogChannel.Chunk,
					$"Chunk {FormatChunkKey(result.Key)}: collision skipped (degenerate / invalid tris). CollisionVertsIn={collisionPacked.Length}, RaycastIn={result.RaycastTriangles.Length}");
			}
		}

		chunkBodiesById[chunk.CollisionBody.GetInstanceId()] = chunk;
		MarkInitialChunkLoadComplete(result.Key);

		RuntimeLog.Info(RuntimeLogChannel.Chunk,
			$"Applied chunk build {FormatChunkKey(result.Key)}. Revision={result.Revision}, Vertices={result.Vertices.Length}, Indices={result.Indices.Length}, CollisionTris={chunk.RaycastTriangles.Count}, CollisionEnabled={chunk.CollisionShape.Shape != null}");
	}

	/// <summary>
	/// Expand indexed render mesh triangles into a flat vertex triple list for <see cref="ConcavePolygonShape3D"/>.
	/// Built on the main thread so worker threads skip maintaining a duplicate collision buffer.
	/// </summary>
	private static Vector3[] PackCollisionTriangleVerticesFromIndexedMesh(Vector3[] vertices, int[] indices)
	{
		if (indices.Length < 3 || indices.Length % 3 != 0)
		{
			return [];
		}

		Vector3[] packed = new Vector3[indices.Length];
		for (int i = 0; i < indices.Length; i++)
		{
			int vi = indices[i];
			if ((uint)vi >= (uint)vertices.Length)
			{
				return [];
			}

			packed[i] = vertices[vi];
		}

		return packed;
	}

	/// <summary>
	/// Jolt removes degenerate triangles when building mesh shapes; if none remain, shape creation fails.
	/// Drop zero-area collision triangles (e.g. inward faces at radius 0) and keep raycast data aligned.
	/// </summary>
	private const float MinCollisionTriangleCrossLengthSq = 1e-14f;

	private static bool IsFiniteCollisionVector(Vector3 v) =>
		float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

	private static bool TryBuildConcaveCollisionShape(
		Vector3[] collisionFaces,
		RaycastTriangleInfo[] raycastInfos,
		out ConcavePolygonShape3D? shape,
		out RaycastTriangleInfo[] filteredRaycast)
	{
		shape = null;
		filteredRaycast = [];
		int triCount = collisionFaces.Length / 3;
		if (collisionFaces.Length == 0 || triCount * 3 != collisionFaces.Length || raycastInfos.Length != triCount)
		{
			return false;
		}

		List<Vector3> packed = new(triCount * 3);
		List<RaycastTriangleInfo> rays = new(triCount);
		for (int t = 0; t < triCount; t++)
		{
			Vector3 v0 = collisionFaces[t * 3];
			Vector3 v1 = collisionFaces[t * 3 + 1];
			Vector3 v2 = collisionFaces[t * 3 + 2];
			if (!IsFiniteCollisionVector(v0) || !IsFiniteCollisionVector(v1) || !IsFiniteCollisionVector(v2))
			{
				continue;
			}

			float crossSq = (v1 - v0).Cross(v2 - v0).LengthSquared();
			if (crossSq < MinCollisionTriangleCrossLengthSq)
			{
				continue;
			}

			packed.Add(v0);
			packed.Add(v1);
			packed.Add(v2);
			rays.Add(raycastInfos[t]);
		}

		if (packed.Count < 3)
		{
			return false;
		}

		filteredRaycast = rays.ToArray();
		shape = new ConcavePolygonShape3D
		{
			BackfaceCollision = true,
			Data = packed.ToArray()
		};
		return true;
	}

	private void MarkInitialChunkLoadComplete(ChunkKey key)
	{
		if (!isInitialChunkLoadActive)
		{
			return;
		}

		if (!pendingInitialLoadChunks.Remove(key))
		{
			return;
		}

		if (pendingInitialLoadChunks.Count == 0)
		{
			isInitialChunkLoadActive = false;
			RuntimeLog.Info(RuntimeLogChannel.World,
				$"Initial planet chunk load complete. TotalChunks={initialLoadTargetChunkCount}");
		}
	}

	private void ResetInitialChunkLoadState()
	{
		pendingInitialLoadChunks.Clear();
		initialLoadTargetChunkCount = 0;
		isInitialChunkLoadActive = true;
	}
}
