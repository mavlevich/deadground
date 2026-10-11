namespace DeadGround.Engine;

/// <summary>
/// Combined visual perception profile for a unit definition,
/// decoupling simulation logic from concrete data schemas.
/// </summary>
public readonly record struct UnitPerceptionProfile(
    VisualSensorProfile? Sensor,
    TargetObservationPoints? TargetPoints);

/// <summary>
/// Catalog interface providing perception parameters for unit definition indices.
/// </summary>
public interface IUnitPerceptionCatalog
{
    bool TryGetProfile(ushort definition, out UnitPerceptionProfile profile);
}
