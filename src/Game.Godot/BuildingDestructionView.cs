using DeadGround.Engine;
using DeadGround.Engine.Net;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Presentation-only modular building destruction view for Godot.
/// Sleeping intact sections render via GPU MultiMesh LOD batches (LOD0/LOD1/LOD2 = 1 draw call for
/// all intact sections on the map), settled rubble mounds render via a single Rubble MultiMesh batch,
/// and only actively damaged or collapsing sections borrow a pre-allocated modular floor rig
/// (1 merged mesh per floor). Collapse animations and debris bursts are gated strictly on live
/// collapse events so re-observed ruins from fog of war snap directly to their settled state.
/// Zero gameplay logic, zero steady-state heap allocations.
/// </summary>
internal sealed class BuildingDestructionView : IDisposable
{
    internal const string PanelkaAssetPath = "res://Assets/Buildings/Panelka1464/panelka_1_464.glb";
    private const float ReferenceFootprint = 12.0f;
    private const float ReferenceFloorHeight = 3.1f;
    private const float ReferenceRubbleHeight = 1.5f;
    private const float CollapseAnimDurationSec = 1.45f;
    private const int MaxDustPuffs = 48;
    private const int InitialDamagedRigPoolSize = 24;
    private const float Lod1CameraSize = 180.0f;
    private const float Lod2CameraSize = 420.0f;

    private static readonly Aabb BatchCustomAabb = new(new Vector3(-2500f, -50f, -2500f), new Vector3(5000f, 350f, 5000f));

    private enum VisualMode : byte
    {
        IntactBatched = 0,
        DamagedRig = 1,
        RubbleBatched = 2
    }

    private sealed class DamagedSectionRig
    {
        public Node3D Root = null!;
        public MeshInstance3D[] IntactFloors = null!;
        public MeshInstance3D[] BreachedFloors = null!;
        public MeshInstance3D RoofNode = null!;
        public MeshInstance3D RubbleNode = null!;
        public int MaxFloors;
    }

    private sealed class SectionVisual
    {
        public StructureId Id;
        public int GeometryIndex;
        public OpaqueStructure Bounds = null!;
        public StructureDestructionProfile Profile;
        public float YawRad;
        public Vector3 BaseCenter;
        public Vector3 FloorScale;
        public Vector3 RubbleScale;
        public Transform3D IntactWorldTransform;
        public Transform3D RubbleWorldTransform;

        public VisualMode Mode;
        public int BatchSlot = -1; // slot inside intact or rubble MultiMesh when batched
        public DamagedSectionRig? ActiveRig;

        public byte ObservedFloorsStanding;
        public byte ObservedBreachMask;
        public ushort ObservedHealth;

        // Cascading visual collapse animation state
        public bool AnimatingCollapse;
        public float CollapseElapsed;
        public byte CollapseFromFloors;
        public byte CollapseToFloors;
        public float CollapseTiltSign;
    }

    private struct DustPuff
    {
        public bool Active;
        public MeshInstance3D Mesh;
        public StandardMaterial3D Material;
        public Vector3 Position;
        public Vector3 Velocity;
        public float Age;
        public float Lifetime;
        public float StartScale;
        public float EndScale;
    }

    private readonly Node3D _root;
    private readonly StaticGeometry _geometry;
    private readonly DebrisPhysicsPool _debrisPool;
    private readonly BuildingDestructionSoundBank _soundBank;
    private readonly Action<StructureId, float>? _onObservedHeightChanged;

    private readonly Mesh _intactLod0Mesh;
    private readonly Mesh _intactLod1Mesh;
    private readonly Mesh _intactLod2Mesh;
    private readonly Mesh _groundFloorMesh;
    private readonly Mesh _midFloorMesh;
    private readonly Mesh _breachedFloorMesh;
    private readonly Mesh _roofMesh;
    private readonly Mesh _rubbleMesh;

    private readonly MultiMesh[] _intactLodMultiMeshes = new MultiMesh[3];
    private readonly MultiMeshInstance3D[] _intactLodInstances = new MultiMeshInstance3D[3];
    private readonly int[] _intactSlotToSection;
    private int _intactBatchedCount;
    private int _activeLodLevel;

    private readonly MultiMesh _rubbleMultiMesh;
    private readonly MultiMeshInstance3D _rubbleInstance;
    private readonly int[] _rubbleSlotToSection;
    private int _rubbleBatchedCount;

    private readonly List<DamagedSectionRig> _freeRigs = new();
    private readonly int _maxFloorsAcrossSections;

    private readonly SectionVisual[] _sections;
    private readonly Dictionary<StructureId, int> _idToSection;
    private readonly bool[] _liveCollapseEventThisFrame;
    private readonly bool[] _liveDamageEventThisFrame;
    private readonly DustPuff[] _dustPuffs = new DustPuff[MaxDustPuffs];
    private readonly float[] _observedMaxYOverrides;
    private int _nextDustPuff;
    private uint _lastProcessedEventId;
    private bool _disposed;

    internal bool LoadedGlbAsset { get; }
    internal int SectionCount => _sections.Length;
    internal int ActiveLodLevel => _activeLodLevel;
    internal int IntactBatchedCount => _intactBatchedCount;
    internal int RubbleBatchedCount => _rubbleBatchedCount;
    internal int ActiveDamagedRigCount { get; private set; }
    internal DebrisPhysicsPool DebrisPool => _debrisPool;
    internal BuildingDestructionSoundBank SoundBank => _soundBank;
    internal float[] ObservedMaxYOverrides => _observedMaxYOverrides;

    /// <summary>
    /// Exact count of active opaque draw calls submitted by this view (excluding shadow pass):
    /// 1 for all sleeping intact sections + 1 for all settled rubble mounds + visible 1-mesh modules
    /// on currently damaged rigs.
    /// </summary>
    internal int ActiveDrawCallEstimate
    {
        get
        {
            int calls = 0;
            if (_intactBatchedCount > 0) calls++;
            if (_rubbleBatchedCount > 0) calls++;
            for (int i = 0; i < _sections.Length; i++)
            {
                DamagedSectionRig? rig = _sections[i].ActiveRig;
                if (rig is null)
                    continue;
                for (int f = 0; f < _sections[i].Profile.FloorsTotal; f++)
                {
                    if (rig.IntactFloors[f].Visible) calls++;
                    if (rig.BreachedFloors[f].Visible) calls++;
                }
                if (rig.RoofNode.Visible) calls++;
                if (rig.RubbleNode.Visible) calls++;
            }
            return calls;
        }
    }

    internal BuildingDestructionView(
        Node3D parent,
        StaticGeometry geometry,
        IReadOnlyList<DestructibleBuildingSpec> specs,
        Action<StructureId, float>? onObservedHeightChanged = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(specs);

        _geometry = geometry;
        _onObservedHeightChanged = onObservedHeightChanged;
        _root = new Node3D { Name = "DestructibleBuildings" };
        parent.AddChild(_root);

        _observedMaxYOverrides = new float[geometry.Structures.Count];
        Array.Fill(_observedMaxYOverrides, float.NaN);

        LoadedGlbAsset = TryExtractMeshes(
            out _intactLod0Mesh,
            out _intactLod1Mesh,
            out _intactLod2Mesh,
            out _groundFloorMesh,
            out _midFloorMesh,
            out _breachedFloorMesh,
            out _roofMesh,
            out _rubbleMesh,
            out Mesh? slabAMesh,
            out Mesh? slabBMesh,
            out Mesh? chunkCMesh);

        _debrisPool = new DebrisPhysicsPool(_root, slabAMesh, slabBMesh, chunkCMesh);
        _soundBank = new BuildingDestructionSoundBank(_root);

        // Pre-allocate pooled concrete dust puff spheres for zero steady-state allocations
        SphereMesh sharedPuffMesh = new() { Radius = 1.0f, Height = 1.6f, RadialSegments = 10, Rings = 6 };
        for (int i = 0; i < MaxDustPuffs; i++)
        {
            StandardMaterial3D mat = new()
            {
                AlbedoColor = new Color(0.76f, 0.74f, 0.69f, 0.0f),
                Roughness = 1.0f,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.PerVertex
            };
            MeshInstance3D mi = new()
            {
                Name = $"DustPuff_{i}",
                Mesh = sharedPuffMesh,
                MaterialOverride = mat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false
            };
            _root.AddChild(mi);
            _dustPuffs[i] = new DustPuff { Mesh = mi, Material = mat };
        }

        // Count valid specs matching geometry structures
        int validCount = 0;
        int maxFloors = 5;
        for (int i = 0; i < specs.Count; i++)
        {
            if (specs[i].Profile.IsValid && geometry.TryGetStructureIndex(specs[i].Id, out _))
            {
                validCount++;
                if (specs[i].Profile.FloorsTotal > maxFloors)
                    maxFloors = specs[i].Profile.FloorsTotal;
            }
        }

        _maxFloorsAcrossSections = maxFloors;
        _sections = new SectionVisual[validCount];
        _idToSection = new Dictionary<StructureId, int>(validCount);
        _liveCollapseEventThisFrame = new bool[validCount];
        _liveDamageEventThisFrame = new bool[validCount];
        _intactSlotToSection = new int[Math.Max(1, validCount)];
        _rubbleSlotToSection = new int[Math.Max(1, validCount)];

        // Create sleeping intact MultiMesh LOD batches (LOD0 close, LOD1 rts, LOD2 distant)
        Mesh[] lodMeshes = [_intactLod0Mesh, _intactLod1Mesh, _intactLod2Mesh];
        int batchCap = Math.Max(1, validCount);
        for (int lod = 0; lod < 3; lod++)
        {
            MultiMesh mm = new()
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                InstanceCount = batchCap,
                VisibleInstanceCount = 0,
                Mesh = lodMeshes[lod]
            };
            MultiMeshInstance3D mmi = new()
            {
                Name = $"PanelkaIntactBatch_LOD{lod}",
                Multimesh = mm,
                CustomAabb = BatchCustomAabb,
                CastShadow = lod < 2 ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = lod == 0
            };
            _root.AddChild(mmi);
            _intactLodMultiMeshes[lod] = mm;
            _intactLodInstances[lod] = mmi;
        }

        // Create settled rubble MultiMesh batch (1 draw call for all collapsed sections)
        _rubbleMultiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            InstanceCount = batchCap,
            VisibleInstanceCount = 0,
            Mesh = _rubbleMesh
        };
        _rubbleInstance = new MultiMeshInstance3D
        {
            Name = "PanelkaRubbleBatch",
            Multimesh = _rubbleMultiMesh,
            CustomAabb = BatchCustomAabb,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
            Visible = false
        };
        _root.AddChild(_rubbleInstance);

        // Pre-allocate pooled modular rigs for actively damaged sections (zero steady-state allocations)
        int initialRigs = Math.Min(validCount, InitialDamagedRigPoolSize);
        for (int r = 0; r < initialRigs; r++)
        {
            _freeRigs.Add(CreateDamagedRig(r));
        }

        int idx = 0;
        for (int i = 0; i < specs.Count; i++)
        {
            DestructibleBuildingSpec spec = specs[i];
            if (!spec.Profile.IsValid || !geometry.TryGetStructureIndex(spec.Id, out int geomIdx))
                continue;

            OpaqueStructure bounds = geometry.Structures[geomIdx];
            float sizeX = bounds.MaxX - bounds.MinX;
            float sizeZ = bounds.MaxZ - bounds.MinZ;
            Vector3 baseCenter = new((bounds.MinX + bounds.MaxX) * 0.5f, bounds.MinY, (bounds.MinZ + bounds.MaxZ) * 0.5f);
            Vector3 floorScale = new(sizeX / ReferenceFootprint, spec.Profile.FloorHeight / ReferenceFloorHeight, sizeZ / ReferenceFootprint);
            Vector3 rubbleScale = new(sizeX / ReferenceFootprint, spec.Profile.RubbleHeight / ReferenceRubbleHeight, sizeZ / ReferenceFootprint);

            Basis rotYaw = new(Vector3.Up, spec.YawRad);
            Transform3D intactTransform = new(rotYaw.Scaled(floorScale), baseCenter);
            Transform3D rubbleTransform = new(rotYaw.Scaled(rubbleScale), baseCenter);

            SectionVisual sec = new()
            {
                Id = spec.Id,
                GeometryIndex = geomIdx,
                Bounds = bounds,
                Profile = spec.Profile,
                YawRad = spec.YawRad,
                BaseCenter = baseCenter,
                FloorScale = floorScale,
                RubbleScale = rubbleScale,
                IntactWorldTransform = intactTransform,
                RubbleWorldTransform = rubbleTransform,
                Mode = VisualMode.IntactBatched,
                BatchSlot = -1,
                ObservedFloorsStanding = spec.Profile.FloorsTotal,
                ObservedBreachMask = 0,
                ObservedHealth = spec.Profile.MaxHealth,
                CollapseTiltSign = (spec.Id.Value & 1) == 0 ? 1f : -1f
            };
            _sections[idx] = sec;
            _idToSection[spec.Id] = idx;
            _observedMaxYOverrides[geomIdx] = bounds.MaxY;

            AddToIntactBatch(idx, sec);
            idx++;
        }
    }

    internal bool ContainsStructure(StructureId id) => _idToSection.ContainsKey(id);

    internal bool TryGetSectionInfo(StructureId id, out OpaqueStructure? bounds, out StructureDestructionProfile profile, out byte observedFloorsStanding)
    {
        if (_idToSection.TryGetValue(id, out int idx))
        {
            SectionVisual sec = _sections[idx];
            bounds = sec.Bounds;
            profile = sec.Profile;
            observedFloorsStanding = sec.ObservedFloorsStanding;
            return true;
        }
        bounds = default;
        profile = default;
        observedFloorsStanding = 0;
        return false;
    }

    internal bool IsSectionAnimatingCollapse(StructureId id)
    {
        return _idToSection.TryGetValue(id, out int idx) && _sections[idx].AnimatingCollapse;
    }

    /// <summary>
    /// Selects LOD0 (close), LOD1 (RTS), or LOD2 (distant) for all sleeping intact panelkas.
    /// Zero heap allocations.
    /// </summary>
    internal void UpdateLod(float cameraSize)
    {
        if (_disposed)
            return;

        int targetLod = cameraSize > Lod2CameraSize ? 2 : (cameraSize > Lod1CameraSize ? 1 : 0);
        if (targetLod == _activeLodLevel)
            return;

        _activeLodLevel = targetLod;
        bool hasIntact = _intactBatchedCount > 0;
        for (int lod = 0; lod < 3; lod++)
        {
            _intactLodInstances[lod].Visible = hasIntact && lod == _activeLodLevel;
        }
    }

    internal void Update(ClientReplica replica, float deltaSeconds)
    {
        if (_disposed)
            return;

        Array.Clear(_liveCollapseEventThisFrame);
        Array.Clear(_liveDamageEventThisFrame);

        // 1. Process incoming combat events FIRST so we know which sections experienced a live
        //    observed breach/collapse this frame vs. stale ruins newly revealed from fog of war.
        ReadOnlySpan<CombatEventObservation> events = replica.Events;
        for (int i = 0; i < events.Length; i++)
        {
            CombatEventObservation ev = events[i];
            if (ev.EventId <= _lastProcessedEventId)
                continue;

            if (ev.Kind is CombatEventKind.StructureDamaged or CombatEventKind.StructureFloorCollapsed or CombatEventKind.StructureCollapsed)
            {
                if (ev.StructureId.Value > 0 && _idToSection.TryGetValue(ev.StructureId, out int evSecIdx))
                {
                    if (ev.Kind is CombatEventKind.StructureFloorCollapsed or CombatEventKind.StructureCollapsed)
                    {
                        _liveCollapseEventThisFrame[evSecIdx] = true;
                    }
                    else
                    {
                        _liveDamageEventThisFrame[evSecIdx] = true;
                    }
                }
                HandleStructureEvent(in ev);
            }
            _lastProcessedEventId = ev.EventId;
        }

        // 2. Synchronize visual modules with authoritative FoW-filtered StructureObservation records
        ReadOnlySpan<StructureObservation> structures = replica.Structures;
        for (int i = 0; i < structures.Length; i++)
        {
            StructureObservation obs = structures[i];
            if (!_idToSection.TryGetValue(obs.Id, out int idx))
                continue;

            SectionVisual sec = _sections[idx];
            bool floorsChanged = obs.FloorsStanding != sec.ObservedFloorsStanding;
            bool breachChanged = obs.FloorBreachMask != sec.ObservedBreachMask;

            if (floorsChanged || breachChanged || obs.Health != sec.ObservedHealth)
            {
                byte prevFloors = sec.ObservedFloorsStanding;
                byte prevMask = sec.ObservedBreachMask;

                sec.ObservedFloorsStanding = obs.FloorsStanding;
                sec.ObservedBreachMask = obs.FloorBreachMask;
                sec.ObservedHealth = obs.Health;

                if (floorsChanged)
                {
                    float newMaxY = StructureDestructionMath.ComputeCurrentHeight(
                        sec.Bounds.MinY,
                        sec.Bounds.MaxY,
                        in sec.Profile,
                        obs.FloorsStanding);
                    _observedMaxYOverrides[sec.GeometryIndex] = newMaxY;
                    _onObservedHeightChanged?.Invoke(sec.Id, newMaxY);

                    if (obs.FloorsStanding < prevFloors && _liveCollapseEventThisFrame[idx])
                    {
                        // Live observed collapse: animate floors pancaking downward
                        BeginCollapseAnimation(idx, sec, prevFloors, obs.FloorsStanding);
                    }
                    else
                    {
                        // Re-observed ruins or non-live state jump: snap directly to settled visuals
                        sec.AnimatingCollapse = false;
                        ApplySettledVisualState(idx, sec);
                    }
                }
                else if (breachChanged)
                {
                    ApplySettledVisualState(idx, sec);
                    byte newlyBreached = (byte)(obs.FloorBreachMask & ~prevMask);
                    if (newlyBreached != 0 && (_liveDamageEventThisFrame[idx] || _liveCollapseEventThisFrame[idx]))
                    {
                        SpawnBreachDust(sec, newlyBreached);
                    }
                }
            }
        }

        // 3. Advance active floor cascade collapse animations
        for (int i = 0; i < _sections.Length; i++)
        {
            SectionVisual sec = _sections[i];
            if (!sec.AnimatingCollapse)
                continue;

            AdvanceCollapseAnimation(i, sec, deltaSeconds);
        }

        // 4. Advance Variant A MultiMesh debris physics and dust puffs
        _debrisPool.Update(deltaSeconds, _geometry.HeightMap);
        UpdateDustPuffs(deltaSeconds);
    }

    private void HandleStructureEvent(in CombatEventObservation ev)
    {
        Vector3 hitPos = new(ev.Target.X, ev.Target.Y, ev.Target.Z);
        Vector3 originPos = new(ev.Origin.X, ev.Origin.Y, ev.Origin.Z);
        Vector3 outward = (originPos - hitPos).LengthSquared() > 0.25f
            ? (originPos - hitPos).Normalized()
            : Vector3.Up;
        float terrainY = _geometry.HeightMap.GetHeight(hitPos.X, hitPos.Z);
        float rubbleHeight = ReferenceRubbleHeight;
        if (ev.StructureId.Value > 0 && _idToSection.TryGetValue(ev.StructureId, out int secIdx))
        {
            rubbleHeight = _sections[secIdx].Profile.RubbleHeight;
        }

        if (ev.Kind == CombatEventKind.StructureDamaged)
        {
            _soundBank.PlayConcreteHit(hitPos);
            _debrisPool.SpawnBurst(hitPos, outward, spreadRadius: 1.4f, count: 10, minSpeed: 4.5f, maxSpeed: 12.5f, rubbleFloorY: terrainY, includeLargeSlabs: false);
            SpawnDustPuff(hitPos, outward * 2.5f + Vector3.Up * 1.8f, startScale: 1.2f, endScale: 4.8f, lifetime: 1.4f);
            SpawnDustPuff(hitPos + Vector3.Up * 0.6f, Vector3.Up * 2.4f, startScale: 0.9f, endScale: 3.6f, lifetime: 1.2f);
        }
        else if (ev.Kind == CombatEventKind.StructureFloorCollapsed)
        {
            _soundBank.PlayFloorCollapse(hitPos);
            _debrisPool.SpawnBurst(hitPos, Vector3.Up, spreadRadius: 5.5f, count: 22, minSpeed: 3.5f, maxSpeed: 11.5f, rubbleFloorY: terrainY + rubbleHeight * 0.4f, includeLargeSlabs: true);
            for (int p = 0; p < 4; p++)
            {
                float a = p * (MathF.Tau * 0.25f) + 0.3f;
                Vector3 dir = new(MathF.Cos(a) * 3.8f, 2.2f, MathF.Sin(a) * 3.8f);
                SpawnDustPuff(hitPos + dir * 0.4f, dir, startScale: 2.2f, endScale: 8.5f, lifetime: 2.4f);
            }
        }
        else if (ev.Kind == CombatEventKind.StructureCollapsed)
        {
            _soundBank.PlayFullCollapse(hitPos);
            _debrisPool.SpawnBurst(hitPos + Vector3.Up * 2.2f, Vector3.Up, spreadRadius: 5.8f, count: 34, minSpeed: 4.0f, maxSpeed: 13.5f, rubbleFloorY: terrainY + rubbleHeight * 0.7f, includeLargeSlabs: true);
            for (int p = 0; p < 6; p++)
            {
                float a = p * (MathF.Tau / 6f);
                Vector3 dir = new(MathF.Cos(a) * 4.8f, 2.6f, MathF.Sin(a) * 4.8f);
                SpawnDustPuff(hitPos + dir * 0.45f, dir, startScale: 2.8f, endScale: 10.5f, lifetime: 3.0f);
            }
        }
    }

    private void BeginCollapseAnimation(int secIdx, SectionVisual sec, byte fromFloors, byte toFloors)
    {
        DamagedSectionRig rig = AcquireDamagedRig(secIdx, sec);
        sec.AnimatingCollapse = true;
        sec.CollapseElapsed = 0f;
        sec.CollapseFromFloors = fromFloors;
        sec.CollapseToFloors = toFloors;

        rig.RubbleNode.Visible = toFloors == 0;
        if (toFloors == 0)
        {
            rig.RubbleNode.Scale = new Vector3(sec.RubbleScale.X, sec.RubbleScale.Y * 0.35f, sec.RubbleScale.Z);
        }

        // Spawn concrete slabs cascading outward from the collapsing perimeter
        float topY = sec.BaseCenter.Y + fromFloors * sec.Profile.FloorHeight * 0.72f;
        float floorY = sec.BaseCenter.Y + (toFloors == 0 ? sec.Profile.RubbleHeight * 0.7f : toFloors * sec.Profile.FloorHeight * 0.25f);
        int burstCount = Math.Clamp((fromFloors - toFloors) * 12, 12, 36);
        _debrisPool.SpawnBurst(
            new Vector3(sec.BaseCenter.X, topY, sec.BaseCenter.Z),
            Vector3.Up,
            spreadRadius: (sec.Bounds.MaxX - sec.Bounds.MinX) * 0.48f,
            count: burstCount,
            minSpeed: 3.0f,
            maxSpeed: 10.0f,
            rubbleFloorY: floorY,
            includeLargeSlabs: true);
    }

    private void AdvanceCollapseAnimation(int secIdx, SectionVisual sec, float dt)
    {
        DamagedSectionRig? rig = sec.ActiveRig;
        if (rig is null)
        {
            sec.AnimatingCollapse = false;
            ApplySettledVisualState(secIdx, sec);
            return;
        }

        sec.CollapseElapsed += dt;
        float progress = Math.Clamp(sec.CollapseElapsed / CollapseAnimDurationSec, 0f, 1f);
        float targetTopY = sec.CollapseToFloors == 0
            ? sec.Profile.RubbleHeight * 0.4f
            : sec.CollapseToFloors * sec.Profile.FloorHeight;

        int total = sec.Profile.FloorsTotal;
        for (int f = 0; f < total; f++)
        {
            bool breached = (sec.ObservedBreachMask & (1 << f)) != 0;
            if (f < sec.CollapseToFloors)
            {
                float restY = f * sec.Profile.FloorHeight;
                rig.IntactFloors[f].Position = new Vector3(0f, restY, 0f);
                rig.IntactFloors[f].Rotation = Vector3.Zero;
                rig.IntactFloors[f].Visible = !breached;
                rig.BreachedFloors[f].Position = new Vector3(0f, restY, 0f);
                rig.BreachedFloors[f].Rotation = Vector3.Zero;
                rig.BreachedFloors[f].Visible = breached;
            }
            else if (f < sec.CollapseFromFloors)
            {
                float stagger = (f - sec.CollapseToFloors) * 0.08f;
                float localT = Math.Clamp((progress - stagger) / MathF.Max(0.25f, 1f - stagger), 0f, 1f);
                float fallEase = localT * localT;
                float startY = f * sec.Profile.FloorHeight;
                float curY = startY + (targetTopY - startY) * fallEase;
                float tilt = sec.CollapseTiltSign * 0.14f * fallEase * (1f + 0.15f * f);
                float jitterX = MathF.Sin(sec.CollapseElapsed * 28f + f * 1.7f) * 0.14f * (1f - localT);
                bool stillVisible = localT < 0.94f;

                rig.IntactFloors[f].Visible = false;
                rig.BreachedFloors[f].Visible = stillVisible;
                rig.BreachedFloors[f].Position = new Vector3(jitterX, curY, 0f);
                rig.BreachedFloors[f].Rotation = new Vector3(tilt * 0.65f, 0f, tilt);
            }
            else
            {
                rig.IntactFloors[f].Visible = false;
                rig.BreachedFloors[f].Visible = false;
            }
        }

        if (sec.CollapseFromFloors == total && sec.CollapseToFloors < total)
        {
            float roofEase = progress * progress;
            float roofStartY = total * sec.Profile.FloorHeight;
            rig.RoofNode.Position = new Vector3(0f, roofStartY + (targetTopY - roofStartY) * roofEase, 0f);
            rig.RoofNode.Rotation = new Vector3(0.18f * roofEase, 0f, -sec.CollapseTiltSign * 0.16f * roofEase);
            rig.RoofNode.Visible = progress < 0.85f;
        }
        else
        {
            rig.RoofNode.Visible = sec.CollapseToFloors == total;
        }

        if (sec.CollapseToFloors == 0)
        {
            rig.RubbleNode.Visible = true;
            float rise = 0.35f + 0.65f * MathF.Min(1f, progress * 1.25f);
            rig.RubbleNode.Scale = new Vector3(sec.RubbleScale.X, sec.RubbleScale.Y * rise, sec.RubbleScale.Z);
        }

        if (progress >= 1f)
        {
            sec.AnimatingCollapse = false;
            ApplySettledVisualState(secIdx, sec);
        }
    }

    private void ApplySettledVisualState(int secIdx, SectionVisual sec)
    {
        if (sec.ObservedFloorsStanding >= sec.Profile.FloorsTotal && sec.ObservedBreachMask == 0)
        {
            // Undamaged: return to sleeping intact MultiMesh batch
            ReleaseDamagedRig(sec);
            RemoveFromRubbleBatch(sec);
            AddToIntactBatch(secIdx, sec);
            return;
        }

        if (sec.ObservedFloorsStanding == 0)
        {
            // Fully collapsed: return rig to pool and move to settled Rubble MultiMesh batch
            ReleaseDamagedRig(sec);
            RemoveFromIntactBatch(sec);
            AddToRubbleBatch(secIdx, sec);
            return;
        }

        // Partially standing or breached: render via pooled modular rig (1 mesh per floor)
        DamagedSectionRig rig = AcquireDamagedRig(secIdx, sec);
        int total = sec.Profile.FloorsTotal;
        int standing = sec.ObservedFloorsStanding;
        for (int f = 0; f < rig.MaxFloors; f++)
        {
            bool inProfile = f < total;
            bool visible = inProfile && f < standing;
            bool breached = visible && ((sec.ObservedBreachMask & (1 << f)) != 0);
            float y = f * sec.Profile.FloorHeight;

            rig.IntactFloors[f].Position = new Vector3(0f, y, 0f);
            rig.IntactFloors[f].Rotation = Vector3.Zero;
            rig.IntactFloors[f].Visible = visible && !breached;

            rig.BreachedFloors[f].Position = new Vector3(0f, y, 0f);
            rig.BreachedFloors[f].Rotation = Vector3.Zero;
            rig.BreachedFloors[f].Visible = breached;
        }

        rig.RoofNode.Position = new Vector3(0f, total * sec.Profile.FloorHeight, 0f);
        rig.RoofNode.Rotation = Vector3.Zero;
        rig.RoofNode.Visible = standing >= total;

        rig.RubbleNode.Scale = sec.RubbleScale;
        rig.RubbleNode.Visible = false;
    }

    private void AddToIntactBatch(int secIdx, SectionVisual sec)
    {
        if (sec.Mode == VisualMode.IntactBatched && sec.BatchSlot >= 0)
            return;

        int slot = _intactBatchedCount++;
        _intactSlotToSection[slot] = secIdx;
        sec.Mode = VisualMode.IntactBatched;
        sec.BatchSlot = slot;

        for (int lod = 0; lod < 3; lod++)
        {
            _intactLodMultiMeshes[lod].SetInstanceTransform(slot, sec.IntactWorldTransform);
            _intactLodMultiMeshes[lod].VisibleInstanceCount = _intactBatchedCount;
            _intactLodInstances[lod].Visible = lod == _activeLodLevel;
        }
    }

    private void RemoveFromIntactBatch(SectionVisual sec)
    {
        if (sec.Mode != VisualMode.IntactBatched || sec.BatchSlot < 0)
            return;

        int slot = sec.BatchSlot;
        int lastSlot = --_intactBatchedCount;
        if (slot != lastSlot)
        {
            int movedSecIdx = _intactSlotToSection[lastSlot];
            SectionVisual movedSec = _sections[movedSecIdx];
            _intactSlotToSection[slot] = movedSecIdx;
            movedSec.BatchSlot = slot;
            for (int lod = 0; lod < 3; lod++)
            {
                _intactLodMultiMeshes[lod].SetInstanceTransform(slot, movedSec.IntactWorldTransform);
            }
        }

        sec.BatchSlot = -1;
        bool anyIntact = _intactBatchedCount > 0;
        for (int lod = 0; lod < 3; lod++)
        {
            _intactLodMultiMeshes[lod].VisibleInstanceCount = _intactBatchedCount;
            _intactLodInstances[lod].Visible = anyIntact && lod == _activeLodLevel;
        }
    }

    private void AddToRubbleBatch(int secIdx, SectionVisual sec)
    {
        if (sec.Mode == VisualMode.RubbleBatched && sec.BatchSlot >= 0)
            return;

        int slot = _rubbleBatchedCount++;
        _rubbleSlotToSection[slot] = secIdx;
        sec.Mode = VisualMode.RubbleBatched;
        sec.BatchSlot = slot;

        _rubbleMultiMesh.SetInstanceTransform(slot, sec.RubbleWorldTransform);
        _rubbleMultiMesh.VisibleInstanceCount = _rubbleBatchedCount;
        _rubbleInstance.Visible = true;
    }

    private void RemoveFromRubbleBatch(SectionVisual sec)
    {
        if (sec.Mode != VisualMode.RubbleBatched || sec.BatchSlot < 0)
            return;

        int slot = sec.BatchSlot;
        int lastSlot = --_rubbleBatchedCount;
        if (slot != lastSlot)
        {
            int movedSecIdx = _rubbleSlotToSection[lastSlot];
            SectionVisual movedSec = _sections[movedSecIdx];
            _rubbleSlotToSection[slot] = movedSecIdx;
            movedSec.BatchSlot = slot;
            _rubbleMultiMesh.SetInstanceTransform(slot, movedSec.RubbleWorldTransform);
        }

        sec.BatchSlot = -1;
        _rubbleMultiMesh.VisibleInstanceCount = _rubbleBatchedCount;
        _rubbleInstance.Visible = _rubbleBatchedCount > 0;
    }

    private DamagedSectionRig AcquireDamagedRig(int secIdx, SectionVisual sec)
    {
        if (sec.ActiveRig is not null)
            return sec.ActiveRig;

        RemoveFromIntactBatch(sec);
        RemoveFromRubbleBatch(sec);

        DamagedSectionRig rig;
        if (_freeRigs.Count > 0)
        {
            int last = _freeRigs.Count - 1;
            rig = _freeRigs[last];
            _freeRigs.RemoveAt(last);
        }
        else
        {
            rig = CreateDamagedRig(secIdx);
        }

        rig.Root.Position = sec.BaseCenter;
        rig.Root.Rotation = new Vector3(0f, sec.YawRad, 0f);
        for (int f = 0; f < rig.MaxFloors; f++)
        {
            rig.IntactFloors[f].Scale = sec.FloorScale;
            rig.BreachedFloors[f].Scale = sec.FloorScale;
            rig.IntactFloors[f].Visible = false;
            rig.BreachedFloors[f].Visible = false;
        }
        rig.RoofNode.Scale = new Vector3(sec.FloorScale.X, 1f, sec.FloorScale.Z);
        rig.RoofNode.Visible = false;
        rig.RubbleNode.Scale = sec.RubbleScale;
        rig.RubbleNode.Visible = false;
        rig.Root.Visible = true;

        sec.Mode = VisualMode.DamagedRig;
        sec.ActiveRig = rig;
        ActiveDamagedRigCount++;
        return rig;
    }

    private void ReleaseDamagedRig(SectionVisual sec)
    {
        if (sec.ActiveRig is null)
            return;

        DamagedSectionRig rig = sec.ActiveRig;
        rig.Root.Visible = false;
        sec.ActiveRig = null;
        ActiveDamagedRigCount--;
        _freeRigs.Add(rig);
    }

    private DamagedSectionRig CreateDamagedRig(int index)
    {
        Node3D rigRoot = new()
        {
            Name = $"DamagedSectionRig_{index}",
            Visible = false
        };
        _root.AddChild(rigRoot);

        int maxFloors = _maxFloorsAcrossSections;
        MeshInstance3D[] intactFloors = new MeshInstance3D[maxFloors];
        MeshInstance3D[] breachedFloors = new MeshInstance3D[maxFloors];

        for (int f = 0; f < maxFloors; f++)
        {
            MeshInstance3D intactMi = new()
            {
                Name = $"FloorIntact_{f}",
                Mesh = f == 0 ? _groundFloorMesh : _midFloorMesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
                Visible = false
            };
            rigRoot.AddChild(intactMi);
            intactFloors[f] = intactMi;

            MeshInstance3D breachedMi = new()
            {
                Name = $"FloorBreached_{f}",
                Mesh = _breachedFloorMesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
                Visible = false
            };
            rigRoot.AddChild(breachedMi);
            breachedFloors[f] = breachedMi;
        }

        MeshInstance3D roofMi = new()
        {
            Name = "RoofCap",
            Mesh = _roofMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
            Visible = false
        };
        rigRoot.AddChild(roofMi);

        MeshInstance3D rubbleMi = new()
        {
            Name = "RubbleMound",
            Mesh = _rubbleMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
            Visible = false
        };
        rigRoot.AddChild(rubbleMi);

        return new DamagedSectionRig
        {
            Root = rigRoot,
            IntactFloors = intactFloors,
            BreachedFloors = breachedFloors,
            RoofNode = roofMi,
            RubbleNode = rubbleMi,
            MaxFloors = maxFloors
        };
    }

    private void SpawnBreachDust(SectionVisual sec, byte newlyBreachedMask)
    {
        for (int f = 0; f < sec.Profile.FloorsTotal; f++)
        {
            if ((newlyBreachedMask & (1 << f)) == 0)
                continue;
            float y = sec.BaseCenter.Y + (f + 0.5f) * sec.Profile.FloorHeight;
            Vector3 center = new(sec.BaseCenter.X, y, sec.BaseCenter.Z);
            SpawnDustPuff(center, Vector3.Up * 1.6f, startScale: 1.8f, endScale: 6.2f, lifetime: 1.8f);
        }
    }

    private void SpawnDustPuff(Vector3 position, Vector3 velocity, float startScale, float endScale, float lifetime)
    {
        ref DustPuff puff = ref _dustPuffs[_nextDustPuff];
        _nextDustPuff = (_nextDustPuff + 1) % MaxDustPuffs;

        puff.Active = true;
        puff.Position = position;
        puff.Velocity = velocity;
        puff.Age = 0f;
        puff.Lifetime = lifetime;
        puff.StartScale = startScale;
        puff.EndScale = endScale;
        puff.Mesh.Position = position;
        puff.Mesh.Scale = new Vector3(startScale, startScale, startScale);
        puff.Mesh.Visible = true;
        puff.Material.AlbedoColor = new Color(0.76f, 0.74f, 0.69f, 0.55f);
    }

    private void UpdateDustPuffs(float dt)
    {
        for (int i = 0; i < MaxDustPuffs; i++)
        {
            ref DustPuff puff = ref _dustPuffs[i];
            if (!puff.Active)
                continue;

            puff.Age += dt;
            if (puff.Age >= puff.Lifetime)
            {
                puff.Active = false;
                puff.Mesh.Visible = false;
                continue;
            }

            float t = puff.Age / puff.Lifetime;
            puff.Position += puff.Velocity * dt;
            puff.Velocity *= MathF.Max(0f, 1f - dt * 1.4f);
            float scale = puff.StartScale + (puff.EndScale - puff.StartScale) * MathF.Sqrt(t);
            float alpha = 0.55f * (1f - t) * (1f - t);

            puff.Mesh.Position = puff.Position;
            puff.Mesh.Scale = new Vector3(scale, scale * 0.78f, scale);
            puff.Material.AlbedoColor = new Color(0.75f, 0.73f, 0.68f, alpha);
        }
    }

    private static bool TryExtractMeshes(
        out Mesh intactLod0,
        out Mesh intactLod1,
        out Mesh intactLod2,
        out Mesh ground,
        out Mesh mid,
        out Mesh breached,
        out Mesh roof,
        out Mesh rubble,
        out Mesh? slabA,
        out Mesh? slabB,
        out Mesh? chunkC)
    {
        slabA = null;
        slabB = null;
        chunkC = null;

        PackedScene? packed = ResourceLoader.Exists(PanelkaAssetPath)
            ? ResourceLoader.Load<PackedScene>(PanelkaAssetPath)
            : null;
        if (packed is not null)
        {
            Node3D? sceneRoot = packed.Instantiate<Node3D>();
            if (sceneRoot is not null)
            {
                try
                {
                    Mesh? l0 = ExtractSingleMesh(sceneRoot, "Section_Intact_LOD0");
                    Mesh? l1 = ExtractSingleMesh(sceneRoot, "Section_Intact_LOD1");
                    Mesh? l2 = ExtractSingleMesh(sceneRoot, "Section_Intact_LOD2");
                    Mesh? g = ExtractSingleMesh(sceneRoot, "Floor_Ground_Intact");
                    Mesh? m = ExtractSingleMesh(sceneRoot, "Floor_Mid_Intact");
                    Mesh? b = ExtractSingleMesh(sceneRoot, "Floor_Breached");
                    Mesh? rf = ExtractSingleMesh(sceneRoot, "Roof_Cap");
                    Mesh? rb = ExtractSingleMesh(sceneRoot, "Rubble_Mound");
                    slabA = ExtractSingleMesh(sceneRoot, "Debris_Slab_A");
                    slabB = ExtractSingleMesh(sceneRoot, "Debris_Slab_B");
                    chunkC = ExtractSingleMesh(sceneRoot, "Debris_Chunk_C");

                    if (l0 is not null && l1 is not null && l2 is not null &&
                        g is not null && m is not null && b is not null && rf is not null && rb is not null)
                    {
                        intactLod0 = l0;
                        intactLod1 = l1;
                        intactLod2 = l2;
                        ground = g;
                        mid = m;
                        breached = b;
                        roof = rf;
                        rubble = rb;
                        return true;
                    }
                }
                finally
                {
                    sceneRoot.QueueFree();
                }
            }
        }

        // Clean procedural box fallback if asset has not been imported yet
        Mesh fullFallback = CreateFallbackMesh(new Vector3(ReferenceFootprint, ReferenceFloorHeight * 5f, ReferenceFootprint), new Color("b3afa4"));
        intactLod0 = fullFallback;
        intactLod1 = fullFallback;
        intactLod2 = fullFallback;
        ground = CreateFallbackMesh(new Vector3(ReferenceFootprint, ReferenceFloorHeight, ReferenceFootprint), new Color("9f9b90"));
        mid = CreateFallbackMesh(new Vector3(ReferenceFootprint, ReferenceFloorHeight, ReferenceFootprint), new Color("b3afa4"));
        breached = CreateFallbackMesh(new Vector3(ReferenceFootprint * 0.96f, ReferenceFloorHeight * 0.96f, ReferenceFootprint * 0.96f), new Color("746f68"));
        roof = CreateFallbackMesh(new Vector3(ReferenceFootprint, 0.4f, ReferenceFootprint), new Color("47484a"));
        rubble = CreateFallbackMesh(new Vector3(ReferenceFootprint * 0.96f, ReferenceRubbleHeight, ReferenceFootprint * 0.96f), new Color("7d7870"));
        return false;
    }

    private static Mesh? ExtractSingleMesh(Node3D root, string moduleName)
    {
        Node? moduleNode = root.FindChild(moduleName, recursive: true, owned: false);
        if (moduleNode is null)
            return null;

        return FindFirstMesh(moduleNode);
    }

    private static Mesh? FindFirstMesh(Node node)
    {
        if (node is MeshInstance3D selfMi && selfMi.Mesh is not null)
            return selfMi.Mesh;
        int count = node.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            Mesh? found = FindFirstMesh(node.GetChild(i));
            if (found is not null)
                return found;
        }
        return null;
    }

    private static Mesh CreateFallbackMesh(Vector3 size, Color color)
    {
        // Offset vertices upward by +size.Y/2 so base sits at Y = 0, matching GLB module convention
        SurfaceTool st = new();
        st.Begin(Mesh.PrimitiveType.Triangles);
        BoxMesh box = new() { Size = size };
        st.AppendFrom(box, 0, new Transform3D(Basis.Identity, new Vector3(0f, size.Y * 0.5f, 0f)));
        ArrayMesh mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D { AlbedoColor = color, Roughness = 0.90f });
        return mesh;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _debrisPool.Dispose();
        _soundBank.Dispose();
        if (GodotObject.IsInstanceValid(_root))
        {
            _root.QueueFree();
        }
    }
}
