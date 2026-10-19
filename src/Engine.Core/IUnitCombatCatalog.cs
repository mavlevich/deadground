namespace DeadGround.Engine;

/// <summary>
/// Data-driven weapon specification for direct-fire combat.
/// </summary>
public sealed record WeaponDefinition(
    string Id,
    float Range,
    int ReloadDurationTicks,
    int Damage,
    int AmmoCapacity,
    float MuzzleHeight,
    ushort PresentationId);

/// <summary>
/// Combat attributes of a unit definition including health and weapon reference.
/// </summary>
public readonly record struct UnitCombatProfile(
    int MaxHealth,
    WeaponDefinition? Weapon,
    float TurretTraverseRate = 0f);

/// <summary>
/// Catalog interface providing combat profiles to combat systems.
/// </summary>
public interface IUnitCombatCatalog
{
    bool TryGetProfile(ushort definition, out UnitCombatProfile profile);
}
