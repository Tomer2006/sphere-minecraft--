using System;
using System.Collections.Generic;
using Godot;

namespace SphereMinecraft;

public partial class PlanetVoxelWorld : Node3D
{
        private const float SurfaceContinuationTolerance = 0.8f;
        private static readonly Vector3I PositiveZ = new(0, 0, 1);
        private static readonly Vector3I NegativeZ = new(0, 0, -1);
        private static readonly VoxelBlockType[] RenderBlockTypes =
        {
            VoxelBlockType.Grass,
            VoxelBlockType.Dirt,
            VoxelBlockType.Stone
        };

        private static readonly Vector3I[] LocalFaceDirections =
        {
            Vector3I.Right,
            Vector3I.Left,
            Vector3I.Up,
            Vector3I.Down,
            PositiveZ,
            NegativeZ
        };

        private static readonly Vector3[][] FaceVertices =
        {
            new[]
            {
                new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, 0.5f),
                new Vector3(0.5f, -0.5f, 0.5f)
            },
            new[]
            {
                new Vector3(-0.5f, -0.5f, 0.5f),
                new Vector3(-0.5f, 0.5f, 0.5f),
                new Vector3(-0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, -0.5f, -0.5f)
            },
            new[]
            {
                new Vector3(-0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, 0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, -0.5f)
            },
            new[]
            {
                new Vector3(-0.5f, -0.5f, 0.5f),
                new Vector3(-0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, -0.5f, 0.5f)
            },
            new[]
            {
                new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f),
                new Vector3(-0.5f, 0.5f, 0.5f),
                new Vector3(-0.5f, -0.5f, 0.5f)
            },
            new[]
            {
                new Vector3(-0.5f, -0.5f, -0.5f),
                new Vector3(-0.5f, 0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, -0.5f),
                new Vector3(0.5f, -0.5f, -0.5f)
            }
        };

        private static readonly Vector2[] FaceUvs =
        {
            new(0f, 0f),
            new(0f, 1f),
            new(1f, 1f),
            new(1f, 0f)
        };

        private static readonly CubeFace[] CubeFaces =
        {
            new(Vector3I.Right, PositiveZ, Vector3I.Up),
            new(Vector3I.Left, NegativeZ, Vector3I.Up),
            new(Vector3I.Up, Vector3I.Right, PositiveZ),
            new(Vector3I.Down, Vector3I.Right, NegativeZ),
            new(PositiveZ, Vector3I.Left, Vector3I.Up),
            new(NegativeZ, Vector3I.Right, Vector3I.Up)
        };

        private int baseRadiusInBlocks = 24;
        private float heightVariationInBlocks = 2.5f;
        private float noiseScale = 1.8f;
        private int worldSeed = 1337;
        private Vector3 distortionOptimizedRotationEuler = new(27f, -41f, 19f);
        private float blockSize = 0.6f;
        private bool generateOnAwake = true;
        private Material? overrideMaterial;
        private bool useDebugColors = true;
        private bool cullFacesAgainstNeighborBlocks = true;
        private int chunkSizeInCells = 12;

        private readonly Dictionary<PlanetCellId, VoxelBlockType> blocks = new();
        private readonly HashSet<PlanetCellId> removedTerrainCells = new();
        private readonly Dictionary<ChunkId, ChunkSection> chunks = new();
        private readonly Dictionary<ChunkId, HashSet<PlanetCellId>> cellsByChunk = new();
        private readonly Dictionary<CollisionObject3D, ChunkRenderSection> renderSectionByCollider = new();
        private readonly Dictionary<VoxelBlockType, Material> materials = new();
        private readonly Color[] faceDebugColors = new Color[CubeFaces.Length];
        private bool generated;
        private Basis distortionOptimizedRotation = Basis.Identity;
        private Basis inverseDistortionOptimizedRotation = Basis.Identity;

        [ExportGroup("Planet Shape")]
        [Export(PropertyHint.Range, "6,512,1")]
        public int BaseRadiusInBlocks
        {
            get => baseRadiusInBlocks;
            set => baseRadiusInBlocks = value;
        }

        [Export(PropertyHint.Range, "0,64,0.1")]
        public float HeightVariationInBlocks
        {
            get => heightVariationInBlocks;
            set => heightVariationInBlocks = value;
        }

        [Export(PropertyHint.Range, "0.1,32,0.1")]
        public float NoiseScale
        {
            get => noiseScale;
            set => noiseScale = value;
        }

        [Export]
        public int WorldSeed
        {
            get => worldSeed;
            set => worldSeed = value;
        }

        [Export]
        public Vector3 DistortionOptimizedRotationEuler
        {
            get => distortionOptimizedRotationEuler;
            set => distortionOptimizedRotationEuler = value;
        }

        [ExportGroup("Blocks")]
        [Export(PropertyHint.Range, "0.25,8,0.05")]
        public float BlockSize
        {
            get => blockSize;
            set => blockSize = value;
        }

        [Export]
        public bool GenerateOnReady
        {
            get => generateOnAwake;
            set => generateOnAwake = value;
        }

        [ExportGroup("Rendering")]
        [Export]
        public Material? OverrideMaterial
        {
            get => overrideMaterial;
            set => overrideMaterial = value;
        }

        [Export]
        public bool UseDebugColors
        {
            get => useDebugColors;
            set => useDebugColors = value;
        }

        [Export]
        public bool CullFacesAgainstNeighborBlocks
        {
            get => cullFacesAgainstNeighborBlocks;
            set => cullFacesAgainstNeighborBlocks = value;
        }

        [ExportGroup("Performance")]
        [Export(PropertyHint.Range, "2,128,1")]
        public int ChunkSizeInCells
        {
            get => chunkSizeInCells;
            set => chunkSizeInCells = value;
        }

        public Vector3 PlanetCenter => GlobalPosition;

        public float ApproximateSurfaceRadius => (baseRadiusInBlocks + heightVariationInBlocks + 0.5f) * blockSize;

        public override void _Ready()
        {
            RefreshCachedData();

            if (generateOnAwake && !generated)
            {
                GeneratePlanet();
            }
        }

        public void GeneratePlanet()
        {
            RefreshCachedData();
            blocks.Clear();
            removedTerrainCells.Clear();
            ClearChunks();
            int maxRadius = Mathf.CeilToInt(baseRadiusInBlocks + heightVariationInBlocks + 1f);

            for (int faceIndex = 0; faceIndex < CubeFaces.Length; faceIndex++)
            {
                for (int u = -maxRadius; u <= maxRadius; u++)
                {
                    for (int v = -maxRadius; v <= maxRadius; v++)
                    {
                        int minRadius = Math.Max(Math.Abs(u), Math.Abs(v));

                        for (int radius = maxRadius; radius >= minRadius; radius--)
                        {
                            PlanetCellId cell = new(faceIndex, radius, u, v);

                            if (!IsCanonicalCell(cell))
                            {
                                continue;
                            }

                            if (!TryGetNaturalTerrainBlock(cell, out VoxelBlockType blockType))
                            {
                                continue;
                            }

                            blocks[cell] = blockType;
                            break;
                        }
                    }
                }
            }

            RebuildChunkIndex();
            generated = true;
            RebuildAllChunks();
        }

        public bool HasBlock(PlanetCellId cell)
        {
            return blocks.ContainsKey(cell) ||
                   (!removedTerrainCells.Contains(cell) && TryGetNaturalTerrainBlock(cell, out _));
        }

        public bool TryGetBlock(PlanetCellId cell, out VoxelBlockType blockType)
        {
            return blocks.TryGetValue(cell, out blockType) ||
                   (!removedTerrainCells.Contains(cell) && TryGetNaturalTerrainBlock(cell, out blockType));
        }

        public bool RemoveBlock(PlanetCellId cell)
        {
            if (!blocks.Remove(cell))
            {
                return false;
            }

            if (TryGetNaturalTerrainBlock(cell, out _))
            {
                removedTerrainCells.Add(cell);
            }

            RemoveCellFromChunkIndex(cell);
            RevealExposedNeighbors(cell);
            RebuildAffectedChunks(cell);
            return true;
        }

        public bool PlaceBlock(PlanetCellId cell, VoxelBlockType blockType)
        {
            if (blockType == VoxelBlockType.Air || HasBlock(cell))
            {
                return false;
            }

            blocks[cell] = blockType;
            AddCellToChunkIndex(cell);
            RebuildAffectedChunks(cell);
            return true;
        }

        public WorldSaveData CreateSaveData()
        {
            WorldSaveData data = new()
            {
                BaseRadiusInBlocks = baseRadiusInBlocks,
                HeightVariationInBlocks = heightVariationInBlocks,
                NoiseScale = noiseScale,
                WorldSeed = worldSeed,
                DistortionOptimizedRotationEuler = Vector3Save.FromVector3(distortionOptimizedRotationEuler),
                BlockSize = blockSize,
                UseDebugColors = useDebugColors,
                CullFacesAgainstNeighborBlocks = cullFacesAgainstNeighborBlocks,
                ChunkSizeInCells = chunkSizeInCells
            };

            foreach ((PlanetCellId cell, VoxelBlockType blockType) in blocks)
            {
                data.Blocks.Add(new BlockEntrySave
                {
                    Cell = CellSave.FromCell(cell),
                    BlockType = (int)blockType
                });
            }

            foreach (PlanetCellId cell in removedTerrainCells)
            {
                data.RemovedCells.Add(CellSave.FromCell(cell));
            }

            return data;
        }

        public void LoadFromSave(WorldSaveData data)
        {
            baseRadiusInBlocks = data.BaseRadiusInBlocks;
            heightVariationInBlocks = data.HeightVariationInBlocks;
            noiseScale = data.NoiseScale;
            worldSeed = data.WorldSeed;
            distortionOptimizedRotationEuler = data.DistortionOptimizedRotationEuler.ToVector3();
            blockSize = data.BlockSize;
            useDebugColors = data.UseDebugColors;
            cullFacesAgainstNeighborBlocks = data.CullFacesAgainstNeighborBlocks;
            chunkSizeInCells = data.ChunkSizeInCells;

            RefreshCachedData();
            blocks.Clear();
            removedTerrainCells.Clear();
            ClearChunks();

            foreach (BlockEntrySave blockEntry in data.Blocks)
            {
                blocks[blockEntry.Cell.ToCell()] = (VoxelBlockType)blockEntry.BlockType;
            }

            foreach (CellSave removedCell in data.RemovedCells)
            {
                removedTerrainCells.Add(removedCell.ToCell());
            }

            RebuildChunkIndex();
            generated = true;
            RebuildAllChunks();
        }

        public Vector3 CellToWorld(PlanetCellId cell)
        {
            return ToGlobal(ProjectCubePoint(ToCubePoint(cell)) * blockSize);
        }

        public Aabb GetCellAabb(PlanetCellId cell)
        {
            Vector3 size = Vector3.One * blockSize * 1.05f;
            return new Aabb(CellToWorld(cell) - size * 0.5f, size);
        }

        public bool TryGetBreakCell(
            CollisionObject3D? collider,
            int triangleIndex,
            Vector3 collisionPoint,
            Vector3 collisionNormal,
            out PlanetCellId cell)
        {
            if (!TryResolveTriangleHit(collider, triangleIndex, out TriangleHitData hitData))
            {
                cell = default;
                return false;
            }

            cell = hitData.Cell;
            return blocks.ContainsKey(cell);
        }

        public bool TryGetPlaceCell(
            CollisionObject3D? collider,
            int triangleIndex,
            Vector3 collisionPoint,
            Vector3 collisionNormal,
            out PlanetCellId cell)
        {
            if (!TryResolveTriangleHit(collider, triangleIndex, out TriangleHitData hitData))
            {
                cell = default;
                return false;
            }

            if (!TryOffsetCell(hitData.Cell, LocalFaceDirections[hitData.FaceIndex], out cell))
            {
                return false;
            }

            return !HasBlock(cell);
        }

        private bool TryResolveTriangleHit(CollisionObject3D? collider, int triangleIndex, out TriangleHitData hitData)
        {
            hitData = default;

            if (collider is null || !renderSectionByCollider.TryGetValue(collider, out ChunkRenderSection? section))
            {
                return false;
            }

            if (triangleIndex < 0 || triangleIndex >= section.TriangleHits.Count)
            {
                return false;
            }

            hitData = section.TriangleHits[triangleIndex];
            return true;
        }

        private Material GetOrCreateMaterial(VoxelBlockType blockType)
        {
            if (materials.TryGetValue(blockType, out Material? material))
            {
                return material;
            }

            material = CreateMaterial(blockType, GetBlockColor(blockType));
            materials[blockType] = material;
            return material;
        }

        private static Color GetBlockColor(VoxelBlockType blockType)
        {
            return blockType switch
            {
                VoxelBlockType.Grass => new Color(0.42f, 0.69f, 0.24f),
                VoxelBlockType.Dirt => new Color(0.45f, 0.30f, 0.18f),
                VoxelBlockType.Stone => new Color(0.57f, 0.60f, 0.64f),
                _ => Colors.White
            };
        }

        private Material CreateMaterial(VoxelBlockType blockType, Color color)
        {
            if (overrideMaterial is not null)
            {
                Material clone = (Material)overrideMaterial.Duplicate();
                clone.ResourceName = blockType + " Material";
                if (clone is BaseMaterial3D baseClone)
                {
                    baseClone.AlbedoColor = color;
                }

                if (useDebugColors)
                {
                    EnableVertexColors(clone);
                }

                return clone;
            }

            StandardMaterial3D material = new()
            {
                ResourceName = blockType + " Material",
                AlbedoColor = color,
                Roughness = 0.92f,
                Metallic = 0f,
                CullMode = BaseMaterial3D.CullModeEnum.Back
            };

            if (useDebugColors)
            {
                EnableVertexColors(material);
            }

            return material;
        }

        private void ClearChunks()
        {
            foreach (ChunkSection chunk in chunks.Values)
            {
                DestroyChunkRuntime(chunk);
            }

            chunks.Clear();
            cellsByChunk.Clear();
            renderSectionByCollider.Clear();
        }

        private void DestroyChunkRuntime(ChunkSection chunk)
        {
            foreach (ChunkRenderSection renderSection in chunk.RenderSections.Values)
            {
                renderSectionByCollider.Remove(renderSection.Body);
                renderSection.MeshInstance.Mesh = null;
                renderSection.CollisionShape.Shape = null;
                renderSection.RuntimeMesh = null;
            }

            if (IsInstanceValid(chunk.Root))
            {
                chunk.Root.QueueFree();
            }
        }

        private void RebuildChunkIndex()
        {
            cellsByChunk.Clear();

            foreach (PlanetCellId cell in blocks.Keys)
            {
                AddCellToChunkIndex(cell);
            }
        }

        private void AddCellToChunkIndex(PlanetCellId cell)
        {
            ChunkId chunkId = GetChunkId(cell);

            if (!cellsByChunk.TryGetValue(chunkId, out HashSet<PlanetCellId>? chunkCells))
            {
                chunkCells = new HashSet<PlanetCellId>();
                cellsByChunk[chunkId] = chunkCells;
            }

            chunkCells.Add(cell);
        }

        private void RemoveCellFromChunkIndex(PlanetCellId cell)
        {
            ChunkId chunkId = GetChunkId(cell);

            if (!cellsByChunk.TryGetValue(chunkId, out HashSet<PlanetCellId>? chunkCells))
            {
                return;
            }

            chunkCells.Remove(cell);

            if (chunkCells.Count == 0)
            {
                cellsByChunk.Remove(chunkId);
            }
        }

        private void RebuildAllChunks()
        {
            List<ChunkId> chunkIds = new(cellsByChunk.Keys);

            foreach (ChunkId chunkId in chunkIds)
            {
                RebuildChunk(chunkId);
            }
        }

        private void RebuildAffectedChunks(PlanetCellId cell)
        {
            HashSet<ChunkId> dirtyChunks = new()
            {
                GetChunkId(cell)
            };

            for (int faceIndex = 0; faceIndex < LocalFaceDirections.Length; faceIndex++)
            {
                dirtyChunks.Add(GetNeighborChunkId(cell, LocalFaceDirections[faceIndex]));
            }

            foreach (ChunkId chunkId in dirtyChunks)
            {
                RebuildChunk(chunkId);
            }
        }

        private ChunkId GetNeighborChunkId(PlanetCellId cell, Vector3I localOffset)
        {
            CubeFace cubeFace = CubeFaces[cell.Face];
            Vector3I cubeOffset =
                cubeFace.AxisU * localOffset.X +
                cubeFace.Normal * localOffset.Y +
                cubeFace.AxisV * localOffset.Z;

            return GetChunkId(ToCubeCoordinate(cell) + cubeOffset);
        }

        private ChunkId GetChunkId(PlanetCellId cell)
        {
            return GetChunkId(ToCubeCoordinate(cell));
        }

        private ChunkId GetChunkId(Vector3I cubeCoordinate)
        {
            int chunkSize = Math.Max(2, chunkSizeInCells);
            return new ChunkId(
                FloorDiv(cubeCoordinate.X, chunkSize),
                FloorDiv(cubeCoordinate.Y, chunkSize),
                FloorDiv(cubeCoordinate.Z, chunkSize));
        }

        private static int FloorDiv(int value, int divisor)
        {
            int quotient = value / divisor;
            int remainder = value % divisor;
            return remainder < 0 ? quotient - 1 : quotient;
        }

        private ChunkSection EnsureChunk(ChunkId chunkId)
        {
            if (chunks.TryGetValue(chunkId, out ChunkSection? chunk))
            {
                return chunk;
            }

            Node3D chunkObject = new()
            {
                Name = $"Chunk {chunkId.X} {chunkId.Y} {chunkId.Z}"
            };
            AddChild(chunkObject);

            chunk = new ChunkSection
            {
                Root = chunkObject
            };

            foreach (VoxelBlockType blockType in RenderBlockTypes)
            {
                Node3D sectionObject = new()
                {
                    Name = blockType + " Section"
                };
                chunkObject.AddChild(sectionObject);

                MeshInstance3D meshInstance = new()
                {
                    MaterialOverride = GetOrCreateMaterial(blockType)
                };
                sectionObject.AddChild(meshInstance);

                StaticBody3D body = new()
                {
                    CollisionLayer = 1,
                    CollisionMask = 0
                };
                sectionObject.AddChild(body);

                CollisionShape3D collisionShape = new();
                body.AddChild(collisionShape);

                ChunkRenderSection renderSection = new ChunkRenderSection
                {
                    MeshInstance = meshInstance,
                    Body = body,
                    CollisionShape = collisionShape
                };

                chunk.RenderSections[blockType] = renderSection;
                renderSectionByCollider[body] = renderSection;
            }

            chunks[chunkId] = chunk;
            return chunk;
        }

        private void RebuildChunk(ChunkId chunkId)
        {
            bool hasChunkCells = cellsByChunk.TryGetValue(chunkId, out HashSet<PlanetCellId>? chunkCells) &&
                                 chunkCells.Count > 0;

            if (!hasChunkCells)
            {
                if (chunks.TryGetValue(chunkId, out ChunkSection? emptyChunk))
                {
                    chunks.Remove(chunkId);
                    DestroyChunkRuntime(emptyChunk);
                }

                return;
            }

            ChunkSection chunk = EnsureChunk(chunkId);

            foreach (VoxelBlockType blockType in RenderBlockTypes)
            {
                BuildMeshForChunkType(blockType, chunk.RenderSections[blockType], chunkCells!);
            }
        }

        private bool TryGetNaturalTerrainBlock(PlanetCellId cell, out VoxelBlockType blockType)
        {
            float depth = SampleNaturalBlockDepth(cell);

            if (depth >= 0f)
            {
                blockType = ResolveBlockType(depth);
                return true;
            }

            if (depth < -SurfaceContinuationTolerance ||
                !TryOffsetCell(cell, Vector3I.Down, out PlanetCellId inwardCell) ||
                !TryGetNaturalTerrainBlock(inwardCell, out _))
            {
                blockType = VoxelBlockType.Air;
                return false;
            }

            blockType = ResolveBlockType(0f);
            return true;
        }

        private float SampleNaturalBlockDepth(PlanetCellId cell)
        {
            Vector3 cubePoint = ToCubePoint(cell);
            Vector3 rotatedPoint = distortionOptimizedRotation * cubePoint;
            float radialDistance = Math.Max(
                Math.Abs(rotatedPoint.X),
                Math.Max(Math.Abs(rotatedPoint.Y), Math.Abs(rotatedPoint.Z)));

            if (radialDistance < 0.01f)
            {
                return float.MaxValue;
            }

            float surfaceRadius = baseRadiusInBlocks;

            if (heightVariationInBlocks > 0f)
            {
                Vector3 direction = GetTerrainDirection(rotatedPoint, radialDistance);
                surfaceRadius += SampleHeight(direction);
            }

            return surfaceRadius - radialDistance;
        }

        private Vector3 GetTerrainDirection(Vector3 rotatedPoint, float radialDistance)
        {
            Vector3 facePoint = rotatedPoint / radialDistance;
            return inverseDistortionOptimizedRotation * ProjectAdjustedSphericalCube(facePoint).Normalized();
        }

        private float SampleHeight(Vector3 direction)
        {
            float seedOffset = worldSeed * 0.001f;
            float xy = UnityPerlinNoise.Noise(direction.X * noiseScale + seedOffset, direction.Y * noiseScale + seedOffset * 2f);
            float yz = UnityPerlinNoise.Noise(direction.Y * noiseScale + seedOffset * 3f, direction.Z * noiseScale + seedOffset * 4f);
            float xz = UnityPerlinNoise.Noise(direction.X * noiseScale + seedOffset * 5f, direction.Z * noiseScale + seedOffset * 6f);
            float combined = (xy + yz + xz) / 3f;
            return (combined * 2f - 1f) * heightVariationInBlocks;
        }

        private static VoxelBlockType ResolveBlockType(float depth)
        {
            if (depth <= 1.25f)
            {
                return VoxelBlockType.Grass;
            }

            if (depth <= 3.5f)
            {
                return VoxelBlockType.Dirt;
            }

            return VoxelBlockType.Stone;
        }

        private void BuildMeshForChunkType(
            VoxelBlockType blockType,
            ChunkRenderSection section,
            HashSet<PlanetCellId> chunkCells)
        {
            List<Vector3> vertices = new();
            List<Vector3> normals = new();
            List<Color> colors = new();
            List<Vector2> uvs = new();
            List<int> triangles = new();
            List<TriangleHitData> triangleHits = new();
            List<Vector3> collisionFaces = new();

            foreach (PlanetCellId cell in chunkCells)
            {
                if (!blocks.TryGetValue(cell, out VoxelBlockType cellBlockType) || cellBlockType != blockType)
                {
                    continue;
                }

                AddBlockFaces(cell, vertices, normals, colors, uvs, triangles, triangleHits, collisionFaces);
            }

            ArrayMesh? mesh = section.RuntimeMesh;

            if (mesh is null)
            {
                mesh = new ArrayMesh
                {
                    ResourceName = blockType + " Mesh"
                };
                section.RuntimeMesh = mesh;
            }
            else
            {
                mesh.ClearSurfaces();
                mesh.ResourceName = blockType + " Mesh";
            }

            if (vertices.Count == 0)
            {
                section.MeshInstance.Mesh = null;
                section.CollisionShape.Shape = null;
                section.MeshInstance.Visible = false;
                section.TriangleHits = triangleHits;
                return;
            }

            Godot.Collections.Array arrays = [];
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = triangles.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

            section.MeshInstance.Mesh = mesh;
            section.CollisionShape.Shape = new ConcavePolygonShape3D
            {
                Data = collisionFaces.ToArray()
            };
            section.MeshInstance.Visible = true;
            section.TriangleHits = triangleHits;
        }

        private void AddBlockFaces(
            PlanetCellId cell,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Color> colors,
            List<Vector2> uvs,
            List<int> triangles,
            List<TriangleHitData> triangleHits,
            List<Vector3> collisionFaces)
        {
            CubeFace cubeFace = CubeFaces[cell.Face];
            Vector3 cubeCenter = ToCubePoint(cell);
            bool shouldCullNeighborFaces = ShouldCullNeighborFaces();

            for (int faceIndex = 0; faceIndex < LocalFaceDirections.Length; faceIndex++)
            {
                if (shouldCullNeighborFaces &&
                    TryOffsetCell(cell, LocalFaceDirections[faceIndex], out PlanetCellId neighbor) &&
                    HasBlock(neighbor))
                {
                    continue;
                }

                int baseVertexIndex = vertices.Count;
                Vector3[] renderedFaceVertices = new Vector3[4];

                for (int i = 0; i < 4; i++)
                {
                    Vector3 localVertex = FaceVertices[faceIndex][i];
                    Vector3 cubeVertex =
                        cubeCenter +
                        (Vector3)cubeFace.AxisU * localVertex.X +
                        (Vector3)cubeFace.Normal * localVertex.Y +
                        (Vector3)cubeFace.AxisV * localVertex.Z;

                    Vector3 localProjected = GetRenderedCornerPoint(cubeVertex) * blockSize;
                    renderedFaceVertices[i] = localProjected;
                    vertices.Add(localProjected);
                    colors.Add(GetDebugColor(cell, localProjected / blockSize));
                    uvs.Add(FaceUvs[i]);
                }

                Vector3 faceCenter =
                    (renderedFaceVertices[0] +
                     renderedFaceVertices[1] +
                     renderedFaceVertices[2] +
                     renderedFaceVertices[3]) * 0.25f;
                Vector3 faceNormal = faceCenter.Normalized();

                if (faceNormal.LengthSquared() < 0.0001f)
                {
                    Vector3 normalA = (renderedFaceVertices[1] - renderedFaceVertices[0])
                        .Cross(renderedFaceVertices[2] - renderedFaceVertices[0])
                        .Normalized();
                    Vector3 normalB = (renderedFaceVertices[2] - renderedFaceVertices[0])
                        .Cross(renderedFaceVertices[3] - renderedFaceVertices[0])
                        .Normalized();
                    faceNormal = (normalA + normalB).Normalized();
                }

                normals.Add(faceNormal);
                normals.Add(faceNormal);
                normals.Add(faceNormal);
                normals.Add(faceNormal);

                triangles.Add(baseVertexIndex);
                triangles.Add(baseVertexIndex + 2);
                triangles.Add(baseVertexIndex + 1);
                triangles.Add(baseVertexIndex);
                triangles.Add(baseVertexIndex + 3);
                triangles.Add(baseVertexIndex + 2);

                collisionFaces.Add(renderedFaceVertices[0]);
                collisionFaces.Add(renderedFaceVertices[2]);
                collisionFaces.Add(renderedFaceVertices[1]);
                collisionFaces.Add(renderedFaceVertices[0]);
                collisionFaces.Add(renderedFaceVertices[3]);
                collisionFaces.Add(renderedFaceVertices[2]);

                TriangleHitData hitData = new TriangleHitData(cell, faceIndex);
                triangleHits.Add(hitData);
                triangleHits.Add(hitData);
            }
        }

        private bool ShouldCullNeighborFaces()
        {
            return cullFacesAgainstNeighborBlocks;
        }

        private bool TryOffsetCell(PlanetCellId cell, Vector3I localOffset, out PlanetCellId neighbor)
        {
            CubeFace cubeFace = CubeFaces[cell.Face];
            Vector3I cubeOffset =
                cubeFace.AxisU * localOffset.X +
                cubeFace.Normal * localOffset.Y +
                cubeFace.AxisV * localOffset.Z;

            return TryEncodeCubeCoordinate(ToCubeCoordinate(cell) + cubeOffset, out neighbor);
        }

        private void RevealExposedNeighbors(PlanetCellId cell)
        {
            for (int directionIndex = 0; directionIndex < LocalFaceDirections.Length; directionIndex++)
            {
                if (!TryOffsetCell(cell, LocalFaceDirections[directionIndex], out PlanetCellId neighbor) ||
                    blocks.ContainsKey(neighbor) ||
                    removedTerrainCells.Contains(neighbor) ||
                    !TryGetNaturalTerrainBlock(neighbor, out VoxelBlockType blockType) ||
                    !IsExposedBlock(neighbor))
                {
                    continue;
                }

                blocks[neighbor] = blockType;
                AddCellToChunkIndex(neighbor);
            }
        }

        private bool IsExposedBlock(PlanetCellId cell)
        {
            for (int directionIndex = 0; directionIndex < LocalFaceDirections.Length; directionIndex++)
            {
                if (!TryOffsetCell(cell, LocalFaceDirections[directionIndex], out PlanetCellId neighbor) ||
                    !HasBlock(neighbor))
                {
                    return true;
                }
            }

            return false;
        }

        private Vector3 GetRenderedCornerPoint(Vector3 exactCubeVertex)
        {
            return ProjectCubePoint(exactCubeVertex);
        }

        private static int GetFaceIndexForPoint(Vector3 cubePoint)
        {
            float absX = Mathf.Abs(cubePoint.X);
            float absY = Mathf.Abs(cubePoint.Y);
            float absZ = Mathf.Abs(cubePoint.Z);

            if (absX >= absY && absX >= absZ)
            {
                return cubePoint.X >= 0f ? 0 : 1;
            }

            if (absY >= absZ)
            {
                return cubePoint.Y >= 0f ? 2 : 3;
            }

            return cubePoint.Z >= 0f ? 4 : 5;
        }

        private bool IsCanonicalCell(PlanetCellId cell)
        {
            return TryEncodeCubeCoordinate(ToCubeCoordinate(cell), out PlanetCellId encoded) && encoded == cell;
        }

        private Vector3I ToCubeCoordinate(PlanetCellId cell)
        {
            CubeFace cubeFace = CubeFaces[cell.Face];
            return cubeFace.Normal * cell.Radius +
                   cubeFace.AxisU * cell.U +
                   cubeFace.AxisV * cell.V;
        }

        private Vector3 ToCubePoint(PlanetCellId cell)
        {
            Vector3I cube = ToCubeCoordinate(cell);
            return new Vector3(cube.X, cube.Y, cube.Z);
        }

        private bool TryEncodeCubeCoordinate(Vector3I cubeCoordinate, out PlanetCellId cell)
        {
            int radius = Math.Max(Math.Abs(cubeCoordinate.X), Math.Max(Math.Abs(cubeCoordinate.Y), Math.Abs(cubeCoordinate.Z)));

            if (radius < 0)
            {
                cell = default;
                return false;
            }

            int faceIndex = GetFaceIndexForCube(cubeCoordinate);
            CubeFace cubeFace = CubeFaces[faceIndex];

            cell = new PlanetCellId(
                faceIndex,
                radius,
                Dot(cubeCoordinate, cubeFace.AxisU),
                Dot(cubeCoordinate, cubeFace.AxisV));

            return true;
        }

        private static int GetFaceIndexForCube(Vector3I cubeCoordinate)
        {
            int absX = Math.Abs(cubeCoordinate.X);
            int absY = Math.Abs(cubeCoordinate.Y);
            int absZ = Math.Abs(cubeCoordinate.Z);

            if (absX >= absY && absX >= absZ)
            {
                return cubeCoordinate.X >= 0 ? 0 : 1;
            }

            if (absY >= absZ)
            {
                return cubeCoordinate.Y >= 0 ? 2 : 3;
            }

            return cubeCoordinate.Z >= 0 ? 4 : 5;
        }

        private Vector3 ProjectCubePoint(Vector3 cubePoint)
        {
            Basis rotation = GetDistortionOptimizedRotation();
            Vector3 rotatedPoint = rotation * cubePoint;
            float radius = Math.Max(Math.Abs(rotatedPoint.X), Math.Max(Math.Abs(rotatedPoint.Y), Math.Abs(rotatedPoint.Z)));

            if (radius < 0.0001f)
            {
                return Vector3.Zero;
            }

            Vector3 facePoint = rotatedPoint / radius;
            Vector3 direction = ProjectAdjustedSphericalCube(facePoint).Normalized();
            return inverseDistortionOptimizedRotation * (direction * radius);
        }

        private static Vector3 ProjectAdjustedSphericalCube(Vector3 facePoint)
        {
            return CubeToSphere(facePoint);
        }

        private Basis GetDistortionOptimizedRotation()
        {
            return distortionOptimizedRotation;
        }

        private void RefreshCachedData()
        {
            Vector3 eulerRadians = distortionOptimizedRotationEuler * (Mathf.Pi / 180f);
            distortionOptimizedRotation = Basis.FromEuler(eulerRadians, EulerOrder.Zxy);
            inverseDistortionOptimizedRotation = distortionOptimizedRotation.Inverse();

            for (int faceIndex = 0; faceIndex < faceDebugColors.Length; faceIndex++)
            {
                faceDebugColors[faceIndex] = Color.FromHsv(faceIndex / (float)CubeFaces.Length, 0.35f, 1f);
            }
        }

        private static Vector3 CubeToSphere(Vector3 point)
        {
            float x2 = point.X * point.X;
            float y2 = point.Y * point.Y;
            float z2 = point.Z * point.Z;

            return new Vector3(
                point.X * Mathf.Sqrt(1f - 0.5f * (y2 + z2) + (y2 * z2) / 3f),
                point.Y * Mathf.Sqrt(1f - 0.5f * (z2 + x2) + (z2 * x2) / 3f),
                point.Z * Mathf.Sqrt(1f - 0.5f * (x2 + y2) + (x2 * y2) / 3f));
        }

        private static int Dot(Vector3I a, Vector3I b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        private Color GetDebugColor(PlanetCellId cell, Vector3 projectedPoint)
        {
            if (!useDebugColors)
            {
                return Colors.White;
            }

            Vector3 direction = projectedPoint.LengthSquared() < 0.0001f ? Vector3.Up : projectedPoint.Normalized();
            Color faceColor = faceDebugColors[cell.Face];
            Color axisColor = new(Mathf.Abs(direction.X), Mathf.Abs(direction.Y), Mathf.Abs(direction.Z), 1f);
            return faceColor.Lerp(axisColor, 0.55f);
        }

        private static void EnableVertexColors(Material material)
        {
            if (material is BaseMaterial3D baseMaterial)
            {
                baseMaterial.VertexColorUseAsAlbedo = true;
            }
        }

        private readonly struct CubeFace
        {
            public CubeFace(Vector3I normal, Vector3I axisU, Vector3I axisV)
            {
                Normal = normal;
                AxisU = axisU;
                AxisV = axisV;
            }

            public Vector3I Normal { get; }

            public Vector3I AxisU { get; }

            public Vector3I AxisV { get; }
        }

        private readonly struct TriangleHitData
        {
            public TriangleHitData(PlanetCellId cell, int faceIndex)
            {
                Cell = cell;
                FaceIndex = faceIndex;
            }

            public PlanetCellId Cell { get; }

            public int FaceIndex { get; }
        }

        private readonly struct ChunkId : IEquatable<ChunkId>
        {
            public ChunkId(int x, int y, int z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public int X { get; }

            public int Y { get; }

            public int Z { get; }

            public bool Equals(ChunkId other)
            {
                return X == other.X && Y == other.Y && Z == other.Z;
            }

            public override bool Equals(object? obj)
            {
                return obj is ChunkId other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(X, Y, Z);
            }
        }

        private sealed class ChunkSection
        {
            public Node3D Root = null!;
            public Dictionary<VoxelBlockType, ChunkRenderSection> RenderSections = new();
        }

        private sealed class ChunkRenderSection
        {
            public MeshInstance3D MeshInstance = null!;
            public StaticBody3D Body = null!;
            public CollisionShape3D CollisionShape = null!;
            public ArrayMesh? RuntimeMesh;
            public List<TriangleHitData> TriangleHits = new();
        }

        private static class UnityPerlinNoise
        {
            private static readonly int[] Permutation =
            {
                151, 160, 137, 91, 90, 15, 131, 13, 201, 95, 96, 53, 194, 233, 7, 225,
                140, 36, 103, 30, 69, 142, 8, 99, 37, 240, 21, 10, 23, 190, 6, 148,
                247, 120, 234, 75, 0, 26, 197, 62, 94, 252, 219, 203, 117, 35, 11, 32,
                57, 177, 33, 88, 237, 149, 56, 87, 174, 20, 125, 136, 171, 168, 68, 175,
                74, 165, 71, 134, 139, 48, 27, 166, 77, 146, 158, 231, 83, 111, 229, 122,
                60, 211, 133, 230, 220, 105, 92, 41, 55, 46, 245, 40, 244, 102, 143, 54,
                65, 25, 63, 161, 1, 216, 80, 73, 209, 76, 132, 187, 208, 89, 18, 169,
                200, 196, 135, 130, 116, 188, 159, 86, 164, 100, 109, 198, 173, 186, 3, 64,
                52, 217, 226, 250, 124, 123, 5, 202, 38, 147, 118, 126, 255, 82, 85, 212,
                207, 206, 59, 227, 47, 16, 58, 17, 182, 189, 28, 42, 223, 183, 170, 213,
                119, 248, 152, 2, 44, 154, 163, 70, 221, 153, 101, 155, 167, 43, 172, 9,
                129, 22, 39, 253, 19, 98, 108, 110, 79, 113, 224, 232, 178, 185, 112, 104,
                218, 246, 97, 228, 251, 34, 242, 193, 238, 210, 144, 12, 191, 179, 162, 241,
                81, 51, 145, 235, 249, 14, 239, 107, 49, 192, 214, 31, 181, 199, 106, 157,
                184, 84, 204, 176, 115, 121, 50, 45, 127, 4, 150, 254, 138, 236, 205, 93,
                222, 114, 67, 29, 24, 72, 243, 141, 128, 195, 78, 66, 215, 61, 156, 180
            };

            private static readonly int[] P = BuildPermutationTable();

            public static float Noise(float x, float y)
            {
                int xi = Mathf.FloorToInt(x) & 255;
                int yi = Mathf.FloorToInt(y) & 255;

                float xf = x - Mathf.Floor(x);
                float yf = y - Mathf.Floor(y);

                float u = Fade(xf);
                float v = Fade(yf);

                int aa = P[P[xi] + yi];
                int ab = P[P[xi] + yi + 1];
                int ba = P[P[xi + 1] + yi];
                int bb = P[P[xi + 1] + yi + 1];

                float x1 = Mathf.Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1f, yf), u);
                float x2 = Mathf.Lerp(Grad(ab, xf, yf - 1f), Grad(bb, xf - 1f, yf - 1f), u);
                float value = Mathf.Lerp(x1, x2, v);
                return (value + 1f) * 0.5f;
            }

            private static int[] BuildPermutationTable()
            {
                int[] table = new int[512];
                for (int i = 0; i < 512; i++)
                {
                    table[i] = Permutation[i & 255];
                }

                return table;
            }

            private static float Fade(float t)
            {
                return t * t * t * (t * (t * 6f - 15f) + 10f);
            }

            private static float Grad(int hash, float x, float y)
            {
                return (hash & 3) switch
                {
                    0 => x + y,
                    1 => -x + y,
                    2 => x - y,
                    _ => -x - y
                };
            }
        }
}
