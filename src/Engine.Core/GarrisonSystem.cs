using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Server-authoritative building garrison simulation system (Stage 2, PR 2.1 + PR 2.3).
/// Manages infantry entry/exit through building entrances, sheltered interior transfers between
/// adjacent building sections, contested close-quarters battle (CQB), elevated window/roof LOS
/// and firing ports, cover damage reduction, concealment rules, and deterministic structural
/// blast/collapse casualties. Strictly allocation-free during simulation ticks.
/// </summary>
public sealed class GarrisonSystem : ISimulationSystem
{
    public const float EntranceArrivalRadius = 3.5f;
    public const float AdjacencyMaxGapMeters = 2.5f;
    public const float AdjacencyMinOverlapMeters = 2.0f;
    public const int AdjacentTransferTicks = 4;
    public const int FastRopeRoofHoldTicks = 50;
    public const float CqbSameBandDamageMultiplier = 0.45f;
    public const float CqbCrossBandDamageMultiplier = 0.35f;
    private const float FootClearanceRadius = 0.45f;
    private const int ExitFanSlotCount = 20;
    private const int MaxPendingEnterUnits = 1024;
    private const int MaxAdjacentPerBuilding = 8;

    private readonly record struct PrecomputedFiringPort(
        float WorldX,
        float LocalY,
        float WorldZ,
        float OutNormalX,
        float OutNormalZ);

    private readonly record struct PrecomputedRoofPort(
        float WorldX,
        float WorldY,
        float WorldZ,
        float OffsetX,
        float OffsetZ);

    private sealed class BuildingEntry
    {
        public StructureId Id;
        public int GeometryIndex;
        public OpaqueStructure Bounds = null!;
        public float YawRad;
        public BuildingGarrisonProfile Profile = null!;
        public Vector2 CenterXZ;
        public Vector2 RawEntranceWorldPos;
        public Vector2 EntranceWorldPos;
        public Vector2 OutwardDir;
        public Vector2 TangentDir;
        public PrecomputedFiringPort[] WorldFiringPorts = [];
        public PrecomputedRoofPort[] WorldRoofPorts = [];
        public readonly EntityId[] Occupants = new EntityId[BuildingGarrisonProfile.MaxOccupantsPerStructure];
        public int OccupantCount;
        public int ExitCursor;
        public readonly StructureId[] AdjacentStructures = new StructureId[MaxAdjacentPerBuilding];
        public int AdjacentCount;
    }

    private readonly StaticGeometry _geometry;
    private readonly Func<ushort, bool> _isInfantryDefinition;
    private readonly Dictionary<StructureId, BuildingEntry> _buildings = new(WorldState.MaxDestructibleStructures);
    private readonly BuildingEntry[] _buildingList = new BuildingEntry[WorldState.MaxDestructibleStructures];
    private readonly EntityId[] _scratchOccupants = new EntityId[BuildingGarrisonProfile.MaxOccupantsPerStructure];
    private readonly EntityId[] _pendingEnterUnits = new EntityId[MaxPendingEnterUnits];
    private readonly EntityId[] _roofAutoDescendUnits = new EntityId[MaxPendingEnterUnits];
    private readonly int[] _roofAutoDescendTicks = new int[MaxPendingEnterUnits];
    private NavigationSystem? _navigation;
    private int _buildingCount;
    private int _pendingEnterCount;
    private int _roofAutoDescendCount;

    public int RegisteredBuildingCount => _buildingCount;
    public int TotalEntries { get; private set; }
    public int TotalExits { get; private set; }
    public int TotalAdjacentTransfers { get; private set; }
    public int TotalCollapseCasualties { get; private set; }

    public GarrisonSystem(StaticGeometry geometry, Func<ushort, bool> isInfantryDefinition)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(isInfantryDefinition);
        _geometry = geometry;
        _isInfantryDefinition = isInfantryDefinition;
    }

    public void SetNavigationSystem(NavigationSystem? navigation)
    {
        _navigation = navigation;
        if (navigation is not null)
        {
            for (int i = 0; i < _buildingCount; i++)
            {
                _buildingList[i].EntranceWorldPos = ResolveWalkableEntrance(_buildingList[i].RawEntranceWorldPos);
            }
        }
    }

    public bool RegisterBuilding(StructureId structureId, float yawRad, BuildingGarrisonProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.IsValid || _buildingCount >= WorldState.MaxDestructibleStructures)
            return false;
        if (_buildings.ContainsKey(structureId))
            return false;
        if (!_geometry.TryGetStructureIndex(structureId, out int geomIdx))
            return false;

        OpaqueStructure bounds = _geometry.Structures[geomIdx];
        Vector2 centerXZ = new((bounds.MinX + bounds.MaxX) * 0.5f, (bounds.MinZ + bounds.MaxZ) * 0.5f);
        Vector2 rawEntrance = ClampToMap(GarrisonMath.ComputeWorldEntrance(bounds, yawRad, profile));
        Vector2 entrance = ResolveWalkableEntrance(rawEntrance);
        Vector2 outward = GarrisonMath.RotateLocalDirection(yawRad, 0f, 1f);
        Vector2 tangent = new(-outward.Y, outward.X);

        // Precompute rotated world firing ports and roof ports once at registration time
        PrecomputedFiringPort[] worldFiringPorts = new PrecomputedFiringPort[profile.FiringPorts.Length];
        for (int i = 0; i < profile.FiringPorts.Length; i++)
        {
            BuildingFiringPort fp = profile.FiringPorts[i];
            Vector3 worldPos = GarrisonMath.RotateLocalPoint(new Vector3(centerXZ.X, 0f, centerXZ.Y), yawRad, new Vector3(fp.LocalX, 0f, fp.LocalZ));
            Vector2 outNormal = GarrisonMath.RotateLocalDirection(yawRad, fp.NormalX, fp.NormalZ);
            worldFiringPorts[i] = new PrecomputedFiringPort(
                worldPos.X + outNormal.X * GarrisonMath.ExteriorClearanceMeters,
                fp.LocalY,
                worldPos.Z + outNormal.Y * GarrisonMath.ExteriorClearanceMeters,
                outNormal.X,
                outNormal.Y);
        }

        PrecomputedRoofPort[] worldRoofPorts = new PrecomputedRoofPort[profile.RoofPorts.Length];
        for (int i = 0; i < profile.RoofPorts.Length; i++)
        {
            BuildingRoofPort rp = profile.RoofPorts[i];
            Vector2 worldOffset = GarrisonMath.RotateLocalDirection(yawRad, rp.LocalX, rp.LocalZ);
            worldRoofPorts[i] = new PrecomputedRoofPort(
                centerXZ.X + worldOffset.X,
                bounds.MaxY + rp.LocalY,
                centerXZ.Y + worldOffset.Y,
                worldOffset.X,
                worldOffset.Y);
        }

        BuildingEntry entry = new()
        {
            Id = structureId,
            GeometryIndex = geomIdx,
            Bounds = bounds,
            YawRad = yawRad,
            Profile = profile,
            CenterXZ = centerXZ,
            RawEntranceWorldPos = rawEntrance,
            EntranceWorldPos = entrance,
            OutwardDir = outward,
            TangentDir = tangent,
            WorldFiringPorts = worldFiringPorts,
            WorldRoofPorts = worldRoofPorts
        };
        _buildings[structureId] = entry;
        for (int i = 0; i < _buildingCount; i++)
        {
            BuildingEntry other = _buildingList[i];
            if (AreBoundsAdjacent(entry.Bounds, other.Bounds))
            {
                LinkAdjacent(entry, other.Id);
                LinkAdjacent(other, entry.Id);
            }
        }
        _buildingList[_buildingCount++] = entry;
        return true;
    }

    public bool IsRegistered(StructureId structureId) => _buildings.ContainsKey(structureId);

    /// <summary>
    /// Finds a registered garrisonable building section whose ground footprint contains <paramref name="position"/>.
    /// Zero allocations.
    /// </summary>
    public bool TryFindBuildingContaining(
        Vector2 position,
        out StructureId structureId,
        out Vector2 centerXZ,
        out float roofHeightAboveGround)
    {
        for (int i = 0; i < _buildingCount; i++)
        {
            BuildingEntry e = _buildingList[i];
            if (position.X >= e.Bounds.MinX && position.X <= e.Bounds.MaxX &&
                position.Y >= e.Bounds.MinZ && position.Y <= e.Bounds.MaxZ)
            {
                structureId = e.Id;
                centerXZ = e.CenterXZ;
                roofHeightAboveGround = MathF.Max(0f, e.Bounds.MaxY - e.Bounds.MinY);
                return true;
            }
        }
        structureId = default;
        centerXZ = default;
        roofHeightAboveGround = 0f;
        return false;
    }

    public bool CanCommitRoofFastRope(WorldState world, StructureId structureId, EntityId unitId)
    {
        if (!_buildings.TryGetValue(structureId, out BuildingEntry? entry))
            return false;
        if (!world.TryGetUnit(unitId, out UnitState unit) || !_isInfantryDefinition(unit.Definition))
            return false;
        if (!CanOccupyRoof(world, structureId))
            return false;
        int unitTeam = world.GetPlayerTeam(unit.Owner);
        if (CountTeamOccupants(entry, world, unitTeam) >= entry.Profile.Capacity)
            return false;
        return entry.OccupantCount < BuildingGarrisonProfile.MaxOccupantsPerStructure;
    }

    /// <summary>
    /// Commits a freshly fast-roped infantryman directly into a building section's rooftop slot
    /// (bypassing ground entrance walk-in and initiating CQB if enemy troops already hold the section).
    /// Schedules automatic descent into interior cover after <see cref="FastRopeRoofHoldTicks"/> ticks.
    /// </summary>
    public bool TryCommitFastRopeRoofEntry(WorldState world, EntityId unitId, StructureId structureId)
    {
        if (!_buildings.TryGetValue(structureId, out BuildingEntry? entry))
            return false;
        if (!TryCommitEntry(entry, unitId, GarrisonSlotKind.Roof, world, _navigation))
            return false;
        TrackRoofAutoDescend(unitId, FastRopeRoofHoldTicks);
        return true;
    }

    /// <summary>
    /// Places an infantry unit directly into a registered building section during scenario setup
    /// (bypassing entrance walk-in).
    /// </summary>
    public bool TryPlaceInitialGarrison(
        WorldState world,
        EntityId unitId,
        StructureId structureId,
        GarrisonSlotKind slotKind = GarrisonSlotKind.Interior)
    {
        if (!_buildings.TryGetValue(structureId, out BuildingEntry? entry))
            return false;
        return TryCommitEntry(entry, unitId, slotKind, world, _navigation);
    }

    public bool AreSectionsAdjacent(StructureId a, StructureId b)
    {
        if (a == b || !_buildings.TryGetValue(a, out BuildingEntry? entry))
            return false;
        for (int i = 0; i < entry.AdjacentCount; i++)
        {
            if (entry.AdjacentStructures[i] == b)
                return true;
        }
        return false;
    }

    public bool AreInSameGarrison(WorldState world, EntityId unitA, EntityId unitB, out StructureId structureId)
    {
        if (world.TryGetGarrison(unitA, out StructureId sA, out GarrisonSlotKind slotA) &&
            slotA != GarrisonSlotKind.None &&
            world.TryGetGarrison(unitB, out StructureId sB, out GarrisonSlotKind slotB) &&
            slotB != GarrisonSlotKind.None &&
            sA == sB)
        {
            structureId = sA;
            return true;
        }
        structureId = default;
        return false;
    }

    public bool IsContested(WorldState world, StructureId structureId)
    {
        if (!_buildings.TryGetValue(structureId, out BuildingEntry? entry) || entry.OccupantCount < 2)
            return false;
        int firstTeam = -1;
        for (int i = 0; i < entry.OccupantCount; i++)
        {
            if (!world.TryGetUnit(entry.Occupants[i], out UnitState occ))
                continue;
            int team = world.GetPlayerTeam(occ.Owner);
            if (firstTeam < 0)
                firstTeam = team;
            else if (team != firstTeam)
                return true;
        }
        return false;
    }

    public float GetCqbDamageMultiplier(WorldState world, EntityId attackerId, EntityId targetId)
    {
        if (world.TryGetGarrison(attackerId, out StructureId sA, out GarrisonSlotKind slotA) &&
            world.TryGetGarrison(targetId, out StructureId sB, out GarrisonSlotKind slotB) &&
            sA == sB)
        {
            return slotA == slotB ? CqbSameBandDamageMultiplier : CqbCrossBandDamageMultiplier;
        }
        return 1.0f;
    }

    /// <summary>
    /// Computes deterministic 3D muzzle and impact points for room-to-room CQB shots inside
    /// a contested section, anchored at window ports on the active floor so external observers
    /// see muzzle flashes flickering across the facade during indoor assaults.
    /// </summary>
    public bool TryGetCqbShotPoints(
        WorldState world,
        StructureId structureId,
        EntityId attackerId,
        EntityId targetId,
        int tick,
        out Vector3 muzzlePos,
        out Vector3 hitPoint)
    {
        if (!_buildings.TryGetValue(structureId, out BuildingEntry? entry))
        {
            muzzlePos = default;
            hitPoint = default;
            return false;
        }

        StructureDestructionProfile destProfile;
        byte floorsStanding;
        if (world.TryGetDestructibleStructure(structureId, out DestructibleStructureState destState))
        {
            destProfile = destState.Profile;
            floorsStanding = destState.FloorsStanding;
        }
        else
        {
            destProfile = new StructureDestructionProfile(1, MathF.Max(2.5f, entry.Bounds.MaxY - entry.Bounds.MinY), 1.0f, 100);
            floorsStanding = 1;
        }

        GarrisonSlotKind attackerSlot = world.GetGarrisonSlotKind(attackerId);
        if (attackerSlot == GarrisonSlotKind.Roof && floorsStanding >= destProfile.FloorsTotal && entry.WorldRoofPorts.Length > 0)
        {
            PrecomputedRoofPort[] rps = entry.WorldRoofPorts;
            int idxA = (int)(((uint)attackerId.Value + (uint)(tick / 6)) % (uint)rps.Length);
            int idxB = (int)(((uint)targetId.Value + 1u + (uint)(tick / 6)) % (uint)rps.Length);
            if (idxB == idxA && rps.Length > 1) idxB = (idxA + 1) % rps.Length;
            muzzlePos = new Vector3(rps[idxA].WorldX, rps[idxA].WorldY, rps[idxA].WorldZ);
            hitPoint = new Vector3(rps[idxB].WorldX, rps[idxB].WorldY, rps[idxB].WorldZ);
            return true;
        }

        if (floorsStanding == 0)
        {
            float rubbleY = entry.Bounds.MinY + destProfile.RubbleHeight + 0.55f;
            float angleA = (((uint)attackerId.Value * 17u + (uint)(tick / 6)) % 12u) * (MathF.Tau / 12f);
            float angleB = angleA + MathF.PI * 0.75f;
            muzzlePos = new Vector3(entry.CenterXZ.X + MathF.Cos(angleA) * 2.4f, rubbleY, entry.CenterXZ.Y + MathF.Sin(angleA) * 2.4f);
            hitPoint = new Vector3(entry.CenterXZ.X + MathF.Cos(angleB) * 2.4f, rubbleY, entry.CenterXZ.Y + MathF.Sin(angleB) * 2.4f);
            return true;
        }

        float floorBaseY = entry.Bounds.MinY + (floorsStanding - 1) * destProfile.FloorHeight;
        PrecomputedFiringPort[] ports = entry.WorldFiringPorts;
        if (ports.Length >= 2)
        {
            int idxA = (int)(((uint)attackerId.Value * 5u + (uint)(tick / 6)) % (uint)ports.Length);
            int idxB = (int)(((uint)targetId.Value * 7u + 3u + (uint)(tick / 6)) % (uint)ports.Length);
            if (idxB == idxA) idxB = (idxA + 1) % ports.Length;
            PrecomputedFiringPort pA = ports[idxA];
            PrecomputedFiringPort pB = ports[idxB];
            muzzlePos = new Vector3(pA.WorldX, floorBaseY + pA.LocalY, pA.WorldZ);
            hitPoint = new Vector3(pB.WorldX, floorBaseY + pB.LocalY, pB.WorldZ);
            return true;
        }

        muzzlePos = new Vector3(entry.CenterXZ.X - 1.5f, floorBaseY + 1.4f, entry.CenterXZ.Y);
        hitPoint = new Vector3(entry.CenterXZ.X + 1.5f, floorBaseY + 1.4f, entry.CenterXZ.Y);
        return true;
    }

    public int GetOccupantCount(StructureId structureId) =>
        _buildings.TryGetValue(structureId, out BuildingEntry? entry) ? entry.OccupantCount : 0;

    public ReadOnlySpan<EntityId> GetOccupants(StructureId structureId) =>
        _buildings.TryGetValue(structureId, out BuildingEntry? entry)
            ? entry.Occupants.AsSpan(0, entry.OccupantCount)
            : [];

    public bool TryGetEntrancePosition(StructureId structureId, out Vector2 entrancePos)
    {
        if (_buildings.TryGetValue(structureId, out BuildingEntry? entry))
        {
            entrancePos = entry.EntranceWorldPos;
            return true;
        }
        entrancePos = default;
        return false;
    }

    /// <summary>
    /// Validates own-unit and static-building preconditions for enqueueing an EnterBuildingCommand.
    /// Deliberately avoids inspecting hidden hostile occupancy or unobserved roof collapse so
    /// command admission cannot act as a Fog-of-War oracle.
    /// </summary>
    public bool CanEnqueueEnter(int player, EnterBuildingCommand cmd, WorldState world)
    {
        if (cmd.SlotKind is not (GarrisonSlotKind.Interior or GarrisonSlotKind.Roof))
            return false;
        if (!world.TryGetUnit(cmd.Unit, out UnitState unit) || unit.Owner != player)
            return false;
        if (!_isInfantryDefinition(unit.Definition))
            return false;
        if (world.IsContained(cmd.Unit) || world.IsSlung(cmd.Unit) || world.IsCrashing(cmd.Unit))
            return false;
        if (!_buildings.TryGetValue(cmd.TargetStructure, out BuildingEntry? entry))
            return false;

        // Switching levels inside the already-occupied building is allowed without extra capacity
        bool isGarrisoned = world.TryGetGarrison(cmd.Unit, out StructureId curStruct, out GarrisonSlotKind curSlot);
        bool alreadyInsideSame = isGarrisoned && curStruct == cmd.TargetStructure;
        if (alreadyInsideSame && curSlot == cmd.SlotKind)
            return false;

        // Already garrisoned in a different section: allow sheltered interior transfer only to adjacent sections
        if (isGarrisoned && !alreadyInsideSame && !AreSectionsAdjacent(curStruct, cmd.TargetStructure))
            return false;

        // Own-side capacity check (never inspects hostile occupants)
        int unitTeam = world.GetPlayerTeam(unit.Owner);
        if (!alreadyInsideSame && CountTeamOccupants(entry, world, unitTeam) >= entry.Profile.Capacity)
            return false;

        return true;
    }

    public bool CanEnqueueExit(int player, ExitBuildingCommand cmd, WorldState world)
    {
        if (!world.TryGetUnit(cmd.Unit, out UnitState unit) || unit.Owner != player)
            return false;
        return world.IsGarrisoned(cmd.Unit);
    }

    public bool TryExecuteEnterOrder(
        int player,
        EnterBuildingCommand cmd,
        WorldState world,
        NavigationSystem? navigation)
    {
        if (!CanEnqueueEnter(player, cmd, world))
            return false;
        if (!_buildings.TryGetValue(cmd.TargetStructure, out BuildingEntry? entry))
            return false;
        if (!world.TryGetUnit(cmd.Unit, out UnitState unit))
            return false;

        if (world.TryGetGarrison(cmd.Unit, out StructureId curStruct, out _))
        {
            // Already inside this section: switch Interior <-> Roof directly if roof is intact
            if (curStruct == cmd.TargetStructure)
            {
                if (cmd.SlotKind == GarrisonSlotKind.Roof && !CanOccupyRoof(world, entry.Id))
                    return false;
                world.ClearPendingEnterOrder(cmd.Unit);
                RemovePendingEnterUnit(cmd.Unit);
                world.SetGarrisonMembership(cmd.Unit, cmd.TargetStructure, cmd.SlotKind);
                return true;
            }

            // Sheltered interior transfer to an adjacent section (pod'ezd-to-pod'ezd without exiting)
            if (AreSectionsAdjacent(curStruct, cmd.TargetStructure))
            {
                world.SetPendingEnterOrder(cmd.Unit, cmd.TargetStructure, cmd.SlotKind, entry.CenterXZ, AdjacentTransferTicks);
                TrackPendingEnterUnit(cmd.Unit);
                return true;
            }

            return false;
        }

        // If already at the entrance, enter immediately without detouring
        float distSq = Vector2.DistanceSquared(unit.Position, entry.EntranceWorldPos);
        if (distSq <= EntranceArrivalRadius * EntranceArrivalRadius)
        {
            return TryCommitEntry(entry, cmd.Unit, cmd.SlotKind, world, navigation);
        }

        // Walk to entrance and enter on arrival
        NavigationSystem? nav = navigation ?? _navigation;
        if (nav is not null)
        {
            if (!nav.EnqueueCommand(player, new MoveCommand(cmd.Sequence, cmd.ClientTick, cmd.Unit, entry.EntranceWorldPos), world))
            {
                world.ClearPendingEnterOrder(cmd.Unit);
                RemovePendingEnterUnit(cmd.Unit);
                return false;
            }
        }
        else
        {
            world.UpdateUnitNavigation(cmd.Unit, unit.Position, entry.EntranceWorldPos, active: true, supportSurface: unit.SupportSurface);
        }

        world.SetPendingEnterOrder(cmd.Unit, cmd.TargetStructure, cmd.SlotKind, entry.EntranceWorldPos);
        TrackPendingEnterUnit(cmd.Unit);
        return true;
    }

    public bool TryExecuteExitOrder(int player, ExitBuildingCommand cmd, WorldState world, NavigationSystem? navigation)
    {
        if (!CanEnqueueExit(player, cmd, world))
            return false;
        return TryExitUnit(cmd.Unit, world, navigation);
    }

    public bool TryExitUnit(EntityId unitId, WorldState world, NavigationSystem? navigation = null)
    {
        if (!world.TryGetGarrison(unitId, out StructureId structId, out _))
            return false;
        if (!_buildings.TryGetValue(structId, out BuildingEntry? entry))
        {
            world.ClearGarrisonMembership(unitId);
            return false;
        }

        RemoveOccupant(entry, unitId);
        world.ClearGarrisonMembership(unitId);
        world.ClearPendingEnterOrder(unitId);
        RemovePendingEnterUnit(unitId);

        // Advance monotonic per-building ExitCursor past any blocked slots so simultaneous exits never stack
        Vector2 exitPos = ComputeSafeExitPosition(entry);
        world.SetPosition(unitId, exitPos);
        world.UpdateUnitNavigation(unitId, exitPos, exitPos, active: false, supportSurface: 0u);
        (navigation ?? _navigation)?.ClearUnit(unitId);
        TotalExits++;
        return true;
    }

    public void Update(WorldState world, int tick)
    {
        // 1. Prune dead/removed occupants and demote rooftop troops if roof has collapsed
        for (int b = 0; b < _buildingCount; b++)
        {
            BuildingEntry entry = _buildingList[b];
            bool roofIntact = CanOccupyRoof(world, entry.Id);
            int i = 0;
            while (i < entry.OccupantCount)
            {
                EntityId occId = entry.Occupants[i];
                if (!world.TryGetUnit(occId, out _) ||
                    !world.TryGetGarrison(occId, out StructureId memberStruct, out GarrisonSlotKind slot) ||
                    memberStruct != entry.Id)
                {
                    RemoveOccupantAt(entry, i);
                    continue;
                }

                if (slot == GarrisonSlotKind.Roof && !roofIntact)
                {
                    world.SetGarrisonMembership(occId, entry.Id, GarrisonSlotKind.Interior);
                }
                i++;
            }
        }

        // 1b. Auto-descend fast-roped rooftop assault infantry into sheltered interior floors
        int r = 0;
        while (r < _roofAutoDescendCount)
        {
            EntityId unitId = _roofAutoDescendUnits[r];
            if (!world.TryGetUnit(unitId, out _) ||
                !world.TryGetGarrison(unitId, out StructureId structId, out GarrisonSlotKind slot) ||
                slot != GarrisonSlotKind.Roof)
            {
                RemoveRoofAutoDescendAt(r);
                continue;
            }

            if (_roofAutoDescendTicks[r] > 1)
            {
                _roofAutoDescendTicks[r]--;
                r++;
                continue;
            }

            world.SetGarrisonMembership(unitId, structId, GarrisonSlotKind.Interior);
            RemoveRoofAutoDescendAt(r);
        }

        // 2. Complete or cancel tracked pending enter orders and adjacent section transfers in O(pending) time
        float arriveSq = EntranceArrivalRadius * EntranceArrivalRadius;
        int p = 0;
        while (p < _pendingEnterCount)
        {
            EntityId unitId = _pendingEnterUnits[p];
            if (!world.TryGetUnit(unitId, out UnitState unit) ||
                !world.TryGetPendingEnterOrder(unitId, out StructureId targetStruct, out GarrisonSlotKind slotKind, out Vector2 entrancePos, out int transferTicks))
            {
                RemovePendingEnterUnitAt(p);
                continue;
            }

            if (world.IsContained(unitId) || !_buildings.TryGetValue(targetStruct, out BuildingEntry? entry))
            {
                world.ClearPendingEnterOrder(unitId);
                RemovePendingEnterUnitAt(p);
                continue;
            }

            // Case A: Sheltered interior transfer from an adjacent occupied section
            if (world.TryGetGarrison(unitId, out StructureId curStruct, out _))
            {
                if (curStruct == targetStruct || !AreSectionsAdjacent(curStruct, targetStruct) || !_buildings.TryGetValue(curStruct, out BuildingEntry? curEntry))
                {
                    world.ClearPendingEnterOrder(unitId);
                    RemovePendingEnterUnitAt(p);
                    continue;
                }

                if (transferTicks > 1)
                {
                    world.SetPendingEnterOrder(unitId, targetStruct, slotKind, entrancePos, transferTicks - 1);
                    p++;
                    continue;
                }

                TryCommitAdjacentTransfer(curEntry, entry, unitId, slotKind, world);
                world.ClearPendingEnterOrder(unitId);
                RemovePendingEnterUnitAt(p);
                continue;
            }

            // Case B: Exterior unit walking to building entrance
            if (Vector2.DistanceSquared(unit.Position, entrancePos) <= arriveSq)
            {
                if (!TryCommitEntry(entry, unitId, slotKind, world, _navigation))
                {
                    // Entry blocked (own team full or roof collapsed en route): cancel cleanly
                    world.ClearPendingEnterOrder(unitId);
                    world.UpdateUnitNavigation(unitId, unit.Position, unit.Position, active: false, supportSurface: unit.SupportSurface);
                    _navigation?.ClearUnit(unitId);
                }
                RemovePendingEnterUnitAt(p);
                continue;
            }

            // Cancel cleanly if navigation reports the entrance is unreachable
            if (_navigation is not null)
            {
                PathRequestStatus navStatus = _navigation.GetUnitStatus(unitId);
                if (navStatus is PathRequestStatus.NoPath or PathRequestStatus.InvalidDestination)
                {
                    world.ClearPendingEnterOrder(unitId);
                    RemovePendingEnterUnitAt(p);
                    continue;
                }
            }

            p++;
        }
    }

    /// <summary>
    /// Computes elevated 3D eye/muzzle/target point for a garrisoned unit facing <paramref name="towardsXZ"/>
    /// using precomputed rotated world ports (zero trig calls in hot path).
    /// Returns false if unit is not garrisoned in a registered building.
    /// </summary>
    public bool TryGetElevatedPoint(WorldState world, EntityId unitId, Vector2 towardsXZ, out Vector3 worldPoint)
    {
        if (!world.TryGetGarrison(unitId, out StructureId structId, out GarrisonSlotKind slotKind) ||
            !_buildings.TryGetValue(structId, out BuildingEntry? entry))
        {
            worldPoint = default;
            return false;
        }

        StructureDestructionProfile destProfile;
        byte floorsStanding;
        if (world.TryGetDestructibleStructure(structId, out DestructibleStructureState destState))
        {
            destProfile = destState.Profile;
            floorsStanding = destState.FloorsStanding;
        }
        else
        {
            // Non-destructible registered building fallback (treat as single-story intact)
            destProfile = new StructureDestructionProfile(1, MathF.Max(2.5f, entry.Bounds.MaxY - entry.Bounds.MinY), 1.0f, 100);
            floorsStanding = 1;
        }

        Vector2 dir = towardsXZ - entry.CenterXZ;
        float dirLenSq = dir.LengthSquared();
        Vector2 dirNorm = dirLenSq > 1e-4f ? dir / MathF.Sqrt(dirLenSq) : new Vector2(0f, 1f);

        // Rooftop garrison (only valid when full roof stands)
        if (slotKind == GarrisonSlotKind.Roof && floorsStanding >= destProfile.FloorsTotal)
        {
            PrecomputedRoofPort[] roofPorts = entry.WorldRoofPorts;
            if (roofPorts.Length > 0)
            {
                int bestIdx = 0;
                float bestDot = float.NegativeInfinity;
                for (int i = 0; i < roofPorts.Length; i++)
                {
                    float d = roofPorts[i].OffsetX * dirNorm.X + roofPorts[i].OffsetZ * dirNorm.Y;
                    if (d > bestDot)
                    {
                        bestDot = d;
                        bestIdx = i;
                    }
                }
                PrecomputedRoofPort rp = roofPorts[bestIdx];
                worldPoint = new Vector3(rp.WorldX, rp.WorldY, rp.WorldZ);
                return true;
            }
            worldPoint = new Vector3(entry.CenterXZ.X, entry.Bounds.MaxY + 1.15f, entry.CenterXZ.Y);
            return true;
        }

        // Collapsed rubble garrison (0 floors standing)
        if (floorsStanding == 0)
        {
            float rubbleEyeY = entry.Bounds.MinY + destProfile.RubbleHeight + 0.55f;
            float halfX = (entry.Bounds.MaxX - entry.Bounds.MinX) * 0.5f;
            float halfZ = (entry.Bounds.MaxZ - entry.Bounds.MinZ) * 0.5f;
            float px = Math.Clamp(entry.CenterXZ.X + dirNorm.X * (halfX + GarrisonMath.ExteriorClearanceMeters), entry.Bounds.MinX - GarrisonMath.ExteriorClearanceMeters, entry.Bounds.MaxX + GarrisonMath.ExteriorClearanceMeters);
            float pz = Math.Clamp(entry.CenterXZ.Y + dirNorm.Y * (halfZ + GarrisonMath.ExteriorClearanceMeters), entry.Bounds.MinZ - GarrisonMath.ExteriorClearanceMeters, entry.Bounds.MaxZ + GarrisonMath.ExteriorClearanceMeters);
            worldPoint = new Vector3(px, rubbleEyeY, pz);
            return true;
        }

        // Interior garrison: precomputed world window port on the top standing floor facing towardsXZ
        float floorBaseY = entry.Bounds.MinY + (floorsStanding - 1) * destProfile.FloorHeight;
        PrecomputedFiringPort[] ports = entry.WorldFiringPorts;
        if (ports.Length > 0)
        {
            int bestIdx = 0;
            float bestScore = float.NegativeInfinity;
            for (int i = 0; i < ports.Length; i++)
            {
                float dot = ports[i].OutNormalX * dirNorm.X + ports[i].OutNormalZ * dirNorm.Y;
                if (dot > bestScore)
                {
                    bestScore = dot;
                    bestIdx = i;
                }
            }
            PrecomputedFiringPort fp = ports[bestIdx];
            worldPoint = new Vector3(fp.WorldX, floorBaseY + fp.LocalY, fp.WorldZ);
            return true;
        }

        // Fallback exterior face point when profile specifies no explicit firing ports
        float halfW = (entry.Bounds.MaxX - entry.Bounds.MinX) * 0.5f;
        float halfD = (entry.Bounds.MaxZ - entry.Bounds.MinZ) * 0.5f;
        if (MathF.Abs(dirNorm.X) * halfD > MathF.Abs(dirNorm.Y) * halfW)
        {
            float sx = dirNorm.X >= 0f ? 1f : -1f;
            worldPoint = new Vector3(entry.CenterXZ.X + sx * (halfW + GarrisonMath.ExteriorClearanceMeters), floorBaseY + 1.4f, entry.CenterXZ.Y);
        }
        else
        {
            float sz = dirNorm.Y >= 0f ? 1f : -1f;
            worldPoint = new Vector3(entry.CenterXZ.X, floorBaseY + 1.4f, entry.CenterXZ.Y + sz * (halfD + GarrisonMath.ExteriorClearanceMeters));
        }
        return true;
    }

    /// <summary>
    /// Fog-of-war concealment check for hostile garrisoned infantry:
    /// - Roof troops are exposed and observable at normal sensor range (subject to 3D LOS).
    /// - Interior troops are concealed beyond ConcealmentSpotRange unless they fired within FiringRevealTicks.
    /// </summary>
    public bool IsGarrisonTargetObservable(WorldState world, int tick, EntityId targetId, float distanceToObserver)
    {
        if (!world.TryGetGarrison(targetId, out StructureId structId, out GarrisonSlotKind slotKind))
            return true;
        if (!_buildings.TryGetValue(structId, out BuildingEntry? entry))
            return true;

        if (slotKind == GarrisonSlotKind.Roof)
            return true;

        if (distanceToObserver <= entry.Profile.ConcealmentSpotRange)
            return true;

        int lastFired = world.GetUnitLastFiredTick(targetId);
        if (lastFired >= 0 && (tick - lastFired) <= entry.Profile.FiringRevealTicks)
            return true;

        return false;
    }

    /// <summary>
    /// Returns the incoming direct-fire damage multiplier for a garrisoned infantry unit
    /// (0.35 inside standing building, 0.55 in collapsed rubble, 0.70 on rooftop parapet).
    /// </summary>
    public float GetIncomingCoverDamageMultiplier(WorldState world, EntityId targetId)
    {
        if (!world.TryGetGarrison(targetId, out StructureId structId, out GarrisonSlotKind slotKind) ||
            !_buildings.TryGetValue(structId, out BuildingEntry? entry))
        {
            return 1.0f;
        }

        if (slotKind == GarrisonSlotKind.Roof)
            return entry.Profile.RoofCoverDamageMultiplier;

        if (world.TryGetDestructibleStructure(structId, out DestructibleStructureState destState) && destState.IsCollapsed)
            return entry.Profile.RubbleCoverDamageMultiplier;

        return entry.Profile.CoverDamageMultiplier;
    }

    /// <summary>
    /// Computes deterministic structural blast and collapse casualties for all troops garrisoned
    /// inside or on the roof of a struck building section into a caller-supplied span (zero allocations).
    /// Also demotes surviving rooftop troops to Interior when the roof collapses.
    /// </summary>
    public int ComputeStructuralHitCasualties(
        WorldState world,
        StructureId structureId,
        int effectiveStructuralDamage,
        byte floorsLost,
        bool sectionCollapsed,
        Span<GarrisonCasualty> results)
    {
        if (!_buildings.TryGetValue(structureId, out BuildingEntry? entry) || entry.OccupantCount == 0)
            return 0;

        int count = entry.OccupantCount;
        for (int i = 0; i < count; i++)
        {
            _scratchOccupants[i] = entry.Occupants[i];
        }

        Vector3 center = new(entry.CenterXZ.X, entry.Bounds.MinY + 1.5f, entry.CenterXZ.Y);

        int written = 0;
        for (int i = 0; i < count && written < results.Length; i++)
        {
            EntityId occId = _scratchOccupants[i];
            if (!world.TryGetUnit(occId, out _) || !world.TryGetGarrison(occId, out StructureId st, out GarrisonSlotKind slot) || st != structureId)
                continue;

            int damage = Math.Max(1, effectiveStructuralDamage / 32);
            if (floorsLost > 0)
            {
                if (slot == GarrisonSlotKind.Roof)
                {
                    damage += Math.Max(6, floorsLost * 5);
                    world.SetGarrisonMembership(occId, structureId, GarrisonSlotKind.Interior);
                }
                else
                {
                    damage += Math.Max(2, floorsLost * 3);
                }
            }
            if (sectionCollapsed)
            {
                damage += 6;
                world.SetGarrisonMembership(occId, structureId, GarrisonSlotKind.Interior);
            }

            results[written++] = new GarrisonCasualty(occId, damage, center);
        }

        return written;
    }

    public void RecordCollapseCasualty() => TotalCollapseCasualties++;

    private bool TryCommitEntry(
        BuildingEntry entry,
        EntityId unitId,
        GarrisonSlotKind slotKind,
        WorldState world,
        NavigationSystem? navigation)
    {
        if (!world.TryGetUnit(unitId, out UnitState unit))
            return false;
        if (!_isInfantryDefinition(unit.Definition))
            return false;
        if (world.IsContained(unitId) || world.IsGarrisoned(unitId))
            return false;

        int unitTeam = world.GetPlayerTeam(unit.Owner);
        if (CountTeamOccupants(entry, world, unitTeam) >= entry.Profile.Capacity)
            return false;
        if (entry.OccupantCount >= BuildingGarrisonProfile.MaxOccupantsPerStructure)
            return false;
        if (slotKind == GarrisonSlotKind.Roof && !CanOccupyRoof(world, entry.Id))
            return false;

        entry.Occupants[entry.OccupantCount++] = unitId;
        world.ClearPendingEnterOrder(unitId);
        world.SetGarrisonMembership(unitId, entry.Id, slotKind);

        world.SetPosition(unitId, entry.CenterXZ);
        world.UpdateUnitNavigation(unitId, entry.CenterXZ, entry.CenterXZ, active: false, supportSurface: 0u);
        (navigation ?? _navigation)?.ClearUnit(unitId);
        TotalEntries++;
        return true;
    }

    private bool TryCommitAdjacentTransfer(
        BuildingEntry fromEntry,
        BuildingEntry toEntry,
        EntityId unitId,
        GarrisonSlotKind requestedSlotKind,
        WorldState world)
    {
        if (!world.TryGetUnit(unitId, out UnitState unit))
            return false;
        if (!_isInfantryDefinition(unit.Definition))
            return false;

        int unitTeam = world.GetPlayerTeam(unit.Owner);
        if (CountTeamOccupants(toEntry, world, unitTeam) >= toEntry.Profile.Capacity)
            return false;
        if (toEntry.OccupantCount >= BuildingGarrisonProfile.MaxOccupantsPerStructure)
            return false;

        GarrisonSlotKind finalSlot = requestedSlotKind == GarrisonSlotKind.Roof && !CanOccupyRoof(world, toEntry.Id)
            ? GarrisonSlotKind.Interior
            : requestedSlotKind;

        RemoveOccupant(fromEntry, unitId);
        toEntry.Occupants[toEntry.OccupantCount++] = unitId;
        world.SetGarrisonMembership(unitId, toEntry.Id, finalSlot);
        world.SetPosition(unitId, toEntry.CenterXZ);
        world.UpdateUnitNavigation(unitId, toEntry.CenterXZ, toEntry.CenterXZ, active: false, supportSurface: 0u);
        TotalAdjacentTransfers++;
        return true;
    }

    private static bool AreBoundsAdjacent(OpaqueStructure a, OpaqueStructure b)
    {
        float gapX = MathF.Max(0f, MathF.Max(a.MinX - b.MaxX, b.MinX - a.MaxX));
        float gapZ = MathF.Max(0f, MathF.Max(a.MinZ - b.MaxZ, b.MinZ - a.MaxZ));
        float overlapX = MathF.Min(a.MaxX, b.MaxX) - MathF.Max(a.MinX, b.MinX);
        float overlapZ = MathF.Min(a.MaxZ, b.MaxZ) - MathF.Max(a.MinZ, b.MinZ);

        bool touchesAlongX = gapX <= AdjacencyMaxGapMeters && overlapZ >= AdjacencyMinOverlapMeters;
        bool touchesAlongZ = gapZ <= AdjacencyMaxGapMeters && overlapX >= AdjacencyMinOverlapMeters;
        return touchesAlongX || touchesAlongZ;
    }

    private static void LinkAdjacent(BuildingEntry entry, StructureId neighborId)
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

    private void TrackPendingEnterUnit(EntityId unitId)
    {
        for (int i = 0; i < _pendingEnterCount; i++)
        {
            if (_pendingEnterUnits[i] == unitId)
                return;
        }
        if (_pendingEnterCount < _pendingEnterUnits.Length)
        {
            _pendingEnterUnits[_pendingEnterCount++] = unitId;
        }
    }

    private void RemovePendingEnterUnit(EntityId unitId)
    {
        for (int i = 0; i < _pendingEnterCount; i++)
        {
            if (_pendingEnterUnits[i] == unitId)
            {
                RemovePendingEnterUnitAt(i);
                return;
            }
        }
    }

    private void RemovePendingEnterUnitAt(int index)
    {
        int last = --_pendingEnterCount;
        if (index != last)
        {
            _pendingEnterUnits[index] = _pendingEnterUnits[last];
        }
        _pendingEnterUnits[last] = default;
    }

    private void TrackRoofAutoDescend(EntityId unitId, int ticks)
    {
        for (int i = 0; i < _roofAutoDescendCount; i++)
        {
            if (_roofAutoDescendUnits[i] == unitId)
            {
                _roofAutoDescendTicks[i] = ticks;
                return;
            }
        }
        if (_roofAutoDescendCount < _roofAutoDescendUnits.Length)
        {
            _roofAutoDescendUnits[_roofAutoDescendCount] = unitId;
            _roofAutoDescendTicks[_roofAutoDescendCount] = ticks;
            _roofAutoDescendCount++;
        }
    }

    private void RemoveRoofAutoDescendAt(int index)
    {
        int last = --_roofAutoDescendCount;
        if (index != last)
        {
            _roofAutoDescendUnits[index] = _roofAutoDescendUnits[last];
            _roofAutoDescendTicks[index] = _roofAutoDescendTicks[last];
        }
        _roofAutoDescendUnits[last] = default;
        _roofAutoDescendTicks[last] = 0;
    }

    public bool CanOccupyRoof(WorldState world, StructureId structureId)
    {
        if (world.TryGetDestructibleStructure(structureId, out DestructibleStructureState destState))
        {
            return destState.FloorsStanding >= destState.Profile.FloorsTotal;
        }
        return true;
    }

    private static int CountTeamOccupants(BuildingEntry entry, WorldState world, int team)
    {
        int count = 0;
        for (int i = 0; i < entry.OccupantCount; i++)
        {
            if (world.TryGetUnit(entry.Occupants[i], out UnitState occ) &&
                world.GetPlayerTeam(occ.Owner) == team)
            {
                count++;
            }
        }
        return count;
    }

    private static int RemoveOccupant(BuildingEntry entry, EntityId unitId)
    {
        for (int i = 0; i < entry.OccupantCount; i++)
        {
            if (entry.Occupants[i] == unitId)
            {
                RemoveOccupantAt(entry, i);
                return i;
            }
        }
        return -1;
    }

    private static void RemoveOccupantAt(BuildingEntry entry, int index)
    {
        int last = --entry.OccupantCount;
        if (index != last)
        {
            entry.Occupants[index] = entry.Occupants[last];
        }
        entry.Occupants[last] = default;
    }

    private Vector2 ResolveWalkableEntrance(Vector2 rawEntrance)
    {
        if (IsFootPointClear(rawEntrance))
            return rawEntrance;

        if (_navigation is not null &&
            _navigation.Grid.Evaluator.TrySnapDestination(
                rawEntrance,
                rawEntrance,
                FootClearanceRadius,
                TraversabilityCapabilities.Foot,
                targetSurface: 0u,
                maxSnapDistance: 12.0f,
                out Vector2 snapped))
        {
            return snapped;
        }

        return rawEntrance;
    }

    private Vector2 ComputeSafeExitPosition(BuildingEntry entry)
    {
        // Fan exiting infantry across 20 distinct slots outside the entrance facade,
        // advancing ExitCursor past any slot blocked by walls, neighboring buildings, or impassable terrain.
        int startSlot = ((entry.ExitCursor % ExitFanSlotCount) + ExitFanSlotCount) % ExitFanSlotCount;
        for (int attempt = 0; attempt < ExitFanSlotCount; attempt++)
        {
            int slot = (startSlot + attempt) % ExitFanSlotCount;
            int lateralRank = (slot % 5) - 2; // -2, -1, 0, +1, +2
            int depthRank = slot / 5;         // 0, 1, 2, 3
            Vector2 candidate = ClampToMap(
                entry.EntranceWorldPos
                + entry.OutwardDir * (1.2f + depthRank * 1.2f)
                + entry.TangentDir * (lateralRank * 1.2f));
            if (IsFootPointClear(candidate))
            {
                entry.ExitCursor = (slot + 1) % ExitFanSlotCount;
                return candidate;
            }
        }

        entry.ExitCursor = (startSlot + 1) % ExitFanSlotCount;
        if (_navigation is not null &&
            _navigation.Grid.Evaluator.TrySnapDestination(
                entry.EntranceWorldPos,
                entry.EntranceWorldPos,
                FootClearanceRadius,
                TraversabilityCapabilities.Foot,
                targetSurface: 0u,
                maxSnapDistance: 12.0f,
                out Vector2 snapped))
        {
            return snapped;
        }

        return entry.EntranceWorldPos;
    }

    private bool IsFootPointClear(Vector2 pos)
    {
        if (_navigation is not null)
        {
            return _navigation.Grid.Evaluator.IsPointClear(pos, FootClearanceRadius, TraversabilityCapabilities.Foot, supportSurface: 0u);
        }

        if (pos.X < FootClearanceRadius || pos.X > _geometry.HeightMap.Width - FootClearanceRadius ||
            pos.Y < FootClearanceRadius || pos.Y > _geometry.HeightMap.Depth - FootClearanceRadius)
        {
            return false;
        }

        IReadOnlyList<OpaqueStructure> structures = _geometry.Structures;
        for (int i = 0; i < structures.Count; i++)
        {
            OpaqueStructure s = structures[i];
            if (!s.IsGrounded) continue;
            if (pos.X >= s.MinX - FootClearanceRadius && pos.X <= s.MaxX + FootClearanceRadius &&
                pos.Y >= s.MinZ - FootClearanceRadius && pos.Y <= s.MaxZ + FootClearanceRadius)
            {
                return false;
            }
        }
        return true;
    }

    private Vector2 ClampToMap(Vector2 pos)
    {
        float maxW = MathF.Max(1f, _geometry.HeightMap.Width - 1f);
        float maxD = MathF.Max(1f, _geometry.HeightMap.Depth - 1f);
        return new Vector2(Math.Clamp(pos.X, 1f, maxW), Math.Clamp(pos.Y, 1f, maxD));
    }
}
