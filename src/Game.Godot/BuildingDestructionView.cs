using DeadGround.Engine;
using DeadGround.Engine.Net;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Presentation-only modular building destruction view for Godot.
/// Sleeping intact sections render via GPU MultiMesh LOD batches (LOD0/LOD1/LOD2 = 1 draw call for
/// all intact sections on the map), settled impassable battle ruins render via 2 asymmetric Rubble
/// MultiMesh batches, and actively damaged sections borrow a pre-allocated modular floor rig
/// crowned by an asymmetric jagged upper-wall ruin cap (Top_Jagged_Cap).
/// Live collapse transitions use a dense billowing dust curtain + multi-slab debris ejection +
/// brief structural shudder and swap behind peak dust opacity (never sliding whole floor boxes).
/// Re-observed ruins from fog of war snap directly to settled visuals without replaying collapse FX.
/// Zero gameplay logic, zero steady-state heap allocations.
/// </summary>
internal sealed class BuildingDestructionView : IDisposable
{
    internal const string PanelkaAssetPath = "res://Assets/Buildings/Panelka1464/panelka_1_464.glb";
    private const float ReferenceWidth = 14.4f;
    private const float ReferenceDepth = 11.8f;
    private const float ReferenceFloorHeight = 2.75f;
    private const float ReferenceRubbleHeight = 1.65f;
    private const float CollapseSwapDelaySec = 0.22f;
    private const float CollapseShudderDurationSec = 0.42f;
    private const int MaxDustPuffs = 96;
    private const int InitialDamagedRigPoolSize = 24;
    private const float Lod1CameraSize = 180.0f;
    private const float Lod2CameraSize = 420.0f;

    private static readonly Aabb BatchCustomAabb = new(new Vector3(-2500f, -50f, -2500f), new Vector3(5000f, 350f, 5000f));

    private enum VisualMode : byte
    {
        IntactBatched = 0,
        DamagedRig = 1,
        RubbleBatchedA = 2,
        RubbleBatchedB = 3
    }

    private sealed class DamagedSectionRig
    {
        public Node3D Root = null!;
        public MeshInstance3D[] IntactFloors = null!;
        public MeshInstance3D[] BreachedFloors = null!;
        public MeshInstance3D JaggedCapNode = null!;
        public MeshInstance3D RoofNode = null!;
        public MeshInstance3D RubbleNodeA = null!;
        public MeshInstance3D RubbleNodeB = null!;
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
        public bool UseRubbleVariantB;
        public float CapMirrorSign;

        public VisualMode Mode;
        public int BatchSlot = -1;
        public DamagedSectionRig? ActiveRig;

        public byte ObservedFloorsStanding;
        public byte ObservedBreachMask;
        public ushort ObservedHealth;

        // Dust-masked structural shudder transition state (brief shudder -> swap behind dust -> settle)
        public bool AnimatingCollapse;
        public bool CollapseSwapped;
        public float CollapseElapsed;
        public byte CollapseFromFloors;
        public byte CollapseToFloors;
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
        public float PeakAlpha;
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
    private readonly Mesh _jaggedCapMesh;
    private readonly Mesh _roofMesh;
    private readonly Mesh _rubbleMeshA;
    private readonly Mesh _rubbleMeshB;

    private readonly MultiMesh[] _intactLodMultiMeshes = new MultiMesh[3];
    private readonly MultiMeshInstance3D[] _intactLodInstances = new MultiMeshInstance3D[3];
    private readonly int[] _intactSlotToSection;
    private int _intactBatchedCount;
    private int _activeLodLevel;

    private readonly MultiMesh[] _rubbleMultiMeshes = new MultiMesh[2];
    private readonly MultiMeshInstance3D[] _rubbleInstances = new MultiMeshInstance3D[2];
    private readonly int[][] _rubbleSlotToSection;
    private readonly int[] _rubbleBatchedCounts = new int[2];

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
    internal int RubbleBatchedCount => _rubbleBatchedCounts[0] + _rubbleBatchedCounts[1];
    internal int ActiveDamagedRigCount { get; private set; }
    internal DebrisPhysicsPool DebrisPool => _debrisPool;
    internal BuildingDestructionSoundBank SoundBank => _soundBank;
    internal float[] ObservedMaxYOverrides => _observedMaxYOverrides;

    /// <summary>
    /// Exact count of active opaque draw calls submitted by this view (excluding shadow pass):
    /// 1 for all sleeping intact sections + 1-2 for all settled asymmetric battle ruins + visible
    /// 1-mesh modules on currently damaged rigs.
    /// </summary>
    internal int ActiveDrawCallEstimate
    {
        get
        {
            int calls = 0;
            if (_intactBatchedCount > 0) calls++;
            if (_rubbleBatchedCounts[0] > 0) calls++;
            if (_rubbleBatchedCounts[1] > 0) calls++;
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
                if (rig.JaggedCapNode.Visible) calls++;
                if (rig.RoofNode.Visible) calls++;
                if (rig.RubbleNodeA.Visible) calls++;
                if (rig.RubbleNodeB.Visible) calls++;
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
            out _jaggedCapMesh,
            out _roofMesh,
            out _rubbleMeshA,
            out _rubbleMeshB,
            out Mesh? slabAMesh,
            out Mesh? slabBMesh,
            out Mesh? chunkCMesh);

        _debrisPool = new DebrisPhysicsPool(_root, slabAMesh, slabBMesh, chunkCMesh);
        _soundBank = new BuildingDestructionSoundBank(_root);

        // Pre-allocate pooled concrete dust puff billows for dense dust-curtain collapse masking (0 runtime allocs)
        SphereMesh sharedPuffMesh = new() { Radius = 1.0f, Height = 1.65f, RadialSegments = 10, Rings = 6 };
        for (int i = 0; i < MaxDustPuffs; i++)
        {
            StandardMaterial3D mat = new()
            {
                AlbedoColor = new Color(0.70f, 0.68f, 0.63f, 0.0f),
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
        int batchCap = Math.Max(1, validCount);
        _intactSlotToSection = new int[batchCap];
        _rubbleSlotToSection = [new int[batchCap], new int[batchCap]];

        // Sleeping intact MultiMesh LOD batches (LOD0 close, LOD1 rts, LOD2 distant)
        Mesh[] lodMeshes = [_intactLod0Mesh, _intactLod1Mesh, _intactLod2Mesh];
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

        // Settled war-torn battle ruins MultiMesh batches (2 asymmetric variants -> max 2 draw calls for 120 ruins)
        Mesh[] rubbleMeshes = [_rubbleMeshA, _rubbleMeshB];
        for (int v = 0; v < 2; v++)
        {
            MultiMesh mm = new()
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                InstanceCount = batchCap,
                VisibleInstanceCount = 0,
                Mesh = rubbleMeshes[v]
            };
            MultiMeshInstance3D mmi = new()
            {
                Name = v == 0 ? "PanelkaRubbleBatchA" : "PanelkaRubbleBatchB",
                Multimesh = mm,
                CustomAabb = BatchCustomAabb,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
                Visible = false
            };
            _root.AddChild(mmi);
            _rubbleMultiMeshes[v] = mm;
            _rubbleInstances[v] = mmi;
        }

        // Pre-allocate pooled modular rigs for actively damaged sections
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
            bool rotated90 = MathF.Abs(MathF.Sin(spec.YawRad)) > 0.707f;
            float localWidthX = rotated90 ? sizeZ : sizeX;
            float localDepthZ = rotated90 ? sizeX : sizeZ;

            Vector3 baseCenter = new((bounds.MinX + bounds.MaxX) * 0.5f, bounds.MinY, (bounds.MinZ + bounds.MaxZ) * 0.5f);
            Vector3 floorScale = new(localWidthX / ReferenceWidth, spec.Profile.FloorHeight / ReferenceFloorHeight, localDepthZ / ReferenceDepth);
            Vector3 rubbleScale = new(localWidthX / ReferenceWidth, Math.Clamp(spec.Profile.RubbleHeight / ReferenceRubbleHeight, 0.75f, 1.35f), localDepthZ / ReferenceDepth);

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
                UseRubbleVariantB = (spec.Id.Value & 1) == 1,
                CapMirrorSign = (spec.Id.Value & 1) == 0 ? 1f : -1f,
                Mode = VisualMode.IntactBatched,
                BatchSlot = -1,
                ObservedFloorsStanding = spec.Profile.FloorsTotal,
                ObservedBreachMask = 0,
                ObservedHealth = spec.Profile.MaxHealth
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

        // 1. Process incoming combat events FIRST to distinguish live observed breaches/collapses
        //    from stale ruins newly revealed from fog of war.
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
                        BeginDustMaskedCollapse(idx, sec, prevFloors, obs.FloorsStanding);
                    }
                    else
                    {
                        // Re-observed ruins or non-live state jump: snap directly to settled battle-ruin visuals
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

        // 3. Advance active dust-masked structural shudder transitions
        for (int i = 0; i < _sections.Length; i++)
        {
            SectionVisual sec = _sections[i];
            if (!sec.AnimatingCollapse)
                continue;

            AdvanceCollapseTransition(i, sec, deltaSeconds);
        }

        // 4. Advance Variant A MultiMesh debris physics and dust clouds
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
            _debrisPool.SpawnBurst(hitPos, outward, spreadRadius: 1.5f, count: 14, minSpeed: 5.0f, maxSpeed: 13.5f, rubbleFloorY: terrainY, includeLargeSlabs: false);
            SpawnDustPuff(hitPos, outward * 2.8f + Vector3.Up * 1.6f, startScale: 1.4f, endScale: 5.2f, lifetime: 1.6f, peakAlpha: 0.62f);
            SpawnDustPuff(hitPos + Vector3.Up * 0.8f, outward * 1.4f + Vector3.Up * 2.4f, startScale: 1.1f, endScale: 4.4f, lifetime: 1.4f, peakAlpha: 0.55f);
        }
        else if (ev.Kind == CombatEventKind.StructureFloorCollapsed)
        {
            _soundBank.PlayFloorCollapse(hitPos);
            _debrisPool.SpawnBurst(hitPos, Vector3.Up, spreadRadius: 6.2f, count: 28, minSpeed: 4.0f, maxSpeed: 12.5f, rubbleFloorY: terrainY + rubbleHeight * 0.4f, includeLargeSlabs: true);
        }
        else if (ev.Kind == CombatEventKind.StructureCollapsed)
        {
            _soundBank.PlayFullCollapse(hitPos);
            _debrisPool.SpawnBurst(hitPos + Vector3.Up * 2.0f, Vector3.Up, spreadRadius: 6.8f, count: 44, minSpeed: 4.5f, maxSpeed: 14.5f, rubbleFloorY: terrainY + rubbleHeight * 0.65f, includeLargeSlabs: true);
        }
    }

    private void BeginDustMaskedCollapse(int secIdx, SectionVisual sec, byte fromFloors, byte toFloors)
    {
        DamagedSectionRig rig = AcquireDamagedRig(secIdx, sec);
        sec.AnimatingCollapse = true;
        sec.CollapseSwapped = false;
        sec.CollapseElapsed = 0f;
        sec.CollapseFromFloors = fromFloors;
        sec.CollapseToFloors = toFloors;

        // Show current pre-collapse state for 0.22 s while violent dust curtain & debris shroud the swap
        ConfigureRigFloors(rig, sec, fromFloors);

        // Dense multi-billow concrete dust curtain wrapping the collapsing section footprint
        float baseY = sec.BaseCenter.Y + toFloors * sec.Profile.FloorHeight;
        float topY = sec.BaseCenter.Y + fromFloors * sec.Profile.FloorHeight;
        float midY = (baseY + topY) * 0.5f;
        float halfX = (sec.Bounds.MaxX - sec.Bounds.MinX) * 0.45f;
        float halfZ = (sec.Bounds.MaxZ - sec.Bounds.MinZ) * 0.45f;

        int ringCount = toFloors == 0 ? 14 : 10;
        for (int p = 0; p < ringCount; p++)
        {
            float a = p * (MathF.Tau / ringCount) + (sec.Id.Value * 0.37f);
            float cx = MathF.Cos(a);
            float sz = MathF.Sin(a);
            Vector3 puffPos = new(
                sec.BaseCenter.X + cx * halfX * 0.82f,
                baseY + ((p % 3) * 0.35f + 0.15f) * MathF.Max(2.5f, topY - baseY),
                sec.BaseCenter.Z + sz * halfZ * 0.82f);
            Vector3 puffVel = new(cx * (3.2f + (p % 3) * 1.1f), 1.6f + (p % 2) * 1.4f, sz * (3.2f + (p % 3) * 1.1f));
            SpawnDustPuff(
                puffPos,
                puffVel,
                startScale: toFloors == 0 ? 3.8f : 3.0f,
                endScale: toFloors == 0 ? 11.5f : 8.8f,
                lifetime: toFloors == 0 ? 3.4f : 2.5f,
                peakAlpha: 0.78f);
        }

        // Core vertical dust column rising from the shattered interior
        for (int c = 0; c < 4; c++)
        {
            float cy = baseY + (c + 0.4f) * (MathF.Max(3.0f, topY - baseY) * 0.25f);
            SpawnDustPuff(
                new Vector3(sec.BaseCenter.X + (c - 1.5f) * 2.1f, cy, sec.BaseCenter.Z),
                new Vector3((c - 1.5f) * 1.2f, 2.8f + c * 0.5f, (c % 2 == 0 ? 1f : -1f) * 1.4f),
                startScale: 4.2f,
                endScale: 12.2f,
                lifetime: 3.6f,
                peakAlpha: 0.82f);
        }

        // Eject heavy concrete slabs and rubble chunks around the perimeter during collapse
        float floorY = sec.BaseCenter.Y + (toFloors == 0 ? sec.Profile.RubbleHeight * 0.65f : toFloors * sec.Profile.FloorHeight * 0.30f);
        int burstCount = Math.Clamp((fromFloors - toFloors) * 16, 20, 48);
        _debrisPool.SpawnBurst(
            new Vector3(sec.BaseCenter.X, midY, sec.BaseCenter.Z),
            Vector3.Up,
            spreadRadius: MathF.Max(halfX, halfZ) * 1.05f,
            count: burstCount,
            minSpeed: 3.8f,
            maxSpeed: 12.0f,
            rubbleFloorY: floorY,
            includeLargeSlabs: true);
    }

    private void AdvanceCollapseTransition(int secIdx, SectionVisual sec, float dt)
    {
        DamagedSectionRig? rig = sec.ActiveRig;
        if (rig is null)
        {
            sec.AnimatingCollapse = false;
            ApplySettledVisualState(secIdx, sec);
            return;
        }

        sec.CollapseElapsed += dt;

        // Phase 1 (0 .. 0.22 s): violent structural tremor right as dust curtain engulfs the section
        if (!sec.CollapseSwapped && sec.CollapseElapsed < CollapseSwapDelaySec)
        {
            float t = sec.CollapseElapsed / CollapseSwapDelaySec;
            float shakeX = MathF.Sin(sec.CollapseElapsed * 62f) * 0.22f * (1f - 0.3f * t);
            float shakeZ = MathF.Cos(sec.CollapseElapsed * 53f) * 0.18f * (1f - 0.3f * t);
            float sagY = -0.35f * t * t;
            rig.Root.Position = sec.BaseCenter + new Vector3(shakeX, sagY, shakeZ);
            return;
        }

        // Swap cleanly behind peak opaque dust cloud to asymmetric jagged shell or war-torn ground ruins
        if (!sec.CollapseSwapped)
        {
            sec.CollapseSwapped = true;
            rig.Root.Position = sec.BaseCenter;
            ConfigureRigFloors(rig, sec, sec.CollapseToFloors);
        }

        // Phase 2 (0.22 .. 0.42 s): brief residual settling vibration of the new ruined shell
        if (sec.CollapseElapsed < CollapseShudderDurationSec)
        {
            float settleT = (sec.CollapseElapsed - CollapseSwapDelaySec) / (CollapseShudderDurationSec - CollapseSwapDelaySec);
            float amp = 0.08f * (1f - settleT);
            rig.Root.Position = sec.BaseCenter + new Vector3(MathF.Sin(sec.CollapseElapsed * 44f) * amp, 0f, 0f);
            return;
        }

        rig.Root.Position = sec.BaseCenter;
        sec.AnimatingCollapse = false;
        ApplySettledVisualState(secIdx, sec);
    }

    private void ConfigureRigFloors(DamagedSectionRig rig, SectionVisual sec, byte standingFloors)
    {
        int total = sec.Profile.FloorsTotal;
        int standing = Math.Clamp((int)standingFloors, 0, total);

        if (standing == 0)
        {
            for (int f = 0; f < rig.MaxFloors; f++)
            {
                rig.IntactFloors[f].Visible = false;
                rig.BreachedFloors[f].Visible = false;
            }
            rig.JaggedCapNode.Visible = false;
            rig.RoofNode.Visible = false;
            rig.RubbleNodeA.Scale = sec.RubbleScale;
            rig.RubbleNodeB.Scale = sec.RubbleScale;
            rig.RubbleNodeA.Visible = !sec.UseRubbleVariantB;
            rig.RubbleNodeB.Visible = sec.UseRubbleVariantB;
            return;
        }

        for (int f = 0; f < rig.MaxFloors; f++)
        {
            bool inProfile = f < total;
            bool visible = inProfile && f < standing;
            // Topmost surviving floor of a partially collapsed shell always shows battle damage
            bool breached = visible && (((sec.ObservedBreachMask & (1 << f)) != 0) || (standing < total && f == standing - 1));
            float y = f * sec.Profile.FloorHeight;

            rig.IntactFloors[f].Position = new Vector3(0f, y, 0f);
            rig.IntactFloors[f].Rotation = Vector3.Zero;
            rig.IntactFloors[f].Visible = visible && !breached;

            rig.BreachedFloors[f].Position = new Vector3(0f, y, 0f);
            rig.BreachedFloors[f].Rotation = Vector3.Zero;
            rig.BreachedFloors[f].Visible = breached;
        }

        // Intact 5-story height keeps Roof_Cap; partially collapsed shells get asymmetric Top_Jagged_Cap!
        bool hasIntactRoof = standing >= total;
        rig.RoofNode.Position = new Vector3(0f, total * sec.Profile.FloorHeight, 0f);
        rig.RoofNode.Rotation = Vector3.Zero;
        rig.RoofNode.Visible = hasIntactRoof;

        rig.JaggedCapNode.Position = new Vector3(0f, standing * sec.Profile.FloorHeight, 0f);
        rig.JaggedCapNode.Rotation = sec.CapMirrorSign < 0f ? new Vector3(0f, MathF.PI, 0f) : Vector3.Zero;
        rig.JaggedCapNode.Scale = new Vector3(sec.FloorScale.X, sec.FloorScale.Y, sec.FloorScale.Z);
        rig.JaggedCapNode.Visible = !hasIntactRoof;

        rig.RubbleNodeA.Visible = false;
        rig.RubbleNodeB.Visible = false;
    }

    private void ApplySettledVisualState(int secIdx, SectionVisual sec)
    {
        if (sec.ObservedFloorsStanding >= sec.Profile.FloorsTotal && sec.ObservedBreachMask == 0)
        {
            ReleaseDamagedRig(sec);
            RemoveFromRubbleBatch(sec);
            AddToIntactBatch(secIdx, sec);
            return;
        }

        if (sec.ObservedFloorsStanding == 0)
        {
            ReleaseDamagedRig(sec);
            RemoveFromIntactBatch(sec);
            AddToRubbleBatch(secIdx, sec);
            return;
        }

        DamagedSectionRig rig = AcquireDamagedRig(secIdx, sec);
        rig.Root.Position = sec.BaseCenter;
        ConfigureRigFloors(rig, sec, sec.ObservedFloorsStanding);
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
        int variant = sec.UseRubbleVariantB ? 1 : 0;
        VisualMode targetMode = variant == 0 ? VisualMode.RubbleBatchedA : VisualMode.RubbleBatchedB;
        if (sec.Mode == targetMode && sec.BatchSlot >= 0)
            return;

        int slot = _rubbleBatchedCounts[variant]++;
        _rubbleSlotToSection[variant][slot] = secIdx;
        sec.Mode = targetMode;
        sec.BatchSlot = slot;

        _rubbleMultiMeshes[variant].SetInstanceTransform(slot, sec.RubbleWorldTransform);
        _rubbleMultiMeshes[variant].VisibleInstanceCount = _rubbleBatchedCounts[variant];
        _rubbleInstances[variant].Visible = true;
    }

    private void RemoveFromRubbleBatch(SectionVisual sec)
    {
        if (sec.Mode is not (VisualMode.RubbleBatchedA or VisualMode.RubbleBatchedB) || sec.BatchSlot < 0)
            return;

        int variant = sec.Mode == VisualMode.RubbleBatchedA ? 0 : 1;
        int slot = sec.BatchSlot;
        int lastSlot = --_rubbleBatchedCounts[variant];
        if (slot != lastSlot)
        {
            int movedSecIdx = _rubbleSlotToSection[variant][lastSlot];
            SectionVisual movedSec = _sections[movedSecIdx];
            _rubbleSlotToSection[variant][slot] = movedSecIdx;
            movedSec.BatchSlot = slot;
            _rubbleMultiMeshes[variant].SetInstanceTransform(slot, movedSec.RubbleWorldTransform);
        }

        sec.BatchSlot = -1;
        _rubbleMultiMeshes[variant].VisibleInstanceCount = _rubbleBatchedCounts[variant];
        _rubbleInstances[variant].Visible = _rubbleBatchedCounts[variant] > 0;
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
        rig.JaggedCapNode.Scale = sec.FloorScale;
        rig.JaggedCapNode.Visible = false;
        rig.RoofNode.Scale = new Vector3(sec.FloorScale.X, 1f, sec.FloorScale.Z);
        rig.RoofNode.Visible = false;
        rig.RubbleNodeA.Scale = sec.RubbleScale;
        rig.RubbleNodeA.Visible = false;
        rig.RubbleNodeB.Scale = sec.RubbleScale;
        rig.RubbleNodeB.Visible = false;
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

        MeshInstance3D jaggedCapMi = new()
        {
            Name = "TopJaggedCap",
            Mesh = _jaggedCapMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
            Visible = false
        };
        rigRoot.AddChild(jaggedCapMi);

        MeshInstance3D roofMi = new()
        {
            Name = "RoofCap",
            Mesh = _roofMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
            Visible = false
        };
        rigRoot.AddChild(roofMi);

        MeshInstance3D rubbleMiA = new()
        {
            Name = "RubbleMoundA",
            Mesh = _rubbleMeshA,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
            Visible = false
        };
        rigRoot.AddChild(rubbleMiA);

        MeshInstance3D rubbleMiB = new()
        {
            Name = "RubbleMoundB",
            Mesh = _rubbleMeshB,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
            Visible = false
        };
        rigRoot.AddChild(rubbleMiB);

        return new DamagedSectionRig
        {
            Root = rigRoot,
            IntactFloors = intactFloors,
            BreachedFloors = breachedFloors,
            JaggedCapNode = jaggedCapMi,
            RoofNode = roofMi,
            RubbleNodeA = rubbleMiA,
            RubbleNodeB = rubbleMiB,
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
            SpawnDustPuff(center, Vector3.Up * 1.8f, startScale: 2.2f, endScale: 7.2f, lifetime: 2.1f, peakAlpha: 0.68f);
        }
    }

    private void SpawnDustPuff(Vector3 position, Vector3 velocity, float startScale, float endScale, float lifetime, float peakAlpha = 0.62f)
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
        puff.PeakAlpha = peakAlpha;
        puff.Mesh.Position = position;
        puff.Mesh.Scale = new Vector3(startScale, startScale, startScale);
        puff.Mesh.Visible = true;
        puff.Material.AlbedoColor = new Color(0.71f, 0.69f, 0.64f, peakAlpha);
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
            puff.Velocity *= MathF.Max(0f, 1f - dt * 1.25f);
            float scale = puff.StartScale + (puff.EndScale - puff.StartScale) * MathF.Pow(t, 0.45f);
            // Hold opaque peak briefly during early bloom to mask geometry swap, then dissipate smoothly
            float fade = t < 0.18f ? 1f : (1f - (t - 0.18f) / 0.82f);
            float alpha = puff.PeakAlpha * fade * fade;

            puff.Mesh.Position = puff.Position;
            puff.Mesh.Scale = new Vector3(scale, scale * 0.80f, scale);
            puff.Material.AlbedoColor = new Color(0.70f, 0.68f, 0.63f, alpha);
        }
    }

    private static bool TryExtractMeshes(
        out Mesh intactLod0,
        out Mesh intactLod1,
        out Mesh intactLod2,
        out Mesh ground,
        out Mesh mid,
        out Mesh breached,
        out Mesh jaggedCap,
        out Mesh roof,
        out Mesh rubbleA,
        out Mesh rubbleB,
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
                    Mesh? jc = ExtractSingleMesh(sceneRoot, "Top_Jagged_Cap");
                    Mesh? rf = ExtractSingleMesh(sceneRoot, "Roof_Cap");
                    Mesh? rbA = ExtractSingleMesh(sceneRoot, "Rubble_Mound");
                    Mesh? rbB = ExtractSingleMesh(sceneRoot, "Rubble_Mound_B");
                    slabA = ExtractSingleMesh(sceneRoot, "Debris_Slab_A");
                    slabB = ExtractSingleMesh(sceneRoot, "Debris_Slab_B");
                    chunkC = ExtractSingleMesh(sceneRoot, "Debris_Chunk_C");

                    if (l0 is not null && l1 is not null && l2 is not null &&
                        g is not null && m is not null && b is not null && jc is not null &&
                        rf is not null && rbA is not null && rbB is not null)
                    {
                        intactLod0 = l0;
                        intactLod1 = l1;
                        intactLod2 = l2;
                        ground = g;
                        mid = m;
                        breached = b;
                        jaggedCap = jc;
                        roof = rf;
                        rubbleA = rbA;
                        rubbleB = rbB;
                        return true;
                    }
                }
                finally
                {
                    sceneRoot.QueueFree();
                }
            }
        }

        Mesh fullFallback = CreateFallbackMesh(new Vector3(ReferenceWidth, ReferenceFloorHeight * 5f, ReferenceDepth), new Color("b3afa4"));
        intactLod0 = fullFallback;
        intactLod1 = fullFallback;
        intactLod2 = fullFallback;
        ground = CreateFallbackMesh(new Vector3(ReferenceWidth, ReferenceFloorHeight, ReferenceDepth), new Color("9f9b90"));
        mid = CreateFallbackMesh(new Vector3(ReferenceWidth, ReferenceFloorHeight, ReferenceDepth), new Color("b3afa4"));
        breached = CreateFallbackMesh(new Vector3(ReferenceWidth * 0.96f, ReferenceFloorHeight * 0.96f, ReferenceDepth * 0.96f), new Color("746f68"));
        jaggedCap = CreateFallbackMesh(new Vector3(ReferenceWidth * 0.92f, 1.4f, ReferenceDepth * 0.92f), new Color("6e6961"));
        roof = CreateFallbackMesh(new Vector3(ReferenceWidth, 0.4f, ReferenceDepth), new Color("47484a"));
        rubbleA = CreateFallbackMesh(new Vector3(ReferenceWidth * 0.96f, ReferenceRubbleHeight, ReferenceDepth * 0.96f), new Color("7d7870"));
        rubbleB = rubbleA;
        return false;
    }

    private static Mesh? ExtractSingleMesh(Node3D root, string moduleName)
    {
        Node? moduleNode = root.FindChild(moduleName, recursive: true, owned: false);
        return moduleNode is not null ? FindFirstMesh(moduleNode) : null;
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
