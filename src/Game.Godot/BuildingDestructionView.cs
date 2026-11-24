using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Presentation-only modular building destruction view for Godot.
/// Consumes replicated StructureObservation snapshots and structure CombatEventObservation events,
/// swaps floor modules (intact -> breached -> cascading gravity drop -> rubble mound), drives
/// Variant A MultiMesh concrete debris physics and procedural audio, and notifies client LOS/FoW
/// systems whenever an observed building's standing height changes. Zero gameplay logic, zero
/// steady-state heap allocations.
/// </summary>
internal sealed class BuildingDestructionView : IDisposable
{
    internal const string PanelkaAssetPath = "res://Assets/Buildings/Panelka1464/panelka_1_464.glb";
    private const float ReferenceFootprint = 12.0f;
    private const float ReferenceFloorHeight = 3.1f;
    private const float ReferenceRubbleHeight = 1.5f;
    private const float CollapseAnimDurationSec = 1.45f;
    private const int MaxDustPuffs = 48;

    private sealed class ModuleTemplate
    {
        public readonly Mesh[] Meshes;
        public readonly Material?[] Materials;
        public readonly Transform3D[] Transforms;

        public ModuleTemplate(Mesh[] meshes, Material?[] materials, Transform3D[] transforms)
        {
            Meshes = meshes;
            Materials = materials;
            Transforms = transforms;
        }
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

        public Node3D Root = null!;
        public Node3D[] IntactFloors = null!;
        public Node3D[] BreachedFloors = null!;
        public Node3D RoofNode = null!;
        public Node3D RubbleNode = null!;

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
    private readonly SectionVisual[] _sections;
    private readonly Dictionary<StructureId, int> _idToSection;
    private readonly DustPuff[] _dustPuffs = new DustPuff[MaxDustPuffs];
    private readonly Random _localRng = new(46401);
    private readonly float[] _observedMaxYOverrides;
    private int _nextDustPuff;
    private uint _lastProcessedEventId;
    private bool _disposed;

    internal bool LoadedGlbAsset { get; }
    internal int SectionCount => _sections.Length;
    internal DebrisPhysicsPool DebrisPool => _debrisPool;
    internal BuildingDestructionSoundBank SoundBank => _soundBank;
    internal float[] ObservedMaxYOverrides => _observedMaxYOverrides;

    internal BuildingDestructionView(
        Node3D parent,
        StaticGeometry geometry,
        ReadOnlySpan<DestructibleBuildingSpec> specs,
        Action<StructureId, float>? onObservedHeightChanged = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(geometry);

        _geometry = geometry;
        _onObservedHeightChanged = onObservedHeightChanged;
        _root = new Node3D { Name = "DestructibleBuildings" };
        parent.AddChild(_root);

        _observedMaxYOverrides = new float[geometry.Structures.Count];
        Array.Fill(_observedMaxYOverrides, float.NaN);

        LoadedGlbAsset = TryExtractTemplates(
            out ModuleTemplate groundTpl,
            out ModuleTemplate midTpl,
            out ModuleTemplate breachedTpl,
            out ModuleTemplate roofTpl,
            out ModuleTemplate rubbleTpl,
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
        for (int i = 0; i < specs.Length; i++)
        {
            if (specs[i].Profile.IsValid && geometry.TryGetStructureIndex(specs[i].Id, out _))
                validCount++;
        }

        _sections = new SectionVisual[validCount];
        _idToSection = new Dictionary<StructureId, int>(validCount);

        int idx = 0;
        for (int i = 0; i < specs.Length; i++)
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

            Node3D sectionRoot = new()
            {
                Name = $"PanelkaSection_{spec.Id.Value}",
                Position = baseCenter,
                Rotation = new Vector3(0f, spec.YawRad, 0f)
            };
            _root.AddChild(sectionRoot);

            int floors = spec.Profile.FloorsTotal;
            Node3D[] intactFloors = new Node3D[floors];
            Node3D[] breachedFloors = new Node3D[floors];

            for (int f = 0; f < floors; f++)
            {
                float y = f * spec.Profile.FloorHeight;
                ModuleTemplate intactTpl = f == 0 ? groundTpl : midTpl;
                Node3D intactNode = InstantiateTemplate($"FloorIntact_{f}", intactTpl, floorScale);
                intactNode.Position = new Vector3(0f, y, 0f);
                sectionRoot.AddChild(intactNode);
                intactFloors[f] = intactNode;

                Node3D breachedNode = InstantiateTemplate($"FloorBreached_{f}", breachedTpl, floorScale);
                breachedNode.Position = new Vector3(0f, y, 0f);
                breachedNode.Visible = false;
                sectionRoot.AddChild(breachedNode);
                breachedFloors[f] = breachedNode;
            }

            Node3D roofNode = InstantiateTemplate("RoofCap", roofTpl, new Vector3(floorScale.X, 1f, floorScale.Z));
            roofNode.Position = new Vector3(0f, floors * spec.Profile.FloorHeight, 0f);
            sectionRoot.AddChild(roofNode);

            Node3D rubbleNode = InstantiateTemplate("RubbleMound", rubbleTpl, rubbleScale);
            rubbleNode.Position = Vector3.Zero;
            rubbleNode.Visible = false;
            sectionRoot.AddChild(rubbleNode);

            _sections[idx] = new SectionVisual
            {
                Id = spec.Id,
                GeometryIndex = geomIdx,
                Bounds = bounds,
                Profile = spec.Profile,
                YawRad = spec.YawRad,
                BaseCenter = baseCenter,
                FloorScale = floorScale,
                RubbleScale = rubbleScale,
                Root = sectionRoot,
                IntactFloors = intactFloors,
                BreachedFloors = breachedFloors,
                RoofNode = roofNode,
                RubbleNode = rubbleNode,
                ObservedFloorsStanding = spec.Profile.FloorsTotal,
                ObservedBreachMask = 0,
                ObservedHealth = spec.Profile.MaxHealth,
                CollapseTiltSign = (spec.Id.Value & 1) == 0 ? 1f : -1f
            };
            _idToSection[spec.Id] = idx;
            _observedMaxYOverrides[geomIdx] = bounds.MaxY;
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

    internal void Update(ClientReplica replica, float deltaSeconds)
    {
        if (_disposed)
            return;

        // 1. Synchronize visual modules with authoritative FoW-filtered StructureObservation records
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

                    if (obs.FloorsStanding < prevFloors)
                    {
                        BeginCollapseAnimation(sec, prevFloors, obs.FloorsStanding);
                    }
                    else
                    {
                        ApplyStaticFloorVisibility(sec);
                    }
                }
                else if (breachChanged)
                {
                    ApplyStaticFloorVisibility(sec);
                    byte newlyBreached = (byte)(obs.FloorBreachMask & ~prevMask);
                    if (newlyBreached != 0)
                    {
                        SpawnBreachDustAndDebris(sec, newlyBreached);
                    }
                }
            }
        }

        // 2. Process incoming combat events for crisp impact puffs, debris, and 3D audio
        ReadOnlySpan<CombatEventObservation> events = replica.Events;
        for (int i = 0; i < events.Length; i++)
        {
            CombatEventObservation ev = events[i];
            if (ev.EventId <= _lastProcessedEventId)
                continue;

            if (ev.Kind is CombatEventKind.StructureDamaged or CombatEventKind.StructureFloorCollapsed or CombatEventKind.StructureCollapsed)
            {
                HandleStructureEvent(in ev);
            }
            _lastProcessedEventId = ev.EventId;
        }

        // 3. Advance active floor cascade collapse animations
        for (int i = 0; i < _sections.Length; i++)
        {
            SectionVisual sec = _sections[i];
            if (!sec.AnimatingCollapse)
                continue;

            AdvanceCollapseAnimation(sec, deltaSeconds);
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
            _debrisPool.SpawnBurst(hitPos, outward, spreadRadius: 1.6f, count: 10, minSpeed: 4.5f, maxSpeed: 12.5f, rubbleFloorY: terrainY, includeLargeSlabs: false);
            SpawnDustPuff(hitPos, outward * 2.5f + Vector3.Up * 1.8f, startScale: 1.2f, endScale: 4.8f, lifetime: 1.4f);
            SpawnDustPuff(hitPos + Vector3.Up * 0.6f, Vector3.Up * 2.4f, startScale: 0.9f, endScale: 3.6f, lifetime: 1.2f);
        }
        else if (ev.Kind == CombatEventKind.StructureFloorCollapsed)
        {
            _soundBank.PlayFloorCollapse(hitPos);
            _debrisPool.SpawnBurst(hitPos, Vector3.Up, spreadRadius: 4.5f, count: 22, minSpeed: 3.5f, maxSpeed: 11.5f, rubbleFloorY: terrainY + rubbleHeight * 0.4f, includeLargeSlabs: true);
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
            _debrisPool.SpawnBurst(hitPos + Vector3.Up * 2.2f, Vector3.Up, spreadRadius: 5.2f, count: 34, minSpeed: 4.0f, maxSpeed: 13.5f, rubbleFloorY: terrainY + rubbleHeight * 0.7f, includeLargeSlabs: true);
            for (int p = 0; p < 6; p++)
            {
                float a = p * (MathF.Tau / 6f);
                Vector3 dir = new(MathF.Cos(a) * 4.8f, 2.6f, MathF.Sin(a) * 4.8f);
                SpawnDustPuff(hitPos + dir * 0.45f, dir, startScale: 2.8f, endScale: 10.5f, lifetime: 3.0f);
            }
        }
    }

    private void BeginCollapseAnimation(SectionVisual sec, byte fromFloors, byte toFloors)
    {
        sec.AnimatingCollapse = true;
        sec.CollapseElapsed = 0f;
        sec.CollapseFromFloors = fromFloors;
        sec.CollapseToFloors = toFloors;

        // Show rubble skirt immediately when any upper floor collapses so debris impacts onto rubble
        sec.RubbleNode.Visible = toFloors == 0;
        if (toFloors == 0)
        {
            sec.RubbleNode.Scale = new Vector3(sec.RubbleScale.X, sec.RubbleScale.Y * 0.35f, sec.RubbleScale.Z);
        }

        // Spawn heavy concrete slabs along the collapsing section perimeter
        float topY = sec.BaseCenter.Y + fromFloors * sec.Profile.FloorHeight * 0.72f;
        float floorY = sec.BaseCenter.Y + (toFloors == 0 ? sec.Profile.RubbleHeight * 0.7f : toFloors * sec.Profile.FloorHeight * 0.25f);
        int burstCount = Math.Clamp((fromFloors - toFloors) * 14, 14, 42);
        _debrisPool.SpawnBurst(
            new Vector3(sec.BaseCenter.X, topY, sec.BaseCenter.Z),
            Vector3.Up,
            spreadRadius: (sec.Bounds.MaxX - sec.Bounds.MinX) * 0.42f,
            count: burstCount,
            minSpeed: 3.0f,
            maxSpeed: 10.5f,
            rubbleFloorY: floorY,
            includeLargeSlabs: true);
    }

    private void AdvanceCollapseAnimation(SectionVisual sec, float dt)
    {
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
                // Standing lower floors stay grounded at exact floor height
                float restY = f * sec.Profile.FloorHeight;
                sec.IntactFloors[f].Position = new Vector3(0f, restY, 0f);
                sec.IntactFloors[f].Rotation = Vector3.Zero;
                sec.IntactFloors[f].Visible = !breached;
                sec.BreachedFloors[f].Position = new Vector3(0f, restY, 0f);
                sec.BreachedFloors[f].Rotation = Vector3.Zero;
                sec.BreachedFloors[f].Visible = breached;
            }
            else if (f < sec.CollapseFromFloors)
            {
                // Falling floors pancake downward with slight stagger and structural tilt
                float stagger = (f - sec.CollapseToFloors) * 0.08f;
                float localT = Math.Clamp((progress - stagger) / MathF.Max(0.25f, 1f - stagger), 0f, 1f);
                float fallEase = localT * localT; // gravity acceleration
                float startY = f * sec.Profile.FloorHeight;
                float curY = startY + (targetTopY - startY) * fallEase;
                float tilt = sec.CollapseTiltSign * 0.14f * fallEase * (1f + 0.15f * f);
                float jitterX = MathF.Sin(sec.CollapseElapsed * 28f + f * 1.7f) * 0.14f * (1f - localT);
                bool stillVisible = localT < 0.94f;

                // Show breached version while collapsing for dramatic exposed concrete edges
                sec.IntactFloors[f].Visible = false;
                sec.BreachedFloors[f].Visible = stillVisible;
                sec.BreachedFloors[f].Position = new Vector3(jitterX, curY, 0f);
                sec.BreachedFloors[f].Rotation = new Vector3(tilt * 0.65f, 0f, tilt);
            }
            else
            {
                sec.IntactFloors[f].Visible = false;
                sec.BreachedFloors[f].Visible = false;
            }
        }

        // Roof drops along with the top falling floor and breaks up before hitting rubble
        if (sec.CollapseFromFloors == total && sec.CollapseToFloors < total)
        {
            float roofEase = progress * progress;
            float roofStartY = total * sec.Profile.FloorHeight;
            sec.RoofNode.Position = new Vector3(0f, roofStartY + (targetTopY - roofStartY) * roofEase, 0f);
            sec.RoofNode.Rotation = new Vector3(0.18f * roofEase, 0f, -sec.CollapseTiltSign * 0.16f * roofEase);
            sec.RoofNode.Visible = progress < 0.85f;
        }
        else
        {
            sec.RoofNode.Visible = sec.CollapseToFloors == total;
        }

        // Rubble heap swells smoothly as falling slabs pile into it
        if (sec.CollapseToFloors == 0)
        {
            sec.RubbleNode.Visible = true;
            float rise = 0.35f + 0.65f * MathF.Min(1f, progress * 1.25f);
            sec.RubbleNode.Scale = new Vector3(sec.RubbleScale.X, sec.RubbleScale.Y * rise, sec.RubbleScale.Z);
        }

        if (progress >= 1f)
        {
            sec.AnimatingCollapse = false;
            ApplyStaticFloorVisibility(sec);
        }
    }

    private static void ApplyStaticFloorVisibility(SectionVisual sec)
    {
        int total = sec.Profile.FloorsTotal;
        int standing = sec.ObservedFloorsStanding;
        for (int f = 0; f < total; f++)
        {
            bool visible = f < standing;
            bool breached = visible && ((sec.ObservedBreachMask & (1 << f)) != 0);
            float y = f * sec.Profile.FloorHeight;

            sec.IntactFloors[f].Position = new Vector3(0f, y, 0f);
            sec.IntactFloors[f].Rotation = Vector3.Zero;
            sec.IntactFloors[f].Visible = visible && !breached;

            sec.BreachedFloors[f].Position = new Vector3(0f, y, 0f);
            sec.BreachedFloors[f].Rotation = Vector3.Zero;
            sec.BreachedFloors[f].Visible = breached;
        }

        sec.RoofNode.Position = new Vector3(0f, total * sec.Profile.FloorHeight, 0f);
        sec.RoofNode.Rotation = Vector3.Zero;
        sec.RoofNode.Visible = standing >= total;

        sec.RubbleNode.Scale = sec.RubbleScale;
        sec.RubbleNode.Visible = standing == 0;
    }

    private void SpawnBreachDustAndDebris(SectionVisual sec, byte newlyBreachedMask)
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

    private static bool TryExtractTemplates(
        out ModuleTemplate ground,
        out ModuleTemplate mid,
        out ModuleTemplate breached,
        out ModuleTemplate roof,
        out ModuleTemplate rubble,
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
                    ModuleTemplate? g = ExtractModule(sceneRoot, "Floor_Ground_Intact");
                    ModuleTemplate? m = ExtractModule(sceneRoot, "Floor_Mid_Intact");
                    ModuleTemplate? b = ExtractModule(sceneRoot, "Floor_Breached");
                    ModuleTemplate? rf = ExtractModule(sceneRoot, "Roof_Cap");
                    ModuleTemplate? rb = ExtractModule(sceneRoot, "Rubble_Mound");
                    slabA = ExtractSingleMesh(sceneRoot, "Debris_Slab_A");
                    slabB = ExtractSingleMesh(sceneRoot, "Debris_Slab_B");
                    chunkC = ExtractSingleMesh(sceneRoot, "Debris_Chunk_C");

                    if (g is not null && m is not null && b is not null && rf is not null && rb is not null)
                    {
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
        ground = CreateFallbackTemplate(new Vector3(ReferenceFootprint, ReferenceFloorHeight, ReferenceFootprint), new Color("9f9b90"));
        mid = CreateFallbackTemplate(new Vector3(ReferenceFootprint, ReferenceFloorHeight, ReferenceFootprint), new Color("b3afa4"));
        breached = CreateFallbackTemplate(new Vector3(ReferenceFootprint * 0.96f, ReferenceFloorHeight * 0.96f, ReferenceFootprint * 0.96f), new Color("746f68"));
        roof = CreateFallbackTemplate(new Vector3(ReferenceFootprint, 0.4f, ReferenceFootprint), new Color("47484a"));
        rubble = CreateFallbackTemplate(new Vector3(ReferenceFootprint * 0.96f, ReferenceRubbleHeight, ReferenceFootprint * 0.96f), new Color("7d7870"));
        return false;
    }

    private static ModuleTemplate? ExtractModule(Node3D root, string moduleName)
    {
        Node? moduleNode = root.FindChild(moduleName, recursive: true, owned: false);
        if (moduleNode is null)
            return null;

        List<Mesh> meshes = new();
        List<Material?> materials = new();
        List<Transform3D> transforms = new();
        CollectMeshes(moduleNode, Transform3D.Identity, meshes, materials, transforms);
        if (meshes.Count == 0)
            return null;

        return new ModuleTemplate(meshes.ToArray(), materials.ToArray(), transforms.ToArray());
    }

    private static Mesh? ExtractSingleMesh(Node3D root, string moduleName)
    {
        ModuleTemplate? tpl = ExtractModule(root, moduleName);
        return tpl is not null && tpl.Meshes.Length > 0 ? tpl.Meshes[0] : null;
    }

    private static void CollectMeshes(
        Node node,
        Transform3D accumulated,
        List<Mesh> meshes,
        List<Material?> materials,
        List<Transform3D> transforms)
    {
        int count = node.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            Node child = node.GetChild(i);
            Transform3D next = child is Node3D n3d ? accumulated * n3d.Transform : accumulated;
            if (child is MeshInstance3D mi && mi.Mesh is not null)
            {
                meshes.Add(mi.Mesh);
                materials.Add(mi.MaterialOverride);
                transforms.Add(next);
            }
            CollectMeshes(child, next, meshes, materials, transforms);
        }
    }

    private static Node3D InstantiateTemplate(string name, ModuleTemplate template, Vector3 scale)
    {
        Node3D container = new() { Name = name, Scale = scale };
        for (int i = 0; i < template.Meshes.Length; i++)
        {
            MeshInstance3D mi = new()
            {
                Mesh = template.Meshes[i],
                MaterialOverride = template.Materials[i],
                Transform = template.Transforms[i],
                CastShadow = GeometryInstance3D.ShadowCastingSetting.On
            };
            container.AddChild(mi);
        }
        return container;
    }

    private static ModuleTemplate CreateFallbackTemplate(Vector3 size, Color color)
    {
        BoxMesh box = new()
        {
            Size = size,
            Material = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.90f }
        };
        return new ModuleTemplate(
            [box],
            [null],
            [new Transform3D(Basis.Identity, new Vector3(0f, size.Y * 0.5f, 0f))]);
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
