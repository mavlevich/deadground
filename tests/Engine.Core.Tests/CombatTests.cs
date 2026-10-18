using System.Numerics;
using DeadGround.Engine;
using Xunit;

namespace DeadGround.Tests;

public sealed class CombatTests
{
    private sealed class TestCombatCatalog : IUnitCombatCatalog
    {
        private readonly Dictionary<ushort, UnitCombatProfile> _profiles = new();

        public TestCombatCatalog Register(ushort definition, ushort maxHealth, WeaponDefinition? weapon)
        {
            _profiles[definition] = new UnitCombatProfile(maxHealth, weapon);
            return this;
        }

        public bool TryGetProfile(ushort definition, out UnitCombatProfile profile) =>
            _profiles.TryGetValue(definition, out profile);
    }

    private sealed class TestPerceptionCatalog : IUnitPerceptionCatalog
    {
        private readonly Dictionary<ushort, UnitPerceptionProfile> _profiles = new();

        public TestPerceptionCatalog Register(ushort definition, VisualSensorProfile? sensor, TargetObservationPoints? points)
        {
            _profiles[definition] = new UnitPerceptionProfile(sensor, points);
            return this;
        }

        public bool TryGetProfile(ushort definition, out UnitPerceptionProfile profile) =>
            _profiles.TryGetValue(definition, out profile);
    }

    private sealed class TestMovementCatalog : IUnitMovementCatalog
    {
        private readonly Dictionary<ushort, UnitMovementProfile> _profiles = new();

        public TestMovementCatalog Register(ushort definition, float speed, float radius)
        {
            _profiles[definition] = new UnitMovementProfile(speed, radius);
            return this;
        }

        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile) =>
            _profiles.TryGetValue(definition, out profile);
    }

    private static HeightMap CreateFlatMap(int size = 128, float cellSize = 1.0f)
    {
        ushort[] samples = new ushort[size * size];
        return new HeightMap(size, size, cellSize, 0.01f, samples);
    }

    private static (Simulation simulation, WorldState world, StaticGeometry geometry) CreateSetup(
        StaticGeometry? geometry = null,
        ushort tankDef = 1,
        float weaponRange = 60f,
        int reloadTicks = 40,
        ushort damage = 50,
        byte ammo = 10)
    {
        geometry ??= new StaticGeometry(CreateFlatMap(), []);

        WeaponDefinition gun = new("tank_gun", weaponRange, reloadTicks, damage, ammo, MuzzleHeight: 1.9f, PresentationId: 1);

        TestCombatCatalog combatCat = new();
        combatCat.Register(tankDef, 100, gun);
        if (tankDef != 2) combatCat.Register(2, 100, gun);
        combatCat.Register(3, 100, gun);

        TestPerceptionCatalog perceptCat = new();
        perceptCat.Register(tankDef,
            sensor: new VisualSensorProfile(120f, eyeHeight: 1.8f),
            points: new TargetObservationPoints(0.8f, 1.8f));
        if (tankDef != 2)
        {
            perceptCat.Register(2,
                sensor: new VisualSensorProfile(120f, eyeHeight: 1.8f),
                points: new TargetObservationPoints(0.8f, 1.8f));
        }
        perceptCat.Register(3,
            sensor: new VisualSensorProfile(20f, eyeHeight: 1.8f),
            points: new TargetObservationPoints(0.8f, 1.8f));

        TestMovementCatalog moveCat = new();
        moveCat.Register(tankDef, speed: 6.0f, radius: 1.5f);
        if (tankDef != 2) moveCat.Register(2, speed: 6.0f, radius: 1.5f);
        moveCat.Register(3, speed: 6.0f, radius: 1.5f);

        DetectionSystem detection = new(geometry, perceptCat);
        NavigationGrid navGrid = new(geometry, cellSize: 0.5f);
        NavigationSystem nav = new(navGrid, moveCat);
        CombatSystem combat = new(geometry, combatCat, perceptCat, detection, nav);

        RuleSet rules = new(detection, nav, combat);
        WorldState world = new(geometry.HeightMap);
        Simulation simulation = new(world, rules);

        return (simulation, world, geometry);
    }

    [Fact]
    public void RangeBoundaries_JustInsideHits_JustOutsideHoldsOrder()
    {
        var (sim, world, _) = CreateSetup(weaponRange: 60f);
        using (sim)
        {
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 100, 10);
            EntityId targetInside = world.CreateUnit(2, 1, new Vector2(69f, 10f), 6f, 100, 10); // dist 59m < 60m
            EntityId targetOutside = world.CreateUnit(2, 1, new Vector2(72f, 10f), 6f, 100, 10); // dist 62m > 60m

            sim.Step(); // Detect targets

            Assert.True(sim.Detection!.TryGetContactByEntity(1, targetInside, out ContactObservation cInside));
            Assert.True(sim.Detection!.TryGetContactByEntity(1, targetOutside, out ContactObservation cOutside));

            // Target inside range fires and hits
            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, attacker, cInside.Id)));
            sim.Step();

            world.TryGetCombatState(targetInside, out WorldState.CombatState sInside);
            Assert.Equal(50, sInside.CurrentHealth);
            world.TryGetCombatState(attacker, out WorldState.CombatState sAttacker);
            Assert.Equal(9, sAttacker.Ammunition);

            // Target moves outside range: attacker maintains order but holds fire with OutOfRange status
            world.UpdateUnitNavigation(targetInside, new Vector2(72f, 10f), new Vector2(72f, 10f), false);
            sim.Step();

            world.TryGetCombatState(targetInside, out WorldState.CombatState sOutside);
            Assert.Equal(50, sOutside.CurrentHealth); // Unharmed by new shots
            world.TryGetCombatState(attacker, out sAttacker);
            Assert.True(sAttacker.HasAttackOrder);
            Assert.Equal(9, sAttacker.Ammunition); // Ammo preserved
            Assert.Equal(AttackExecutionStatus.OutOfRange, sAttacker.Status);
        }
    }

    [Fact]
    public void Obstruction_BuildingBlocksFire_And_LowWallAllowsTurretHit()
    {
        OpaqueStructure building = new(
            new StructureId(1), minX: 45f, minY: 0f, minZ: 42f, maxX: 55f, maxY: 6f, maxZ: 58f, isGrounded: true);
        OpaqueStructure lowWall = new(
            new StructureId(2), minX: 30f, minY: 0f, minZ: 15f, maxX: 31f, maxY: 1.0f, maxZ: 35f, isGrounded: true);
        StaticGeometry geometry = new(CreateFlatMap(), [building, lowWall]);

        var (sim, world, _) = CreateSetup(geometry);
        using (sim)
        {
            // Tank behind 1.0m low wall at X=30..31: attacker at (12, 25), target at (33, 25)
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(12f, 25f), 6f, 100, 10);
            EntityId targetLowWall = world.CreateUnit(2, 1, new Vector2(33f, 25f), 6f, 100, 10);

            // Tank behind 6.0m building: attacker at (15, 50), target at (65, 50)
            EntityId targetBuilding = world.CreateUnit(2, 1, new Vector2(65f, 50f), 6f, 100, 10);

            sim.Step();

            Assert.True(sim.Detection!.TryGetContactByEntity(1, targetLowWall, out ContactObservation cLowWall));
            Assert.False(sim.Detection!.TryGetContactByEntity(1, targetBuilding, out _)); // Completely blocked by building

            // Attack over low wall hits turret point
            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, attacker, cLowWall.Id)));
            sim.Step();

            world.TryGetCombatState(targetLowWall, out WorldState.CombatState sLowWall);
            Assert.Equal(50, sLowWall.CurrentHealth);
        }
    }

    [Fact]
    public void VisibleUpperTargetPoint_LowWallBlocksHull_HitsTurret()
    {
        OpaqueStructure lowWall = new(
            new StructureId(1), minX: 30f, minY: 0f, minZ: 15f, maxX: 31f, maxY: 1.0f, maxZ: 35f, isGrounded: true);
        StaticGeometry geometry = new(CreateFlatMap(), [lowWall]);

        Vector3 muzzle = new(12f, 1.9f, 25f);
        Vector3 hullCenter = new(33f, 0.8f, 25f);
        Vector3 turret = new(33f, 1.8f, 25f);

        // Ray to hull point is blocked by 1.0m wall
        Assert.False(geometry.CheckLineOfSight(muzzle, hullCenter).IsClear);
        // Ray to turret point is clear over 1.0m wall
        Assert.True(geometry.CheckLineOfSight(muzzle, turret).IsClear);

        var (sim, world, _) = CreateSetup(geometry);
        using (sim)
        {
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(12f, 25f), 6f, 100, 10);
            EntityId target = world.CreateUnit(2, 1, new Vector2(33f, 25f), 6f, 100, 10);

            sim.Step();
            Assert.True(sim.Detection!.TryGetContactByEntity(1, target, out ContactObservation contact));

            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, attacker, contact.Id)));
            sim.Step();

            world.TryGetCombatState(target, out WorldState.CombatState state);
            Assert.Equal(50, state.CurrentHealth);
        }
    }

    [Fact]
    public void AmmunitionAndReloadTiming_ConsumesAmmo_TicksDownReload_HoldsWhileReloading()
    {
        var (sim, world, _) = CreateSetup(reloadTicks: 40);
        using (sim)
        {
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 100, 2); // 2 rounds
            EntityId target = world.CreateUnit(2, 1, new Vector2(30f, 10f), 6f, 200, 10);

            sim.Step();
            Assert.True(sim.Detection!.TryGetContactByEntity(1, target, out ContactObservation contact));
            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, attacker, contact.Id)));

            // Tick 1: fires first round
            sim.Step();
            world.TryGetCombatState(attacker, out WorldState.CombatState c1);
            Assert.Equal(1, c1.Ammunition);
            Assert.Equal(40, c1.ReloadTicksRemaining);
            world.TryGetCombatState(target, out WorldState.CombatState t1);
            Assert.Equal(150, t1.CurrentHealth);

            // Reloading over 39 ticks: does not fire
            for (int i = 0; i < 39; i++) sim.Step();

            world.TryGetCombatState(attacker, out WorldState.CombatState c2);
            Assert.Equal(1, c2.ReloadTicksRemaining);
            world.TryGetCombatState(target, out WorldState.CombatState t2);
            Assert.Equal(150, t2.CurrentHealth);

            // 40th tick: reload completes and fires second round
            sim.Step();
            world.TryGetCombatState(attacker, out WorldState.CombatState c3);
            Assert.Equal(0, c3.Ammunition); // Out of ammo
            world.TryGetCombatState(target, out WorldState.CombatState t3);
            Assert.Equal(100, t3.CurrentHealth);

            // Subsequent ticks: cannot fire due to 0 ammo
            for (int i = 0; i < 50; i++) sim.Step();
            world.TryGetCombatState(target, out WorldState.CombatState t4);
            Assert.Equal(100, t4.CurrentHealth);
        }
    }

    [Fact]
    public void DamageAppliedOnce_TargetHealthReducedByExactDamage()
    {
        var (sim, world, _) = CreateSetup();
        using (sim)
        {
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 100, 10);
            EntityId target = world.CreateUnit(2, 1, new Vector2(30f, 10f), 6f, 100, 10);

            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(sim.Detection!.TryGetContactByEntity(1, target, out ContactObservation contact));
            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, attacker, contact.Id)));

            sim.Step();
            world.TryGetCombatState(target, out WorldState.CombatState t1);
            Assert.Equal(50, t1.CurrentHealth); // Reduced by exactly 50
        }
    }

    [Fact]
    public void Destruction_RemovesDetectionAndNavigationParticipation()
    {
        var (sim, world, _) = CreateSetup();
        using (sim)
        {
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 100, 10);
            EntityId target = world.CreateUnit(2, 1, new Vector2(30f, 10f), 6f, 50, 10); // 50 HP: destroyed in 1 hit

            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(sim.Detection!.TryGetContactByEntity(1, target, out ContactObservation contact));
            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, attacker, contact.Id)));

            sim.Step(); // Shot fires and destroys target

            // Target removed from WorldState
            Assert.Equal(1, world.Count);
            Assert.False(world.TryGetCombatState(target, out _));

            // Attacker order reset
            world.TryGetCombatState(attacker, out WorldState.CombatState attCombat);
            Assert.Equal(AttackExecutionStatus.None, attCombat.Status);

            for (int i = 0; i < 4; i++) sim.Step(); // Target no longer observed
            Assert.False(sim.Detection!.TryGetContactByEntity(1, target, out _));
        }
    }

    [Fact]
    public void MultipleDestructionsDuringSingleTick_StableDeterministicOrderSortedByEntityId()
    {
        var (sim, world, _) = CreateSetup();
        using (sim)
        {
            // 4 units with 50 HP each, mutually opposing
            EntityId a1 = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 50, 10);
            EntityId b1 = world.CreateUnit(2, 1, new Vector2(20f, 10f), 6f, 50, 10);
            EntityId a2 = world.CreateUnit(1, 1, new Vector2(10f, 20f), 6f, 50, 10);
            EntityId b2 = world.CreateUnit(2, 1, new Vector2(20f, 20f), 6f, 50, 10);

            // Step 4 ticks so all staggered observer phases complete
            for (int i = 0; i < 4; i++) sim.Step();

            Assert.True(sim.Detection!.TryGetContactByEntity(1, b1, out ContactObservation cb1));
            Assert.True(sim.Detection!.TryGetContactByEntity(1, b2, out ContactObservation cb2));
            Assert.True(sim.Detection!.TryGetContactByEntity(2, a1, out ContactObservation ca1));
            Assert.True(sim.Detection!.TryGetContactByEntity(2, a2, out ContactObservation ca2));

            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, a1, cb1.Id)));
            Assert.True(sim.TryEnqueue(1, new AttackCommand(2, sim.Tick, a2, cb2.Id)));
            Assert.True(sim.TryEnqueue(2, new AttackCommand(1, sim.Tick, b1, ca1.Id)));
            Assert.True(sim.TryEnqueue(2, new AttackCommand(2, sim.Tick, b2, ca2.Id)));

            // All 4 destroyed in 1 tick
            sim.Step();
            Assert.Equal(0, world.Count);
        }
    }

    [Fact]
    public void StationaryFiringOnly_MovingAttackerCannotFire()
    {
        var (sim, world, _) = CreateSetup();
        using (sim)
        {
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 100, 10);
            EntityId target = world.CreateUnit(2, 1, new Vector2(30f, 10f), 6f, 100, 10);

            sim.Step();
            Assert.True(sim.Detection!.TryGetContactByEntity(1, target, out ContactObservation contact));

            // Start moving attacker
            Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, attacker, new Vector2(10f, 50f))));
            sim.Step();
            Assert.True(world.TryGetUnit(attacker, out UnitState u1) && u1.Moving);

            // Attack command replaces movement, stops attacker, and fires
            Assert.True(sim.TryEnqueue(1, new AttackCommand(2, sim.Tick, attacker, contact.Id)));
            sim.Step();

            Assert.True(world.TryGetUnit(attacker, out UnitState u2) && !u2.Moving);
            world.TryGetCombatState(target, out WorldState.CombatState t1);
            Assert.Equal(50, t1.CurrentHealth);
        }
    }

    [Fact]
    public void LastKnownTransition_TargetMovingBehindCover_DropsAttackOrder()
    {
        OpaqueStructure building = new(
            new StructureId(1), minX: 45f, minY: 0f, minZ: 42f, maxX: 55f, maxY: 6f, maxZ: 58f, isGrounded: true);
        StaticGeometry geometry = new(CreateFlatMap(), [building]);

        var (sim, world, _) = CreateSetup(geometry);
        using (sim)
        {
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(15f, 30f), 6f, 100, 10);
            EntityId target = world.CreateUnit(2, 1, new Vector2(65f, 30f), 6f, 100, 10);

            sim.Step();
            Assert.True(sim.Detection!.TryGetContactByEntity(1, target, out ContactObservation contact));
            Assert.Equal(ContactState.Current, contact.State);

            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, attacker, contact.Id)));
            sim.Step(); // fires 1st shot

            world.TryGetCombatState(target, out WorldState.CombatState t1);
            Assert.Equal(50, t1.CurrentHealth);

            // Move target behind building to (65, 50)
            world.UpdateUnitNavigation(target, new Vector2(65f, 50f), new Vector2(65f, 50f), false);
            sim.Step();

            // Contact becomes LastKnown
            if (sim.Detection!.TryGetContactByEntity(1, target, out ContactObservation lostContact))
            {
                Assert.Equal(ContactState.LastKnown, lostContact.State);
            }

            // Attacker order dropped
            world.TryGetCombatState(attacker, out WorldState.CombatState attCombat);
            Assert.True(attCombat.Status == AttackExecutionStatus.None || attCombat.Status == AttackExecutionStatus.TargetLost);
        }
    }

    [Fact]
    public void ZeroSteadyStateSimulationAllocations_DuringActiveCombat()
    {
        var (sim, world, _) = CreateSetup();
        using (sim)
        {
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 100, 100);
            EntityId target = world.CreateUnit(2, 1, new Vector2(30f, 10f), 6f, 10000, 100);

            sim.Step();
            Assert.True(sim.Detection!.TryGetContactByEntity(1, target, out ContactObservation contact));
            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, attacker, contact.Id)));

            // Warm up
            for (int i = 0; i < 15; i++) sim.Step();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 60; i++)
            {
                sim.Step();
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0, after - before);
        }
    }

    [Fact]
    public void Destruction_ObservedVictim_RetiresContactImmediately_WhileUnobservedRetainsUncertainty()
    {
        var (sim, world, _) = CreateSetup();
        using (sim)
        {
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 100, 10);
            EntityId target = world.CreateUnit(2, 1, new Vector2(30f, 10f), 6f, 50, 10);

            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(sim.Detection!.TryGetContactByEntity(1, target, out ContactObservation contact));
            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, attacker, contact.Id)));

            sim.Step(); // Shot fires and destroys target

            // Target truth entity is removed
            Assert.False(world.TryGetUnit(target, out _));

            // Observed contact is retired immediately (no lingering LastKnown marker for observing side)
            Assert.False(sim.Detection!.TryGetContactByEntity(1, target, out _));
            ReadOnlySpan<ContactObservation> contacts = sim.Detection.GetContacts(1);
            Assert.Equal(0, contacts.Length);

            // Even 10 ticks later, no phantom contact exists
            for (int i = 0; i < 10; i++) sim.Step();
            Assert.False(sim.Detection.TryGetContactByEntity(1, target, out _));
            Assert.Equal(0, sim.Detection.GetContacts(1).Length);
        }
    }

    [Fact]
    public void SimultaneousVolley_MultipleAttackersOnSingleVictim_EmitsSingleDestructionTransition()
    {
        var (sim, world, _) = CreateSetup();
        using (sim)
        {
            // 3 attackers on side 1, 1 victim on side 2 with 50 HP (50 damage from one hit destroys it)
            EntityId a1 = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 100, 10);
            EntityId a2 = world.CreateUnit(1, 1, new Vector2(10f, 15f), 6f, 100, 10);
            EntityId a3 = world.CreateUnit(1, 1, new Vector2(10f, 20f), 6f, 100, 10);
            EntityId victim = world.CreateUnit(2, 1, new Vector2(30f, 15f), 6f, 50, 10);

            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(sim.Detection!.TryGetContactByEntity(1, victim, out ContactObservation contact));

            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, a1, contact.Id)));
            Assert.True(sim.TryEnqueue(1, new AttackCommand(2, sim.Tick, a2, contact.Id)));
            Assert.True(sim.TryEnqueue(1, new AttackCommand(3, sim.Tick, a3, contact.Id)));

            // Single tick where all 3 fire simultaneously
            sim.Step();

            // TotalUnitsDestroyed must increment exactly once
            Assert.Equal(1, sim.Combat!.TotalUnitsDestroyed);
            Assert.Equal(3, world.Count); // 3 attackers survive, 1 victim removed

            // Drain events: exactly one UnitDestroyed event emitted
            CombatEventObservation[] buffer = new CombatEventObservation[32];
            ReadOnlySpan<CombatEventObservation> p1Events = sim.Combat.DrainEvents(1, buffer);

            int destroyedEvents = 0;
            int shotEvents = 0;
            for (int e = 0; e < p1Events.Length; e++)
            {
                if (p1Events[e].Kind == CombatEventKind.UnitDestroyed) destroyedEvents++;
                if (p1Events[e].Kind == CombatEventKind.ShotFired) shotEvents++;
            }

            Assert.Equal(3, shotEvents);
            Assert.Equal(1, destroyedEvents);
        }
    }

    [Fact]
    public void OrderPreservation_WhenMoveRejectedByCapacityOrObstacle_PreservesActiveAttackOrder()
    {
        OpaqueStructure building = new(
            new StructureId(1), minX: 40f, minY: 0f, minZ: 40f, maxX: 60f, maxY: 6f, maxZ: 60f, isGrounded: true);
        StaticGeometry geo = new(CreateFlatMap(), [building]);
        var (sim, world, _) = CreateSetup(geo);
        using (sim)
        {
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 100, 10);
            EntityId target = world.CreateUnit(2, 1, new Vector2(30f, 10f), 6f, 100, 10);

            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(sim.Detection!.TryGetContactByEntity(1, target, out ContactObservation contact));

            // Establish active attack order
            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, attacker, contact.Id)));
            sim.Step();

            Assert.True(world.TryGetCombatState(attacker, out WorldState.CombatState cs1));
            Assert.True(cs1.HasAttackOrder);
            Assert.Equal(contact.Id, cs1.TargetContactId);

            // Rejection Path 1: Move target deep inside building (InvalidDestination)
            Assert.True(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, attacker, new Vector2(50f, 50f))));
            sim.Step();

            // Navigation status is InvalidDestination, attack order preserved!
            Assert.Equal(PathRequestStatus.InvalidDestination, (sim.Rules as RuleSet)!.FindSystem<NavigationSystem>()!.GetUnitStatus(attacker));
            Assert.True(world.TryGetCombatState(attacker, out WorldState.CombatState cs2));
            Assert.True(cs2.HasAttackOrder);
            Assert.Equal(contact.Id, cs2.TargetContactId);

            // Rejection Path 2: Navigation queue full (CapacityExceeded)
            NavigationSystem nav = (sim.Rules as RuleSet)!.FindSystem<NavigationSystem>()!;
            // Fill request queue
            for (int k = 0; k < nav.Tuning.MaxPendingRequests; k++)
            {
                nav.EnqueueCommand(1, new MoveCommand((uint)(100 + k), sim.Tick, new EntityId((uint)(1000 + k)), new Vector2(15f, 15f)));
            }

            // Issue move command that exceeds capacity
            Assert.True(sim.TryEnqueue(1, new MoveCommand(3, sim.Tick, attacker, new Vector2(12f, 12f))));
            sim.Step();

            Assert.Equal(PathRequestStatus.CapacityExceeded, nav.GetUnitStatus(attacker));
            Assert.True(world.TryGetCombatState(attacker, out WorldState.CombatState cs3));
            Assert.True(cs3.HasAttackOrder);
            Assert.Equal(contact.Id, cs3.TargetContactId);

            // Valid move: accepts and clears attack order (Move -> Attack -> Move sequence)
            nav.ResetPlayer(1);
            Assert.True(sim.TryEnqueue(1, new MoveCommand(4, sim.Tick, attacker, new Vector2(15f, 15f))));
            sim.Step();

            Assert.True(world.TryGetCombatState(attacker, out WorldState.CombatState cs4));
            Assert.False(cs4.HasAttackOrder);
            Assert.Equal(AttackExecutionStatus.None, cs4.Status);
        }
    }

    [Fact]
    public void DestructionEvent_OpponentAndOwnerReceiveKnownVehicleDefinition()
    {
        var (sim, world, _) = CreateSetup();
        using (sim)
        {
            ushort victimDef = 2; // Non-zero definition
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 100, 10);
            EntityId victim = world.CreateUnit(2, victimDef, new Vector2(30f, 10f), 6f, 50, 10);

            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(sim.Detection!.TryGetContactByEntity(1, victim, out ContactObservation contact));
            Assert.Equal(ContactClassification.Vehicle, contact.Classification);
            Assert.Equal(victimDef, contact.Type);

            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, attacker, contact.Id)));
            sim.Step(); // Victim destroyed

            CombatEventObservation[] buffer = new CombatEventObservation[16];

            // Opponent (side 1) observes destruction with known definition (seen means known)
            ReadOnlySpan<CombatEventObservation> oppEvents = sim.Combat!.DrainEvents(1, buffer);
            bool foundOppDestroyed = false;
            for (int i = 0; i < oppEvents.Length; i++)
            {
                if (oppEvents[i].Kind == CombatEventKind.UnitDestroyed)
                {
                    Assert.Equal(victimDef, oppEvents[i].PresentationId);
                    foundOppDestroyed = true;
                }
            }
            Assert.True(foundOppDestroyed);

            // Victim owner (side 2) receives truth definition
            ReadOnlySpan<CombatEventObservation> ownerEvents = sim.Combat.DrainEvents(2, buffer);
            bool foundOwnerDestroyed = false;
            for (int i = 0; i < ownerEvents.Length; i++)
            {
                if (ownerEvents[i].Kind == CombatEventKind.UnitDestroyed)
                {
                    Assert.Equal(victimDef, ownerEvents[i].PresentationId);
                    foundOwnerDestroyed = true;
                }
            }
            Assert.True(foundOwnerDestroyed);
        }
    }

    [Fact]
    public void MultiDestructionTick_ZeroHeapAllocations()
    {
        var (sim, world, _) = CreateSetup();
        using (sim)
        {
            // Setup multiple opposing pairs
            EntityId a1 = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 50, 10);
            EntityId b1 = world.CreateUnit(2, 1, new Vector2(20f, 10f), 6f, 50, 10);
            EntityId a2 = world.CreateUnit(1, 1, new Vector2(10f, 20f), 6f, 50, 10);
            EntityId b2 = world.CreateUnit(2, 1, new Vector2(20f, 20f), 6f, 50, 10);

            for (int i = 0; i < 4; i++) sim.Step();

            Assert.True(sim.Detection!.TryGetContactByEntity(1, b1, out ContactObservation cb1));
            Assert.True(sim.Detection!.TryGetContactByEntity(1, b2, out ContactObservation cb2));
            Assert.True(sim.Detection!.TryGetContactByEntity(2, a1, out ContactObservation ca1));
            Assert.True(sim.Detection!.TryGetContactByEntity(2, a2, out ContactObservation ca2));

            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, a1, cb1.Id)));
            Assert.True(sim.TryEnqueue(1, new AttackCommand(2, sim.Tick, a2, cb2.Id)));
            Assert.True(sim.TryEnqueue(2, new AttackCommand(1, sim.Tick, b1, ca1.Id)));
            Assert.True(sim.TryEnqueue(2, new AttackCommand(2, sim.Tick, b2, ca2.Id)));

            // Warm up GC and JIT
            sim.Step();

            // Re-create pairs for allocation measurement
            EntityId a3 = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 50, 10);
            EntityId b3 = world.CreateUnit(2, 1, new Vector2(20f, 10f), 6f, 50, 10);
            EntityId a4 = world.CreateUnit(1, 1, new Vector2(10f, 20f), 6f, 50, 10);
            EntityId b4 = world.CreateUnit(2, 1, new Vector2(20f, 20f), 6f, 50, 10);

            for (int i = 0; i < 4; i++) sim.Step();

            Assert.True(sim.Detection.TryGetContactByEntity(1, b3, out ContactObservation cb3));
            Assert.True(sim.Detection.TryGetContactByEntity(1, b4, out ContactObservation cb4));
            Assert.True(sim.Detection.TryGetContactByEntity(2, a3, out ContactObservation ca3));
            Assert.True(sim.Detection.TryGetContactByEntity(2, a4, out ContactObservation ca4));

            Assert.True(sim.TryEnqueue(1, new AttackCommand(3, sim.Tick, a3, cb3.Id)));
            Assert.True(sim.TryEnqueue(1, new AttackCommand(4, sim.Tick, a4, cb4.Id)));
            Assert.True(sim.TryEnqueue(2, new AttackCommand(3, sim.Tick, b3, ca3.Id)));
            Assert.True(sim.TryEnqueue(2, new AttackCommand(4, sim.Tick, b4, ca4.Id)));

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            sim.Step(); // Multi-kill tick (4 units destroyed simultaneously)
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0, after - before);
        }
    }

    [Fact]
    public void Validated3DEndpoints_PreserveHeightsInObservationAndRaycast()
    {
        // Wall with height 1.7m between attacker and target
        OpaqueStructure wall = new(
            new StructureId(1), minX: 18f, minY: 0f, minZ: 5f, maxX: 22f, maxY: 1.7f, maxZ: 15f, isGrounded: true);
        StaticGeometry geo = new(CreateFlatMap(), [wall]);

        var (sim, world, _) = CreateSetup(geo);
        using (sim)
        {
            // Attacker muzzle height is 1.9m, target target_point height is 1.8m.
            // Wall is 1.7m. Direct ray passes above 1.7m wall!
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 100, 10);
            EntityId target = world.CreateUnit(2, 1, new Vector2(30f, 10f), 6f, 100, 10);

            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(sim.Detection!.TryGetContactByEntity(1, target, out ContactObservation contact));

            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, attacker, contact.Id)));
            sim.Step();

            CombatEventObservation[] buffer = new CombatEventObservation[16];
            ReadOnlySpan<CombatEventObservation> events = sim.Combat!.DrainEvents(1, buffer);
            Assert.True(events.Length > 0);

            CombatEventObservation shot = events[0];
            Assert.Equal(CombatEventKind.ShotFired, shot.Kind);

            // Exact 3D heights preserved
            Assert.True(MathF.Abs(shot.Origin.Y - 1.9f) < 0.05f);
            Assert.True(MathF.Abs(shot.Target.Y - 1.8f) < 0.05f);

            // Shooter identity associated with shooter's owner
            Assert.Equal(attacker, shot.ShooterUnitId);
        }
    }

    [Fact]
    public void AttackOrder_TargetTemporarilyHiddenInLastKnown_HoldsOrderAndResumesWhenCurrentAgain()
    {
        // Wall between (18, 11) and (22, 15)
        OpaqueStructure wall = new(
            new StructureId(1), minX: 18f, minY: 0f, minZ: 11f, maxX: 22f, maxY: 6f, maxZ: 15f, isGrounded: true);
        StaticGeometry geo = new(CreateFlatMap(), [wall]);

        var (sim, world, _) = CreateSetup(geo, weaponRange: 60f, reloadTicks: 12);
        using (sim)
        {
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 100, 10);
            EntityId target = world.CreateUnit(2, 1, new Vector2(30f, 10f), 6f, 100, 10);

            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(sim.Detection!.TryGetContactByEntity(1, target, out ContactObservation contact));
            Assert.Equal(ContactState.Current, contact.State);

            // Establish attack order and fire initial shot
            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, attacker, contact.Id)));
            sim.Step();

            world.TryGetCombatState(attacker, out WorldState.CombatState cs1);
            Assert.True(cs1.HasAttackOrder);
            Assert.Equal(9, cs1.Ammunition);
            world.TryGetCombatState(target, out WorldState.CombatState ts1);
            Assert.Equal(50, ts1.CurrentHealth);

            // Move target behind wall corner (Z = 12.5) within correlation slack
            world.UpdateUnitNavigation(target, new Vector2(30f, 12.5f), new Vector2(30f, 12.5f), false);
            for (int i = 0; i < 4; i++) sim.Step();

            // Contact transitions to LastKnown
            Assert.True(sim.Detection.TryGetContact(1, contact.Id, out ContactObservation cHidden));
            Assert.Equal(ContactState.LastKnown, cHidden.State);

            // Advance 3 ticks while hidden: order held, fire held
            for (int i = 0; i < 3; i++)
            {
                sim.Step();
                world.TryGetCombatState(attacker, out WorldState.CombatState csHidden);
                Assert.True(csHidden.HasAttackOrder);
                Assert.Equal(AttackExecutionStatus.TargetLost, csHidden.Status);
                Assert.Equal(9, csHidden.Ammunition); // Holds fire
                world.TryGetCombatState(target, out WorldState.CombatState tsHidden);
                Assert.Equal(50, tsHidden.CurrentHealth); // No new damage
            }

            // Move target back into view (Z = 10)
            world.UpdateUnitNavigation(target, new Vector2(30f, 10f), new Vector2(30f, 10f), false);
            for (int i = 0; i < 4; i++) sim.Step();

            Assert.True(sim.Detection.TryGetContactByEntity(1, target, out ContactObservation cVisible));
            Assert.Equal(ContactState.Current, cVisible.State);

            // Attacker automatically resumes firing when contact is Current again
            sim.Step();
            world.TryGetCombatState(attacker, out WorldState.CombatState csResumed);
            Assert.Equal(8, csResumed.Ammunition); // Fired 2nd round
            world.TryGetCombatState(target, out WorldState.CombatState tsResumed);
            Assert.Equal(0, tsResumed.CurrentHealth); // Target destroyed
        }
    }

    [Fact]
    public void AttackOrder_TargetHiddenUntilContactExpires_DropsOrder()
    {
        OpaqueStructure wall = new(
            new StructureId(1), minX: 18f, minY: 0f, minZ: 11f, maxX: 22f, maxY: 6f, maxZ: 15f, isGrounded: true);
        StaticGeometry geo = new(CreateFlatMap(), [wall]);

        var (sim, world, _) = CreateSetup(geo, weaponRange: 60f);
        using (sim)
        {
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 100, 10);
            EntityId target = world.CreateUnit(2, 1, new Vector2(30f, 10f), 6f, 100, 10);

            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(sim.Detection!.TryGetContactByEntity(1, target, out ContactObservation contact));

            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, attacker, contact.Id)));
            sim.Step();

            world.TryGetCombatState(attacker, out WorldState.CombatState cs1);
            Assert.True(cs1.HasAttackOrder);

            // Move target behind wall
            world.UpdateUnitNavigation(target, new Vector2(30f, 12.5f), new Vector2(30f, 12.5f), false);
            for (int i = 0; i < 4; i++) sim.Step();

            // Advance until contact memory duration expires (100 ticks)
            for (int i = 0; i < 110; i++) sim.Step();

            // Contact expired and removed from tracker
            Assert.False(sim.Detection.TryGetContact(1, contact.Id, out _));

            // Attack order was dropped
            world.TryGetCombatState(attacker, out WorldState.CombatState csExpired);
            Assert.False(csExpired.HasAttackOrder);
            Assert.Equal(AttackExecutionStatus.None, csExpired.Status);
        }
    }

    [Fact]
    public void UpdateKnowledge_FreeOfSideEffects_TwoCallsReturnSameEvents()
    {
        var (sim, world, _) = CreateSetup();
        using (sim)
        {
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 100, 10);
            EntityId target = world.CreateUnit(2, 1, new Vector2(30f, 10f), 6f, 100, 10);

            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(sim.Detection!.TryGetContactByEntity(1, target, out ContactObservation contact));

            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, attacker, contact.Id)));
            sim.Step(); // Shot fired

            PlayerKnowledge k1 = new(1);
            PlayerKnowledge k2 = new(1);

            sim.UpdateKnowledge(k1);
            sim.UpdateKnowledge(k2);

            Assert.True(k1.EventCount > 0);
            Assert.Equal(k1.EventCount, k2.EventCount);
            Assert.Equal(k1.Events[0].EventId, k2.Events[0].EventId);
            Assert.Equal(k1.Events[0].Kind, k2.Events[0].Kind);
        }
    }

    [Fact]
    public void AttackOrder_OutOfRange_RejectsOrderWithoutFreezingMovement()
    {
        var (sim, world, _) = CreateSetup(weaponRange: 40f);
        using (sim)
        {
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 100, 10);
            EntityId target = world.CreateUnit(2, 1, new Vector2(70f, 10f), 6f, 100, 10); // dist 60m = 1.5x range

            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(sim.Detection!.TryGetContactByEntity(1, target, out ContactObservation contact));

            // Unit is moving towards (10, 30)
            Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, attacker, new Vector2(10f, 30f))));
            sim.Step();

            Assert.True(world.TryGetUnit(attacker, out UnitState uMoving));
            Assert.True(uMoving.Moving);

            // Issue attack order on contact at 1.5x range
            Assert.True(sim.TryEnqueue(1, new AttackCommand(2, sim.Tick, attacker, contact.Id)));
            sim.Step();

            // Order was rejected: unit does NOT freeze, continues moving!
            world.TryGetCombatState(attacker, out WorldState.CombatState cs);
            Assert.False(cs.HasAttackOrder);
            Assert.Equal(AttackExecutionStatus.None, cs.Status);

            Assert.True(world.TryGetUnit(attacker, out UnitState uStillMoving));
            Assert.True(uStillMoving.Moving);
        }
    }

    [Fact]
    public void TookFire_VictimOfHiddenShooterReceivesTookFireWithCoarseBearingAndHitPoint()
    {
        var (sim, world, _) = CreateSetup(weaponRange: 60f);
        using (sim)
        {
            // Attacker has sensor range 120m and weapon range 60m.
            // Victim is definition 3 with sensor range 20m.
            // At distance 35m, attacker sees victim, but victim CANNOT see attacker!
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 100, 10);
            EntityId victim = world.CreateUnit(2, 3, new Vector2(45f, 10f), 6f, 100, 10);

            for (int i = 0; i < 4; i++) sim.Step();

            // Attacker detects victim
            Assert.True(sim.Detection!.TryGetContactByEntity(1, victim, out ContactObservation cVictim));

            // Victim does NOT observe attacker (out of victim's 20m sensor range)
            Assert.False(sim.Detection.IsEntityObservedBy(2, attacker));

            // Attacker shoots victim
            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, attacker, cVictim.Id)));
            sim.Step();

            // Target took damage
            world.TryGetCombatState(victim, out WorldState.CombatState vcs);
            Assert.Equal(50, vcs.CurrentHealth);

            // Victim owner (player 2) receives event
            ReadOnlySpan<CombatEventObservation> p2Events = sim.Combat!.GetEvents(2);
            Assert.True(p2Events.Length > 0);

            CombatEventObservation ev = p2Events[0];
            Assert.Equal(CombatEventKind.TookFire, ev.Kind);

            // No shooter identity leaked
            Assert.Equal(default, ev.ShooterUnitId);

            // Exact hit point on victim preserved
            Assert.True(MathF.Abs(ev.Target.X - 45f) < 0.1f);
            Assert.True(MathF.Abs(ev.Target.Z - 10f) < 0.1f);

            // Shooter exact position is NOT leaked
            float muzzleDist = Vector3.Distance(ev.Origin, new Vector3(10f, 1.9f, 10f));
            Assert.True(muzzleDist > 5.0f, "Exact shooter origin must not be leaked.");

            // Coarse bearing from victim hit point towards Origin points back towards shooter
            Vector3 incoming = Vector3.Normalize(ev.Origin - ev.Target);
            Vector3 trueDir = Vector3.Normalize(new Vector3(10f, 1.9f, 10f) - ev.Target);
            float dot = Vector3.Dot(new Vector3(incoming.X, 0, incoming.Z), new Vector3(trueDir.X, 0, trueDir.Z));
            Assert.True(dot > 0.95f, "Coarse bearing must be within directional tolerance.");
        }
    }

    [Fact]
    public void CombatSystem_InjectsNavigation_DirectlyClearsNavigationOnDestruction()
    {
        var (sim, world, _) = CreateSetup(weaponRange: 60f);
        using (sim)
        {
            NavigationSystem nav = (sim.Rules as RuleSet)!.FindSystem<NavigationSystem>()!;
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 100, 10);
            EntityId target = world.CreateUnit(2, 1, new Vector2(30f, 10f), 6f, 50, 10); // 50 HP (one shot kill)

            // Target has an active navigation slot
            Assert.True(sim.TryEnqueue(2, new MoveCommand(1, sim.Tick, target, new Vector2(30f, 20f))));
            for (int i = 0; i < 4; i++) sim.Step();

            Assert.True(nav.GetUnitStatus(target) != PathRequestStatus.None);
            Assert.True(sim.Detection!.TryGetContactByEntity(1, target, out ContactObservation contact));

            // Attacker destroys target in single shot
            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, attacker, contact.Id)));
            sim.Step();

            // Target is removed and navigation slot is cleared immediately on destruction tick
            Assert.False(world.TryGetUnit(target, out _));
            Assert.Equal(PathRequestStatus.None, nav.GetUnitStatus(target));
        }
    }

    [Fact]
    public void AttackOrder_OutOfRangeReplacement_PreservesExistingActiveAttackOrder()
    {
        var (sim, world, _) = CreateSetup(weaponRange: 40f, reloadTicks: 10, damage: 25);
        using (sim)
        {
            EntityId attacker = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 100, 10);
            EntityId nearTarget = world.CreateUnit(2, 1, new Vector2(30f, 10f), 6f, 100, 10); // dist 20m <= 40m
            EntityId farTarget = world.CreateUnit(2, 1, new Vector2(70f, 10f), 6f, 100, 10);  // dist 60m > 40m

            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(sim.Detection!.TryGetContactByEntity(1, nearTarget, out ContactObservation cNear));
            Assert.True(sim.Detection.TryGetContactByEntity(1, farTarget, out ContactObservation cFar));

            // Establish valid attack order on near target
            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, attacker, cNear.Id)));
            sim.Step();

            world.TryGetCombatState(attacker, out WorldState.CombatState csInitial);
            Assert.True(csInitial.HasAttackOrder);
            Assert.Equal(cNear.Id, csInitial.TargetContactId);
            Assert.Equal(9, csInitial.Ammunition);
            world.TryGetCombatState(nearTarget, out WorldState.CombatState tsNear1);
            Assert.Equal(75, tsNear1.CurrentHealth);

            // Attempt to replace with out-of-range target
            Assert.True(sim.TryEnqueue(1, new AttackCommand(2, sim.Tick, attacker, cFar.Id)));
            sim.Step();

            // Command was rejected: attacker PRESERVES previous valid order on near target
            world.TryGetCombatState(attacker, out WorldState.CombatState csPreserved);
            Assert.True(csPreserved.HasAttackOrder);
            Assert.Equal(cNear.Id, csPreserved.TargetContactId);
            Assert.NotEqual(AttackExecutionStatus.OutOfRange, csPreserved.Status);

            // Attacker continues cycling and fires next shot at near target when reloaded
            for (int i = 0; i < 10; i++) sim.Step();
            world.TryGetCombatState(attacker, out WorldState.CombatState csFiredAgain);
            Assert.Equal(8, csFiredAgain.Ammunition);
            world.TryGetCombatState(nearTarget, out WorldState.CombatState tsNear2);
            Assert.Equal(50, tsNear2.CurrentHealth);
        }
    }

    [Fact]
    public void AttackOrder_TargetKilledWhileUnobservedInLastKnown_PreservesOrderUntilContactExpiry()
    {
        // Wall between (18, 11) and (22, 15)
        OpaqueStructure wall = new(
            new StructureId(1), minX: 18f, minY: 0f, minZ: 11f, maxX: 22f, maxY: 6f, maxZ: 15f, isGrounded: true);
        StaticGeometry geo = new(CreateFlatMap(), [wall]);

        var (sim, world, _) = CreateSetup(geo, weaponRange: 60f, reloadTicks: 10, damage: 50);
        using (sim)
        {
            // Player 1 attacker at (10, 10)
            EntityId p1Attacker = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f, 100, 10);
            // Player 2 target at (30, 10)
            EntityId p2Target = world.CreateUnit(2, 1, new Vector2(30f, 10f), 6f, 100, 10);
            // Player 3 killer at (30, 20) with direct unblocked view of (30, 12.5)
            EntityId p3Killer = world.CreateUnit(3, 1, new Vector2(30f, 20f), 6f, 100, 10);

            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(sim.Detection!.TryGetContactByEntity(1, p2Target, out ContactObservation p1Contact));
            Assert.True(sim.Detection.TryGetContactByEntity(3, p2Target, out ContactObservation p3Contact));

            // Player 1 attacks Player 2 target and fires initial shot
            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, p1Attacker, p1Contact.Id)));
            sim.Step();

            world.TryGetCombatState(p2Target, out WorldState.CombatState ts1);
            Assert.Equal(50, ts1.CurrentHealth); // Damaged to 50 HP

            // Consume Player 1's own initial shot event
            PlayerKnowledge k1Init = new(1);
            sim.UpdateKnowledge(k1Init);
            Assert.Equal(1, k1Init.Events.Length);
            Assert.Equal(CombatEventKind.ShotFired, k1Init.Events[0].Kind);
            sim.AcknowledgeCombatEvents(1, 1);

            // Player 2 moves behind wall (Z = 12.5) - hidden from Player 1, but in direct view of Player 3
            world.UpdateUnitNavigation(p2Target, new Vector2(30f, 12.5f), new Vector2(30f, 12.5f), false);
            for (int i = 0; i < 4; i++) sim.Step();

            // For Player 1, contact transitioned to LastKnown
            Assert.True(sim.Detection.TryGetContact(1, p1Contact.Id, out ContactObservation cLastKnown));
            Assert.Equal(ContactState.LastKnown, cLastKnown.State);

            // Player 3 orders attack and destroys Player 2
            Assert.True(sim.TryEnqueue(3, new AttackCommand(1, sim.Tick, p3Killer, p3Contact.Id)));
            sim.Step();

            // Player 2 unit is destroyed and removed from truth world
            Assert.False(world.TryGetUnit(p2Target, out _));

            // Player 1 did NOT observe the destruction:
            // 1. Player 1 receives NO combat events
            PlayerKnowledge k1 = new(1);
            sim.UpdateKnowledge(k1);
            Assert.Equal(0, k1.Events.Length);

            // 2. Player 1 STILL holds its LastKnown contact marker
            Assert.True(sim.Detection.TryGetContact(1, p1Contact.Id, out ContactObservation cStillLastKnown));
            Assert.Equal(ContactState.LastKnown, cStillLastKnown.State);

            // 3. Player 1's attacker still retains its attack order (TargetLost, holding fire) - NO leak of death
            world.TryGetCombatState(p1Attacker, out WorldState.CombatState csHidden);
            Assert.True(csHidden.HasAttackOrder);
            Assert.Equal(p1Contact.Id, csHidden.TargetContactId);
            Assert.Equal(AttackExecutionStatus.TargetLost, csHidden.Status);

            // Advance until contact memory duration expires (100 ticks)
            for (int i = 0; i < 110; i++) sim.Step();

            // Now contact is expired and removed from Player 1's tracker
            Assert.False(sim.Detection.TryGetContact(1, p1Contact.Id, out _));

            // Only AFTER contact expiry is Player 1's attack order cleared
            world.TryGetCombatState(p1Attacker, out WorldState.CombatState csExpired);
            Assert.False(csExpired.HasAttackOrder);
            Assert.Equal(AttackExecutionStatus.None, csExpired.Status);
        }
    }
}
