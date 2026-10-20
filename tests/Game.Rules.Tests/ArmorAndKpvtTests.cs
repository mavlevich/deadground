using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class ArmorAndKpvtTests
{
    private static HeightMap CreateFlatMap(int size = 129, float cellSize = 10f)
    {
        return new HeightMap(size, size, cellSize, 0.01f, new ushort[size * size]);
    }

    [Fact]
    public void HitSideSelection_SectorBorders_AndWrapBoundary()
    {
        UnitArmor armor = new(Front: 100f, Side: 50f, Rear: 30f, FrontSectorAngle: 90f, RearSectorAngle: 90f);
        Vector2 targetPos = new(100f, 100f);

        // 1. Target facing North (HullHeading = 0).
        // Attacker directly North (incoming angle 0 deg) -> Front
        Assert.Equal(ArmorSide.Front, CombatSystem.DetermineHitSide(new Vector2(100f, 50f), targetPos, 0f, armor));
        // Attacker directly South (incoming angle 180 deg) -> Rear
        Assert.Equal(ArmorSide.Rear, CombatSystem.DetermineHitSide(new Vector2(100f, 150f), targetPos, 0f, armor));
        // Attacker directly East (incoming angle -90 deg) -> Side
        Assert.Equal(ArmorSide.Side, CombatSystem.DetermineHitSide(new Vector2(150f, 100f), targetPos, 0f, armor));
        // Attacker directly West (incoming angle +90 deg) -> Side
        Assert.Equal(ArmorSide.Side, CombatSystem.DetermineHitSide(new Vector2(50f, 100f), targetPos, 0f, armor));

        // 2. Exact sector boundaries (45 deg and 135 deg).
        // 45 deg angle offset: (dx = 50, dz = -50) has bearing -45 deg -> Front (boundary inclusive)
        Assert.Equal(ArmorSide.Front, CombatSystem.DetermineHitSide(new Vector2(150f, 50f), targetPos, 0f, armor));

        // Just past 45 deg: angle ~ 46 deg -> Side
        float rad46 = 46f * (MathF.PI / 180f);
        Vector2 pos46 = targetPos + new Vector2(MathF.Sin(rad46) * 50f, -MathF.Cos(rad46) * 50f);
        Assert.Equal(ArmorSide.Side, CombatSystem.DetermineHitSide(pos46, targetPos, 0f, armor));

        // 135 deg angle offset: (dx = 50, dz = 50) has bearing -135 deg -> Rear (boundary inclusive)
        Assert.Equal(ArmorSide.Rear, CombatSystem.DetermineHitSide(new Vector2(150f, 150f), targetPos, 0f, armor));

        // Just before 135 deg: angle ~ 134 deg -> Side
        float rad134 = 134f * (MathF.PI / 180f);
        Vector2 pos134 = targetPos + new Vector2(MathF.Sin(rad134) * 50f, -MathF.Cos(rad134) * 50f);
        Assert.Equal(ArmorSide.Side, CombatSystem.DetermineHitSide(pos134, targetPos, 0f, armor));

        // 3. Wrap across +/- pi boundary
        // Target heading is +3.14 (pointing almost South).
        // Attacker is North-East of target with incoming angle -3.13 (also almost South).
        // Angular difference across the wrap is 0.013 rad -> Front!
        float targetHeadingNearPi = 3.14f;
        float incomingNearMinusPi = -3.13f;
        Vector2 shooterNearMinusPi = targetPos + new Vector2(-MathF.Sin(incomingNearMinusPi) * 50f, -MathF.Cos(incomingNearMinusPi) * 50f);
        Assert.Equal(ArmorSide.Front, CombatSystem.DetermineHitSide(shooterNearMinusPi, targetPos, targetHeadingNearPi, armor));

        // Shooter facing target rear from opposite side of wrap boundary
        float targetHeadingNearMinusPi = -3.14f;
        float incomingNearZero = 0.05f;
        Vector2 shooterFromRear = targetPos + new Vector2(-MathF.Sin(incomingNearZero) * 50f, -MathF.Cos(incomingNearZero) * 50f);
        Assert.Equal(ArmorSide.Rear, CombatSystem.DetermineHitSide(shooterFromRear, targetPos, targetHeadingNearMinusPi, armor));
    }

    [Fact]
    public void PenetrationFalloff_LinearInterpolationWithRange()
    {
        WeaponDefinition kpvt = new(
            Id: "kpvt_test",
            Range: 1000f,
            ReloadDurationTicks: 30,
            Damage: 10,
            AmmoCapacity: 500,
            MuzzleHeight: 1.9f,
            PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.MachineGun, 0),
            PenetrationNear: 35f,
            PenetrationFar: 20f,
            PenetrationNearRange: 0f,
            PenetrationFarRange: 1000f,
            RoundsPerBurst: 5);

        // Near point (0 m)
        Assert.Equal(35f, kpvt.GetPenetrationAt(0f));
        // Sub-zero distance clamped to near
        Assert.Equal(35f, kpvt.GetPenetrationAt(-10f));
        // Mid-range (500 m): exactly halfway between 35 and 20 -> 27.5 mm
        Assert.Equal(27.5f, kpvt.GetPenetrationAt(500f), precision: 3);
        // Quarter range (250 m): 35 - 0.25 * 15 = 31.25 mm
        Assert.Equal(31.25f, kpvt.GetPenetrationAt(250f), precision: 3);
        // Max range (1000 m): 20 mm
        Assert.Equal(20f, kpvt.GetPenetrationAt(1000f));
        // Beyond max range: clamped to far penetration
        Assert.Equal(20f, kpvt.GetPenetrationAt(1200f));

        // Default weapon without penetration data penetrations anything
        WeaponDefinition defaultWeapon = new("default_gun", 60f, 40, 50, 10, 1.9f, 1);
        Assert.Equal(float.MaxValue, defaultWeapon.GetPenetrationAt(10f));
        Assert.Equal(float.MaxValue, defaultWeapon.GetPenetrationAt(50f));
    }

    [Fact]
    public void NonPenetration_DoesZeroDamage_AndEmitsDeflectedEvent()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        HeightMap map = CreateFlatMap();
        StaticGeometry geo = new(map, []);
        OpsRules rules = new(geo, catalog, null);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        UnitDefinition btrDef = catalog.RequireDefinition("btr70");
        ushort btrIdx = catalog.RequireDefinitionIndex("btr70");
        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");

        // Attacker: BTR-70 at (100, 100) facing North (0 deg)
        EntityId btr = world.CreateUnit(1, btrIdx, new Vector2(100f, 100f), btrDef.Speed, btrDef.MaxHealth, catalog.GetAmmoCapacity(btrDef), initialTurretYaw: 0f);
        // Target: T-62 at (100, 50) facing South (PI rad) -> BTR hits T-62 Front (200 mm armor).
        // KPVT penetration is max 35 mm << 200 mm, so it must deflect!
        EntityId t62 = world.CreateUnit(2, t62Idx, new Vector2(100f, 50f), t62Def.Speed, t62Def.MaxHealth, catalog.GetAmmoCapacity(t62Def), initialTurretYaw: MathF.PI);

        world.SetFireStance(btr, FireStance.HoldFire);
        world.SetFireStance(t62, FireStance.HoldFire);

        for (int i = 0; i < 4; i++) sim.Step();

        DetectionSystem det = rules.FindSystem<DetectionSystem>()!;
        Assert.True(det.TryGetContactByEntity(1, t62, out ContactObservation targetContact));

        world.SetFireStance(btr, FireStance.FireAtWill);
        Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, btr, targetContact.Id)));

        // Run until the BTR aligns and fires
        CombatSystem combat = rules.FindSystem<CombatSystem>()!;
        bool shotFired = false;
        for (int step = 0; step < 40; step++)
        {
            sim.Step();
            if (combat.TotalShotsFired > 0)
            {
                shotFired = true;
                break;
            }
        }

        Assert.True(shotFired, "BTR should have fired at T-62.");
        // Target took 0 damage
        Assert.True(world.TryGetCombatState(t62, out WorldState.CombatState t62Cs));
        Assert.Equal(t62Def.MaxHealth, t62Cs.CurrentHealth);

        // Check events: attacker receives ShotFired with Result == Deflected
        ReadOnlySpan<CombatEventObservation> p1Events = combat.GetEvents(1);
        Assert.True(p1Events.Length > 0);
        Assert.Equal(CombatEventKind.ShotFired, p1Events[0].Kind);
        Assert.Equal(HitResult.Deflected, p1Events[0].Result);

        // Check victim events: target owner receives TookFire or ShotFired with Result == Deflected
        ReadOnlySpan<CombatEventObservation> p2Events = combat.GetEvents(2);
        Assert.True(p2Events.Length > 0);
        Assert.Equal(HitResult.Deflected, p2Events[0].Result);
    }

    [Fact]
    public void Kpvt_PenetratesBtrSide_AndDeflectsOnT62Front()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        HeightMap map = CreateFlatMap();
        StaticGeometry geo = new(map, []);
        OpsRules rules = new(geo, catalog, null);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        UnitDefinition btrDef = catalog.RequireDefinition("btr70");
        ushort btrIdx = catalog.RequireDefinitionIndex("btr70");

        // Attacker BTR at (100, 100) facing North (0 deg).
        EntityId btrAttacker = world.CreateUnit(1, btrIdx, new Vector2(100f, 100f), btrDef.Speed, btrDef.MaxHealth, catalog.GetAmmoCapacity(btrDef), initialTurretYaw: 0f);
        // Target BTR at (100, 50) heading East (PI/2). Attacker is directly to its South -> hits BTR flank (Side armor: 7 mm).
        // KPVT penetration at 50m is ~34.25 mm >> 7 mm, so it MUST penetrate!
        EntityId btrTarget = world.CreateUnit(2, btrIdx, new Vector2(100f, 50f), btrDef.Speed, btrDef.MaxHealth, catalog.GetAmmoCapacity(btrDef), initialTurretYaw: MathF.PI / 2f);

        world.SetFireStance(btrAttacker, FireStance.HoldFire);
        world.SetFireStance(btrTarget, FireStance.HoldFire);

        for (int i = 0; i < 4; i++) sim.Step();

        DetectionSystem det = rules.FindSystem<DetectionSystem>()!;
        Assert.True(det.TryGetContactByEntity(1, btrTarget, out ContactObservation contact));

        world.SetFireStance(btrAttacker, FireStance.FireAtWill);
        Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, btrAttacker, contact.Id)));

        CombatSystem combat = rules.FindSystem<CombatSystem>()!;
        for (int i = 0; i < 40; i++)
        {
            sim.Step();
            if (combat.TotalShotsFired > 0) break;
        }

        Assert.Equal(1, combat.TotalShotsFired);
        Assert.True(world.TryGetCombatState(btrTarget, out WorldState.CombatState damagedBtrCs));
        // BTR-70 has 50 HP, KPVT burst does 10 damage -> 40 HP remaining
        Assert.Equal(40, damagedBtrCs.CurrentHealth);

        ReadOnlySpan<CombatEventObservation> events = combat.GetEvents(1);
        Assert.True(events.Length > 0);
        Assert.Equal(HitResult.Penetrated, events[0].Result);
    }

    [Fact]
    public void BurstAmmunition_ConsumesRoundsPerBurst_AndEnforcesBurstInterval()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        HeightMap map = CreateFlatMap();
        StaticGeometry geo = new(map, []);
        OpsRules rules = new(geo, catalog, null);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        UnitDefinition btrDef = catalog.RequireDefinition("btr70");
        ushort btrIdx = catalog.RequireDefinitionIndex("btr70");
        int initialAmmo = catalog.GetAmmoCapacity(btrDef); // 500 rounds

        EntityId shooter = world.CreateUnit(1, btrIdx, new Vector2(100f, 100f), btrDef.Speed, btrDef.MaxHealth, initialAmmo, initialTurretYaw: 0f);
        EntityId target = world.CreateUnit(2, btrIdx, new Vector2(100f, 50f), btrDef.Speed, btrDef.MaxHealth, initialAmmo, initialTurretYaw: MathF.PI);

        world.SetFireStance(shooter, FireStance.HoldFire);
        world.SetFireStance(target, FireStance.HoldFire);

        for (int i = 0; i < 4; i++) sim.Step();

        DetectionSystem det = rules.FindSystem<DetectionSystem>()!;
        Assert.True(det.TryGetContactByEntity(1, target, out ContactObservation contact));

        world.SetFireStance(shooter, FireStance.FireAtWill);
        Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, shooter, contact.Id)));

        CombatSystem combat = rules.FindSystem<CombatSystem>()!;

        // Advance until the first burst fires
        int firstShotTick = -1;
        for (int t = 0; t < 40; t++)
        {
            sim.Step();
            if (combat.TotalShotsFired == 1)
            {
                firstShotTick = sim.Tick;
                break;
            }
        }

        Assert.True(firstShotTick > 0);
        Assert.True(world.TryGetCombatState(shooter, out WorldState.CombatState cs1));
        // Consumed 5 rounds of ammunition in first burst
        Assert.Equal(initialAmmo - 5, cs1.Ammunition);
        // Reload ticks set to 30 ticks (1.5 seconds at 20 Hz)
        Assert.Equal(30, cs1.ReloadTicksRemaining);

        // Step 29 ticks: reload timer decrements, no second burst fired yet
        for (int r = 0; r < 29; r++)
        {
            sim.Step();
            Assert.Equal(1, combat.TotalShotsFired);
        }

        // 30th tick: second burst fires
        sim.Step();
        Assert.Equal(2, combat.TotalShotsFired);
        Assert.True(world.TryGetCombatState(shooter, out WorldState.CombatState cs2));
        Assert.Equal(initialAmmo - 10, cs2.Ammunition);
    }

    [Fact]
    public void EventHitResult_RoundTripsOverWireProtocol()
    {
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];

        foreach (HitResult expectedResult in new[] { HitResult.None, HitResult.Penetrated, HitResult.Deflected, HitResult.Miss })
        {
            ClientReplica replica = new();
            CombatEventObservation ev = new(
                EventId: 101,
                ServerTick: 50,
                Kind: CombatEventKind.ShotFired,
                PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.MachineGun, 0),
                Origin: new Vector3(10f, 1.8f, 20f),
                Target: new Vector3(30f, 1.2f, 40f),
                ShooterUnitId: new EntityId(7),
                Result: expectedResult);

            int size = WireProtocol.WriteSnapshot(buffer, 1, 50, [], [], [ev]);
            Assert.True(replica.TryApply(buffer.AsSpan(0, size)));
            Assert.Equal(1, replica.EventCount);
            Assert.Equal(expectedResult, replica.Events[0].Result);
        }

        // Invalid hit result value (> 2) must be rejected by WireProtocol
        ClientReplica failReplica = new();
        CombatEventObservation validEv = new(102, 50, CombatEventKind.ShotFired, 1, Vector3.Zero, Vector3.UnitX, default, HitResult.None);
        int validSize = WireProtocol.WriteSnapshot(buffer, 1, 50, [], [], [validEv]);
        int eventOffset = WireProtocol.SnapshotHeaderSize;
        // Corrupt Result at byte 39 of the event record
        buffer[eventOffset + 39] = 99;
        Assert.False(failReplica.TryApply(buffer.AsSpan(0, validSize)));
    }

    [Fact]
    public void Contacts_DoNotLeakArmorOrHealth()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        HeightMap map = CreateFlatMap();
        StaticGeometry geo = new(map, []);
        OpsRules rules = new(geo, catalog, null);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");

        EntityId enemyTank = world.CreateUnit(2, t62Idx, new Vector2(100f, 120f), t62Def.Speed, t62Def.MaxHealth, 40, initialTurretYaw: 0.5f);
        world.ApplyDamage(enemyTank, 30, out _); // Damaged down to 70 HP

        // Observer unit on side 1
        world.CreateUnit(1, t62Idx, new Vector2(100f, 100f), t62Def.Speed, t62Def.MaxHealth, 40, initialTurretYaw: 0f);

        for (int i = 0; i < 4; i++) sim.Step();

        DetectionSystem det = rules.FindSystem<DetectionSystem>()!;
        Assert.True(det.TryGetContactByEntity(1, enemyTank, out ContactObservation contact));

        // Contact only exposes public tracking data: ID, State, Classification, Type, Position, LastSeenTick, TurretYaw, HullHeading
        Assert.Equal(ContactState.Current, contact.State);
        Assert.Equal(ContactClassification.Tank, contact.Classification);
        Assert.Equal(t62Idx, contact.Type);
        Assert.Equal(new Vector2(100f, 120f), contact.Position);

        // Verify ContactObservation type has no health or armor properties via reflection
        Type contactType = typeof(ContactObservation);
        Assert.Null(contactType.GetProperty("Health"));
        Assert.Null(contactType.GetProperty("Armor"));
        Assert.Null(contactType.GetProperty("ArmorFront"));
        Assert.Null(contactType.GetProperty("MaxHealth"));

        // Verify wire protocol contact record size is exactly 24 bytes and contains no health/armor
        Assert.Equal(24, WireProtocol.ContactSize);
    }

    [Fact]
    public void SteadyStateCombatTicks_ZeroManagedHeapAllocations()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        HeightMap map = CreateFlatMap();
        StaticGeometry geo = new(map, []);
        OpsRules rules = new(geo, catalog, null);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        UnitDefinition btrDef = catalog.RequireDefinition("btr70");
        ushort btrIdx = catalog.RequireDefinitionIndex("btr70");

        EntityId t62 = world.CreateUnit(1, t62Idx, new Vector2(100f, 100f), t62Def.Speed, t62Def.MaxHealth, 40, initialTurretYaw: 0f);
        EntityId btr = world.CreateUnit(2, btrIdx, new Vector2(100f, 160f), btrDef.Speed, btrDef.MaxHealth, 500, initialTurretYaw: 0f);

        // Warm up JIT and detection structures
        for (int i = 0; i < 20; i++)
        {
            sim.Step();
        }

        // Measure steady-state allocation during active combat ticks
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            sim.Step();
        }
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocatedAfter - allocatedBefore);
    }

    [Fact]
    public void AutoFire_KpvtPrefersPenetrableApcOverImpenetrableTank()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        HeightMap map = CreateFlatMap();
        StaticGeometry geo = new(map, []);
        OpsRules rules = new(geo, catalog, null);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        UnitDefinition btrDef = catalog.RequireDefinition("btr70");
        ushort btrIdx = catalog.RequireDefinitionIndex("btr70");
        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        UnitDefinition m113Def = catalog.RequireDefinition("m113a1");
        ushort m113Idx = catalog.RequireDefinitionIndex("m113a1");

        // Attacker: BTR-70 at (100, 100) facing North (0 rad)
        EntityId btr = world.CreateUnit(1, btrIdx, new Vector2(100f, 100f), btrDef.Speed, btrDef.MaxHealth, catalog.GetAmmoCapacity(btrDef), initialTurretYaw: 0f);

        // Targets on Side 2:
        // M60A1 Tank at (100, 50) facing South (PI rad): front presented (220 mm armor, impenetrable by KPVT)
        EntityId m60 = world.CreateUnit(2, m60Idx, new Vector2(100f, 50f), m60Def.Speed, m60Def.MaxHealth, catalog.GetAmmoCapacity(m60Def), initialTurretYaw: MathF.PI);
        // M113A1 APC at (120, 50) facing South (PI rad): front presented (20 mm armor, penetrable by KPVT)
        EntityId m113 = world.CreateUnit(2, m113Idx, new Vector2(120f, 50f), m113Def.Speed, m113Def.MaxHealth, catalog.GetAmmoCapacity(m113Def), initialTurretYaw: MathF.PI);

        // Hold fire initially for 4 ticks to establish contacts
        world.SetFireStance(btr, FireStance.HoldFire);
        world.SetFireStance(m60, FireStance.HoldFire);
        world.SetFireStance(m113, FireStance.HoldFire);

        for (int i = 0; i < 4; i++) sim.Step();

        // Switch BTR to FireAtWill
        world.SetFireStance(btr, FireStance.FireAtWill);

        CombatSystem combat = rules.FindSystem<CombatSystem>()!;

        // Step simulation until BTR targets and fires
        for (int step = 0; step < 40; step++)
        {
            sim.Step();
            if (combat.TotalShotsFired > 0) break;
        }

        Assert.True(combat.TotalShotsFired > 0, "BTR should have auto-fired.");
        // M113A1 should take damage, M60A1 should take zero damage
        Assert.True(world.TryGetCombatState(m113, out WorldState.CombatState m113Cs));
        Assert.True(m113Cs.CurrentHealth < m113Def.MaxHealth, "M113A1 should have taken damage from KPVT.");

        Assert.True(world.TryGetCombatState(m60, out WorldState.CombatState m60Cs));
        Assert.Equal(m60Def.MaxHealth, m60Cs.CurrentHealth);
    }

    [Fact]
    public void AutoFire_TankGunPrefersTankOverApc()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        HeightMap map = CreateFlatMap();
        StaticGeometry geo = new(map, []);
        OpsRules rules = new(geo, catalog, null);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        UnitDefinition m113Def = catalog.RequireDefinition("m113a1");
        ushort m113Idx = catalog.RequireDefinitionIndex("m113a1");

        // Attacker: T-62 at (100, 100) facing North (0 rad)
        EntityId t62 = world.CreateUnit(1, t62Idx, new Vector2(100f, 100f), t62Def.Speed, t62Def.MaxHealth, catalog.GetAmmoCapacity(t62Def), initialTurretYaw: 0f);

        // Targets on Side 2: both at distance 50m, both penetrable by 115mm gun
        EntityId m60 = world.CreateUnit(2, m60Idx, new Vector2(100f, 50f), m60Def.Speed, m60Def.MaxHealth, catalog.GetAmmoCapacity(m60Def), initialTurretYaw: MathF.PI);
        EntityId m113 = world.CreateUnit(2, m113Idx, new Vector2(120f, 50f), m113Def.Speed, m113Def.MaxHealth, catalog.GetAmmoCapacity(m113Def), initialTurretYaw: MathF.PI);

        world.SetFireStance(t62, FireStance.HoldFire);
        world.SetFireStance(m60, FireStance.HoldFire);
        world.SetFireStance(m113, FireStance.HoldFire);

        for (int i = 0; i < 4; i++) sim.Step();

        world.SetFireStance(t62, FireStance.FireAtWill);

        CombatSystem combat = rules.FindSystem<CombatSystem>()!;
        for (int step = 0; step < 40; step++)
        {
            sim.Step();
            if (combat.TotalShotsFired > 0) break;
        }

        Assert.True(combat.TotalShotsFired > 0, "T-62 should have auto-fired.");
        // Tank gun prefers Tank class over Apc class
        Assert.True(world.TryGetCombatState(m60, out WorldState.CombatState m60Cs));
        Assert.True(m60Cs.CurrentHealth < m60Def.MaxHealth, "M60A1 tank should have been targeted and damaged first.");

        Assert.True(world.TryGetCombatState(m113, out WorldState.CombatState m113Cs));
        Assert.Equal(m113Def.MaxHealth, m113Cs.CurrentHealth);
    }

    [Fact]
    public void AutoFire_TargetChoiceUsesObservedAspectNotWorldTruth()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        IUnitCombatCatalog combatCatalog = new OpsCombatCatalog(catalog);
        ushort btrIdx = catalog.RequireDefinitionIndex("btr70");
        combatCatalog.TryGetProfile(btrIdx, out UnitCombatProfile btrProfile);
        WeaponDefinition kpvt = btrProfile.Weapon!;

        ushort m113Idx = catalog.RequireDefinitionIndex("m113a1");

        // Attacker is at (100, 1000). Target position is (100, 100) -> distance is 900 m.
        // At 900 m, KPVT penetration is 19.6 mm.
        // M113A1 front armor is 20 mm (impenetrable); rear armor is 12 mm (penetrable).
        Vector2 attackerPos = new(100f, 1000f);
        Vector2 targetPos = new(100f, 100f);

        // Case A: Observed contact heading is 0 rad (facing North, away from attacker -> rear aspect exposed).
        // Rear armor of M113A1 is 12 mm, KPVT penetration at 900 m is 19.6 mm > 12 mm -> penetrable!
        ContactObservation rearObs = new(
            Id: new ContactId(1),
            State: ContactState.Current,
            Classification: ContactClassification.Apc,
            Type: m113Idx,
            Position: targetPos,
            LastSeenTick: 10,
            TurretYaw: 0f,
            HullHeading: 0f);

        Assert.True(CombatSystem.CanPenetrateObservedContact(attackerPos, kpvt, rearObs, combatCatalog));

        // Case B: Observed contact heading is PI rad (facing South, toward attacker -> front aspect exposed).
        // Front armor of M113A1 is 20 mm, KPVT penetration at 900 m is 19.6 mm < 20 mm -> impenetrable!
        ContactObservation frontObs = new(
            Id: new ContactId(2),
            State: ContactState.Current,
            Classification: ContactClassification.Apc,
            Type: m113Idx,
            Position: targetPos,
            LastSeenTick: 10,
            TurretYaw: 0f,
            HullHeading: MathF.PI);

        Assert.False(CombatSystem.CanPenetrateObservedContact(attackerPos, kpvt, frontObs, combatCatalog));
    }

    [Fact]
    public void AutoFire_ReturnFireIgnoresImpenetrableAttacker()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        HeightMap map = CreateFlatMap();
        StaticGeometry geo = new(map, []);
        OpsRules rules = new(geo, catalog, null);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        UnitDefinition btrDef = catalog.RequireDefinition("btr70");
        ushort btrIdx = catalog.RequireDefinitionIndex("btr70");
        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");

        // BTR-70 on Side 1 with ReturnFire stance
        EntityId btr = world.CreateUnit(1, btrIdx, new Vector2(100f, 100f), btrDef.Speed, btrDef.MaxHealth, catalog.GetAmmoCapacity(btrDef), initialTurretYaw: 0f);
        // M60A1 on Side 2 at (100, 50) facing South (PI rad) -> front exposed (220 mm)
        EntityId m60 = world.CreateUnit(2, m60Idx, new Vector2(100f, 50f), m60Def.Speed, m60Def.MaxHealth, catalog.GetAmmoCapacity(m60Def), initialTurretYaw: MathF.PI);

        world.SetFireStance(btr, FireStance.ReturnFire);
        world.SetFireStance(m60, FireStance.HoldFire);

        // Advance a few ticks to let detection establish contacts
        for (int i = 0; i < 4; i++) sim.Step();

        // Simulate taking fire from M60
        world.TryGetCombatState(btr, out WorldState.CombatState btrCs);
        btrCs.LastTookFireAttacker = m60;
        btrCs.LastTookFireTick = sim.Tick;
        world.SetCombatState(btr, btrCs);

        for (int step = 0; step < 20; step++)
        {
            sim.Step();
        }

        // BTR-70 must NOT have engaged M60A1 because KPVT cannot penetrate M60A1 front
        Assert.True(world.TryGetCombatState(btr, out WorldState.CombatState finalBtrCs));
        Assert.Equal(0u, finalBtrCs.TargetEntityId.Value);
        Assert.Equal(0u, finalBtrCs.TargetContactId.Value);
    }

    [Fact]
    public void PartialBurst_ScalesDamageProportionally()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        HeightMap map = CreateFlatMap();
        StaticGeometry geo = new(map, []);
        OpsRules rules = new(geo, catalog, null);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        UnitDefinition btrDef = catalog.RequireDefinition("btr70");
        ushort btrIdx = catalog.RequireDefinitionIndex("btr70");

        // BTR with only 3 rounds remaining for a 5-round burst (full burst = 10 dmg)
        // Scaled damage = Math.Max(1, (10 * 3) / 5) = 6
        EntityId shooter = world.CreateUnit(1, btrIdx, new Vector2(100f, 100f), btrDef.Speed, btrDef.MaxHealth, 3, initialTurretYaw: 0f);
        // Target BTR at (100, 50) heading East (PI/2) -> side exposed (9 mm armor, penetrable)
        EntityId target = world.CreateUnit(2, btrIdx, new Vector2(100f, 50f), btrDef.Speed, btrDef.MaxHealth, 500, initialTurretYaw: MathF.PI / 2f);

        world.SetFireStance(shooter, FireStance.HoldFire);
        world.SetFireStance(target, FireStance.HoldFire);

        for (int i = 0; i < 4; i++) sim.Step();

        DetectionSystem det = rules.FindSystem<DetectionSystem>()!;
        Assert.True(det.TryGetContactByEntity(1, target, out ContactObservation contact));

        world.SetFireStance(shooter, FireStance.FireAtWill);
        Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, shooter, contact.Id)));

        CombatSystem combat = rules.FindSystem<CombatSystem>()!;
        for (int i = 0; i < 40; i++)
        {
            sim.Step();
            if (combat.TotalShotsFired > 0) break;
        }

        Assert.Equal(1, combat.TotalShotsFired);
        Assert.True(world.TryGetCombatState(target, out WorldState.CombatState targetCs));
        // MaxHealth = 50, took 6 damage -> 44 health remaining
        Assert.Equal(44, targetCs.CurrentHealth);

        // Shooter ammo consumed completely to 0
        Assert.True(world.TryGetCombatState(shooter, out WorldState.CombatState shooterCs));
        Assert.Equal(0, shooterCs.Ammunition);
    }

    [Fact]
    public void CatalogValidation_RejectsInvalidPresentationKind_AndNonPositivePenetration()
    {
        string validJson = """
        {
          "schema_version": 1,
          "weapons": [
            {
              "id": "test_gun",
              "range": 1000.0,
              "reload": 5.0,
              "damage": 100,
              "ammo_capacity": 40,
              "muzzle_height": 1.9,
              "presentation_kind": "cannon",
              "penetration_near": 200.0,
              "penetration_far": 150.0
            }
          ],
          "units": [
            {
              "id": "test_unit",
              "speed": 10.0,
              "health": 100,
              "target_points": [1.0],
              "weapon": "test_gun"
            }
          ]
        }
        """;
        Assert.NotNull(PrototypeScenario.LoadCatalog(validJson));

        // Missing presentation_kind
        string missingKindJson = validJson.Replace("\"presentation_kind\": \"cannon\",", "");
        var ex1 = Assert.Throws<InvalidOperationException>(() => PrototypeScenario.LoadCatalog(missingKindJson));
        Assert.Contains("presentation_kind", ex1.Message);

        // Unknown presentation_kind
        string unknownKindJson = validJson.Replace("\"presentation_kind\": \"cannon\",", "\"presentation_kind\": \"laser\",");
        var ex2 = Assert.Throws<InvalidOperationException>(() => PrototypeScenario.LoadCatalog(unknownKindJson));
        Assert.Contains("Unknown weapon presentation_kind", ex2.Message);

        // Non-positive penetration near (0)
        string zeroPenNearJson = validJson.Replace("\"penetration_near\": 200.0,", "\"penetration_near\": 0.0,");
        var ex3 = Assert.Throws<InvalidOperationException>(() => PrototypeScenario.LoadCatalog(zeroPenNearJson));
        Assert.Contains("Invalid weapon penetration", ex3.Message);

        // Non-positive penetration far (-5)
        string negPenFarJson = validJson.Replace("\"penetration_far\": 150.0", "\"penetration_far\": -5.0");
        var ex4 = Assert.Throws<InvalidOperationException>(() => PrototypeScenario.LoadCatalog(negPenFarJson));
        Assert.Contains("Invalid weapon penetration", ex4.Message);
    }

    [Fact]
    public void WeaponPresentation_MakeId_EncodesKindAndIndexWithoutCollisions()
    {
        ushort cannonId0 = WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0);
        ushort cannonId1 = WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 1);
        ushort mgId0 = WeaponPresentation.MakeId(WeaponPresentationKind.MachineGun, 0);
        ushort mgId1 = WeaponPresentation.MakeId(WeaponPresentationKind.MachineGun, 1);

        Assert.NotEqual(cannonId0, cannonId1);
        Assert.NotEqual(mgId0, mgId1);
        Assert.NotEqual(cannonId0, mgId0);

        Assert.True(WeaponPresentation.IsCannon(cannonId0));
        Assert.True(WeaponPresentation.IsCannon(cannonId1));
        Assert.False(WeaponPresentation.IsMachineGun(cannonId0));

        Assert.True(WeaponPresentation.IsMachineGun(mgId0));
        Assert.True(WeaponPresentation.IsMachineGun(mgId1));
        Assert.False(WeaponPresentation.IsCannon(mgId0));
    }

    [Fact]
    public void DuelRanges_TanksApcAndRecon_Pinned()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        IUnitCombatCatalog combatCatalog = new OpsCombatCatalog(catalog);
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        ushort btrIdx = catalog.RequireDefinitionIndex("btr70");
        ushort brdmIdx = catalog.RequireDefinitionIndex("brdm2");
        ushort m113Idx = catalog.RequireDefinitionIndex("m113a1");
        ushort m151Idx = catalog.RequireDefinitionIndex("m151a2");

        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
        UnitDefinition btrDef = catalog.RequireDefinition("btr70");
        UnitDefinition brdmDef = catalog.RequireDefinition("brdm2");
        UnitDefinition m113Def = catalog.RequireDefinition("m113a1");
        UnitDefinition m151Def = catalog.RequireDefinition("m151a2");

        combatCatalog.TryGetProfile(t62Idx, out UnitCombatProfile t62Prof);
        combatCatalog.TryGetProfile(m60Idx, out UnitCombatProfile m60Prof);
        combatCatalog.TryGetProfile(btrIdx, out UnitCombatProfile btrProf);
        combatCatalog.TryGetProfile(m113Idx, out UnitCombatProfile m113Prof);
        combatCatalog.TryGetProfile(m151Idx, out UnitCombatProfile m151Prof);

        WeaponDefinition t62Gun = t62Prof.Weapon!;
        WeaponDefinition m60Gun = m60Prof.Weapon!;
        WeaponDefinition kpvt = btrProf.Weapon!;
        WeaponDefinition m2hb = m113Prof.Weapon!;
        WeaponDefinition m60Mg = m151Prof.Weapon!;

        // 1. Tank Duel:
        // Both penetrate each other frontally at 1,000 m
        Assert.True(t62Gun.GetPenetrationAt(1000f) > m60Def.ArmorFront, "T-62 must penetrate M60A1 front at 1,000m.");
        Assert.True(m60Gun.GetPenetrationAt(1000f) > t62Def.ArmorFront, "M60A1 must penetrate T-62 front at 1,000m.");

        // Calculate frontal penetration cutoff ranges
        float t62Cutoff = (t62Gun.PenetrationNear - m60Def.ArmorFront) / ((t62Gun.PenetrationNear - t62Gun.PenetrationFar) / (t62Gun.PenetrationFarRange - t62Gun.PenetrationNearRange));
        float m60Cutoff = (m60Gun.PenetrationNear - t62Def.ArmorFront) / ((m60Gun.PenetrationNear - m60Gun.PenetrationFar) / (m60Gun.PenetrationFarRange - m60Gun.PenetrationNearRange));

        Assert.InRange(t62Cutoff, 1200f, 1500f);
        Assert.InRange(m60Cutoff, 1200f, 1500f);

        // Cutoff ranges must be within 20% of each other
        float diffRatio = MathF.Abs(t62Cutoff - m60Cutoff) / MathF.Min(t62Cutoff, m60Cutoff);
        Assert.True(diffRatio < 0.20f, $"Cutoff ranges {t62Cutoff:F1}m and {m60Cutoff:F1}m must be within 20% (got {diffRatio:P1}).");

        // Tanks penetrate opponent sides and rear at maximum range
        Assert.True(t62Gun.GetPenetrationAt(t62Gun.Range) > m60Def.ArmorSide);
        Assert.True(m60Gun.GetPenetrationAt(m60Gun.Range) > t62Def.ArmorSide);

        // 2. APC Duel:
        // KPVT vs M113A1 front (20 mm): cutoff is 875 m
        Assert.True(kpvt.GetPenetrationAt(800f) > m113Def.ArmorFront);
        Assert.False(kpvt.GetPenetrationAt(900f) > m113Def.ArmorFront);

        // M2HB vs BTR-70 front (16 mm): cutoff is 857 m
        Assert.True(m2hb.GetPenetrationAt(800f) > btrDef.ArmorFront);
        Assert.False(m2hb.GetPenetrationAt(900f) > btrDef.ArmorFront);

        // M2HB vs BRDM-2 front (16 mm): cutoff is 857 m
        Assert.True(m2hb.GetPenetrationAt(800f) > brdmDef.ArmorFront);
        Assert.False(m2hb.GetPenetrationAt(900f) > brdmDef.ArmorFront);

        // APCs penetrate opponent sides at all combat ranges (up to 1,000 m)
        Assert.True(kpvt.GetPenetrationAt(1000f) > m113Def.ArmorSide);
        Assert.True(m2hb.GetPenetrationAt(1000f) > btrDef.ArmorSide);
        Assert.True(m2hb.GetPenetrationAt(1000f) > brdmDef.ArmorSide);

        // 3. Recon (M151A2 7.62mm M60):
        // Penetrates BRDM-2 side (8 mm) at close range (< 160 m)
        Assert.True(m60Mg.GetPenetrationAt(100f) > brdmDef.ArmorSide);
        Assert.False(m60Mg.GetPenetrationAt(200f) > brdmDef.ArmorSide);

        // Never penetrates BRDM-2 front (16 mm), BTR-70 front (16 mm), M113A1 front (20 mm), or tank fronts
        Assert.False(m60Mg.GetPenetrationAt(0f) > brdmDef.ArmorFront);
        Assert.False(m60Mg.GetPenetrationAt(0f) > btrDef.ArmorFront);
        Assert.False(m60Mg.GetPenetrationAt(0f) > m113Def.ArmorFront);
        Assert.False(m60Mg.GetPenetrationAt(0f) > t62Def.ArmorFront);
        Assert.False(m60Mg.GetPenetrationAt(0f) > m60Def.ArmorFront);
    }

    [Fact]
    public void CatalogValidation_RejectsMoreThan255Weapons()
    {
        UnitDefinition dummyUnit = new(
            Id: "dummy",
            Speed: 10f);

        WeaponData[] weapons = new WeaponData[256];
        for (int i = 0; i < 256; i++)
        {
            weapons[i] = new WeaponData(
                Id: $"gun_{i}",
                Range: 100f,
                ReloadSeconds: 1f,
                Damage: 10,
                AmmoCapacity: 10,
                MuzzleHeight: 1.5f,
                PresentationKind: "cannon",
                PenetrationNear: 100f,
                PenetrationFar: 50f);
        }

        UnitCatalog catalog = new(
            SchemaVersion: 1,
            Units: [dummyUnit],
            Weapons: weapons);

        var ex = Assert.Throws<InvalidOperationException>(() => PrototypeScenario.ValidateCatalog(catalog));
        Assert.Contains("255 weapons", ex.Message);
    }
}

