using System.Diagnostics;
using System.Runtime.CompilerServices;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using Vector2 = Godot.Vector2;

namespace DeadGround.Client;

/// <summary>
/// Client-side fog of war visibility grid and terrain presentation.
/// Evaluates visibility using only client-known state: own units from ClientReplica,
/// perception profiles from the unit catalog, map heightmap and static geometry.
///
/// Fully allocation-free in steady-state updates.
/// </summary>
internal sealed class FogOfWarView : IDisposable
{
    private const string TerrainShaderCode = @"
shader_type spatial;
render_mode unshaded, cull_back;

uniform sampler2D fog_texture : filter_linear, repeat_disable;
uniform vec2 map_size;
uniform bool fog_enabled = true;

varying vec2 terrain_uv;

void vertex() {
    terrain_uv = VERTEX.xz / map_size;
}

void fragment() {
    vec3 base_color = COLOR.rgb;
    if (fog_enabled) {
        float vis = texture(fog_texture, clamp(terrain_uv, vec2(0.0), vec2(1.0))).r;
        float gray = dot(base_color, vec3(0.299, 0.587, 0.114));
        vec3 desaturated = mix(base_color, vec3(gray), 0.35);
        vec3 unseen_color = desaturated * 0.45;
        ALBEDO = mix(unseen_color, base_color, vis);
    } else {
        ALBEDO = base_color;
    }
}
";

    public const float CycleInterval = 0.25f;
    public const float FrameTimeBudgetMs = 0.85f;
    // A unit that moved less than this since its last sweep keeps its cached viewshed.
    public const float ResweepDistance = 2.0f;

    // The cells one own unit saw at its last sweep. Units that do not move are not swept again,
    // so a large force that holds its ground costs nothing per cycle.
    private sealed class UnitViewshed
    {
        public NumericsVector2 Position;
        public ushort Definition;
        public int[] Cells = [];
        public int CellCount;
        public bool Present;
        public bool Swept;
    }

    private readonly HeightMap _map;
    private readonly StaticGeometry? _geometry;
    private readonly byte[] _activeVisibility;
    private readonly byte[] _scratchVisibility;
    // How many own units see each cell; a cell is seen while its count is above zero.
    private readonly ushort[] _seenCount;
    private readonly Dictionary<EntityId, UnitViewshed> _viewsheds = new();
    private readonly List<EntityId> _removedUnits = new();
    private UnitViewshed? _sweepTarget;
    private int _sweepsThisCycle;
    private readonly byte[] _blurBuffer;
    private readonly byte[] _textureBuffer;
    private readonly float[] _horizonBuffer;
    private readonly float[] _structureTops;
    private readonly float[] _cellGroundY;
    private readonly float[] _cellBlockY;
    private readonly float[] _cellCenterX;
    private readonly float[] _cellCenterZ;
    private readonly Image _image;
    private readonly ImageTexture _texture;
    private readonly Shader _shader;
    private readonly ShaderMaterial _material;

    private float _timeSinceCycleStart;
    private int _unitIndexInCycle;
    private bool _cycleInProgress;
    private bool _isDisposed;

    public float CellSize { get; }
    public int Columns { get; }
    public int Rows { get; }
    public bool IsEnabled { get; private set; } = true;
    public ShaderMaterial Material => _material;
    public ImageTexture Texture => _texture;
    internal bool IsCycleInProgress => _cycleInProgress;
    // Units swept in the last completed cycle; stationary units are not counted.
    internal int SweepsInLastCycle { get; private set; }

    public FogOfWarView(HeightMap map, StaticGeometry? geometry = null, float? customCellSize = null)
    {
        _map = map;
        _geometry = geometry;

        CellSize = customCellSize ?? (Math.Max(map.Width, map.Depth) > 500f ? 20.0f : 4.0f);
        Columns = (int)MathF.Ceiling(map.Width / CellSize);
        Rows = (int)MathF.Ceiling(map.Depth / CellSize);

        int totalCells = Columns * Rows;
        _activeVisibility = new byte[totalCells];
        _scratchVisibility = new byte[totalCells];
        _seenCount = new ushort[totalCells];
        _blurBuffer = new byte[totalCells];
        _textureBuffer = new byte[totalCells];
        _horizonBuffer = new float[totalCells];
        _structureTops = new float[totalCells];
        _cellGroundY = new float[totalCells];
        _cellBlockY = new float[totalCells];
        _cellCenterX = new float[Columns];
        for (int cx = 0; cx < Columns; cx++) _cellCenterX[cx] = (cx + 0.5f) * CellSize;
        _cellCenterZ = new float[Rows];
        for (int cz = 0; cz < Rows; cz++) _cellCenterZ[cz] = (cz + 0.5f) * CellSize;

        if (geometry is not null)
        {
            var structures = geometry.Structures;
            int count = structures.Count;
            for (int i = 0; i < count; i++)
            {
                OpaqueStructure s = structures[i];
                int minCx = Math.Clamp((int)MathF.Floor(s.MinX / CellSize), 0, Columns - 1);
                int maxCx = Math.Clamp((int)MathF.Floor((s.MaxX - 1e-4f) / CellSize), 0, Columns - 1);
                int minCz = Math.Clamp((int)MathF.Floor(s.MinZ / CellSize), 0, Rows - 1);
                int maxCz = Math.Clamp((int)MathF.Floor((s.MaxZ - 1e-4f) / CellSize), 0, Rows - 1);

                for (int cz = minCz; cz <= maxCz; cz++)
                {
                    int rowOffset = cz * Columns;
                    for (int cx = minCx; cx <= maxCx; cx++)
                    {
                        int idx = rowOffset + cx;
                        if (s.MaxY > _structureTops[idx])
                        {
                            _structureTops[idx] = s.MaxY;
                        }
                    }
                }
            }
        }

        for (int cz = 0; cz < Rows; cz++)
        {
            float wz = (cz + 0.5f) * CellSize;
            int rowOffset = cz * Columns;
            for (int cx = 0; cx < Columns; cx++)
            {
                float wx = (cx + 0.5f) * CellSize;
                int idx = rowOffset + cx;
                float gy = map.GetHeight(wx, wz);
                _cellGroundY[idx] = gy;
                float sy = _structureTops[idx];
                _cellBlockY[idx] = sy > 0f ? Math.Max(gy, sy) : gy;
            }
        }

        _image = Image.CreateEmpty(Columns, Rows, false, Image.Format.R8);
        _texture = ImageTexture.CreateFromImage(_image);

        _shader = new Shader { Code = TerrainShaderCode };
        _material = new ShaderMaterial { Shader = _shader };
        _material.SetShaderParameter("fog_texture", _texture);
        _material.SetShaderParameter("map_size", new Vector2(map.Width, map.Depth));
        _material.SetShaderParameter("fog_enabled", IsEnabled);
    }

    public void AttachToTerrain(MeshInstance3D terrain)
    {
        terrain.MaterialOverride = _material;
    }

    public void Toggle()
    {
        SetEnabled(!IsEnabled);
    }

    public void SetEnabled(bool enabled)
    {
        IsEnabled = enabled;
        _material.SetShaderParameter("fog_enabled", IsEnabled);
    }

    public bool IsCellSeen(int cx, int cz)
    {
        if (cx < 0 || cx >= Columns || cz < 0 || cz >= Rows) return false;
        return _activeVisibility[cz * Columns + cx] > 0;
    }

    public bool IsWorldPointSeen(float x, float z)
    {
        int cx = (int)(x / CellSize);
        int cz = (int)(z / CellSize);
        return IsCellSeen(cx, cz);
    }

    public int GetSeenCellCount()
    {
        int seen = 0;
        for (int i = 0; i < _activeVisibility.Length; i++)
        {
            if (_activeVisibility[i] > 0) seen++;
        }
        return seen;
    }

    public void Update(float delta, ClientReplica replica, UnitCatalog catalog)
    {
        ReadOnlySpan<OwnUnitObservation> ownUnits = replica.OwnUnits;
        _timeSinceCycleStart += delta;

        if (!_cycleInProgress)
        {
            if (_timeSinceCycleStart < CycleInterval)
            {
                return;
            }
            _timeSinceCycleStart = 0f;
            BeginCycle(ownUnits);
        }

        long startTimestamp = Stopwatch.GetTimestamp();
        long maxTicks = (long)(FrameTimeBudgetMs * Stopwatch.Frequency / 1000.0);
        while (_unitIndexInCycle < ownUnits.Length)
        {
            bool swept = ProcessUnit(ownUnits[_unitIndexInCycle], catalog);
            _unitIndexInCycle++;
            if (swept && (Stopwatch.GetTimestamp() - startTimestamp) >= maxTicks)
            {
                break;
            }
        }

        if (_unitIndexInCycle >= ownUnits.Length)
        {
            EndCycle();
        }
    }

    public void UpdateAllNow(ClientReplica replica, UnitCatalog catalog)
    {
        ReadOnlySpan<OwnUnitObservation> ownUnits = replica.OwnUnits;
        BeginCycle(ownUnits);
        for (int i = 0; i < ownUnits.Length; i++)
        {
            ProcessUnit(ownUnits[i], catalog);
        }
        EndCycle();
    }

    // Test helper: the view of a single observer, replacing everything seen before.
    public void UpdateUnitNow(NumericsVector2 position, float eyeHeight, float sensorRange)
    {
        foreach (UnitViewshed viewshed in _viewsheds.Values)
        {
            RemoveContribution(viewshed);
        }
        _viewsheds.Clear();
        _cycleInProgress = false;

        UnitViewshed probe = new() { Position = position, Present = true };
        _sweepTarget = probe;
        SweepUnitViewshed(position, eyeHeight, sensorRange);
        _sweepTarget = null;
        AddContribution(probe);
        _viewsheds[default] = probe;
        PublishVisibility();
    }

    private void BeginCycle(ReadOnlySpan<OwnUnitObservation> ownUnits)
    {
        foreach (UnitViewshed viewshed in _viewsheds.Values)
        {
            viewshed.Present = false;
        }
        // Presence is marked for the whole cycle up front, so a unit is never dropped just because
        // the sweeps of this cycle are spread over several frames.
        for (int i = 0; i < ownUnits.Length; i++)
        {
            if (_viewsheds.TryGetValue(ownUnits[i].Id, out UnitViewshed? viewshed))
            {
                viewshed.Present = true;
            }
        }
        _cycleInProgress = true;
        _unitIndexInCycle = 0;
        _sweepsThisCycle = 0;
    }

    private void EndCycle()
    {
        _cycleInProgress = false;
        _removedUnits.Clear();
        foreach (KeyValuePair<EntityId, UnitViewshed> pair in _viewsheds)
        {
            if (!pair.Value.Present)
            {
                _removedUnits.Add(pair.Key);
            }
        }
        for (int i = 0; i < _removedUnits.Count; i++)
        {
            RemoveContribution(_viewsheds[_removedUnits[i]]);
            _viewsheds.Remove(_removedUnits[i]);
        }

        SweepsInLastCycle = _sweepsThisCycle;
        if (_sweepsThisCycle > 0 || _removedUnits.Count > 0)
        {
            PublishVisibility();
        }
    }

    // Returns true when the unit was swept, false when its cached viewshed was still valid.
    private bool ProcessUnit(in OwnUnitObservation u, UnitCatalog catalog)
    {
        if (!_viewsheds.TryGetValue(u.Id, out UnitViewshed? viewshed))
        {
            viewshed = new UnitViewshed();
            _viewsheds.Add(u.Id, viewshed);
        }
        viewshed.Present = true;

        if (viewshed.Swept && viewshed.Definition == u.Definition &&
            NumericsVector2.DistanceSquared(viewshed.Position, u.Position) < ResweepDistance * ResweepDistance)
        {
            return false;
        }

        float eyeHeight = 1.8f;
        float sensorRange = 60.0f;
        if (u.Definition < catalog.Units.Length)
        {
            UnitDefinition def = catalog.Units[u.Definition];
            eyeHeight = def.EyeHeight;
            sensorRange = def.SensorRange;
        }

        RemoveContribution(viewshed);
        _sweepTarget = viewshed;
        SweepUnitViewshed(u.Position, eyeHeight, sensorRange);
        _sweepTarget = null;
        AddContribution(viewshed);
        viewshed.Position = u.Position;
        viewshed.Definition = u.Definition;
        viewshed.Swept = true;
        _sweepsThisCycle++;
        return true;
    }

    private void AddContribution(UnitViewshed viewshed)
    {
        for (int i = 0; i < viewshed.CellCount; i++)
        {
            _seenCount[viewshed.Cells[i]]++;
        }
    }

    private void RemoveContribution(UnitViewshed viewshed)
    {
        for (int i = 0; i < viewshed.CellCount; i++)
        {
            _seenCount[viewshed.Cells[i]]--;
        }
        viewshed.CellCount = 0;
    }

    private void MarkSeen(int cellIdx)
    {
        UnitViewshed target = _sweepTarget!;
        if (target.CellCount == target.Cells.Length)
        {
            // Grows only while a unit first sees more cells than before; steady state reuses it.
            Array.Resize(ref target.Cells, Math.Max(256, target.Cells.Length * 2));
        }
        target.Cells[target.CellCount++] = cellIdx;
    }

    private void SweepUnitViewshed(NumericsVector2 unitPos, float eyeHeight, float sensorRange)
    {
        float x0 = unitPos.X;
        float z0 = unitPos.Y;
        float groundY0 = _map.GetHeight(x0, z0);
        float y0 = groundY0 + eyeHeight;

        int cx0 = (int)MathF.Floor(x0 / CellSize);
        int cz0 = (int)MathF.Floor(z0 / CellSize);

        int maxR = (int)MathF.Ceiling(sensorRange / CellSize);
        float rangeSq = sensorRange * sensorRange;

        int minBx = Math.Max(0, cx0 - maxR);
        int maxBx = Math.Min(Columns - 1, cx0 + maxR);
        int minBz = Math.Max(0, cz0 - maxR);
        int maxBz = Math.Min(Rows - 1, cz0 + maxR);

        int spanX = maxBx - minBx + 1;
        for (int cz = minBz; cz <= maxBz; cz++)
        {
            Array.Fill(_horizonBuffer, -1000f, cz * Columns + minBx, spanX);
        }

        if (cx0 >= 0 && cx0 < Columns && cz0 >= 0 && cz0 < Rows)
        {
            MarkSeen(cz0 * Columns + cx0);
        }

        for (int r = 1; r <= maxR; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                ProcessCell(cx0, cz0, dx, -r, r, x0, z0, y0, rangeSq);
                ProcessCell(cx0, cz0, dx, r, r, x0, z0, y0, rangeSq);
            }
            for (int dz = -(r - 1); dz <= r - 1; dz++)
            {
                ProcessCell(cx0, cz0, -r, dz, r, x0, z0, y0, rangeSq);
                ProcessCell(cx0, cz0, r, dz, r, x0, z0, y0, rangeSq);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessCell(int cx0, int cz0, int dx, int dz, int r, float x0, float z0, float y0, float rangeSq)
    {
        int cx = cx0 + dx;
        int cz = cz0 + dz;
        if (cx < 0 || cx >= Columns || cz < 0 || cz >= Rows)
        {
            return;
        }

        float diffX = _cellCenterX[cx] - x0;
        float diffZ = _cellCenterZ[cz] - z0;
        float distSq = diffX * diffX + diffZ * diffZ;
        if (distSq > rangeSq)
        {
            return;
        }

        float invDist = 1.0f / MathF.Sqrt(distSq > 1e-4f ? distSq : 1e-4f);

        float hInterp;
        if (r == 1)
        {
            hInterp = -1000f;
        }
        else
        {
            int absDx = Math.Abs(dx);
            int absDz = Math.Abs(dz);
            if (absDx >= absDz)
            {
                int sx = dx > 0 ? 1 : -1;
                int sz = dz > 0 ? 1 : (dz < 0 ? -1 : 0);
                float t = (float)absDz / absDx;

                int cxA = cx - sx;
                int czA = cz;
                int cxB = cx - sx;
                int czB = cz - sz;

                float hA = GetHorizon(cxA, czA);
                float hB = GetHorizon(cxB, czB);
                hInterp = (1.0f - t) * hA + t * hB;
            }
            else
            {
                int sz = dz > 0 ? 1 : -1;
                int sx = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
                float t = (float)absDx / absDz;

                int cxA = cx;
                int czA = cz - sz;
                int cxB = cx - sx;
                int czB = cz - sz;

                float hA = GetHorizon(cxA, czA);
                float hB = GetHorizon(cxB, czB);
                hInterp = (1.0f - t) * hA + t * hB;
            }
        }

        int cellIdx = cz * Columns + cx;
        float yGround = _cellGroundY[cellIdx];
        float yBlock = _cellBlockY[cellIdx];
        float tGround = (yGround - y0) * invDist;
        float tBlock = (yBlock - y0) * invDist;

        float tVisible = Math.Max(tGround, tBlock);
        if (tVisible >= hInterp - 1e-4f)
        {
            MarkSeen(cellIdx);
        }

        _horizonBuffer[cellIdx] = Math.Max(hInterp, tBlock);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float GetHorizon(int cx, int cz)
    {
        if (cx < 0 || cx >= Columns || cz < 0 || cz >= Rows) return -1000f;
        return _horizonBuffer[cz * Columns + cx];
    }

    private void PublishVisibility()
    {
        for (int i = 0; i < _seenCount.Length; i++)
        {
            _scratchVisibility[i] = _seenCount[i] > 0 ? (byte)255 : (byte)0;
        }
        Array.Copy(_scratchVisibility, _activeVisibility, _activeVisibility.Length);

        int cols = Columns;
        int rows = Rows;
        for (int z = 0; z < rows; z++)
        {
            int rowOffset = z * cols;
            for (int x = 0; x < cols; x++)
            {
                int left = x > 0 ? _scratchVisibility[rowOffset + x - 1] : _scratchVisibility[rowOffset + x];
                int center = _scratchVisibility[rowOffset + x];
                int right = x < cols - 1 ? _scratchVisibility[rowOffset + x + 1] : _scratchVisibility[rowOffset + x];
                _blurBuffer[rowOffset + x] = (byte)((left + (center << 1) + right) >> 2);
            }
        }

        for (int z = 0; z < rows; z++)
        {
            int rowOffset = z * cols;
            int prevRowOffset = z > 0 ? (z - 1) * cols : rowOffset;
            int nextRowOffset = z < rows - 1 ? (z + 1) * cols : rowOffset;
            for (int x = 0; x < cols; x++)
            {
                int up = _blurBuffer[prevRowOffset + x];
                int center = _blurBuffer[rowOffset + x];
                int down = _blurBuffer[nextRowOffset + x];
                _textureBuffer[rowOffset + x] = (byte)((up + (center << 1) + down) >> 2);
            }
        }

        _image.SetData(cols, rows, false, Image.Format.R8, _textureBuffer);
        _texture.Update(_image);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _material.Dispose();
        _shader.Dispose();
        _texture.Dispose();
        _image.Dispose();
    }
}
