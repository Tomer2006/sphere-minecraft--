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
		int uvBfsRadius = Mathf.Clamp(ActiveRenderChunkLoadRadius, 1, 32);
		int radialExtentChunks = Mathf.Clamp(ActiveRenderRadiusChunkLoadRadius, 1, 64);
		int maxRadiusChunk = GetMaxStreamingRadiusChunk();
		HashSet<ChunkKey> nextRenderChunks = CollectActiveCubicChunks(
			anchor,
			chunkSize,
			uvBfsRadius,
			lastStreamingPlayerRadiusChunk,
			radialExtentChunks,
			maxRadiusChunk);
		ApplyChunkSet(nextRenderChunks, buildImmediately, FormatAnchor(anchor));
	}

	private HashSet<ChunkKey> CollectActiveCubicChunks(
		ChunkAnchor anchor,
		int chunkSize,
		int uvBfsRadius,
		int playerRadiusChunk,
		int radialExtentChunks,
		int maxRadiusChunk)
	{
		int pc = playerRadiusChunk == int.MinValue ? 0 : Mathf.Clamp(playerRadiusChunk, 0, maxRadiusChunk);
		int r0 = Mathf.Max(0, pc - radialExtentChunks);
		int r1 = Mathf.Min(maxRadiusChunk, pc + radialExtentChunks);

		(int Face, int UChunk, int VChunk) start = (anchor.Face, anchor.U / chunkSize, anchor.V / chunkSize);
		HashSet<(int Face, int UChunk, int VChunk)> visited = [start];
		Queue<((int Face, int UChunk, int VChunk) key, int distance)> frontier = new();
		frontier.Enqueue((start, 0));

		while (frontier.Count > 0)
		{
			((int Face, int UChunk, int VChunk) key, int distance) = frontier.Dequeue();
			if (distance >= uvBfsRadius)
			{
				continue;
			}

			foreach ((int Face, int UChunk, int VChunk) neighbor in GetAdjacentUvChunks(key, chunkSize))
			{
				if (!visited.Add(neighbor))
				{
					continue;
				}

				frontier.Enqueue((neighbor, distance + 1));
			}
		}

		HashSet<ChunkKey> keys = [];
		foreach ((int face, int uChunk, int vChunk) in visited)
		{
			for (int radiusChunk = r0; radiusChunk <= r1; radiusChunk++)
			{
				keys.Add(new ChunkKey(face, radiusChunk, uChunk, vChunk));
			}
		}

		return keys;
	}

	private HashSet<(int Face, int UChunk, int VChunk)> GetAdjacentUvChunks(
		(int Face, int UChunk, int VChunk) key,
		int chunkSize)
	{
		HashSet<(int Face, int UChunk, int VChunk)> neighbors = [];
		AddAdjacentUvChunkForDirection(key, CellFace.NegativeU, chunkSize, neighbors);
		AddAdjacentUvChunkForDirection(key, CellFace.PositiveU, chunkSize, neighbors);
		AddAdjacentUvChunkForDirection(key, CellFace.NegativeV, chunkSize, neighbors);
		AddAdjacentUvChunkForDirection(key, CellFace.PositiveV, chunkSize, neighbors);
		return neighbors;
	}

	private void AddAdjacentUvChunkForDirection(
		(int Face, int UChunk, int VChunk) key,
		CellFace direction,
		int chunkSize,
		HashSet<(int Face, int UChunk, int VChunk)> results)
	{
		int faceChunkCount = Mathf.CeilToInt(faceResolution / (float)chunkSize);
		switch (direction)
		{
			case CellFace.NegativeU:
				if (key.UChunk > 0)
				{
					results.Add((key.Face, key.UChunk - 1, key.VChunk));
					return;
				}

				break;
			case CellFace.PositiveU:
				if (key.UChunk < faceChunkCount - 1)
				{
					results.Add((key.Face, key.UChunk + 1, key.VChunk));
					return;
				}

				break;
			case CellFace.NegativeV:
				if (key.VChunk > 0)
				{
					results.Add((key.Face, key.UChunk, key.VChunk - 1));
					return;
				}

				break;
			case CellFace.PositiveV:
				if (key.VChunk < faceChunkCount - 1)
				{
					results.Add((key.Face, key.UChunk, key.VChunk + 1));
					return;
				}

				break;
			default:
				return;
		}

		foreach (PlanetCellId edgeCell in EnumerateUvChunkEdgeCells(key, direction, chunkSize))
		{
			if (!TryGetAdjacentCellStatic(edgeCell, direction, faceResolution, out PlanetCellId adjacentCell))
			{
				continue;
			}

			results.Add((
				adjacentCell.Face,
				adjacentCell.U / chunkSize,
				adjacentCell.V / chunkSize));
		}
	}

	private IEnumerable<PlanetCellId> EnumerateUvChunkEdgeCells(
		(int Face, int UChunk, int VChunk) key,
		CellFace direction,
		int chunkSize)
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

	private void ApplyChunkSet(HashSet<ChunkKey> nextRenderChunks, bool buildImmediately, string scopeLabel)
	{
		WorldSnapshot snapshot = CreateWorldSnapshot();
		List<ChunkKey> orderedChunkKeys = GetChunkBuildOrder(nextRenderChunks);
		int staleChunkCount = 0;
		int addedChunkCount = 0;

		List<ChunkKey> staleActiveChunks = [];
		foreach (ChunkKey staleKey in activeRenderChunks)
		{
			if (nextRenderChunks.Contains(staleKey))
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
		// null unless we resolve here. Without a player, distance ties at 0 and CompareChunkBuildPriority
		// tie-breakers (radius, face, u, v) order work deterministically.
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

		return 0f;
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

	/// <summary>
	/// Upper bound for radial chunk indices when collecting active chunks. Uses <see cref="int.MaxValue"/> so
	/// outward building is not limited to the procedural terrain shell; streaming depth is still bounded by
	/// the player's radius chunk plus <see cref="ActiveRenderRadiusChunkLoadRadius"/>.
	/// </summary>
	private static int GetMaxStreamingRadiusChunk() => int.MaxValue;
}
