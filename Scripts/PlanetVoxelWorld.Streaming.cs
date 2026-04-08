using System.Collections.Generic;
using Godot;

namespace SphereMinecraft;

public partial class PlanetVoxelWorld
{
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
		Vector3 playerOffset = trackedPlayer.GlobalPosition - PlanetCenter;
		float playerDist = playerOffset.Length();
		int playerRadiusChunk = int.MinValue;
		if (playerDist > 0.0001f)
		{
			int playerRadiusBlocks = Mathf.FloorToInt(playerDist / Mathf.Max(0.0001f, BlockSize));
			playerRadiusChunk = playerRadiusBlocks / Mathf.Max(8, ChunkSizeInCells);
		}

		if (!force &&
			currentAnchor.HasValue &&
			currentAnchor.Value.Equals(nextAnchor) &&
			playerRadiusChunk == lastStreamingPlayerRadiusChunk)
		{
			return;
		}

		currentAnchor = nextAnchor;
		lastStreamingPlayerRadiusChunk = playerRadiusChunk;
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

		ApplyChunkSet(nextRenderChunks, buildImmediately, "all-planet");
	}

	private void SyncActiveChunks(ChunkAnchor anchor, bool buildImmediately = false)
	{
		HashSet<ChunkKey> nextRenderChunks = [];
		int chunkSize = Mathf.Max(8, ChunkSizeInCells);
		HashSet<ChunkColumnKey> activeSurfaceChunks = CollectActiveChunkColumns(anchor, chunkSize);

		foreach (ChunkColumnKey surfaceChunk in activeSurfaceChunks)
		{
			int maxRadiusChunk = GetMaxSurfaceRadiusChunkForColumn(surfaceChunk, chunkSize);

			for (int radiusChunk = 0; radiusChunk <= maxRadiusChunk; radiusChunk++)
			{
				ChunkKey key = new(surfaceChunk.Face, radiusChunk, surfaceChunk.UChunk, surfaceChunk.VChunk);
				nextRenderChunks.Add(key);
			}
		}

		ApplyChunkSet(nextRenderChunks, buildImmediately, FormatAnchor(anchor));
	}

	private HashSet<ChunkColumnKey> CollectActiveChunkColumns(ChunkAnchor anchor, int chunkSize)
	{
		ChunkColumnKey start = new(anchor.Face, anchor.U / chunkSize, anchor.V / chunkSize);
		HashSet<ChunkColumnKey> visited = [start];
		Queue<ChunkColumnSearchState> frontier = new();
		frontier.Enqueue(new ChunkColumnSearchState(start, 0));

		while (frontier.Count > 0)
		{
			ChunkColumnSearchState current = frontier.Dequeue();
			if (current.Distance >= ActiveRenderChunkRadius)
			{
				continue;
			}

			HashSet<ChunkColumnKey> neighbors = GetAdjacentChunkColumns(current.Key, chunkSize);
			foreach (ChunkColumnKey neighbor in neighbors)
			{
				if (!visited.Add(neighbor))
				{
					continue;
				}

				frontier.Enqueue(new ChunkColumnSearchState(neighbor, current.Distance + 1));
			}
		}

		return visited;
	}

	private HashSet<ChunkColumnKey> GetAdjacentChunkColumns(ChunkColumnKey key, int chunkSize)
	{
		HashSet<ChunkColumnKey> neighbors = [];
		AddAdjacentChunkColumnsForDirection(key, CellFace.NegativeU, chunkSize, neighbors);
		AddAdjacentChunkColumnsForDirection(key, CellFace.PositiveU, chunkSize, neighbors);
		AddAdjacentChunkColumnsForDirection(key, CellFace.NegativeV, chunkSize, neighbors);
		AddAdjacentChunkColumnsForDirection(key, CellFace.PositiveV, chunkSize, neighbors);
		return neighbors;
	}

	private void AddAdjacentChunkColumnsForDirection(
		ChunkColumnKey key,
		CellFace direction,
		int chunkSize,
		HashSet<ChunkColumnKey> results)
	{
		int faceChunkCount = Mathf.CeilToInt(faceResolution / (float)chunkSize);
		switch (direction)
		{
			case CellFace.NegativeU:
				if (key.UChunk > 0)
				{
					results.Add(new ChunkColumnKey(key.Face, key.UChunk - 1, key.VChunk));
					return;
				}

				break;
			case CellFace.PositiveU:
				if (key.UChunk < faceChunkCount - 1)
				{
					results.Add(new ChunkColumnKey(key.Face, key.UChunk + 1, key.VChunk));
					return;
				}

				break;
			case CellFace.NegativeV:
				if (key.VChunk > 0)
				{
					results.Add(new ChunkColumnKey(key.Face, key.UChunk, key.VChunk - 1));
					return;
				}

				break;
			case CellFace.PositiveV:
				if (key.VChunk < faceChunkCount - 1)
				{
					results.Add(new ChunkColumnKey(key.Face, key.UChunk, key.VChunk + 1));
					return;
				}

				break;
			default:
				return;
		}

		foreach (PlanetCellId edgeCell in EnumerateChunkEdgeCells(key, direction, chunkSize))
		{
			if (!TryGetAdjacentCellStatic(edgeCell, direction, faceResolution, out PlanetCellId adjacentCell))
			{
				continue;
			}

			results.Add(new ChunkColumnKey(
				adjacentCell.Face,
				adjacentCell.U / chunkSize,
				adjacentCell.V / chunkSize));
		}
	}

	private IEnumerable<PlanetCellId> EnumerateChunkEdgeCells(ChunkColumnKey key, CellFace direction, int chunkSize)
	{
		int uStart = key.UChunk * chunkSize;
		int uEnd = Mathf.Min(faceResolution, uStart + chunkSize);
		int vStart = key.VChunk * chunkSize;
		int vEnd = Mathf.Min(faceResolution, vStart + chunkSize);

		switch (direction)
		{
			case CellFace.NegativeU:
			{
				int u = uStart;
				for (int v = vStart; v < vEnd; v++)
				{
					yield return new PlanetCellId(key.Face, 0, u, v);
				}

				yield break;
			}
			case CellFace.PositiveU:
			{
				int u = uEnd - 1;
				for (int v = vStart; v < vEnd; v++)
				{
					yield return new PlanetCellId(key.Face, 0, u, v);
				}

				yield break;
			}
			case CellFace.NegativeV:
			{
				int v = vStart;
				for (int u = uStart; u < uEnd; u++)
				{
					yield return new PlanetCellId(key.Face, 0, u, v);
				}

				yield break;
			}
			case CellFace.PositiveV:
			{
				int v = vEnd - 1;
				for (int u = uStart; u < uEnd; u++)
				{
					yield return new PlanetCellId(key.Face, 0, u, v);
				}

				yield break;
			}
		}
	}

	private int GetMaxSurfaceRadiusChunkForColumn(ChunkColumnKey key, int chunkSize)
	{
		int uStart = key.UChunk * chunkSize;
		int uEnd = Mathf.Min(faceResolution, uStart + chunkSize);
		int vStart = key.VChunk * chunkSize;
		int vEnd = Mathf.Min(faceResolution, vStart + chunkSize);
		int maxSurfaceHeight = 1;

		lock (worldDataLock)
		{
			for (int u = uStart; u < uEnd; u++)
			{
				for (int v = vStart; v < vEnd; v++)
				{
					maxSurfaceHeight = Mathf.Max(maxSurfaceHeight, GetDefaultColumnHeightUnlocked(key.Face, u, v));
				}
			}
		}

		int maxRadiusExclusive = maxSurfaceHeight + ExtraOutwardBlocks;
		return Mathf.Max(0, Mathf.Max(0, maxRadiusExclusive - 1) / chunkSize);
	}

	private void ApplyChunkSet(
		HashSet<ChunkKey> nextRenderChunks,
		bool buildImmediately,
		string scopeLabel)
	{
		WorldSnapshot snapshot = CreateWorldSnapshot();
		List<ChunkKey> orderedChunkKeys = GetChunkBuildOrder(nextRenderChunks);
		int staleChunkCount = 0;
		int addedChunkCount = 0;

		List<ChunkKey> staleActiveChunks = [];
		foreach (ChunkKey staleKey in activeRenderChunks)
		{
			if (!nextRenderChunks.Contains(staleKey))
			{
				staleActiveChunks.Add(staleKey);
			}
		}

		foreach (ChunkKey staleKey in staleActiveChunks)
		{
			staleChunkCount++;
			DestroyChunk(staleKey);
			activeRenderChunks.Remove(staleKey);
		}

		List<ChunkKey> orphanedLoadedChunks = [];
		foreach (ChunkKey loadedKey in chunks.Keys)
		{
			if (!nextRenderChunks.Contains(loadedKey))
			{
				orphanedLoadedChunks.Add(loadedKey);
			}
		}

		foreach (ChunkKey orphanedKey in orphanedLoadedChunks)
		{
			if (activeRenderChunks.Contains(orphanedKey))
			{
				continue;
			}

			staleChunkCount++;
			DestroyChunk(orphanedKey);
		}

		HashSet<ChunkKey> queuedChunkKeys = buildImmediately
			? []
			: PruneQueuedChunkBuilds(nextRenderChunks);

		BeginInitialChunkLoad(nextRenderChunks);

		foreach (ChunkKey key in orderedChunkKeys)
		{
			bool wasActive = activeRenderChunks.Contains(key);
			activeRenderChunks.Add(key);
			if (IsChunkReady(key))
			{
				continue;
			}

			if (buildImmediately)
			{
				addedChunkCount++;
				BuildChunkImmediate(key, snapshot);
			}
			else
			{
				if (queuedChunkKeys.Contains(key))
				{
					continue;
				}

				addedChunkCount++;
				RequestChunkBuild(key, snapshot);
			}
		}

		RuntimeLog.Info(RuntimeLogChannel.Chunk,
			$"Chunk sync complete for {scopeLabel}. Chunks={nextRenderChunks.Count}, QueuedOrBuilt={addedChunkCount}, Destroyed={staleChunkCount}, BuildImmediately={buildImmediately}, AlwaysLoadWholePlanet={AlwaysLoadWholePlanet}");
	}

	private HashSet<ChunkKey> PruneQueuedChunkBuilds(HashSet<ChunkKey> activeChunkKeys)
	{
		HashSet<ChunkKey> keptKeys = [];
		if (queuedChunkBuilds.Count == 0)
		{
			return keptKeys;
		}

		Queue<ChunkBuildRequest> filteredQueue = new();
		while (queuedChunkBuilds.Count > 0)
		{
			ChunkBuildRequest request = queuedChunkBuilds.Dequeue();
			if (!activeChunkKeys.Contains(request.Key))
			{
				continue;
			}

			if (latestRequestedRevisions.TryGetValue(request.Key, out int latestRevision) && latestRevision != request.Revision)
			{
				continue;
			}

			if (!keptKeys.Add(request.Key))
			{
				continue;
			}

			filteredQueue.Enqueue(request);
		}

		while (filteredQueue.Count > 0)
		{
			queuedChunkBuilds.Enqueue(filteredQueue.Dequeue());
		}

		return keptKeys;
	}

	private List<ChunkKey> GetChunkBuildOrder(HashSet<ChunkKey> chunkKeys)
	{
		List<ChunkKey> ordered = new(chunkKeys);
		ordered.Sort(CompareChunkBuildPriority);
		return ordered;
	}

	private int CompareChunkBuildPriority(ChunkKey a, ChunkKey b)
	{
		float distanceA = GetChunkPriorityDistanceSquared(a);
		float distanceB = GetChunkPriorityDistanceSquared(b);
		int distanceCompare = distanceA.CompareTo(distanceB);
		if (distanceCompare != 0)
		{
			return distanceCompare;
		}

		int radiusCompare = GetApproximateChunkRadiusDistance(a).CompareTo(GetApproximateChunkRadiusDistance(b));
		if (radiusCompare != 0)
		{
			return radiusCompare;
		}

		int faceCompare = a.Face.CompareTo(b.Face);
		if (faceCompare != 0)
		{
			return faceCompare;
		}

		int uCompare = a.UChunk.CompareTo(b.UChunk);
		if (uCompare != 0)
		{
			return uCompare;
		}

		int vCompare = a.VChunk.CompareTo(b.VChunk);
		if (vCompare != 0)
		{
			return vCompare;
		}

		return b.RadiusChunk.CompareTo(a.RadiusChunk);
	}

	private float GetChunkPriorityDistanceSquared(ChunkKey key)
	{
		if (trackedPlayer == null)
		{
			return 0f;
		}

		int chunkSize = Mathf.Max(8, ChunkSizeInCells);
		int uStart = key.UChunk * chunkSize;
		int vStart = key.VChunk * chunkSize;
		int uSpan = Mathf.Min(chunkSize, faceResolution - uStart);
		int vSpan = Mathf.Min(chunkSize, faceResolution - vStart);
		float uCenter = uStart + uSpan * 0.5f;
		float vCenter = vStart + vSpan * 0.5f;
		float radiusCenter = key.RadiusChunk * chunkSize + chunkSize * 0.5f;
		Vector3 chunkCenter = NormalizedCubeDirection(
			key.Face,
			GetRawFaceCoordinateStatic(uCenter, faceResolution),
			GetRawFaceCoordinateStatic(vCenter, faceResolution)) * (radiusCenter * BlockSize);
		Vector3 playerLocal = trackedPlayer.GlobalPosition - PlanetCenter;
		return playerLocal.DistanceSquaredTo(chunkCenter);
	}

	private int GetApproximateChunkColumnDistance(ChunkKey key)
	{
		if (!currentAnchor.HasValue)
		{
			return 0;
		}

		int chunkSize = Mathf.Max(8, ChunkSizeInCells);
		ChunkAnchor anchor = currentAnchor.Value;
		int anchorUChunk = anchor.U / chunkSize;
		int anchorVChunk = anchor.V / chunkSize;
		int facePenalty = key.Face == anchor.Face ? 0 : ActiveRenderChunkRadius + 1;
		return facePenalty + Mathf.Abs(key.UChunk - anchorUChunk) + Mathf.Abs(key.VChunk - anchorVChunk);
	}

	private int GetApproximateChunkRadiusDistance(ChunkKey key)
	{
		if (lastStreamingPlayerRadiusChunk == int.MinValue)
		{
			return 0;
		}

		return Mathf.Abs(key.RadiusChunk - lastStreamingPlayerRadiusChunk);
	}

	private void BeginInitialChunkLoad(HashSet<ChunkKey> chunkKeys)
	{
		#region agent log
		AgentDebugLog.Write("C", "PlanetVoxelWorld.Streaming:BeginInitialChunkLoad", "enter",
			new { isInitialChunkLoadActive, chunkKeyCount = chunkKeys.Count });
		#endregion
		if (!isInitialChunkLoadActive)
		{
			#region agent log
			AgentDebugLog.Write("C", "PlanetVoxelWorld.Streaming:BeginInitialChunkLoad", "skipped inactive",
				new { isInitialChunkLoadActive });
			#endregion
			return;
		}

		pendingInitialLoadChunks.Clear();

		foreach (ChunkKey key in chunkKeys)
		{
			if (!IsChunkReady(key))
			{
				pendingInitialLoadChunks.Add(key);
			}
		}

		initialLoadTargetChunkCount = pendingInitialLoadChunks.Count;
		if (initialLoadTargetChunkCount == 0)
		{
			isInitialChunkLoadActive = false;
		}

		#region agent log
		AgentDebugLog.Write("C", "PlanetVoxelWorld.Streaming:BeginInitialChunkLoad", "exit",
			new
			{
				initialLoadTargetChunkCount,
				pendingCount = pendingInitialLoadChunks.Count,
				isInitialChunkLoadActive
			});
		#endregion
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
}
