namespace DeadGround.Engine;

/// <summary>
/// Immutable physical movement and clearance profile of a unit definition.
/// </summary>
public readonly record struct UnitMovementProfile(float Speed, float Radius);

/// <summary>
/// Catalog interface providing unit speed and radius properties to navigation systems.
/// </summary>
public interface IUnitMovementCatalog
{
    bool TryGetProfile(ushort definition, out UnitMovementProfile profile);
}
