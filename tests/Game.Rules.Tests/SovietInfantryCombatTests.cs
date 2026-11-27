using System.Numerics;
using DeadGround.Engine;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class SovietInfantryCombatTests
{
    private static (Simulation Sim, WorldState World, OpsRules Rules, UnitCatalog Catalog) CreateFlatContext(ulong seed = 7407UL)
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        const int size = 129;
        HeightMap map = new(size, size, 8f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        OpsRules rules = new(geo, catalog, new NavigationTuning(CellSize: 4.0f));
        WorldState world = new(map);
        Simulation sim = new(world, rules, matchSeed: seed);
        return (sim, world, rules, catalog);
    }

    [Fact]
    public void Catalog_ContainsSovietRiflemanAndRpg7_WithExpectedSpecsAndPresentationKinds()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        OpsCombatCatalog combatCatalog = new(catalog);

        UnitDefinition rifleman = catalog.RequireDefinition("soviet_rifleman");
        UnitDefinition rpg = catalog.RequireDefinition("soviet_rpg");
        ushort rifleIdx = catalog.RequireDefinitionIndex("soviet_rifleman");
        ushort rpgIdx = catalog.RequireDefinitionIndex("soviet_rpg");

        Assert.Equal("soviet", rifleman.Faction);
        Assert.Equal("infantry", rifleman.Classification);
        Assert.Equal(240, catalog.GetAmmoCapacity(rifleman));

        Assert.Equal("soviet", rpg.Faction);
        Assert.Equal("infantry", rpg.Classification);
        Assert.Equal(4, catalog.GetAmmoCapacity(rpg));

        Assert.True(combatCatalog.TryGetProfile(rifleIdx, out UnitCombatProfile rifleProfile));
        Assert.NotNull(rifleProfile.Weapon);
        Assert.True(WeaponPresentation.IsRifle(rifleProfile.Weapon!.PresentationId));
        Assert.True(WeaponPresentation.IsSovietRifle(rifleProfile.Weapon.PresentationId));
        Assert.Equal(3, rifleProfile.Weapon.RoundsPerBurst);

        Assert.True(combatCatalog.TryGetProfile(rpgIdx, out UnitCombatProfile rpgProfile));
        Assert.NotNull(rpgProfile.Weapon);
        Assert.True(rpgProfile.Weapon!.IsAntiTankRocket);
        Assert.Equal(260f, rpgProfile.Weapon.GetPenetrationAt(0f));
        Assert.Equal(260f, rpgProfile.Weapon.GetPenetrationAt(300f));
        Assert.Equal(4.5f, rpgProfile.Weapon.SplashRadius);
        Assert.Equal(45, rpgProfile.Weapon.StructuralDamage);
    }

    [Fact]
    public void SovietRifleman_Ak74_EngagesInfantry_AndNeverAutoFiresAtTankArmor()
    {
        var (sim, world, rules, catalog) = CreateFlatContext(7401UL);
        using (sim)
        {
            UnitDefinition akDef = catalog.RequireDefinition("soviet_rifleman");
            ushort akIdx = catalog.RequireDefinitionIndex("soviet_rifleman");
            UnitDefinition usInfDef = catalog.RequireDefinition("us_rifleman");
            ushort usInfIdx = catalog.RequireDefinitionIndex("us_rifleman");
            UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
            ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");

            CombatSystem combat = rules.FindSystem<CombatSystem>()!;
            sim.SetPlayerAdmitted(1, true);
            sim.SetPlayerAdmitted(2, true);

            // Soviet rifleman facing east toward an enemy M60A1 tank only -> must hold fire (cannot penetrate)
            EntityId shooter = world.CreateUnit(1, akIdx, new Vector2(200f, 300f), akDef.Speed, akDef.MaxHealth, 240, initialTurretYaw: -MathF.PI / 2f);
            EntityId tank = world.CreateUnit(2, m60Idx, new Vector2(320f, 300f), m60Def.Speed, m60Def.MaxHealth, 0);
            world.SetFireStance(tank, FireStance.HoldFire);

            for (int i = 0; i < 20; i++) sim.Step();
            Assert.Equal(0, combat.TotalShotsFired);

            // Spawn enemy US rifleman at 90 m -> Soviet AK-74 engages with 3-round RifleAk bursts and eliminates target
            EntityId enemyInf = world.CreateUnit(2, usInfIdx, new Vector2(290f, 300f), usInfDef.Speed, usInfDef.MaxHealth, 0);
            world.SetFireStance(enemyInf, FireStance.HoldFire);

            bool sawAkShotEvent = false;
            for (int i = 0; i < 120 && world.TryGetUnit(enemyInf, out _); i++)
            {
                sim.Step();
                ReadOnlySpan<CombatEventObservation> events = combat.GetEvents(1);
                for (int e = 0; e < events.Length; e++)
                {
                    if (events[e].Kind == CombatEventKind.ShotFired &&
                        WeaponPresentation.IsSovietRifle(events[e].PresentationId))
                    {
                        sawAkShotEvent = true;
                    }
                }
                combat.AcknowledgeEvents(1, events.Length);
            }

            Assert.True(sawAkShotEvent, "Expected ShotFired event tagged with WeaponPresentationKind.RifleAk.");
            Assert.False(world.TryGetUnit(enemyInf, out _), "Soviet AK-74 rifleman should eliminate exposed enemy infantry.");
            Assert.True(world.TryGetCombatState(shooter, out WorldState.CombatState cs));
            Assert.True(cs.Ammunition < 240 && (240 - cs.Ammunition) % 3 == 0, "AK-74 fires 3-round bursts.");
        }
    }

    [Fact]
    public void SovietRpg7_AutoFire_ConservesRocketsAgainstInfantry_AndDestroysArmor()
    {
        var (sim, world, rules, catalog) = CreateFlatContext(7402UL);
        using (sim)
        {
            UnitDefinition rpgDef = catalog.RequireDefinition("soviet_rpg");
            ushort rpgIdx = catalog.RequireDefinitionIndex("soviet_rpg");
            UnitDefinition usInfDef = catalog.RequireDefinition("us_rifleman");
            ushort usInfIdx = catalog.RequireDefinitionIndex("us_rifleman");
            UnitDefinition m113Def = catalog.RequireDefinition("m113a1");
            ushort m113Idx = catalog.RequireDefinitionIndex("m113a1");

            CombatSystem combat = rules.FindSystem<CombatSystem>()!;
            sim.SetPlayerAdmitted(1, true);
            sim.SetPlayerAdmitted(2, true);

            // RPG-7 gunner with FireAtWill sees an enemy rifleman at 80 m -> must conserve AT rockets!
            EntityId rpg = world.CreateUnit(1, rpgIdx, new Vector2(200f, 300f), rpgDef.Speed, rpgDef.MaxHealth, 4, initialTurretYaw: -MathF.PI / 2f);
            EntityId enemyInf = world.CreateUnit(2, usInfIdx, new Vector2(280f, 300f), usInfDef.Speed, usInfDef.MaxHealth, 0);
            world.SetFireStance(enemyInf, FireStance.HoldFire);

            for (int i = 0; i < 30; i++) sim.Step();
            Assert.Equal(0, combat.TotalShotsFired);
            Assert.True(world.TryGetCombatState(rpg, out WorldState.CombatState csBefore));
            Assert.Equal(4, csBefore.Ammunition);

            // Enemy M113A1 APC enters at 90 m -> RPG-7 immediately fires PG-7V (55 dmg vs 50 HP) and destroys it!
            EntityId apc = world.CreateUnit(2, m113Idx, new Vector2(290f, 300f), m113Def.Speed, m113Def.MaxHealth, 0);
            world.SetFireStance(apc, FireStance.HoldFire);

            bool sawRpgEvent = false;
            for (int i = 0; i < 200 && world.TryGetUnit(apc, out _); i++)
            {
                sim.Step();
                ReadOnlySpan<CombatEventObservation> events = combat.GetEvents(1);
                for (int e = 0; e < events.Length; e++)
                {
                    if (events[e].Kind == CombatEventKind.ShotFired &&
                        WeaponPresentation.IsAntiTankRocket(events[e].PresentationId))
                    {
                        sawRpgEvent = true;
                    }
                }
                combat.AcknowledgeEvents(1, events.Length);
            }

            Assert.True(sawRpgEvent, "Expected ShotFired event tagged with WeaponPresentationKind.AntiTankRocket.");
            Assert.False(world.TryGetUnit(apc, out _), "PG-7V direct hit (55 dmg) should destroy M113A1 (50 HP).");
            Assert.True(world.TryGetUnit(enemyInf, out _), "Enemy rifleman outside 4.5 m splash remains unharmed by conserved auto-fire.");
        }
    }

    [Fact]
    public void SovietRpg7_PenetratesM60A1Front_AndSplashDamagesNearbyInfantryOnExplicitOrder()
    {
        var (sim, world, rules, catalog) = CreateFlatContext(7403UL);
        using (sim)
        {
            UnitDefinition rpgDef = catalog.RequireDefinition("soviet_rpg");
            ushort rpgIdx = catalog.RequireDefinitionIndex("soviet_rpg");
            UnitDefinition usInfDef = catalog.RequireDefinition("us_rifleman");
            ushort usInfIdx = catalog.RequireDefinitionIndex("us_rifleman");
            UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
            ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");

            DetectionSystem detection = rules.FindSystem<DetectionSystem>()!;
            CombatSystem combat = rules.FindSystem<CombatSystem>()!;
            sim.SetPlayerAdmitted(1, true);
            sim.SetPlayerAdmitted(2, true);

            // Test 1: RPG-7 penetrates M60A1 front armor (220 mm) at 100 m with constant 260 mm HEAT penetration
            EntityId rpg = world.CreateUnit(1, rpgIdx, new Vector2(200f, 300f), rpgDef.Speed, rpgDef.MaxHealth, 4, initialTurretYaw: -MathF.PI / 2f);
            EntityId m60 = world.CreateUnit(2, m60Idx, new Vector2(300f, 300f), m60Def.Speed, m60Def.MaxHealth, 0, initialTurretYaw: MathF.PI / 2f);
            world.SetFireStance(m60, FireStance.HoldFire);
            // Adjacent infantry standing 2.5 m beside the tank should suffer secondary HEAT blast splash!
            EntityId bystander = world.CreateUnit(2, usInfIdx, new Vector2(300f, 302.5f), usInfDef.Speed, usInfDef.MaxHealth, 0);
            world.SetFireStance(bystander, FireStance.HoldFire);

            for (int i = 0; i < 40 && combat.TotalShotsFired == 0; i++) sim.Step();
            Assert.True(combat.TotalShotsFired >= 1, "RPG-7 should fire at frontal M60A1.");

            // Keep stepping until at least one hit damages the M60A1 (260 mm HEAT > 220 mm front)
            for (int i = 0; i < 200; i++)
            {
                if (!world.TryGetCombatState(m60, out WorldState.CombatState curTankCs) || curTankCs.CurrentHealth < m60Def.MaxHealth)
                    break;
                sim.Step();
            }

            bool tankDamagedOrDestroyed = !world.TryGetCombatState(m60, out WorldState.CombatState tankAfterCs) || tankAfterCs.CurrentHealth < m60Def.MaxHealth;
            Assert.True(tankDamagedOrDestroyed, "PG-7V HEAT warhead (260 mm) must penetrate M60A1 frontal armor (220 mm).");

            bool bystanderSplashHit = !world.TryGetCombatState(bystander, out WorldState.CombatState bystanderAfterCs) || bystanderAfterCs.CurrentHealth < usInfDef.MaxHealth;
            Assert.True(bystanderSplashHit, "PG-7V detonation must deal secondary HEAT splash damage to infantry within 4.5 m.");

            // Test 2: Explicit AttackCommand against an infantry unit overrides ammo conservation
            EntityId rpg2 = world.CreateUnit(1, rpgIdx, new Vector2(200f, 500f), rpgDef.Speed, rpgDef.MaxHealth, 4, initialTurretYaw: -MathF.PI / 2f);
            world.SetFireStance(rpg2, FireStance.HoldFire);
            EntityId loneInf = world.CreateUnit(2, usInfIdx, new Vector2(260f, 500f), usInfDef.Speed, usInfDef.MaxHealth, 0);
            world.SetFireStance(loneInf, FireStance.HoldFire);

            for (int i = 0; i < 6; i++) sim.Step();
            Assert.True(detection.TryGetContactByEntity(1, loneInf, out ContactObservation cInf));
            Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, rpg2, cInf.Id)));

            int shotsBefore = combat.TotalShotsFired;
            for (int i = 0; i < 20 && combat.TotalShotsFired == shotsBefore; i++) sim.Step();
            Assert.True(combat.TotalShotsFired > shotsBefore, "Explicit AttackCommand must allow RPG-7 to fire at infantry.");
        }
    }

    [Fact]
    public void SovietRpg7_AttackGround_DamagesDestructiblePanelkaBuilding()
    {
        StaticGeometry geometry = UrbanDestructionPlaygroundScenario.CreateStaticGeometry();
        using Simulation sim = UrbanDestructionPlaygroundScenario.CreateSimulation(geometry, matchSeed: 1464UL);
        WorldState world = sim.World;
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        ushort rpgDefIdx = catalog.RequireDefinitionIndex("soviet_rpg");

        // Locate the Soviet RPG-7 gunner in Player 1's urban assault fireteam
        EntityId rpgId = default;
        for (int i = 0; i < world.Count; i++)
        {
            UnitState u = world.GetUnitAt(i);
            if (u.Owner == 1 && u.Definition == rpgDefIdx && !world.IsContained(u.Id))
            {
                rpgId = u.Id;
                break;
            }
        }
        Assert.NotEqual(default, rpgId);

        StructureId targetSection = new(103);
        Assert.True(world.TryGetDestructibleStructure(targetSection, out DestructibleStructureState before));
        int healthBefore = before.Health;

        Vector3 aimPt = new((before.Min.X + before.Max.X) * 0.5f, before.Min.Y + 3.0f, before.Min.Z);
        Assert.True(sim.TryEnqueue(1, new AttackGroundCommand(1, sim.Tick, rpgId, aimPt, targetSection)));

        for (int i = 0; i < 40; i++)
        {
            sim.Step();
            if (world.TryGetDestructibleStructure(targetSection, out DestructibleStructureState cur) && cur.Health < healthBefore)
                break;
        }

        Assert.True(world.TryGetDestructibleStructure(targetSection, out DestructibleStructureState after));
        Assert.True(after.Health < healthBefore, "RPG-7V AttackGround must deal structural damage to destructible buildings.");
    }

    [Fact]
    public void SovietRpg7_TurnsBodyHullHeadingTowardTarget_WhenFiringAndAfterReload()
    {
        (Simulation sim, WorldState world, OpsRules rules, UnitCatalog catalog) = CreateFlatContext(1984UL);
        using (sim)
        {
            sim.SetPlayerAdmitted(1, true);
            sim.SetPlayerAdmitted(2, true);

            ushort rpgIdx = catalog.RequireDefinitionIndex("soviet_rpg");
            ushort apcIdx = catalog.RequireDefinitionIndex("m113a1");
            UnitDefinition rpgDef = catalog.Units[rpgIdx];
            UnitDefinition apcDef = catalog.Units[apcIdx];

            // Spawn RPG gunner facing south (yaw=0 -> -Z), target directly east (+X -> expected yaw = -Pi/2)
            EntityId rpg = world.CreateUnit(1, rpgIdx, new Vector2(200f, 300f), rpgDef.Speed, rpgDef.MaxHealth, 4, initialTurretYaw: 0f);
            EntityId apc = world.CreateUnit(2, apcIdx, new Vector2(310f, 300f), apcDef.Speed, apcDef.MaxHealth, 0);
            world.SetFireStance(apc, FireStance.HoldFire);

            CombatSystem combat = rules.FindSystem<CombatSystem>()!;
            for (int i = 0; i < 40 && combat.TotalShotsFired == 0; i++) sim.Step();
            Assert.True(combat.TotalShotsFired >= 1, "RPG gunner must engage the M113A1 to the east.");

            // Advance 10 more ticks after the shot (during reload / after kill) and confirm gunner stays facing east!
            for (int i = 0; i < 10; i++) sim.Step();

            Assert.True(world.TryGetCombatState(rpg, out WorldState.CombatState cs));
            float expectedEastYaw = MathF.Atan2(-1f, 0f); // -Pi/2
            float hullError = MathF.Abs(CombatSystem.ShorterArcDelta(cs.HullHeading, expectedEastYaw));
            float turretError = MathF.Abs(CombatSystem.ShorterArcDelta(cs.TurretYaw, expectedEastYaw));
            Assert.True(hullError < 0.05f, $"RPG gunner HullHeading must turn toward shot direction east (-1.571), got {cs.HullHeading:F3}.");
            Assert.True(turretError < 0.05f, $"RPG gunner TurretYaw must stay aligned east (-1.571), got {cs.TurretYaw:F3}.");

            // Also verify AttackGround toward west (-X -> expected yaw = +Pi/2) rotates HullHeading west
            Vector3 westGround = new(120f, 2f, 300f);
            Assert.True(sim.TryEnqueue(1, new AttackGroundCommand(1, sim.Tick, rpg, westGround, default)));
            for (int i = 0; i < 5; i++) sim.Step();
            Assert.True(world.TryGetCombatState(rpg, out WorldState.CombatState csWest));
            float expectedWestYaw = MathF.Atan2(1f, 0f); // +Pi/2
            float westErr = MathF.Abs(CombatSystem.ShorterArcDelta(csWest.HullHeading, expectedWestYaw));
            Assert.True(westErr < 0.05f, $"RPG gunner AttackGround must turn HullHeading west (+1.571), got {csWest.HullHeading:F3}.");
        }
    }

    [Fact]
    public void ProvingGround_KeepsAllFriendlyUnitsAliveOnSpawn_AndSupportsSandboxSpawning()
    {
        StaticGeometry geometry = ProvingGroundScenario.CreateStaticGeometry();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        using Simulation sim = ProvingGroundScenario.CreateSimulation(geometry, catalog, matchSeed: 2048UL);
        WorldState world = sim.World;

        int initialP1Count = 0;
        for (int i = 0; i < world.Count; i++)
        {
            if (world.GetUnitAt(i).Owner == 1) initialP1Count++;
        }
        Assert.True(initialP1Count >= 30, $"Expected >= 30 Soviet units across 5 proving ground zones, got {initialP1Count}.");

        // Run 120 ticks (6 seconds) with no orders: zero Player 1 casualties because OPFOR targets hold/return fire safely
        for (int t = 0; t < 120; t++) sim.Step();

        int afterP1Count = 0;
        for (int i = 0; i < world.Count; i++)
        {
            if (world.GetUnitAt(i).Owner == 1) afterP1Count++;
        }
        Assert.Equal(initialP1Count, afterP1Count);

        // Verify on-the-fly sandbox unit spawning & OPFOR stance switching
        ushort rpgIdx = catalog.RequireDefinitionIndex("soviet_rpg");
        UnitDefinition rpgDef = catalog.Units[rpgIdx];
        Assert.True(sim.TrySpawnSandboxUnit(
            owner: 1,
            definition: rpgIdx,
            position: new Vector2(450f, 520f),
            speed: rpgDef.Speed,
            maxHealth: rpgDef.MaxHealth,
            ammo: catalog.GetAmmoCapacity(rpgDef),
            initialYaw: MathF.PI,
            stance: FireStance.FireAtWill,
            passengerCapacity: 0,
            out EntityId spawnedRpg));
        Assert.True(world.TryGetUnit(spawnedRpg, out _));

        int switched = sim.SetAllUnitsFireStanceForOwner(2, FireStance.FireAtWill);
        Assert.True(switched >= 20, $"Expected >= 20 OPFOR units to switch stance, switched {switched}.");
    }
}
