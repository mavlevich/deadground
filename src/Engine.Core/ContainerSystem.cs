using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Authoritative simulation system managing container occupancy, boarding approach,
/// passenger containment lockstep positions, and seat reservation lifecycle.
/// </summary>
public sealed class ContainerSystem : ISimulationSystem
{
    public const float BoardingDistanceMargin = 1.8f;
    private readonly NavigationSystem? _navigation;
    private readonly IUnitMovementCatalog? _catalog;

    public ContainerSystem(NavigationSystem? navigation = null, IUnitMovementCatalog? catalog = null)
    {
        _navigation = navigation;
        _catalog = catalog ?? navigation?.Catalog;
    }

    public void Update(WorldState world, int tick)
    {
        ArgumentNullException.ThrowIfNull(world);

        // 1. Process active boarding orders
        int unitCount = world.Count;
        for (int i = 0; i < unitCount; i++)
        {
            UnitState unit = world.GetUnitAt(i);
            if (!world.TryGetBoardingTarget(unit.Id, out EntityId transportId))
                continue;

            // Check if target transport is still valid and alive
            if (!world.TryGetUnit(transportId, out UnitState transport))
            {
                world.ClearBoardingOrder(unit.Id);
                _navigation?.ClearUnit(unit.Id);
                continue;
            }

            // If transport began moving, took off, or became slung while unit was approaching, cancel boarding
            if (transport.Moving || world.IsAirborne(transport.Id) || world.IsSlung(transport.Id) || world.IsSlung(unit.Id))
            {
                world.ClearBoardingOrder(unit.Id);
                _navigation?.ClearUnit(unit.Id);
                world.UpdateUnitNavigation(unit.Id, unit.Position, unit.Position, false, tick, unit.SupportSurface);
                continue;
            }

            // Cancel boarding if navigation failed (NoPath, InvalidDestination, CapacityExceeded)
            if (_navigation is not null)
            {
                PathRequestStatus navStatus = _navigation.GetUnitStatus(unit.Id);
                if (navStatus is PathRequestStatus.NoPath or PathRequestStatus.InvalidDestination or PathRequestStatus.CapacityExceeded)
                {
                    world.ClearBoardingOrder(unit.Id);
                    _navigation.ClearUnit(unit.Id);
                    world.UpdateUnitNavigation(unit.Id, unit.Position, unit.Position, false, tick, unit.SupportSurface);
                    continue;
                }
            }

            // Check boarding distance to transport using catalog dimensions
            float transportRadius = (_catalog is not null && _catalog.TryGetProfile(transport.Definition, out UnitMovementProfile tProf) && tProf.EffectiveCollisionRadius > 0f)
                ? tProf.EffectiveCollisionRadius
                : 1.4f;
            float soldierRadius = (_catalog is not null && _catalog.TryGetProfile(unit.Definition, out UnitMovementProfile sProf) && sProf.EffectiveCollisionRadius > 0f)
                ? sProf.EffectiveCollisionRadius
                : 0.4f;
            float maxBoardDist = transportRadius + soldierRadius + BoardingDistanceMargin;
            float dist = Vector2.Distance(unit.Position, transport.Position);
            if (dist <= maxBoardDist)
            {
                int slots = world.GetBoardingSlots(unit.Id);
                world.Board(transport.Id, unit.Id, slots);
                _navigation?.ClearUnit(unit.Id);
                world.ClearAttackOrder(unit.Id);
            }
        }

        // 2. Lockstep passenger positions and surfaces to their containers
        world.UpdateContainedPositions();
    }
}
