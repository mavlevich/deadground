using System.Numerics;
using DeadGround.Engine;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class AutoFireTargetChoiceTests
{
    private static (Simulation Sim, WorldState World, OpsRules Rules, UnitCatalog Catalog) CreateTestContext()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        const int size = 201;
        HeightMap map = new(size, size, 10f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        OpsRules rules = new(geo, catalog, null);
        WorldState world = new(map);
        Simulation sim = new(world, rules, matchSeed: 1337UL);
        return (sim, world, rules, catalog);
    }

    [Fact]
    public void TankEngagingApc_SwitchesToEnemyTankWhenAppearing()
    {
        var (sim, world, rules, catalog) = CreateTestContext();
        using (sim)
        {
            UnitDefinition t62Def = catalog.RequireDefinition("t62");
            ushort t62Idx = catalog.RequireDefinitionIndex("t62");
            UnitDefinition m113Def = catalog.RequireDefinition("m113a1");
            ushort m113Idx = catalog.RequireDefinitionIndex("m113a1");
            UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
            ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");

            int t62Ammo = catalog.GetAmmoCapacity(t62Def);

            // Player 1: T-62 at (200, 500) facing east toward enemy
            EntityId shooter = world.CreateUnit(1, t62Idx, new Vector2(200f, 500f), t62Def.Speed, t62Def.MaxHealth, t62Ammo, initialTurretYaw: -MathF.PI / 2f);
            world.SetFireStance(shooter, FireStance.HoldFire);

            // Player 2: M113A1 (APC) at (400, 500) with 2000 HP so it survives shots
            EntityId apc = world.CreateUnit(2, m113Idx, new Vector2(400f, 500f), m113Def.Speed, 2000, 0);
            world.SetFireStance(apc, FireStance.HoldFire);

            // Warm up detection
            for (int i = 0; i < 4; i++) sim.Step();

            DetectionSystem detection = rules.FindSystem<DetectionSystem>()!;
            Assert.True(detection.TryGetContactByEntity(1, apc, out ContactObservation cApc));

            // Switch shooter to FireAtWill: auto-fire engages the APC
            world.SetFireStance(shooter, FireStance.FireAtWill);
            sim.Step();

            Assert.True(world.TryGetCombatState(shooter, out WorldState.CombatState cs));
            Assert.Equal(cApc.Id, cs.TargetContactId);
            Assert.Equal(apc, cs.TargetEntityId);

            // Spawn Player 2 M60A1 (Tank) at (450, 500) with 2000 HP
            EntityId enemyTank = world.CreateUnit(2, m60Idx, new Vector2(450f, 500f), m60Def.Speed, 2000, 0);
            world.SetFireStance(enemyTank, FireStance.HoldFire);

            // Warm up detection so M60A1 is spotted
            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(detection.TryGetContactByEntity(1, enemyTank, out ContactObservation cTank));

            // Ensure weapon is ready to fire
            world.TryGetCombatState(shooter, out cs);
            cs.ReloadTicksRemaining = 0;
            cs.HasSwitchedTargetThisReload = false;
            world.SetCombatState(shooter, cs);

            // Step simulation: shooter should re-evaluate and switch to the higher priority Tank
            sim.Step();

            Assert.True(world.TryGetCombatState(shooter, out cs));
            Assert.Equal(cTank.Id, cs.TargetContactId);
            Assert.Equal(enemyTank, cs.TargetEntityId);
            Assert.Equal(1, cs.ConsecutiveShotsOnTarget); // reset to 0, then incremented on firing at enemy tank
        }
    }

    [Fact]
    public void NoSwitch_BetweenEqualPriorityTargets_PreventsFlipFlopping()
    {
        var (sim, world, rules, catalog) = CreateTestContext();
        using (sim)
        {
            UnitDefinition t62Def = catalog.RequireDefinition("t62");
            ushort t62Idx = catalog.RequireDefinitionIndex("t62");
            UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
            ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");

            int t62Ammo = catalog.GetAmmoCapacity(t62Def);

            // Player 1: T-62 at (200, 500) facing east
            EntityId shooter = world.CreateUnit(1, t62Idx, new Vector2(200f, 500f), t62Def.Speed, t62Def.MaxHealth, t62Ammo, initialTurretYaw: -MathF.PI / 2f);
            world.SetFireStance(shooter, FireStance.HoldFire);

            // Player 2: Tank A at (400, 500) (dist 200m) with 2000 HP
            EntityId tankA = world.CreateUnit(2, m60Idx, new Vector2(400f, 500f), m60Def.Speed, 2000, 0);
            world.SetFireStance(tankA, FireStance.HoldFire);

            for (int i = 0; i < 4; i++) sim.Step();

            DetectionSystem detection = rules.FindSystem<DetectionSystem>()!;
            Assert.True(detection.TryGetContactByEntity(1, tankA, out ContactObservation cTankA));

            world.SetFireStance(shooter, FireStance.FireAtWill);
            sim.Step();

            Assert.True(world.TryGetCombatState(shooter, out WorldState.CombatState cs));
            Assert.Equal(cTankA.Id, cs.TargetContactId);

            // Spawn Tank B at (300, 500) (closer, dist 100m!) with 2000 HP
            EntityId tankB = world.CreateUnit(2, m60Idx, new Vector2(300f, 500f), m60Def.Speed, 2000, 0);
            world.SetFireStance(tankB, FireStance.HoldFire);

            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(detection.TryGetContactByEntity(1, tankB, out ContactObservation cTankB));

            // Ensure weapon is ready to fire
            world.TryGetCombatState(shooter, out cs);
            cs.ReloadTicksRemaining = 0;
            cs.HasSwitchedTargetThisReload = false;
            world.SetCombatState(shooter, cs);

            // Step simulation: should NOT switch to Tank B because both are tanks (equal priority)
            sim.Step();

            Assert.True(world.TryGetCombatState(shooter, out cs));
            Assert.Equal(cTankA.Id, cs.TargetContactId);
            Assert.Equal(tankA, cs.TargetEntityId);
        }
    }

    [Fact]
    public void AtMostOneSwitchPerReload_LimitsSwitchingUntilReloadCycles()
    {
        var (sim, world, rules, catalog) = CreateTestContext();
        using (sim)
        {
            UnitDefinition t62Def = catalog.RequireDefinition("t62");
            ushort t62Idx = catalog.RequireDefinitionIndex("t62");
            UnitDefinition m151Def = catalog.RequireDefinition("m151a2");
            ushort m151Idx = catalog.RequireDefinitionIndex("m151a2");
            UnitDefinition m113Def = catalog.RequireDefinition("m113a1");
            ushort m113Idx = catalog.RequireDefinitionIndex("m113a1");
            UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
            ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");

            int t62Ammo = catalog.GetAmmoCapacity(t62Def);

            // Player 1: T-62 at (200, 500) facing east (-MathF.PI / 2)
            EntityId shooter = world.CreateUnit(1, t62Idx, new Vector2(200f, 500f), t62Def.Speed, t62Def.MaxHealth, t62Ammo, initialTurretYaw: -MathF.PI / 2f);
            world.SetFireStance(shooter, FireStance.HoldFire);

            // Player 2: Recon (M151A2, priority 2) at (400, 500) with 2000 HP
            EntityId recon = world.CreateUnit(2, m151Idx, new Vector2(400f, 500f), m151Def.Speed, 2000, 0);
            world.SetFireStance(recon, FireStance.HoldFire);

            for (int i = 0; i < 4; i++) sim.Step();

            DetectionSystem detection = rules.FindSystem<DetectionSystem>()!;
            Assert.True(detection.TryGetContactByEntity(1, recon, out ContactObservation cRecon));

            world.SetFireStance(shooter, FireStance.FireAtWill);
            sim.Step();

            Assert.True(world.TryGetCombatState(shooter, out WorldState.CombatState cs));
            Assert.Equal(cRecon.Id, cs.TargetContactId);

            // Spawn APC (M113A1, priority 1) off-axis at (400, 700) so turret must traverse
            EntityId apc = world.CreateUnit(2, m113Idx, new Vector2(400f, 700f), m113Def.Speed, 2000, 0);
            world.SetFireStance(apc, FireStance.HoldFire);

            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(detection.TryGetContactByEntity(1, apc, out ContactObservation cApc));

            // Ensure shooter is ready to fire
            world.TryGetCombatState(shooter, out cs);
            cs.ReloadTicksRemaining = 0;
            cs.HasSwitchedTargetThisReload = false;
            world.SetCombatState(shooter, cs);

            // Step: switches from Recon to APC. Because APC is at (400, 700), turret is not aligned yet so it does not fire yet.
            sim.Step();

            Assert.True(world.TryGetCombatState(shooter, out cs));
            Assert.Equal(cApc.Id, cs.TargetContactId);
            Assert.Equal(apc, cs.TargetEntityId);
            Assert.True(cs.HasSwitchedTargetThisReload);

            // Spawn Tank (M60A1, priority 0) at (400, 800) with 2000 HP
            EntityId tank = world.CreateUnit(2, m60Idx, new Vector2(400f, 800f), m60Def.Speed, 2000, 0);
            world.SetFireStance(tank, FireStance.HoldFire);

            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(detection.TryGetContactByEntity(1, tank, out ContactObservation cTank));

            // While still in the same reload cycle (turret still traversing, ReloadTicksRemaining == 0, HasSwitchedTargetThisReload == true):
            sim.Step();

            // Shooter must NOT switch to the Tank because it already switched once this reload cycle
            Assert.True(world.TryGetCombatState(shooter, out cs));
            Assert.Equal(cApc.Id, cs.TargetContactId);
            Assert.Equal(apc, cs.TargetEntityId);

            // Now simulate reload cycle reset: shot fired and reload completes
            cs.ReloadTicksRemaining = 0;
            cs.HasSwitchedTargetThisReload = false;
            world.SetCombatState(shooter, cs);

            // In the new reload cycle, shooter CAN switch to the higher priority Tank
            sim.Step();

            Assert.True(world.TryGetCombatState(shooter, out cs));
            Assert.Equal(cTank.Id, cs.TargetContactId);
            Assert.Equal(tank, cs.TargetEntityId);
        }
    }

    [Fact]
    public void ExplicitAttackOrders_KeepTheirTarget_NeverSwitchToHigherPriorityTarget()
    {
        var (sim, world, rules, catalog) = CreateTestContext();
        using (sim)
        {
            UnitDefinition t62Def = catalog.RequireDefinition("t62");
            ushort t62Idx = catalog.RequireDefinitionIndex("t62");
            UnitDefinition m113Def = catalog.RequireDefinition("m113a1");
            ushort m113Idx = catalog.RequireDefinitionIndex("m113a1");
            UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
            ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");

            int t62Ammo = catalog.GetAmmoCapacity(t62Def);

            // Player 1: T-62 at (200, 500) facing east
            EntityId shooter = world.CreateUnit(1, t62Idx, new Vector2(200f, 500f), t62Def.Speed, t62Def.MaxHealth, t62Ammo, initialTurretYaw: -MathF.PI / 2f);
            world.SetFireStance(shooter, FireStance.HoldFire);

            // Player 2: APC (M113A1) at (400, 500) with 2000 HP
            EntityId apc = world.CreateUnit(2, m113Idx, new Vector2(400f, 500f), m113Def.Speed, 2000, 0);
            world.SetFireStance(apc, FireStance.HoldFire);

            for (int i = 0; i < 4; i++) sim.Step();

            DetectionSystem detection = rules.FindSystem<DetectionSystem>()!;
            Assert.True(detection.TryGetContactByEntity(1, apc, out ContactObservation cApc));

            // Issue explicit attack order on the APC
            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, shooter, cApc.Id)));
            sim.Step();

            Assert.True(world.TryGetCombatState(shooter, out WorldState.CombatState cs));
            Assert.True(cs.HasAttackOrder);
            Assert.Equal(cApc.Id, cs.TargetContactId);
            Assert.Equal(apc, cs.TargetEntityId);

            // Spawn Tank (M60A1, priority 0) in plain sight at (350, 500) with 2000 HP
            EntityId tank = world.CreateUnit(2, m60Idx, new Vector2(350f, 500f), m60Def.Speed, 2000, 0);
            world.SetFireStance(tank, FireStance.HoldFire);

            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(detection.TryGetContactByEntity(1, tank, out ContactObservation cTank));

            // Step across several ticks with weapon ready to fire
            for (int step = 0; step < 5; step++)
            {
                world.TryGetCombatState(shooter, out cs);
                cs.ReloadTicksRemaining = 0;
                world.SetCombatState(shooter, cs);
                sim.Step();

                // Order must remain on the ordered APC target
                Assert.True(world.TryGetCombatState(shooter, out cs));
                Assert.True(cs.HasAttackOrder);
                Assert.Equal(cApc.Id, cs.TargetContactId);
                Assert.Equal(apc, cs.TargetEntityId);
            }
        }
    }

    [Fact]
    public void TargetChoice_UsesObservedDataOnly_NeverWorldTruth()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        const int size = 201;
        HeightMap map = new(size, size, 10f, 0.01f, new ushort[size * size]);

        // Place a wide opaque wall blocking line of sight between (200, 200) and (300, 500)
        // Segment from (200, 200) to (300, 500) crosses x=250 at z=350. Wall covers z from 300 to 600.
        OpaqueStructure wall = new(
            new StructureId(1),
            minX: 245f, minY: 0f, minZ: 300f,
            maxX: 255f, maxY: 50f, maxZ: 600f,
            isGrounded: true);
        StaticGeometry geo = new(map, [wall]);
        OpsRules rules = new(geo, catalog, null);
        WorldState world = new(map);
        using Simulation sim = new(world, rules, matchSeed: 1337UL);

        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        UnitDefinition m113Def = catalog.RequireDefinition("m113a1");
        ushort m113Idx = catalog.RequireDefinitionIndex("m113a1");
        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");

        int t62Ammo = catalog.GetAmmoCapacity(t62Def);

        // Player 1: T-62 at (200, 200) facing east. Line of sight to (400, 200) is clear of the wall (z=200).
        EntityId shooter = world.CreateUnit(1, t62Idx, new Vector2(200f, 200f), t62Def.Speed, t62Def.MaxHealth, t62Ammo, initialTurretYaw: -MathF.PI / 2f);
        world.SetFireStance(shooter, FireStance.HoldFire);

        // Player 2: APC (M113A1) at (400, 200) in clear line of sight (z=200 is north of wall)
        EntityId apc = world.CreateUnit(2, m113Idx, new Vector2(400f, 200f), m113Def.Speed, 2000, 0);
        world.SetFireStance(apc, FireStance.HoldFire);

        // Player 2: Tank (M60A1) at (300, 500) behind the wall
        // Line from (200, 200) to (300, 500) crosses x=250 at z=350, which is inside wall [300, 600]
        EntityId hiddenTank = world.CreateUnit(2, m60Idx, new Vector2(300f, 500f), m60Def.Speed, 2000, 0);
        world.SetFireStance(hiddenTank, FireStance.HoldFire);

        for (int i = 0; i < 4; i++) sim.Step();

        DetectionSystem detection = rules.FindSystem<DetectionSystem>()!;
        Assert.True(detection.TryGetContactByEntity(1, apc, out ContactObservation cApc));
        // Verify hidden tank has NO contact in player 1's tracker
        Assert.False(detection.TryGetContactByEntity(1, hiddenTank, out _));

        // Switch shooter to FireAtWill
        world.SetFireStance(shooter, FireStance.FireAtWill);
        sim.Step();

        // Shooter must engage the visible APC and NOT the hidden tank
        Assert.True(world.TryGetCombatState(shooter, out WorldState.CombatState cs));
        Assert.Equal(cApc.Id, cs.TargetContactId);
        Assert.Equal(apc, cs.TargetEntityId);

        // Advance simulation with weapon ready: shooter still never targets the unobserved tank
        world.TryGetCombatState(shooter, out cs);
        cs.ReloadTicksRemaining = 0;
        world.SetCombatState(shooter, cs);
        sim.Step();

        Assert.True(world.TryGetCombatState(shooter, out cs));
        Assert.Equal(cApc.Id, cs.TargetContactId);
        Assert.Equal(apc, cs.TargetEntityId);
    }

    [Fact]
    public void AutoFire_WhenAmmunitionExhausted_DoesNotFireExtraShotsOrDealDamage_AcrossMultipleReloadPeriods()
    {
        var (sim, world, rules, catalog) = CreateTestContext();
        using (sim)
        {
            UnitDefinition t62Def = catalog.RequireDefinition("t62");
            ushort t62Idx = catalog.RequireDefinitionIndex("t62");
            UnitDefinition m113Def = catalog.RequireDefinition("m113a1");
            ushort m113Idx = catalog.RequireDefinitionIndex("m113a1");

            CombatSystem combat = rules.FindSystem<CombatSystem>()!;

            // T-62 at (200, 500) with exactly 1 round of ammo, facing east toward target
            EntityId shooter = world.CreateUnit(1, t62Idx, new Vector2(200f, 500f), t62Def.Speed, t62Def.MaxHealth, 1, initialTurretYaw: -MathF.PI / 2f);
            world.SetFireStance(shooter, FireStance.HoldFire);

            // Target M113A1 at (400, 500) with 60000 HP to survive without destruction
            EntityId target = world.CreateUnit(2, m113Idx, new Vector2(400f, 500f), m113Def.Speed, 60000, 0);
            world.SetFireStance(target, FireStance.HoldFire);

            // Warm up detection
            for (int i = 0; i < 4; i++) sim.Step();

            // Enable FireAtWill
            world.SetFireStance(shooter, FireStance.FireAtWill);

            // Step until first shot is executed
            int maxWarmTicks = 20;
            while (combat.TotalShotsFired == 0 && maxWarmTicks-- > 0)
            {
                sim.Step();
            }
            Assert.True(maxWarmTicks > 0, "Timed out waiting for first shot to execute.");

            Assert.Equal(1, combat.TotalShotsFired);
            Assert.True(world.TryGetCombatState(shooter, out WorldState.CombatState cs));
            Assert.Equal(0, cs.Ammunition);

            int damageAfterFirstShot = combat.TotalDamageApplied;
            ulong rngStateAfterFirstShot = combat.RandomStream.State;
            int initialRanging = cs.ConsecutiveShotsOnTarget;

            // Advance simulation for 600 ticks (over 3 full reload cycles for T-62 which is 170 ticks)
            for (int i = 0; i < 600; i++)
            {
                sim.Step();
            }

            // Verify no additional shots were fired, no damage dealt, no RNG consumed, and status is NoAmmo
            Assert.Equal(1, combat.TotalShotsFired);
            Assert.Equal(damageAfterFirstShot, combat.TotalDamageApplied);
            Assert.Equal(rngStateAfterFirstShot, combat.RandomStream.State);

            Assert.True(world.TryGetCombatState(shooter, out cs));
            Assert.Equal(AttackExecutionStatus.NoAmmo, cs.Status);
            Assert.Equal(initialRanging, cs.ConsecutiveShotsOnTarget);
        }
    }

    [Fact]
    public void ReturnFire_WhenAmmunitionExhausted_DoesNotFireExtraShotsOrDealDamage()
    {
        var (sim, world, rules, catalog) = CreateTestContext();
        using (sim)
        {
            UnitDefinition t62Def = catalog.RequireDefinition("t62");
            ushort t62Idx = catalog.RequireDefinitionIndex("t62");
            UnitDefinition m113Def = catalog.RequireDefinition("m113a1");
            ushort m113Idx = catalog.RequireDefinitionIndex("m113a1");

            CombatSystem combat = rules.FindSystem<CombatSystem>()!;

            // T-62 at (200, 500) with exactly 1 round of ammo in ReturnFire stance
            EntityId shooter = world.CreateUnit(1, t62Idx, new Vector2(200f, 500f), t62Def.Speed, t62Def.MaxHealth, 1, initialTurretYaw: -MathF.PI / 2f);
            world.SetFireStance(shooter, FireStance.ReturnFire);

            // Target M113A1 at (400, 500) with 60000 HP
            EntityId target = world.CreateUnit(2, m113Idx, new Vector2(400f, 500f), m113Def.Speed, 60000, 0);
            world.SetFireStance(target, FireStance.HoldFire);

            // Warm up detection
            for (int i = 0; i < 4; i++) sim.Step();

            // Simulate took fire from target
            world.TryGetCombatState(shooter, out WorldState.CombatState cs);
            cs.LastTookFireAttacker = target;
            cs.LastTookFireTick = 4;
            world.SetCombatState(shooter, cs);

            // Step until first shot is executed
            int maxWarmTicks = 20;
            while (combat.TotalShotsFired == 0 && maxWarmTicks-- > 0)
            {
                sim.Step();
            }
            Assert.True(maxWarmTicks > 0, "Timed out waiting for first shot to execute.");

            Assert.Equal(1, combat.TotalShotsFired);
            Assert.True(world.TryGetCombatState(shooter, out cs));
            Assert.Equal(0, cs.Ammunition);

            int damageAfterFirstShot = combat.TotalDamageApplied;
            ulong rngStateAfterFirstShot = combat.RandomStream.State;

            // Advance simulation for 600 ticks
            for (int i = 0; i < 600; i++)
            {
                sim.Step();
            }

            Assert.Equal(1, combat.TotalShotsFired);
            Assert.Equal(damageAfterFirstShot, combat.TotalDamageApplied);
            Assert.Equal(rngStateAfterFirstShot, combat.RandomStream.State);

            Assert.True(world.TryGetCombatState(shooter, out cs));
            Assert.Equal(AttackExecutionStatus.NoAmmo, cs.Status);
        }
    }

    [Fact]
    public void TargetSwitchBudget_ResetsNaturallyThroughOrganicShotAndReloadCycle()
    {
        var (sim, world, rules, catalog) = CreateTestContext();
        using (sim)
        {
            UnitDefinition t62Def = catalog.RequireDefinition("t62");
            ushort t62Idx = catalog.RequireDefinitionIndex("t62");
            UnitDefinition brdmDef = catalog.RequireDefinition("brdm2");
            ushort brdmIdx = catalog.RequireDefinitionIndex("brdm2");
            UnitDefinition m113Def = catalog.RequireDefinition("m113a1");
            ushort m113Idx = catalog.RequireDefinitionIndex("m113a1");
            UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
            ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");

            CombatSystem combat = rules.FindSystem<CombatSystem>()!;
            DetectionSystem detection = rules.FindSystem<DetectionSystem>()!;

            // Shooter T-62 at (200, 500) facing east (-PI/2) toward initial target
            EntityId shooter = world.CreateUnit(1, t62Idx, new Vector2(200f, 500f), t62Def.Speed, t62Def.MaxHealth, 10, initialTurretYaw: -MathF.PI / 2f);
            world.SetFireStance(shooter, FireStance.HoldFire);

            // Initial Target: BRDM-2 (Recon, priority 2) at (400, 500) with 60000 HP
            EntityId recon = world.CreateUnit(2, brdmIdx, new Vector2(400f, 500f), brdmDef.Speed, 60000, 0);
            world.SetFireStance(recon, FireStance.HoldFire);

            // Warm up detection
            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(detection.TryGetContactByEntity(1, recon, out ContactObservation cRecon));

            // Enable FireAtWill: shooter locks onto BRDM-2 and fires first shot
            world.SetFireStance(shooter, FireStance.FireAtWill);
            sim.Step();

            Assert.Equal(1, combat.TotalShotsFired);
            Assert.True(world.TryGetCombatState(shooter, out WorldState.CombatState cs));
            Assert.Equal(cRecon.Id, cs.TargetContactId);
            Assert.False(cs.HasSwitchedTargetThisReload);
            Assert.True(cs.ReloadTicksRemaining > 0);

            // Let shooter reload naturally almost to completion (stop when 1 tick remaining)
            int maxInitialReloadTicks = 200;
            while (cs.ReloadTicksRemaining > 1 && maxInitialReloadTicks-- > 0)
            {
                sim.Step();
                world.TryGetCombatState(shooter, out cs);
            }
            Assert.True(maxInitialReloadTicks > 0, "Timed out waiting for initial reload countdown.");

            // Spawn higher priority target: M113A1 (APC, priority 1) at (400, 700)
            // Offset requires turret traverse so shooter won't fire instantly
            EntityId apc = world.CreateUnit(2, m113Idx, new Vector2(400f, 700f), m113Def.Speed, 60000, 0);
            world.SetFireStance(apc, FireStance.HoldFire);
            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(detection.TryGetContactByEntity(1, apc, out ContactObservation cApc));

            // Reload finishes (ReloadTicksRemaining == 0) and shooter re-evaluates:
            // switches target from Recon (pri 2) to APC (pri 1) and begins traversing turret!
            world.TryGetCombatState(shooter, out cs);
            int maxSwitchTicks = 50;
            while (cs.TargetContactId != cApc.Id && maxSwitchTicks-- > 0)
            {
                sim.Step();
                world.TryGetCombatState(shooter, out cs);
            }
            Assert.True(maxSwitchTicks > 0, "Timed out waiting for shooter to switch target to APC.");

            Assert.Equal(cApc.Id, cs.TargetContactId);
            Assert.True(cs.HasSwitchedTargetThisReload);

            // Now introduce Tank: M60A1 (Tank, priority 0) at (400, 800) during the same reload cycle
            EntityId tank = world.CreateUnit(2, m60Idx, new Vector2(400f, 800f), m60Def.Speed, 60000, 0);
            world.SetFireStance(tank, FireStance.HoldFire);
            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(detection.TryGetContactByEntity(1, tank, out ContactObservation cTank));

            // In the same reload cycle, shooter CANNOT switch to the Tank because switch budget is 1
            Assert.True(world.TryGetCombatState(shooter, out cs));
            Assert.Equal(cApc.Id, cs.TargetContactId);
            Assert.True(cs.HasSwitchedTargetThisReload);

            // Now let the shot naturally fire at the APC and naturally reload to completion
            int shotsBefore = combat.TotalShotsFired;
            int maxFireTicks = 100;
            while (combat.TotalShotsFired == shotsBefore && maxFireTicks-- > 0)
            {
                sim.Step();
            }
            Assert.True(maxFireTicks > 0, "Timed out waiting for shot to naturally fire at APC.");

            // Shot was fired naturally at the APC. In ExecuteShot, HasSwitchedTargetThisReload was reset to false
            // and ReloadTicksRemaining was set to weapon reload duration.
            Assert.True(world.TryGetCombatState(shooter, out cs));
            Assert.False(cs.HasSwitchedTargetThisReload);
            Assert.True(cs.ReloadTicksRemaining > 0);

            // Advance simulation naturally until the reload duration completes
            int maxReloadTicks = 250;
            while (cs.ReloadTicksRemaining > 0 && maxReloadTicks-- > 0)
            {
                sim.Step();
                world.TryGetCombatState(shooter, out cs);
            }
            Assert.True(maxReloadTicks > 0, "Timed out waiting for reload duration to complete.");

            Assert.Equal(0, cs.ReloadTicksRemaining);

            // On the tick reload completes and weapon is ready to fire in the new reload cycle,
            // shooter naturally switches to the highest-priority Tank target!
            sim.Step();
            Assert.True(world.TryGetCombatState(shooter, out cs));
            Assert.Equal(cTank.Id, cs.TargetContactId);
            Assert.Equal(tank, cs.TargetEntityId);
        }
    }

    [Fact]
    public void HarnessStopCondition_IgnoresImpenetrableContacts_AndHaltsOnlyForPenetrableTargets()
    {
        var (sim, world, rules, catalog) = CreateTestContext();
        using (sim)
        {
            UnitDefinition brdmDef = catalog.RequireDefinition("brdm2");
            ushort brdmIdx = catalog.RequireDefinitionIndex("brdm2");
            UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
            ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
            UnitDefinition m113Def = catalog.RequireDefinition("m113a1");
            ushort m113Idx = catalog.RequireDefinitionIndex("m113a1");

            CombatSystem combat = rules.FindSystem<CombatSystem>()!;
            DetectionSystem detection = rules.FindSystem<DetectionSystem>()!;

            // BRDM-2 at (200, 500) facing east, armed with 14.5mm KPVT
            EntityId brdm = world.CreateUnit(1, brdmIdx, new Vector2(200f, 500f), brdmDef.Speed, brdmDef.MaxHealth, 500, initialTurretYaw: -MathF.PI / 2f);
            world.SetFireStance(brdm, FireStance.FireAtWill);

            // Enemy 1: Frontal M60A1 at (700, 500) facing west (towards BRDM-2)
            // M60A1 has 137mm front armor, KPVT has 32mm penetration at 500m -> completely impenetrable!
            EntityId m60 = world.CreateUnit(2, m60Idx, new Vector2(700f, 500f), m60Def.Speed, m60Def.MaxHealth, 0, initialTurretYaw: MathF.PI / 2f);
            world.SetFireStance(m60, FireStance.HoldFire);

            // Warm up detection
            for (int i = 0; i < 4; i++) sim.Step();

            SideContactTracker tracker = detection.GetTracker(1);
            Assert.True(detection.TryGetContactByEntity(1, m60, out ContactObservation cM60));

            Assert.True(world.TryGetUnit(brdm, out UnitState brdmUnit));
            IUnitCombatCatalog combatCatalog = new OpsCombatCatalog(catalog);
            Assert.True(combatCatalog.TryGetProfile(brdmIdx, out UnitCombatProfile brdmProfile));
            Assert.True(world.TryGetCombatState(brdm, out WorldState.CombatState brdmCs));

            // Check single contact eligibility: M60A1 is impenetrable, so false
            Assert.False(combat.IsContactEligibleForAutoFire(brdmUnit, brdmProfile.Weapon!, brdmCs, cM60, tracker, world));
            // Check overall: no eligible targets in sight
            Assert.False(combat.HasEligibleAutoFireTarget(brdmUnit, brdmProfile.Weapon!, brdmCs, tracker, world));

            // Enemy 2: M113A1 at (400, 500) appears. M113A1 has thin aluminum armor (~38mm front, 32mm side)
            // KPVT can penetrate it -> penetrable target!
            EntityId m113 = world.CreateUnit(2, m113Idx, new Vector2(400f, 500f), m113Def.Speed, m113Def.MaxHealth, 0);
            world.SetFireStance(m113, FireStance.HoldFire);

            for (int i = 0; i < 4; i++) sim.Step();
            Assert.True(detection.TryGetContactByEntity(1, m113, out ContactObservation cM113));

            // Now M113A1 is eligible for auto-fire!
            Assert.True(combat.IsContactEligibleForAutoFire(brdmUnit, brdmProfile.Weapon!, brdmCs, cM113, tracker, world));
            // Overall: unit now has an eligible auto-fire target!
            Assert.True(combat.HasEligibleAutoFireTarget(brdmUnit, brdmProfile.Weapon!, brdmCs, tracker, world));
        }
    }
}
