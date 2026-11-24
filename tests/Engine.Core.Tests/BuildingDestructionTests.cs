using System.Numerics;
using DeadGround.Rules.Tests;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class BuildingDestructionTests
{
    private const ushort DefTank = 0;
    private const ushort DefGrad = 1;
    private const ushort DefScout = 2;

    private sealed class TestPerceptionCatalog : IUnitPerceptionCatalog
    {
        private static readonly VisualSensorProfile TankSensor = new(600f, 3.0f);
        private static readonly VisualSensorProfile ScoutSensor = new(800f, 2.5f);
        private static readonly TargetObservationPoints Points = new([1.0f, 2.5f]);
        private static readonly UnitPerceptionProfile TankProfile = new(TankSensor, Points, ContactClassification.Tank, 1.0f);
        private static readonly UnitPerceptionProfile ScoutProfile = new(ScoutSensor, Points, ContactClassification.Recon, 0.8f);

        public bool TryGetProfile(ushort definition, out UnitPerceptionProfile profile)
        {
            profile = definition == DefScout ? ScoutProfile : TankProfile;
            return true;
        }
    }

    private sealed class TestMovementCatalog : IUnitMovementCatalog
    {
        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile)
        {
            profile = new UnitMovementProfile(
                Speed: 12f,
                Radius: 1.5f,
                Capabilities: TraversabilityCapabilities.DefaultVehicle,
                TurnRate: 45f,
                ReverseSpeed: 3f,
                Acceleration: 3f,
                Deceleration: 5f,
                CanPivot: true,
                MinTurnRadius: 0f,
                CollisionRadius: 3.0f,
                Height: 2.8f);
            return true;
        }
    }

    private sealed class TestSmokeCatalog : IUnitSmokeCatalog
    {
        public bool TryGetSmokeProfile(ushort definition, out SmokeProfile profile)
        {
            profile = default;
            return false;
        }
    }

    private sealed class TestCombatCatalog(int tankStructuralDamage = 95, int gradStructuralDamage = 145) : IUnitCombatCatalog
    {
        private readonly WeaponDefinition _tankGun = new(
            Id: "test_115mm",
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
            StructuralDamage: tankStructuralDamage);

        private readonly WeaponDefinition _gradRocket = new(
            Id: "test_122mm_rocket",
            Range: 2500f,
            ReloadDurationTicks: 60,
            Damage: 90,
            AmmoCapacity: 40,
            MuzzleHeight: 2.4f,
            PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.RocketArtillery, 1),
            PenetrationNear: 25f,
            PenetrationFar: 25f,
            PenetrationNearRange: 0f,
            PenetrationFarRange: 2500f,
            IsIndirectFire: true,
            MinRange: 100f,
            SalvoCount: 6,
            SalvoIntervalTicks: 4,
            DispersionRadius: 12f,
            SplashRadius: 24f,
            ProjectileSpeed: 160f,
            SetupTicks: 2,
            ElevationDegrees: 32f,
            StructuralDamage: gradStructuralDamage);

        public bool TryGetProfile(ushort definition, out UnitCombatProfile profile)
        {
            if (definition == DefTank)
            {
                profile = new UnitCombatProfile(100, _tankGun, 90f, new UnitArmor(150f, 80f, 40f), 1.8f);
                return true;
            }
            if (definition == DefGrad)
            {
                profile = new UnitCombatProfile(60, _gradRocket, 90f, new UnitArmor(10f, 5f, 5f), 1.8f);
                return true;
            }
            profile = new UnitCombatProfile(40, null, 0f, new UnitArmor(10f, 5f, 5f), 1.2f);
            return true;
        }
    }

    private static (Simulation Sim, WorldState World, StaticGeometry Geometry, BuildingDestructionSystem Destruction, SmokeSystem Smoke, DetectionSystem Detection) CreateHarness(
        OpaqueStructure[] structures,
        ulong seed = 101UL,
        int tankStructuralDamage = 95,
        int gradStructuralDamage = 145)
    {
        HeightMap map = new(129, 129, 16f, 0.01f, new ushort[129 * 129]);
        StaticGeometry geometry = new(map, structures);
        TestPerceptionCatalog perc = new();
        TestMovementCatalog move = new();
        TestSmokeCatalog smokeCat = new();
        TestCombatCatalog combatCat = new(tankStructuralDamage, gradStructuralDamage);

        NavigationSystem nav = new(new NavigationGrid(geometry, 16f), move);
        SmokeSystem smoke = new(geometry, smokeCat);
        BuildingDestructionSystem destruction = new(geometry, smoke);
        DetectionSystem det = new(geometry, perc, new DetectionTuning(evaluationIntervalTicks: 1), smokeSystem: smoke, buildingDestruction: destruction);
        CombatSystem combat = new(geometry, combatCat, perc, det, nav, combatRandom: new DeterministicRandom(seed), buildingDestruction: destruction);
        ArtillerySystem artillery = new(geometry, combatCat, nav);

        WorldState world = new(map);
        Simulation sim = new(world, new RuleSet(nav, destruction, smoke, det, combat, artillery), matchSeed: seed);
        return (sim, world, geometry, destruction, smoke, det);
    }

    [Fact]
    public void StructureDestructionMath_DerivesHeightAndFloorFromDataDrivenProfile()
    {
        StructureDestructionProfile panelka = new(FloorsTotal: 5, FloorHeight: 3.0f, RubbleHeight: 2.2f, MaxHealth: 500);
        Assert.True(panelka.IsValid);

        Assert.Equal(15.5f, StructureDestructionMath.ComputeCurrentHeight(0f, 15.5f, in panelka, 5), precision: 3);
        Assert.Equal(9.0f, StructureDestructionMath.ComputeCurrentHeight(0f, 15.5f, in panelka, 3), precision: 3);
        Assert.Equal(3.0f, StructureDestructionMath.ComputeCurrentHeight(0f, 15.5f, in panelka, 1), precision: 3);
        Assert.Equal(2.2f, StructureDestructionMath.ComputeCurrentHeight(0f, 15.5f, in panelka, 0), precision: 3);

        Assert.Equal((byte)0, StructureDestructionMath.ResolveFloorIndex(1.2f, 0f, in panelka, 5));
        Assert.Equal((byte)2, StructureDestructionMath.ResolveFloorIndex(7.4f, 0f, in panelka, 5));
        Assert.Equal((byte)4, StructureDestructionMath.ResolveFloorIndex(15.0f, 0f, in panelka, 5));

        // Different data-driven profile (3-story building on elevated terrain at MinY = 10m)
        StructureDestructionProfile threeStory = new(FloorsTotal: 3, FloorHeight: 3.5f, RubbleHeight: 1.5f, MaxHealth: 300);
        Assert.Equal(17.0f, StructureDestructionMath.ComputeCurrentHeight(10f, 21f, in threeStory, 2), precision: 3);
        Assert.Equal(11.5f, StructureDestructionMath.ComputeCurrentHeight(10f, 21f, in threeStory, 0), precision: 3);
    }

    [Fact]
    public void DirectHits_BreachFloorAndTriggerProgressiveUpperFloorCollapse()
    {
        OpaqueStructure section = new(new StructureId(101), 400f, 0f, 400f, 412f, 15.5f, 412f);
        (Simulation sim, WorldState world, _, BuildingDestructionSystem destruction, _, _) = CreateHarness([section]);
        using (sim)
        {
            StructureDestructionProfile profile = new(
                FloorsTotal: 5,
                FloorHeight: 3.0f,
                RubbleHeight: 2.2f,
                MaxHealth: 500,
                DustCloudDurationTicks: 120);
            Assert.True(destruction.RegisterStructure(world, section.Id, in profile));

            // 1st hit on floor 2 (y = 7.2m): 60 damage out of 100 floor budget -> floor 2 breached, still standing
            Assert.True(destruction.ApplyDirectHit(world, 10, section.Id, new Vector3(406f, 7.2f, 400f), 60, out byte struck1, out byte lost1, out bool collapsed1));
            Assert.Equal((byte)2, struck1);
            Assert.Equal((byte)0, lost1);
            Assert.False(collapsed1);
            Assert.True(world.TryGetDestructibleStructure(section.Id, out DestructibleStructureState s1));
            Assert.Equal((byte)5, s1.FloorsStanding);
            Assert.NotEqual(0, s1.FloorBreachMask & (1 << 2));

            // 2nd hit on floor 2: 50 damage -> floor 2 fails, floors 2, 3, and 4 progressively collapse (3 floors lost)
            Assert.True(destruction.ApplyDirectHit(world, 20, section.Id, new Vector3(406f, 7.2f, 400f), 50, out byte struck2, out byte lost2, out bool collapsed2));
            Assert.Equal((byte)2, struck2);
            Assert.Equal((byte)3, lost2);
            Assert.False(collapsed2);
            Assert.True(world.TryGetDestructibleStructure(section.Id, out DestructibleStructureState s2));
            Assert.Equal((byte)2, s2.FloorsStanding);
            Assert.Equal(6.0f, s2.CurrentMaxY, precision: 2);
            Assert.Equal(0, s2.FloorBreachMask & (1 << 2)); // Collapsed floor breach bit cleared

            // Destroy ground floor (floor 0) -> remaining section collapses completely to rubble
            Assert.True(destruction.ApplyDirectHit(world, 30, section.Id, new Vector3(406f, 1.5f, 400f), 110, out byte struck3, out byte lost3, out bool collapsed3));
            Assert.Equal((byte)0, struck3);
            Assert.Equal((byte)2, lost3);
            Assert.True(collapsed3);
            Assert.True(world.TryGetDestructibleStructure(section.Id, out DestructibleStructureState s3));
            Assert.Equal((byte)0, s3.FloorsStanding);
            Assert.Equal((ushort)0, s3.Health);
            Assert.Equal(2.2f, s3.CurrentMaxY, precision: 2);
        }
    }

    [Fact]
    public void CollapseSpawnsDustSmokeVolume_BlocksLosTemporarily_ThenOpensLosOverRubble()
    {
        OpaqueStructure section = new(new StructureId(201), 500f, 0f, 500f, 512f, 15.5f, 512f);
        (Simulation sim, WorldState world, StaticGeometry geometry, BuildingDestructionSystem destruction, SmokeSystem smoke, _) = CreateHarness([section]);
        using (sim)
        {
            StructureDestructionProfile profile = new(
                FloorsTotal: 5,
                FloorHeight: 3.0f,
                RubbleHeight: 2.0f,
                MaxHealth: 400,
                DustCloudRadius: 16f,
                DustCloudHeight: 14f,
                DustCloudDurationTicks: 40);
            Assert.True(destruction.RegisterStructure(world, section.Id, in profile));

            LineOfSightContext losCtx = new(geometry.Structures.Count);
            Vector3 observerEye = new(440f, 3.0f, 506f);
            Vector3 targetPt = new(570f, 3.0f, 506f);

            // Intact 15.5m building blocks LOS at y=3.0m
            destruction.CopyHeightOverridesTo(losCtx);
            smoke.CopyActiveVolumesTo(losCtx);
            Assert.False(geometry.IsLineOfSightClear(observerEye, targetPt, losCtx));

            // Collapse section completely at tick 5
            Assert.True(destruction.ApplyDirectHit(world, 5, section.Id, new Vector3(500f, 1.0f, 506f), 400, out _, out _, out bool collapsed));
            Assert.True(collapsed);
            Assert.Equal(1, world.SmokeCloudCount);

            // Immediately after collapse, dust SmokeVolume keeps LOS blocked even though rubble is only 2.0m tall
            destruction.CopyHeightOverridesTo(losCtx);
            smoke.CopyActiveVolumesTo(losCtx);
            Assert.False(geometry.IsLineOfSightClear(observerEye, targetPt, losCtx),
                "Collapse dust SmokeVolume must occlude LOS while dust is active.");

            // Advance past DustCloudDurationTicks (40 ticks) so dust dissipates
            for (int t = 0; t < 48; t++)
                sim.Step();

            destruction.CopyHeightOverridesTo(losCtx);
            smoke.CopyActiveVolumesTo(losCtx);
            Assert.Equal(0, world.SmokeCloudCount);
            Assert.True(geometry.IsLineOfSightClear(observerEye, targetPt, losCtx),
                "Once dust dissipates, LOS at y=3.0m must pass cleanly over 2.0m rubble.");
        }
    }

    [Fact]
    public void AttackGroundCommand_TankDemolishesTargetedSection_AndClearsOrderOnCollapse()
    {
        OpaqueStructure section = new(new StructureId(301), 600f, 0f, 600f, 612f, 15.5f, 612f);
        (Simulation sim, WorldState world, _, BuildingDestructionSystem destruction, _, _) = CreateHarness([section], tankStructuralDamage: 120);
        using (sim)
        {
            StructureDestructionProfile profile = new(FloorsTotal: 5, FloorHeight: 3.0f, RubbleHeight: 2.2f, MaxHealth: 300, DustCloudDurationTicks: 60);
            Assert.True(destruction.RegisterStructure(world, section.Id, in profile));

            EntityId tank = world.CreateUnit(1, DefTank, new Vector2(460f, 606f), 12f, 100, 20, initialTurretYaw: -MathF.PI * 0.5f);

            // Order tank to fire directly at the ground floor (y = 1.5m) of section 301
            Assert.True(sim.TryEnqueue(1, new AttackGroundCommand(1u, 0, tank, new Vector3(600f, 1.5f, 606f), section.Id)));

            for (int t = 0; t < 120; t++)
                sim.Step();

            Assert.True(world.TryGetDestructibleStructure(section.Id, out DestructibleStructureState st));
            Assert.Equal((byte)0, st.FloorsStanding);
            Assert.Equal((ushort)0, st.Health);
            Assert.True(sim.Combat!.TotalShotsFired >= 1);

            // Order must auto-clear once section reaches rubble so tank does not waste remaining ammo
            Assert.True(world.TryGetCombatState(tank, out WorldState.CombatState cs));
            Assert.False(cs.HasAttackGroundOrder);
            Assert.True(cs.Ammunition >= 17, $"Tank should stop firing once section collapses, remaining ammo: {cs.Ammunition}");
        }
    }

    [Fact]
    public void FogOfWarKnowledgeFilter_HiddenDestructionDoesNotLeakUntilObserved()
    {
        // Tall blocker wall at x=500 hides the destructible building at x=1200 from Player 1 at x=200
        OpaqueStructure blocker = new(new StructureId(10), 495f, 0f, 300f, 505f, 40f, 900f);
        OpaqueStructure targetBuilding = new(new StructureId(401), 1200f, 0f, 600f, 1212f, 15.5f, 612f);

        (Simulation sim, WorldState world, _, BuildingDestructionSystem destruction, _, _) = CreateHarness([blocker, targetBuilding], tankStructuralDamage: 200);
        using (sim)
        {
            StructureDestructionProfile profile = new(FloorsTotal: 5, FloorHeight: 3.0f, RubbleHeight: 2.2f, MaxHealth: 300, DustCloudDurationTicks: 100);
            Assert.True(destruction.RegisterStructure(world, targetBuilding.Id, in profile));

            // Player 1 scout far away behind wall (cannot observe x=1200)
            world.CreateUnit(1, DefScout, new Vector2(200f, 606f), 12f, 40, 0);
            // Player 2 tank right next to targetBuilding demolishes it in fog of war
            EntityId p2Tank = world.CreateUnit(2, DefTank, new Vector2(1100f, 606f), 12f, 100, 20, initialTurretYaw: -MathF.PI * 0.5f);
            Assert.True(sim.TryEnqueue(2, new AttackGroundCommand(1u, 0, p2Tank, new Vector3(1200f, 1.5f, 606f), targetBuilding.Id)));

            PlayerKnowledge p1 = new(1);
            PlayerKnowledge p2 = new(2);
            bool p1SawAnyStructureEvent = false;
            bool p2SawStructureCollapsedEvent = false;

            for (int t = 0; t < 80; t++)
            {
                sim.Step();
                sim.UpdateKnowledge(p1);
                sim.UpdateKnowledge(p2);

                ReadOnlySpan<CombatEventObservation> p1Evs = p1.Events;
                for (int e = 0; e < p1Evs.Length; e++)
                {
                    if (p1Evs[e].Kind is CombatEventKind.StructureDamaged or CombatEventKind.StructureCollapsed)
                        p1SawAnyStructureEvent = true;
                }
                sim.AcknowledgeCombatEvents(1, p1Evs.Length);

                ReadOnlySpan<CombatEventObservation> p2Evs = p2.Events;
                for (int e = 0; e < p2Evs.Length; e++)
                {
                    if (p2Evs[e].Kind == CombatEventKind.StructureCollapsed)
                        p2SawStructureCollapsedEvent = true;
                }
                sim.AcknowledgeCombatEvents(2, p2Evs.Length);
            }

            Assert.True(world.TryGetDestructibleStructure(targetBuilding.Id, out DestructibleStructureState truth));
            Assert.Equal((byte)0, truth.FloorsStanding);

            // Player 2 observed the collapse; Player 1 MUST have zero leaked structure or dust knowledge
            Assert.True(p2SawStructureCollapsedEvent, "Player 2 observing its own target must receive StructureCollapsed event.");
            Assert.Equal(1, p2.StructureCount);
            Assert.Equal((byte)0, p2.Structures[0].FloorsStanding);

            Assert.False(p1SawAnyStructureEvent, "Unobserved building destruction must never leak combat events to Player 1.");
            Assert.Equal(0, p1.StructureCount);
            Assert.Equal(0, p1.SmokeCloudCount);

            // Now place a Player 1 scout with clear LOS to the ruined building -> Player 1 learns the ruined state
            world.CreateUnit(1, DefScout, new Vector2(1050f, 606f), 12f, 40, 0);
            for (int t = 0; t < 120; t++) sim.Step(); // Let dust settle and detection update
            sim.UpdateKnowledge(p1);

            Assert.Equal(1, p1.StructureCount);
            Assert.Equal(targetBuilding.Id, p1.Structures[0].Id);
            Assert.Equal((byte)0, p1.Structures[0].FloorsStanding);
            Assert.Equal((ushort)0, p1.Structures[0].Health);
        }
    }

    [Fact]
    public void MultiSectionBarrageAndDirectFire_IsBitForBitDeterministicAcrossReplay()
    {
        static (ulong StateHash, int CollapsedFloors, int CollapsedSections, int DirectHits, int SplashHits) RunReplay(ulong seed)
        {
            OpaqueStructure[] sections =
            [
                new(new StructureId(501), 800f, 0f, 800f, 812f, 15.5f, 812f),
                new(new StructureId(502), 812f, 0f, 800f, 824f, 15.5f, 812f),
                new(new StructureId(503), 824f, 0f, 800f, 836f, 15.5f, 812f),
                new(new StructureId(504), 836f, 0f, 800f, 848f, 15.5f, 812f)
            ];

            (Simulation sim, WorldState world, _, BuildingDestructionSystem destruction, _, _) = CreateHarness(sections, seed: seed);
            using (sim)
            {
                StructureDestructionProfile panelka = new(FloorsTotal: 5, FloorHeight: 3.0f, RubbleHeight: 2.2f, MaxHealth: 400, DustCloudDurationTicks: 100);
                for (int i = 0; i < sections.Length; i++)
                    Assert.True(destruction.RegisterStructure(world, sections[i].Id, in panelka));

                EntityId tank = world.CreateUnit(1, DefTank, new Vector2(806f, 650f), 12f, 100, 20, initialTurretYaw: MathF.PI);
                EntityId grad = world.CreateUnit(1, DefGrad, new Vector2(824f, 300f), 12f, 60, 40, initialTurretYaw: MathF.PI);

                Assert.True(sim.TryEnqueue(1, new AttackGroundCommand(1u, 0, tank, new Vector3(806f, 4.5f, 800f), sections[0].Id)));
                Assert.True(sim.TryEnqueue(1, new FireMissionCommand(2u, 0, grad, new Vector2(824f, 806f))));

                for (int t = 0; t < 220; t++)
                    sim.Step();

                return (
                    world.ComputeStateHash(),
                    destruction.TotalFloorsCollapsed,
                    destruction.TotalSectionsCollapsed,
                    destruction.TotalDirectHits,
                    destruction.TotalSplashHits);
            }
        }

        var runA = RunReplay(seed: 9001UL);
        var runB = RunReplay(seed: 9001UL);

        Assert.Equal(runA.StateHash, runB.StateHash);
        Assert.Equal(runA.CollapsedFloors, runB.CollapsedFloors);
        Assert.Equal(runA.CollapsedSections, runB.CollapsedSections);
        Assert.Equal(runA.DirectHits, runB.DirectHits);
        Assert.Equal(runA.SplashHits, runB.SplashHits);
        Assert.True(runA.CollapsedFloors > 0, "Expected tank + Grad barrage to collapse building floors.");
        Assert.True(runA.DirectHits > 0 && runA.SplashHits > 0, "Expected both direct tank hits and artillery splash hits.");
    }

    [Fact]
    public void BuildingDestructionAndLosOverrides_HotPath_ZeroManagedAllocationsPerTick()
    {
        OpaqueStructure[] sections =
        [
            new(new StructureId(601), 700f, 0f, 700f, 712f, 15.5f, 712f),
            new(new StructureId(602), 712f, 0f, 700f, 724f, 15.5f, 712f),
            new(new StructureId(603), 724f, 0f, 700f, 736f, 15.5f, 712f),
            new(new StructureId(604), 736f, 0f, 700f, 748f, 15.5f, 712f)
        ];

        (Simulation sim, WorldState world, _, BuildingDestructionSystem destruction, _, _) = CreateHarness(sections, seed: 314UL);
        using (sim)
        {
            StructureDestructionProfile panelka = new(FloorsTotal: 5, FloorHeight: 3.0f, RubbleHeight: 2.2f, MaxHealth: 500, DustCloudDurationTicks: 80);
            for (int i = 0; i < sections.Length; i++)
                Assert.True(destruction.RegisterStructure(world, sections[i].Id, in panelka));

            EntityId tank = world.CreateUnit(1, DefTank, new Vector2(706f, 540f), 12f, 100, 40, initialTurretYaw: MathF.PI);
            world.CreateUnit(2, DefScout, new Vector2(730f, 860f), 12f, 40, 0);

            Assert.True(sim.TryEnqueue(1, new AttackGroundCommand(1u, 0, tank, new Vector3(706f, 1.5f, 700f), sections[0].Id)));

            PlayerKnowledge p1 = new(1);
            PlayerKnowledge p2 = new(2);

            // Warm up JIT, first hit, tracker dictionary entries, and dust smoke paths
            for (int t = 0; t < 25; t++)
            {
                sim.Step();
                sim.UpdateKnowledge(p1);
                sim.UpdateKnowledge(p2);
                sim.AcknowledgeCombatEvents(1, p1.EventCount);
                sim.AcknowledgeCombatEvents(2, p2.EventCount);
            }

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
            Assert.True(destruction.TotalDirectHits >= 1);
            Assert.True(destruction.TotalFloorsCollapsed >= 1);
        }
    }

    [Fact]
    public void P1_StructureImpactEvents_NeverLeakHiddenShooterOrigin_ForArtilleryOrDirectFire()
    {
        // Tall blocker wall at z=550 hides Player 2's Grad at z=200 from Player 1's scout at z=950,
        // while Player 1's scout has full LOS to the target building at z=1000.
        OpaqueStructure wall = new(new StructureId(20), 600f, 0f, 545f, 1400f, 45f, 555f);
        OpaqueStructure building = new(new StructureId(701), 994f, 0f, 1000f, 1006f, 15.5f, 1012f);

        (Simulation sim, WorldState world, _, BuildingDestructionSystem destruction, _, DetectionSystem det) =
            CreateHarness([wall, building], seed: 707UL, gradStructuralDamage: 220);
        using (sim)
        {
            StructureDestructionProfile profile = new(FloorsTotal: 5, FloorHeight: 3.0f, RubbleHeight: 2.2f, MaxHealth: 300, DustCloudDurationTicks: 100);
            Assert.True(destruction.RegisterStructure(world, building.Id, in profile));

            // Player 1 scout watches the building from z=900 (wall at z=550 hides Player 2 Grad at z=200)
            world.CreateUnit(1, DefScout, new Vector2(1000f, 900f), 12f, 40, 0);
            EntityId p2Grad = world.CreateUnit(2, DefGrad, new Vector2(1000f, 200f), 12f, 60, 40, initialTurretYaw: MathF.PI);

            Assert.True(sim.TryEnqueue(2, new FireMissionCommand(1u, 0, p2Grad, new Vector2(1000f, 1006f))));

            PlayerKnowledge p1 = new(1);
            int p1StructureEvents = 0;

            for (int t = 0; t < 200; t++)
            {
                sim.Step();
                Assert.False(det.IsEntityObservedBy(1, p2Grad), "Hidden Grad behind wall must not be observed by Player 1.");
                sim.UpdateKnowledge(p1);

                ReadOnlySpan<CombatEventObservation> evs = p1.Events;
                for (int e = 0; e < evs.Length; e++)
                {
                    CombatEventObservation ev = evs[e];
                    if (ev.Kind is CombatEventKind.StructureDamaged or CombatEventKind.StructureFloorCollapsed or CombatEventKind.StructureCollapsed)
                    {
                        p1StructureEvents++;
                        Assert.Equal(ev.Target, ev.Origin);
                        Assert.True(Vector2.Distance(new Vector2(ev.Origin.X, ev.Origin.Z), new Vector2(1000f, 200f)) > 500f,
                            "Structure event must never disclose hidden launcher origin.");
                    }
                }
                sim.AcknowledgeCombatEvents(1, evs.Length);
            }

            Assert.True(p1StructureEvents >= 1, "Observing third party must still receive sanitized structure impact events.");
        }
    }

    [Fact]
    public void P2_CollapseDust_RefreshesSingleCloudPerSection_AndNeverEvictsPlayerSmokeAtCapacity()
    {
        OpaqueStructure section = new(new StructureId(711), 500f, 0f, 500f, 512f, 15.5f, 512f);
        (Simulation sim, WorldState world, _, BuildingDestructionSystem destruction, _, _) = CreateHarness([section]);
        using (sim)
        {
            StructureDestructionProfile profile = new(FloorsTotal: 5, FloorHeight: 3.0f, RubbleHeight: 2.2f, MaxHealth: 500, DustCloudDurationTicks: 100);
            Assert.True(destruction.RegisterStructure(world, section.Id, in profile));

            // 1st collapse (floor 2 -> floors 2..4 collapse) spawns 1 dust cloud
            Assert.True(destruction.ApplyDirectHit(world, 10, section.Id, new Vector3(506f, 7.0f, 500f), 110, out _, out byte lost1, out bool full1));
            Assert.Equal((byte)3, lost1);
            Assert.False(full1);
            Assert.Equal(1, world.SmokeCloudCount);
            SmokeCloudId firstId = world.SmokeClouds[0].Id;
            int firstExpiry = world.SmokeClouds[0].ExpiresTick;

            // 2nd collapse on the same section at tick 40 refreshes the same cloud instead of adding a duplicate
            Assert.True(destruction.ApplyDirectHit(world, 40, section.Id, new Vector3(506f, 1.0f, 500f), 110, out _, out byte lost2, out bool full2));
            Assert.Equal((byte)2, lost2);
            Assert.True(full2);
            Assert.Equal(1, world.SmokeCloudCount);
            Assert.Equal(firstId, world.SmokeClouds[0].Id);
            Assert.True(world.SmokeClouds[0].ExpiresTick > firstExpiry);

            // Saturate all 64 slots with Player 1 tactical smoke; another building collapse must NOT evict player smoke
            world.ClearSmokeClouds();
            for (int i = 0; i < WorldState.MaxSmokeClouds; i++)
            {
                world.AddSmokeCloud(owner: 1, new Vector2(100f + i * 2f, 100f), 0, 15f, 10f, 50, 300, 10, 40, 1f);
            }
            Assert.Equal(WorldState.MaxSmokeClouds, world.SmokeCloudCount);

            OpaqueStructure sec2 = new(new StructureId(712), 600f, 0f, 500f, 612f, 15.5f, 512f);
            (Simulation sim2, WorldState world2, _, BuildingDestructionSystem dest2, _, _) = CreateHarness([sec2]);
            using (sim2)
            {
                Assert.True(dest2.RegisterStructure(world2, sec2.Id, in profile));
                for (int i = 0; i < WorldState.MaxSmokeClouds; i++)
                {
                    world2.AddSmokeCloud(owner: 1, new Vector2(100f + i * 2f, 100f), 0, 15f, 10f, 50, 300, 10, 40, 1f);
                }
                Assert.True(dest2.ApplyDirectHit(world2, 60, sec2.Id, new Vector3(606f, 1.0f, 500f), 500, out _, out _, out bool collapsed2));
                Assert.True(collapsed2);
                Assert.Equal(WorldState.MaxSmokeClouds, world2.SmokeCloudCount);
                for (int i = 0; i < world2.SmokeCloudCount; i++)
                {
                    Assert.Equal(1, world2.SmokeClouds[i].Owner);
                }
            }
        }
    }

    [Fact]
    public void P2_AttackGroundOnHiddenCollapsedBuilding_DoesNotLeakCollapseState_UntilObserved()
    {
        OpaqueStructure wall = new(new StructureId(30), 695f, 0f, 300f, 705f, 45f, 900f);
        OpaqueStructure targetBuilding = new(new StructureId(721), 1200f, 0f, 600f, 1212f, 15.5f, 612f);

        (Simulation sim, WorldState world, _, BuildingDestructionSystem destruction, _, _) =
            CreateHarness([wall, targetBuilding], tankStructuralDamage: 400);
        using (sim)
        {
            StructureDestructionProfile profile = new(FloorsTotal: 5, FloorHeight: 3.0f, RubbleHeight: 2.2f, MaxHealth: 300, DustCloudDurationTicks: 40);
            Assert.True(destruction.RegisterStructure(world, targetBuilding.Id, in profile));

            // Player 2 collapses targetBuilding completely in fog of war
            Assert.True(destruction.ApplyDirectHit(world, 1, targetBuilding.Id, new Vector3(1200f, 1.0f, 606f), 400, out _, out _, out bool collapsed));
            Assert.True(collapsed);

            // Player 1 tank behind wall has never seen the collapsed building -> order must be ACCEPTED (no FoW leak)
            EntityId p1Tank = world.CreateUnit(1, DefTank, new Vector2(300f, 606f), 12f, 100, 20);
            Assert.True(sim.TryEnqueue(1, new AttackGroundCommand(1u, 0, p1Tank, new Vector3(1206f, 4.5f, 606f), targetBuilding.Id)));
            sim.Step();

            Assert.True(world.TryGetCombatState(p1Tank, out WorldState.CombatState csBeforeObs));
            Assert.True(csBeforeObs.HasAttackGroundOrder, "AttackGround on unobserved collapsed building must not silently reject or auto-clear.");

            // Now Player 1 gets a scout with clear LOS to the rubble -> side learns it collapsed and tank clears order
            world.CreateUnit(1, DefScout, new Vector2(1050f, 606f), 12f, 40, 0);
            for (int t = 0; t < 4; t++) sim.Step();

            Assert.True(world.TryGetCombatState(p1Tank, out WorldState.CombatState csAfterObs));
            Assert.False(csAfterObs.HasAttackGroundOrder, "AttackGround must auto-clear once Player 1 actually observes the collapsed building.");
        }
    }

    [Fact]
    public void P2_ZeroStructuralDamageRejectedOnBuilding_AndPureGroundShotAutoClears()
    {
        OpaqueStructure building = new(new StructureId(731), 600f, 0f, 600f, 612f, 15.5f, 612f);

        // Case 1: Weapon with structural_damage == 0 cannot be ordered onto a building
        (Simulation simZero, WorldState worldZero, _, BuildingDestructionSystem destZero, _, _) =
            CreateHarness([building], tankStructuralDamage: 0);
        using (simZero)
        {
            StructureDestructionProfile profile = new(FloorsTotal: 5, FloorHeight: 3.0f, RubbleHeight: 2.2f, MaxHealth: 300);
            Assert.True(destZero.RegisterStructure(worldZero, building.Id, in profile));

            EntityId mgUnit = worldZero.CreateUnit(1, DefTank, new Vector2(500f, 606f), 12f, 100, 40, initialTurretYaw: -MathF.PI * 0.5f);
            Assert.True(simZero.TryEnqueue(1, new AttackGroundCommand(1u, 0, mgUnit, new Vector3(600f, 2.0f, 606f), building.Id)));
            for (int t = 0; t < 20; t++) simZero.Step();

            Assert.True(worldZero.TryGetCombatState(mgUnit, out WorldState.CombatState csMg));
            Assert.False(csMg.HasAttackGroundOrder);
            Assert.Equal(0, simZero.Combat!.TotalShotsFired);
        }

        // Case 2: Pure ground-point AttackGround (TargetStructure == default) fires once and auto-clears
        (Simulation simGround, WorldState worldGround, _, _, _, _) = CreateHarness([building], tankStructuralDamage: 95);
        using (simGround)
        {
            EntityId tank = worldGround.CreateUnit(1, DefTank, new Vector2(400f, 400f), 12f, 100, 20, initialTurretYaw: -MathF.PI * 0.5f);
            Assert.True(simGround.TryEnqueue(1, new AttackGroundCommand(1u, 0, tank, new Vector3(520f, 0.5f, 400f))));
            for (int t = 0; t < 35; t++) simGround.Step();

            Assert.Equal(1, simGround.Combat!.TotalShotsFired);
            Assert.True(worldGround.TryGetCombatState(tank, out WorldState.CombatState csTank));
            Assert.False(csTank.HasAttackGroundOrder, "Pure ground-point AttackGround must auto-clear after one shot so auto-fire resumes.");
            Assert.Equal(19, csTank.Ammunition);
        }
    }

    [Fact]
    public void P2_RegisterStructure_RejectsGeometryMismatchedProfile_AndEmitsDistinctCollapseEvents()
    {
        OpaqueStructure section = new(new StructureId(741), 500f, 0f, 500f, 512f, 15.5f, 512f);
        (Simulation sim, WorldState world, _, BuildingDestructionSystem destruction, _, _) =
            CreateHarness([section], tankStructuralDamage: 60);
        using (sim)
        {
            // Mismatched FloorHeight (5 * 5m = 25m > 15.5m) must be rejected
            StructureDestructionProfile tooTall = new(FloorsTotal: 5, FloorHeight: 5.0f, RubbleHeight: 2.2f, MaxHealth: 500);
            Assert.False(destruction.RegisterStructure(world, section.Id, in tooTall));

            // RubbleHeight >= AABB height must be rejected
            StructureDestructionProfile rubbleTooTall = new(FloorsTotal: 5, FloorHeight: 3.0f, RubbleHeight: 16.0f, MaxHealth: 500);
            Assert.False(destruction.RegisterStructure(world, section.Id, in rubbleTooTall));

            // Valid 5-floor profile registers cleanly
            StructureDestructionProfile valid = new(FloorsTotal: 5, FloorHeight: 3.0f, RubbleHeight: 2.2f, MaxHealth: 500, DustCloudDurationTicks: 60);
            Assert.True(destruction.RegisterStructure(world, section.Id, in valid));

            // Verify distinct events: StructureDamaged (breach), StructureFloorCollapsed (partial), StructureCollapsed (full)
            EntityId tank = world.CreateUnit(1, DefTank, new Vector2(400f, 506f), 12f, 100, 40, initialTurretYaw: -MathF.PI * 0.5f);
            Assert.True(sim.TryEnqueue(1, new AttackGroundCommand(1u, 0, tank, new Vector3(500f, 7.2f, 506f), section.Id)));

            bool sawDamaged = false;
            bool sawFloorCollapsed = false;
            bool sawFullCollapsed = false;
            PlayerKnowledge p1 = new(1);

            for (int t = 0; t < 180; t++)
            {
                sim.Step();
                sim.UpdateKnowledge(p1);
                ReadOnlySpan<CombatEventObservation> evs = p1.Events;
                for (int e = 0; e < evs.Length; e++)
                {
                    if (evs[e].Kind == CombatEventKind.StructureDamaged) sawDamaged = true;
                    else if (evs[e].Kind == CombatEventKind.StructureFloorCollapsed) sawFloorCollapsed = true;
                    else if (evs[e].Kind == CombatEventKind.StructureCollapsed) sawFullCollapsed = true;
                }
                sim.AcknowledgeCombatEvents(1, evs.Length);

                // Once upper floors collapsed, switch target to ground floor to finish demolition
                if (sawFloorCollapsed && !sawFullCollapsed && world.TryGetDestructibleStructure(section.Id, out DestructibleStructureState cur) && cur.FloorsStanding == 2)
                {
                    sim.TryEnqueue(1, new AttackGroundCommand((uint)(t + 10), t, tank, new Vector3(500f, 1.5f, 506f), section.Id));
                }
            }

            Assert.True(sawDamaged, "Expected StructureDamaged event on non-collapsing hit.");
            Assert.True(sawFloorCollapsed, "Expected StructureFloorCollapsed event on partial upper-floor collapse.");
            Assert.True(sawFullCollapsed, "Expected StructureCollapsed event on full section collapse.");
        }
    }

    [Fact]
    public void P2_DetectionSystem_SkipsRedundantStructureLosQueries_WhenStateAlreadyKnown()
    {
        OpaqueStructure section = new(new StructureId(751), 500f, 0f, 500f, 512f, 15.5f, 512f);
        (Simulation sim, WorldState world, _, BuildingDestructionSystem destruction, _, DetectionSystem det) = CreateHarness([section]);
        using (sim)
        {
            StructureDestructionProfile valid = new(FloorsTotal: 5, FloorHeight: 3.0f, RubbleHeight: 2.2f, MaxHealth: 300, DustCloudDurationTicks: 0);
            Assert.True(destruction.RegisterStructure(world, section.Id, in valid));
            Assert.True(destruction.ApplyDirectHit(world, 0, section.Id, new Vector3(500f, 1.5f, 506f), 300, out _, out _, out _));

            // Single Player 1 scout with no enemies on map: first tick observes collapsed section, next tick skips LOS
            world.CreateUnit(1, DefScout, new Vector2(400f, 506f), 12f, 40, 0);

            sim.Step();
            Assert.Single(det.GetKnownStructures(1).ToArray());
            Assert.True(det.LosQueriesCount >= 1);

            sim.Step();
            Assert.Equal(0, det.LosQueriesCount);
        }
    }
}
