using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class TurretTraverseTests
{
    [Fact]
    public void TraverseRate_CapsTurnPerTick_And180DegreeTurnTakesPeriodTicks()
    {
        // T-62 period traverse rate: 18.0 deg/s (360 deg in 20.0s, 180 deg in 10.0s = 200 ticks at 20Hz)
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        const int size = 65;
        HeightMap map = new(size, size, 10f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        OpsRules rules = new(geo, catalog, null);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        int ammo = catalog.GetAmmoCapacity(t62Def);

        // Spawn T-62 at (100, 100) facing North (yaw 0)
        EntityId tank = world.CreateUnit(1, t62Idx, new Vector2(100f, 100f), t62Def.Speed, t62Def.MaxHealth, ammo, initialTurretYaw: 0f);
        // Spawn target due South at (100, 200) (180 degrees away, desired yaw MathF.PI)
        EntityId target = world.CreateUnit(2, t62Idx, new Vector2(100f, 200f), t62Def.Speed, t62Def.MaxHealth, ammo, initialTurretYaw: 0f);
        world.SetFireStance(tank, FireStance.HoldFire);
        world.SetFireStance(target, FireStance.HoldFire);

        // Warmup detection
        for (int i = 0; i < 4; i++) sim.Step();

        DetectionSystem detection = rules.FindSystem<DetectionSystem>()!;
        Assert.True(detection.TryGetContactByEntity(1, target, out ContactObservation contact));

        // Order attack
        world.SetFireStance(tank, FireStance.FireAtWill);
        Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, tank, contact.Id)));

        float maxTurnPerTick = (t62Def.TurretTraverseRate * (MathF.PI / 180f)) * Simulation.TickSeconds;
        Assert.True(maxTurnPerTick > 0f);

        float prevYaw = 0f;
        int alignedTick = -1;

        for (int tick = 1; tick <= 210; tick++)
        {
            sim.Step();
            world.TryGetCombatState(tank, out WorldState.CombatState cs);

            float delta = MathF.Abs(CombatSystem.ShorterArcDelta(prevYaw, cs.TurretYaw));
            // Cap verification: per-tick turn never exceeds traverse rate budget
            Assert.True(delta <= maxTurnPerTick + 0.0001f, $"Tick {tick}: turn delta {delta} exceeded max {maxTurnPerTick}");

            float errorToTarget = MathF.Abs(CombatSystem.ShorterArcDelta(cs.TurretYaw, MathF.PI));
            if (errorToTarget <= CombatSystem.TurretAlignmentTolerance && alignedTick < 0)
            {
                alignedTick = tick;
            }
            prevYaw = cs.TurretYaw;
        }

        // A 180 degree turn at 18 deg/s (0.9 deg/tick) takes 200 ticks to reach 180 deg,
        // and enters alignment tolerance (2.86 deg = ~3.18 ticks) at tick 197.
        Assert.True(alignedTick is >= 196 and <= 201, $"Expected alignment at ~197..200 ticks, got {alignedTick}");
    }

    [Fact]
    public void ShorterArc_TurnsAcrossWrapBoundary()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        const int size = 65;
        HeightMap map = new(size, size, 10f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        OpsRules rules = new(geo, catalog, null);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        int ammo = catalog.GetAmmoCapacity(t62Def);

        // Initial yaw is +3.0 radians (close to +pi = 3.14159)
        float startYaw = 3.0f;
        EntityId tank = world.CreateUnit(1, t62Idx, new Vector2(100f, 100f), t62Def.Speed, t62Def.MaxHealth, ammo, initialTurretYaw: startYaw);

        // Place target so that desired yaw is -3.0 radians
        // Direction vector: (-sin(-3.0), -cos(-3.0)) = (sin(3.0), -cos(3.0))
        float desiredYaw = -3.0f;
        Vector2 targetDir = new(-MathF.Sin(desiredYaw), -MathF.Cos(desiredYaw));
        Vector2 targetPos = new Vector2(100f, 100f) + targetDir * 50f;

        EntityId target = world.CreateUnit(2, t62Idx, targetPos, t62Def.Speed, t62Def.MaxHealth, ammo);
        world.SetFireStance(target, FireStance.HoldFire);

        // Shorter arc across +/-pi boundary is Tau - 6.0 ~= 0.283185 rad (~16.2 degrees).
        // Long arc would be 6.0 rad (~343.8 degrees).
        float shorterArc = MathF.Abs(CombatSystem.ShorterArcDelta(startYaw, desiredYaw));
        Assert.True(shorterArc < 0.35f, $"Shorter arc should be ~0.28 rad, was {shorterArc}");

        for (int i = 0; i < 4; i++) sim.Step();

        DetectionSystem detection = rules.FindSystem<DetectionSystem>()!;
        Assert.True(detection.TryGetContactByEntity(1, target, out ContactObservation contact));

        Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, tank, contact.Id)));

        // At 0.9 deg/tick (0.0157 rad/tick), shorter arc of 0.283 rad takes ceil(0.283 / 0.0157) ~= 18 ticks.
        // Long arc would take ceil(6.0 / 0.0157) ~= 382 ticks.
        int alignedTick = -1;
        for (int tick = 1; tick <= 30; tick++)
        {
            sim.Step();
            world.TryGetCombatState(tank, out WorldState.CombatState cs);
            float error = MathF.Abs(CombatSystem.ShorterArcDelta(cs.TurretYaw, desiredYaw));
            if (error <= CombatSystem.TurretAlignmentTolerance && alignedTick < 0)
            {
                alignedTick = tick;
                break;
            }
        }

        Assert.True(alignedTick is > 0 and <= 22, $"Turret aligned in {alignedTick} ticks, proving it took the shorter arc across the wrap.");
    }

    [Fact]
    public void ShotDelays_MatchPeriodRate_T62TakesNoticeablyLongerThanM60A1()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        const int size = 65;
        HeightMap map = new(size, size, 10f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        OpsRules rules = new(geo, catalog, null);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");

        // Both tanks face North (yaw 0). Targets are directly on their flank (due East, yaw -pi/2 = 90 degrees turn).
        // T-62: 18.0 deg/s -> 90 deg / 18 = 5.0s = 100 ticks.
        // M60A1: 22.5 deg/s -> 90 deg / 22.5 = 4.0s = 80 ticks.
        EntityId t62 = world.CreateUnit(1, t62Idx, new Vector2(100f, 100f), t62Def.Speed, t62Def.MaxHealth, catalog.GetAmmoCapacity(t62Def), initialTurretYaw: 0f);
        EntityId m60 = world.CreateUnit(1, m60Idx, new Vector2(100f, 300f), m60Def.Speed, m60Def.MaxHealth, catalog.GetAmmoCapacity(m60Def), initialTurretYaw: 0f);
        world.SetFireStance(t62, FireStance.HoldFire);
        world.SetFireStance(m60, FireStance.HoldFire);

        // Flank targets at same relative offsets
        EntityId t62Target = world.CreateUnit(2, t62Idx, new Vector2(150f, 100f), t62Def.Speed, t62Def.MaxHealth, 0, initialTurretYaw: 0f);
        EntityId m60Target = world.CreateUnit(2, m60Idx, new Vector2(150f, 300f), m60Def.Speed, m60Def.MaxHealth, 0, initialTurretYaw: 0f);
        world.SetFireStance(t62Target, FireStance.HoldFire);
        world.SetFireStance(m60Target, FireStance.HoldFire);

        for (int i = 0; i < 4; i++) sim.Step();

        DetectionSystem detection = rules.FindSystem<DetectionSystem>()!;
        Assert.True(detection.TryGetContactByEntity(1, t62Target, out ContactObservation cT62Target));
        Assert.True(detection.TryGetContactByEntity(1, m60Target, out ContactObservation cM60Target));

        world.SetFireStance(t62, FireStance.FireAtWill);
        world.SetFireStance(m60, FireStance.FireAtWill);
        Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, t62, cT62Target.Id)));
        Assert.True(sim.TryEnqueue(1, new AttackCommand(2, sim.Tick, m60, cM60Target.Id)));

        int m60ShotTick = -1;
        int t62ShotTick = -1;

        int initialT62Ammo = catalog.GetAmmoCapacity(t62Def);
        int initialM60Ammo = catalog.GetAmmoCapacity(m60Def);

        for (int tick = 1; tick <= 120; tick++)
        {
            sim.Step();
            world.TryGetCombatState(m60, out WorldState.CombatState m60Cs);
            world.TryGetCombatState(t62, out WorldState.CombatState t62Cs);

            if (m60Cs.Ammunition < initialM60Ammo && m60ShotTick < 0)
            {
                m60ShotTick = tick;
            }

            if (t62Cs.Ammunition < initialT62Ammo && t62ShotTick < 0)
            {
                t62ShotTick = tick;
            }

            if (m60ShotTick > 0 && t62ShotTick > 0) break;
        }

        // M60A1 fires at ~78 ticks (4.0s minus tolerance)
        Assert.True(m60ShotTick is >= 75 and <= 82, $"M60A1 shot tick was {m60ShotTick}, expected ~78");
        // T-62 fires at ~97 ticks (5.0s minus tolerance)
        Assert.True(t62ShotTick is >= 95 and <= 102, $"T-62 shot tick was {t62ShotTick}, expected ~98");

        // T-62 takes noticeably longer (~19..20 ticks longer, which is 1.0 second)
        Assert.True(t62ShotTick - m60ShotTick >= 15,
            $"T-62 took {t62ShotTick} ticks vs M60 {m60ShotTick} ticks (diff {t62ShotTick - m60ShotTick})");
    }

    [Fact]
    public void Snapshots_Roundtrip16BitTurretYaw()
    {
        float[] testYaws =
        [
            0f,
            0.5f,
            -0.5f,
            MathF.PI * 0.25f,
            -MathF.PI * 0.25f,
            MathF.PI * 0.5f,
            -MathF.PI * 0.5f,
            MathF.PI * 0.75f,
            -MathF.PI * 0.75f,
            MathF.PI - 0.001f,
            -MathF.PI + 0.001f
        ];

        byte[] buffer = new byte[WireProtocol.MaxMessageSize];

        foreach (float expectedYaw in testYaws)
        {
            ushort q = WireProtocol.QuantizeYaw(expectedYaw);
            float deq = WireProtocol.DequantizeYaw(q);
            Assert.True(MathF.Abs(expectedYaw - deq) < 0.0002f, $"Quantize/Dequantize error {MathF.Abs(expectedYaw - deq)} for yaw {expectedYaw}");

            OwnUnitObservation own = new(
                new EntityId(10), 1, new Vector2(50f, 50f), 0,
                TurretYaw: expectedYaw, HullHeading: -expectedYaw);

            ContactObservation contact = new(
                new ContactId(20), ContactState.Current, ContactClassification.Tank, 1,
                new Vector2(80f, 80f), 5,
                TurretYaw: expectedYaw, HullHeading: -expectedYaw);

            int size = WireProtocol.WriteSnapshot(buffer, 1, 10, [own], [contact], []);
            ClientReplica replica = new();
            Assert.True(replica.TryApply(buffer.AsSpan(0, size)));

            Assert.Equal(1, replica.OwnUnits.Length);
            Assert.True(MathF.Abs(expectedYaw - replica.OwnUnits[0].TurretYaw) < 0.0002f,
                $"Unit TurretYaw roundtrip error {MathF.Abs(expectedYaw - replica.OwnUnits[0].TurretYaw)} for yaw {expectedYaw}");

            Assert.Equal(1, replica.Contacts.Length);
            Assert.True(MathF.Abs(expectedYaw - replica.Contacts[0].TurretYaw) < 0.0002f,
                $"Contact TurretYaw roundtrip error {MathF.Abs(expectedYaw - replica.Contacts[0].TurretYaw)} for yaw {expectedYaw}");
            Assert.True(MathF.Abs(CombatSystem.ShorterArcDelta(-expectedYaw, replica.OwnUnits[0].HullHeading)) < 0.0002f,
                $"Unit HullHeading roundtrip gave {replica.OwnUnits[0].HullHeading} for {-expectedYaw}");
            Assert.True(MathF.Abs(CombatSystem.ShorterArcDelta(-expectedYaw, replica.Contacts[0].HullHeading)) < 0.0002f,
                $"Contact HullHeading roundtrip gave {replica.Contacts[0].HullHeading} for {-expectedYaw}");
        }
    }

    [Fact]
    public void ContactTracker_FreezesTurretYaw_OnExpiryToLastKnown()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        const int size = 65;
        HeightMap map = new(size, size, 10f, 0.01f, new ushort[size * size]);
        OpaqueStructure wall = new(new StructureId(1), minX: 30f, minY: 0f, minZ: 0f, maxX: 32f, maxY: 10f, maxZ: 50f, isGrounded: true);
        StaticGeometry geo = new(map, [wall]);
        OpsRules rules = new(geo, catalog, null);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");

        // Player 1 observer at (20, 20)
        EntityId observer = world.CreateUnit(1, t62Idx, new Vector2(20f, 20f), t62Def.Speed, 100, 0, initialTurretYaw: 0f);
        // Player 2 target at (25, 20) in front of the wall with initial yaw 0.5 rad
        float initialTargetYaw = 0.5f;
        EntityId target = world.CreateUnit(2, t62Idx, new Vector2(25f, 20f), t62Def.Speed, 100, 0, initialTurretYaw: initialTargetYaw);
        world.SetFireStance(observer, FireStance.HoldFire);
        world.SetFireStance(target, FireStance.HoldFire);

        // Warm up and verify contact is Current with initial yaw
        for (int i = 0; i < 4; i++) sim.Step();

        SideContactTracker tracker = rules.FindSystem<DetectionSystem>()!.GetTracker(1);
        Assert.True(tracker.TryGetContactByEntity(target, out ContactObservation currentContact));
        Assert.Equal(ContactState.Current, currentContact.State);
        Assert.True(MathF.Abs(currentContact.TurretYaw - initialTargetYaw) < 0.001f);

        // Change target's turret yaw to a known value while observed
        float observedTurnYaw = 1.25f;
        world.SetTurretYaw(target, observedTurnYaw);
        sim.Step();

        Assert.True(tracker.TryGetContactByEntity(target, out currentContact));
        Assert.Equal(ContactState.Current, currentContact.State);
        Assert.True(MathF.Abs(currentContact.TurretYaw - observedTurnYaw) < 0.001f);

        ContactId contactId = currentContact.Id;

        // Move target behind the wall to (40, 20) so line of sight is obstructed
        world.SetPosition(target, new Vector2(40f, 20f));
        // Detection runs every 4 ticks; step 5 times to transition to LastKnown
        for (int i = 0; i < 5; i++) sim.Step();

        Assert.True(tracker.TryGetContact(contactId, out ContactObservation lastKnownContact));
        Assert.Equal(ContactState.LastKnown, lastKnownContact.State);
        // Contact yaw must remain frozen at observedTurnYaw
        Assert.True(MathF.Abs(lastKnownContact.TurretYaw - observedTurnYaw) < 0.001f,
            $"Frozen LastKnown yaw was {lastKnownContact.TurretYaw}, expected {observedTurnYaw}");

        // Change target's truth yaw in secret while hidden
        world.SetTurretYaw(target, -2.5f);
        for (int i = 0; i < 5; i++) sim.Step();

        Assert.True(tracker.TryGetContact(contactId, out lastKnownContact));
        Assert.Equal(ContactState.LastKnown, lastKnownContact.State);
        // Must NOT leak truth yaw (-2.5f); must still be frozen at observedTurnYaw (1.25f)
        Assert.True(MathF.Abs(lastKnownContact.TurretYaw - observedTurnYaw) < 0.001f,
            $"Contact leaked truth yaw! Got {lastKnownContact.TurretYaw}, expected frozen {observedTurnYaw}");
    }

    [Fact]
    public void ClientInterpolation_ChoosesShorterArc()
    {
        // 1. Math interpolation function across wrap boundary
        float fromYaw = 3.0f;
        float toYaw = -3.0f;
        float midYaw = ClientReplica.InterpolateYaw(fromYaw, toYaw, 0.5f);

        // The shorter arc between +3.0 and -3.0 crosses pi (~3.14159) or -pi
        // It must NOT cross 0.0 (which would be the long arc of ~6.0 rad)
        Assert.True(MathF.Abs(midYaw) > 3.0f, $"Interpolated mid yaw {midYaw} must cross pi, not 0");

        // 2. ClientReplica sampling with two snapshots across wrap
        ClientReplica replica = new();
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];

        // Snapshot at tick 10 with yaw 3.0f
        OwnUnitObservation u10 = new(new EntityId(1), 1, new Vector2(10f, 10f), 0, TurretYaw: 3.0f, HullHeading: 3.0f);
        ContactObservation c10 = new(new ContactId(101), ContactState.Current, ContactClassification.Tank, 1, new Vector2(20f, 20f), 10, TurretYaw: 3.0f, HullHeading: 3.0f);
        int size1 = WireProtocol.WriteSnapshot(buffer, 1, 10, [u10], [c10], []);
        Assert.True(replica.TryApply(buffer.AsSpan(0, size1)));

        // Snapshot at tick 20 with yaw -3.0f
        OwnUnitObservation u20 = new(new EntityId(1), 1, new Vector2(10f, 10f), 0, TurretYaw: -3.0f, HullHeading: -3.0f);
        ContactObservation c20 = new(new ContactId(101), ContactState.Current, ContactClassification.Tank, 1, new Vector2(20f, 20f), 20, TurretYaw: -3.0f, HullHeading: -3.0f);
        int size2 = WireProtocol.WriteSnapshot(buffer, 2, 20, [u20], [c20], []);
        Assert.True(replica.TryApply(buffer.AsSpan(0, size2)));

        // Sample at render tick 15 (alpha = 0.5)
        replica.SampleYaws(new EntityId(1), 15.0, out float sampledUnitYaw, out float sampledUnitHull);
        replica.SampleContactYaws(new ContactId(101), 15.0, out float sampledContactYaw, out float sampledContactHull);

        Assert.True(MathF.Abs(sampledUnitYaw) > 3.0f, $"Sampled unit yaw {sampledUnitYaw} must cross pi along shorter arc");
        Assert.True(MathF.Abs(sampledContactYaw) > 3.0f, $"Sampled contact yaw {sampledContactYaw} must cross pi along shorter arc");
        Assert.True(MathF.Abs(sampledUnitHull) > 3.0f, $"Sampled unit hull {sampledUnitHull} must cross pi along shorter arc");
        Assert.True(MathF.Abs(sampledContactHull) > 3.0f, $"Sampled contact hull {sampledContactHull} must cross pi along shorter arc");
    }

    private static (Simulation Sim, WorldState World, UnitCatalog Catalog) FlatWorld(params OpaqueStructure[] structures)
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        const int size = 65;
        HeightMap map = new(size, size, 10f, 0.01f, new ushort[size * size]);
        OpsRules rules = new(new StaticGeometry(map, structures), catalog, null);
        WorldState world = new(map);
        return (new Simulation(world, rules), world, catalog);
    }

    private static EntityId Spawn(WorldState world, UnitCatalog catalog, int player, string type, Vector2 at, float yaw,
        int health = 0)
    {
        UnitDefinition def = catalog.RequireDefinition(type);
        return world.CreateUnit(player, catalog.RequireDefinitionIndex(type), at, def.Speed,
            health > 0 ? health : def.MaxHealth, catalog.GetAmmoCapacity(def), initialTurretYaw: yaw);
    }

    [Fact]
    public void DrivingOffWithoutTarget_GunStaysOverTheFront()
    {
        // Playtest: the gun lagged behind every hull turn and never settled. The turret rides on the
        // hull, so with no target it stays over the front whichever way the tank drives.
        (Simulation sim, WorldState world, UnitCatalog catalog) = FlatWorld();
        using Simulation _ = sim;
        EntityId tank = Spawn(world, catalog, 1, "t62", new Vector2(100f, 100f), 0f);
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, tank, new Vector2(500f, 100f))));
        bool turned = false;
        for (int tick = 1; tick <= 60; tick++)
        {
            sim.Step();
            world.TryGetCombatState(tank, out WorldState.CombatState cs);
            Assert.True(MathF.Abs(CombatSystem.ShorterArcDelta(cs.HullHeading, cs.TurretYaw)) < 0.0001f,
                $"Tick {tick}: gun {cs.TurretYaw} off the hull {cs.HullHeading}");
            turned |= MathF.Abs(CombatSystem.ShorterArcDelta(cs.HullHeading, -MathF.PI / 2f)) < 0.001f;
        }
        Assert.True(turned, "The hull must face the travel toward +X.");
    }

    [Fact]
    public void HullFacesEachLegOfTheRoute_NotTheFinalTarget()
    {
        // Playtest: hull and gun turned apart. The sim aimed the hull at the order's end point while the
        // client turned it along the route, so around an obstacle the two disagreed.
        OpaqueStructure wall = new(new StructureId(1), 190f, 0f, 60f, 210f, 5f, 400f);
        (Simulation sim, WorldState world, UnitCatalog catalog) = FlatWorld(wall);
        using Simulation _ = sim;
        EntityId tank = Spawn(world, catalog, 1, "t62", new Vector2(100f, 200f), -MathF.PI / 2f);
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, tank, new Vector2(300f, 200f))));
        Vector2 previous = new(100f, 200f);
        int offAxisTicks = 0;
        for (int tick = 1; tick <= 400; tick++)
        {
            sim.Step();
            world.TryGetUnit(tank, out UnitState unit);
            world.TryGetCombatState(tank, out WorldState.CombatState cs);
            Vector2 step = unit.Position - previous;
            previous = unit.Position;
            if (step.Length() < 0.05f) continue;
            float travel = MathF.Atan2(-step.X, -step.Y);
            Assert.True(MathF.Abs(CombatSystem.ShorterArcDelta(cs.HullHeading, cs.TurretYaw)) < 0.0001f,
                $"Tick {tick}: gun {cs.TurretYaw} off the hull {cs.HullHeading}");
            if (MathF.Abs(CombatSystem.ShorterArcDelta(cs.HullHeading, -MathF.PI / 2f)) > 0.2f) offAxisTicks++;
        }
        Assert.True(offAxisTicks > 10, $"The route must bend around the wall, {offAxisTicks} off-axis ticks.");
    }

    [Fact]
    public void UnarmedAndRuleSetMovers_TurnTheirHullsToo()
    {
        // The hull heading is sent for every unit, so it must not depend on having a gun or a combat system.
        (Simulation sim, WorldState world, UnitCatalog catalog) = FlatWorld();
        using Simulation _ = sim;
        EntityId jeep = Spawn(world, catalog, 1, "m151a2", new Vector2(100f, 100f), 0f);
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, jeep, new Vector2(400f, 100f))));
        for (int tick = 0; tick < 35; tick++) sim.Step();
        world.TryGetCombatState(jeep, out WorldState.CombatState jeepState);
        Assert.True(MathF.Abs(CombatSystem.ShorterArcDelta(jeepState.HullHeading, -MathF.PI / 2f)) < 0.001f,
            $"An unarmed unit must face its travel, hull {jeepState.HullHeading}");

        using WorldState plainWorld = new(HeightMap.CreatePrototype());
        using Simulation plain = new(plainWorld, new OpsRules());
        EntityId box = plainWorld.CreateUnit(1, 0, new Vector2(10f, 10f), 6f, initialTurretYaw: MathF.PI);
        plainWorld.SetTurretYaw(box, MathF.PI + 0.4f); // the gun off the front by 0.4 rad
        Assert.True(plain.TryEnqueue(1, new MoveCommand(1, plain.Tick, box, new Vector2(10f, 40f))));
        for (int tick = 0; tick < 10; tick++) plain.Step();
        plainWorld.TryGetCombatState(box, out WorldState.CombatState boxState);
        Assert.True(MathF.Abs(CombatSystem.ShorterArcDelta(boxState.HullHeading, MathF.PI)) < 0.001f,
            $"The plain movement system must turn the hull to its travel, hull {boxState.HullHeading}");
        Assert.True(MathF.Abs(CombatSystem.ShorterArcDelta(boxState.TurretYaw, MathF.PI + 0.4f)) < 0.001f,
            $"The gun must ride on the turned hull, yaw {boxState.TurretYaw}");
    }

    [Fact]
    public void HullHeading_ReachesOwnKnowledgeAndEnemyContacts()
    {
        (Simulation sim, WorldState world, UnitCatalog catalog) = FlatWorld();
        using Simulation _ = sim;
        EntityId watcher = Spawn(world, catalog, 1, "brdm2", new Vector2(300f, 100f), 0f);
        EntityId tank = Spawn(world, catalog, 2, "t62", new Vector2(250f, 140f), 0f);
        world.SetFireStance(watcher, FireStance.HoldFire);
        world.SetFireStance(tank, FireStance.HoldFire);
        // First seen standing still and facing north, then seen driving east. The gun starts well off
        // the front, so a mix-up of gun and hull shows.
        for (int tick = 0; tick < 10; tick++) sim.Step();
        PlayerKnowledge enemy = new(1);
        sim.UpdateKnowledge(enemy);
        Assert.Equal(1, enemy.Contacts.Length);
        Assert.True(MathF.Abs(enemy.Contacts[0].HullHeading) < 0.001f, $"Parked contact hull {enemy.Contacts[0].HullHeading}");
        world.SetTurretYaw(tank, 1.0f);
        Assert.True(sim.TryEnqueue(2, new MoveCommand(1, sim.Tick, tank, new Vector2(350f, 140f))));
        for (int tick = 0; tick < 70; tick++) sim.Step();

        PlayerKnowledge own = new(2);
        sim.UpdateKnowledge(own);
        Assert.True(MathF.Abs(CombatSystem.ShorterArcDelta(own.OwnUnits[0].HullHeading, -MathF.PI / 2f)) < 0.001f,
            $"Own knowledge hull {own.OwnUnits[0].HullHeading}");

        sim.UpdateKnowledge(enemy);
        Assert.Equal(1, enemy.Contacts.Length);
        Assert.True(MathF.Abs(CombatSystem.ShorterArcDelta(enemy.Contacts[0].HullHeading, -MathF.PI / 2f)) < 0.001f,
            $"Contact hull {enemy.Contacts[0].HullHeading}");
    }

    [Fact]
    public void NoTarget_GunReturnsOverTheFrontAtTraverseRate()
    {
        (Simulation sim, WorldState world, UnitCatalog catalog) = FlatWorld();
        using Simulation _ = sim;
        EntityId tank = Spawn(world, catalog, 1, "t62", new Vector2(100f, 100f), 0f);
        world.SetTurretYaw(tank, 1.0f); // left over from an engagement, about 57 degrees off the front
        float cap = catalog.RequireDefinition("t62").TurretTraverseRate * (MathF.PI / 180f) * Simulation.TickSeconds;
        float previous = 1.0f;
        int frontTick = -1;
        for (int tick = 1; tick <= 90; tick++)
        {
            sim.Step();
            world.TryGetCombatState(tank, out WorldState.CombatState cs);
            Assert.True(MathF.Abs(CombatSystem.ShorterArcDelta(previous, cs.TurretYaw)) <= cap + 0.0001f);
            if (frontTick < 0 && MathF.Abs(cs.TurretYaw) < 0.0001f) frontTick = tick;
            previous = cs.TurretYaw;
        }
        // 1.0 rad at 0.9 degrees per tick: about 64 ticks.
        Assert.True(frontTick is >= 60 and <= 70, $"Expected the gun over the front after about 64 ticks, got {frontTick}");
    }

    [Fact]
    public void PullingBack_KeepsTheGunOnTheEngagedTargetWithoutFiring()
    {
        // A tank that drives off from a fight keeps watching its target over the rear deck. It fires
        // only once it halts again (mobile fire is a later rule).
        (Simulation sim, WorldState world, UnitCatalog catalog) = FlatWorld();
        using Simulation _ = sim;
        EntityId tank = Spawn(world, catalog, 1, "t62", new Vector2(300f, 300f), MathF.PI);
        EntityId enemy = Spawn(world, catalog, 2, "m60a1", new Vector2(300f, 500f), 0f, health: 100000);
        world.SetFireStance(enemy, FireStance.HoldFire);
        for (int tick = 0; tick < 40; tick++) sim.Step();
        world.TryGetCombatState(tank, out WorldState.CombatState engaged);
        Assert.NotEqual(0u, engaged.TargetContactId.Value);

        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, tank, new Vector2(300f, 60f))));
        int ammo = -1;
        bool reachedBackFacing = false;
        for (int tick = 1; tick <= 140; tick++)
        {
            sim.Step();
            world.TryGetCombatState(tank, out WorldState.CombatState cs);
            if (!world.TryGetUnit(tank, out UnitState unit) || !unit.Moving) continue;
            if (ammo < 0) ammo = cs.Ammunition;
            Assert.Equal(ammo, cs.Ammunition);
            if (MathF.Abs(CombatSystem.ShorterArcDelta(cs.HullHeading, 0f)) < 0.001f)
            {
                reachedBackFacing = true;
            }
            Assert.True(MathF.Abs(CombatSystem.ShorterArcDelta(cs.TurretYaw, MathF.PI)) <= CombatSystem.TurretAlignmentTolerance,
                $"Tick {tick}: the gun must stay on the enemy behind, yaw {cs.TurretYaw}");
        }
        Assert.True(ammo >= 0, "The tank never drove off.");
        Assert.True(reachedBackFacing, "The hull must turn to face the way back.");
    }

    [Fact]
    public void HoldFireForOthers_StopsOnlyTheOtherPlayers()
    {
        using Simulation sim = BattleScenario.CreateSimulation();
        int changed = TestOptions.HoldFireForOthers(sim, 1);
        Assert.True(changed > 0);
        for (int i = 0; i < sim.World.Count; i++)
        {
            UnitState unit = sim.World.GetUnitAt(i);
            Assert.True(sim.World.TryGetCombatState(unit.Id, out WorldState.CombatState cs));
            Assert.Equal(unit.Owner == 1 ? FireStance.FireAtWill : FireStance.HoldFire, cs.Stance);
        }
    }

    [Fact]
    public void YawQuantization_KeepsZeroAndQuarterTurnsExact()
    {
        // With 65535 steps a zero yaw came back as 0.00005 rad, enough to turn an idle hull on the client.
        Assert.Equal(0f, WireProtocol.DequantizeYaw(WireProtocol.QuantizeYaw(0f)));
        Assert.Equal(MathF.PI / 2f, WireProtocol.DequantizeYaw(WireProtocol.QuantizeYaw(MathF.PI / 2f)), 6);
        Assert.Equal(-MathF.PI / 2f, WireProtocol.DequantizeYaw(WireProtocol.QuantizeYaw(-MathF.PI / 2f)), 6);
        Assert.Equal(MathF.PI, WireProtocol.DequantizeYaw(WireProtocol.QuantizeYaw(MathF.PI)), 6);
        Assert.Equal(MathF.PI, WireProtocol.DequantizeYaw(WireProtocol.QuantizeYaw(-MathF.PI)), 6);
        float step = MathF.Tau / 65536f;
        for (float yaw = -3.14f; yaw < 3.14f; yaw += 0.0137f)
        {
            float back = WireProtocol.DequantizeYaw(WireProtocol.QuantizeYaw(yaw));
            Assert.True(MathF.Abs(CombatSystem.ShorterArcDelta(yaw, back)) <= step * 0.51f, $"{yaw} came back as {back}");
        }
    }
}
