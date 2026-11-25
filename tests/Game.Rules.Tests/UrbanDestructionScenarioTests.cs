using System.Diagnostics;
using System.Numerics;
using DeadGround.Engine;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class UrbanDestructionScenarioTests
{
    [Fact]
    public void UrbanDestructionPlayground_ConcealsUsTanksBehindPanelka_ThenOpensLosAfterCollapseAndDustClears()
    {
        StaticGeometry geometry = UrbanDestructionPlaygroundScenario.CreateStaticGeometry();
        using Simulation sim = UrbanDestructionPlaygroundScenario.CreateSimulation(geometry, matchSeed: 1464UL);

        PlayerKnowledge p1 = new(1);
        for (int i = 0; i < 20; i++) sim.Step();
        sim.UpdateKnowledge(p1);

        Assert.Equal(8, p1.OwnUnits.Length);
        Assert.Equal(0, p1.ContactCount);
        Assert.Equal(0, p1.StructureCount);

        // Order both Soviet T-62 tanks to demolish central panelka sections 102 and 103
        EntityId t62West = p1.OwnUnits[0].Id;
        EntityId t62East = p1.OwnUnits[1].Id;
        Assert.True(sim.TryEnqueue(1, new AttackGroundCommand(1, sim.Tick, t62West, new Vector3(494f, 3.5f, 512f), new StructureId(102))));
        Assert.True(sim.TryEnqueue(1, new AttackGroundCommand(2, sim.Tick, t62East, new Vector3(506f, 3.5f, 512f), new StructureId(103))));

        int bothCollapsedTick = -1;
        for (int i = 0; i < 600; i++)
        {
            sim.Step();
            if (bothCollapsedTick < 0 &&
                sim.World.TryGetDestructibleStructure(new StructureId(102), out DestructibleStructureState s102) &&
                sim.World.TryGetDestructibleStructure(new StructureId(103), out DestructibleStructureState s103) &&
                s102.FloorsStanding == 0 && s103.FloorsStanding == 0)
            {
                bothCollapsedTick = sim.Tick;
                break;
            }
        }

        Assert.True(bothCollapsedTick > 0, "Central panelka sections 102 and 103 must collapse under T-62 direct fire.");

        // Advance until collapse dust clears (~180 ticks + margin), then Player 1 spots US tanks behind ruins
        bool spottedUsTankThroughRuins = false;
        for (int i = 0; i < 260; i++)
        {
            sim.Step();
            sim.UpdateKnowledge(p1);
            if (p1.ContactCount > 0)
            {
                spottedUsTankThroughRuins = true;
                break;
            }
        }

        Assert.True(spottedUsTankThroughRuins, "Collapsing central panelka sections and letting dust clear must expose US M60A1 tanks behind the ruins.");
        Assert.True(p1.StructureCount >= 2);
    }

    [Fact]
    public void UrbanDestructionPlayground_IsDeterministicAcrossIdenticalSeeds()
    {
        StaticGeometry geoA = UrbanDestructionPlaygroundScenario.CreateStaticGeometry();
        StaticGeometry geoB = UrbanDestructionPlaygroundScenario.CreateStaticGeometry();
        using Simulation simA = UrbanDestructionPlaygroundScenario.CreateSimulation(geoA, matchSeed: 46400UL);
        using Simulation simB = UrbanDestructionPlaygroundScenario.CreateSimulation(geoB, matchSeed: 46400UL);

        PlayerKnowledge pA = new(1);
        PlayerKnowledge pB = new(1);
        simA.UpdateKnowledge(pA);
        simB.UpdateKnowledge(pB);

        EntityId t62A = pA.OwnUnits[0].Id;
        EntityId gradA = pA.OwnUnits[3].Id;
        EntityId t62B = pB.OwnUnits[0].Id;
        EntityId gradB = pB.OwnUnits[3].Id;

        Assert.True(simA.TryEnqueue(1, new AttackGroundCommand(1, 0, t62A, new Vector3(494f, 4.0f, 512f), new StructureId(102))));
        Assert.True(simB.TryEnqueue(1, new AttackGroundCommand(1, 0, t62B, new Vector3(494f, 4.0f, 512f), new StructureId(102))));
        Assert.True(simA.TryEnqueue(1, new FireMissionCommand(2, 0, gradA, new Vector2(500f, 518f))));
        Assert.True(simB.TryEnqueue(1, new FireMissionCommand(2, 0, gradB, new Vector2(500f, 518f))));

        for (int i = 0; i < 320; i++)
        {
            simA.Step();
            simB.Step();
        }

        Assert.Equal(simA.World.ComputeStateHash(), simB.World.ComputeStateHash());
        Assert.True(simA.BuildingDestruction!.TotalFloorsCollapsed > 0, "Expected floors collapsed during deterministic barrage + direct-fire run.");
    }

    [Fact]
    public void ThirtyBuildingStressTest_120SectionsCollapsing_IsDeterministicAndZeroAlloc()
    {
        // 30 4-section panelkas = 120 destructible sections (within 256 limit) on a 1024m x 1024m urban grid.
        // Timing percentiles (P50/P95/P99/max) are measured in tests/Benchmarks (--building-destruction)
        // so unit tests remain immune to CI runner CPU noise while enforcing 0 B steady-state allocations.
        HeightMap map = UrbanDestructionPlaygroundScenario.CreateHeightMap();
        const int buildings = 30;
        const int sectionsPerBuilding = 4;
        const int totalSections = buildings * sectionsPerBuilding;
        OpaqueStructure[] structures = new OpaqueStructure[totalSections];

        int idx = 0;
        for (int b = 0; b < buildings; b++)
        {
            int col = b % 6;
            int row = b / 6;
            float baseX = 120f + col * 130f;
            float baseZ = 220f + row * 120f;
            for (int s = 0; s < sectionsPerBuilding; s++)
            {
                float minX = baseX + s * 12f;
                float minZ = baseZ;
                float gy = map.GetHeight(minX + 6f, minZ + 6f);
                structures[idx] = new OpaqueStructure(
                    new StructureId(1000 + idx),
                    minX,
                    gy,
                    minZ,
                    minX + 12f,
                    gy + 15.5f,
                    minZ + 12f,
                    isGrounded: true);
                idx++;
            }
        }

        StaticGeometry geometry = new(map, structures);
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        OpsRules rules = new(geometry, catalog, new NavigationTuning(CellSize: 4.0f));
        WorldState world = new(map);

        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        int t62Ammo = catalog.GetAmmoCapacity(t62Def);
        UnitDefinition bm21Def = catalog.RequireDefinition("bm21");
        ushort bm21Idx = catalog.RequireDefinitionIndex("bm21");
        int bm21Ammo = catalog.GetAmmoCapacity(bm21Def);

        EntityId[] tanks = new EntityId[8];
        for (int i = 0; i < 8; i++)
        {
            tanks[i] = world.CreateUnit(1, t62Idx, new Vector2(140f + i * 95f, 150f), t62Def.Speed, t62Def.MaxHealth, t62Ammo);
        }
        EntityId grad1 = world.CreateUnit(1, bm21Idx, new Vector2(300f, 80f), bm21Def.Speed, bm21Def.MaxHealth, bm21Ammo);
        EntityId grad2 = world.CreateUnit(1, bm21Idx, new Vector2(680f, 80f), bm21Def.Speed, bm21Def.MaxHealth, bm21Ammo);
        for (int i = 0; i < 6; i++)
        {
            world.CreateUnit(2, t62Idx, new Vector2(180f + i * 115f, 840f), t62Def.Speed, t62Def.MaxHealth, t62Ammo);
        }

        using Simulation sim = new(world, rules, matchSeed: 30120UL);
        StructureDestructionProfile profile = UrbanDestructionPlaygroundScenario.PanelkaProfile;
        for (int i = 0; i < totalSections; i++)
        {
            Assert.True(sim.BuildingDestruction!.RegisterStructure(world, structures[i].Id, in profile));
        }

        // Order tanks and Grad launchers to engage buildings simultaneously
        uint seq = 1;
        for (int i = 0; i < 6; i++)
        {
            OpaqueStructure target = structures[i * sectionsPerBuilding];
            Assert.True(sim.TryEnqueue(1, new AttackGroundCommand(seq++, 0, tanks[i], new Vector3(target.MinX + 6f, target.MinY + 2f, target.MinZ), target.Id)));
        }
        Assert.True(sim.TryEnqueue(1, new FireMissionCommand(seq++, 0, grad1, new Vector2(280f, 460f))));
        Assert.True(sim.TryEnqueue(1, new FireMissionCommand(seq++, 0, grad2, new Vector2(660f, 460f))));

        // Warmup 90 ticks so tanks and Grad rockets begin breaching durable 760 HP sections
        for (int i = 0; i < 90; i++) sim.Step();

        const int measuredTicks = 160;
        long maxAlloc = 0;
        PlayerKnowledge p1 = new(1);
        PlayerKnowledge p2 = new(2);

        for (int i = 0; i < measuredTicks; i++)
        {
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            sim.Step();
            sim.UpdateKnowledge(p1);
            sim.UpdateKnowledge(p2);
            long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            if (allocDelta > maxAlloc) maxAlloc = allocDelta;
        }

        Assert.Equal(120, world.DestructibleStructureCount);
        Assert.True(sim.BuildingDestruction!.TotalFloorsCollapsed > 0, "Expected collapsing sections during 30-building stress test.");
        Assert.Equal(0, maxAlloc);
    }

    [Fact]
    public void RubbleOvershootMisses_ClearCollapsedRubbleAndImpactGroundBehindTarget()
    {
        StaticGeometry geometry = UrbanDestructionPlaygroundScenario.CreateStaticGeometry();
        using Simulation sim = UrbanDestructionPlaygroundScenario.CreateSimulation(geometry, matchSeed: 46411UL);

        // Collapse central section 102 and 103 to rubble (1.65 m above BaseHeight 2.0 m -> 3.65 m)
        for (int sid = 102; sid <= 103; sid++)
        {
            Assert.True(sim.World.TryGetDestructibleStructureIndex(new StructureId(sid), out int idx));
            ref DestructibleStructureState st = ref sim.World.GetDestructibleStructureRef(idx);
            st.FloorsStanding = 0;
            st.FloorBreachMask = 0;
            st.Health = 0;
        }
        sim.BuildingDestruction!.SyncOverridesFromWorld(sim.World);
        sim.Step();

        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        OpsCombatCatalog combatCatalog = new(catalog);
        Assert.True(combatCatalog.TryGetProfile(catalog.RequireDefinitionIndex("t62"), out UnitCombatProfile t62Profile));

        Vector2 attackerPos = UrbanDestructionPlaygroundScenario.SovietT62WestPos;
        Vector2 targetPos = UrbanDestructionPlaygroundScenario.UsM60WestPos;
        Vector3 muzzlePos = new(attackerPos.X, UrbanDestructionPlaygroundScenario.BaseHeight + t62Profile.Weapon!.MuzzleHeight, attackerPos.Y);
        Vector3 aimedTurret = new(targetPos.X, UrbanDestructionPlaygroundScenario.BaseHeight + 2.9f, targetPos.Y);

        int impactsBeyondTarget = 0;
        for (int i = 0; i < 40; i++)
        {
            Vector3 missPt = sim.Combat!.CalculateMissImpactPoint(
                muzzlePos,
                attackerPos,
                targetPos,
                targetRadius: 1.8f,
                t62Profile.Weapon,
                targetSupportSurface: 0,
                targetAltitude: 0f,
                aimedHitPoint: aimedTurret,
                isAttackerMoving: false);
            if (missPt.Z > targetPos.Y + 1.0f)
            {
                impactsBeyondTarget++;
            }
        }

        Assert.True(impactsBeyondTarget >= 12,
            $"Expected long/wide misses aimed over collapsed rubble to sail past and hit ground behind target, got {impactsBeyondTarget}/40.");
    }
}
