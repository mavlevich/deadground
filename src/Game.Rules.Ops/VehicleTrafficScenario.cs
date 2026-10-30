using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

/// <summary>
/// Vehicle traffic and separation test scenario for interactive playground and smoke testing.
/// Includes 6 standard situations: column behind stopped vehicle, parked ally/enemy,
/// narrow passage head-on, shared destination queue, bridge deck vs under-span vertical clearance,
/// and mixed vehicle types (T-62, BTR-70, BRDM-2).
/// </summary>
public static class VehicleTrafficScenario
{
    public const int WorldCapacity = 64;
    public const float MapSize = 300f;
    public const float BridgeElevation = 8.0f;
    public const float BridgeWidth = 12.0f;
    public const float BridgeThickness = 1.5f;

    public static readonly Vector2 BridgeStart = new(110f, 160f);
    public static readonly Vector2 BridgeEnd = new(210f, 160f);

    public static StaticGeometry CreateStaticGeometry()
    {
        const int resolution = 61;
        const float spacing = 5.0f; // 60 * 5 = 300m map
        ushort[] heights = new ushort[resolution * resolution];
        Array.Fill(heights, (ushort)0);

        HeightMap map = new(resolution, resolution, spacing, 0.01f, heights);

        BridgeStructure bridge = new(
            new StructureId(1),
            BridgeStart,
            BridgeEnd,
            BridgeWidth,
            BridgeElevation,
            BridgeElevation,
            BridgeThickness);

        // Narrow passage walls at X=172 and X=188, Y from 45 to 75
        OpaqueStructure westWall = new(
            new StructureId(1),
            minX: 172f, minY: 0f, minZ: 45f,
            maxX: 174f, maxY: 5f, maxZ: 75f,
            isGrounded: true);
        OpaqueStructure eastWall = new(
            new StructureId(2),
            minX: 186f, minY: 0f, minZ: 45f,
            maxX: 188f, maxY: 5f, maxZ: 75f,
            isGrounded: true);

        return new StaticGeometry(
            map,
            [westWall, eastWall],
            [bridge],
            ReadOnlySpan<TerrainObstacle>.Empty);
    }

    public static MatchSetup CreateMatchSetup(StaticGeometry geometry, UnitCatalog? catalog = null)
    {
        catalog ??= ColdWarContent.LoadCatalog();
        return new MatchSetup(
            MatchSetup.CurrentProtocolVersion,
            catalog.ContentHash,
            MatchSetup.ComputeMapHash(geometry),
            "VehicleTraffic",
            PlayerTeams: [1, 2],
            PlayerFactions: ["soviet", "us"]);
    }

    public static Simulation CreateSimulation(StaticGeometry? geometry = null, UnitCatalog? catalog = null)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= ColdWarContent.LoadCatalog();

        WorldState world = new(geometry.HeightMap, WorldCapacity);
        OpsRules rules = new(geometry, catalog);
        Simulation sim = new(world, rules);

        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        ushort btr70Idx = catalog.RequireDefinitionIndex("btr70");
        ushort brdm2Idx = catalog.RequireDefinitionIndex("brdm2");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");

        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        UnitDefinition btr70Def = catalog.RequireDefinition("btr70");
        UnitDefinition brdm2Def = catalog.RequireDefinition("brdm2");
        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");

        // 1. Column behind stopped vehicle (Player 1, Soviet)
        // Leader parked at (60, 60)
        EntityId leader = world.CreateUnit(1, t62Idx, new Vector2(60f, 60f), t62Def.Speed, t62Def.MaxHealth, 0, MathF.PI);
        world.SetFireStance(leader, FireStance.HoldFire);

        // Follower 1 at (60, 45) moving North
        EntityId follower1 = world.CreateUnit(1, t62Idx, new Vector2(60f, 45f), t62Def.Speed, t62Def.MaxHealth, 0, MathF.PI);
        world.SetFireStance(follower1, FireStance.HoldFire);
        sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, follower1, new Vector2(60f, 70f)));

        // Follower 2 at (60, 30) moving North
        EntityId follower2 = world.CreateUnit(1, t62Idx, new Vector2(60f, 30f), t62Def.Speed, t62Def.MaxHealth, 0, MathF.PI);
        world.SetFireStance(follower2, FireStance.HoldFire);
        sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, follower2, new Vector2(60f, 70f)));

        // 2. Parked ally and enemy
        EntityId parkedAlly = world.CreateUnit(1, btr70Idx, new Vector2(100f, 60f), btr70Def.Speed, btr70Def.MaxHealth, 0, MathF.PI);
        EntityId parkedEnemy = world.CreateUnit(2, m60Idx, new Vector2(130f, 60f), m60Def.Speed, m60Def.MaxHealth, 0, 0f);
        world.SetFireStance(parkedAlly, FireStance.HoldFire);
        world.SetFireStance(parkedEnemy, FireStance.HoldFire);

        // 3. Narrow passage head-on (between walls at X=172 and X=188)
        EntityId passageNorth = world.CreateUnit(1, brdm2Idx, new Vector2(180f, 35f), brdm2Def.Speed, brdm2Def.MaxHealth, 0, MathF.PI);
        EntityId passageSouth = world.CreateUnit(2, brdm2Idx, new Vector2(180f, 85f), brdm2Def.Speed, brdm2Def.MaxHealth, 0, 0f);
        world.SetFireStance(passageNorth, FireStance.HoldFire);
        world.SetFireStance(passageSouth, FireStance.HoldFire);
        sim.TryEnqueue(1, new MoveCommand(3, sim.Tick, passageNorth, new Vector2(180f, 85f)));
        sim.TryEnqueue(2, new MoveCommand(4, sim.Tick, passageSouth, new Vector2(180f, 35f)));

        // 4. Multiple vehicles with shared destination
        EntityId queue1 = world.CreateUnit(1, t62Idx, new Vector2(40f, 220f), t62Def.Speed, t62Def.MaxHealth, 0, MathF.PI);
        EntityId queue2 = world.CreateUnit(1, t62Idx, new Vector2(40f, 195f), t62Def.Speed, t62Def.MaxHealth, 0, MathF.PI);
        EntityId queue3 = world.CreateUnit(1, t62Idx, new Vector2(40f, 170f), t62Def.Speed, t62Def.MaxHealth, 0, MathF.PI);
        world.SetFireStance(queue1, FireStance.HoldFire);
        world.SetFireStance(queue2, FireStance.HoldFire);
        world.SetFireStance(queue3, FireStance.HoldFire);
        Vector2 sharedGoal = new(40f, 260f);
        sim.TryEnqueue(1, new MoveCommand(5, sim.Tick, queue1, sharedGoal));
        sim.TryEnqueue(1, new MoveCommand(6, sim.Tick, queue2, sharedGoal));
        sim.TryEnqueue(1, new MoveCommand(7, sim.Tick, queue3, sharedGoal));

        // 5. Bridge deck vs under-span vertical clearance
        // Tank on bridge deck (SupportSurface = 1) moving East to (190, 160)
        EntityId bridgeTank = world.CreateUnit(1, t62Idx, new Vector2(120f, 160f), t62Def.Speed, t62Def.MaxHealth, 0, -MathF.PI / 2f, initialSupportSurface: 1);
        world.SetFireStance(bridgeTank, FireStance.HoldFire);
        sim.TryEnqueue(1, new MoveCommand(8, sim.Tick, bridgeTank, new Vector2(190f, 160f)));

        // BTR under bridge on dry ground (SupportSurface = 0) moving North to (160, 190)
        EntityId underBtr = world.CreateUnit(1, btr70Idx, new Vector2(160f, 130f), btr70Def.Speed, btr70Def.MaxHealth, 0, MathF.PI);
        world.SetFireStance(underBtr, FireStance.HoldFire);
        sim.TryEnqueue(1, new MoveCommand(9, sim.Tick, underBtr, new Vector2(160f, 190f)));

        return sim;
    }
}
