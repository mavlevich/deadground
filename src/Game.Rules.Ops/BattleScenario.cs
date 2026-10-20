using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

// Historical unit types of content/coldwar (TDD section 17.1).
public static class ColdWarContent
{
    public static UnitCatalog LoadCatalog()
    {
        using Stream stream = typeof(ColdWarContent).Assembly.GetManifestResourceStream("DeadGround.ColdWarUnits.json")
            ?? throw new InvalidOperationException("Cold War unit data not found.");
        return PrototypeScenario.LoadCatalog(stream);
    }
}

// The unit catalog each scenario uses. Server and client both ask here, so a new historical scenario
// cannot run on the server with one catalog and on the client with another.
public static class ScenarioContent
{
    public static bool UsesColdWarContent(string scenario) => scenario == "battle";

    public static UnitCatalog LoadCatalog(string scenario) =>
        UsesColdWarContent(scenario) ? ColdWarContent.LoadCatalog() : PrototypeScenario.LoadCatalog();
}

// A 2 x 2 km meeting engagement with historical units at 1:1 scale. A ridge with two saddles runs
// between the start areas, so the sides do not see each other at the start. A small village sits on
// the western saddle.
public static class BattleScenario
{
    public const int MapResolution = 1001;
    public const float MapSpacing = 2.0f;
    public const float MapSize = (MapResolution - 1) * MapSpacing;

    // Soviet forces start in the south-west, US forces in the north-east (map X east, Z south).
    public static readonly Vector2 SovietStart = new(380f, 1640f);
    public static readonly Vector2 UsStart = new(1640f, 380f);

    private const float BaseHeight = 6f;
    private const float RidgeHeight = 22f;
    private const float RidgeWidth = 110f;
    private const float SaddleOffset = 520f;
    private const float SaddleWidth = 120f;
    private const float SaddleDepth = 0.75f;
    private const float BuildingHeight = 7f;

    // Unit type, offset from the start point, for player 1; player 2 mirrors it with its own types.
    private static readonly (string Soviet, string Us, Vector2 Offset)[] Order =
    [
        ("t62", "m60a1", new Vector2(-40f, 0f)),
        ("t62", "m60a1", new Vector2(0f, 0f)),
        ("t62", "m60a1", new Vector2(40f, 0f)),
        ("btr70", "m113a1", new Vector2(-20f, 45f)),
        ("btr70", "m113a1", new Vector2(20f, 45f)),
        ("brdm2", "m151a2", new Vector2(60f, -60f)),
    ];

    public static int UnitsPerSide => Order.Length;

    public static float TerrainHeight(float x, float z)
    {
        // Distance across the ridge (it runs along x = z) and position along it.
        float across = (x - z) * 0.70710678f;
        float along = (x + z) * 0.70710678f - MapSize * 0.70710678f;
        float saddle = 1f - SaddleDepth * (Bell(along - SaddleOffset, SaddleWidth) + Bell(along + SaddleOffset, SaddleWidth));
        float ridge = RidgeHeight * Bell(across, RidgeWidth) * saddle;
        float rolling = 3f * MathF.Sin(x / 170f) * MathF.Cos(z / 230f) + 2f * MathF.Sin((x + 2f * z) / 310f);
        return BaseHeight + ridge + rolling;
    }

    private static float Bell(float distance, float width) => MathF.Exp(-distance * distance / (2f * width * width));

    public static HeightMap CreateMap()
    {
        ushort[] heights = new ushort[MapResolution * MapResolution];
        for (int row = 0; row < MapResolution; row++)
        {
            for (int column = 0; column < MapResolution; column++)
            {
                float h = TerrainHeight(column * MapSpacing, row * MapSpacing);
                heights[row * MapResolution + column] = (ushort)MathF.Round(MathF.Max(0f, h) * 100f);
            }
        }
        return new HeightMap(MapResolution, MapResolution, MapSpacing, 0.01f, heights);
    }

    public static StaticGeometry CreateStaticGeometry()
    {
        HeightMap map = CreateMap();
        // Village on the western saddle of the ridge.
        Vector2[] houses = [new(600f, 640f), new(635f, 600f), new(668f, 655f), new(700f, 615f)];
        OpaqueStructure[] structures = new OpaqueStructure[houses.Length];
        for (int i = 0; i < houses.Length; i++)
        {
            Vector2 c = houses[i];
            float top = map.GetHeight(c.X, c.Y) + BuildingHeight;
            // The base sits at zero, below the terrain, so the house is grounded on any slope.
            structures[i] = new OpaqueStructure(
                new StructureId(i + 1),
                minX: c.X - 6f, minY: 0f, minZ: c.Y - 5f,
                maxX: c.X + 6f, maxY: top, maxZ: c.Y + 5f,
                isGrounded: true);
        }
        return new StaticGeometry(map, structures);
    }

    public static Simulation CreateSimulation(
        StaticGeometry? geometry = null,
        UnitCatalog? catalog = null,
        ulong matchSeed = 1337UL,
        bool swapSides = false,
        bool excludeRecon = false)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= ScenarioContent.LoadCatalog("battle");
        OpsRules rules = new(geometry, catalog);
        WorldState world = new(geometry.HeightMap);
        Vector2 p1Start = swapSides ? UsStart : SovietStart;
        Vector2 p2Start = swapSides ? SovietStart : UsStart;
        Vector2 p1Enemy = swapSides ? SovietStart : UsStart;
        Vector2 p2Enemy = swapSides ? UsStart : SovietStart;

        ReadOnlySpan<(string Soviet, string Us, Vector2 Offset)> spawnOrder =
            excludeRecon ? Order.AsSpan(0, 5) : Order.AsSpan();

        foreach ((string soviet, string us, Vector2 offset) in spawnOrder)
        {
            Vector2 p1Offset = swapSides ? -offset : offset;
            Vector2 p2Offset = swapSides ? offset : -offset;
            Spawn(world, catalog, 1, soviet, p1Start + p1Offset, p1Enemy);
            Spawn(world, catalog, 2, us, p2Start + p2Offset, p2Enemy);
        }
        return new Simulation(world, rules, matchSeed);
    }

    private static void Spawn(WorldState world, UnitCatalog catalog, int player, string type, Vector2 position, Vector2 enemySide)
    {
        UnitDefinition def = catalog.RequireDefinition(type);
        Vector2 dir = enemySide - position;
        float initialYaw = MathF.Atan2(-dir.X, -dir.Y);
        world.CreateUnit(player, catalog.RequireDefinitionIndex(type), position, def.Speed, def.MaxHealth, catalog.GetAmmoCapacity(def), initialYaw);
    }

    public static MatchSetup CreateMatchSetup(StaticGeometry? geometry = null, string ruleSetId = "Battle", UnitCatalog? catalog = null)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= ScenarioContent.LoadCatalog("battle");
        return new MatchSetup(
            MatchSetup.CurrentProtocolVersion,
            catalog.ContentHash,
            MatchSetup.ComputeMapHash(geometry),
            ruleSetId,
            PlayerFactions: PrototypeScenario.CreatePlayerFactions(catalog));
    }
}
