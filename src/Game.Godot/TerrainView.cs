using System.Collections.Generic;
using System.Threading.Tasks;
using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Chunked 3D terrain presenter with orthographic screen-space LOD,
/// LOD0 perspective rendering with frustum culling, and a lowered chunked shadow proxy.
/// Implements IMapShadowLayers cleanly without coupling to EnvironmentView.
/// </summary>
internal sealed partial class TerrainView : Node3D, IMapShadowLayers
{
    public static readonly Vector3 LightDirection = new(-0.5f, 0.70710678f, -0.5f);
    public static readonly Color LowColor = new("424f3c");
    public static readonly Color HighColor = new("687459");

    public const int DefaultChunkCells = 100;
    public const int DefaultShadowStride = 4;
    public const float MaxCellScreenPixels = 20.0f;
    public const float MaxScreenHeightErrorPixels = 2.0f;
    public const float MaxWorldHeightErrorMeters = 0.50f;
    public const float LodHysteresisRatio = 0.15f;
    public const float GridBaseYOffset = 0.06f;

    internal sealed class TerrainChunk
    {
        public required int ChunkX { get; init; }
        public required int ChunkZ { get; init; }
        public required int StartCellX { get; init; }
        public required int EndCellX { get; init; }
        public required int StartCellZ { get; init; }
        public required int EndCellZ { get; init; }
        public required Vector3 BoundsMin { get; init; }
        public required Vector3 BoundsMax { get; init; }
        public required MeshInstance3D VisibleInstance { get; init; }
        public required ArrayMesh[] LodMeshes { get; init; }
        public required float[] MaxHeightErrors { get; init; }
        public MeshInstance3D? ShadowProxyInstance { get; init; }
        public int CurrentLodIndex { get; set; }
    }

    private sealed class LodMeshBuffers
    {
        public required Vector3[] Vertices { get; init; }
        public required Vector3[] Normals { get; init; }
        public required Color[] Colors { get; init; }
        public required int[] Indices { get; init; }
        public required float MaxError { get; init; }
    }

    private sealed class ChunkBuildData
    {
        public required int ChunkX { get; init; }
        public required int ChunkZ { get; init; }
        public required int StartCellX { get; init; }
        public required int EndCellX { get; init; }
        public required int StartCellZ { get; init; }
        public required int EndCellZ { get; init; }
        public required Vector3 BoundsMin { get; init; }
        public required Vector3 BoundsMax { get; init; }
        public required LodMeshBuffers[] Lods { get; init; }
        public int ProxyCols { get; init; }
        public int ProxyRows { get; init; }
        public Vector3[]? ProxyVertices { get; init; }
        public Vector3[]? ProxyNormals { get; init; }
        public int[]? ProxyIndices { get; init; }
        public float[]? ProxyDrops { get; init; }
    }

    private static readonly int[] DefaultStrides = [1, 2, 5, 10];

    private readonly HeightMap _map;
    private readonly Material _terrainMaterial;
    private readonly int[] _lodStrides;
    private readonly float[] _globalMaxHeightErrors;
    private readonly List<TerrainChunk> _chunks = [];
    private readonly int _chunkCols;
    private readonly int _chunkRows;
    private readonly int _shadowStride;
    private bool _terrainShadowsEnabled = true;
    private int _activeGlobalLodIndex;
    private float _lastOrthoMetersPerPixel = -1f;
    private int _lodSwitchCount;
    private bool _wasPerspective;
    private MeshInstance3D? _gridInstance;

    internal IReadOnlyList<TerrainChunk> Chunks => _chunks;
    public IReadOnlyList<int> LodStrides => _lodStrides;
    public IReadOnlyList<float> GlobalMaxHeightErrors => _globalMaxHeightErrors;
    public int ChunkCols => _chunkCols;
    public int ChunkRows => _chunkRows;
    public int ShadowStride => _shadowStride;
    public int ActiveGlobalLodIndex => _activeGlobalLodIndex;
    public int ActiveGlobalStride => _lodStrides[_activeGlobalLodIndex];
    public int LodSwitchCount => _lodSwitchCount;
    public Material TerrainMaterial => _terrainMaterial;

    public bool VegetationShadowsEnabled => false;
    public bool TerrainShadowsEnabled => _terrainShadowsEnabled;

    public TerrainView(
        HeightMap map,
        FogOfWarView? fogOverlay = null,
        Material? materialOverride = null,
        int chunkCells = DefaultChunkCells,
        int shadowStride = DefaultShadowStride)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
        Name = "TerrainView";
        _shadowStride = Math.Max(0, shadowStride);

        _terrainMaterial = materialOverride ?? (fogOverlay != null
            ? fogOverlay.Material
            : new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
                DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Lambert,
                VertexColorUseAsAlbedo = true,
                Roughness = 0.95f,
                SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled
            });

        int totalCellsX = Math.Max(1, map.Columns - 1);
        int totalCellsZ = Math.Max(1, map.Rows - 1);
        int effectiveChunkCells = Math.Max(4, chunkCells);
        _lodStrides = SelectSupportedStrides(Math.Max(totalCellsX, totalCellsZ));
        _globalMaxHeightErrors = new float[_lodStrides.Length];

        _chunkCols = Math.Max(1, (totalCellsX + effectiveChunkCells - 1) / effectiveChunkCells);
        _chunkRows = Math.Max(1, (totalCellsZ + effectiveChunkCells - 1) / effectiveChunkCells);

        int chunkCount = _chunkCols * _chunkRows;
        ChunkBuildData[] builtChunks = new ChunkBuildData[chunkCount];
        float minH = _map.MinHeight;
        float heightSpan = _map.MaxHeight - minH;

        Parallel.For(0, chunkCount, idx =>
        {
            int cx = idx % _chunkCols;
            int cz = idx / _chunkCols;
            int startX = cx * effectiveChunkCells;
            int endX = Math.Min(totalCellsX, startX + effectiveChunkCells);
            int startZ = cz * effectiveChunkCells;
            int endZ = Math.Min(totalCellsZ, startZ + effectiveChunkCells);
            builtChunks[idx] = BuildChunkBuffers(_map, _lodStrides, _shadowStride, minH, heightSpan, cx, cz, startX, endX, startZ, endZ);
        });

        for (int idx = 0; idx < chunkCount; idx++)
        {
            ChunkBuildData data = builtChunks[idx];
            for (int li = 0; li < _lodStrides.Length; li++)
            {
                if (data.Lods[li].MaxError > _globalMaxHeightErrors[li])
                {
                    _globalMaxHeightErrors[li] = data.Lods[li].MaxError;
                }
            }
        }

        if (_shadowStride > 1 && chunkCount > 1)
        {
            ReconcileBoundaryShadowDrops(builtChunks, _chunkCols, _chunkRows);
        }

        if (_shadowStride > 1)
        {
            Parallel.For(0, chunkCount, ApplyProxyDrops);
        }

        void ApplyProxyDrops(int idx)
        {
            ChunkBuildData data = builtChunks[idx];
            if (data.ProxyVertices is null || data.ProxyDrops is null) return;
            for (int i = 0; i < data.ProxyVertices.Length; i++)
            {
                float d = data.ProxyDrops[i];
                if (d > 0f)
                {
                    Vector3 v = data.ProxyVertices[i];
                    data.ProxyVertices[i] = new Vector3(v.X, v.Y - d, v.Z);
                }
            }
        }

        using Godot.Collections.Array surfaceArrays = new();
        surfaceArrays.Resize((int)Mesh.ArrayType.Max);
        using Godot.Collections.Array proxyArrays = new();
        proxyArrays.Resize((int)Mesh.ArrayType.Max);

        for (int idx = 0; idx < chunkCount; idx++)
        {
            ChunkBuildData data = builtChunks[idx];
            ArrayMesh[] lodMeshes = new ArrayMesh[_lodStrides.Length];
            float[] chunkErrors = new float[_lodStrides.Length];

            for (int li = 0; li < _lodStrides.Length; li++)
            {
                LodMeshBuffers lb = data.Lods[li];
                lodMeshes[li] = CreateArrayMesh(surfaceArrays, lb.Vertices, lb.Normals, lb.Colors, lb.Indices);
                chunkErrors[li] = lb.MaxError;
            }

            MeshInstance3D visibleInst = new()
            {
                Name = $"TerrainChunk_{data.ChunkX}_{data.ChunkZ}",
                Mesh = lodMeshes[0],
                MaterialOverride = _terrainMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };

            MeshInstance3D? proxyInst = null;
            if (data.ProxyVertices is not null && data.ProxyNormals is not null && data.ProxyIndices is not null)
            {
                ArrayMesh proxyMesh = CreateArrayMesh(proxyArrays, data.ProxyVertices, data.ProxyNormals, null, data.ProxyIndices);
                proxyInst = new MeshInstance3D
                {
                    Name = $"TerrainShadowProxy_{data.ChunkX}_{data.ChunkZ}",
                    Mesh = proxyMesh,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly,
                    Visible = _terrainShadowsEnabled
                };
            }

            TerrainChunk chunk = new()
            {
                ChunkX = data.ChunkX,
                ChunkZ = data.ChunkZ,
                StartCellX = data.StartCellX,
                EndCellX = data.EndCellX,
                StartCellZ = data.StartCellZ,
                EndCellZ = data.EndCellZ,
                BoundsMin = data.BoundsMin,
                BoundsMax = data.BoundsMax,
                VisibleInstance = visibleInst,
                LodMeshes = lodMeshes,
                MaxHeightErrors = chunkErrors,
                ShadowProxyInstance = proxyInst,
                CurrentLodIndex = 0
            };
            _chunks.Add(chunk);
            AddChild(chunk.VisibleInstance);
            if (chunk.ShadowProxyInstance is not null)
            {
                AddChild(chunk.ShadowProxyInstance);
            }
        }
    }

    public static TerrainView Create(
        HeightMap map,
        FogOfWarView? fogOverlay = null,
        Material? materialOverride = null,
        int chunkCells = DefaultChunkCells,
        int shadowStride = DefaultShadowStride) =>
        new(map, fogOverlay, materialOverride, chunkCells, shadowStride);

    public static float ComputeHillshade(Vector3 normal)
    {
        float dot = normal.Dot(LightDirection);
        return 0.45f + 0.778f * dot;
    }

    public static Vector3 EvaluateHeightMapNormal(HeightMap map, float wx, float wz)
    {
        float cs = map.CellSize;
        return new Vector3(
            map.GetHeight(wx - cs, wz) - map.GetHeight(wx + cs, wz),
            2f * cs,
            map.GetHeight(wx, wz - cs) - map.GetHeight(wx, wz + cs)).Normalized();
    }

    /// <summary>
    /// Computes the physical 3D render target height in pixels after Scaling3DScale,
    /// avoiding stretched logical GetVisibleRect heights.
    /// </summary>
    public static float GetEffectiveViewportPixelHeight(Viewport? viewport)
    {
        if (viewport is null || !GodotObject.IsInstanceValid(viewport)) return 900f;

        float baseHeight;
        if (viewport is SubViewport subViewport && subViewport.Size.Y > 0)
        {
            baseHeight = subViewport.Size.Y;
        }
        else
        {
            Vector2I winSize = SettingsPanel.ResolveWindowPixelSize(viewport);
            baseHeight = winSize.Y;
        }

        float scale3D = viewport.Scaling3DScale > 0f ? viewport.Scaling3DScale : 1f;
        return Math.Max(1f, baseHeight * scale3D);
    }

    public void AttachGrid(MeshInstance3D? grid)
    {
        _gridInstance = grid;
        SyncAttachedGridLift();
    }

    public void SetVegetationShadowsEnabled(bool enabled)
    {
    }

    public void SetTerrainShadowsEnabled(bool enabled)
    {
        if (_terrainShadowsEnabled == enabled) return;
        _terrainShadowsEnabled = enabled;
        for (int i = 0; i < _chunks.Count; i++)
        {
            MeshInstance3D? proxy = _chunks[i].ShadowProxyInstance;
            if (proxy is not null && GodotObject.IsInstanceValid(proxy))
            {
                proxy.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
                proxy.Visible = enabled;
            }
        }
    }

    public void SetMaterial(Material material)
    {
        for (int i = 0; i < _chunks.Count; i++)
        {
            if (GodotObject.IsInstanceValid(_chunks[i].VisibleInstance))
            {
                _chunks[i].VisibleInstance.MaterialOverride = material;
            }
        }
    }

    public void UpdateLod(Camera3D? camera, float viewportHeightPx)
    {
        if (camera is null || !GodotObject.IsInstanceValid(camera)) return;
        UpdateLod(camera.Projection, camera.Size, viewportHeightPx);
    }

    public void UpdateLod(
        Camera3D.ProjectionType projection,
        float cameraSize,
        float viewportHeightPx)
    {
        if (_chunks.Count == 0 || _lodStrides.Length <= 1) return;

        // Perspective uses LOD0 plus frustum culling so nearby ground stays full detail without skirts or chatter.
        if (projection != Camera3D.ProjectionType.Orthogonal)
        {
            bool enteringPerspective = !_wasPerspective;
            _wasPerspective = true;
            _lastOrthoMetersPerPixel = -1f;
            if (!enteringPerspective && _activeGlobalLodIndex == 0)
            {
                return;
            }

            if (_activeGlobalLodIndex != 0)
            {
                _activeGlobalLodIndex = 0;
                _lodSwitchCount++;
            }
            ApplyLodToAllChunks(0, forceAssign: enteringPerspective);
            SyncAttachedGridLift();
            return;
        }

        bool returningFromPerspective = _wasPerspective;
        _wasPerspective = false;

        float metersPerPixel = Math.Max(0.0001f, cameraSize / Math.Max(1f, viewportHeightPx));
        int nextLod = SelectOrthoLodIndex(
            metersPerPixel,
            _map.CellSize,
            _lodStrides,
            _globalMaxHeightErrors,
            _activeGlobalLodIndex,
            returningFromPerspective ? -1f : _lastOrthoMetersPerPixel);

        _lastOrthoMetersPerPixel = metersPerPixel;
        if (!returningFromPerspective && nextLod == _activeGlobalLodIndex)
        {
            return;
        }

        if (nextLod != _activeGlobalLodIndex)
        {
            _activeGlobalLodIndex = nextLod;
            _lodSwitchCount++;
        }
        ApplyLodToAllChunks(nextLod, forceAssign: returningFromPerspective);
        SyncAttachedGridLift();
    }

    private void ApplyLodToAllChunks(int lodIndex, bool forceAssign)
    {
        for (int i = 0; i < _chunks.Count; i++)
        {
            TerrainChunk chunk = _chunks[i];
            ArrayMesh targetMesh = chunk.LodMeshes[lodIndex];
            if (forceAssign || chunk.CurrentLodIndex != lodIndex || !ReferenceEquals(chunk.VisibleInstance.Mesh, targetMesh))
            {
                chunk.CurrentLodIndex = lodIndex;
                chunk.VisibleInstance.Mesh = targetMesh;
            }
        }
    }

    private void SyncAttachedGridLift()
    {
        if (_gridInstance is null || !GodotObject.IsInstanceValid(_gridInstance)) return;
        float targetY = _globalMaxHeightErrors[_activeGlobalLodIndex];
        if (!Mathf.IsEqualApprox(_gridInstance.Position.Y, targetY))
        {
            Vector3 pos = _gridInstance.Position;
            _gridInstance.Position = new Vector3(pos.X, targetY, pos.Z);
        }
    }

    public static int SelectOrthoLodIndex(
        float metersPerPixel,
        float baseCellSizeMeters,
        int[] strides,
        float[] maxHeightErrors,
        int previousLodIndex,
        float previousMetersPerPixel)
    {
        if (strides.Length == 0) return 0;

        int bestLod = 0;
        for (int i = 1; i < strides.Length; i++)
        {
            if (maxHeightErrors[i] > MaxWorldHeightErrorMeters)
            {
                continue;
            }

            float strideMeters = strides[i] * baseCellSizeMeters;
            float minMppForCellSize = strideMeters / MaxCellScreenPixels;
            float requiredMppForError = maxHeightErrors[i] / MaxScreenHeightErrorPixels;
            float thresholdMpp = Math.Max(minMppForCellSize, requiredMppForError);

            if (previousMetersPerPixel > 0f)
            {
                if (i > previousLodIndex)
                {
                    thresholdMpp *= 1f + LodHysteresisRatio * 0.5f;
                }
                else
                {
                    thresholdMpp *= 1f - LodHysteresisRatio * 0.5f;
                }
            }

            if (metersPerPixel >= thresholdMpp)
            {
                bestLod = i;
            }
        }

        return bestLod;
    }

    public bool HasUpwardFrontFaces()
    {
        if (_chunks.Count == 0) return false;
        for (int c = 0; c < _chunks.Count; c++)
        {
            ArrayMesh mesh = _chunks[c].LodMeshes[0];
            if (!HasMeshUpwardFrontFaces(mesh)) return false;
        }
        return true;
    }

    internal static bool HasMeshUpwardFrontFaces(Mesh mesh)
    {
        using Godot.Collections.Array arrays = mesh.SurfaceGetArrays(0);
        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        int[] indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        for (int i = 0; i < indices.Length; i += 3)
        {
            Vector3 a = vertices[indices[i]], b = vertices[indices[i + 1]], c = vertices[indices[i + 2]];
            if ((b - a).Cross(c - a).Y >= 0) return false;
        }
        return indices.Length > 0;
    }

    // Grid line spacing in meters: 8 m on small test maps, 100 m on kilometer maps so the grid
    // stays readable and its mesh stays small.
    internal static float GridSpacing(HeightMap map) => Math.Max(map.Width, map.Depth) > 500f ? 100f : 8f;

    public static MeshInstance3D CreateGrid(HeightMap map)
    {
        float spacing = GridSpacing(map);
        List<Vector3> vertices = new();
        for (float line = 0; line <= map.Width; line += spacing)
            for (float p = 0; p < map.Depth; p += map.CellSize)
            {
                float end = Math.Min(p + map.CellSize, map.Depth);
                vertices.Add(new Vector3(line, map.GetHeight(line, p) + GridBaseYOffset, p));
                vertices.Add(new Vector3(line, map.GetHeight(line, end) + GridBaseYOffset, end));
            }
        for (float line = 0; line <= map.Depth; line += spacing)
            for (float p = 0; p < map.Width; p += map.CellSize)
            {
                float end = Math.Min(p + map.CellSize, map.Width);
                vertices.Add(new Vector3(p, map.GetHeight(p, line) + GridBaseYOffset, line));
                vertices.Add(new Vector3(end, map.GetHeight(end, line) + GridBaseYOffset, line));
            }
        using Godot.Collections.Array arrays = new();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        ArrayMesh mesh = new();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        return new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.19f, 0.26f, 0.21f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                DisableFog = true
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
    }

    internal static int[] BuildSampleIndices(int startCell, int endCell, int stride)
    {
        List<int> list = [];
        for (int g = startCell; g < endCell; g += stride)
        {
            list.Add(g);
        }
        if (list.Count == 0 || list[^1] != endCell)
        {
            list.Add(endCell);
        }
        return list.ToArray();
    }

    internal static int[] SelectSupportedStrides(int maxCells)
    {
        if (maxCells <= 32) return [1];
        if (maxCells <= 128) return [1, 2];
        return DefaultStrides;
    }

    private static ArrayMesh CreateArrayMesh(
        Godot.Collections.Array reusableArrays,
        Vector3[] vertices,
        Vector3[] normals,
        Color[]? colors,
        int[] indices)
    {
        reusableArrays[(int)Mesh.ArrayType.Vertex] = vertices;
        reusableArrays[(int)Mesh.ArrayType.Normal] = normals;
        reusableArrays[(int)Mesh.ArrayType.Color] = colors is not null ? Variant.From(colors) : default;
        reusableArrays[(int)Mesh.ArrayType.Index] = indices;
        ArrayMesh mesh = new();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, reusableArrays);
        return mesh;
    }

    private static ChunkBuildData BuildChunkBuffers(
        HeightMap map,
        int[] strides,
        int shadowStride,
        float minH,
        float heightSpan,
        int cx,
        int cz,
        int startX,
        int endX,
        int startZ,
        int endZ)
    {
        float cs = map.CellSize;
        int cols0 = endX - startX + 1;
        int rows0 = endZ - startZ + 1;
        int vertCount0 = cols0 * rows0;

        Vector3[] verts0 = new Vector3[vertCount0];
        Vector3[] norms0 = new Vector3[vertCount0];
        Color[] colors0 = new Color[vertCount0];

        float bMinY = float.MaxValue;
        float bMaxY = float.MinValue;

        for (int lz = 0; lz < rows0; lz++)
        {
            float wz = (startZ + lz) * cs;
            int rowOff = lz * cols0;
            for (int lx = 0; lx < cols0; lx++)
            {
                float wx = (startX + lx) * cs;
                float h = map.GetHeight(wx, wz);
                if (h < bMinY) bMinY = h;
                if (h > bMaxY) bMaxY = h;
                verts0[rowOff + lx] = new Vector3(wx, h, wz);
            }
        }

        for (int lz = 0; lz < rows0; lz++)
        {
            int gz = startZ + lz;
            float wz = gz * cs;
            int rowOff = lz * cols0;
            for (int lx = 0; lx < cols0; lx++)
            {
                int gx = startX + lx;
                float wx = gx * cs;
                int vi = rowOff + lx;

                float hL = lx > 0 ? verts0[vi - 1].Y : map.GetHeight(wx - cs, wz);
                float hR = lx < cols0 - 1 ? verts0[vi + 1].Y : map.GetHeight(wx + cs, wz);
                float hU = lz > 0 ? verts0[vi - cols0].Y : map.GetHeight(wx, wz - cs);
                float hD = lz < rows0 - 1 ? verts0[vi + cols0].Y : map.GetHeight(wx, wz + cs);

                Vector3 norm = new Vector3(hL - hR, 2f * cs, hU - hD).Normalized();
                norms0[vi] = norm;

                float h = verts0[vi].Y;
                float heightT = heightSpan > 0.001f ? Mathf.Clamp((h - minH) / heightSpan, 0f, 1f) : 0.5f;
                Color baseColor = LowColor.Lerp(HighColor, heightT);
                float hillshade = ComputeHillshade(norm);
                colors0[vi] = new Color(
                    Mathf.Clamp(baseColor.R * hillshade, 0f, 1f),
                    Mathf.Clamp(baseColor.G * hillshade, 0f, 1f),
                    Mathf.Clamp(baseColor.B * hillshade, 0f, 1f),
                    1.0f);
            }
        }

        int[] indices0 = BuildQuadIndices(cols0, rows0);
        LodMeshBuffers[] lods = new LodMeshBuffers[strides.Length];
        lods[0] = new LodMeshBuffers
        {
            Vertices = verts0,
            Normals = norms0,
            Colors = colors0,
            Indices = indices0,
            MaxError = 0f
        };

        for (int li = 1; li < strides.Length; li++)
        {
            int stride = strides[li];
            int[] xs = BuildSampleIndices(startX, endX, stride);
            int[] zs = BuildSampleIndices(startZ, endZ, stride);
            int vCols = xs.Length;
            int vRows = zs.Length;
            int count = vCols * vRows;

            Vector3[] verts = new Vector3[count];
            Vector3[] norms = new Vector3[count];
            Color[] colors = new Color[count];
            for (int iz = 0; iz < vRows; iz++)
            {
                int srcRow = (zs[iz] - startZ) * cols0;
                int dstRow = iz * vCols;
                for (int ix = 0; ix < vCols; ix++)
                {
                    int srcIdx = srcRow + (xs[ix] - startX);
                    int dstIdx = dstRow + ix;
                    verts[dstIdx] = verts0[srcIdx];
                    norms[dstIdx] = norms0[srcIdx];
                    colors[dstIdx] = colors0[srcIdx];
                }
            }

            float maxErr = MeasureSurfaceErrorAndDrops(verts0, startX, startZ, cols0, rows0, xs, zs, drops: null);
            lods[li] = new LodMeshBuffers
            {
                Vertices = verts,
                Normals = norms,
                Colors = colors,
                Indices = BuildQuadIndices(vCols, vRows),
                MaxError = maxErr
            };
        }

        int pCols = 0, pRows = 0;
        Vector3[]? proxyVerts = null;
        Vector3[]? proxyNorms = null;
        int[]? proxyIndices = null;
        float[]? proxyDrops = null;

        if (shadowStride > 0)
        {
            int[] pxs = BuildSampleIndices(startX, endX, shadowStride);
            int[] pzs = BuildSampleIndices(startZ, endZ, shadowStride);
            pCols = pxs.Length;
            pRows = pzs.Length;
            int pCount = pCols * pRows;
            proxyVerts = new Vector3[pCount];
            proxyNorms = new Vector3[pCount];
            for (int iz = 0; iz < pRows; iz++)
            {
                int srcRow = (pzs[iz] - startZ) * cols0;
                int dstRow = iz * pCols;
                for (int ix = 0; ix < pCols; ix++)
                {
                    int srcIdx = srcRow + (pxs[ix] - startX);
                    int dstIdx = dstRow + ix;
                    proxyVerts[dstIdx] = verts0[srcIdx];
                    proxyNorms[dstIdx] = norms0[srcIdx];
                }
            }

            proxyIndices = BuildQuadIndices(pCols, pRows);
            if (shadowStride > 1)
            {
                proxyDrops = new float[pCount];
                MeasureSurfaceErrorAndDrops(verts0, startX, startZ, cols0, rows0, pxs, pzs, proxyDrops);
            }
        }

        return new ChunkBuildData
        {
            ChunkX = cx,
            ChunkZ = cz,
            StartCellX = startX,
            EndCellX = endX,
            StartCellZ = startZ,
            EndCellZ = endZ,
            BoundsMin = new Vector3(startX * cs, bMinY, startZ * cs),
            BoundsMax = new Vector3(endX * cs, bMaxY, endZ * cs),
            Lods = lods,
            ProxyCols = pCols,
            ProxyRows = pRows,
            ProxyVertices = proxyVerts,
            ProxyNormals = proxyNorms,
            ProxyIndices = proxyIndices,
            ProxyDrops = proxyDrops
        };
    }

    private static int[] BuildQuadIndices(int vCols, int vRows)
    {
        int[] indices = new int[(vCols - 1) * (vRows - 1) * 6];
        int offset = 0;
        for (int iz = 0; iz < vRows - 1; iz++)
        {
            for (int ix = 0; ix < vCols - 1; ix++)
            {
                int a = iz * vCols + ix;
                int b = a + 1;
                int c = a + vCols;
                int d = c + 1;
                // Godot uses clockwise front faces. The opposite winding hides the ground from above.
                indices[offset++] = a;
                indices[offset++] = b;
                indices[offset++] = c;
                indices[offset++] = b;
                indices[offset++] = d;
                indices[offset++] = c;
            }
        }
        return indices;
    }

    /// <summary>
    /// Computes exact maximum surface error between a coarse sample grid and the fine triangulated
    /// surface by evaluating all common-refinement vertices (fine grid points, coarse-diagonal vs fine-edge
    /// crossings, and coarse-diagonal vs fine-diagonal crossings). Optionally populates per-vertex
    /// shadow proxy drop compensations along exact proxy triangles.
    /// </summary>
    internal static float ComputeExactMaxHeightError(HeightMap map, int[] xs, int[] zs)
    {
        int startX = xs[0];
        int startZ = zs[0];
        int endX = xs[^1];
        int endZ = zs[^1];
        int cols0 = endX - startX + 1;
        int rows0 = endZ - startZ + 1;
        Vector3[] verts0 = new Vector3[cols0 * rows0];
        float cs = map.CellSize;
        for (int lz = 0; lz < rows0; lz++)
        {
            float wz = (startZ + lz) * cs;
            int rowOff = lz * cols0;
            for (int lx = 0; lx < cols0; lx++)
            {
                verts0[rowOff + lx] = new Vector3((startX + lx) * cs, map.GetHeight((startX + lx) * cs, wz), wz);
            }
        }
        return MeasureSurfaceErrorAndDrops(verts0, startX, startZ, cols0, rows0, xs, zs, drops: null);
    }

    internal static float[] ComputeConservativeShadowProxyDrops(
        Vector3[] fineVertices,
        int fineCols,
        int fineRows,
        int stride)
    {
        int[] xs = BuildSampleIndices(0, fineCols - 1, stride);
        int[] zs = BuildSampleIndices(0, fineRows - 1, stride);
        float[] drops = new float[xs.Length * zs.Length];
        MeasureSurfaceErrorAndDrops(fineVertices, 0, 0, fineCols, fineRows, xs, zs, drops);
        return drops;
    }

    internal static void ReconcileHorizontalEdgeShadowDrops(
        float[] leftDrops,
        int leftCols,
        int leftRows,
        float[] rightDrops,
        int rightCols,
        int rightRows)
    {
        int rows = Math.Min(leftRows, rightRows);
        for (int r = 0; r < rows; r++)
        {
            int li = r * leftCols + (leftCols - 1);
            int ri = r * rightCols;
            float merged = Math.Max(leftDrops[li], rightDrops[ri]);
            leftDrops[li] = merged;
            rightDrops[ri] = merged;
        }
    }

    internal static void ReconcileVerticalEdgeShadowDrops(
        float[] topDrops,
        int topCols,
        int topRows,
        float[] bottomDrops,
        int bottomCols)
    {
        int cols = Math.Min(topCols, bottomCols);
        int topRowOff = (topRows - 1) * topCols;
        for (int c = 0; c < cols; c++)
        {
            int ti = topRowOff + c;
            int bi = c;
            float merged = Math.Max(topDrops[ti], bottomDrops[bi]);
            topDrops[ti] = merged;
            bottomDrops[bi] = merged;
        }
    }

    private static float MeasureSurfaceErrorAndDrops(
        Vector3[] verts0,
        int startX,
        int startZ,
        int cols0,
        int rows0,
        int[] xs,
        int[] zs,
        float[]? drops)
    {
        float maxAbsErr = 0f;
        int cCols = xs.Length;

        for (int iz = 0; iz < zs.Length - 1; iz++)
        {
            int z0 = zs[iz], z1 = zs[iz + 1];
            int dz = z1 - z0;
            float invDz = 1f / dz;

            for (int ix = 0; ix < cCols - 1; ix++)
            {
                int x0 = xs[ix], x1 = xs[ix + 1];
                int dx = x1 - x0;
                float invDx = 1f / dx;

                float ha = verts0[(z0 - startZ) * cols0 + (x0 - startX)].Y;
                float hb = verts0[(z0 - startZ) * cols0 + (x1 - startX)].Y;
                float hc = verts0[(z1 - startZ) * cols0 + (x0 - startX)].Y;
                float hd = verts0[(z1 - startZ) * cols0 + (x1 - startX)].Y;

                float t1Above = 0f;
                float t2Above = 0f;

                void AccumulateSample(float u, float v, float fineH)
                {
                    float coarseH = EvaluateQuadTrianglesHeight(ha, hb, hc, hd, u, v);
                    float diff = coarseH - fineH;
                    float absErr = MathF.Abs(diff);
                    if (absErr > maxAbsErr) maxAbsErr = absErr;

                    if (drops is not null)
                    {
                        float uv = u + v;
                        if (uv <= 1f + 1e-5f && diff > t1Above) t1Above = diff;
                        if (uv >= 1f - 1e-5f && diff > t2Above) t2Above = diff;
                    }
                }

                // 1. All fine integer grid points in [x0..x1] x [z0..z1]
                for (int fz = z0; fz <= z1; fz++)
                {
                    float v = (fz - z0) * invDz;
                    int fineRow = (fz - startZ) * cols0;
                    for (int fx = x0; fx <= x1; fx++)
                    {
                        float u = (fx - x0) * invDx;
                        float fineH = verts0[fineRow + (fx - startX)].Y;
                        AccumulateSample(u, v, fineH);
                    }
                }

                // 2. Coarse diagonal crossings against fine vertical grid lines x = fx
                for (int fx = x0 + 1; fx < x1; fx++)
                {
                    float u = (fx - x0) * invDx;
                    float v = 1f - u;
                    float gz = z0 + v * dz;
                    float fineH = EvaluateFineSurfaceHeightLocal(verts0, startX, startZ, cols0, rows0, fx, gz);
                    AccumulateSample(u, v, fineH);
                }

                // 3. Coarse diagonal crossings against fine horizontal grid lines z = fz
                for (int fz = z0 + 1; fz < z1; fz++)
                {
                    float v = (fz - z0) * invDz;
                    float u = 1f - v;
                    float gx = x0 + u * dx;
                    float fineH = EvaluateFineSurfaceHeightLocal(verts0, startX, startZ, cols0, rows0, gx, fz);
                    AccumulateSample(u, v, fineH);
                }

                // 4. Coarse diagonal crossings against fine cell diagonals x + z = k (for non-square coarse cells)
                if (dx != dz)
                {
                    float denom = dx - dz;
                    int kMin = x0 + z0 + 1;
                    int kMax = x1 + z1 - 1;
                    for (int k = kMin; k <= kMax; k++)
                    {
                        float u = (k - x0 - z0 - dz) / denom;
                        if (u > 0f && u < 1f)
                        {
                            float v = 1f - u;
                            float fineH = EvaluateFineSurfaceHeightLocal(
                                verts0, startX, startZ, cols0, rows0, x0 + u * dx, z0 + v * dz);
                            AccumulateSample(u, v, fineH);
                        }
                    }
                }

                if (drops is not null)
                {
                    const float margin = 0.08f;
                    int i00 = iz * cCols + ix;
                    int i10 = i00 + 1;
                    int i01 = i00 + cCols;
                    int i11 = i01 + 1;

                    if (t1Above > 0.001f)
                    {
                        float drop1 = t1Above + margin;
                        if (drop1 > drops[i00]) drops[i00] = drop1;
                        if (drop1 > drops[i10]) drops[i10] = drop1;
                        if (drop1 > drops[i01]) drops[i01] = drop1;
                    }
                    if (t2Above > 0.001f)
                    {
                        float drop2 = t2Above + margin;
                        if (drop2 > drops[i10]) drops[i10] = drop2;
                        if (drop2 > drops[i01]) drops[i01] = drop2;
                        if (drop2 > drops[i11]) drops[i11] = drop2;
                    }
                }
            }
        }

        return maxAbsErr;
    }

    private static void ReconcileBoundaryShadowDrops(ChunkBuildData[] chunks, int chunkCols, int chunkRows)
    {
        // Horizontal seam sync: chunk (cx, cz) right edge vs chunk (cx+1, cz) left edge
        for (int cz = 0; cz < chunkRows; cz++)
        {
            for (int cx = 0; cx < chunkCols - 1; cx++)
            {
                ChunkBuildData left = chunks[cz * chunkCols + cx];
                ChunkBuildData right = chunks[cz * chunkCols + (cx + 1)];
                if (left.ProxyDrops is null || right.ProxyDrops is null) continue;
                ReconcileHorizontalEdgeShadowDrops(
                    left.ProxyDrops, left.ProxyCols, left.ProxyRows,
                    right.ProxyDrops, right.ProxyCols, right.ProxyRows);
            }
        }

        // Vertical seam sync: chunk (cx, cz) bottom edge vs chunk (cx, cz+1) top edge (also propagates 4-way corners)
        for (int cz = 0; cz < chunkRows - 1; cz++)
        {
            for (int cx = 0; cx < chunkCols; cx++)
            {
                ChunkBuildData top = chunks[cz * chunkCols + cx];
                ChunkBuildData bottom = chunks[(cz + 1) * chunkCols + cx];
                if (top.ProxyDrops is null || bottom.ProxyDrops is null) continue;
                ReconcileVerticalEdgeShadowDrops(
                    top.ProxyDrops, top.ProxyCols, top.ProxyRows,
                    bottom.ProxyDrops, bottom.ProxyCols);
            }
        }
    }

    private static float EvaluateFineSurfaceHeightLocal(
        Vector3[] verts0,
        int startX,
        int startZ,
        int cols0,
        int rows0,
        float gx,
        float gz)
    {
        float localX = Math.Clamp(gx - startX, 0f, cols0 - 1);
        float localZ = Math.Clamp(gz - startZ, 0f, rows0 - 1);
        int lx = Math.Clamp((int)MathF.Floor(localX), 0, cols0 - 2);
        int lz = Math.Clamp((int)MathF.Floor(localZ), 0, rows0 - 2);
        float fu = localX - lx;
        float fv = localZ - lz;
        int idx = lz * cols0 + lx;
        return EvaluateQuadTrianglesHeight(
            verts0[idx].Y,
            verts0[idx + 1].Y,
            verts0[idx + cols0].Y,
            verts0[idx + cols0 + 1].Y,
            fu,
            fv);
    }

    internal static float EvaluateSurfaceHeightAtWorld(HeightMap map, int[] xs, int[] zs, Vector3[] vertices, float wx, float wz)
    {
        float gx = Math.Clamp(wx / map.CellSize, xs[0], xs[^1]);
        float gz = Math.Clamp(wz / map.CellSize, zs[0], zs[^1]);

        int ix = 0;
        while (ix < xs.Length - 2 && gx > xs[ix + 1]) ix++;
        int iz = 0;
        while (iz < zs.Length - 2 && gz > zs[iz + 1]) iz++;

        float u = Math.Clamp((gx - xs[ix]) / Math.Max(1, xs[ix + 1] - xs[ix]), 0f, 1f);
        float v = Math.Clamp((gz - zs[iz]) / Math.Max(1, zs[iz + 1] - zs[iz]), 0f, 1f);
        int vCols = xs.Length;
        int idx = iz * vCols + ix;
        return EvaluateQuadTrianglesHeight(
            vertices[idx].Y,
            vertices[idx + 1].Y,
            vertices[idx + vCols].Y,
            vertices[idx + vCols + 1].Y,
            u,
            v);
    }

    private static float EvaluateQuadTrianglesHeight(float ha, float hb, float hc, float hd, float u, float v)
    {
        if (u + v <= 1f)
        {
            return ha + u * (hb - ha) + v * (hc - ha);
        }
        float wb = 1f - v;
        float wc = 1f - u;
        float wd = u + v - 1f;
        return wb * hb + wc * hc + wd * hd;
    }
}
