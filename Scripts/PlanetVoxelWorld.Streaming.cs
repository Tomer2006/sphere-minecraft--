using System.Collections.Generic;
using Godot;

namespace SphereMinecraft;

public partial class PlanetVoxelWorld
{
	private void UpdateStreaming(bool force = false, bool buildImmediately = false)
	{
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

	private void SyncActiveChunks(ChunkAnchor anchor, bool buildImmediately = false)
	{
		int chunkSize = Mathf.Max(8, ChunkSizeInCells);
		int innerR = Mathf.Clamp(ActiveRenderChunkUnloadRadius, 1, 32);
		int outerR = Mathf.Clamp(ActiveRenderChunkLoadRadius, 1, 32);
		if (outerR < innerR)
		{
			(innerR, outerR) = (outerR, innerR);
		}

		HashSet<ChunkColumnKey> retentionColumns = CollectActiveChunkColumns(anchor, chunkSize, outerR);
		HashSet<ChunkColumnKey> coreColumns = CollectActiveChunkColumns(anchor, chunkSize, innerR);
		HashSet<ChunkKey> nextRenderChunks = ExpandColumnsToChunkKeys(retentionColumns, chunkSize);
		HashSet<ChunkKey> coreChunkKeys = ExpandColumnsToChunkKeys(coreColumns, chunkSize);
		ApplyChunkSet(nextRenderChunks, coreChunkKeys, retentionColumns, buildImmediately, FormatAnchor(anchor));
	}

	private HashSet<ChunkKey> ExpandColumnsToChunkKeys(HashSet<ChunkColumnKey> columns, int chunkSize)
	{
		HashSet<ChunkKey> keys = [];
		foreach (ChunkColumnKey surfaceChunk in columns)
		{
			int maxRadiusChunk = GetMaxSurfaceRadiusChunkForColumn(surfaceChunk, chunkSize);
			for (int radiusChunk = 0; radiusChunk <= maxRadiusChunk; radiusChunk++)
			{
				keys.Add(new ChunkKey(surfaceChunk.Face, radiusChunk, surfaceChunk.UChunk, surfaceChunk.VChunk));
			}
		}

		return keys;
	}

	private HashSet<ChunkColumnKey> CollectActiveChunkColumns(ChunkAnchor anchor, int chunkSize, int bfsRadius)
	{
		ChunkColumnKey start = new(anchor.Face, anchor.U / chunkSize, anchor.V / chunkSize);
		HashSet<ChunkColumnKey> visited = [start];
		Queue<ChunkColumnSearchState> frontier = new();
		frontier.Enqueue(new ChunkColumnSearchState(start, 0));

		while (frontier.Count > 0)
		{
			ChunkColumnSearchState current = frontier.Dequeue();
			if (current.Distance >= bfsRadius)
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

	private int GetMaxSurfaceRadiusChunkForColumn(ChunkColumnKey _, int chunkSize)
	{
		// Per-column (u,v) sampling called GetDefaultColumnHeightUnlocked for every surface cell in the
		// footprint while holding worldDataLock — tens of thousands of noise lookups on large streams.
		// The planet-wide bound matches the same formula used for full-planet sync and is safe here
		// because the old loop only consulted default heights (not player-placed towers).
		return GetMaxPlanetRadiusChunk(chunkSize);
	}

	private void ApplyChunkSet(
		HashSet<ChunkKey> nextRenderChunks,
		HashSet<ChunkKey> coreChunkKeys,
		HashSet<ChunkColumnKey> retentionColumns,
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
			if (coreChunkKeys.Contains(staleKey))
			{
				continue;
			}

			ChunkColumnKey col = new(staleKey.Face, staleKey.UChunk, staleKey.VChunk);
			if (retentionColumns.Contains(col))
			{
				continue;
			}

			staleActiveChunks.Add(staleKey);
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
			$"Chunk sync complete for {scopeLabel}. Chunks={nextRenderChunks.Count}, QueuedOrBuilt={addedChunkCount}, Destroyed={staleChunkCount}, BuildImmediately={buildImmediately}");
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
		// ApplyCompletedBuilds runs before UpdateStreaming each frame, so trackedPlayer is often still
		// null unless we resolve here. Without a real distance every chunk ties at 0 and order is random.
		trackedPlayer ??= ResolvePlayer();
		if (trackedPlayer != null)
		{
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

		if (currentAnchor.HasValue)
		{
			int columnSteps = GetApproximateChunkColumnDistance(key);
			return (float)(columnSteps * columnSteps);
		}

		return 0f;
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
		int facePenalty = key.Face == anchor.Face ? 0 : ActiveRenderChunkLoadRadius + 1;
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
}
