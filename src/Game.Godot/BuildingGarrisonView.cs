using DeadGround.Engine;
using DeadGround.Engine.Net;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

/// <summary>
/// Client presentation for building garrisons (Stage 2, PR 2.2):
/// - Renders data-driven window and rooftop infantry figures via a single MultiMesh (1 draw call).
/// - Renders Variant A X-Ray silhouettes through walls/roofs for own interior garrisons via a
///   second NoDepthTest MultiMesh (1 draw call), preserving building batching.
/// - Projects pre-pooled clickable tactical garrison badges over occupied/spotted sections.
/// - Strictly Fog-of-War driven from ClientReplica (OwnUnits + Contacts only).
/// - Zero managed heap allocations during steady-state Update frames.
/// </summary>
internal sealed class BuildingGarrisonView : IDisposable
{
    private const int MaxInstances = 512;
    private const int MaxBadges = 64;
    private const int MaxScratchUnitsPerBuilding = 16;
    private const int MaxAdjacentPerBuilding = 8;

    private readonly struct WorldFiringPort
    {
        public readonly float WorldX;
        public readonly float LocalY;
        public readonly float WorldZ;
        public readonly float YawRad;
        public readonly float NormalX;
        public readonly float NormalZ;

        public WorldFiringPort(float worldX, float localY, float worldZ, float yawRad, float normalX, float normalZ)
        {
            WorldX = worldX;
            LocalY = localY;
            WorldZ = worldZ;
            YawRad = yawRad;
            NormalX = normalX;
            NormalZ = normalZ;
        }
    }

    private readonly struct WorldRoofPort
    {
        public readonly float WorldX;
        public readonly float WorldY;
        public readonly float WorldZ;
        public readonly float YawRad;

        public WorldRoofPort(float worldX, float worldY, float worldZ, float yawRad)
        {
            WorldX = worldX;
            WorldY = worldY;
            WorldZ = worldZ;
            YawRad = yawRad;
        }
    }

    private sealed class BuildingVisualEntry
    {
        public StructureId Id;
        public OpaqueStructure Bounds = null!;
        public StructureDestructionProfile DestructionProfile;
        public BuildingGarrisonProfile GarrisonProfile = null!;
        public NumericsVector2 CenterXZ;
        public NumericsVector2 EntranceWorldPos;
        public float GroundY;
        public float YawRad;
        public WorldFiringPort[] FiringPorts = [];
        public WorldRoofPort[] RoofPorts = [];
        public readonly StructureId[] AdjacentStructures = new StructureId[MaxAdjacentPerBuilding];
        public int AdjacentCount;

        // Per-frame scratch counts & facing directions (reset each Update without allocating)
        public int OwnInteriorCount;
        public int OwnRoofCount;
        public bool OwnSelected;
        public int HostileCurrentInteriorCount;
        public int HostileCurrentRoofCount;
        public int HostileLastKnownCount;
        public byte ObservedFloorsStanding;
        public readonly float[] OwnInteriorAimYaws = new float[MaxScratchUnitsPerBuilding];
        public readonly bool[] OwnInteriorHasAim = new bool[MaxScratchUnitsPerBuilding];
        public readonly float[] HostileInteriorAimYaws = new float[MaxScratchUnitsPerBuilding];
        public readonly bool[] HostileInteriorHasAim = new bool[MaxScratchUnitsPerBuilding];
    }

    private sealed class BadgeSlot
    {
        public Button Button = null!;
        public StyleBoxFlat Style = null!;
        public StructureId AssignedStructureId;
        public int CachedOwnTotal = -1;
        public int CachedOwnRoof = -1;
        public int CachedHostileCurrent = -1;
        public int CachedHostileRoof = -1;
        public int CachedHostileLastKnown = -1;
        public bool CachedSelected;
        public bool CachedHovered;
    }

    private static readonly Color OwnFigureColor = new(0.45f, 0.56f, 0.37f, 1.0f);
    private static readonly Color OwnRoofFigureColor = new(0.54f, 0.66f, 0.44f, 1.0f);
    private static readonly Color HostileFigureColor = new(0.86f, 0.30f, 0.24f, 1.0f);
    private static readonly Color XRayIdleColor = new(0.28f, 0.88f, 0.78f, 0.36f);
    private static readonly Color XRaySelectedColor = new(0.36f, 1.00f, 0.84f, 0.72f);
    private static readonly Color XRayCqbColorA = new(1.00f, 0.56f, 0.22f, 0.68f);
    private static readonly Color XRayCqbColorB = new(1.00f, 0.82f, 0.32f, 0.86f);

    private static readonly Color BadgeOwnBg = new(0.08f, 0.14f, 0.18f, 0.88f);
    private static readonly Color BadgeOwnSelectedBg = new(0.11f, 0.25f, 0.28f, 0.94f);
    private static readonly Color BadgeOwnBorder = new(0.30f, 0.78f, 0.72f, 0.95f);
    private static readonly Color BadgeOwnSelectedBorder = new(0.46f, 1.00f, 0.86f, 1.0f);
    private static readonly Color BadgeHostileBg = new(0.22f, 0.08f, 0.08f, 0.88f);
    private static readonly Color BadgeHostileBorder = new(0.92f, 0.34f, 0.26f, 0.96f);
    private static readonly Color BadgeCqbBg = new(0.32f, 0.11f, 0.06f, 0.95f);
    private static readonly Color BadgeCqbBorderA = new(1.00f, 0.44f, 0.14f, 1.0f);
    private static readonly Color BadgeCqbBorderB = new(1.00f, 0.84f, 0.24f, 1.0f);
    private static readonly Color BadgeLastKnownBg = new(0.16f, 0.12f, 0.08f, 0.72f);
    private static readonly Color BadgeLastKnownBorder = new(0.76f, 0.58f, 0.30f, 0.75f);

    private readonly Node3D _root;
    private readonly MultiMesh _figuresMesh;
    private readonly MultiMeshInstance3D _figuresInstance;
    private readonly MultiMesh _xrayMesh;
    private readonly MultiMeshInstance3D _xrayInstance;
    private readonly MeshInstance3D _entranceHoverRing;
    private Control? _badgeContainer;
    private BadgeSlot[] _badgePool = [];
    private readonly Action<StructureId>? _onBadgeClicked;

    private readonly Dictionary<StructureId, BuildingVisualEntry> _byId;
    private readonly BuildingVisualEntry[] _entries;
    private readonly bool[] _portUsedMask = new bool[32];
    private readonly int _entryCount;
    private float _cqbPulseTime;
    private bool _disposed;

    public int RegisteredBuildingCount => _entryCount;
    public int ActiveFigureCount { get; private set; }
    public int ActiveXRayCount { get; private set; }
    public int VisibleBadgeCount { get; private set; }
    public int ContestedBuildingCount { get; private set; }
    public int ActiveMultiMeshDrawCalls =>
        (ActiveFigureCount > 0 ? 1 : 0) + (ActiveXRayCount > 0 ? 1 : 0);

    public BuildingGarrisonView(
        Node3D parent3D,
        StaticGeometry geometry,
        IReadOnlyList<DestructibleBuildingSpec> specs,
        Control? badgeOverlayParent = null,
        Action<StructureId>? onBadgeClicked = null)
    {
        ArgumentNullException.ThrowIfNull(parent3D);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(specs);

        _onBadgeClicked = onBadgeClicked;
        _root = new Node3D { Name = "BuildingGarrisonView" };
        parent3D.AddChild(_root);

        int cap = Math.Min(specs.Count, WorldState.MaxDestructibleStructures);
        _byId = new Dictionary<StructureId, BuildingVisualEntry>(cap);
        _entries = new BuildingVisualEntry[cap];

        int count = 0;
        for (int i = 0; i < specs.Count && count < cap; i++)
        {
            DestructibleBuildingSpec spec = specs[i];
            if (_byId.ContainsKey(spec.Id)) continue;
            if (!geometry.TryGetStructureIndex(spec.Id, out int geomIdx)) continue;

            OpaqueStructure bounds = geometry.Structures[geomIdx];
            float sizeX = bounds.MaxX - bounds.MinX;
            float sizeZ = bounds.MaxZ - bounds.MinZ;
            bool rotated90 = MathF.Abs(MathF.Sin(spec.YawRad)) > 0.707f;
            float localW = rotated90 ? sizeZ : sizeX;
            float localD = rotated90 ? sizeX : sizeZ;
            BuildingGarrisonProfile gProfile = spec.GarrisonProfile ?? BuildingGarrisonProfile.CreateDefault(localW, localD);

            NumericsVector2 centerXZ = new((bounds.MinX + bounds.MaxX) * 0.5f, (bounds.MinZ + bounds.MaxZ) * 0.5f);
            NumericsVector2 entrance = GarrisonMath.ComputeWorldEntrance(bounds, spec.YawRad, gProfile);
            float groundY = geometry.HeightMap.GetHeight(entrance.X, entrance.Y);

            WorldFiringPort[] worldFiringPorts = new WorldFiringPort[gProfile.FiringPorts.Length];
            for (int p = 0; p < gProfile.FiringPorts.Length; p++)
            {
                BuildingFiringPort fp = gProfile.FiringPorts[p];
                NumericsVector3 wp = GarrisonMath.RotateLocalPoint(
                    new NumericsVector3(centerXZ.X, 0f, centerXZ.Y),
                    spec.YawRad,
                    new NumericsVector3(fp.LocalX, 0f, fp.LocalZ));
                NumericsVector2 normal = GarrisonMath.RotateLocalDirection(spec.YawRad, fp.NormalX, fp.NormalZ);
                float yaw = MathF.Atan2(normal.X, normal.Y);
                // Place figure tucked slightly into the window opening so head, shoulders, and rifle read cleanly
                worldFiringPorts[p] = new WorldFiringPort(
                    wp.X + normal.X * 0.06f,
                    fp.LocalY,
                    wp.Z + normal.Y * 0.06f,
                    yaw,
                    normal.X,
                    normal.Y);
            }

            WorldRoofPort[] worldRoofPorts = new WorldRoofPort[gProfile.RoofPorts.Length];
            for (int p = 0; p < gProfile.RoofPorts.Length; p++)
            {
                BuildingRoofPort rp = gProfile.RoofPorts[p];
                NumericsVector2 offset = GarrisonMath.RotateLocalDirection(spec.YawRad, rp.LocalX, rp.LocalZ);
                float yaw = offset.LengthSquared() > 1e-4f
                    ? MathF.Atan2(offset.X, offset.Y)
                    : spec.YawRad;
                worldRoofPorts[p] = new WorldRoofPort(
                    centerXZ.X + offset.X,
                    bounds.MaxY,
                    centerXZ.Y + offset.Y,
                    yaw);
            }

            BuildingVisualEntry entry = new()
            {
                Id = spec.Id,
                Bounds = bounds,
                DestructionProfile = spec.Profile,
                GarrisonProfile = gProfile,
                CenterXZ = centerXZ,
                EntranceWorldPos = entrance,
                GroundY = groundY,
                YawRad = spec.YawRad,
                FiringPorts = worldFiringPorts,
                RoofPorts = worldRoofPorts,
                ObservedFloorsStanding = spec.Profile.FloorsTotal
            };
            _byId[spec.Id] = entry;
            for (int prev = 0; prev < count; prev++)
            {
                BuildingVisualEntry other = _entries[prev];
                if (AreBoundsAdjacent(entry.Bounds, other.Bounds))
                {
                    LinkAdjacent(entry, other.Id);
                    LinkAdjacent(other, entry.Id);
                }
            }
            _entries[count++] = entry;
        }
        _entryCount = count;

        ArrayMesh soldierMesh = CreateSoldierSilhouetteMesh();

        // MultiMesh #1: Normal depth-tested window and roof figures (1 draw call for entire map)
        StandardMaterial3D figureMat = new()
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 0.85f,
            Metallic = 0.05f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        _figuresMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            InstanceCount = MaxInstances,
            VisibleInstanceCount = 0,
            Mesh = soldierMesh
        };
        _figuresInstance = new MultiMeshInstance3D
        {
            Name = "GarrisonFiguresMultiMesh",
            Multimesh = _figuresMesh,
            MaterialOverride = figureMat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        _root.AddChild(_figuresInstance);

        // MultiMesh #2: Variant A X-Ray silhouettes for own interior garrisons (1 draw call for entire map)
        StandardMaterial3D xrayMat = new()
        {
            VertexColorUseAsAlbedo = true,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            RenderPriority = 10
        };
        _xrayMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            InstanceCount = MaxInstances,
            VisibleInstanceCount = 0,
            Mesh = soldierMesh
        };
        _xrayInstance = new MultiMeshInstance3D
        {
            Name = "GarrisonXRayMultiMesh",
            Multimesh = _xrayMesh,
            MaterialOverride = xrayMat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        _root.AddChild(_xrayInstance);

        // Hover entrance highlight ring (shown when hovering a garrisonable building with infantry selected)
        _entranceHoverRing = new MeshInstance3D
        {
            Name = "GarrisonEntranceHoverRing",
            Mesh = CreateEntranceRingMesh(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        _entranceHoverRing.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.32f, 0.96f, 0.78f, 0.85f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            NoDepthTest = true,
            RenderPriority = 9
        };
        _root.AddChild(_entranceHoverRing);

        // Pre-pooled clickable tactical garrison badges (can also be attached later via AttachBadgeOverlay)
        if (badgeOverlayParent is not null)
        {
            AttachBadgeOverlay(badgeOverlayParent);
        }
    }

    public void AttachBadgeOverlay(Control badgeOverlayParent)
    {
        ArgumentNullException.ThrowIfNull(badgeOverlayParent);
        if (_disposed || _badgeContainer is not null) return;

        _badgeContainer = new Control
        {
            Name = "GarrisonBadgeLayer",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _badgeContainer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        badgeOverlayParent.AddChild(_badgeContainer);

        _badgePool = new BadgeSlot[MaxBadges];
        for (int i = 0; i < MaxBadges; i++)
        {
            StyleBoxFlat style = new()
            {
                BgColor = BadgeOwnBg,
                BorderColor = BadgeOwnBorder,
                BorderWidthLeft = 1,
                BorderWidthRight = 1,
                BorderWidthTop = 1,
                BorderWidthBottom = 1,
                CornerRadiusTopLeft = 3,
                CornerRadiusTopRight = 3,
                CornerRadiusBottomLeft = 3,
                CornerRadiusBottomRight = 3,
                ContentMarginLeft = 6,
                ContentMarginRight = 6,
                ContentMarginTop = 2,
                ContentMarginBottom = 2
            };
            Button btn = new()
            {
                Visible = false,
                FocusMode = Control.FocusModeEnum.None,
                MouseFilter = Control.MouseFilterEnum.Stop,
                CustomMinimumSize = new Vector2(46, 20)
            };
            btn.AddThemeStyleboxOverride("normal", style);
            btn.AddThemeStyleboxOverride("hover", style);
            btn.AddThemeStyleboxOverride("pressed", style);
            btn.AddThemeFontSizeOverride("font_size", 11);
            btn.AddThemeColorOverride("font_color", new Color(0.92f, 0.98f, 0.96f));
            BadgeSlot slot = new() { Button = btn, Style = style };
            btn.Pressed += () =>
            {
                if (slot.AssignedStructureId != default)
                {
                    _onBadgeClicked?.Invoke(slot.AssignedStructureId);
                }
            };
            _badgeContainer.AddChild(btn);
            _badgePool[i] = slot;
        }
    }

    public bool IsGarrisonable(StructureId structureId) => _byId.ContainsKey(structureId);

    public bool AreSectionsAdjacent(StructureId a, StructureId b)
    {
        if (a == b || !_byId.TryGetValue(a, out BuildingVisualEntry? entry))
            return false;
        for (int i = 0; i < entry.AdjacentCount; i++)
        {
            if (entry.AdjacentStructures[i] == b)
                return true;
        }
        return false;
    }

    private static bool AreBoundsAdjacent(OpaqueStructure a, OpaqueStructure b)
    {
        float gapX = MathF.Max(0f, MathF.Max(a.MinX - b.MaxX, b.MinX - a.MaxX));
        float gapZ = MathF.Max(0f, MathF.Max(a.MinZ - b.MaxZ, b.MinZ - a.MaxZ));
        float overlapX = MathF.Min(a.MaxX, b.MaxX) - MathF.Max(a.MinX, b.MinX);
        float overlapZ = MathF.Min(a.MaxZ, b.MaxZ) - MathF.Max(a.MinZ, b.MinZ);
        return (gapX <= GarrisonSystem.AdjacencyMaxGapMeters && overlapZ >= GarrisonSystem.AdjacencyMinOverlapMeters) ||
            (gapZ <= GarrisonSystem.AdjacencyMaxGapMeters && overlapX >= GarrisonSystem.AdjacencyMinOverlapMeters);
    }

    private static void LinkAdjacent(BuildingVisualEntry entry, StructureId neighborId)
    {
        for (int i = 0; i < entry.AdjacentCount; i++)
        {
            if (entry.AdjacentStructures[i] == neighborId)
                return;
        }
        if (entry.AdjacentCount < entry.AdjacentStructures.Length)
        {
            entry.AdjacentStructures[entry.AdjacentCount++] = neighborId;
        }
    }

    public bool TryGetBuildingInfo(StructureId structureId, out NumericsVector2 entranceWorldPos, out int capacity, out byte floorsTotal)
    {
        if (_byId.TryGetValue(structureId, out BuildingVisualEntry? entry))
        {
            entranceWorldPos = entry.EntranceWorldPos;
            capacity = entry.GarrisonProfile.Capacity;
            floorsTotal = entry.DestructionProfile.FloorsTotal;
            return true;
        }
        entranceWorldPos = default;
        capacity = 0;
        floorsTotal = 0;
        return false;
    }

    public bool TryGetBadgeAtScreenPoint(Vector2 screenPoint, out StructureId structureId)
    {
        for (int i = 0; i < VisibleBadgeCount && i < _badgePool.Length; i++)
        {
            BadgeSlot slot = _badgePool[i];
            if (slot.Button.Visible && slot.Button.GetGlobalRect().HasPoint(screenPoint))
            {
                structureId = slot.AssignedStructureId;
                return true;
            }
        }
        structureId = default;
        return false;
    }

    public void Update(
        ClientReplica replica,
        HashSet<EntityId>? selectedUnits,
        StructureId hoveredStructure,
        bool showEntranceHint,
        Camera3D? camera,
        float deltaSeconds = 0.016f)
    {
        if (_disposed) return;

        _cqbPulseTime += MathF.Max(0f, deltaSeconds);
        float cqbPulse = 0.5f + 0.5f * MathF.Sin(_cqbPulseTime * 7.5f);

        // 1. Reset per-building frame counters (0 allocations)
        for (int i = 0; i < _entryCount; i++)
        {
            BuildingVisualEntry e = _entries[i];
            e.OwnInteriorCount = 0;
            e.OwnRoofCount = 0;
            e.OwnSelected = false;
            e.HostileCurrentInteriorCount = 0;
            e.HostileCurrentRoofCount = 0;
            e.HostileLastKnownCount = 0;
            e.ObservedFloorsStanding = replica.TryGetStructure(e.Id, out StructureObservation obs)
                ? obs.FloorsStanding
                : e.DestructionProfile.FloorsTotal;
        }

        // 2. Tally own garrisoned units from ClientReplica.OwnUnits (including active aiming yaw for window shifting)
        ReadOnlySpan<OwnUnitObservation> ownUnits = replica.OwnUnits;
        for (int i = 0; i < ownUnits.Length; i++)
        {
            ref readonly OwnUnitObservation u = ref ownUnits[i];
            if (u.GarrisonStructureId == default || u.GarrisonSlotKind == GarrisonSlotKind.None)
                continue;
            if (!_byId.TryGetValue(u.GarrisonStructureId, out BuildingVisualEntry? entry))
                continue;

            if (u.GarrisonSlotKind == GarrisonSlotKind.Roof)
            {
                entry.OwnRoofCount++;
            }
            else
            {
                int slotIdx = entry.OwnInteriorCount;
                if (slotIdx < MaxScratchUnitsPerBuilding)
                {
                    entry.OwnInteriorAimYaws[slotIdx] = u.TurretYaw;
                    entry.OwnInteriorHasAim[slotIdx] = u.AttackStatus != AttackExecutionStatus.None;
                }
                entry.OwnInteriorCount++;
            }

            if (selectedUnits is not null && selectedUnits.Contains(u.Id))
                entry.OwnSelected = true;
        }

        // 3. Tally spotted hostile garrison contacts strictly from ClientReplica.Contacts (FoW safe)
        ReadOnlySpan<ContactObservation> contacts = replica.Contacts;
        for (int i = 0; i < contacts.Length; i++)
        {
            ref readonly ContactObservation c = ref contacts[i];
            if (c.GarrisonStructureId == default || c.GarrisonSlotKind == GarrisonSlotKind.None)
                continue;
            if (!_byId.TryGetValue(c.GarrisonStructureId, out BuildingVisualEntry? entry))
                continue;

            if (c.State == ContactState.Current)
            {
                if (c.GarrisonSlotKind == GarrisonSlotKind.Roof)
                {
                    entry.HostileCurrentRoofCount++;
                }
                else
                {
                    int slotIdx = entry.HostileCurrentInteriorCount;
                    if (slotIdx < MaxScratchUnitsPerBuilding)
                    {
                        entry.HostileInteriorAimYaws[slotIdx] = c.TurretYaw;
                        entry.HostileInteriorHasAim[slotIdx] = true;
                    }
                    entry.HostileCurrentInteriorCount++;
                }
            }
            else if (c.State == ContactState.LastKnown)
            {
                entry.HostileLastKnownCount++;
            }
        }

        // 4. Populate figures MultiMesh & Variant A X-Ray MultiMesh
        int figCount = 0;
        int xrayCount = 0;
        int badgeCount = 0;
        int contestedCount = 0;

        for (int i = 0; i < _entryCount; i++)
        {
            BuildingVisualEntry e = _entries[i];
            int ownTotal = e.OwnInteriorCount + e.OwnRoofCount;
            int hostileCurrentTotal = e.HostileCurrentInteriorCount + e.HostileCurrentRoofCount;
            if (ownTotal == 0 && hostileCurrentTotal == 0 && e.HostileLastKnownCount == 0)
                continue;

            bool isContested = ownTotal > 0 && hostileCurrentTotal > 0;
            if (isContested) contestedCount++;

            bool isHovered = hoveredStructure == e.Id;
            bool isSelected = e.OwnSelected;
            Color xrayTint = isContested
                ? XRayCqbColorA.Lerp(XRayCqbColorB, cqbPulse)
                : ((isSelected || isHovered) ? XRaySelectedColor : XRayIdleColor);

            // Clear per-building window mask once so own and hostile figures never stack on the same sill
            int maskLen = Math.Min(e.FiringPorts.Length, _portUsedMask.Length);
            for (int m = 0; m < maskLen; m++) _portUsedMask[m] = false;

            // Own interior / rubble figures + Variant A X-Ray silhouettes
            if (e.OwnInteriorCount > 0)
            {
                AppendInteriorFigures(
                    e,
                    e.OwnInteriorCount,
                    e.OwnInteriorAimYaws,
                    e.OwnInteriorHasAim,
                    OwnFigureColor,
                    emitXRay: true,
                    xrayTint,
                    rubblePhaseOffset: 0f,
                    ref figCount,
                    ref xrayCount);
            }

            // Own rooftop figures (full upright figures on parapet, plus subtle X-Ray if selected or in CQB)
            if (e.OwnRoofCount > 0)
            {
                AppendRoofFigures(e, e.OwnRoofCount, OwnRoofFigureColor, emitXRay: isSelected || isContested, xrayTint, ref figCount, ref xrayCount);
            }

            // Spotted hostile CURRENT interior figures (NEVER X-Ray, NEVER for LastKnown or concealed enemies)
            if (e.HostileCurrentInteriorCount > 0)
            {
                AppendInteriorFigures(
                    e,
                    e.HostileCurrentInteriorCount,
                    e.HostileInteriorAimYaws,
                    e.HostileInteriorHasAim,
                    HostileFigureColor,
                    emitXRay: false,
                    default,
                    rubblePhaseOffset: MathF.PI * 0.5f,
                    ref figCount,
                    ref xrayCount);
            }

            // Spotted hostile CURRENT rooftop figures
            if (e.HostileCurrentRoofCount > 0)
            {
                AppendRoofFigures(e, e.HostileCurrentRoofCount, HostileFigureColor, emitXRay: false, default, ref figCount, ref xrayCount);
            }

            // Project clickable tactical badge if overlay & camera available
            if (badgeCount < _badgePool.Length && camera is not null)
            {
                float currentMaxY = StructureDestructionMath.ComputeCurrentHeight(
                    e.Bounds.MinY,
                    e.Bounds.MaxY,
                    in e.DestructionProfile,
                    e.ObservedFloorsStanding);
                Vector3 anchor3D = new(e.CenterXZ.X, currentMaxY + 2.2f, e.CenterXZ.Y);
                if (!camera.IsPositionBehind(anchor3D))
                {
                    Vector2 screenPos = camera.UnprojectPosition(anchor3D);
                    BadgeSlot slot = _badgePool[badgeCount++];
                    UpdateBadgeSlot(
                        slot,
                        e,
                        ownTotal,
                        e.OwnRoofCount,
                        hostileCurrentTotal,
                        e.HostileCurrentRoofCount,
                        e.HostileLastKnownCount,
                        isSelected,
                        isHovered,
                        cqbPulse,
                        screenPos);
                }
            }
        }

        // Hide unused pooled badges
        for (int b = badgeCount; b < _badgePool.Length; b++)
        {
            if (_badgePool[b].Button.Visible)
            {
                _badgePool[b].Button.Visible = false;
                _badgePool[b].AssignedStructureId = default;
            }
        }

        ActiveFigureCount = figCount;
        ActiveXRayCount = xrayCount;
        VisibleBadgeCount = badgeCount;
        ContestedBuildingCount = contestedCount;

        _figuresMesh.VisibleInstanceCount = figCount;
        _figuresInstance.Visible = figCount > 0;

        _xrayMesh.VisibleInstanceCount = xrayCount;
        _xrayInstance.Visible = xrayCount > 0;

        // 5. Entrance hover indicator when infantry is selected and hovering a garrisonable building
        if (showEntranceHint && hoveredStructure != default && _byId.TryGetValue(hoveredStructure, out BuildingVisualEntry? hoveredEntry))
        {
            _entranceHoverRing.GlobalPosition = new Vector3(
                hoveredEntry.EntranceWorldPos.X,
                hoveredEntry.GroundY + 0.12f,
                hoveredEntry.EntranceWorldPos.Y);
            _entranceHoverRing.Rotation = new Vector3(0f, hoveredEntry.YawRad, 0f);
            _entranceHoverRing.Visible = true;
        }
        else if (_entranceHoverRing.Visible)
        {
            _entranceHoverRing.Visible = false;
        }
    }

    private void AppendInteriorFigures(
        BuildingVisualEntry e,
        int unitCount,
        float[] aimYaws,
        bool[] hasAim,
        Color figureColor,
        bool emitXRay,
        Color xrayColor,
        float rubblePhaseOffset,
        ref int figCount,
        ref int xrayCount)
    {
        if (e.ObservedFloorsStanding == 0)
        {
            // Collapsed rubble cover positions around the rubble crest
            int shown = Math.Min(unitCount, 6);
            float rubbleY = e.Bounds.MinY + e.DestructionProfile.RubbleHeight;
            for (int k = 0; k < shown; k++)
            {
                float angle = (MathF.Tau * k) / Math.Max(1, shown) + e.YawRad + rubblePhaseOffset;
                float rx = e.CenterXZ.X + MathF.Cos(angle) * 2.8f;
                float rz = e.CenterXZ.Y + MathF.Sin(angle) * 2.8f;
                Transform3D xform = new(Basis.FromEuler(new Vector3(0f, -angle, 0f)).Scaled(new Vector3(0.95f, 0.82f, 0.95f)), new Vector3(rx, rubbleY, rz));
                AddInstance(in xform, figureColor, emitXRay, xrayColor, ref figCount, ref xrayCount);
            }
            return;
        }

        int portCount = e.FiringPorts.Length;
        if (portCount == 0) return;

        // Place figures dynamically at top-standing-floor window ports matching each unit's active firing side
        float topFloorBaseY = e.Bounds.MinY + (e.ObservedFloorsStanding - 1) * e.DestructionProfile.FloorHeight;
        int shownPorts = Math.Min(unitCount, portCount);
        int stride = shownPorts <= 4 && portCount >= 12 ? 3 : 1;
        int maskLimit = Math.Min(portCount, _portUsedMask.Length);

        for (int k = 0; k < shownPorts; k++)
        {
            int chosenIdx = -1;
            if (k < MaxScratchUnitsPerBuilding && hasAim[k])
            {
                // Shift soldier to the best unoccupied window facing their active TurretYaw direction
                float aimX = -MathF.Sin(aimYaws[k]);
                float aimZ = -MathF.Cos(aimYaws[k]);
                float bestDot = float.NegativeInfinity;
                for (int p = 0; p < portCount; p++)
                {
                    if (p < maskLimit && _portUsedMask[p]) continue;
                    WorldFiringPort cand = e.FiringPorts[p];
                    float dot = cand.NormalX * aimX + cand.NormalZ * aimZ;
                    if (dot > bestDot + 1e-4f)
                    {
                        bestDot = dot;
                        chosenIdx = p;
                    }
                }
            }

            if (chosenIdx < 0)
            {
                // Idle soldier (or all preferred ports taken): pick first unoccupied port with facade spread
                int startIdx = (k * stride) % portCount;
                for (int step = 0; step < portCount; step++)
                {
                    int candIdx = (startIdx + step) % portCount;
                    if (candIdx >= maskLimit || !_portUsedMask[candIdx])
                    {
                        chosenIdx = candIdx;
                        break;
                    }
                }
                if (chosenIdx < 0) chosenIdx = startIdx;
            }

            if (chosenIdx < maskLimit) _portUsedMask[chosenIdx] = true;

            WorldFiringPort wp = e.FiringPorts[chosenIdx];
            // Position figure so head + rifle align with window sill height (port.LocalY)
            float figureBaseY = topFloorBaseY + MathF.Max(0.05f, wp.LocalY - 1.15f);
            Basis basis = Basis.FromEuler(new Vector3(0f, wp.YawRad, 0f));
            Transform3D xform = new(basis, new Vector3(wp.WorldX, figureBaseY, wp.WorldZ));
            AddInstance(in xform, figureColor, emitXRay, xrayColor, ref figCount, ref xrayCount);
        }
    }

    private void AppendRoofFigures(
        BuildingVisualEntry e,
        int unitCount,
        Color figureColor,
        bool emitXRay,
        Color xrayColor,
        ref int figCount,
        ref int xrayCount)
    {
        int portCount = e.RoofPorts.Length;
        if (portCount == 0) return;

        int shown = Math.Min(unitCount, portCount * 2);
        for (int k = 0; k < shown; k++)
        {
            WorldRoofPort rp = e.RoofPorts[k % portCount];
            float lateralJitter = k >= portCount ? 1.15f : 0f;
            float rx = rp.WorldX + MathF.Cos(rp.YawRad) * lateralJitter;
            float rz = rp.WorldZ - MathF.Sin(rp.YawRad) * lateralJitter;
            Basis basis = Basis.FromEuler(new Vector3(0f, rp.YawRad, 0f));
            Transform3D xform = new(basis, new Vector3(rx, rp.WorldY, rz));
            AddInstance(in xform, figureColor, emitXRay, xrayColor, ref figCount, ref xrayCount);
        }
    }

    private void AddInstance(
        in Transform3D xform,
        Color figureColor,
        bool emitXRay,
        Color xrayColor,
        ref int figCount,
        ref int xrayCount)
    {
        if (figCount < MaxInstances)
        {
            _figuresMesh.SetInstanceTransform(figCount, xform);
            _figuresMesh.SetInstanceColor(figCount, figureColor);
            figCount++;
        }
        if (emitXRay && xrayCount < MaxInstances)
        {
            _xrayMesh.SetInstanceTransform(xrayCount, xform);
            _xrayMesh.SetInstanceColor(xrayCount, xrayColor);
            xrayCount++;
        }
    }

    private static void UpdateBadgeSlot(
        BadgeSlot slot,
        BuildingVisualEntry e,
        int ownTotal,
        int ownRoof,
        int hostileCurrent,
        int hostileRoof,
        int hostileLastKnown,
        bool isSelected,
        bool isHovered,
        float cqbPulse,
        Vector2 screenPos)
    {
        slot.AssignedStructureId = e.Id;
        bool isContested = ownTotal > 0 && hostileCurrent > 0;
        bool stateChanged =
            slot.CachedOwnTotal != ownTotal ||
            slot.CachedOwnRoof != ownRoof ||
            slot.CachedHostileCurrent != hostileCurrent ||
            slot.CachedHostileRoof != hostileRoof ||
            slot.CachedHostileLastKnown != hostileLastKnown ||
            slot.CachedSelected != isSelected ||
            slot.CachedHovered != isHovered;

        if (stateChanged)
        {
            slot.CachedOwnTotal = ownTotal;
            slot.CachedOwnRoof = ownRoof;
            slot.CachedHostileCurrent = hostileCurrent;
            slot.CachedHostileRoof = hostileRoof;
            slot.CachedHostileLastKnown = hostileLastKnown;
            slot.CachedSelected = isSelected;
            slot.CachedHovered = isHovered;

            if (isContested)
            {
                slot.Style.BgColor = BadgeCqbBg;
                slot.Style.BorderColor = BadgeCqbBorderA.Lerp(BadgeCqbBorderB, cqbPulse);
                slot.Button.Text = $"CQB {ownTotal}v{hostileCurrent}";
            }
            else if (ownTotal > 0)
            {
                slot.Style.BgColor = (isSelected || isHovered) ? BadgeOwnSelectedBg : BadgeOwnBg;
                slot.Style.BorderColor = (isSelected || isHovered) ? BadgeOwnSelectedBorder : BadgeOwnBorder;
                slot.Button.Text = ownRoof > 0
                    ? $"{ownTotal}/{e.GarrisonProfile.Capacity} ROOF"
                    : $"{ownTotal}/{e.GarrisonProfile.Capacity}";
            }
            else if (hostileCurrent > 0)
            {
                slot.Style.BgColor = BadgeHostileBg;
                slot.Style.BorderColor = BadgeHostileBorder;
                slot.Button.Text = hostileRoof > 0 ? "! ROOF" : "! INF";
            }
            else
            {
                slot.Style.BgColor = BadgeLastKnownBg;
                slot.Style.BorderColor = BadgeLastKnownBorder;
                slot.Button.Text = "? INF";
            }
        }
        else if (isContested)
        {
            // Smooth zero-allocation border pulse while CQB assault is underway
            slot.Style.BorderColor = BadgeCqbBorderA.Lerp(BadgeCqbBorderB, cqbPulse);
        }

        Vector2 size = slot.Button.Size;
        if (size.X < 10f) size = slot.Button.CustomMinimumSize;
        slot.Button.Position = new Vector2(screenPos.X - size.X * 0.5f, screenPos.Y - size.Y * 0.5f);
        if (!slot.Button.Visible)
        {
            slot.Button.Visible = true;
        }
    }

    private static ArrayMesh CreateSoldierSilhouetteMesh()
    {
        // Compact, high-readability ~1.65 m infantry silhouette facing +Z:
        // boots/legs, torso, helmeted head, and raised rifle barrel pointing forward (+Z).
        SurfaceTool st = new();
        st.Begin(Mesh.PrimitiveType.Triangles);

        // Legs (two sturdy blocks)
        AddBox(st, new Vector3(-0.12f, 0.38f, 0f), new Vector3(0.14f, 0.76f, 0.16f), new Color(0.72f, 0.72f, 0.72f));
        AddBox(st, new Vector3(0.12f, 0.38f, 0f), new Vector3(0.14f, 0.76f, 0.16f), new Color(0.72f, 0.72f, 0.72f));
        // Torso + chest rig
        AddBox(st, new Vector3(0f, 1.02f, 0f), new Vector3(0.44f, 0.56f, 0.24f), new Color(0.95f, 0.95f, 0.95f));
        // Helmeted head
        AddBox(st, new Vector3(0f, 1.46f, 0.02f), new Vector3(0.24f, 0.26f, 0.26f), new Color(1.0f, 1.0f, 1.0f));
        // Forward-aimed rifle silhouette (+Z)
        AddBox(st, new Vector3(0.14f, 1.16f, 0.38f), new Vector3(0.08f, 0.09f, 0.86f), new Color(0.42f, 0.42f, 0.44f));

        st.GenerateNormals();
        return st.Commit();
    }

    private static ArrayMesh CreateEntranceRingMesh()
    {
        SurfaceTool st = new();
        st.Begin(Mesh.PrimitiveType.Triangles);

        const int segs = 24;
        const float innerR = 1.05f;
        const float outerR = 1.35f;
        Color ringCol = Colors.White;
        for (int i = 0; i < segs; i++)
        {
            float a0 = (MathF.Tau * i) / segs;
            float a1 = (MathF.Tau * (i + 1)) / segs;
            Vector3 in0 = new(MathF.Cos(a0) * innerR, 0f, MathF.Sin(a0) * innerR);
            Vector3 out0 = new(MathF.Cos(a0) * outerR, 0f, MathF.Sin(a0) * outerR);
            Vector3 in1 = new(MathF.Cos(a1) * innerR, 0f, MathF.Sin(a1) * innerR);
            Vector3 out1 = new(MathF.Cos(a1) * outerR, 0f, MathF.Sin(a1) * outerR);

            st.SetColor(ringCol); st.AddVertex(in0);
            st.SetColor(ringCol); st.AddVertex(out0);
            st.SetColor(ringCol); st.AddVertex(out1);

            st.SetColor(ringCol); st.AddVertex(in0);
            st.SetColor(ringCol); st.AddVertex(out1);
            st.SetColor(ringCol); st.AddVertex(in1);
        }

        // Small inward arrow pointing from entrance ring toward the doorway (-Z local)
        Vector3 tip = new(0f, 0f, -1.75f);
        Vector3 left = new(-0.45f, 0f, -1.15f);
        Vector3 right = new(0.45f, 0f, -1.15f);
        st.SetColor(ringCol); st.AddVertex(left);
        st.SetColor(ringCol); st.AddVertex(tip);
        st.SetColor(ringCol); st.AddVertex(right);

        st.GenerateNormals();
        return st.Commit();
    }

    private static void AddBox(SurfaceTool st, Vector3 center, Vector3 size, Color color)
    {
        Vector3 h = size * 0.5f;
        Vector3 p000 = center + new Vector3(-h.X, -h.Y, -h.Z);
        Vector3 p100 = center + new Vector3(h.X, -h.Y, -h.Z);
        Vector3 p110 = center + new Vector3(h.X, h.Y, -h.Z);
        Vector3 p010 = center + new Vector3(-h.X, h.Y, -h.Z);
        Vector3 p001 = center + new Vector3(-h.X, -h.Y, h.Z);
        Vector3 p101 = center + new Vector3(h.X, -h.Y, h.Z);
        Vector3 p111 = center + new Vector3(h.X, h.Y, h.Z);
        Vector3 p011 = center + new Vector3(-h.X, h.Y, h.Z);

        // +Z front
        AddQuad(st, p001, p101, p111, p011, color);
        // -Z back
        AddQuad(st, p100, p000, p010, p110, color);
        // +X right
        AddQuad(st, p101, p100, p110, p111, color);
        // -X left
        AddQuad(st, p000, p001, p011, p010, color);
        // +Y top
        AddQuad(st, p011, p111, p110, p010, color);
        // -Y bottom
        AddQuad(st, p000, p100, p101, p001, color);
    }

    private static void AddQuad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
    {
        st.SetColor(color); st.AddVertex(a);
        st.SetColor(color); st.AddVertex(b);
        st.SetColor(color); st.AddVertex(c);
        st.SetColor(color); st.AddVertex(a);
        st.SetColor(color); st.AddVertex(c);
        st.SetColor(color); st.AddVertex(d);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (GodotObject.IsInstanceValid(_root))
        {
            _root.GetParent()?.RemoveChild(_root);
            _root.QueueFree();
        }
        if (_badgeContainer is not null && GodotObject.IsInstanceValid(_badgeContainer))
        {
            _badgeContainer.GetParent()?.RemoveChild(_badgeContainer);
            _badgeContainer.QueueFree();
        }
    }
}
