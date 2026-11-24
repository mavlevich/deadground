using System.Numerics;
using DeadGround.Engine;
using DeadGround.Rules.Tests;
using Xunit;

namespace Engine.Core.Tests;

public sealed class BuildingGarrisonTests
{
    private const ushort DefInfantry = 0;
    private const ushort DefTank = 1;
    private const ushort DefScout = 2;
    private const ushort DefAtgmInfantry = 3;

    private static HeightMap CreateFlatMap(int size = 129, float spacing = 4f)
    {
        return new HeightMap(size, size, spacing, 0.01f, new ushort[size * size]);
    }

    private sealed class TestPerceptionCatalog : IUnitPerceptionCatalog
    {
        private static readonly UnitPerceptionProfile InfantryProfile = new(
            new VisualSensorProfile(400f, 1.5f),
            new TargetObservationPoints([0.5f, 1.5f]),
            ContactClassification.Infantry);

        private static readonly UnitPerceptionProfile TankProfile = new(
            new VisualSensorProfile(600f, 2.2f),
            new TargetObservationPoints([0.8f, 1.8f, 2.4f]),
            ContactClassification.Tank);

        private static readonly UnitPerceptionProfile ScoutProfile = new(
            new VisualSensorProfile(800f, 2.1f),
            new TargetObservationPoints([0.8f, 1.9f]),
            ContactClassification.Recon);

        public bool TryGetProfile(ushort definition, out UnitPerceptionProfile profile)
        {
            profile = definition switch
            {
                DefInfantry or DefAtgmInfantry => InfantryProfile,
                DefTank => TankProfile,
                DefScout => ScoutProfile,
                _ => default
            };
            return definition <= DefAtgmInfantry;
        }
    }

    private sealed class TestMovementCatalog : IUnitMovementCatalog
    {
        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile)
        {
            profile = definition switch
            {
                DefInfantry or DefAtgmInfantry => new UnitMovementProfile(4.0f, 0.5f, TraversabilityCapabilities.Foot, 180f, 2.0f, 6.0f, 8.0f, true, 0f, 0.45f, 1.7f),
                DefTank => new UnitMovementProfile(12.0f, 1.6f, TraversabilityCapabilities.DefaultVehicle, 45f, 4.0f, 3.0f, 5.0f, true, 0f, 1.6f, 2.4f),
                DefScout => new UnitMovementProfile(16.0f, 1.2f, TraversabilityCapabilities.DefaultVehicle, 60f, 6.0f, 4.0f, 6.0f, true, 0f, 1.2f, 2.1f),
                _ => default
            };
            return definition <= DefAtgmInfantry;
        }
    }

    private sealed class TestCombatCatalog : IUnitCombatCatalog
    {
        private readonly WeaponDefinition _rifle;
        private readonly WeaponDefinition _cannon;
        private readonly WeaponDefinition _atgm;

        public TestCombatCatalog(int rifleDamage = 20, int cannonStructuralDamage = 95, int rifleReloadTicks = 10)
        {
            _rifle = new WeaponDefinition(
                Id: "test_rifle",
                Range: 300f,
                ReloadDurationTicks: rifleReloadTicks,
                Damage: rifleDamage,
                AmmoCapacity: 200,
                MuzzleHeight: 1.4f,
                PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Rifle, 0),
                PenetrationNear: 20f,
                PenetrationFar: 10f,
                PenetrationNearRange: 0f,
                PenetrationFarRange: 300f,
                RoundsPerBurst: 1,
                AccuracyCurve: [new AccuracyPoint(0f, 1.0f), new AccuracyPoint(300f, 1.0f)],
                DispersionMils: 0f,
                TargetMovementPenalty: 0f,
                RangingBonus: 0f,
                MaxRangingBonus: 0f,
                StructuralDamage: 0);

            _cannon = new WeaponDefinition(
                Id: "test_cannon",
                Range: 1000f,
                ReloadDurationTicks: 10,
                Damage: 50,
                AmmoCapacity: 40,
                MuzzleHeight: 2.0f,
                PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 1),
                PenetrationNear: 250f,
                PenetrationFar: 180f,
                PenetrationNearRange: 0f,
                PenetrationFarRange: 1000f,
                RoundsPerBurst: 1,
                AccuracyCurve: [new AccuracyPoint(0f, 1.0f), new AccuracyPoint(1000f, 1.0f)],
                DispersionMils: 0f,
                TargetMovementPenalty: 0f,
                RangingBonus: 0f,
                MaxRangingBonus: 0f,
                StructuralDamage: cannonStructuralDamage);

            _atgm = new WeaponDefinition(
                Id: "test_atgm",
                Range: 800f,
                ReloadDurationTicks: 60,
                Damage: 120,
                AmmoCapacity: 4,
                MuzzleHeight: 1.4f,
                PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.SurfaceToAirMissile, 2),
                PenetrationNear: 400f,
                PenetrationFar: 400f,
                PenetrationNearRange: 0f,
                PenetrationFarRange: 800f,
                RoundsPerBurst: 1,
                AccuracyCurve: [new AccuracyPoint(0f, 1.0f), new AccuracyPoint(800f, 1.0f)],
                DispersionMils: 0f,
                TargetMovementPenalty: 0f,
                RangingBonus: 0f,
                MaxRangingBonus: 0f,
                MinRange: 30f,
                SplashRadius: 4f,
                ProjectileSpeed: 120f,
                GuidanceKind: MissileGuidanceKind.Optical,
                AirTargetsOnly: false,
                MissileMaxTurnRateDegPerSec: 60f,
                MissileProximityFuzeRadius: 3f,
                MissileMaxFlightTicks: 160,
                MissileMotorBurnTicks: 120);
        }

        public bool TryGetProfile(ushort definition, out UnitCombatProfile profile)
        {
            profile = definition switch
            {
                DefInfantry => new UnitCombatProfile(100, _rifle, 360f, default, 0.5f, LeavesWreck: false),
                DefAtgmInfantry => new UnitCombatProfile(100, _atgm, 360f, default, 0.5f, LeavesWreck: false),
                DefTank => new UnitCombatProfile(200, _cannon, 180f, new UnitArmor(100f, 60f, 40f), 1.6f, LeavesWreck: true),
                DefScout => new UnitCombatProfile(80, null, 0f, default, 1.2f, LeavesWreck: true),
                _ => default
            };
            return definition <= DefAtgmInfantry;
        }
    }

    private static (Simulation Sim, WorldState World, GarrisonSystem Garrison, BuildingDestructionSystem Destruction, OpaqueStructure Panelka) CreateHarness(
        OpaqueStructure[]? extraStructures = null,
        float panelkaYawRad = MathF.PI,
        int rifleDamage = 20,
        int rifleReloadTicks = 10,
        int panelkaHealth = 360,
        bool includeNav = true)
    {
        HeightMap map = CreateFlatMap();
        // 5-story Series 1-464 panelka centered at (200, 200): 14.4 m x 11.8 m, height 13.75 m
        OpaqueStructure panelka = new(
            new StructureId(101),
            minX: 192.8f, minY: 0f, minZ: 194.1f,
            maxX: 207.2f, maxY: 13.75f, maxZ: 205.9f,
            isGrounded: true);

        List<OpaqueStructure> allStructures = [panelka];
        if (extraStructures is not null)
        {
            allStructures.AddRange(extraStructures);
        }

        StaticGeometry geometry = new(map, allStructures.ToArray());
        TestPerceptionCatalog perception = new();
        TestMovementCatalog movement = new();
        TestCombatCatalog combatCatalog = new(rifleDamage: rifleDamage, rifleReloadTicks: rifleReloadTicks);

        NavigationSystem? nav = includeNav
            ? new NavigationSystem(new NavigationGrid(geometry, 2.0f, enableHierarchical: true), movement)
            : null;
        BuildingDestructionSystem destruction = new(geometry);
        GarrisonSystem garrison = new(geometry, def => def is DefInfantry or DefAtgmInfantry);
        garrison.SetNavigationSystem(nav);
        Assert.True(garrison.RegisterBuilding(panelka.Id, panelkaYawRad, BuildingGarrisonProfile.CreateDefault()));

        DetectionSystem detection = new(geometry, perception, new DetectionTuning(evaluationIntervalTicks: 1), buildingDestruction: destruction);
        detection.SetGarrisonSystem(garrison);
        CombatSystem combat = new(geometry, combatCatalog, perception, detection, nav, buildingDestruction: destruction);
        combat.SetGarrisonSystem(garrison);

        List<ISimulationSystem> systems = [];
        if (nav is not null) systems.Add(nav);
        systems.Add(destruction);
        systems.Add(garrison);
        systems.Add(detection);
        systems.Add(combat);

        WorldState world = new(map);
        Simulation sim = new(world, new RuleSet(systems.ToArray()), matchSeed: 464UL);
        sim.SetPlayerAdmitted(1, true);
        sim.SetPlayerAdmitted(2, true);

        StructureDestructionProfile destProfile = new(
            FloorsTotal: 5,
            FloorHeight: 2.75f,
            RubbleHeight: 1.65f,
            MaxHealth: (ushort)panelkaHealth,
            StructuralArmor: 0,
            DustCloudRadius: 12f,
            DustCloudHeight: 10f,
            DustCloudDurationTicks: 40);
        Assert.True(destruction.RegisterStructure(world, panelka.Id, in destProfile));

        return (sim, world, garrison, destruction, panelka);
    }

    [Fact]
    public void Infantry_EntersViaEntrance_WalksAndGarrisons_AndExitsOutsideBounds()
    {
        (Simulation sim, WorldState world, GarrisonSystem garrison, _, OpaqueStructure panelka) = CreateHarness();
        using (sim)
        {
            Assert.True(garrison.TryGetEntrancePosition(panelka.Id, out Vector2 entrance));
            // Yaw = PI places front courtyard (+Z local) toward -Z world (south of building, Z < 194.1)
            Assert.True(entrance.Y < panelka.MinZ);

            // Spawn infantry 12m south of the entrance so it must walk to the door first
            EntityId squad = world.CreateUnit(1, DefInfantry, entrance + new Vector2(0f, -12f), 4.0f, 100, 200);
            Assert.True(sim.TryEnqueue(1, new EnterBuildingCommand(1u, 0, squad, panelka.Id, GarrisonSlotKind.Interior)));

            // Step until the squad walks to the door and enters
            for (int t = 0; t < 160 && !world.IsGarrisoned(squad); t++)
            {
                sim.Step();
            }

            Assert.True(world.IsGarrisoned(squad));
            Assert.True(world.TryGetGarrison(squad, out StructureId structId, out GarrisonSlotKind slot));
            Assert.Equal(panelka.Id, structId);
            Assert.Equal(GarrisonSlotKind.Interior, slot);
            Assert.Equal(1, garrison.TotalEntries);
            Assert.Equal(1, garrison.GetOccupantCount(panelka.Id));

            // Move and GroupMove commands are rejected while garrisoned
            Assert.False(sim.TryEnqueue(1, new MoveCommand(2u, sim.Tick, squad, new Vector2(180f, 180f))));
            Assert.False(sim.TryEnqueue(1, new GroupMoveCommand(2u, sim.Tick, new[] { squad }, new Vector2(180f, 180f))));

            // Exit the building cleanly
            Assert.True(sim.TryEnqueue(1, new ExitBuildingCommand(2u, sim.Tick, squad)));
            sim.Step();

            Assert.False(world.IsGarrisoned(squad));
            Assert.Equal(0, garrison.GetOccupantCount(panelka.Id));
            Assert.Equal(1, garrison.TotalExits);

            Assert.True(world.TryGetUnit(squad, out UnitState exited));
            bool insideAabb = exited.Position.X >= panelka.MinX && exited.Position.X <= panelka.MaxX &&
                              exited.Position.Y >= panelka.MinZ && exited.Position.Y <= panelka.MaxZ;
            Assert.False(insideAabb, $"Exited infantry must be placed outside building AABB, got {exited.Position}");

            // Move is accepted again after exiting
            Assert.True(sim.TryEnqueue(1, new MoveCommand(3u, sim.Tick, squad, new Vector2(180f, 180f))));
        }
    }

    [Fact]
    public void Garrison_EnforcesInfantryOnly_CapacityLimit_SingleSideOccupancy_AndRoofIntactness()
    {
        (Simulation sim, WorldState world, GarrisonSystem garrison, BuildingDestructionSystem destruction, OpaqueStructure panelka) =
            CreateHarness(includeNav: false);
        using (sim)
        {
            Assert.True(garrison.TryGetEntrancePosition(panelka.Id, out Vector2 entrance));

            // 1. Vehicles cannot enter buildings
            EntityId tank = world.CreateUnit(1, DefTank, entrance, 12f, 200, 40);
            Assert.False(sim.TryEnqueue(1, new EnterBuildingCommand(1u, 0, tank, panelka.Id, GarrisonSlotKind.Interior)));

            // 2. Fill all 12 slots with Player 1 infantry
            uint seq = 1u;
            EntityId[] friendlies = new EntityId[12];
            for (int i = 0; i < 12; i++)
            {
                friendlies[i] = world.CreateUnit(1, DefInfantry, entrance, 4f, 100, 200);
                Assert.True(sim.TryEnqueue(1, new EnterBuildingCommand(seq++, 0, friendlies[i], panelka.Id, GarrisonSlotKind.Interior)));
            }
            sim.Step();
            Assert.Equal(12, garrison.GetOccupantCount(panelka.Id));

            // 13th friendly infantry is rejected at enqueue (own-side capacity check)
            EntityId overflow = world.CreateUnit(1, DefInfantry, entrance, 4f, 100, 200);
            Assert.False(sim.TryEnqueue(1, new EnterBuildingCommand(seq++, sim.Tick, overflow, panelka.Id, GarrisonSlotKind.Interior)));

            // 3. Hostile infantry cannot enter an enemy-occupied section (no CQB in PR 2.1),
            //    and enqueueing from afar does NOT leak occupancy as a FoW oracle (rejected on arrival/commit)
            EntityId hostileAtDoor = world.CreateUnit(2, DefInfantry, entrance, 4f, 100, 200);
            Assert.True(sim.TryEnqueue(2, new EnterBuildingCommand(1u, sim.Tick, hostileAtDoor, panelka.Id, GarrisonSlotKind.Interior)));
            sim.Step();
            Assert.False(world.IsGarrisoned(hostileAtDoor));
            Assert.Equal(12, garrison.GetOccupantCount(panelka.Id));

            // 4. Switching Interior -> Roof works while all 5 floors stand
            Assert.True(sim.TryEnqueue(1, new EnterBuildingCommand(seq++, sim.Tick, friendlies[0], panelka.Id, GarrisonSlotKind.Roof)));
            sim.Step();
            Assert.Equal(GarrisonSlotKind.Roof, world.GetGarrisonSlotKind(friendlies[0]));

            // 5. Collapse top floor -> rooftop infantry is demoted to Interior and cannot return to Roof
            Assert.True(destruction.ApplyDirectHit(world, sim.Tick, panelka.Id, new Vector3(200f, 10f, 194.1f), 100, out _, out byte floorsLost, out _));
            Assert.True(floorsLost >= 1);
            sim.Step();
            Assert.Equal(GarrisonSlotKind.Interior, world.GetGarrisonSlotKind(friendlies[0]));

            // Attempting to re-enter Roof now fails at execution without breaking state
            Assert.True(sim.TryEnqueue(1, new EnterBuildingCommand(seq++, sim.Tick, friendlies[0], panelka.Id, GarrisonSlotKind.Roof)));
            sim.Step();
            Assert.Equal(GarrisonSlotKind.Interior, world.GetGarrisonSlotKind(friendlies[0]));
        }
    }

    [Fact]
    public void Garrison_ElevatedWindowAndRoofFiring_SeesOverObstacle_WithoutSelfOcclusion()
    {
        // A 3.5m high wall sits 20m south of the panelka (Z=172..174), completely blocking ground infantry (eye=1.5m)
        // from seeing an enemy infantry target at Z=150 (height=1.5m), while elevated 5-story windows (~12.4m)
        // and the rooftop (~14.9m) see and shoot cleanly over the wall without self-occlusion by the panelka AABB.
        OpaqueStructure wall = new(
            new StructureId(200),
            minX: 160f, minY: 0f, minZ: 172f,
            maxX: 240f, maxY: 3.5f, maxZ: 174f,
            isGrounded: true);

        (Simulation sim, WorldState world, GarrisonSystem garrison, _, OpaqueStructure panelka) =
            CreateHarness(extraStructures: [wall], rifleDamage: 20, rifleReloadTicks: 5, includeNav: false);
        using (sim)
        {
            Assert.True(garrison.TryGetEntrancePosition(panelka.Id, out Vector2 entrance));
            EntityId shooter = world.CreateUnit(1, DefInfantry, entrance, 4f, 100, 200);
            EntityId target = world.CreateUnit(2, DefInfantry, new Vector2(200f, 150f), 4f, 100, 0);
            world.SetFireStance(target, FireStance.HoldFire);

            // On the ground behind the 3.5m wall, shooter has NO LOS to target
            sim.Step();
            PlayerKnowledge p1 = new(1);
            sim.UpdateKnowledge(p1);
            Assert.Equal(0, p1.ContactCount);
            Assert.Equal(0, sim.Combat!.TotalShotsFired);

            // Enter panelka Interior: elevated window port sees over the 3.5m wall and opens fire without self-occlusion
            Assert.True(sim.TryEnqueue(1, new EnterBuildingCommand(1u, sim.Tick, shooter, panelka.Id, GarrisonSlotKind.Interior)));
            for (int t = 0; t < 30; t++)
            {
                sim.Step();
            }

            sim.UpdateKnowledge(p1);
            Assert.True(p1.ContactCount >= 1 || !world.TryGetUnit(target, out _), "Elevated garrison should spot target over 3.5m wall.");
            Assert.True(sim.Combat.TotalShotsFired >= 1, "Elevated garrison should fire through window port without self-occlusion.");
        }
    }

    [Fact]
    public void Garrison_CoverDamageMultiplier_ReducesDirectFireDamage_InteriorRubbleRoof()
    {
        // Measure single-shot damage against open ground (1.0x), Roof (0.70x), Rubble (0.55x), Interior (0.35x)
        int RunSingleShotDamage(GarrisonSlotKind? slot, bool collapseBuildingFirst)
        {
            (Simulation sim, WorldState world, GarrisonSystem garrison, BuildingDestructionSystem destruction, OpaqueStructure panelka) =
                CreateHarness(rifleDamage: 40, rifleReloadTicks: 2, includeNav: false);
            using (sim)
            {
                Assert.True(garrison.TryGetEntrancePosition(panelka.Id, out Vector2 entrance));
                // Place attacker 30m south of entrance (within 40m concealment range, outside 5-story rooftop parapet dead zone)
                EntityId attacker = world.CreateUnit(1, DefInfantry, entrance + new Vector2(0f, -30f), 4f, 100, 200);
                EntityId victim = world.CreateUnit(2, DefInfantry, entrance, 4f, 100, 0);
                world.SetFireStance(victim, FireStance.HoldFire);

                if (slot.HasValue)
                {
                    Assert.True(sim.TryEnqueue(2, new EnterBuildingCommand(1u, 0, victim, panelka.Id, slot.Value)));
                    sim.Step();
                    Assert.Equal(slot.Value, world.GetGarrisonSlotKind(victim));
                }

                if (collapseBuildingFirst)
                {
                    Assert.True(destruction.ApplyDirectHit(world, sim.Tick, panelka.Id, new Vector3(200f, 1.5f, 194.1f), 500, out _, out _, out bool collapsed));
                    Assert.True(collapsed);
                }

                int hpBefore = world.TryGetCombatState(victim, out WorldState.CombatState pre) ? pre.CurrentHealth : 0;
                for (int t = 0; t < 20 && sim.Combat!.TotalShotsFired == 0; t++)
                {
                    sim.Step();
                }

                // Single-layer cover: 100%-accuracy shot hits on the very first shot (no hidden hit-probability double-dip)
                Assert.Equal(1, sim.Combat!.TotalShotsFired);
                int hpAfter = world.TryGetCombatState(victim, out WorldState.CombatState post) ? post.CurrentHealth : 0;
                Assert.True(hpAfter < hpBefore, "Expected 100%-accuracy first shot to hit without double-dipped cover miss.");
                return hpBefore - hpAfter;
            }
        }

        int openDmg = RunSingleShotDamage(null, collapseBuildingFirst: false);
        int roofDmg = RunSingleShotDamage(GarrisonSlotKind.Roof, collapseBuildingFirst: false);
        int rubbleDmg = RunSingleShotDamage(GarrisonSlotKind.Interior, collapseBuildingFirst: true);
        int interiorDmg = RunSingleShotDamage(GarrisonSlotKind.Interior, collapseBuildingFirst: false);

        Assert.Equal(40, openDmg);
        Assert.Equal(28, roofDmg);      // 40 * 0.70 = 28
        Assert.Equal(22, rubbleDmg);    // 40 * 0.55 = 22
        Assert.Equal(14, interiorDmg);  // 40 * 0.35 = 14
        Assert.True(interiorDmg < rubbleDmg && rubbleDmg < roofDmg && roofDmg < openDmg);
    }

    [Fact]
    public void Garrison_StructuralHitAndFloorCollapse_InflictsDeterministicCasualties_AndDemotesRoofTroops()
    {
        (Simulation sim, WorldState world, GarrisonSystem garrison, _, OpaqueStructure panelka) =
            CreateHarness(panelkaHealth: 200, includeNav: false);
        using (sim)
        {
            Assert.True(garrison.TryGetEntrancePosition(panelka.Id, out Vector2 entrance));
            EntityId tank = world.CreateUnit(1, DefTank, new Vector2(200f, 130f), 12f, 200, 40, initialTurretYaw: MathF.PI);
            world.SetFireStance(tank, FireStance.HoldFire);
            EntityId roofSoldier = world.CreateUnit(2, DefInfantry, entrance, 4f, 35, 0);
            EntityId interiorSoldier = world.CreateUnit(2, DefInfantry, entrance, 4f, 35, 0);
            world.SetFireStance(roofSoldier, FireStance.HoldFire);
            world.SetFireStance(interiorSoldier, FireStance.HoldFire);

            Assert.True(sim.TryEnqueue(2, new EnterBuildingCommand(1u, 0, roofSoldier, panelka.Id, GarrisonSlotKind.Roof)));
            Assert.True(sim.TryEnqueue(2, new EnterBuildingCommand(2u, 0, interiorSoldier, panelka.Id, GarrisonSlotKind.Interior)));
            sim.Step();

            Assert.Equal(GarrisonSlotKind.Roof, world.GetGarrisonSlotKind(roofSoldier));
            Assert.Equal(GarrisonSlotKind.Interior, world.GetGarrisonSlotKind(interiorSoldier));

            // Player 1 tank fires AttackGround at the panelka until it collapses completely
            Assert.True(sim.TryEnqueue(1, new AttackGroundCommand(1u, sim.Tick, tank, new Vector3(200f, 2.0f, 194.1f), panelka.Id)));
            for (int t = 0; t < 80; t++)
            {
                sim.Step();
                if (world.TryGetDestructibleStructure(panelka.Id, out DestructibleStructureState st) && st.IsCollapsed)
                    break;
            }

            Assert.True(world.TryGetDestructibleStructure(panelka.Id, out DestructibleStructureState finalSt));
            Assert.True(finalSt.IsCollapsed);

            // Structural hits + floor/full collapses inflict heavy casualties on both roof and interior troops
            WorldState.CombatState roofPost = default;
            WorldState.CombatState interiorPost = default;
            bool roofAlive = world.TryGetUnit(roofSoldier, out _) && world.TryGetCombatState(roofSoldier, out roofPost);
            bool interiorAlive = world.TryGetUnit(interiorSoldier, out _) && world.TryGetCombatState(interiorSoldier, out interiorPost);
            int roofHp = roofAlive ? roofPost.CurrentHealth : 0;
            int interiorHp = interiorAlive ? interiorPost.CurrentHealth : 0;
            Assert.True(!roofAlive || roofHp < 35, "Rooftop soldier must take structural/collapse casualties.");
            Assert.True(!interiorAlive || interiorHp < 35, "Interior soldier must take structural/collapse casualties.");
            if (roofAlive)
            {
                Assert.Equal(GarrisonSlotKind.Interior, world.GetGarrisonSlotKind(roofSoldier));
            }
            Assert.True(!roofAlive || !interiorAlive || garrison.TotalCollapseCasualties >= 1 ||
                        (roofHp < interiorHp),
                "Roof troops must suffer heavier collapse damage than interior troops.");
        }
    }

    [Fact]
    public void Garrison_FogOfWarConcealment_InteriorHiddenBeyondSpotRange_RevealedOnFireAndCloseRecon_RoofVisible()
    {
        (Simulation sim, WorldState world, GarrisonSystem garrison, _, OpaqueStructure panelka) =
            CreateHarness(rifleReloadTicks: 100, includeNav: false);
        using (sim)
        {
            Assert.True(garrison.TryGetEntrancePosition(panelka.Id, out Vector2 entrance));
            // Player 1 observer at 80m south (> 40m ConcealmentSpotRange)
            EntityId scout = world.CreateUnit(1, DefScout, new Vector2(200f, 120f), 16f, 80, 0);
            // Player 2 infantry enters Interior on HoldFire
            EntityId defender = world.CreateUnit(2, DefInfantry, entrance, 4f, 100, 200);
            world.SetFireStance(defender, FireStance.HoldFire);

            Assert.True(sim.TryEnqueue(2, new EnterBuildingCommand(1u, 0, defender, panelka.Id, GarrisonSlotKind.Interior)));
            for (int t = 0; t < 5; t++) sim.Step();

            PlayerKnowledge p1 = new(1);
            sim.UpdateKnowledge(p1);
            // 1. Unfired interior infantry beyond 40m is completely concealed
            Assert.Equal(0, p1.ContactCount);

            // 2. Switch defender to FireAtWill -> it fires at the scout, breaking concealment for 60 ticks
            Assert.True(sim.TryEnqueue(2, new SetFireStanceCommand(2u, sim.Tick, defender, FireStance.FireAtWill)));
            for (int t = 0; t < 10 && sim.Combat!.TotalShotsFired == 0; t++) sim.Step();
            Assert.True(sim.Combat!.TotalShotsFired >= 1);
            sim.Step();

            sim.UpdateKnowledge(p1);
            Assert.Equal(1, p1.ContactCount);
            ContactObservation spotted = p1.Contacts[0];
            Assert.Equal(ContactState.Current, spotted.State);
            Assert.Equal(ContactClassification.Infantry, spotted.Classification);
            Assert.Equal(panelka.Id, spotted.GarrisonStructureId);
            Assert.Equal(GarrisonSlotKind.Interior, spotted.GarrisonSlotKind);

            // 3. Switch defender back to HoldFire and let 60-tick FiringRevealTicks expire -> contact drops to LastKnown
            Assert.True(sim.TryEnqueue(2, new SetFireStanceCommand(3u, sim.Tick, defender, FireStance.HoldFire)));
            for (int t = 0; t < 65; t++) sim.Step();

            sim.UpdateKnowledge(p1);
            Assert.Equal(1, p1.ContactCount);
            Assert.Equal(ContactState.LastKnown, p1.Contacts[0].State);

            // 4. Move defender to Roof -> immediately observable again at 80m with 3D LOS
            Assert.True(sim.TryEnqueue(2, new EnterBuildingCommand(4u, sim.Tick, defender, panelka.Id, GarrisonSlotKind.Roof)));
            for (int t = 0; t < 3; t++) sim.Step();

            sim.UpdateKnowledge(p1);
            ContactObservation? roofCurrent = FindCurrentContact(p1.Contacts);
            Assert.True(roofCurrent.HasValue, "Rooftop infantry should be observable at 80m with 3D LOS.");
            Assert.Equal(GarrisonSlotKind.Roof, roofCurrent.Value.GarrisonSlotKind);

            // 5. Return to Interior (concealed at 80m), then move scout inside 40m close recon range -> spotted immediately
            Assert.True(sim.TryEnqueue(2, new EnterBuildingCommand(5u, sim.Tick, defender, panelka.Id, GarrisonSlotKind.Interior)));
            for (int t = 0; t < 3; t++) sim.Step();
            sim.UpdateKnowledge(p1);
            Assert.Null(FindCurrentContact(p1.Contacts));

            world.SetPosition(scout, new Vector2(200f, 175f)); // ~25m from building center (< 40m)
            for (int t = 0; t < 3; t++) sim.Step();
            sim.UpdateKnowledge(p1);
            ContactObservation? closeCurrent = FindCurrentContact(p1.Contacts);
            Assert.True(closeCurrent.HasValue, "Interior infantry must be spotted within 40m close recon range.");
            Assert.Equal(GarrisonSlotKind.Interior, closeCurrent.Value.GarrisonSlotKind);
        }
    }

    private static ContactObservation? FindCurrentContact(ReadOnlySpan<ContactObservation> contacts)
    {
        for (int i = 0; i < contacts.Length; i++)
        {
            if (contacts[i].State == ContactState.Current)
                return contacts[i];
        }
        return null;
    }

    [Fact]
    public void Garrison_Determinism_IdenticalInputsProduceIdenticalWorldStateHashes()
    {
        ulong RunDeterministicTrial()
        {
            (Simulation sim, WorldState world, GarrisonSystem garrison, _, OpaqueStructure panelka) =
                CreateHarness(panelkaHealth: 360, includeNav: true);
            using (sim)
            {
                Assert.True(garrison.TryGetEntrancePosition(panelka.Id, out Vector2 entrance));
                EntityId inf1 = world.CreateUnit(1, DefInfantry, entrance + new Vector2(-4f, -8f), 4f, 100, 200);
                EntityId inf2 = world.CreateUnit(1, DefInfantry, entrance + new Vector2(4f, -8f), 4f, 100, 200);
                EntityId enemy = world.CreateUnit(2, DefInfantry, new Vector2(200f, 145f), 4f, 100, 200);

                Assert.True(sim.TryEnqueue(1, new EnterBuildingCommand(1u, 0, inf1, panelka.Id, GarrisonSlotKind.Interior)));
                Assert.True(sim.TryEnqueue(1, new EnterBuildingCommand(2u, 0, inf2, panelka.Id, GarrisonSlotKind.Roof)));

                for (int t = 0; t < 120; t++)
                {
                    if (t == 90 && world.IsGarrisoned(inf1))
                    {
                        sim.TryEnqueue(1, new ExitBuildingCommand(3u, sim.Tick, inf1));
                    }
                    sim.Step();
                }

                _ = enemy;
                return world.ComputeStateHash();
            }
        }

        ulong hashA = RunDeterministicTrial();
        ulong hashB = RunDeterministicTrial();
        Assert.Equal(hashA, hashB);
    }

    [Fact]
    public void Garrison_SteadyStateTicks_ZeroManagedAllocations()
    {
        (Simulation sim, WorldState world, GarrisonSystem garrison, _, OpaqueStructure panelka) =
            CreateHarness(rifleReloadTicks: 8, includeNav: true);
        using (sim)
        {
            Assert.True(garrison.TryGetEntrancePosition(panelka.Id, out Vector2 entrance));
            EntityId defender = world.CreateUnit(1, DefInfantry, entrance, 4f, 100, 200);
            EntityId enemy = world.CreateUnit(2, DefInfantry, new Vector2(200f, 155f), 4f, 2000, 200);
            EntityId tank = world.CreateUnit(2, DefTank, new Vector2(215f, 130f), 12f, 200, 40, initialTurretYaw: MathF.PI);
            world.SetFireStance(tank, FireStance.HoldFire);
            _ = enemy;

            Assert.True(sim.TryEnqueue(1, new EnterBuildingCommand(1u, 0, defender, panelka.Id, GarrisonSlotKind.Interior)));

            PlayerKnowledge p1 = new(1);
            PlayerKnowledge p2 = new(2);

            // Warm up JIT, first entry, detection tracker, and combat event paths
            for (int t = 0; t < 30; t++)
            {
                sim.Step();
                sim.UpdateKnowledge(p1);
                sim.UpdateKnowledge(p2);
                sim.AcknowledgeCombatEvents(1, p1.EventCount);
                sim.AcknowledgeCombatEvents(2, p2.EventCount);
            }

            Assert.True(world.IsGarrisoned(defender));

            using NoGcAllocationWindow window = new();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int t = 0; t < 60; t++)
            {
                sim.Step();
                sim.UpdateKnowledge(p1);
                sim.UpdateKnowledge(p2);
                sim.AcknowledgeCombatEvents(1, p1.EventCount);
                sim.AcknowledgeCombatEvents(2, p2.EventCount);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated == 0, window.Describe(allocated));
        }
    }

    [Fact]
    public void Garrison_GuidedMissileLaunch_BreaksFogOfWarConcealment()
    {
        (Simulation sim, WorldState world, GarrisonSystem garrison, _, OpaqueStructure panelka) =
            CreateHarness(includeNav: false);
        using (sim)
        {
            Assert.True(garrison.TryGetEntrancePosition(panelka.Id, out Vector2 entrance));
            // Player 1 tank parked 120m south (> 40m ConcealmentSpotRange) on HoldFire
            EntityId p1Tank = world.CreateUnit(1, DefTank, new Vector2(200f, 80f), 12f, 200, 40);
            world.SetFireStance(p1Tank, FireStance.HoldFire);

            // Player 2 ATGM infantry enters panelka Interior on HoldFire
            EntityId p2Atgm = world.CreateUnit(2, DefAtgmInfantry, entrance, 4f, 100, 4);
            world.SetFireStance(p2Atgm, FireStance.HoldFire);
            Assert.True(sim.TryEnqueue(2, new EnterBuildingCommand(1u, 0, p2Atgm, panelka.Id, GarrisonSlotKind.Interior)));
            for (int t = 0; t < 5; t++) sim.Step();

            PlayerKnowledge p1 = new(1);
            sim.UpdateKnowledge(p1);
            Assert.Equal(0, p1.ContactCount);
            Assert.Equal(-1, world.GetUnitLastFiredTick(p2Atgm));

            // Allow ATGM infantry to fire a guided missile from inside the building -> must record firing & reveal garrison
            Assert.True(sim.TryEnqueue(2, new SetFireStanceCommand(2u, sim.Tick, p2Atgm, FireStance.FireAtWill)));
            for (int t = 0; t < 15 && sim.Combat!.TotalShotsFired == 0; t++) sim.Step();

            Assert.True(sim.Combat!.TotalShotsFired >= 1, "Expected garrisoned ATGM team to launch a guided missile.");
            Assert.True(world.GetUnitLastFiredTick(p2Atgm) >= 0, "Guided missile launch must call RecordUnitFired.");
            sim.Step();

            sim.UpdateKnowledge(p1);
            ContactObservation? revealed = FindCurrentContact(p1.Contacts);
            Assert.True(revealed.HasValue, "Guided missile launch must break interior FoW concealment.");
            Assert.Equal(panelka.Id, revealed.Value.GarrisonStructureId);
            Assert.Equal(GarrisonSlotKind.Interior, revealed.Value.GarrisonSlotKind);
        }
    }

    [Fact]
    public void Garrison_TwelveSimultaneousExits_LandOnTwelveDistinctWalkablePositions_AndAvoidBlockedCells()
    {
        // Place a solid courtyard wall directly blocking one side of the exit fan so passability check must route around it
        OpaqueStructure courtyardWall = new(
            new StructureId(210),
            minX: 196.0f, minY: 0f, minZ: 190.5f,
            maxX: 201.0f, maxY: 3.0f, maxZ: 192.5f,
            isGrounded: true);

        (Simulation sim, WorldState world, GarrisonSystem garrison, _, OpaqueStructure panelka) =
            CreateHarness(extraStructures: [courtyardWall], includeNav: true);
        using (sim)
        {
            Assert.True(garrison.TryGetEntrancePosition(panelka.Id, out Vector2 entrance));
            Assert.True(sim.Navigation!.Grid.Evaluator.IsPointClear(entrance, 0.45f, TraversabilityCapabilities.Foot));

            uint seq = 1u;
            EntityId[] soldiers = new EntityId[12];
            for (int i = 0; i < 12; i++)
            {
                soldiers[i] = world.CreateUnit(1, DefInfantry, entrance, 4f, 100, 200);
                Assert.True(sim.TryEnqueue(1, new EnterBuildingCommand(seq++, 0, soldiers[i], panelka.Id, GarrisonSlotKind.Interior)));
            }
            sim.Step();
            Assert.Equal(12, garrison.GetOccupantCount(panelka.Id));

            // Order all 12 soldiers to exit on the exact same tick
            for (int i = 0; i < 12; i++)
            {
                Assert.True(sim.TryEnqueue(1, new ExitBuildingCommand(seq++, sim.Tick, soldiers[i])));
            }
            sim.Step();
            Assert.Equal(0, garrison.GetOccupantCount(panelka.Id));

            // Every exited soldier must land on a walkable foot cell clear of both panelka and courtyardWall,
            // with all 12 positions distinct (no swap-remove stacking)
            List<Vector2> exitPositions = new(12);
            for (int i = 0; i < 12; i++)
            {
                Assert.True(world.TryGetUnit(soldiers[i], out UnitState u));
                Assert.True(
                    sim.Navigation.Grid.Evaluator.IsPointClear(u.Position, 0.45f, TraversabilityCapabilities.Foot),
                    $"Exited soldier {i} landed on blocked cell {u.Position}.");
                for (int j = 0; j < exitPositions.Count; j++)
                {
                    float sep = Vector2.Distance(u.Position, exitPositions[j]);
                    Assert.True(sep >= 0.5f, $"Soldiers {i} and {j} stacked on exit ({u.Position} vs {exitPositions[j]}).");
                }
                exitPositions.Add(u.Position);
            }
        }
    }

    [Fact]
    public void Garrison_ArtillerySplashNearBuilding_InflictsStructuralCasualties_WithoutOpenGroundDoubleDip()
    {
        (Simulation sim, WorldState world, GarrisonSystem garrison, _, OpaqueStructure panelka) =
            CreateHarness(panelkaHealth: 360, includeNav: false);
        using (sim)
        {
            Assert.True(garrison.TryGetEntrancePosition(panelka.Id, out Vector2 entrance));
            EntityId interiorSoldier = world.CreateUnit(2, DefInfantry, entrance, 4f, 100, 0);
            world.SetFireStance(interiorSoldier, FireStance.HoldFire);
            Assert.True(sim.TryEnqueue(2, new EnterBuildingCommand(1u, 0, interiorSoldier, panelka.Id, GarrisonSlotKind.Interior)));
            sim.Step();
            Assert.True(world.IsGarrisoned(interiorSoldier));

            // Detonate a Grad-class rocket right beside the south facade (18m splash radius, 120 open damage, 95 structural damage)
            RocketProjectileState rocket = new()
            {
                ShooterOwner = 1,
                PresentationId = WeaponPresentation.MakeId(WeaponPresentationKind.RocketArtillery, 0),
                Origin = new Vector3(200f, 3f, 20f),
                Target = new Vector3(200f, 0f, 192f),
                Damage = 120,
                Penetration = 25f,
                SplashRadius = 18f,
                StructuralDamage = 95
            };
            sim.Combat!.ResolveArtilleryImpact(world, sim.Tick, rocket);

            // Garrisoned soldier must survive (not instantly killed by 120 open-ground splash) and instead
            // take calibrated structural blast/collapse damage from ComputeStructuralHitCasualties
            Assert.True(world.TryGetUnit(interiorSoldier, out _), "Garrisoned infantry should not take full open-ground artillery splash.");
            Assert.True(world.TryGetCombatState(interiorSoldier, out WorldState.CombatState cs));
            Assert.True(cs.CurrentHealth < 100 && cs.CurrentHealth > 60,
                $"Expected calibrated structural blast damage (HP between 61 and 99), got {cs.CurrentHealth}.");
        }
    }

    [Fact]
    public void Garrison_RoofCollapse_DemotesRoofTroopsAndConcealsBeyondSpotRange()
    {
        (Simulation sim, WorldState world, GarrisonSystem garrison, BuildingDestructionSystem destruction, OpaqueStructure panelka) =
            CreateHarness(includeNav: false);
        using (sim)
        {
            Assert.True(garrison.TryGetEntrancePosition(panelka.Id, out Vector2 entrance));
            // Scout 80m away sees rooftop soldier cleanly via 3D LOS
            world.CreateUnit(1, DefScout, new Vector2(200f, 120f), 16f, 80, 0);
            EntityId roofSoldier = world.CreateUnit(2, DefInfantry, entrance, 4f, 100, 0);
            world.SetFireStance(roofSoldier, FireStance.HoldFire);

            Assert.True(sim.TryEnqueue(2, new EnterBuildingCommand(1u, 0, roofSoldier, panelka.Id, GarrisonSlotKind.Roof)));
            for (int t = 0; t < 3; t++) sim.Step();

            PlayerKnowledge p1 = new(1);
            sim.UpdateKnowledge(p1);
            ContactObservation? roofSpot = FindCurrentContact(p1.Contacts);
            Assert.True(roofSpot.HasValue);
            Assert.Equal(GarrisonSlotKind.Roof, roofSpot.Value.GarrisonSlotKind);

            // Collapse top floor -> soldier drops to Interior and conceals beyond 40m (drops from Current to LastKnown)
            Assert.True(destruction.ApplyDirectHit(world, sim.Tick, panelka.Id, new Vector3(200f, 11f, 194.1f), 100, out _, out byte floorsLost, out _));
            Assert.True(floorsLost >= 1);
            for (int t = 0; t < 3; t++) sim.Step();

            Assert.Equal(GarrisonSlotKind.Interior, world.GetGarrisonSlotKind(roofSoldier));
            sim.UpdateKnowledge(p1);
            Assert.Null(FindCurrentContact(p1.Contacts));
        }
    }
}
