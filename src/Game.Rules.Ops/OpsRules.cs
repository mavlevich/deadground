using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

public sealed class OpsRules : RuleSet
{
    public OpsRules() : base(new MovementSystem()) { }

    public OpsRules(StaticGeometry geometry, IUnitPerceptionCatalog catalog, DetectionTuning? tuning = null)
        : base(new MovementSystem(), new DetectionSystem(geometry, catalog, tuning)) { }

    public OpsRules(StaticGeometry geometry, UnitCatalog catalog)
        : this(geometry, new OpsPerceptionCatalog(catalog), PrototypeScenario.CreateDetectionTuning(catalog)) { }
}

public sealed record DetectionSettings(
    int MemoryDurationTicks = DetectionTuning.DefaultMemoryDurationTicks,
    int CorrelationWindowTicks = DetectionTuning.DefaultCorrelationWindowTicks,
    float CorrelationDistanceGate = DetectionTuning.DefaultCorrelationDistanceGate,
    int EvaluationIntervalTicks = DetectionTuning.DefaultEvaluationIntervalTicks,
    float MaxCorrelationSpeed = DetectionTuning.DefaultMaxCorrelationSpeed,
    float Slack = DetectionTuning.DefaultSlack,
    float MaxGate = DetectionTuning.DefaultMaxGate);

public sealed record UnitDefinition(
    string Id,
    float Speed,
    float SensorRange = 60.0f,
    float EyeHeight = 1.8f,
    float[]? TargetPoints = null);

public sealed record UnitCatalog(
    int SchemaVersion,
    UnitDefinition[] Units,
    DetectionSettings? Detection = null)
{
    public ulong ContentHash { get; init; }

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
            _profiles[i] = new UnitPerceptionProfile(sensor, points);
        }
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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(UnitCatalog))]
[JsonSerializable(typeof(UnitDefinition))]
[JsonSerializable(typeof(DetectionSettings))]
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

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (UnitDefinition unit in data.Units)
        {
            if (string.IsNullOrWhiteSpace(unit.Id) || !names.Add(unit.Id))
                throw new InvalidOperationException("Invalid unit definition ID.");
            if (!float.IsFinite(unit.Speed) || unit.Speed <= 0)
                throw new InvalidOperationException("Invalid unit speed.");
            if (!float.IsFinite(unit.SensorRange) || unit.SensorRange <= 0)
                throw new InvalidOperationException("Invalid sensor range.");
            if (!float.IsFinite(unit.EyeHeight) || unit.EyeHeight <= 0)
                throw new InvalidOperationException("Invalid eye height.");
            if (unit.TargetPoints is null or { Length: < 1 or > 8 })
                throw new InvalidOperationException("Invalid target points count.");
            foreach (float height in unit.TargetPoints)
            {
                if (!float.IsFinite(height) || height < 0)
                    throw new InvalidOperationException("Invalid target observation height.");
            }
        }
        return data;
    }

    public static UnitCatalog LoadCatalog(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream ms = new();
        stream.CopyTo(ms);
        byte[] bytes = ms.ToArray();
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= 1099511628211UL;
        }
        UnitCatalog data = JsonSerializer.Deserialize(bytes, ContentJsonContext.Default.UnitCatalog)
            ?? throw new InvalidOperationException("Unit data is empty.");
        data = data with { ContentHash = hash };
        return ValidateCatalog(data);
    }

    public static UnitCatalog LoadCatalog(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= 1099511628211UL;
        }
        UnitCatalog data = JsonSerializer.Deserialize(bytes, ContentJsonContext.Default.UnitCatalog)
            ?? throw new InvalidOperationException("Unit data is empty.");
        data = data with { ContentHash = hash };
        return ValidateCatalog(data);
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

    public static MatchSetup CreateMatchSetup(HeightMap map, string ruleSetId = "Ops", UnitCatalog? catalog = null)
    {
        catalog ??= LoadCatalog();
        return new MatchSetup(
            MatchSetup.CurrentProtocolVersion,
            catalog.ContentHash,
            MatchSetup.ComputeMapHash(map),
            ruleSetId);
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
            ruleSetId);
    }
}
