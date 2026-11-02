using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

/// <summary>
/// Group movement and destination distribution playground scenario.
/// Provides 6 test situations:
/// 1. 10 tanks on open field
/// 2. Mixed armor (T-62, BTR-70, BRDM-2)
/// 3. Near building (snapping and exclusion)
/// 4. Bottleneck / corridor approach (partial placement)
/// 5. Bridge approach (deck clearance and bridge routing)
/// 6. Retargeting on the move
/// </summary>
public static class GroupMoveScenario
{
    public const int WorldCapacity = 64;
    public const float MapSize = 300f;
    public const float BridgeElevation = 8.0f;
    public const float BridgeWidth = 12.0f;
    public const float BridgeThickness = 1.5f;

    public static readonly Vector2 BridgeStart = new(110f, 220f);
    public static readonly Vector2 BridgeEnd = new(210f, 220f);

    public static StaticGeometry CreateStaticGeometry()
    {
        const int resolution = 61;
        const float spacing = 5.0f; // 60 * 5 = 300m map
        ushort[] heights = new ushort[resolution * resolution];
        Array.Fill(heights, (ushort)0);

        HeightMap map = new(resolution, resolution, spacing, 0.01f, heights);

        // Bridge spanning water/canyon from (110, 220) to (210, 220)
        BridgeStructure bridge = new(
            new StructureId(1),
            BridgeStart,
            BridgeEnd,
            BridgeWidth,
            BridgeElevation,
            BridgeElevation,
            BridgeThickness);

        // Large building near group 3 at X in [60..80], Y in [140..160]
        OpaqueStructure building = new(
            new StructureId(1),
            minX: 60f, minY: 0f, minZ: 140f,
            maxX: 80f, maxY: 6f, maxZ: 160f,
            isGrounded: true);

        // Bottleneck walls: 6m passage between walls at X=177 and X=183, Y from 45 to 75
        OpaqueStructure westWall = new(
            new StructureId(2),
            minX: 160f, minY: 0f, minZ: 45f,
            maxX: 177f, maxY: 5f, maxZ: 75f,
            isGrounded: true);
        OpaqueStructure eastWall = new(
            new StructureId(3),
            minX: 183f, minY: 0f, minZ: 45f,
            maxX: 200f, maxY: 5f, maxZ: 75f,
            isGrounded: true);

        return new StaticGeometry(
            map,
            [building, westWall, eastWall],
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
            "GroupMove",
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

        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        UnitDefinition btr70Def = catalog.RequireDefinition("btr70");
        UnitDefinition brdm2Def = catalog.RequireDefinition("brdm2");

        // 1. 10 tanks on open field (Player 1, Soviet) at X in [20..60], Y in [20..32]
        for (int i = 0; i < 10; i++)
        {
            float x = 20f + (i % 5) * 10.0f;
            float y = 20f + (i / 5) * 12.0f;
            EntityId id = world.CreateUnit(1, t62Idx, new Vector2(x, y), t62Def.Speed, t62Def.MaxHealth, 0, MathF.PI);
            world.SetFireStance(id, FireStance.HoldFire);
        }

        // 2. Mixed armor: 2x T-62, 2x BTR-70, 2x BRDM-2 at X in [100..110], Y in [20..44]
        EntityId[] mixedUnits =
        [
            world.CreateUnit(1, t62Idx, new Vector2(100f, 20f), t62Def.Speed, t62Def.MaxHealth, 0, MathF.PI),
            world.CreateUnit(1, t62Idx, new Vector2(110f, 20f), t62Def.Speed, t62Def.MaxHealth, 0, MathF.PI),
            world.CreateUnit(1, btr70Idx, new Vector2(100f, 32f), btr70Def.Speed, btr70Def.MaxHealth, 0, MathF.PI),
            world.CreateUnit(1, btr70Idx, new Vector2(110f, 32f), btr70Def.Speed, btr70Def.MaxHealth, 0, MathF.PI),
            world.CreateUnit(1, brdm2Idx, new Vector2(100f, 44f), brdm2Def.Speed, brdm2Def.MaxHealth, 0, MathF.PI),
            world.CreateUnit(1, brdm2Idx, new Vector2(110f, 44f), brdm2Def.Speed, brdm2Def.MaxHealth, 0, MathF.PI)
        ];
        foreach (EntityId id in mixedUnits) world.SetFireStance(id, FireStance.HoldFire);

        // 3. Near building: 4 tanks facing a building at X in [45..55], Y in [138..150]
        for (int i = 0; i < 4; i++)
        {
            float x = 45f + (i % 2) * 10.0f;
            float y = 138f + (i / 2) * 12.0f;
            EntityId id = world.CreateUnit(1, t62Idx, new Vector2(x, y), t62Def.Speed, t62Def.MaxHealth, 0, -MathF.PI / 2f);
            world.SetFireStance(id, FireStance.HoldFire);
        }

        // 4. Bottleneck approach: 6 tanks approaching 6m passage
        for (int i = 0; i < 6; i++)
        {
            float x = 165f + (i % 3) * 10.0f;
            float y = 20f + (i / 3) * 12.0f;
            EntityId id = world.CreateUnit(1, t62Idx, new Vector2(x, y), t62Def.Speed, t62Def.MaxHealth, 0, MathF.PI);
            world.SetFireStance(id, FireStance.HoldFire);
        }

        // 5. Bridge approach: 4 vehicles facing bridge start
        EntityId[] bridgeGroup =
        [
            world.CreateUnit(1, t62Idx, new Vector2(70f, 215f), t62Def.Speed, t62Def.MaxHealth, 0, -MathF.PI / 2f),
            world.CreateUnit(1, t62Idx, new Vector2(70f, 227f), t62Def.Speed, t62Def.MaxHealth, 0, -MathF.PI / 2f),
            world.CreateUnit(1, btr70Idx, new Vector2(58f, 215f), btr70Def.Speed, btr70Def.MaxHealth, 0, -MathF.PI / 2f),
            world.CreateUnit(1, btr70Idx, new Vector2(58f, 227f), btr70Def.Speed, btr70Def.MaxHealth, 0, -MathF.PI / 2f)
        ];
        foreach (EntityId id in bridgeGroup) world.SetFireStance(id, FireStance.HoldFire);

        // 6. Retargeting on the move: 3 tanks already moving North
        EntityId move1 = world.CreateUnit(1, t62Idx, new Vector2(225f, 80f), t62Def.Speed, t62Def.MaxHealth, 0, MathF.PI);
        EntityId move2 = world.CreateUnit(1, t62Idx, new Vector2(237f, 80f), t62Def.Speed, t62Def.MaxHealth, 0, MathF.PI);
        EntityId move3 = world.CreateUnit(1, t62Idx, new Vector2(249f, 80f), t62Def.Speed, t62Def.MaxHealth, 0, MathF.PI);
        world.SetFireStance(move1, FireStance.HoldFire);
        world.SetFireStance(move2, FireStance.HoldFire);
        world.SetFireStance(move3, FireStance.HoldFire);

        sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, move1, new Vector2(225f, 150f)));
        sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, move2, new Vector2(237f, 150f)));
        sim.TryEnqueue(1, new MoveCommand(3, sim.Tick, move3, new Vector2(249f, 150f)));

        return sim;
    }
}
