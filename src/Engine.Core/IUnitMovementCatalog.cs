namespace DeadGround.Engine;

/// <summary>
/// Immutable physical movement, clearance, and traversability capability profile of a unit definition.
/// Radius determines physical clearance (MovementClass).
/// Capabilities determines which terrain types (ground, dense forest, water, bridges) the unit can enter.
/// </summary>
public readonly record struct UnitMovementProfile(
    float Speed,
    float Radius,
    TraversabilityCapabilities Capabilities = TraversabilityCapabilities.DefaultVehicle)
{
    public MovementClass MovementClass => MovementClassExtensions.FromRadius(Radius);
}

/// <summary>
/// Catalog interface providing unit speed and radius properties to navigation systems.
/// </summary>
public interface IUnitMovementCatalog
{
    bool TryGetProfile(ushort definition, out UnitMovementProfile profile);
}
