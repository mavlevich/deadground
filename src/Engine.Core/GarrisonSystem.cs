using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Server-authoritative building garrison simulation system (Stage 2, PR 2.1 thin slice).
/// Manages infantry entry/exit through building entrances, single-side occupancy, elevated
/// window/roof LOS and firing ports, cover damage reduction, concealment rules, and deterministic
/// structural blast/collapse casualties. Strictly allocation-free during simulation ticks.
/// </summary>
public sealed class GarrisonSystem : ISimulationSystem
{
    public const float EntranceArrivalRadius = 3.5f;

    private sealed class BuildingEntry
    {
        public StructureId Id;
        public int GeometryIndex;
        public OpaqueStructure Bounds = null!;
        public float YawRad;
        public BuildingGarrisonProfile Profile = null!;
        public Vector2 EntranceWorldPos;
        public readonly EntityId[] Occupants = new EntityId[BuildingGarrisonProfile.MaxOccupantsPerStructure];
        public int OccupantCount;
    }

    private readonly StaticGeometry _geometry;
    private readonly Func<ushort, bool> _isInfantryDefinition;
    private readonly Dictionary<StructureId, BuildingEntry> _buildings = new(WorldState.MaxDestructibleStructures);
    private readonly BuildingEntry[] _buildingList = new BuildingEntry[WorldState.MaxDestructibleStructures];
    private readonly EntityId[] _scratchOccupants = new EntityId[BuildingGarrisonProfile.MaxOccupantsPerStructure];
    private NavigationSystem? _navigation;
    private int _buildingCount;

    public int RegisteredBuildingCount => _buildingCount;
    public int TotalEntries { get; private set; }
    public int TotalExits { get; private set; }
    public int TotalCollapseCasualties { get; private set; }

    public GarrisonSystem(StaticGeometry geometry, Func<ushort, bool> isInfantryDefinition)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(isInfantryDefinition);
        _geometry = geometry;
        _isInfantryDefinition = isInfantryDefinition;
    }

    public void SetNavigationSystem(NavigationSystem? navigation) => _navigation = navigation;

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
        Vector2 entrance = GarrisonMath.ComputeWorldEntrance(bounds, yawRad, profile);
        entrance = ClampToMap(entrance);

        BuildingEntry entry = new()
        {
            Id = structureId,
            GeometryIndex = geomIdx,
            Bounds = bounds,
            YawRad = yawRad,
            Profile = profile,
            EntranceWorldPos = entrance
        };
        _buildings[structureId] = entry;
        _buildingList[_buildingCount++] = entry;
        return true;
    }

    public bool IsRegistered(StructureId structureId) => _buildings.ContainsKey(structureId);

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
        bool alreadyInsideSame = world.TryGetGarrison(cmd.Unit, out StructureId curStruct, out GarrisonSlotKind curSlot) &&
            curStruct == cmd.TargetStructure;
        if (alreadyInsideSame && curSlot == cmd.SlotKind)
            return false;

        // Must exit current building before entering a different building (PR 2.1 thin slice)
        if (!alreadyInsideSame && world.IsGarrisoned(cmd.Unit))
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

        // Already inside this section: switch Interior <-> Roof directly if roof is intact
        if (world.TryGetGarrison(cmd.Unit, out StructureId curStruct, out _) && curStruct == cmd.TargetStructure)
        {
            if (cmd.SlotKind == GarrisonSlotKind.Roof && !CanOccupyRoof(world, entry.Id))
                return false;
            world.SetGarrisonMembership(cmd.Unit, cmd.TargetStructure, cmd.SlotKind);
            return true;
        }

        // If already at the entrance, enter immediately without detouring
        float distSq = Vector2.DistanceSquared(unit.Position, entry.EntranceWorldPos);
        if (distSq <= EntranceArrivalRadius * EntranceArrivalRadius)
        {
            return TryCommitEntry(entry, cmd.Unit, cmd.SlotKind, world, navigation);
        }

        // Walk to entrance and enter on arrival
        world.SetPendingEnterOrder(cmd.Unit, cmd.TargetStructure, cmd.SlotKind, entry.EntranceWorldPos);
        NavigationSystem? nav = navigation ?? _navigation;
        if (nav is not null)
        {
            nav.EnqueueCommand(player, new MoveCommand(cmd.Sequence, cmd.ClientTick, cmd.Unit, entry.EntranceWorldPos), world);
        }
        else
        {
            world.UpdateUnitNavigation(cmd.Unit, unit.Position, entry.EntranceWorldPos, active: true, supportSurface: unit.SupportSurface);
        }
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

        int slotIndex = RemoveOccupant(entry, unitId);
        world.ClearGarrisonMembership(unitId);
        world.ClearPendingEnterOrder(unitId);

        // Compute deterministic non-overlapping fan position outside the building entrance
        Vector2 exitPos = ComputeSafeExitPosition(entry, Math.Max(0, slotIndex));
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

        // 2. Complete pending enter orders once infantry reaches the entrance radius
        int unitCount = world.Count;
        float arriveSq = EntranceArrivalRadius * EntranceArrivalRadius;
        for (int i = 0; i < unitCount; i++)
        {
            UnitState unit = world.GetUnitAt(i);
            if (!world.TryGetPendingEnterOrder(unit.Id, out StructureId targetStruct, out GarrisonSlotKind slotKind, out Vector2 entrancePos))
                continue;

            if (world.IsContained(unit.Id) || world.IsGarrisoned(unit.Id) || !_buildings.TryGetValue(targetStruct, out BuildingEntry? entry))
            {
                world.ClearPendingEnterOrder(unit.Id);
                continue;
            }

            if (Vector2.DistanceSquared(unit.Position, entrancePos) <= arriveSq)
            {
                if (!TryCommitEntry(entry, unit.Id, slotKind, world, _navigation))
                {
                    // Entry blocked (enemy occupied, full, or roof collapsed en route): cancel cleanly
                    world.ClearPendingEnterOrder(unit.Id);
                    world.UpdateUnitNavigation(unit.Id, unit.Position, unit.Position, active: false, supportSurface: unit.SupportSurface);
                    _navigation?.ClearUnit(unit.Id);
                }
            }
        }
    }

    /// <summary>
    /// Computes elevated 3D eye/muzzle/target point for a garrisoned unit facing <paramref name="towardsXZ"/>.
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

        worldPoint = GarrisonMath.ResolveElevatedPoint(
            entry.Bounds,
            entry.YawRad,
            in destProfile,
            entry.Profile,
            floorsStanding,
            slotKind,
            towardsXZ);
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

        Vector3 center = new(
            (entry.Bounds.MinX + entry.Bounds.MaxX) * 0.5f,
            entry.Bounds.MinY + 1.5f,
            (entry.Bounds.MinZ + entry.Bounds.MaxZ) * 0.5f);

        int written = 0;
        for (int i = 0; i < count && written < results.Length; i++)
        {
            EntityId occId = _scratchOccupants[i];
            if (!world.TryGetUnit(occId, out _) || !world.TryGetGarrison(occId, out StructureId st, out GarrisonSlotKind slot) || st != structureId)
                continue;

            int damage = Math.Max(1, effectiveStructuralDamage / 14);
            if (floorsLost > 0)
            {
                if (slot == GarrisonSlotKind.Roof)
                {
                    damage += Math.Max(8, floorsLost * 6);
                    world.SetGarrisonMembership(occId, structureId, GarrisonSlotKind.Interior);
                }
                else
                {
                    damage += Math.Max(4, floorsLost * 4);
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
        if (entry.OccupantCount >= entry.Profile.Capacity)
            return false;
        if (slotKind == GarrisonSlotKind.Roof && !CanOccupyRoof(world, entry.Id))
            return false;

        int unitTeam = world.GetPlayerTeam(unit.Owner);
        if (IsOccupiedByHostile(entry, world, unitTeam))
            return false;

        entry.Occupants[entry.OccupantCount++] = unitId;
        world.ClearPendingEnterOrder(unitId);
        world.SetGarrisonMembership(unitId, entry.Id, slotKind);

        Vector2 buildingCenter = new(
            (entry.Bounds.MinX + entry.Bounds.MaxX) * 0.5f,
            (entry.Bounds.MinZ + entry.Bounds.MaxZ) * 0.5f);
        world.SetPosition(unitId, buildingCenter);
        world.UpdateUnitNavigation(unitId, buildingCenter, buildingCenter, active: false, supportSurface: 0u);
        (navigation ?? _navigation)?.ClearUnit(unitId);
        TotalEntries++;
        return true;
    }

    private bool CanOccupyRoof(WorldState world, StructureId structureId)
    {
        if (world.TryGetDestructibleStructure(structureId, out DestructibleStructureState destState))
        {
            return destState.FloorsStanding >= destState.Profile.FloorsTotal;
        }
        return true;
    }

    private static bool IsOccupiedByHostile(BuildingEntry entry, WorldState world, int queryingTeam)
    {
        for (int i = 0; i < entry.OccupantCount; i++)
        {
            if (world.TryGetUnit(entry.Occupants[i], out UnitState occ) &&
                world.GetPlayerTeam(occ.Owner) != queryingTeam)
            {
                return true;
            }
        }
        return false;
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

    private Vector2 ComputeSafeExitPosition(BuildingEntry entry, int slotIndex)
    {
        // Fan exiting infantry along a shallow arc outside the entrance facade so troops never stack
        Vector2 outward = GarrisonMath.RotateLocalDirection(entry.YawRad, 0f, 1f);
        Vector2 tangent = new(-outward.Y, outward.X);
        int lateralRank = (slotIndex % 5) - 2; // -2, -1, 0, +1, +2
        int depthRank = slotIndex / 5;         // 0, 1, 2
        Vector2 pos = entry.EntranceWorldPos
            + outward * (1.2f + depthRank * 1.1f)
            + tangent * (lateralRank * 1.0f);
        return ClampToMap(pos);
    }

    private Vector2 ClampToMap(Vector2 pos)
    {
        float maxW = MathF.Max(1f, _geometry.HeightMap.Width - 1f);
        float maxD = MathF.Max(1f, _geometry.HeightMap.Depth - 1f);
        return new Vector2(Math.Clamp(pos.X, 1f, maxW), Math.Clamp(pos.Y, 1f, maxD));
    }
}
