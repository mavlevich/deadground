using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class FireAccuracyTests
{
    private static HeightMap CreateFlatMap(int size = 129, float cellSize = 10f)
    {
        return new HeightMap(size, size, cellSize, 0.01f, new ushort[size * size]);
    }

    [Fact]
    public void AccuracyCurve_LinearInterpolation_AndClamping()
    {
        AccuracyPoint[] curve =
        [
            new(100f, 0.95f),
            new(500f, 0.85f),
            new(1000f, 0.70f),
            new(1500f, 0.50f)
        ];

        WeaponDefinition weapon = new(
            Id: "test_gun",
            Range: 1500f,
            ReloadDurationTicks: 30,
            Damage: 50,
            AmmoCapacity: 40,
            MuzzleHeight: 1.8f,
            PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
            AccuracyCurve: curve,
            DispersionMils: 3.5f,
            TargetMovementPenalty: 0.15f,
            RangingBonus: 0.05f,
            MaxRangingBonus: 0.15f);

        // Before first point: clamped to first point
        Assert.Equal(0.95f, weapon.GetAccuracyAt(0f));
        Assert.Equal(0.95f, weapon.GetAccuracyAt(50f));
        Assert.Equal(0.95f, weapon.GetAccuracyAt(100f));

        // Between 100m and 500m: midpoint 300m -> (0.95 + 0.85) / 2 = 0.90
        Assert.Equal(0.90f, weapon.GetAccuracyAt(300f), precision: 3);

        // Exact point at 500m
        Assert.Equal(0.85f, weapon.GetAccuracyAt(500f));

        // Between 500m and 1000m: midpoint 750m -> (0.85 + 0.70) / 2 = 0.775
        Assert.Equal(0.775f, weapon.GetAccuracyAt(750f), precision: 3);

        // Exact point at 1000m
        Assert.Equal(0.70f, weapon.GetAccuracyAt(1000f));

        // Between 1000m and 1500m: midpoint 1250m -> (0.70 + 0.50) / 2 = 0.60
        Assert.Equal(0.60f, weapon.GetAccuracyAt(1250f), precision: 3);

        // Beyond last point: clamped to last point
        Assert.Equal(0.50f, weapon.GetAccuracyAt(1500f));
        Assert.Equal(0.50f, weapon.GetAccuracyAt(2000f));

        // Weapon without curve defaults to 1.0f
        WeaponDefinition noCurveWeapon = new("simple_gun", 100f, 10, 10, 10, 1.5f, 1);
        Assert.Equal(1.0f, noCurveWeapon.GetAccuracyAt(50f));
    }

    [Fact]
    public void HitProbability_TargetMovementPenalty_ReducesHitChance()
    {
        AccuracyPoint[] curve = [new(100f, 0.90f), new(1000f, 0.80f)];
        WeaponDefinition weapon = new(
            Id: "test_gun",
            Range: 1000f,
            ReloadDurationTicks: 30,
            Damage: 50,
            AmmoCapacity: 40,
            MuzzleHeight: 1.8f,
            PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
            AccuracyCurve: curve,
            TargetMovementPenalty: 0.15f);

        float staticHitChance = CombatSystem.CalculateHitProbability(
            weapon, distance: 550f, visibleShare: 1.0f, isTargetMoving: false, consecutiveShots: 0);
        float movingHitChance = CombatSystem.CalculateHitProbability(
            weapon, distance: 550f, visibleShare: 1.0f, isTargetMoving: true, consecutiveShots: 0);

        Assert.Equal(0.15f, staticHitChance - movingHitChance, precision: 3);
    }

    [Fact]
    public void HitProbability_VisibleShare_ScalesAccuracy()
    {
        AccuracyPoint[] curve = [new(100f, 0.80f), new(1000f, 0.80f)];
        WeaponDefinition weapon = new(
            Id: "test_gun",
            Range: 1000f,
            ReloadDurationTicks: 30,
            Damage: 50,
            AmmoCapacity: 40,
            MuzzleHeight: 1.8f,
            PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
            AccuracyCurve: curve);

        // Fully visible target
        float fullVis = CombatSystem.CalculateHitProbability(
            weapon, distance: 500f, visibleShare: 1.0f, isTargetMoving: false, consecutiveShots: 0);
        Assert.Equal(0.80f, fullVis, precision: 3);

        // Half visible target (e.g. hull down behind ridge)
        float halfVis = CombatSystem.CalculateHitProbability(
            weapon, distance: 500f, visibleShare: 0.5f, isTargetMoving: false, consecutiveShots: 0);
        Assert.Equal(0.40f, halfVis, precision: 3);

        // Zero visibility gives zero probability
        float zeroVis = CombatSystem.CalculateHitProbability(
            weapon, distance: 500f, visibleShare: 0.0f, isTargetMoving: false, consecutiveShots: 0);
        Assert.Equal(0.0f, zeroVis);
    }

    [Fact]
    public void HitProbability_RangingBonus_IncreasesWithConsecutiveShotsAndCaps()
    {
        AccuracyPoint[] curve = [new(100f, 0.70f), new(1000f, 0.70f)];
        WeaponDefinition weapon = new(
            Id: "test_gun",
            Range: 1000f,
            ReloadDurationTicks: 30,
            Damage: 50,
            AmmoCapacity: 40,
            MuzzleHeight: 1.8f,
            PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
            AccuracyCurve: curve,
            RangingBonus: 0.04f,
            MaxRangingBonus: 0.12f);

        // Shot 0: base 0.70
        float shot0 = CombatSystem.CalculateHitProbability(weapon, 500f, 1.0f, false, consecutiveShots: 0);
        Assert.Equal(0.70f, shot0, precision: 3);

        // Shot 1: 0.70 + 0.04 = 0.74
        float shot1 = CombatSystem.CalculateHitProbability(weapon, 500f, 1.0f, false, consecutiveShots: 1);
        Assert.Equal(0.74f, shot1, precision: 3);

        // Shot 2: 0.70 + 0.08 = 0.78
        float shot2 = CombatSystem.CalculateHitProbability(weapon, 500f, 1.0f, false, consecutiveShots: 2);
        Assert.Equal(0.78f, shot2, precision: 3);

        // Shot 3: 0.70 + 0.12 = 0.82 (reaches cap)
        float shot3 = CombatSystem.CalculateHitProbability(weapon, 500f, 1.0f, false, consecutiveShots: 3);
        Assert.Equal(0.82f, shot3, precision: 3);

        // Shot 10: capped at max ranging bonus 0.12 -> 0.82
        float shot10 = CombatSystem.CalculateHitProbability(weapon, 500f, 1.0f, false, consecutiveShots: 10);
        Assert.Equal(0.82f, shot10, precision: 3);
    }

    [Fact]
    public void VisibleShare_ShooterEyeHeight_HullDownTargetBehindRidge()
    {
        // 129x129 map with 10m cells
        int size = 129;
        ushort[] heights = new ushort[size * size];

        // Create a ridge at x = 50 that is 2.0m high (200 in 0.01m scale)
        // Shooter is at x = 30, target is at x = 70.
        // Target ground is 0m, top of target is 2.5m.
        // Lower points of target will be blocked by the ridge.
        // Upper points can be seen over the ridge.
        for (int z = 0; z < size; z++)
        {
            heights[z * size + 50] = 200; // 2.0 meters
        }

        HeightMap map = new(size, size, 10f, 0.01f, heights);
        StaticGeometry geo = new(map, []);
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        OpsRules rules = new(geo, catalog);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");

        // Attacker at (300, 500)
        EntityId m60 = world.CreateUnit(1, m60Idx, new Vector2(300f, 500f), m60Def.Speed, m60Def.MaxHealth, catalog.GetAmmoCapacity(m60Def));
        // Target at (700, 500) behind the ridge at x = 500
        EntityId t62 = world.CreateUnit(2, t62Idx, new Vector2(700f, 500f), t62Def.Speed, t62Def.MaxHealth, catalog.GetAmmoCapacity(t62Def));

        CombatSystem combat = rules.FindSystem<CombatSystem>()!;
        Assert.True(world.TryGetUnit(m60, out UnitState attacker));
        Assert.True(world.TryGetUnit(t62, out UnitState target));

        float visibleShare = combat.ComputeVisibleShare(attacker, target);

        // Ridge at midpoint blocks lower target points from attacker eye height (approx 2m),
        // but high points (turret top) remain visible.
        Assert.True(visibleShare > 0.0f && visibleShare < 1.0f,
            $"Visible share should be partial for hull-down target, got {visibleShare}");
    }

    [Fact]
    public void Miss_DoesZeroDamage_AndEmitsMissResult()
    {
        // Setup catalog with 0% hit chance weapon to force a miss
        AccuracyPoint[] zeroCurve = [new(0f, 0.0f), new(1000f, 0.0f)];
        WeaponData zeroWeapon = new(
            Id: "zero_gun",
            Range: 200f,
            ReloadDurationTicks: 10,
            Damage: 50,
            AmmoCapacity: 40,
            PresentationKind: "cannon",
            AccuracyCurve: zeroCurve,
            Dispersion: 4.0f);

        UnitDefinition shooterDef = new("shooter", Speed: 10f, SensorRange: 300f, Weapon: "zero_gun", TargetPoints: [0.5f, 1.5f]);
        UnitDefinition targetDef = new("target", Speed: 10f, SensorRange: 300f, TargetPoints: [0.5f, 1.5f]);

        UnitCatalog catalog = new(1, [shooterDef, targetDef], Weapons: [zeroWeapon]);
        HeightMap map = CreateFlatMap();
        StaticGeometry geo = new(map, []);
        OpsRules rules = new(geo, catalog);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        ushort shooterIdx = catalog.RequireDefinitionIndex("shooter");
        ushort targetIdx = catalog.RequireDefinitionIndex("target");

        EntityId shooter = world.CreateUnit(1, shooterIdx, new Vector2(100f, 100f), 10f, 100, 40, initialTurretYaw: 0f);
        EntityId target = world.CreateUnit(2, targetIdx, new Vector2(100f, 50f), 10f, 100, 0, initialTurretYaw: MathF.PI);

        world.SetFireStance(shooter, FireStance.HoldFire);
        world.SetFireStance(target, FireStance.HoldFire);

        for (int i = 0; i < 4; i++) sim.Step();

        DetectionSystem det = rules.FindSystem<DetectionSystem>()!;
        Assert.True(det.TryGetContactByEntity(1, target, out ContactObservation targetContact));

        world.SetFireStance(shooter, FireStance.FireAtWill);
        Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, shooter, targetContact.Id)));

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

        Assert.True(shotFired, "Shooter should have fired.");
        // Target took 0 damage
        Assert.True(world.TryGetCombatState(target, out WorldState.CombatState targetCs));
        Assert.Equal(100, targetCs.CurrentHealth);
        Assert.Equal(0, combat.TotalDamageApplied);

        // Attacker received ShotFired event with Result == Miss
        ReadOnlySpan<CombatEventObservation> events = combat.GetEvents(1);
        Assert.True(events.Length > 0);
        Assert.Equal(CombatEventKind.ShotFired, events[0].Kind);
        Assert.Equal(HitResult.Miss, events[0].Result);

        // Impact point is displaced from target center
        Vector2 impactPos2D = new(events[0].Target.X, events[0].Target.Z);
        float distToTargetCenter = Vector2.Distance(impactPos2D, new Vector2(100f, 50f));
        Assert.True(distToTargetCenter > 0.5f, $"Miss impact must not hit target center, distance was {distToTargetCenter}");
    }

    [Fact]
    public void MissImpactPoint_AngularDispersionGrowsWithRange_AndNeverInsideTargetRadius()
    {
        HeightMap map = CreateFlatMap(size: 257, cellSize: 10f);
        StaticGeometry geo = new(map, []);
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        DetectionSystem det = new(geo, new OpsPerceptionCatalog(catalog));
        CombatSystem combat = new(
            geo,
            new OpsCombatCatalog(catalog),
            new OpsPerceptionCatalog(catalog),
            det,
            combatRandom: DeterministicRandom.CreateDomainStream(1337UL, RandomDomain.Combat));

        AccuracyPoint[] curve = [new(100f, 0.5f), new(2000f, 0.5f)];
        WeaponDefinition weapon = new(
            Id: "dispersion_test_gun",
            Range: 2000f,
            ReloadDurationTicks: 30,
            Damage: 50,
            AmmoCapacity: 40,
            MuzzleHeight: 1.8f,
            PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
            AccuracyCurve: curve,
            DispersionMils: 5.0f);

        Vector2 attackerPos = new(1000f, 1000f);
        Vector3 muzzle = new(1000f, 1.8f, 1000f);
        float targetRadius = 1.5f;

        // Test at short range: 200m
        Vector2 closeTargetPos = new(1000f, 800f);
        float maxCloseDist = 0f;
        for (int i = 0; i < 100; i++)
        {
            Vector3 missImpact = combat.CalculateMissImpactPoint(muzzle, attackerPos, closeTargetPos, targetRadius, weapon);
            Vector2 impact2D = new(missImpact.X, missImpact.Z);
            float distToTarget = Vector2.Distance(impact2D, closeTargetPos);

            Assert.True(distToTarget > targetRadius, $"Impact distance {distToTarget} must exceed target radius {targetRadius}");
            if (distToTarget > maxCloseDist) maxCloseDist = distToTarget;
            Assert.Equal(map.GetHeight(impact2D.X, impact2D.Y), missImpact.Y, precision: 3);
        }

        // Test at far range: 1500m
        Vector2 farTargetPos = new(1000f, 2500f);
        float maxFarDist = 0f;
        for (int i = 0; i < 100; i++)
        {
            Vector3 missImpact = combat.CalculateMissImpactPoint(muzzle, attackerPos, farTargetPos, targetRadius, weapon);
            Vector2 impact2D = new(missImpact.X, missImpact.Z);
            float distToTarget = Vector2.Distance(impact2D, farTargetPos);

            Assert.True(distToTarget > targetRadius, $"Impact distance {distToTarget} must exceed target radius {targetRadius}");
            if (distToTarget > maxFarDist) maxFarDist = distToTarget;
            Assert.Equal(map.GetHeight(impact2D.X, impact2D.Y), missImpact.Y, precision: 3);
        }

        // Angular dispersion in mils scales with range, so far range dispersion is distinctly larger
        Assert.True(maxFarDist > maxCloseDist * 2.0f,
            $"Far dispersion ({maxFarDist:F2}m) must grow significantly compared to close dispersion ({maxCloseDist:F2}m)");
    }

    [Fact]
    public void RangingBonus_ResetsWhenTargetMovesFarOrChanges()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geo = new(map, []);
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        OpsRules rules = new(geo, catalog);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");

        EntityId shooter = world.CreateUnit(1, m60Idx, new Vector2(100f, 100f), m60Def.Speed, m60Def.MaxHealth, catalog.GetAmmoCapacity(m60Def), initialTurretYaw: 0f);
        EntityId targetA = world.CreateUnit(2, t62Idx, new Vector2(100f, 50f), t62Def.Speed, t62Def.MaxHealth, catalog.GetAmmoCapacity(t62Def), initialTurretYaw: MathF.PI);
        EntityId targetB = world.CreateUnit(2, t62Idx, new Vector2(120f, 50f), t62Def.Speed, t62Def.MaxHealth, catalog.GetAmmoCapacity(t62Def), initialTurretYaw: MathF.PI);

        world.SetFireStance(shooter, FireStance.HoldFire);
        world.SetFireStance(targetA, FireStance.HoldFire);
        world.SetFireStance(targetB, FireStance.HoldFire);

        for (int i = 0; i < 4; i++) sim.Step();

        DetectionSystem det = rules.FindSystem<DetectionSystem>()!;
        Assert.True(det.TryGetContactByEntity(1, targetA, out ContactObservation contactA));
        Assert.True(det.TryGetContactByEntity(1, targetB, out ContactObservation contactB));

        // Fire first shot at targetA
        world.SetFireStance(shooter, FireStance.FireAtWill);
        Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, shooter, contactA.Id)));

        CombatSystem combat = rules.FindSystem<CombatSystem>()!;
        while (combat.TotalShotsFired < 1) sim.Step();

        Assert.True(world.TryGetCombatState(shooter, out WorldState.CombatState cs1));
        Assert.Equal(1, cs1.ConsecutiveShotsOnTarget);
        Assert.Equal(targetA, cs1.LastTargetEntityId);

        // Switch target to targetB
        Assert.True(sim.TryEnqueue(1, new AttackCommand(2, sim.Tick, shooter, contactB.Id)));
        // Fast forward reload and next shot
        while (combat.TotalShotsFired < 2) sim.Step();

        Assert.True(world.TryGetCombatState(shooter, out WorldState.CombatState cs2));
        Assert.Equal(1, cs2.ConsecutiveShotsOnTarget);
        Assert.Equal(targetB, cs2.LastTargetEntityId);
    }

    [Fact]
    public void DeterministicReplays_WithSameCombatSeed_ProduceIdenticalOutcomes()
    {
        static (int Shots, int Damage, float LastImpactX, float LastImpactZ) RunSimulation(int seed)
        {
            UnitCatalog catalog = ColdWarContent.LoadCatalog();
            HeightMap map = CreateFlatMap();
            StaticGeometry geo = new(map, []);
            DetectionSystem det = new(geo, new OpsPerceptionCatalog(catalog));
            CombatSystem combat = new(
                geo,
                new OpsCombatCatalog(catalog),
                new OpsPerceptionCatalog(catalog),
                det,
                combatRandom: DeterministicRandom.CreateDomainStream((ulong)seed, RandomDomain.Combat));

            WorldState world = new(map);
            UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
            ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
            UnitDefinition t62Def = catalog.RequireDefinition("t62");
            ushort t62Idx = catalog.RequireDefinitionIndex("t62");

            EntityId m60 = world.CreateUnit(1, m60Idx, new Vector2(100f, 100f), m60Def.Speed, m60Def.MaxHealth, catalog.GetAmmoCapacity(m60Def), initialTurretYaw: 0f);
            EntityId t62 = world.CreateUnit(2, t62Idx, new Vector2(100f, 40f), t62Def.Speed, t62Def.MaxHealth, catalog.GetAmmoCapacity(t62Def), initialTurretYaw: MathF.PI);

            world.SetFireStance(m60, FireStance.FireAtWill);
            world.SetFireStance(t62, FireStance.HoldFire);

            det.Update(world, 0);

            float lastX = 0f;
            float lastZ = 0f;

            for (int tick = 1; tick <= 100; tick++)
            {
                det.Update(world, tick);
                combat.Update(world, tick);

                ReadOnlySpan<CombatEventObservation> events = combat.GetEvents(1);
                if (events.Length > 0)
                {
                    lastX = events[0].Target.X;
                    lastZ = events[0].Target.Z;
                }
            }

            return (combat.TotalShotsFired, combat.TotalDamageApplied, lastX, lastZ);
        }

        var run1 = RunSimulation(42);
        var run2 = RunSimulation(42);

        Assert.Equal(run1.Shots, run2.Shots);
        Assert.Equal(run1.Damage, run2.Damage);
        Assert.Equal(run1.LastImpactX, run2.LastImpactX);
        Assert.Equal(run1.LastImpactZ, run2.LastImpactZ);
    }

    [Fact]
    public void WireProtocol_HitResultMiss_RoundTripsCorrectly()
    {
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];
        ClientReplica replica = new();

        CombatEventObservation missEvent = new(
            EventId: 9001,
            ServerTick: 12,
            Kind: CombatEventKind.ShotFired,
            PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
            Origin: new Vector3(100f, 1.8f, 100f),
            Target: new Vector3(100f, 0.0f, 45f),
            ShooterUnitId: new EntityId(1),
            Result: HitResult.Miss);

        int written = WireProtocol.WriteSnapshot(
            buffer,
            sequence: 1,
            tick: 12,
            units: [],
            contacts: [],
            events: [missEvent]);

        Assert.True(written > 0);
        Assert.True(replica.TryApply(buffer.AsSpan(0, written)));

        Assert.Equal(1, replica.Events.Length);
        Assert.Equal(CombatEventKind.ShotFired, replica.Events[0].Kind);
        Assert.Equal(HitResult.Miss, replica.Events[0].Result);
        Assert.Equal(9001u, replica.Events[0].EventId);
        Assert.Equal(new Vector3(100f, 0.0f, 45f), replica.Events[0].Target);
    }

    [Fact]
    public void PinnedDuelAccuracy_ColdWarTanksAt1000m_WithinTenPercentagePoints()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        OpsCombatCatalog combatCatalog = new(catalog);

        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");

        Assert.True(combatCatalog.TryGetProfile(t62Idx, out UnitCombatProfile t62Profile));
        Assert.True(combatCatalog.TryGetProfile(m60Idx, out UnitCombatProfile m60Profile));

        Assert.NotNull(t62Profile.Weapon);
        Assert.NotNull(m60Profile.Weapon);

        // At 1,000 meters static duel, fully visible, halted, first shot (consecutiveShots: 0)
        float t62HitChance = CombatSystem.CalculateHitProbability(
            t62Profile.Weapon, distance: 1000f, visibleShare: 1.0f, isTargetMoving: false, consecutiveShots: 0);

        float m60HitChance = CombatSystem.CalculateHitProbability(
            m60Profile.Weapon, distance: 1000f, visibleShare: 1.0f, isTargetMoving: false, consecutiveShots: 0);

        // Historical Cold War tank guns are around 80-85% at 1,000 m static duel
        Assert.InRange(t62HitChance, 0.75f, 0.90f);
        Assert.InRange(m60HitChance, 0.75f, 0.90f);

        float difference = MathF.Abs(t62HitChance - m60HitChance);
        Assert.True(difference <= 0.10f,
            $"First-shot hit chance at 1,000 m must be within 10 percentage points. T-62: {t62HitChance:P1}, M60A1: {m60HitChance:P1}, diff: {difference:P1}");
    }

    [Fact]
    public void SteadyCombatTicks_ZeroHeapAllocations()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        HeightMap map = CreateFlatMap();
        StaticGeometry geo = new(map, []);
        DetectionSystem det = new(geo, new OpsPerceptionCatalog(catalog));
        CombatSystem combat = new(
            geo,
            new OpsCombatCatalog(catalog),
            new OpsPerceptionCatalog(catalog),
            det,
            combatRandom: DeterministicRandom.CreateDomainStream(1337UL, RandomDomain.Combat));

        WorldState world = new(map);
        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");

        EntityId m60 = world.CreateUnit(1, m60Idx, new Vector2(100f, 100f), m60Def.Speed, m60Def.MaxHealth, catalog.GetAmmoCapacity(m60Def), initialTurretYaw: 0f);
        EntityId t62 = world.CreateUnit(2, t62Idx, new Vector2(100f, 40f), t62Def.Speed, t62Def.MaxHealth, catalog.GetAmmoCapacity(t62Def), initialTurretYaw: MathF.PI);

        world.SetFireStance(m60, FireStance.FireAtWill);
        world.SetFireStance(t62, FireStance.FireAtWill);

        // Warm up systems for 30 ticks to warm JIT and any static buffers
        for (int tick = 1; tick <= 30; tick++)
        {
            det.Update(world, tick);
            combat.Update(world, tick);
        }

        // Measure allocations during steady combat
        using NoGcAllocationWindow window = new();
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int tick = 31; tick <= 80; tick++)
        {
            det.Update(world, tick);
            combat.Update(world, tick);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, window.Describe(allocated));
    }

    [Fact]
    public void VisibleShareZero_SkipsFiring_DoesNotDeductAmmo()
    {
        // Setup a 129x129 map with a high ridge blocking LOS completely
        int size = 129;
        ushort[] heights = new ushort[size * size];
        for (int z = 0; z < size; z++)
        {
            // Ridge 50.0m tall at x = 50 (5000 in 0.01m units)
            heights[z * size + 50] = 5000;
        }

        HeightMap map = new(size, size, 10f, 0.01f, heights);
        StaticGeometry geo = new(map, []);
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        DetectionSystem det = new(geo, new OpsPerceptionCatalog(catalog));
        CombatSystem combat = new(
            geo,
            new OpsCombatCatalog(catalog),
            new OpsPerceptionCatalog(catalog),
            det,
            combatRandom: DeterministicRandom.CreateDomainStream(1337UL, RandomDomain.Combat));

        WorldState world = new(map);
        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");

        // Attacker at (300, 500), target at (700, 500) behind 50m ridge at x = 500
        EntityId m60 = world.CreateUnit(1, m60Idx, new Vector2(300f, 500f), m60Def.Speed, m60Def.MaxHealth, catalog.GetAmmoCapacity(m60Def), initialTurretYaw: 0f);
        EntityId t62 = world.CreateUnit(2, t62Idx, new Vector2(700f, 500f), t62Def.Speed, t62Def.MaxHealth, catalog.GetAmmoCapacity(t62Def), initialTurretYaw: MathF.PI);

        Assert.True(world.TryGetUnit(m60, out UnitState attacker));
        Assert.True(world.TryGetUnit(t62, out UnitState target));

        // Direct visible share computation gives 0
        float visibleShare = combat.ComputeVisibleShare(attacker, target);
        Assert.Equal(0.0f, visibleShare);

        // CalculateHitProbability clamps visibleShare <= 0 to 0.0f
        OpsCombatCatalog combatCatalog = new(catalog);
        Assert.True(combatCatalog.TryGetProfile(m60Idx, out UnitCombatProfile m60Profile));
        Assert.NotNull(m60Profile.Weapon);
        float hitProb = CombatSystem.CalculateHitProbability(m60Profile.Weapon, 400f, visibleShare, false, 0);
        Assert.Equal(0.0f, hitProb);

        // Add a spotter for player 1 on the east side of the ridge near t62
        EntityId spotter = world.CreateUnit(1, m60Idx, new Vector2(700f, 450f), m60Def.Speed, m60Def.MaxHealth, 0);

        for (int t = 0; t < 4; t++) det.Update(world, t);
        Assert.True(det.TryGetContactByEntity(1, t62, out ContactObservation targetContact));

        // Shooter m60 is ordered to attack t62, but has no line of sight
        Assert.True(combat.TryExecuteAttackOrder(1, new AttackCommand(1, 4, m60, targetContact.Id), world));

        // Step combat for 20 ticks
        for (int tick = 5; tick <= 25; tick++)
        {
            det.Update(world, tick);
            combat.Update(world, tick);
        }

        // Verify: zero shots fired, no ammo deducted, status is Obstructed
        Assert.Equal(0, combat.TotalShotsFired);
        Assert.True(world.TryGetCombatState(m60, out WorldState.CombatState cs));
        Assert.Equal(catalog.GetAmmoCapacity(m60Def), cs.Ammunition);
        Assert.Equal(AttackExecutionStatus.Obstructed, cs.Status);
    }

    [Fact]
    public void CatalogValidation_AccuracyConstraints()
    {
        UnitDefinition dummyUnit = new("u", Speed: 10f, SensorRange: 50f, Weapon: "w", TargetPoints: [1.0f]);

        // Non-monotonic range must fail validation
        WeaponData nonMonotonicWeapon = new(
            Id: "w", Range: 100f, Damage: 10, AmmoCapacity: 10, PresentationKind: "cannon",
            AccuracyCurve: [new(50f, 0.8f), new(40f, 0.7f)]);
        UnitCatalog cat1 = new(1, [dummyUnit], Weapons: [nonMonotonicWeapon]);
        Assert.Throws<InvalidOperationException>(() => PrototypeScenario.ValidateCatalog(cat1));

        // Negative hit probability must fail validation
        WeaponData negativeProbWeapon = new(
            Id: "w", Range: 100f, Damage: 10, AmmoCapacity: 10, PresentationKind: "cannon",
            AccuracyCurve: [new(50f, -0.1f)]);
        UnitCatalog cat2 = new(1, [dummyUnit], Weapons: [negativeProbWeapon]);
        Assert.Throws<InvalidOperationException>(() => PrototypeScenario.ValidateCatalog(cat2));

        // Negative dispersion must fail validation
        WeaponData negativeDispWeapon = new(
            Id: "w", Range: 100f, Damage: 10, AmmoCapacity: 10, PresentationKind: "cannon",
            Dispersion: -1.0f);
        UnitCatalog cat3 = new(1, [dummyUnit], Weapons: [negativeDispWeapon]);
        Assert.Throws<InvalidOperationException>(() => PrototypeScenario.ValidateCatalog(cat3));
    }
}

