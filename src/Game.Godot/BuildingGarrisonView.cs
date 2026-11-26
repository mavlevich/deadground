using DeadGround.Engine;
using DeadGround.Engine.Net;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

/// <summary>
/// Client presentation coordinator for building garrisons (Stage 2, PR 2.2 - PR 2.4):
/// - Positions real animated soldier models (UnitView / ContactView) at building window ports,
///   rooftops, and rubble crests with smooth indoor running interpolation (8.5 m/s).
/// - Applies a wall-occluded X-Ray MaterialOverlay to own UnitViews so exposed rooftop/window
///   parts render normally while wall-occluded silhouettes remain readable through concrete.
/// - Projects pre-pooled clickable tactical garrison badges (own count, spotted enemy count,
///   "? / cap" FoW last-known markers, and pulsing "CQB own v enemy" assault indicators).
/// - Strictly Fog-of-War driven from ClientReplica (OwnUnits + Contacts only).
/// - Zero managed heap allocations during steady-state Update frames.
/// </summary>
internal sealed class BuildingGarrisonView : IDisposable
{
    private const int MaxBadges = 64;
    private const int MaxScratchUnitsPerBuilding = 16;
    private const int MaxAdjacentPerBuilding = 8;
    private const int MaxTrackedOwnFigures = 128;
    private const float InteriorRunSpeedMetersPerSec = 8.5f;
    private const float ThreatWatchRangeMeters = 450.0f;

    private struct TrackedOwnFigure
    {
        public EntityId UnitId;
        public Vector3 Position;
        public float YawRad;
        public int LastSeenFrame;
        public bool Active;
    }

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
        public readonly EntityId[] OwnInteriorIds = new EntityId[MaxScratchUnitsPerBuilding];
        public readonly float[] OwnInteriorAimYaws = new float[MaxScratchUnitsPerBuilding];
        public readonly bool[] OwnInteriorHasAim = new bool[MaxScratchUnitsPerBuilding];
        public readonly EntityId[] OwnRoofIds = new EntityId[MaxScratchUnitsPerBuilding];
        public readonly float[] OwnRoofAimYaws = new float[MaxScratchUnitsPerBuilding];
        public readonly bool[] OwnRoofHasAim = new bool[MaxScratchUnitsPerBuilding];
        public readonly int[] HostileInteriorContactIndices = new int[MaxScratchUnitsPerBuilding];
        public readonly float[] HostileInteriorAimYaws = new float[MaxScratchUnitsPerBuilding];
        public readonly bool[] HostileInteriorHasAim = new bool[MaxScratchUnitsPerBuilding];
        public readonly int[] HostileRoofContactIndices = new int[MaxScratchUnitsPerBuilding];
        public readonly float[] HostileRoofAimYaws = new float[MaxScratchUnitsPerBuilding];
        public readonly bool[] HostileRoofHasAim = new bool[MaxScratchUnitsPerBuilding];
        public bool HasThreatWatchDir;
        public float ThreatWatchDirX;
        public float ThreatWatchDirZ;
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

    private static readonly Color BadgeOwnBg = new(0.08f, 0.14f, 0.18f, 0.90f);
    private static readonly Color BadgeOwnSelectedBg = new(0.11f, 0.25f, 0.28f, 0.95f);
    private static readonly Color BadgeOwnBorder = new(0.30f, 0.78f, 0.72f, 0.96f);
    private static readonly Color BadgeOwnSelectedBorder = new(0.46f, 1.00f, 0.86f, 1.0f);
    private static readonly Color BadgeHostileBg = new(0.24f, 0.08f, 0.08f, 0.90f);
    private static readonly Color BadgeHostileBorder = new(0.94f, 0.34f, 0.26f, 0.96f);
    private static readonly Color BadgeCqbBg = new(0.36f, 0.11f, 0.05f, 0.96f);
    private static readonly Color BadgeCqbBorderA = new(1.00f, 0.44f, 0.14f, 1.0f);
    private static readonly Color BadgeCqbBorderB = new(1.00f, 0.86f, 0.24f, 1.0f);
    private static readonly Color BadgeLastKnownBg = new(0.18f, 0.13f, 0.08f, 0.80f);
    private static readonly Color BadgeLastKnownBorder = new(0.80f, 0.62f, 0.30f, 0.85f);

    private readonly Node3D _root;
    private readonly MeshInstance3D _entranceHoverRing;
    private Control? _badgeContainer;
    private BadgeSlot[] _badgePool = [];
    private readonly Action<StructureId>? _onBadgeClicked;

    private readonly Dictionary<StructureId, BuildingVisualEntry> _byId;
    private readonly BuildingVisualEntry[] _entries;
    private readonly bool[] _portUsedMask = new bool[32];
    private readonly TrackedOwnFigure[] _trackedOwnFigures = new TrackedOwnFigure[MaxTrackedOwnFigures];
    private readonly int _entryCount;
    private int _frameCounter;
    private float _cqbPulseTime;
    private bool _disposed;

    public int RegisteredBuildingCount => _entryCount;
    public int ActiveFigureCount { get; private set; }
    public int ActiveXRayCount { get; private set; }
    public int VisibleBadgeCount { get; private set; }
    public int ContestedBuildingCount { get; private set; }
    public int ActiveMultiMeshDrawCalls => 0;

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
                // Real soldier GLB models face -Z, so outward yaw is atan2(-nx, -nz)
                float yaw = MathF.Atan2(-normal.X, -normal.Y);
                // Stand slightly recessed behind the window plane so shoulders/rifle read cleanly at the sill
                // while wall-occluded parts glow softly via X-Ray overlay
                worldFiringPorts[p] = new WorldFiringPort(
                    wp.X - normal.X * 0.30f,
                    fp.LocalY,
                    wp.Z - normal.Y * 0.30f,
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
                    ? MathF.Atan2(-offset.X, -offset.Y)
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
                BorderWidthLeft = 2,
                BorderWidthRight = 2,
                BorderWidthTop = 2,
                BorderWidthBottom = 2,
                CornerRadiusTopLeft = 4,
                CornerRadiusTopRight = 4,
                CornerRadiusBottomLeft = 4,
                CornerRadiusBottomRight = 4,
                ContentMarginLeft = 7,
                ContentMarginRight = 7,
                ContentMarginTop = 3,
                ContentMarginBottom = 3
            };
            Button btn = new()
            {
                Visible = false,
                FocusMode = Control.FocusModeEnum.None,
                MouseFilter = Control.MouseFilterEnum.Stop,
                CustomMinimumSize = new Vector2(56, 22)
            };
            btn.AddThemeStyleboxOverride("normal", style);
            btn.AddThemeStyleboxOverride("hover", style);
            btn.AddThemeStyleboxOverride("pressed", style);
            btn.AddThemeFontSizeOverride("font_size", 12);
            btn.AddThemeColorOverride("font_color", new Color(0.95f, 0.99f, 0.97f));
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

    public bool TryGetRoofFastRopeInfo(
        StructureId structureId,
        out NumericsVector2 centerXZ,
        out float roofHeightAboveGround,
        out byte floorsTotal,
        out int capacity)
    {
        if (_byId.TryGetValue(structureId, out BuildingVisualEntry? entry))
        {
            centerXZ = entry.CenterXZ;
            roofHeightAboveGround = MathF.Max(0f, entry.Bounds.MaxY - entry.Bounds.MinY);
            floorsTotal = entry.DestructionProfile.FloorsTotal;
            capacity = entry.GarrisonProfile.Capacity;
            return true;
        }
        centerXZ = default;
        roofHeightAboveGround = 0f;
        floorsTotal = 0;
        capacity = 0;
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

    internal string GetBadgeTextForTesting(int index) =>
        (uint)index < (uint)_badgePool.Length ? _badgePool[index].Button.Text : string.Empty;

    public void Update(
        ClientReplica replica,
        HashSet<EntityId>? selectedUnits,
        StructureId hoveredStructure,
        bool showEntranceHint,
        Camera3D? camera,
        float deltaSeconds = 0.016f,
        UnitViewSet? unitViews = null,
        ContactViewSet? contactViews = null)
    {
        if (_disposed) return;

        _frameCounter++;
        float dt = Math.Clamp(deltaSeconds, 0f, 0.25f);
        _cqbPulseTime += dt;
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
            e.HasThreatWatchDir = false;
            e.ThreatWatchDirX = 0f;
            e.ThreatWatchDirZ = 0f;
            e.ObservedFloorsStanding = replica.TryGetStructure(e.Id, out StructureObservation obs)
                ? obs.FloorsStanding
                : e.DestructionProfile.FloorsTotal;
        }

        // 2. Tally own garrisoned units from ClientReplica.OwnUnits
        ReadOnlySpan<OwnUnitObservation> ownUnits = replica.OwnUnits;
        for (int i = 0; i < ownUnits.Length; i++)
        {
            ref readonly OwnUnitObservation u = ref ownUnits[i];
            if (u.GarrisonStructureId == default || u.GarrisonSlotKind == GarrisonSlotKind.None)
                continue;
            if (!_byId.TryGetValue(u.GarrisonStructureId, out BuildingVisualEntry? entry))
                continue;

            bool hasAim = u.AttackStatus != AttackExecutionStatus.None;
            if (u.GarrisonSlotKind == GarrisonSlotKind.Roof)
            {
                int slotIdx = entry.OwnRoofCount;
                if (slotIdx < MaxScratchUnitsPerBuilding)
                {
                    entry.OwnRoofIds[slotIdx] = u.Id;
                    entry.OwnRoofAimYaws[slotIdx] = u.TurretYaw;
                    entry.OwnRoofHasAim[slotIdx] = hasAim;
                }
                entry.OwnRoofCount++;
            }
            else
            {
                int slotIdx = entry.OwnInteriorCount;
                if (slotIdx < MaxScratchUnitsPerBuilding)
                {
                    entry.OwnInteriorIds[slotIdx] = u.Id;
                    entry.OwnInteriorAimYaws[slotIdx] = u.TurretYaw;
                    entry.OwnInteriorHasAim[slotIdx] = hasAim;
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
                    int slotIdx = entry.HostileCurrentRoofCount;
                    if (slotIdx < MaxScratchUnitsPerBuilding)
                    {
                        entry.HostileRoofContactIndices[slotIdx] = i;
                        entry.HostileRoofAimYaws[slotIdx] = c.TurretYaw;
                        entry.HostileRoofHasAim[slotIdx] = true;
                    }
                    entry.HostileCurrentRoofCount++;
                }
                else
                {
                    int slotIdx = entry.HostileCurrentInteriorCount;
                    if (slotIdx < MaxScratchUnitsPerBuilding)
                    {
                        entry.HostileInteriorContactIndices[slotIdx] = i;
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

        // 3b. Compute nearest spotted external enemy threat direction for occupied friendly buildings
        // so garrisoned infantry watch windows facing spotted tanks/infantry even before opening fire.
        float watchRangeSq = ThreatWatchRangeMeters * ThreatWatchRangeMeters;
        for (int i = 0; i < _entryCount; i++)
        {
            BuildingVisualEntry e = _entries[i];
            if (e.OwnInteriorCount == 0 && e.OwnRoofCount == 0) continue;

            float bestDistSq = watchRangeSq;
            float dirX = 0f;
            float dirZ = 0f;
            bool foundThreat = false;
            for (int c = 0; c < contacts.Length; c++)
            {
                ref readonly ContactObservation obs = ref contacts[c];
                if (obs.State != ContactState.Current) continue;
                if (obs.GarrisonStructureId == e.Id) continue;

                float dx = obs.Position.X - e.CenterXZ.X;
                float dz = obs.Position.Y - e.CenterXZ.Y;
                float dSq = dx * dx + dz * dz;
                if (dSq > 1e-2f && dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    float invLen = 1f / MathF.Sqrt(dSq);
                    dirX = dx * invLen;
                    dirZ = dz * invLen;
                    foundThreat = true;
                }
            }
            if (foundThreat)
            {
                e.HasThreatWatchDir = true;
                e.ThreatWatchDirX = dirX;
                e.ThreatWatchDirZ = dirZ;
            }
        }

        // 4. Position real animated UnitViews & ContactViews and project tactical badges
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
            GarrisonXRayMode ownXRayMode = isContested
                ? GarrisonXRayMode.Cqb
                : ((isSelected || isHovered) ? GarrisonXRayMode.Selected : GarrisonXRayMode.Idle);

            // Clear per-building window mask once so own and hostile soldiers never stack on the same sill
            int maskLen = Math.Min(e.FiringPorts.Length, _portUsedMask.Length);
            for (int m = 0; m < maskLen; m++) _portUsedMask[m] = false;

            // Own interior / rubble real soldier models + wall-occluded X-Ray silhouette
            if (e.OwnInteriorCount > 0)
            {
                ApplyOwnInteriorPoses(
                    e,
                    selectedUnits,
                    isContested,
                    ownXRayMode,
                    dt,
                    unitViews,
                    ref figCount,
                    ref xrayCount);
            }

            // Own rooftop real soldier models (exposed on open rooftop: never apply X-Ray tint)
            if (e.OwnRoofCount > 0)
            {
                ApplyOwnRoofPoses(
                    e,
                    selectedUnits,
                    dt,
                    unitViews,
                    ref figCount);
            }

            // Spotted hostile CURRENT interior real soldier models (red wall-occluded X-Ray ONLY during active CQB)
            if (e.HostileCurrentInteriorCount > 0)
            {
                ApplyHostileInteriorPoses(
                    e,
                    contacts,
                    isContested,
                    dt,
                    contactViews,
                    ref figCount);
            }

            // Spotted hostile CURRENT rooftop real soldier models (NEVER X-Ray)
            if (e.HostileCurrentRoofCount > 0)
            {
                ApplyHostileRoofPoses(
                    e,
                    contacts,
                    dt,
                    contactViews,
                    ref figCount);
            }

            // Project clickable tactical badge if overlay & camera available
            if (badgeCount < _badgePool.Length && camera is not null)
            {
                float currentMaxY = StructureDestructionMath.ComputeCurrentHeight(
                    e.Bounds.MinY,
                    e.Bounds.MaxY,
                    in e.DestructionProfile,
                    e.ObservedFloorsStanding);
                Vector3 anchor3D = new(e.CenterXZ.X, currentMaxY + 2.4f, e.CenterXZ.Y);
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

        // Prune stale tracked own figures that exited buildings or died
        for (int t = 0; t < MaxTrackedOwnFigures; t++)
        {
            if (_trackedOwnFigures[t].Active && _trackedOwnFigures[t].LastSeenFrame != _frameCounter)
            {
                _trackedOwnFigures[t].Active = false;
                _trackedOwnFigures[t].UnitId = default;
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

    private void ApplyOwnInteriorPoses(
        BuildingVisualEntry e,
        HashSet<EntityId>? selectedUnits,
        bool isContested,
        GarrisonXRayMode xrayMode,
        float dt,
        UnitViewSet? unitViews,
        ref int figCount,
        ref int xrayCount)
    {
        int count = Math.Min(e.OwnInteriorCount, MaxScratchUnitsPerBuilding);
        if (e.ObservedFloorsStanding == 0)
        {
            float rubbleY = e.Bounds.MinY + e.DestructionProfile.RubbleHeight;
            for (int k = 0; k < count; k++)
            {
                EntityId unitId = e.OwnInteriorIds[k];
                float angle = (MathF.Tau * k) / Math.Max(1, count) + e.YawRad;
                float rx = e.CenterXZ.X + MathF.Cos(angle) * 2.6f;
                float rz = e.CenterXZ.Y + MathF.Sin(angle) * 2.6f;
                Vector3 targetPos = new(rx, rubbleY, rz);
                float targetYaw = e.OwnInteriorHasAim[k]
                    ? e.OwnInteriorAimYaws[k]
                    : MathF.Atan2(-MathF.Cos(angle), -MathF.Sin(angle));

                ResolveSmoothedOwnPose(unitId, targetPos, targetYaw, dt, out Vector3 posePos, out float poseYaw, out bool isRunning);
                bool unitSelected = selectedUnits is not null && selectedUnits.Contains(unitId);
                if (unitViews is not null && unitViews.Views.TryGetValue(unitId, out UnitView? view))
                {
                    view.ApplyGarrisonPresentation(posePos, poseYaw, isRunning, e.OwnInteriorHasAim[k] || e.HasThreatWatchDir, isRoof: false, unitSelected, xrayMode);
                }
                figCount++;
                xrayCount++;
            }
            return;
        }

        int portCount = e.FiringPorts.Length;
        if (portCount == 0) return;

        float topFloorBaseY = e.Bounds.MinY + (e.ObservedFloorsStanding - 1) * e.DestructionProfile.FloorHeight;
        float lowerFloorBaseY = e.ObservedFloorsStanding >= 2
            ? e.Bounds.MinY + (e.ObservedFloorsStanding - 2) * e.DestructionProfile.FloorHeight
            : topFloorBaseY;
        int stride = count <= 4 && portCount >= 12 ? 3 : 1;
        int maskLimit = Math.Min(portCount, _portUsedMask.Length);
        // During active CQB, shift assault troopers across upper-floor rooms every ~1.8s so
        // real indoor room-clearing movement and Run animations play continuously.
        int cqbShift = isContested ? (int)(_cqbPulseTime / 1.8f) : 0;

        for (int k = 0; k < count; k++)
        {
            EntityId unitId = e.OwnInteriorIds[k];
            int chosenIdx = SelectWindowPort(
                e,
                k + cqbShift,
                stride,
                maskLimit,
                hasAim: !isContested && e.OwnInteriorHasAim[k],
                aimYaw: e.OwnInteriorAimYaws[k],
                useThreatWatchFallback: !isContested);

            WorldFiringPort wp = e.FiringPorts[chosenIdx];
            float floorY = (isContested && ((k + cqbShift) & 1) == 1) ? lowerFloorBaseY : topFloorBaseY;
            Vector3 targetPos = new(wp.WorldX, floorY + 0.08f, wp.WorldZ);
            float targetYaw = e.OwnInteriorHasAim[k] ? e.OwnInteriorAimYaws[k] : wp.YawRad;

            ResolveSmoothedOwnPose(unitId, targetPos, targetYaw, dt, out Vector3 posePos, out float poseYaw, out bool isRunning);
            bool unitSelected = selectedUnits is not null && selectedUnits.Contains(unitId);
            if (unitViews is not null && unitViews.Views.TryGetValue(unitId, out UnitView? view))
            {
                view.ApplyGarrisonPresentation(
                    posePos,
                    poseYaw,
                    isRunning,
                    isAiming: e.OwnInteriorHasAim[k] || e.HasThreatWatchDir || isContested,
                    isRoof: false,
                    unitSelected,
                    xrayMode);
            }
            figCount++;
            xrayCount++;
        }
    }

    private void ApplyOwnRoofPoses(
        BuildingVisualEntry e,
        HashSet<EntityId>? selectedUnits,
        float dt,
        UnitViewSet? unitViews,
        ref int figCount)
    {
        int portCount = e.RoofPorts.Length;
        if (portCount == 0) return;

        int count = Math.Min(e.OwnRoofCount, MaxScratchUnitsPerBuilding);
        for (int k = 0; k < count; k++)
        {
            EntityId unitId = e.OwnRoofIds[k];
            WorldRoofPort rp = e.RoofPorts[k % portCount];
            float lateralJitter = k >= portCount ? 1.15f : 0f;
            float rx = rp.WorldX + MathF.Cos(rp.YawRad) * lateralJitter;
            float rz = rp.WorldZ - MathF.Sin(rp.YawRad) * lateralJitter;
            Vector3 targetPos = new(rx, rp.WorldY, rz);
            float targetYaw = e.OwnRoofHasAim[k]
                ? e.OwnRoofAimYaws[k]
                : (e.HasThreatWatchDir ? MathF.Atan2(-e.ThreatWatchDirX, -e.ThreatWatchDirZ) : rp.YawRad);

            ResolveSmoothedOwnPose(unitId, targetPos, targetYaw, dt, out Vector3 posePos, out float poseYaw, out bool isRunning);
            bool unitSelected = selectedUnits is not null && selectedUnits.Contains(unitId);
            if (unitViews is not null && unitViews.Views.TryGetValue(unitId, out UnitView? view))
            {
                view.ApplyGarrisonPresentation(
                    posePos,
                    poseYaw,
                    isRunning,
                    isAiming: e.OwnRoofHasAim[k] || e.HasThreatWatchDir,
                    isRoof: true,
                    unitSelected,
                    GarrisonXRayMode.None);
            }
            figCount++;
        }
    }

    private void ApplyHostileInteriorPoses(
        BuildingVisualEntry e,
        ReadOnlySpan<ContactObservation> contacts,
        bool isContested,
        float dt,
        ContactViewSet? contactViews,
        ref int figCount)
    {
        int count = Math.Min(e.HostileCurrentInteriorCount, MaxScratchUnitsPerBuilding);
        if (e.ObservedFloorsStanding == 0)
        {
            float rubbleY = e.Bounds.MinY + e.DestructionProfile.RubbleHeight;
            for (int k = 0; k < count; k++)
            {
                int contactIdx = e.HostileInteriorContactIndices[k];
                ref readonly ContactObservation c = ref contacts[contactIdx];
                float angle = (MathF.Tau * k) / Math.Max(1, count) + e.YawRad + MathF.PI * 0.5f;
                Vector3 pos = new(e.CenterXZ.X + MathF.Cos(angle) * 2.6f, rubbleY, e.CenterXZ.Y + MathF.Sin(angle) * 2.6f);
                float yaw = e.HostileInteriorHasAim[k] ? e.HostileInteriorAimYaws[k] : MathF.Atan2(-MathF.Cos(angle), -MathF.Sin(angle));
                if (contactViews is not null && contactViews.Views.TryGetValue(c.Id, out ContactView? view))
                {
                    view.ApplyGarrisonPresentation(c, pos, yaw, isAiming: true, isSelected: false, dt, isCqbContested: isContested);
                }
                figCount++;
            }
            return;
        }

        int portCount = e.FiringPorts.Length;
        if (portCount == 0) return;

        float topFloorBaseY = e.Bounds.MinY + (e.ObservedFloorsStanding - 1) * e.DestructionProfile.FloorHeight;
        int stride = count <= 4 && portCount >= 12 ? 3 : 1;
        int maskLimit = Math.Min(portCount, _portUsedMask.Length);

        for (int k = 0; k < count; k++)
        {
            int contactIdx = e.HostileInteriorContactIndices[k];
            ref readonly ContactObservation c = ref contacts[contactIdx];
            int chosenIdx = SelectWindowPort(
                e,
                k,
                stride,
                maskLimit,
                hasAim: e.HostileInteriorHasAim[k],
                aimYaw: e.HostileInteriorAimYaws[k],
                useThreatWatchFallback: false);

            WorldFiringPort wp = e.FiringPorts[chosenIdx];
            Vector3 pos = new(wp.WorldX, topFloorBaseY + 0.08f, wp.WorldZ);
            float yaw = wp.YawRad;
            if (contactViews is not null && contactViews.Views.TryGetValue(c.Id, out ContactView? view))
            {
                view.ApplyGarrisonPresentation(c, pos, yaw, isAiming: true, isSelected: false, dt, isCqbContested: isContested);
            }
            figCount++;
        }
    }

    private void ApplyHostileRoofPoses(
        BuildingVisualEntry e,
        ReadOnlySpan<ContactObservation> contacts,
        float dt,
        ContactViewSet? contactViews,
        ref int figCount)
    {
        int portCount = e.RoofPorts.Length;
        if (portCount == 0) return;

        int count = Math.Min(e.HostileCurrentRoofCount, MaxScratchUnitsPerBuilding);
        for (int k = 0; k < count; k++)
        {
            int contactIdx = e.HostileRoofContactIndices[k];
            ref readonly ContactObservation c = ref contacts[contactIdx];
            WorldRoofPort rp = e.RoofPorts[k % portCount];
            float lateralJitter = k >= portCount ? 1.15f : 0f;
            float rx = rp.WorldX + MathF.Cos(rp.YawRad) * lateralJitter;
            float rz = rp.WorldZ - MathF.Sin(rp.YawRad) * lateralJitter;
            Vector3 pos = new(rx, rp.WorldY, rz);
            float yaw = e.HostileRoofHasAim[k] ? e.HostileRoofAimYaws[k] : rp.YawRad;
            if (contactViews is not null && contactViews.Views.TryGetValue(c.Id, out ContactView? view))
            {
                view.ApplyGarrisonPresentation(c, pos, yaw, isAiming: true, isSelected: false, dt);
            }
            figCount++;
        }
    }

    private int SelectWindowPort(
        BuildingVisualEntry e,
        int unitSlotIndex,
        int stride,
        int maskLimit,
        bool hasAim,
        float aimYaw,
        bool useThreatWatchFallback)
    {
        int portCount = e.FiringPorts.Length;
        int chosenIdx = -1;
        bool hasDesiredDir = false;
        float aimX = 0f;
        float aimZ = 0f;

        if (hasAim)
        {
            aimX = -MathF.Sin(aimYaw);
            aimZ = -MathF.Cos(aimYaw);
            hasDesiredDir = true;
        }
        else if (useThreatWatchFallback && e.HasThreatWatchDir)
        {
            aimX = e.ThreatWatchDirX;
            aimZ = e.ThreatWatchDirZ;
            hasDesiredDir = true;
        }

        if (hasDesiredDir)
        {
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
            int startIdx = (unitSlotIndex * stride) % portCount;
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
        return chosenIdx;
    }

    private void ResolveSmoothedOwnPose(
        EntityId unitId,
        Vector3 targetPos,
        float targetYaw,
        float dt,
        out Vector3 outPos,
        out float outYaw,
        out bool isRunningInside)
    {
        int foundIdx = -1;
        int freeIdx = -1;
        for (int i = 0; i < MaxTrackedOwnFigures; i++)
        {
            if (_trackedOwnFigures[i].Active && _trackedOwnFigures[i].UnitId == unitId)
            {
                foundIdx = i;
                break;
            }
            if (freeIdx < 0 && !_trackedOwnFigures[i].Active)
            {
                freeIdx = i;
            }
        }

        if (foundIdx < 0)
        {
            if (freeIdx >= 0)
            {
                _trackedOwnFigures[freeIdx] = new TrackedOwnFigure
                {
                    UnitId = unitId,
                    Position = targetPos,
                    YawRad = targetYaw,
                    LastSeenFrame = _frameCounter,
                    Active = true
                };
            }
            outPos = targetPos;
            outYaw = targetYaw;
            isRunningInside = false;
            return;
        }

        ref TrackedOwnFigure tr = ref _trackedOwnFigures[foundIdx];
        tr.LastSeenFrame = _frameCounter;
        Vector3 delta = targetPos - tr.Position;
        float dist = delta.Length();

        if (dist > 32f || dt <= 0f)
        {
            tr.Position = targetPos;
            tr.YawRad = targetYaw;
            outPos = targetPos;
            outYaw = targetYaw;
            isRunningInside = false;
            return;
        }

        float maxStep = InteriorRunSpeedMetersPerSec * dt;
        if (dist <= maxStep || dist <= 0.08f)
        {
            tr.Position = targetPos;
            tr.YawRad = targetYaw;
            outPos = targetPos;
            outYaw = targetYaw;
            isRunningInside = false;
            return;
        }

        Vector3 dir = delta / dist;
        tr.Position += dir * maxStep;
        float runYaw = (delta.X * delta.X + delta.Z * delta.Z) > 0.001f
            ? MathF.Atan2(-delta.X, -delta.Z)
            : targetYaw;
        tr.YawRad = runYaw;

        outPos = tr.Position;
        outYaw = runYaw;
        isRunningInside = dist > 0.35f;
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

            int cap = e.GarrisonProfile.Capacity;
            if (isContested)
            {
                slot.Style.BgColor = BadgeCqbBg;
                slot.Style.BorderColor = BadgeCqbBorderA.Lerp(BadgeCqbBorderB, cqbPulse);
                slot.Button.Text = $"CQB {ownTotal} v {hostileCurrent}";
            }
            else if (ownTotal > 0)
            {
                slot.Style.BgColor = (isSelected || isHovered) ? BadgeOwnSelectedBg : BadgeOwnBg;
                slot.Style.BorderColor = (isSelected || isHovered) ? BadgeOwnSelectedBorder : BadgeOwnBorder;
                slot.Button.Text = ownRoof > 0
                    ? $"{ownTotal} / {cap} ROOF"
                    : $"{ownTotal} / {cap}";
            }
            else if (hostileCurrent > 0)
            {
                slot.Style.BgColor = BadgeHostileBg;
                slot.Style.BorderColor = BadgeHostileBorder;
                slot.Button.Text = hostileRoof > 0
                    ? $"! {hostileCurrent} / {cap} ROOF"
                    : $"! {hostileCurrent} / {cap}";
            }
            else
            {
                slot.Style.BgColor = BadgeLastKnownBg;
                slot.Style.BorderColor = BadgeLastKnownBorder;
                slot.Button.Text = $"? / {cap}";
            }
        }
        else if (isContested)
        {
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

        Vector3 tip = new(0f, 0f, -1.75f);
        Vector3 left = new(-0.45f, 0f, -1.15f);
        Vector3 right = new(0.45f, 0f, -1.15f);
        st.SetColor(ringCol); st.AddVertex(left);
        st.SetColor(ringCol); st.AddVertex(tip);
        st.SetColor(ringCol); st.AddVertex(right);

        st.GenerateNormals();
        return st.Commit();
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
