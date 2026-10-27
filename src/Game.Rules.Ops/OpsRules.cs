using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

public sealed class OpsRules : RuleSet
{
    public OpsRules() : base(new MovementSystem()) { }
    public OpsRules(params ISimulationSystem[] systems) : base(systems) { }

    public OpsRules(StaticGeometry geometry, IUnitPerceptionCatalog catalog, DetectionTuning? tuning = null)
        : base(new MovementSystem(), new DetectionSystem(geometry, catalog, tuning)) { }

    public OpsRules(
        StaticGeometry geometry,
        IUnitPerceptionCatalog perceptionCatalog,
        IUnitMovementCatalog movementCatalog,
        NavigationTuning? navTuning = null,
        DetectionTuning? detTuning = null)
        : this(geometry, perceptionCatalog, movementCatalog, null, navTuning, detTuning, null)
    { }

    public OpsRules(
        StaticGeometry geometry,
        IUnitPerceptionCatalog perceptionCatalog,
        IUnitMovementCatalog movementCatalog,
        IUnitCombatCatalog? combatCatalog,
        NavigationTuning? navTuning = null,
        DetectionTuning? detTuning = null,
        DeterministicRandom? combatRandom = null)
        : base(CreateSystems(geometry, perceptionCatalog, movementCatalog, combatCatalog, navTuning, detTuning, combatRandom))
    { }

    public OpsRules(StaticGeometry geometry, UnitCatalog catalog, DeterministicRandom? combatRandom = null)
        : this(geometry, new OpsPerceptionCatalog(catalog), new OpsMovementCatalog(catalog), new OpsCombatCatalog(catalog), null, PrototypeScenario.CreateDetectionTuning(catalog), combatRandom) { }

    public OpsRules(StaticGeometry geometry, UnitCatalog catalog, NavigationTuning? navTuning, DetectionTuning? detTuning = null, DeterministicRandom? combatRandom = null)
        : this(
            geometry,
            new OpsPerceptionCatalog(catalog),
            new OpsMovementCatalog(catalog),
            new OpsCombatCatalog(catalog),
            navTuning,
            detTuning ?? PrototypeScenario.CreateDetectionTuning(catalog),
            combatRandom)
    { }

    private static ISimulationSystem[] CreateSystems(
        StaticGeometry geometry,
        IUnitPerceptionCatalog perceptionCatalog,
        IUnitMovementCatalog movementCatalog,
        IUnitCombatCatalog? combatCatalog,
        NavigationTuning? navTuning,
        DetectionTuning? detTuning,
        DeterministicRandom? combatRandom = null)
    {
        NavigationSystem nav = new(new NavigationGrid(geometry, navTuning?.CellSize ?? NavigationTuning.DefaultCellSize, enableHierarchical: true, sectorSize: navTuning?.SectorSize ?? NavigationTuning.DefaultSectorSize), movementCatalog, navTuning);
        DetectionSystem det = new(geometry, perceptionCatalog, detTuning);
        if (combatCatalog is not null)
        {
            CombatSystem combat = new(geometry, combatCatalog, perceptionCatalog, det, nav, combatRandom: combatRandom);
            return [nav, det, combat];
        }
        return [nav, det];
    }
}

public sealed record DetectionSettings(
    int MemoryDurationTicks = DetectionTuning.DefaultMemoryDurationTicks,
    int CorrelationWindowTicks = DetectionTuning.DefaultCorrelationWindowTicks,
    float CorrelationDistanceGate = DetectionTuning.DefaultCorrelationDistanceGate,
    int EvaluationIntervalTicks = DetectionTuning.DefaultEvaluationIntervalTicks,
    float MaxCorrelationSpeed = DetectionTuning.DefaultMaxCorrelationSpeed,
    float Slack = DetectionTuning.DefaultSlack,
    float MaxGate = DetectionTuning.DefaultMaxGate);

public sealed record WeaponData(
    string Id,
    float Range,
    int ReloadDurationTicks = 0,
    float ReloadSeconds = 0f,
    int Damage = 0,
    int AmmoCapacity = 0,
    float MuzzleHeight = 1.8f,
    string? PresentationId = null,
    string? PresentationKind = null,
    float PenetrationNear = 0f,
    float PenetrationFar = 0f,
    float PenetrationNearRange = 0f,
    float PenetrationFarRange = 0f,
    int RoundsPerBurst = 1,
    AccuracyPoint[]? AccuracyCurve = null,
    float DispersionMils = 3.0f,
    float Dispersion = 0f,
    float TargetMovementPenalty = 0.20f,
    float RangingBonus = 0.08f,
    float MaxRangingBonus = 0.24f);

public sealed record UnitDefinition(
    string Id,
    float Speed,
    float SensorRange = 60.0f,
    float EyeHeight = 1.8f,
    float[]? TargetPoints = null,
    float Radius = 1.0f,
    int MaxHealth = 100,
    string? Weapon = null,
    string? Classification = null,
    // Historical types (section 17.1): display name, faction and period speeds in m/s.
    // A type gives speed_offroad instead of speed; units move at it until maps have roads.
    string? Name = null,
    string? Faction = null,
    float SpeedRoad = 0f,
    float SpeedOffroad = 0f,
    float ReverseSpeed = 0f,
    float WaterSpeed = 0f,
    float TurretTraverseRate = 0f,
    float ArmorFront = 0f,
    float ArmorSide = 0f,
    float ArmorRear = 0f,
    float FrontSectorAngle = 90f,
    float RearSectorAngle = 90f,
    TraversabilityCapabilities Capabilities = TraversabilityCapabilities.DefaultVehicle,
    float TurnRate = 0f,
    bool CanPivot = true,
    float MinTurnRadius = 0f,
    float Acceleration = 0f,
    float Deceleration = 0f);

public sealed record FactionData(string Id, string? Name = null);

public sealed record UnitCatalog(
    int SchemaVersion,
    UnitDefinition[] Units,
    DetectionSettings? Detection = null,
    WeaponData[]? Weapons = null,
    FactionData[]? Factions = null)
{
    public ulong ContentHash { get; init; }

    public bool HasFaction(string id)
    {
        if (Factions is null) return false;
        for (int i = 0; i < Factions.Length; i++)
        {
            if (string.Equals(Factions[i].Id, id, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public UnitDefinition RequireDefinition(string id)
    {
        for (int i = 0; i < Units.Length; i++)
        {
            if (string.Equals(Units[i].Id, id, StringComparison.Ordinal))
                return Units[i];
        }
        throw new KeyNotFoundException($"Unit definition '{id}' not found in catalog.");
    }

    public ushort RequireDefinitionIndex(string id)
    {
        for (int i = 0; i < Units.Length; i++)
        {
            if (string.Equals(Units[i].Id, id, StringComparison.Ordinal))
                return (ushort)i;
        }
        throw new KeyNotFoundException($"Unit definition '{id}' not found in catalog.");
    }

    // Full ammunition load of the unit's weapon, or 0 for an unarmed unit.
    public int GetAmmoCapacity(UnitDefinition unit)
    {
        if (unit.Weapon is null || Weapons is null) return 0;
        for (int i = 0; i < Weapons.Length; i++)
        {
            if (string.Equals(Weapons[i].Id, unit.Weapon, StringComparison.Ordinal))
                return Weapons[i].AmmoCapacity;
        }
        return 0;
    }

    public UnitDefinition? GetDefinition(string id)
    {
        for (int i = 0; i < Units.Length; i++)
        {
            if (string.Equals(Units[i].Id, id, StringComparison.Ordinal))
                return Units[i];
        }
        return null;
    }
}

public sealed class OpsPerceptionCatalog : IUnitPerceptionCatalog
{
    private readonly UnitPerceptionProfile[] _profiles;

    public OpsPerceptionCatalog(UnitCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _profiles = new UnitPerceptionProfile[catalog.Units.Length];
        for (int i = 0; i < catalog.Units.Length; i++)
        {
            UnitDefinition unit = catalog.Units[i];
            VisualSensorProfile? sensor = unit.SensorRange > 0 && unit.EyeHeight > 0
                ? new VisualSensorProfile(unit.SensorRange, unit.EyeHeight)
                : null;
            TargetObservationPoints? points = unit.TargetPoints is { Length: > 0 }
                ? new TargetObservationPoints(unit.TargetPoints)
                : null;
            ContactClassification classification = ParseClassification(unit.Classification, unit.Id);
            _profiles[i] = new UnitPerceptionProfile(sensor, points, classification);
        }
    }

    private static ContactClassification ParseClassification(string? classification, string id)
    {
        if (!string.IsNullOrWhiteSpace(classification))
        {
            return classification.ToLowerInvariant() switch
            {
                "command" or "command_vehicle" or "commandvehicle" => ContactClassification.CommandVehicle,
                "tank" => ContactClassification.Tank,
                "recon" or "recon_vehicle" => ContactClassification.Recon,
                "apc" => ContactClassification.Apc,
                "vehicle" => ContactClassification.Vehicle,
                _ => ContactClassification.Vehicle
            };
        }

        string lowerId = id.ToLowerInvariant();
        if (lowerId.Contains("cmd") || lowerId.Contains("command"))
            return ContactClassification.CommandVehicle;
        if (lowerId.Contains("tank"))
            return ContactClassification.Tank;
        if (lowerId.Contains("recon"))
            return ContactClassification.Recon;
        return ContactClassification.Vehicle;
    }

    public bool TryGetProfile(ushort definition, out UnitPerceptionProfile profile)
    {
        if (definition < _profiles.Length)
        {
            profile = _profiles[definition];
            return true;
        }
        profile = default;
        return false;
    }
}

public sealed class OpsMovementCatalog : IUnitMovementCatalog
{
    private readonly UnitMovementProfile[] _profiles;

    public OpsMovementCatalog(UnitCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _profiles = new UnitMovementProfile[catalog.Units.Length];
        for (int i = 0; i < catalog.Units.Length; i++)
        {
            UnitDefinition unit = catalog.Units[i];
            TraversabilityCapabilities caps = unit.Capabilities;
            if (caps == TraversabilityCapabilities.DefaultVehicle &&
                (string.Equals(unit.Classification, "infantry", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(unit.Classification, "foot", StringComparison.OrdinalIgnoreCase)))
            {
                caps = TraversabilityCapabilities.Foot;
            }
            float turnRate = unit.TurnRate > 0f ? unit.TurnRate : 45.0f;
            float revSpeed = unit.ReverseSpeed > 0f ? unit.ReverseSpeed : MathF.Max(1.0f, unit.Speed * 0.5f);
            float accel = unit.Acceleration > 0f ? unit.Acceleration : 3.0f;
            float decel = unit.Deceleration > 0f ? unit.Deceleration : 5.0f;
            bool canPivot = unit.CanPivot;
            float minTurnRadius = unit.MinTurnRadius;
            _profiles[i] = new UnitMovementProfile(unit.Speed, unit.Radius, caps, turnRate, revSpeed, accel, decel, canPivot, minTurnRadius);
        }
    }

    public bool TryGetProfile(ushort definition, out UnitMovementProfile profile)
    {
        if (definition < _profiles.Length)
        {
            profile = _profiles[definition];
            return true;
        }
        profile = default;
        return false;
    }
}

public sealed class OpsCombatCatalog : IUnitCombatCatalog
{
    private readonly UnitCombatProfile[] _profiles;

    public OpsCombatCatalog(UnitCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Dictionary<string, WeaponDefinition> weapons = new(StringComparer.Ordinal);
        if (catalog.Weapons is not null)
        {
            ushort presIdCounter = 0;
            foreach (WeaponData w in catalog.Weapons)
            {
                int reloadTicks = w.ReloadDurationTicks > 0
                    ? w.ReloadDurationTicks
                    : (int)MathF.Round(w.ReloadSeconds * Simulation.TickRate);

                WeaponPresentationKind kind = string.Equals(w.PresentationKind, "machine_gun", StringComparison.OrdinalIgnoreCase)
                    ? WeaponPresentationKind.MachineGun
                    : string.Equals(w.PresentationKind, "cannon", StringComparison.OrdinalIgnoreCase)
                        ? WeaponPresentationKind.Cannon
                        : throw new InvalidOperationException($"Unsupported presentation_kind '{w.PresentationKind}'.");

                ushort presId = WeaponPresentation.MakeId(kind, presIdCounter++);

                float penNear = w.PenetrationNear;
                float penFar = w.PenetrationFar > 0f ? w.PenetrationFar : w.PenetrationNear;
                float penNearRange = w.PenetrationNearRange;
                float penFarRange = w.PenetrationFarRange > 0f ? w.PenetrationFarRange : w.Range;
                int burst = Math.Max(1, w.RoundsPerBurst);

                float dispersion = w.DispersionMils > 0f ? w.DispersionMils : (w.Dispersion > 0f ? w.Dispersion : 3.0f);
                weapons[w.Id] = new WeaponDefinition(
                    w.Id,
                    w.Range,
                    reloadTicks,
                    w.Damage,
                    w.AmmoCapacity,
                    w.MuzzleHeight,
                    presId,
                    penNear,
                    penFar,
                    penNearRange,
                    penFarRange,
                    burst,
                    w.AccuracyCurve,
                    dispersion,
                    w.TargetMovementPenalty,
                    w.RangingBonus,
                    w.MaxRangingBonus);
            }
        }

        _profiles = new UnitCombatProfile[catalog.Units.Length];
        for (int i = 0; i < catalog.Units.Length; i++)
        {
            UnitDefinition unit = catalog.Units[i];
            WeaponDefinition? weapon = unit.Weapon is not null && weapons.TryGetValue(unit.Weapon, out WeaponDefinition? def)
                ? def
                : null;
            UnitArmor armor = new(
                unit.ArmorFront,
                unit.ArmorSide,
                unit.ArmorRear,
                unit.FrontSectorAngle > 0f ? unit.FrontSectorAngle : 90f,
                unit.RearSectorAngle > 0f ? unit.RearSectorAngle : 90f);
            _profiles[i] = new UnitCombatProfile(unit.MaxHealth, weapon, unit.TurretTraverseRate, armor, unit.Radius > 0f ? unit.Radius : 1.0f);
        }
    }

    public bool TryGetProfile(ushort definition, out UnitCombatProfile profile)
    {
        if (definition < _profiles.Length)
        {
            profile = _profiles[definition];
            return true;
        }
        profile = default;
        return false;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(UnitCatalog))]
[JsonSerializable(typeof(UnitDefinition))]
[JsonSerializable(typeof(DetectionSettings))]
[JsonSerializable(typeof(WeaponData))]
[JsonSerializable(typeof(WeaponData[]))]
[JsonSerializable(typeof(AccuracyPoint))]
[JsonSerializable(typeof(AccuracyPoint[]))]
[JsonSerializable(typeof(FactionData))]
[JsonSerializable(typeof(FactionData[]))]
[JsonSerializable(typeof(TraversabilityCapabilities))]
[JsonSerializable(typeof(float[]))]
internal partial class ContentJsonContext : JsonSerializerContext;

public static class PrototypeScenario
{
    public static DetectionTuning CreateDetectionTuning(UnitCatalog catalog)
    {
        if (catalog.Detection is null) return new DetectionTuning();
        return new DetectionTuning(
            catalog.Detection.MemoryDurationTicks,
            catalog.Detection.CorrelationWindowTicks,
            catalog.Detection.CorrelationDistanceGate,
            catalog.Detection.EvaluationIntervalTicks,
            catalog.Detection.MaxCorrelationSpeed,
            catalog.Detection.Slack,
            catalog.Detection.MaxGate);
    }

    public static UnitCatalog ValidateCatalog(UnitCatalog data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.SchemaVersion != 1 || data.Units is not { Length: > 0 })
            throw new InvalidOperationException("Unsupported or empty unit catalog.");

        if (data.Detection is not null)
        {
            if (data.Detection.MemoryDurationTicks <= 0 ||
                data.Detection.CorrelationWindowTicks <= 0 ||
                !float.IsFinite(data.Detection.CorrelationDistanceGate) ||
                data.Detection.CorrelationDistanceGate <= 0 ||
                data.Detection.EvaluationIntervalTicks <= 0 ||
                !float.IsFinite(data.Detection.MaxCorrelationSpeed) ||
                data.Detection.MaxCorrelationSpeed <= 0 ||
                !float.IsFinite(data.Detection.Slack) ||
                data.Detection.Slack < 0 ||
                !float.IsFinite(data.Detection.MaxGate) ||
                data.Detection.MaxGate <= 0)
            {
                throw new InvalidOperationException("Invalid detection tuning parameters.");
            }
        }

        if (data.Factions is not null)
        {
            HashSet<string> factionNames = new(StringComparer.Ordinal);
            foreach (FactionData faction in data.Factions)
            {
                if (string.IsNullOrWhiteSpace(faction.Id) || !factionNames.Add(faction.Id))
                    throw new InvalidOperationException("Invalid faction definition ID.");
            }
        }

        HashSet<string> weaponNames = new(StringComparer.Ordinal);
        if (data.Weapons is not null)
        {
            if (data.Weapons.Length > 255)
                throw new InvalidOperationException("Weapon catalog exceeds maximum of 255 weapons.");

            foreach (WeaponData weapon in data.Weapons)
            {
                if (string.IsNullOrWhiteSpace(weapon.Id) || !weaponNames.Add(weapon.Id))
                    throw new InvalidOperationException("Invalid weapon definition ID.");
                if (string.IsNullOrWhiteSpace(weapon.PresentationKind))
                    throw new InvalidOperationException("Missing required weapon presentation_kind.");
                if (!string.Equals(weapon.PresentationKind, "cannon", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(weapon.PresentationKind, "machine_gun", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Unknown weapon presentation_kind '{weapon.PresentationKind}'.");
                }
                if (!float.IsFinite(weapon.Range) || weapon.Range <= 0)
                    throw new InvalidOperationException("Invalid weapon range.");
                if (weapon.ReloadDurationTicks < 0 || weapon.ReloadDurationTicks > ushort.MaxValue)
                    throw new InvalidOperationException("Invalid reload duration.");
                if (!float.IsFinite(weapon.ReloadSeconds) || weapon.ReloadSeconds < 0)
                    throw new InvalidOperationException("Invalid reload duration.");
                int computedReloadTicks = weapon.ReloadDurationTicks > 0
                    ? weapon.ReloadDurationTicks
                    : (int)Math.Round(weapon.ReloadSeconds / Simulation.TickSeconds);
                if (computedReloadTicks < 0 || computedReloadTicks > ushort.MaxValue)
                    throw new InvalidOperationException("Computed reload duration ticks exceeds protocol field capacity.");
                if (weapon.Damage <= 0)
                    throw new InvalidOperationException("Invalid weapon damage.");
                if (weapon.AmmoCapacity <= 0 || weapon.AmmoCapacity > ushort.MaxValue)
                    throw new InvalidOperationException("Invalid ammo capacity.");
                if (!float.IsFinite(weapon.MuzzleHeight) || weapon.MuzzleHeight <= 0)
                    throw new InvalidOperationException("Invalid muzzle height.");
                if (!float.IsFinite(weapon.PenetrationNear) || weapon.PenetrationNear <= 0)
                    throw new InvalidOperationException("Invalid weapon penetration.");
                if (!float.IsFinite(weapon.PenetrationFar) || weapon.PenetrationFar <= 0)
                    throw new InvalidOperationException("Invalid weapon penetration.");
                if (!float.IsFinite(weapon.PenetrationNearRange) || weapon.PenetrationNearRange < 0)
                    throw new InvalidOperationException("Invalid weapon penetration range.");
                if (!float.IsFinite(weapon.PenetrationFarRange) || weapon.PenetrationFarRange < 0)
                    throw new InvalidOperationException("Invalid weapon penetration range.");
                if (weapon.RoundsPerBurst < 1 || weapon.RoundsPerBurst > ushort.MaxValue)
                    throw new InvalidOperationException("Invalid weapon rounds per burst.");
                if (weapon.AccuracyCurve is not null)
                {
                    float prevRange = -1f;
                    foreach (AccuracyPoint pt in weapon.AccuracyCurve)
                    {
                        if (!float.IsFinite(pt.Range) || pt.Range < 0)
                            throw new InvalidOperationException("Invalid accuracy curve range.");
                        if (!float.IsFinite(pt.HitProbability) || pt.HitProbability < 0f || pt.HitProbability > 1f)
                            throw new InvalidOperationException("Invalid accuracy curve hit probability.");
                        if (pt.Range < prevRange)
                            throw new InvalidOperationException("Accuracy curve ranges must be monotonically non-decreasing.");
                        prevRange = pt.Range;
                    }
                }
                float disp = weapon.DispersionMils > 0f ? weapon.DispersionMils : weapon.Dispersion;
                if (!float.IsFinite(disp) || disp < 0)
                    throw new InvalidOperationException("Invalid weapon dispersion.");
                if (!float.IsFinite(weapon.TargetMovementPenalty) || weapon.TargetMovementPenalty < 0f || weapon.TargetMovementPenalty > 1f)
                    throw new InvalidOperationException("Invalid target movement penalty.");
                if (!float.IsFinite(weapon.RangingBonus) || weapon.RangingBonus < 0f || weapon.RangingBonus > 1f)
                    throw new InvalidOperationException("Invalid ranging bonus.");
                if (!float.IsFinite(weapon.MaxRangingBonus) || weapon.MaxRangingBonus < 0f || weapon.MaxRangingBonus > 1f)
                    throw new InvalidOperationException("Invalid max ranging bonus.");
            }
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (UnitDefinition unit in data.Units)
        {
            if (string.IsNullOrWhiteSpace(unit.Id) || !names.Add(unit.Id))
                throw new InvalidOperationException("Invalid unit definition ID.");
            if (!float.IsFinite(unit.Speed) || unit.Speed <= 0)
                throw new InvalidOperationException("Invalid unit speed.");
            if (!float.IsFinite(unit.Radius) || unit.Radius <= 0)
                throw new InvalidOperationException("Invalid unit radius.");
            if (!float.IsFinite(unit.SensorRange) || unit.SensorRange <= 0)
                throw new InvalidOperationException("Invalid sensor range.");
            if (!float.IsFinite(unit.EyeHeight) || unit.EyeHeight <= 0)
                throw new InvalidOperationException("Invalid eye height.");
            if (unit.MaxHealth <= 0 || unit.MaxHealth > ushort.MaxValue)
                throw new InvalidOperationException("Invalid unit max health.");
            if (unit.Weapon is not null && !weaponNames.Contains(unit.Weapon))
                throw new InvalidOperationException("Unknown weapon referenced by unit.");
            if (unit.TargetPoints is null or { Length: < 1 or > 8 })
                throw new InvalidOperationException("Invalid target points count.");
            foreach (float height in unit.TargetPoints)
            {
                if (!float.IsFinite(height) || height < 0)
                    throw new InvalidOperationException("Invalid target observation height.");
            }
            if (unit.Name is not null && string.IsNullOrWhiteSpace(unit.Name))
                throw new InvalidOperationException($"Unit '{unit.Id}' has a blank name.");
            if (unit.Faction is not null && !data.HasFaction(unit.Faction))
                throw new InvalidOperationException($"Unit '{unit.Id}' names unknown faction '{unit.Faction}'.");
            if (!float.IsFinite(unit.SpeedRoad) || !float.IsFinite(unit.SpeedOffroad) ||
                !float.IsFinite(unit.ReverseSpeed) || !float.IsFinite(unit.WaterSpeed) ||
                unit.SpeedRoad < 0 || unit.SpeedOffroad < 0 || unit.ReverseSpeed < 0 || unit.WaterSpeed < 0)
                throw new InvalidOperationException($"Unit '{unit.Id}' has an invalid period speed.");
            if (unit.SpeedOffroad > 0 && unit.SpeedRoad == 0)
                throw new InvalidOperationException($"Unit '{unit.Id}' gives speed_offroad but no speed_road.");
            if (unit.SpeedOffroad > 0 && unit.SpeedRoad < unit.SpeedOffroad)
                throw new InvalidOperationException($"Unit '{unit.Id}' is faster off-road than on the road.");
            if (unit.ReverseSpeed > 0 && unit.ReverseSpeed >= Math.Max(unit.SpeedRoad, unit.Speed))
                throw new InvalidOperationException($"Unit '{unit.Id}' reverses as fast as it drives forward.");
            if (!float.IsFinite(unit.TurretTraverseRate) || unit.TurretTraverseRate < 0)
                throw new InvalidOperationException($"Unit '{unit.Id}' has an invalid turret traverse rate.");
            if (!float.IsFinite(unit.TurnRate) || unit.TurnRate < 0)
                throw new InvalidOperationException($"Unit '{unit.Id}' has an invalid turn rate.");
            if (!float.IsFinite(unit.Acceleration) || unit.Acceleration < 0)
                throw new InvalidOperationException($"Unit '{unit.Id}' has an invalid acceleration.");
            if (!float.IsFinite(unit.Deceleration) || unit.Deceleration < 0)
                throw new InvalidOperationException($"Unit '{unit.Id}' has an invalid deceleration.");
            if (!float.IsFinite(unit.MinTurnRadius) || unit.MinTurnRadius < 0)
                throw new InvalidOperationException($"Unit '{unit.Id}' has an invalid minimum turn radius.");
            if (!unit.CanPivot && unit.MinTurnRadius <= 0)
                throw new InvalidOperationException($"Unit '{unit.Id}' cannot pivot in place but specifies no positive minimum turn radius.");
            if (!float.IsFinite(unit.ArmorFront) || unit.ArmorFront < 0 ||
                !float.IsFinite(unit.ArmorSide) || unit.ArmorSide < 0 ||
                !float.IsFinite(unit.ArmorRear) || unit.ArmorRear < 0)
            {
                throw new InvalidOperationException($"Unit '{unit.Id}' has an invalid armor value.");
            }
            if (!float.IsFinite(unit.FrontSectorAngle) || unit.FrontSectorAngle < 0 || unit.FrontSectorAngle > 360f ||
                !float.IsFinite(unit.RearSectorAngle) || unit.RearSectorAngle < 0 || unit.RearSectorAngle > 360f)
            {
                throw new InvalidOperationException($"Unit '{unit.Id}' has an invalid armor sector angle.");
            }
        }
        return data;
    }

    public static UnitCatalog LoadCatalog(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream ms = new();
        stream.CopyTo(ms);
        return ParseCatalog(ms.ToArray());
    }

    public static UnitCatalog LoadCatalog(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return ParseCatalog(System.Text.Encoding.UTF8.GetBytes(json));
    }

    private static UnitCatalog ParseCatalog(byte[] bytes)
    {
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= 1099511628211UL;
        }
        UnitCatalog data = JsonSerializer.Deserialize(bytes, ContentJsonContext.Default.UnitCatalog)
            ?? throw new InvalidOperationException("Unit data is empty.");
        data = data with { ContentHash = hash };
        return ValidateCatalog(ApplyOffroadSpeed(data));
    }

    // A historical type gives speed_offroad and no speed; it moves at its off-road speed.
    private static UnitCatalog ApplyOffroadSpeed(UnitCatalog data)
    {
        if (data.Units is null) return data;
        UnitDefinition[] units = (UnitDefinition[])data.Units.Clone();
        for (int i = 0; i < units.Length; i++)
        {
            UnitDefinition unit = units[i];
            if (unit is null || unit.SpeedOffroad == 0f) continue;
            if (unit.Speed != 0f)
                throw new InvalidOperationException($"Unit '{unit.Id}' gives both speed and speed_offroad.");
            units[i] = unit with { Speed = unit.SpeedOffroad };
        }
        return data with { Units = units };
    }

    public static UnitCatalog LoadCatalog()
    {
        using Stream stream = typeof(PrototypeScenario).Assembly.GetManifestResourceStream("DeadGround.Units.json")
            ?? throw new InvalidOperationException("Unit data not found.");
        return LoadCatalog(stream);
    }

    public static Simulation Create(HeightMap map)
    {
        UnitCatalog data = LoadCatalog();
        WorldState world = new(map);
        UnitDefinition vehicleDef = data.RequireDefinition("prototype_vehicle");
        ushort vehicleIdx = data.RequireDefinitionIndex("prototype_vehicle");
        for (int z = 0; z < 3; z++)
            for (int x = 0; x < 4; x++)
                world.CreateUnit(1, vehicleIdx, new Vector2(24 + x * 9, 24 + z * 9), vehicleDef.Speed);
        for (int i = 0; i < 4; i++)
            world.CreateUnit(2, vehicleIdx, new Vector2(90 + i * 6, 90), vehicleDef.Speed);
        return new Simulation(world, new OpsRules());
    }

    public static ulong ComputeCatalogHash() => LoadCatalog().ContentHash;

    // Two-player scenarios: player 1 fields Soviet forces, player 2 US forces.
    private static readonly string[] DefaultFactions = ["soviet", "us"];

    public static IReadOnlyList<string> DefaultPlayerFactionIds => DefaultFactions;

    // Per-player faction IDs for MatchSetup, or null for catalogs that define no factions.
    public static string[]? CreatePlayerFactions(UnitCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Factions is null) return null;
        foreach (string id in DefaultFactions)
        {
            if (!catalog.HasFaction(id))
                throw new InvalidOperationException($"Faction '{id}' is not defined in the unit catalog.");
        }
        return (string[])DefaultFactions.Clone();
    }

    public static MatchSetup CreateMatchSetup(HeightMap map, string ruleSetId = "Ops", UnitCatalog? catalog = null)
    {
        catalog ??= LoadCatalog();
        return new MatchSetup(
            MatchSetup.CurrentProtocolVersion,
            catalog.ContentHash,
            MatchSetup.ComputeMapHash(map),
            ruleSetId,
            PlayerFactions: PrototypeScenario.CreatePlayerFactions(catalog));
    }
}

public static class VisibilityScenario
{
    public static HeightMap CreateMap()
    {
        const int size = 65;
        ushort[] heights = new ushort[size * size];
        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                // Smooth hill centered at (80, 80) with 25m radius, peak height 8m
                float dx = x * 2 - 80f;
                float dz = z * 2 - 80f;
                float d2 = dx * dx + dz * dz;
                float h = MathF.Max(0, 1f - d2 / (25f * 25f)) * 8f;
                heights[z * size + x] = (ushort)MathF.Round(h * 100f);
            }
        }
        return new HeightMap(size, size, 2f, 0.01f, heights);
    }

    public static StaticGeometry CreateStaticGeometry()
    {
        HeightMap map = CreateMap();

        // 1. Opaque building at (45..55, 40..50), height 6.0m
        OpaqueStructure building = new(
            new StructureId(1),
            minX: 45f, minY: 0f, minZ: 40f,
            maxX: 55f, maxY: 6f, maxZ: 50f,
            isGrounded: true);

        // 2. Low wall at (30..31, 20..35), height 1.0m (partial hull-down cover)
        OpaqueStructure wall = new(
            new StructureId(2),
            minX: 30f, minY: 0f, minZ: 20f,
            maxX: 31f, maxY: 1.0f, maxZ: 35f,
            isGrounded: true);

        return new StaticGeometry(map, [building, wall]);
    }

    public static Simulation CreateSimulation(StaticGeometry? geometry = null, UnitCatalog? catalog = null, int? evaluationIntervalTicks = null)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= PrototypeScenario.LoadCatalog();

        DetectionTuning tuning = evaluationIntervalTicks.HasValue
            ? new DetectionTuning(
                catalog.Detection?.MemoryDurationTicks ?? DetectionTuning.DefaultMemoryDurationTicks,
                catalog.Detection?.CorrelationWindowTicks ?? DetectionTuning.DefaultCorrelationWindowTicks,
                catalog.Detection?.CorrelationDistanceGate ?? DetectionTuning.DefaultCorrelationDistanceGate,
                evaluationIntervalTicks.Value,
                catalog.Detection?.MaxCorrelationSpeed ?? DetectionTuning.DefaultMaxCorrelationSpeed,
                catalog.Detection?.Slack ?? DetectionTuning.DefaultSlack,
                catalog.Detection?.MaxGate ?? DetectionTuning.DefaultMaxGate)
            : PrototypeScenario.CreateDetectionTuning(catalog);

        OpsRules rules = new(geometry, new OpsPerceptionCatalog(catalog), tuning);
        WorldState world = new(geometry.HeightMap);

        UnitDefinition reconDef = catalog.RequireDefinition("recon_vehicle");
        ushort reconIdx = catalog.RequireDefinitionIndex("recon_vehicle");
        UnitDefinition tankDef = catalog.RequireDefinition("prototype_tank");
        ushort tankIdx = catalog.RequireDefinitionIndex("prototype_tank");

        // Side 1: 2 friendly observers
        world.CreateUnit(1, reconIdx, new Vector2(20f, 25f), reconDef.Speed); // Observer 1 (Recon)
        world.CreateUnit(1, reconIdx, new Vector2(20f, 45f), reconDef.Speed); // Observer 2 (Recon)

        // Side 2: 3 enemy units
        world.CreateUnit(2, tankIdx, new Vector2(33f, 27f), tankDef.Speed); // Enemy 1 (Tank behind low wall)
        world.CreateUnit(2, tankIdx, new Vector2(60f, 45f), tankDef.Speed); // Enemy 2 (Tank behind building)
        world.CreateUnit(2, reconIdx, new Vector2(95f, 95f), reconDef.Speed); // Enemy 3 (Recon behind hill)

        return new Simulation(world, rules);
    }

    public static MatchSetup CreateMatchSetup(StaticGeometry? geometry = null, string ruleSetId = "Visibility", UnitCatalog? catalog = null)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= PrototypeScenario.LoadCatalog();
        return new MatchSetup(
            MatchSetup.CurrentProtocolVersion,
            catalog.ContentHash,
            MatchSetup.ComputeMapHash(geometry),
            ruleSetId,
            PlayerFactions: PrototypeScenario.CreatePlayerFactions(catalog));
    }
}

public static class NavigationScenario
{
    public static HeightMap CreateMap()
    {
        const int size = 65;
        ushort[] heights = new ushort[size * size];
        return new HeightMap(size, size, 2f, 0.01f, heights);
    }

    public static StaticGeometry CreateStaticGeometry()
    {
        HeightMap map = CreateMap();

        // 1. Standalone building at (30..44, 30..44), height 6.0m
        OpaqueStructure building = new(
            new StructureId(1),
            minX: 30f, minY: 0f, minZ: 30f,
            maxX: 44f, maxY: 6f, maxZ: 44f,
            isGrounded: true);

        // 2. Wall with bypass at (58..60, 10..40), height 3.0m
        OpaqueStructure wallWithBypass = new(
            new StructureId(2),
            minX: 58f, minY: 0f, minZ: 10f,
            maxX: 60f, maxY: 3f, maxZ: 40f,
            isGrounded: true);

        // 3. L-shaped obstacle: two contiguous perpendicular walls
        OpaqueStructure lWall1 = new(
            new StructureId(3),
            minX: 75f, minY: 0f, minZ: 25f,
            maxX: 77f, maxY: 3f, maxZ: 50f,
            isGrounded: true);

        OpaqueStructure lWall2 = new(
            new StructureId(4),
            minX: 75f, minY: 0f, minZ: 48f,
            maxX: 95f, maxY: 3f, maxZ: 50f,
            isGrounded: true);

        // 4. Narrow passage: 2.2m gap between X=55 and X=57.2
        // Passable for recon vehicle (radius 0.8m, clearance 1.6m), blocked for tank (radius 1.4m, clearance 2.8m)
        OpaqueStructure narrowWest = new(
            new StructureId(5),
            minX: 40f, minY: 0f, minZ: 70f,
            maxX: 55f, maxY: 3f, maxZ: 85f,
            isGrounded: true);

        OpaqueStructure narrowEast = new(
            new StructureId(6),
            minX: 57.2f, minY: 0f, minZ: 70f,
            maxX: 72.2f, maxY: 3f, maxZ: 85f,
            isGrounded: true);

        // 5. Fully enclosed courtyard: 4 walls enclosing interior [92..108, 77..95]
        OpaqueStructure courtSouth = new(
            new StructureId(7),
            minX: 90f, minY: 0f, minZ: 75f,
            maxX: 110f, maxY: 3f, maxZ: 77f,
            isGrounded: true);

        OpaqueStructure courtNorth = new(
            new StructureId(8),
            minX: 90f, minY: 0f, minZ: 95f,
            maxX: 110f, maxY: 3f, maxZ: 97f,
            isGrounded: true);

        OpaqueStructure courtWest = new(
            new StructureId(9),
            minX: 90f, minY: 0f, minZ: 77f,
            maxX: 92f, maxY: 3f, maxZ: 95f,
            isGrounded: true);

        OpaqueStructure courtEast = new(
            new StructureId(10),
            minX: 108f, minY: 0f, minZ: 77f,
            maxX: 110f, maxY: 3f, maxZ: 95f,
            isGrounded: true);

        return new StaticGeometry(map,
        [
            building,
            wallWithBypass,
            lWall1,
            lWall2,
            narrowWest,
            narrowEast,
            courtSouth,
            courtNorth,
            courtWest,
            courtEast
        ]);
    }

    public static Simulation CreateSimulation(StaticGeometry? geometry = null, UnitCatalog? catalog = null, NavigationTuning? navTuning = null)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= PrototypeScenario.LoadCatalog();

        OpsRules rules = new(geometry, catalog, navTuning);
        WorldState world = new(geometry.HeightMap);

        UnitDefinition reconDef = catalog.RequireDefinition("recon_vehicle");
        ushort reconIdx = catalog.RequireDefinitionIndex("recon_vehicle");
        UnitDefinition tankDef = catalog.RequireDefinition("prototype_tank");
        ushort tankIdx = catalog.RequireDefinitionIndex("prototype_tank");
        UnitDefinition vehicleDef = catalog.RequireDefinition("prototype_vehicle");
        ushort vehicleIdx = catalog.RequireDefinitionIndex("prototype_vehicle");

        // Side 1: friendly units in open area
        world.CreateUnit(1, reconIdx, new Vector2(15f, 20f), reconDef.Speed);
        world.CreateUnit(1, tankIdx, new Vector2(18f, 20f), tankDef.Speed);
        world.CreateUnit(1, vehicleIdx, new Vector2(21f, 20f), vehicleDef.Speed);

        // Side 2: other units
        world.CreateUnit(2, tankIdx, new Vector2(65f, 30f), tankDef.Speed);
        world.CreateUnit(2, reconIdx, new Vector2(80f, 80f), reconDef.Speed);

        return new Simulation(world, rules);
    }

    public static MatchSetup CreateMatchSetup(StaticGeometry? geometry = null, string ruleSetId = "Navigation", UnitCatalog? catalog = null)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= PrototypeScenario.LoadCatalog();
        return new MatchSetup(
            MatchSetup.CurrentProtocolVersion,
            catalog.ContentHash,
            MatchSetup.ComputeMapHash(geometry),
            ruleSetId,
            PlayerFactions: PrototypeScenario.CreatePlayerFactions(catalog));
    }
}

public static class LargeNavigationScenario
{
    public const int MapResolution = 1001; // 1001 x 1001 samples at 2.0m spacing = 2000m x 2000m
    public const float MapSpacing = 2.0f;

    public static HeightMap CreateMap()
    {
        ushort[] heights = new ushort[MapResolution * MapResolution];
        return new HeightMap(MapResolution, MapResolution, MapSpacing, 0.01f, heights);
    }

    public static StaticGeometry CreateStaticGeometry()
    {
        HeightMap map = CreateMap();
        List<OpaqueStructure> structures = new(32);
        int structId = 1;

        // 1. Urban blocks around [500..800, 500..800]
        for (int r = 0; r < 3; r++)
        {
            float z = 520f + r * 90f;
            for (int c = 0; c < 3; c++)
            {
                float x = 520f + c * 90f;
                structures.Add(new OpaqueStructure(
                    new StructureId(structId++),
                    minX: x, minY: 0f, minZ: z,
                    maxX: x + 50f, maxY: 6f, maxZ: z + 50f,
                    isGrounded: true));
            }
        }

        // 2. Long 500m barrier forcing large detour: [950..956, 300..800]
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 950f, minY: 0f, minZ: 300f,
            maxX: 956f, maxY: 4f, maxZ: 800f,
            isGrounded: true));

        // 3. Narrow 2.2m passage at X=1200, Z=500..540
        // West building: [1150..1200, 500..540]
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 1150f, minY: 0f, minZ: 500f,
            maxX: 1200f, maxY: 4f, maxZ: 540f,
            isGrounded: true));

        // East building: [1202.2..1252.2, 500..540]
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 1202.2f, minY: 0f, minZ: 500f,
            maxX: 1252.2f, maxY: 4f, maxZ: 540f,
            isGrounded: true));

        // 4. Enclosed unreachable courtyard: [1400..1480, 1400..1480]
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 1400f, minY: 0f, minZ: 1400f,
            maxX: 1480f, maxY: 4f, maxZ: 1404f,
            isGrounded: true));
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 1400f, minY: 0f, minZ: 1476f,
            maxX: 1480f, maxY: 4f, maxZ: 1480f,
            isGrounded: true));
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 1400f, minY: 0f, minZ: 1404f,
            maxX: 1404f, maxY: 4f, maxZ: 1476f,
            isGrounded: true));
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 1476f, minY: 0f, minZ: 1404f,
            maxX: 1480f, maxY: 4f, maxZ: 1476f,
            isGrounded: true));

        // 5. Winding slalom route requiring > 64 waypoints at [1600..1900, 200..700]
        // 8 alternating barrier walls
        for (int i = 0; i < 8; i++)
        {
            float z = 250f + i * 55f;
            float minX = (i % 2 == 0) ? 1600f : 1660f;
            float maxX = (i % 2 == 0) ? 1840f : 1900f;
            structures.Add(new OpaqueStructure(
                new StructureId(structId++),
                minX: minX, minY: 0f, minZ: z,
                maxX: maxX, maxY: 3f, maxZ: z + 4f,
                isGrounded: true));
        }

        return new StaticGeometry(map, structures.ToArray());
    }

    public static Simulation CreateSimulation(StaticGeometry? geometry = null, UnitCatalog? catalog = null, NavigationTuning? navTuning = null)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= PrototypeScenario.LoadCatalog();

        OpsRules rules = new(geometry, catalog, navTuning);
        WorldState world = new(geometry.HeightMap);

        UnitDefinition reconDef = catalog.RequireDefinition("recon_vehicle");
        ushort reconIdx = catalog.RequireDefinitionIndex("recon_vehicle");
        UnitDefinition tankDef = catalog.RequireDefinition("prototype_tank");
        ushort tankIdx = catalog.RequireDefinitionIndex("prototype_tank");

        // Side 1: base in open area [200, 200]
        world.CreateUnit(1, reconIdx, new Vector2(200f, 200f), reconDef.Speed);
        world.CreateUnit(1, tankIdx, new Vector2(215f, 200f), tankDef.Speed);
        world.CreateUnit(1, tankIdx, new Vector2(230f, 200f), tankDef.Speed);

        // Side 2: base in eastern area [1800, 1800]
        world.CreateUnit(2, reconIdx, new Vector2(1800f, 1800f), reconDef.Speed);
        world.CreateUnit(2, tankIdx, new Vector2(1815f, 1800f), tankDef.Speed);
        world.CreateUnit(2, tankIdx, new Vector2(1830f, 1800f), tankDef.Speed);

        return new Simulation(world, rules);
    }

    public static MatchSetup CreateMatchSetup(StaticGeometry? geometry = null, string ruleSetId = "LargeNavigation", UnitCatalog? catalog = null)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= PrototypeScenario.LoadCatalog();
        return new MatchSetup(
            MatchSetup.CurrentProtocolVersion,
            catalog.ContentHash,
            MatchSetup.ComputeMapHash(geometry),
            ruleSetId,
            PlayerFactions: PrototypeScenario.CreatePlayerFactions(catalog));
    }
}


public static class CombatScenario
{
    public static HeightMap CreateMap()
    {
        const int size = 65;
        ushort[] heights = new ushort[size * size];
        return new HeightMap(size, size, 2f, 0.01f, heights);
    }

    public static StaticGeometry CreateStaticGeometry()
    {
        HeightMap map = CreateMap();

        // 1. High building at (45..55, 42..58), height 6.0m (blocks fire and line of sight)
        OpaqueStructure building = new(
            new StructureId(1),
            minX: 45f, minY: 0f, minZ: 42f,
            maxX: 55f, maxY: 6f, maxZ: 58f,
            isGrounded: true);

        // 2. Low wall at (30..31, 15..35), height 1.0m (partial hull-down cover)
        OpaqueStructure lowWall = new(
            new StructureId(2),
            minX: 30f, minY: 0f, minZ: 15f,
            maxX: 31f, maxY: 1.0f, maxZ: 35f,
            isGrounded: true);

        return new StaticGeometry(map, [building, lowWall]);
    }

    public static Simulation CreateSimulation(StaticGeometry? geometry = null, UnitCatalog? catalog = null, NavigationTuning? navTuning = null, ulong matchSeed = 2024UL)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= PrototypeScenario.LoadCatalog();

        OpsRules rules = new(geometry, catalog, navTuning);
        WorldState world = new(geometry.HeightMap);

        UnitDefinition reconDef = catalog.RequireDefinition("recon_vehicle");
        ushort reconIdx = catalog.RequireDefinitionIndex("recon_vehicle");
        UnitDefinition tankDef = catalog.RequireDefinition("prototype_tank");
        ushort tankIdx = catalog.RequireDefinitionIndex("prototype_tank");
        UnitDefinition apcDef = catalog.RequireDefinition("apc");
        ushort apcIdx = catalog.RequireDefinitionIndex("apc");

        int tankAmmo = catalog.GetAmmoCapacity(tankDef);

        float side1Yaw = -MathF.PI / 2f;
        float side2Yaw = MathF.PI / 2f;

        // Side 1: 2 tanks and 1 recon vehicle
        world.CreateUnit(1, reconIdx, new Vector2(15f, 25f), reconDef.Speed, reconDef.MaxHealth, 0, side1Yaw);
        world.CreateUnit(1, tankIdx, new Vector2(12f, 25f), tankDef.Speed, tankDef.MaxHealth, tankAmmo, side1Yaw);
        world.CreateUnit(1, tankIdx, new Vector2(15f, 50f), tankDef.Speed, tankDef.MaxHealth, tankAmmo, side1Yaw);

        // Side 2: 2 tanks and 1 recon vehicle
        world.CreateUnit(2, tankIdx, new Vector2(33f, 25f), tankDef.Speed, tankDef.MaxHealth, tankAmmo, side2Yaw); // behind low wall
        world.CreateUnit(2, tankIdx, new Vector2(65f, 50f), tankDef.Speed, tankDef.MaxHealth, tankAmmo, side2Yaw); // behind building
        world.CreateUnit(2, reconIdx, new Vector2(65f, 80f), reconDef.Speed, reconDef.MaxHealth, 0, side2Yaw);

        // One unarmed APC per side, created last so earlier entity IDs stay the same.
        world.CreateUnit(1, apcIdx, new Vector2(8f, 38f), apcDef.Speed, apcDef.MaxHealth, 0, side1Yaw);
        world.CreateUnit(2, apcIdx, new Vector2(75f, 65f), apcDef.Speed, apcDef.MaxHealth, 0, side2Yaw);

        return new Simulation(world, rules, matchSeed);
    }

    public static MatchSetup CreateMatchSetup(StaticGeometry? geometry = null, string ruleSetId = "Combat", UnitCatalog? catalog = null)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= PrototypeScenario.LoadCatalog();
        return new MatchSetup(
            MatchSetup.CurrentProtocolVersion,
            catalog.ContentHash,
            MatchSetup.ComputeMapHash(geometry),
            ruleSetId,
            PlayerFactions: PrototypeScenario.CreatePlayerFactions(catalog));
    }
}
