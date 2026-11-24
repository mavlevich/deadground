using System.Diagnostics;
using System.Runtime.CompilerServices;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;
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
render_mode cull_back, diffuse_burley, specular_disabled;

uniform sampler2D fog_texture : filter_linear, repeat_disable;
uniform vec2 map_size;
uniform bool fog_enabled = true;
uniform vec3 terrain_tint = vec3(1.0, 1.0, 1.0);

varying vec2 terrain_uv;

void vertex() {
    terrain_uv = VERTEX.xz / map_size;
}

void fragment() {
    vec3 base_color = COLOR.rgb * terrain_tint;
    if (fog_enabled) {
        float vis = texture(fog_texture, clamp(terrain_uv, vec2(0.0), vec2(1.0))).r;
        float gray = dot(base_color, vec3(0.299, 0.587, 0.114));
        vec3 desaturated = mix(base_color, vec3(gray), 0.35);
        vec3 unseen_color = desaturated * 0.45;
        ALBEDO = mix(unseen_color, base_color, vis);
    } else {
        ALBEDO = base_color;
    }
    ROUGHNESS = 0.95;
    SPECULAR = 0.0;
}
";

    public const float CycleInterval = 0.25f;
    public const float FrameTimeBudgetMs = 1.5f;
    // A unit that moved less than this since its last sweep keeps its cached viewshed.
    public const float ResweepDistance = 2.0f;

    // The cells one own unit saw at its last sweep. Units that do not move are not swept again,
    // so a large force that holds its ground costs nothing per cycle.
    private sealed class UnitViewshed
    {
        public NumericsVector2 Position;
        public float Altitude;
        public ushort Definition;
        public int[] Cells = [];
        public int CellCount;
        public bool Present;
        public bool Swept;
    }

    private readonly HeightMap _map;
    private readonly StaticGeometry? _geometry;
    private readonly byte[] _activeVisibility;
    // How many own units see each cell; a cell is seen while its count is above zero.
    private readonly ushort[] _seenCount;
    private readonly Dictionary<EntityId, UnitViewshed> _viewsheds = new();
    private readonly List<EntityId> _removedUnits = new();
    private int[] _pendingSweepCells;
    private int _pendingSweepCellCount;
    private bool _unitSweepInProgress;
    private UnitViewshed? _activeSweepViewshed;
    private NumericsVector2 _activeSweepPos;
    private float _activeSweepAltitude;
    private ushort _activeSweepDef;
    private float _activeSweepX0;
    private float _activeSweepZ0;
    private float _activeSweepY0;
    private float _activeSweepRangeSq;
    private int _activeSweepCx0;
    private int _activeSweepCz0;
    private int _activeSweepMaxR;
    private int _activeSweepNextR;
    private bool _publishPending;
    private int _sweepsThisCycle;
    private readonly byte[] _blurBuffer;
    private readonly byte[] _textureBuffer;
    private readonly float[] _horizonBuffer;
    private readonly float[] _structureTops;
    private readonly float[]? _structureMaxYOverrides;
    private bool _structureHeightDirty;
    private readonly float[] _cellGroundY;
    private readonly float[] _cellBlockY;
    private readonly float[] _cellCenterX;
    private readonly float[] _cellCenterZ;
    private readonly bool _hasVegetation;
    private readonly float[] _cellVegDensity;
    private readonly float[] _cellVegCanopyTopY;
    private readonly float[] _vegTauBuffer;
    private readonly float[] _vegCanopySlopeBuffer;
    private readonly Image _image;
    private readonly ImageTexture _texture;
    private readonly Shader _shader;
    private readonly ShaderMaterial _material;
    private readonly SmokeVolume[] _smokeVolumes = new SmokeVolume[WorldState.MaxSmokeClouds];
    private readonly SmokeVolume[] _unitSmokeVolumes = new SmokeVolume[WorldState.MaxSmokeClouds];
    private int _smokeVolumeCount;
    private int _unitSmokeCount;
    private float _unitSmokeMinDistSq;
    private ulong _unitSmokeSectorMask;
    private ulong _lastSmokeSignature;

    private float _timeSinceCycleStart;
    private int _unitIndexInCycle;
    private bool _cycleInProgress;
    private bool _isDisposed;

    public float CellSize { get; }
    public int Columns { get; }
    public int Rows { get; }
    public bool IsEnabled { get; private set; } = true;
    public bool IsCalculationSuspended { get; set; }
    public ShaderMaterial Material => _material;
    public ImageTexture Texture => _texture;
    internal bool IsCycleInProgress => _cycleInProgress;
    // Units swept in the last completed cycle; stationary units are not counted.
    internal int SweepsInLastCycle { get; private set; }

    public FogOfWarView(HeightMap map, StaticGeometry? geometry = null, float? customCellSize = null, bool useLambertDiffuse = true)
    {
        _map = map;
        _geometry = geometry;

        CellSize = customCellSize ?? (Math.Max(map.Width, map.Depth) > 500f ? 20.0f : 4.0f);
        Columns = (int)MathF.Ceiling(map.Width / CellSize);
        Rows = (int)MathF.Ceiling(map.Depth / CellSize);

        int totalCells = Columns * Rows;
        _activeVisibility = new byte[totalCells];
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
        _pendingSweepCells = new int[totalCells];

        if (geometry is not null)
        {
            var structures = geometry.Structures;
            int count = structures.Count;
            _structureMaxYOverrides = count > 0 ? new float[count] : null;
            if (_structureMaxYOverrides is not null)
            {
                Array.Fill(_structureMaxYOverrides, float.NaN);
            }
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

        if (geometry is not null && geometry.VegetationZones.Count > 0)
        {
            _hasVegetation = true;
            _cellVegDensity = new float[totalCells];
            _cellVegCanopyTopY = new float[totalCells];
            _vegTauBuffer = new float[totalCells];
            _vegCanopySlopeBuffer = new float[totalCells];
            BakeVegetationGrid(geometry);
        }
        else
        {
            _hasVegetation = false;
            _cellVegDensity = [];
            _cellVegCanopyTopY = [];
            _vegTauBuffer = [];
            _vegCanopySlopeBuffer = [];
        }

        _image = Image.CreateEmpty(Columns, Rows, false, Image.Format.R8);
        _texture = ImageTexture.CreateFromImage(_image);

        string shaderCode = useLambertDiffuse
            ? TerrainShaderCode.Replace("diffuse_burley", "diffuse_lambert", StringComparison.Ordinal)
            : TerrainShaderCode;
        _shader = new Shader { Code = shaderCode };
        _material = new ShaderMaterial { Shader = _shader };
        _material.SetShaderParameter("fog_texture", _texture);
        _material.SetShaderParameter("map_size", new Vector2(map.Width, map.Depth));
        _material.SetShaderParameter("fog_enabled", IsEnabled);
        _material.SetShaderParameter("terrain_tint", new Vector3(1f, 1f, 1f));
    }

    public void AttachToTerrain(MeshInstance3D terrain)
    {
        terrain.MaterialOverride = _material;
    }

    public void SetTerrainTint(Color tint)
    {
        _material.SetShaderParameter("terrain_tint", new Vector3(tint.R, tint.G, tint.B));
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
        if (IsCalculationSuspended)
        {
            return;
        }

        ReadOnlySpan<OwnUnitObservation> ownUnits = replica.OwnUnits;
        SyncSmokeVolumes(replica);
        _timeSinceCycleStart += delta;

        if (_publishPending)
        {
            _publishPending = false;
            EndCycle();
            if (_structureHeightDirty)
            {
                FlushStructureHeightDirty();
            }
            return;
        }

        if (!_cycleInProgress)
        {
            if (_structureHeightDirty)
            {
                FlushStructureHeightDirty();
            }
            if (_timeSinceCycleStart < CycleInterval)
            {
                return;
            }
            _timeSinceCycleStart = 0f;
            BeginCycle(ownUnits);
        }

        long startTimestamp = Stopwatch.GetTimestamp();
        long maxTicks = (long)(FrameTimeBudgetMs * Stopwatch.Frequency / 1000.0);

        if (_unitSweepInProgress)
        {
            bool completed = ContinueActiveUnitSweep(startTimestamp, maxTicks);
            if (!completed)
            {
                return;
            }
            _unitIndexInCycle++;
            if ((Stopwatch.GetTimestamp() - startTimestamp) >= maxTicks)
            {
                if (_unitIndexInCycle >= ownUnits.Length)
                {
                    _publishPending = true;
                }
                return;
            }
        }

        while (_unitIndexInCycle < ownUnits.Length)
        {
            bool startedOrSwept = ProcessUnitBudgeted(ownUnits[_unitIndexInCycle], catalog, startTimestamp, maxTicks, out bool completed);
            if (!completed)
            {
                return;
            }
            _unitIndexInCycle++;
            if (startedOrSwept && (Stopwatch.GetTimestamp() - startTimestamp) >= maxTicks)
            {
                if (_unitIndexInCycle >= ownUnits.Length)
                {
                    _publishPending = true;
                }
                return;
            }
        }

        if (_unitIndexInCycle >= ownUnits.Length)
        {
            EndCycle();
        }
    }

    public void UpdateAllNow(ClientReplica replica, UnitCatalog catalog)
    {
        CancelInProgressSweep();
        _structureHeightDirty = false;
        SyncSmokeVolumes(replica);
        ReadOnlySpan<OwnUnitObservation> ownUnits = replica.OwnUnits;
        BeginCycle(ownUnits);
        for (int i = 0; i < ownUnits.Length; i++)
        {
            ProcessUnitBudgeted(ownUnits[i], catalog, 0L, long.MaxValue, out _);
        }
        EndCycle();
    }

    private bool SyncSmokeVolumes(ClientReplica replica)
    {
        ReadOnlySpan<SmokeCloudObservation> clouds = replica.SmokeClouds;
        int tick = replica.LatestTick;
        int outCount = 0;
        ulong sig = 14695981039346656037UL;

        for (int i = 0; i < clouds.Length && outCount < _smokeVolumes.Length; i++)
        {
            ref readonly SmokeCloudObservation c = ref clouds[i];
            c.EvaluateAtTick(tick, out float effectiveRadius, out float effectiveDensity);
            if (effectiveRadius <= 0f || effectiveDensity <= 0f)
            {
                continue;
            }

            float baseY = _geometry is not null
                ? _geometry.GetSurfaceHeight(c.Position, c.SupportSurface)
                : _map.GetHeight(c.Position.X, c.Position.Y);

            _smokeVolumes[outCount++] = new SmokeVolume(
                c.Position.X,
                c.Position.Y,
                baseY - 0.25f,
                baseY + c.Height,
                effectiveRadius,
                effectiveDensity,
                c.Id);

            float diameterTau = 2.0f * effectiveRadius * effectiveDensity;
            ulong opaqueBit = diameterTau >= SmokeLos.BlockingOpticalDepth ? 1UL : 0UL;

            sig ^= c.Id.Value;
            sig *= 1099511628211UL;
            sig ^= (ulong)(int)MathF.Floor(effectiveRadius);
            sig *= 1099511628211UL;
            sig ^= (ulong)(int)MathF.Floor(effectiveDensity * 100.0f);
            sig *= 1099511628211UL;
            sig ^= (ulong)(int)MathF.Floor(diameterTau * 4.0f);
            sig *= 1099511628211UL;
            sig ^= opaqueBit;
            sig *= 1099511628211UL;
        }

        _smokeVolumeCount = outCount;
        sig ^= (ulong)outCount;
        sig *= 1099511628211UL;

        if (sig != _lastSmokeSignature)
        {
            _lastSmokeSignature = sig;
            foreach (UnitViewshed viewshed in _viewsheds.Values)
            {
                viewshed.Swept = false;
            }
            if (_unitSweepInProgress || _publishPending)
            {
                CancelInProgressSweep();
                _cycleInProgress = false;
                _timeSinceCycleStart = CycleInterval;
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// Updates the effective top elevation of a destructible structure on the Fog of War blocking grid.
    /// Recomputes only the cells overlapped by that structure's AABB (zero allocations) and invalidates
    /// cached observer viewsheds so sight opens promptly once buildings collapse.
    /// </summary>
    public bool UpdateStructureHeight(StructureId structureId, float newMaxY)
    {
        if (_isDisposed || _geometry is null || _structureMaxYOverrides is null || !float.IsFinite(newMaxY))
            return false;
        if (!_geometry.TryGetStructureIndex(structureId, out int structIndex))
            return false;

        OpaqueStructure target = _geometry.Structures[structIndex];
        float clampedMaxY = Math.Clamp(newMaxY, target.MinY, target.MaxY);
        float prevMaxY = float.IsNaN(_structureMaxYOverrides[structIndex])
            ? target.MaxY
            : _structureMaxYOverrides[structIndex];
        if (MathF.Abs(prevMaxY - clampedMaxY) < 0.01f)
            return false;

        _structureMaxYOverrides[structIndex] = clampedMaxY;

        int minCx = Math.Clamp((int)MathF.Floor(target.MinX / CellSize), 0, Columns - 1);
        int maxCx = Math.Clamp((int)MathF.Floor((target.MaxX - 1e-4f) / CellSize), 0, Columns - 1);
        int minCz = Math.Clamp((int)MathF.Floor(target.MinZ / CellSize), 0, Rows - 1);
        int maxCz = Math.Clamp((int)MathF.Floor((target.MaxZ - 1e-4f) / CellSize), 0, Rows - 1);

        var structures = _geometry.Structures;
        int totalStructures = structures.Count;
        for (int cz = minCz; cz <= maxCz; cz++)
        {
            float cellMinZ = cz * CellSize;
            float cellMaxZ = cellMinZ + CellSize;
            int rowOffset = cz * Columns;
            for (int cx = minCx; cx <= maxCx; cx++)
            {
                float cellMinX = cx * CellSize;
                float cellMaxX = cellMinX + CellSize;
                float maxTop = 0f;
                for (int i = 0; i < totalStructures; i++)
                {
                    OpaqueStructure s = structures[i];
                    if (s.MaxX <= cellMinX || s.MinX >= cellMaxX || s.MaxZ <= cellMinZ || s.MinZ >= cellMaxZ)
                        continue;
                    float effectiveMaxY = float.IsNaN(_structureMaxYOverrides[i]) ? s.MaxY : _structureMaxYOverrides[i];
                    if (effectiveMaxY > maxTop)
                    {
                        maxTop = effectiveMaxY;
                    }
                }

                int idx = rowOffset + cx;
                _structureTops[idx] = maxTop;
                float gy = _cellGroundY[idx];
                _cellBlockY[idx] = maxTop > 0f ? Math.Max(gy, maxTop) : gy;
            }
        }

        // Coalesce multiple height updates within a frame or during an active barrage instead of
        // cancelling an in-flight sweep and starving publication. FlushStructureHeightDirty() triggers
        // an immediate resweep at the next cycle boundary.
        _structureHeightDirty = true;

        return true;
    }

    private void FlushStructureHeightDirty()
    {
        _structureHeightDirty = false;
        foreach (UnitViewshed viewshed in _viewsheds.Values)
        {
            viewshed.Swept = false;
        }
        _timeSinceCycleStart = CycleInterval;
    }

    // Test helper: the view of a single observer, replacing everything seen before.
    public void UpdateUnitNow(NumericsVector2 position, float eyeHeight, float sensorRange)
    {
        CancelInProgressSweep();
        _structureHeightDirty = false;
        foreach (UnitViewshed viewshed in _viewsheds.Values)
        {
            RemoveContribution(viewshed);
        }
        _viewsheds.Clear();
        _cycleInProgress = false;

        UnitViewshed probe = new() { Position = position, Present = true };
        StartUnitSweep(probe, position, 0, eyeHeight, sensorRange);
        ContinueActiveUnitSweep(0L, long.MaxValue);
        _viewsheds[default] = probe;
        PublishVisibility();
    }

    private void CancelInProgressSweep()
    {
        _unitSweepInProgress = false;
        _activeSweepViewshed = null;
        _pendingSweepCellCount = 0;
        _publishPending = false;
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
            if (ownUnits[i].ContainerId == default &&
                ownUnits[i].FlightPhase != FlightPhase.Crashing &&
                _viewsheds.TryGetValue(ownUnits[i].Id, out UnitViewshed? viewshed))
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

    // Returns true when the unit needed a sweep (completed or in progress), false when cached.
    private bool ProcessUnitBudgeted(
        in OwnUnitObservation u,
        UnitCatalog catalog,
        long startTimestamp,
        long maxTicks,
        out bool completed)
    {
        // Passengers inside a transport and crashing aircraft do not observe independently (matching DetectionSystem).
        if (u.ContainerId != default || u.FlightPhase == FlightPhase.Crashing)
        {
            completed = true;
            return false;
        }

        if (!_viewsheds.TryGetValue(u.Id, out UnitViewshed? viewshed))
        {
            viewshed = new UnitViewshed();
            _viewsheds.Add(u.Id, viewshed);
        }
        viewshed.Present = true;

        float altitude = MathF.Max(0f, u.Altitude);
        if (u.GarrisonStructureId != default &&
            _geometry is not null &&
            _geometry.TryGetStructureIndex(u.GarrisonStructureId, out int gIdx))
        {
            OpaqueStructure s = _geometry.Structures[gIdx];
            float effectiveMaxY = _structureMaxYOverrides is not null && !float.IsNaN(_structureMaxYOverrides[gIdx])
                ? _structureMaxYOverrides[gIdx]
                : s.MaxY;
            float groundY = _map.GetHeight(u.Position.X, u.Position.Y);
            float roofBonus = u.GarrisonSlotKind == GarrisonSlotKind.Roof ? 1.15f : 0.35f;
            altitude = MathF.Max(altitude, (effectiveMaxY - groundY) + roofBonus);
        }
        if (viewshed.Swept && viewshed.Definition == u.Definition &&
            NumericsVector2.DistanceSquared(viewshed.Position, u.Position) < ResweepDistance * ResweepDistance &&
            MathF.Abs(viewshed.Altitude - altitude) < ResweepDistance)
        {
            completed = true;
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

        StartUnitSweep(viewshed, u.Position, u.Definition, eyeHeight + altitude, sensorRange, altitude);
        completed = ContinueActiveUnitSweep(startTimestamp, maxTicks);
        return true;
    }

    private void StartUnitSweep(
        UnitViewshed viewshed,
        NumericsVector2 unitPos,
        ushort definition,
        float eyeHeight,
        float sensorRange,
        float altitude = 0f)
    {
        float x0 = unitPos.X;
        float z0 = unitPos.Y;
        float groundY0 = _map.GetHeight(x0, z0);
        float y0 = groundY0 + eyeHeight;

        PrepareUnitSmokeFilter(x0, z0, sensorRange);

        int cx0 = (int)MathF.Floor(x0 / CellSize);
        int cz0 = (int)MathF.Floor(z0 / CellSize);
        int maxRangeR = (int)MathF.Ceiling(sensorRange / CellSize);
        int maxMapR = Math.Max(
            Math.Max(Math.Max(cx0, Columns - 1 - cx0), Math.Max(cz0, Rows - 1 - cz0)),
            0);
        int maxR = Math.Min(maxRangeR, maxMapR);
        float rangeSq = sensorRange * sensorRange;

        int minBx = Math.Max(0, cx0 - maxR);
        int maxBx = Math.Min(Columns - 1, cx0 + maxR);
        int minBz = Math.Max(0, cz0 - maxR);
        int maxBz = Math.Min(Rows - 1, cz0 + maxR);

        int spanX = maxBx - minBx + 1;
        if (spanX > 0)
        {
            for (int cz = minBz; cz <= maxBz; cz++)
            {
                int rowStart = cz * Columns + minBx;
                Array.Fill(_horizonBuffer, -1000f, rowStart, spanX);
                if (_hasVegetation)
                {
                    Array.Fill(_vegTauBuffer, 0f, rowStart, spanX);
                    Array.Fill(_vegCanopySlopeBuffer, -1000f, rowStart, spanX);
                }
            }
        }

        _pendingSweepCellCount = 0;
        if (cx0 >= 0 && cx0 < Columns && cz0 >= 0 && cz0 < Rows)
        {
            int startIdx = cz0 * Columns + cx0;
            _pendingSweepCells[_pendingSweepCellCount++] = startIdx;
            if (_hasVegetation && _cellVegDensity[startIdx] > 0f)
            {
                _vegCanopySlopeBuffer[startIdx] = (_cellVegCanopyTopY[startIdx] - y0) / MathF.Max(1f, CellSize * 0.5f);
            }
        }

        _activeSweepViewshed = viewshed;
        _activeSweepPos = unitPos;
        _activeSweepAltitude = altitude;
        _activeSweepDef = definition;
        _activeSweepX0 = x0;
        _activeSweepZ0 = z0;
        _activeSweepY0 = y0;
        _activeSweepRangeSq = rangeSq;
        _activeSweepCx0 = cx0;
        _activeSweepCz0 = cz0;
        _activeSweepMaxR = maxR;
        _activeSweepNextR = 1;
        _unitSweepInProgress = true;
    }

    private bool ContinueActiveUnitSweep(long startTimestamp, long maxTicks)
    {
        int r = _activeSweepNextR;
        int maxR = _activeSweepMaxR;
        bool checkBudget = maxTicks != long.MaxValue;
        int ringsThisCall = 0;

        while (r <= maxR)
        {
            SweepRing(r);
            r++;
            ringsThisCall++;

            if (checkBudget && r <= maxR && (ringsThisCall & 3) == 0 &&
                (Stopwatch.GetTimestamp() - startTimestamp) >= maxTicks)
            {
                _activeSweepNextR = r;
                return false;
            }
        }

        _activeSweepNextR = r;
        CommitActiveUnitSweep();
        return true;
    }

    private void CommitActiveUnitSweep()
    {
        UnitViewshed viewshed = _activeSweepViewshed!;
        RemoveContribution(viewshed);

        int count = _pendingSweepCellCount;
        if (viewshed.Cells.Length < count)
        {
            int newCapacity = Math.Max(256, viewshed.Cells.Length);
            while (newCapacity < count)
            {
                newCapacity *= 2;
            }
            viewshed.Cells = new int[newCapacity];
        }

        Array.Copy(_pendingSweepCells, viewshed.Cells, count);
        viewshed.CellCount = count;
        AddContribution(viewshed);

        viewshed.Position = _activeSweepPos;
        viewshed.Altitude = _activeSweepAltitude;
        viewshed.Definition = _activeSweepDef;
        viewshed.Swept = true;
        _sweepsThisCycle++;

        _unitSweepInProgress = false;
        _activeSweepViewshed = null;
        _pendingSweepCellCount = 0;
    }

    private void AddContribution(UnitViewshed viewshed)
    {
        ushort[] seenCount = _seenCount;
        int[] cells = viewshed.Cells;
        int count = viewshed.CellCount;
        for (int i = 0; i < count; i++)
        {
            seenCount[cells[i]]++;
        }
    }

    private void RemoveContribution(UnitViewshed viewshed)
    {
        ushort[] seenCount = _seenCount;
        int[] cells = viewshed.Cells;
        int count = viewshed.CellCount;
        for (int i = 0; i < count; i++)
        {
            seenCount[cells[i]]--;
        }
        viewshed.CellCount = 0;
    }

    private void SweepRing(int r)
    {
        SweepHorizontalEdge(r, -r);
        SweepHorizontalEdge(r, r);
        SweepVerticalEdge(r, -r);
        SweepVerticalEdge(r, r);
    }

    private void SweepHorizontalEdge(int r, int dz)
    {
        int cols = Columns;
        int rows = Rows;
        int cx0 = _activeSweepCx0;
        int cz0 = _activeSweepCz0;
        int cz = cz0 + dz;
        if ((uint)cz >= (uint)rows)
        {
            return;
        }

        int minDx = Math.Max(-(r - 1), -cx0);
        int maxDx = Math.Min(r - 1, cols - 1 - cx0);
        if (minDx > maxDx)
        {
            return;
        }

        float x0 = _activeSweepX0;
        float z0 = _activeSweepZ0;
        float y0 = _activeSweepY0;
        float rangeSq = _activeSweepRangeSq;
        float invR = 1.0f / r;

        float[] cellCenterX = _cellCenterX;
        float[] cellGroundY = _cellGroundY;
        float[] cellBlockY = _cellBlockY;
        float[] horizon = _horizonBuffer;
        bool hasVeg = _hasVegetation;
        float[] vegDensity = _cellVegDensity;
        float[] vegCanopyTopY = _cellVegCanopyTopY;
        float[] vegTau = _vegTauBuffer;
        float[] vegCanopySlope = _vegCanopySlopeBuffer;
        int[] pendingCells = _pendingSweepCells;
        int pendingCount = _pendingSweepCellCount;

        int unitSmokeCount = _unitSmokeCount;
        float unitSmokeMinDistSq = _unitSmokeMinDistSq;
        ulong unitSmokeSectorMask = _unitSmokeSectorMask;
        NumericsVector3 eyePt = new(x0, y0, z0);

        float diffZ = _cellCenterZ[cz] - z0;
        float diffZSq = diffZ * diffZ;
        if (diffZSq > rangeSq)
        {
            return;
        }

        int rowOffset = cz * cols;
        int sz = dz > 0 ? 1 : -1;
        int czPrev = cz - sz;
        bool prevRowValid = (uint)czPrev < (uint)rows;
        int prevRowOffset = czPrev * cols;

        bool startCellValid = (uint)cx0 < (uint)cols && (uint)cz0 < (uint)rows;
        float startDens = hasVeg && startCellValid ? vegDensity[cz0 * cols + cx0] : 0f;
        float startCanopySlope = hasVeg && startCellValid ? vegCanopySlope[cz0 * cols + cx0] : -1000f;

        for (int dx = minDx; dx <= maxDx; dx++)
        {
            int cx = cx0 + dx;
            float diffX = cellCenterX[cx] - x0;
            float distSq = diffX * diffX + diffZSq;
            if (distSq > rangeSq)
            {
                continue;
            }

            float invDist = 1.0f / MathF.Sqrt(distSq > 1e-4f ? distSq : 1e-4f);
            int cellIdx = rowOffset + cx;

            float hInterp;
            float interpT = 0f;
            int idxA = 0;
            int idxB = 0;
            bool aValid = false;
            bool bValid = false;

            if (r == 1)
            {
                hInterp = -1000f;
            }
            else
            {
                int absDx = dx < 0 ? -dx : dx;
                int sx = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
                interpT = absDx * invR;
                int cxB = cx - sx;

                aValid = prevRowValid;
                bValid = prevRowValid && (uint)cxB < (uint)cols;
                idxA = prevRowOffset + cx;
                idxB = prevRowOffset + cxB;

                float hA = aValid ? horizon[idxA] : -1000f;
                float hB = bValid ? horizon[idxB] : -1000f;
                hInterp = (1.0f - interpT) * hA + interpT * hB;
            }

            float yGround = cellGroundY[cellIdx];
            float yBlock = cellBlockY[cellIdx];
            float tGround = (yGround - y0) * invDist;
            float tBlock = (yBlock - y0) * invDist;
            float tVisible = tGround > tBlock ? tGround : tBlock;

            bool vegClear = true;
            if (hasVeg)
            {
                float cellDens = vegDensity[cellIdx];
                float dist = distSq * invDist;
                float tauHere;
                float canopySlope;

                if (r == 1)
                {
                    tauHere = 0.5f * (startDens + cellDens) * dist;
                    canopySlope = startCanopySlope;
                }
                else
                {
                    float tauA = aValid ? vegTau[idxA] : 0f;
                    float tauB = bValid ? vegTau[idxB] : 0f;
                    float tauPrev = (1.0f - interpT) * tauA + interpT * tauB;
                    float densA = aValid ? vegDensity[idxA] : 0f;
                    float densB = bValid ? vegDensity[idxB] : 0f;
                    float prevDens = (1.0f - interpT) * densA + interpT * densB;
                    tauHere = tauPrev + 0.5f * (prevDens + cellDens) * (dist * invR);

                    float csA = aValid ? vegCanopySlope[idxA] : -1000f;
                    float csB = bValid ? vegCanopySlope[idxB] : -1000f;
                    canopySlope = csA > csB ? csA : csB;
                }

                if (cellDens > 0f)
                {
                    float tCanopy = (vegCanopyTopY[cellIdx] - y0) * invDist;
                    if (tCanopy > canopySlope)
                    {
                        canopySlope = tCanopy;
                    }
                }

                vegTau[cellIdx] = tauHere;
                vegCanopySlope[cellIdx] = canopySlope;
                vegClear = tauHere <= 1.0f || tVisible > canopySlope;
            }

            if (vegClear && tVisible >= hInterp - 1e-4f)
            {
                bool smokeClear = true;
                if (unitSmokeCount > 0 && distSq >= unitSmokeMinDistSq)
                {
                    if (unitSmokeSectorMask == ulong.MaxValue ||
                        (unitSmokeSectorMask & (1UL << FastSector64(diffX, diffZ))) != 0)
                    {
                        smokeClear = SmokeLos.IsSmokeClear(
                            eyePt,
                            new NumericsVector3(cellCenterX[cx], yGround + 0.5f, _cellCenterZ[cz]),
                            _unitSmokeVolumes.AsSpan(0, unitSmokeCount));
                    }
                }
                if (smokeClear)
                {
                    pendingCells[pendingCount++] = cellIdx;
                }
            }

            horizon[cellIdx] = hInterp > tBlock ? hInterp : tBlock;
        }

        _pendingSweepCellCount = pendingCount;
    }

    private void SweepVerticalEdge(int r, int dx)
    {
        int cols = Columns;
        int rows = Rows;
        int cx0 = _activeSweepCx0;
        int cz0 = _activeSweepCz0;
        int cx = cx0 + dx;
        if ((uint)cx >= (uint)cols)
        {
            return;
        }

        int minDz = Math.Max(-r, -cz0);
        int maxDz = Math.Min(r, rows - 1 - cz0);
        if (minDz > maxDz)
        {
            return;
        }

        float x0 = _activeSweepX0;
        float z0 = _activeSweepZ0;
        float y0 = _activeSweepY0;
        float rangeSq = _activeSweepRangeSq;
        float invR = 1.0f / r;

        float[] cellCenterZ = _cellCenterZ;
        float[] cellGroundY = _cellGroundY;
        float[] cellBlockY = _cellBlockY;
        float[] horizon = _horizonBuffer;
        bool hasVeg = _hasVegetation;
        float[] vegDensity = _cellVegDensity;
        float[] vegCanopyTopY = _cellVegCanopyTopY;
        float[] vegTau = _vegTauBuffer;
        float[] vegCanopySlope = _vegCanopySlopeBuffer;
        int[] pendingCells = _pendingSweepCells;
        int pendingCount = _pendingSweepCellCount;

        int unitSmokeCount = _unitSmokeCount;
        float unitSmokeMinDistSq = _unitSmokeMinDistSq;
        ulong unitSmokeSectorMask = _unitSmokeSectorMask;
        NumericsVector3 eyePt = new(x0, y0, z0);

        float cellX = _cellCenterX[cx];
        float diffX = cellX - x0;
        float diffXSq = diffX * diffX;
        if (diffXSq > rangeSq)
        {
            return;
        }

        int sx = dx > 0 ? 1 : -1;
        int cxPrev = cx - sx;
        bool prevColValid = (uint)cxPrev < (uint)cols;

        bool startCellValid = (uint)cx0 < (uint)cols && (uint)cz0 < (uint)rows;
        float startDens = hasVeg && startCellValid ? vegDensity[cz0 * cols + cx0] : 0f;
        float startCanopySlope = hasVeg && startCellValid ? vegCanopySlope[cz0 * cols + cx0] : -1000f;

        for (int dz = minDz; dz <= maxDz; dz++)
        {
            int cz = cz0 + dz;
            float diffZ = cellCenterZ[cz] - z0;
            float distSq = diffXSq + diffZ * diffZ;
            if (distSq > rangeSq)
            {
                continue;
            }

            float invDist = 1.0f / MathF.Sqrt(distSq > 1e-4f ? distSq : 1e-4f);
            int rowOffset = cz * cols;
            int cellIdx = rowOffset + cx;

            float hInterp;
            float interpT = 0f;
            int idxA = 0;
            int idxB = 0;
            bool aValid = false;
            bool bValid = false;

            if (r == 1)
            {
                hInterp = -1000f;
            }
            else
            {
                int absDz = dz < 0 ? -dz : dz;
                int sz = dz > 0 ? 1 : (dz < 0 ? -1 : 0);
                interpT = absDz * invR;
                int czB = cz - sz;

                aValid = prevColValid;
                bValid = prevColValid && (uint)czB < (uint)rows;
                idxA = rowOffset + cxPrev;
                idxB = czB * cols + cxPrev;

                float hA = aValid ? horizon[idxA] : -1000f;
                float hB = bValid ? horizon[idxB] : -1000f;
                hInterp = (1.0f - interpT) * hA + interpT * hB;
            }

            float yGround = cellGroundY[cellIdx];
            float yBlock = cellBlockY[cellIdx];
            float tGround = (yGround - y0) * invDist;
            float tBlock = (yBlock - y0) * invDist;
            float tVisible = tGround > tBlock ? tGround : tBlock;

            bool vegClear = true;
            if (hasVeg)
            {
                float cellDens = vegDensity[cellIdx];
                float dist = distSq * invDist;
                float tauHere;
                float canopySlope;

                if (r == 1)
                {
                    tauHere = 0.5f * (startDens + cellDens) * dist;
                    canopySlope = startCanopySlope;
                }
                else
                {
                    float tauA = aValid ? vegTau[idxA] : 0f;
                    float tauB = bValid ? vegTau[idxB] : 0f;
                    float tauPrev = (1.0f - interpT) * tauA + interpT * tauB;
                    float densA = aValid ? vegDensity[idxA] : 0f;
                    float densB = bValid ? vegDensity[idxB] : 0f;
                    float prevDens = (1.0f - interpT) * densA + interpT * densB;
                    tauHere = tauPrev + 0.5f * (prevDens + cellDens) * (dist * invR);

                    float csA = aValid ? vegCanopySlope[idxA] : -1000f;
                    float csB = bValid ? vegCanopySlope[idxB] : -1000f;
                    canopySlope = csA > csB ? csA : csB;
                }

                if (cellDens > 0f)
                {
                    float tCanopy = (vegCanopyTopY[cellIdx] - y0) * invDist;
                    if (tCanopy > canopySlope)
                    {
                        canopySlope = tCanopy;
                    }
                }

                vegTau[cellIdx] = tauHere;
                vegCanopySlope[cellIdx] = canopySlope;
                vegClear = tauHere <= 1.0f || tVisible > canopySlope;
            }

            if (vegClear && tVisible >= hInterp - 1e-4f)
            {
                bool smokeClear = true;
                if (unitSmokeCount > 0 && distSq >= unitSmokeMinDistSq)
                {
                    if (unitSmokeSectorMask == ulong.MaxValue ||
                        (unitSmokeSectorMask & (1UL << FastSector64(diffX, diffZ))) != 0)
                    {
                        smokeClear = SmokeLos.IsSmokeClear(
                            eyePt,
                            new NumericsVector3(cellX, yGround + 0.5f, cellCenterZ[cz]),
                            _unitSmokeVolumes.AsSpan(0, unitSmokeCount));
                    }
                }
                if (smokeClear)
                {
                    pendingCells[pendingCount++] = cellIdx;
                }
            }

            horizon[cellIdx] = hInterp > tBlock ? hInterp : tBlock;
        }

        _pendingSweepCellCount = pendingCount;
    }

    private void BakeVegetationGrid(StaticGeometry geometry)
    {
        var zones = geometry.VegetationZones;
        var clearings = geometry.VegetationClearings;
        int zoneCount = zones.Count;
        int clearingCount = clearings.Count;
        float quarterCell = CellSize * 0.25f;
        float threeQuarterCell = CellSize * 0.75f;

        for (int z = 0; z < zoneCount; z++)
        {
            VegetationZone zone = zones[z];
            if (zone.Density <= 0f)
            {
                continue;
            }

            int minCx = Math.Clamp((int)MathF.Floor(zone.MinX / CellSize), 0, Columns - 1);
            int maxCx = Math.Clamp((int)MathF.Floor((zone.MaxX - 1e-4f) / CellSize), 0, Columns - 1);
            int minCz = Math.Clamp((int)MathF.Floor(zone.MinZ / CellSize), 0, Rows - 1);
            int maxCz = Math.Clamp((int)MathF.Floor((zone.MaxZ - 1e-4f) / CellSize), 0, Rows - 1);

            for (int cz = minCz; cz <= maxCz; cz++)
            {
                float cellBaseZ = cz * CellSize;
                int rowOffset = cz * Columns;
                for (int cx = minCx; cx <= maxCx; cx++)
                {
                    float cellBaseX = cx * CellSize;
                    int hits = 0;

                    for (int sy = 0; sy < 2; sy++)
                    {
                        float pz = cellBaseZ + (sy == 0 ? quarterCell : threeQuarterCell);
                        for (int sx = 0; sx < 2; sx++)
                        {
                            float px = cellBaseX + (sx == 0 ? quarterCell : threeQuarterCell);
                            if (!zone.ContainsPoint(new NumericsVector2(px, pz)))
                            {
                                continue;
                            }

                            bool cleared = false;
                            for (int c = 0; c < clearingCount; c++)
                            {
                                VegetationClearing clr = clearings[c];
                                if (px >= clr.MinX && px <= clr.MaxX && pz >= clr.MinZ && pz <= clr.MaxZ &&
                                    clr.ContainsPoint(px, pz))
                                {
                                    cleared = true;
                                    break;
                                }
                            }

                            if (!cleared)
                            {
                                hits++;
                            }
                        }
                    }

                    if (hits > 0)
                    {
                        int idx = rowOffset + cx;
                        _cellVegDensity[idx] += zone.Density * (hits * 0.25f);
                        float canopyTopY = _cellGroundY[idx] + zone.CanopyHeight;
                        if (canopyTopY > _cellVegCanopyTopY[idx])
                        {
                            _cellVegCanopyTopY[idx] = canopyTopY;
                        }
                    }
                }
            }
        }
    }

    private void PrepareUnitSmokeFilter(float x0, float z0, float sensorRange)
    {
        _unitSmokeCount = 0;
        _unitSmokeMinDistSq = float.MaxValue;
        _unitSmokeSectorMask = 0UL;

        if (_smokeVolumeCount == 0)
        {
            return;
        }

        for (int i = 0; i < _smokeVolumeCount; i++)
        {
            ref readonly SmokeVolume v = ref _smokeVolumes[i];
            float dx = v.CenterX - x0;
            float dz = v.CenterZ - z0;
            float distSq = dx * dx + dz * dz;
            float maxReach = sensorRange + v.Radius;
            if (distSq > maxReach * maxReach)
            {
                continue;
            }

            _unitSmokeVolumes[_unitSmokeCount++] = v;

            float dist = MathF.Sqrt(distSq);
            float paddedRadius = v.Radius + 0.25f;
            if (dist <= paddedRadius)
            {
                _unitSmokeMinDistSq = 0f;
                _unitSmokeSectorMask = ulong.MaxValue;
                continue;
            }

            float nearDist = dist - paddedRadius;
            float nearDistSq = nearDist * nearDist;
            if (nearDistSq < _unitSmokeMinDistSq)
            {
                _unitSmokeMinDistSq = nearDistSq;
            }

            if (_unitSmokeSectorMask == ulong.MaxValue)
            {
                continue;
            }

            float centerAngle = MathF.Atan2(dz, dx);
            float sinHalf = Math.Clamp(paddedRadius / dist, 0f, 1f);
            float halfAngle = MathF.Asin(sinHalf);
            float a1 = centerAngle - halfAngle;
            float a2 = centerAngle + halfAngle;

            int s1 = (FastSector64(MathF.Cos(a1), MathF.Sin(a1)) - 1) & 63;
            int s2 = (FastSector64(MathF.Cos(a2), MathF.Sin(a2)) + 1) & 63;
            int s = s1;
            for (int step = 0; step < 64; step++)
            {
                _unitSmokeSectorMask |= 1UL << s;
                if (s == s2)
                {
                    break;
                }
                s = (s + 1) & 63;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FastSector64(float dx, float dz)
    {
        // Strictly monotonic 64-sector octant mapping in [0, 63] without Atan2.
        float sector;
        if (dx >= 0f)
        {
            if (dz >= 0f)
            {
                sector = dx >= dz
                    ? (dz / MathF.Max(dx, 1e-6f)) * 8f
                    : 16f - (dx / MathF.Max(dz, 1e-6f)) * 8f;
            }
            else
            {
                float adz = -dz;
                sector = adz >= dx
                    ? 48f + (dx / MathF.Max(adz, 1e-6f)) * 8f
                    : 64f - (adz / MathF.Max(dx, 1e-6f)) * 8f;
            }
        }
        else
        {
            float adx = -dx;
            if (dz >= 0f)
            {
                sector = dz >= adx
                    ? 16f + (adx / MathF.Max(dz, 1e-6f)) * 8f
                    : 32f - (dz / MathF.Max(adx, 1e-6f)) * 8f;
            }
            else
            {
                float adz = -dz;
                sector = adx >= adz
                    ? 32f + (adz / MathF.Max(adx, 1e-6f)) * 8f
                    : 48f - (adx / MathF.Max(adz, 1e-6f)) * 8f;
            }
        }

        return ((int)sector) & 63;
    }

    private void PublishVisibility()
    {
        ushort[] seenCount = _seenCount;
        byte[] active = _activeVisibility;
        byte[] blur = _blurBuffer;
        byte[] tex = _textureBuffer;
        int total = seenCount.Length;
        for (int i = 0; i < total; i++)
        {
            active[i] = seenCount[i] > 0 ? (byte)255 : (byte)0;
        }

        int cols = Columns;
        int rows = Rows;
        int lastCol = cols - 1;
        for (int z = 0; z < rows; z++)
        {
            int rowOffset = z * cols;
            blur[rowOffset] = (byte)((active[rowOffset] * 3 + (cols > 1 ? active[rowOffset + 1] : active[rowOffset])) >> 2);
            for (int x = 1; x < lastCol; x++)
            {
                int idx = rowOffset + x;
                blur[idx] = (byte)((active[idx - 1] + (active[idx] << 1) + active[idx + 1]) >> 2);
            }
            if (lastCol > 0)
            {
                int endIdx = rowOffset + lastCol;
                blur[endIdx] = (byte)((active[endIdx - 1] + active[endIdx] * 3) >> 2);
            }
        }

        for (int z = 0; z < rows; z++)
        {
            int rowOffset = z * cols;
            int prevRowOffset = z > 0 ? (z - 1) * cols : rowOffset;
            int nextRowOffset = z < rows - 1 ? (z + 1) * cols : rowOffset;
            for (int x = 0; x < cols; x++)
            {
                tex[rowOffset + x] = (byte)((blur[prevRowOffset + x] + (blur[rowOffset + x] << 1) + blur[nextRowOffset + x]) >> 2);
            }
        }

        _image.SetData(cols, rows, false, Image.Format.R8, tex);
        _texture.Update(_image);
    }

    public void Clear()
    {
        CancelInProgressSweep();
        Array.Clear(_activeVisibility);
        Array.Clear(_seenCount);
        _viewsheds.Clear();
        _removedUnits.Clear();
        _timeSinceCycleStart = 0f;
        _unitIndexInCycle = 0;
        _cycleInProgress = false;
        _smokeVolumeCount = 0;
        _lastSmokeSignature = 0;

        Array.Clear(_blurBuffer);
        Array.Clear(_textureBuffer);
        _image.SetData(Columns, Rows, false, Image.Format.R8, _textureBuffer);
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
