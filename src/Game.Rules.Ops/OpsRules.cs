using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

public sealed class OpsRules : RuleSet
{
    public OpsRules() : base(new MovementSystem()) { }
}

public sealed record UnitDefinition(string Id, float Speed);
public sealed record UnitCatalog(int SchemaVersion, UnitDefinition[] Units);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(UnitCatalog))]
internal partial class ContentJsonContext : JsonSerializerContext;

public static class PrototypeScenario
{
    public static UnitCatalog LoadCatalog()
    {
        using Stream stream = typeof(PrototypeScenario).Assembly.GetManifestResourceStream("DeadGround.Units.json")
            ?? throw new InvalidOperationException("Unit data not found.");
        UnitCatalog data = JsonSerializer.Deserialize(stream, ContentJsonContext.Default.UnitCatalog)
            ?? throw new InvalidOperationException("Unit data is empty.");
        if (data.SchemaVersion != 1 || data.Units is not { Length: > 0 })
            throw new InvalidOperationException("Unsupported or empty unit catalog.");
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (UnitDefinition unit in data.Units)
            if (string.IsNullOrWhiteSpace(unit.Id) || !names.Add(unit.Id) || !float.IsFinite(unit.Speed) || unit.Speed <= 0)
                throw new InvalidOperationException("Invalid unit definition.");
        return data;
    }

    public static Simulation Create(HeightMap map)
    {
        UnitCatalog data = LoadCatalog();
        WorldState world = new(map);
        for (int z = 0; z < 3; z++)
            for (int x = 0; x < 4; x++)
                world.CreateUnit(1, 0, new Vector2(24 + x * 9, 24 + z * 9), data.Units[0].Speed);
        for (int i = 0; i < 4; i++)
            world.CreateUnit(2, 0, new Vector2(90 + i * 6, 90), data.Units[0].Speed);
        return new Simulation(world, new OpsRules());
    }

    public static ulong ComputeCatalogHash()
    {
        using Stream stream = typeof(PrototypeScenario).Assembly.GetManifestResourceStream("DeadGround.Units.json")
            ?? throw new InvalidOperationException("Unit data not found.");
        byte[] buffer = new byte[1024];
        ulong hash = 14695981039346656037UL;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                hash ^= buffer[i];
                hash *= 1099511628211UL;
            }
        }
        return hash;
    }

    public static ulong ComputeCatalogHash(UnitCatalog catalog) => ComputeCatalogHash();

    public static MatchSetup CreateMatchSetup(HeightMap map, string ruleSetId = "Ops")
    {
        _ = LoadCatalog();
        return new MatchSetup(
            MatchSetup.CurrentProtocolVersion,
            ComputeCatalogHash(),
            MatchSetup.ComputeMapHash(map),
            ruleSetId);
    }
}
