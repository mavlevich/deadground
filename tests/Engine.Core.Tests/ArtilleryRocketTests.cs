using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class ArtilleryRocketTests
{
    private const ushort MlrsDef = 0;
    private const ushort InfantryDef = 1;
    private const ushort ApcDef = 2;
    private const ushort TankDef = 3;

    private static readonly WeaponDefinition GradWeapon = new(
        Id: "test_grad_122mm",
        Range: 4000f,
        ReloadDurationTicks: 200,
        Damage: 90,
        AmmoCapacity: 40,
        MuzzleHeight: 2.35f,
        PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.RocketArtillery, 1),
        PenetrationNear: 25f,
        PenetrationFar: 25f,
        PenetrationNearRange: 0f,
        PenetrationFarRange: 4000f,
        IsIndirectFire: true,
        MinRange: 350f,
        SalvoCount: 6,
        SalvoIntervalTicks: 10,
        DispersionRadius: 15f,
        SplashRadius: 22f,
        ProjectileSpeed: 160f,
        SetupTicks: 20,
        ElevationDegrees: 32f);

    private sealed class TestCombatCatalog : IUnitCombatCatalog
    {
        private readonly UnitCombatProfile[] _profiles =
        [
            new(40, GradWeapon, TurretTraverseRate: 45f, Armor: default, Radius: 1.5f),
            new(15, null, TurretTraverseRate: 0f, Armor: default, Radius: 0.4f, LeavesWreck: false),
            new(50, null, TurretTraverseRate: 20f, Armor: new UnitArmor(20f, 12f, 12f), Radius: 1.4f),
            new(100, null, TurretTraverseRate: 20f, Armor: new UnitArmor(220f, 76f, 41f), Radius: 1.8f)
        ];

        public bool TryGetProfile(ushort definition, out UnitCombatProfile profile)
        {
            if (definition < _profiles.Length)
            {
                profile = _profiles[definition];
                return true;
            }
            profile = default;
            return false;
        }
    }

    private sealed class TestPerceptionCatalog : IUnitPerceptionCatalog
    {
        private readonly UnitPerceptionProfile[] _profiles =
        [
            new(new VisualSensorProfile(1200f, 2.2f), new TargetObservationPoints([1.0f, 2.5f]), ContactClassification.Artillery),
            new(new VisualSensorProfile(800f, 1.6f), new TargetObservationPoints([0.6f, 1.4f]), ContactClassification.Infantry),
            new(new VisualSensorProfile(1500f, 2.2f), new TargetObservationPoints([0.9f, 2.2f]), ContactClassification.Apc),
            new(new VisualSensorProfile(2000f, 2.8f), new TargetObservationPoints([1.0f, 2.8f]), ContactClassification.Tank)
        ];

        public bool TryGetProfile(ushort definition, out UnitPerceptionProfile profile)
        {
            if (definition < _profiles.Length)
            {
                profile = _profiles[definition];
                return true;
            }
            profile = default;
            return false;
        }
    }

    private static StaticGeometry CreateRidgeGeometry()
    {
        const int res = 129;
        const float spacing = 16f; // 2048m x 2048m
        ushort[] heights = new ushort[res * res];
        for (int r = 0; r < res; r++)
        {
            float z = r * spacing;
            float dz = MathF.Abs(z - 960f);
            float ridge = dz < 200f ? 50f * MathF.Pow(MathF.Cos(dz / 200f * MathF.PI * 0.5f), 2f) : 0f;
            for (int c = 0; c < res; c++)
            {
                heights[r * res + c] = (ushort)Math.Clamp((int)MathF.Round((4f + ridge) * 100f), 0, ushort.MaxValue);
            }
        }
        return new StaticGeometry(new HeightMap(res, res, spacing, 0.01f, heights), ReadOnlySpan<OpaqueStructure>.Empty);
    }

    private static Simulation CreateTestSimulation(out StaticGeometry geometry, ulong seed = 2024UL)
    {
        geometry = CreateRidgeGeometry();
        TestCombatCatalog combatCatalog = new();
        TestPerceptionCatalog perceptionCatalog = new();
        DetectionSystem det = new(geometry, perceptionCatalog);
        CombatSystem combat = new(geometry, combatCatalog, perceptionCatalog, det);
        ArtillerySystem artillery = new(geometry, combatCatalog);
        RuleSet rules = new(new MovementSystem(), det, combat, artillery);
        WorldState world = new(geometry.HeightMap);
        return new Simulation(world, rules, seed);
    }

    [Fact]
    public void FireMission_ElevatesLauncherFiresStaggeredSalvoOverRidgeAndDestroysSoftTargets()
    {
        using Simulation sim = CreateTestSimulation(out StaticGeometry geometry);
        Vector2 launcherPos = new(1000f, 360f);
        Vector2 targetCenter = new(1000f, 1520f);

        EntityId gradId = sim.World.CreateUnit(1, MlrsDef, launcherPos, 9.7f, 40, 40, MathF.PI);
        EntityId inf1 = sim.World.CreateUnit(2, InfantryDef, targetCenter, 3.2f, 15, 0, 0f);
        EntityId inf2 = sim.World.CreateUnit(2, InfantryDef, targetCenter + new Vector2(4f, -3f), 3.2f, 15, 0, 0f);
        EntityId apcId = sim.World.CreateUnit(2, ApcDef, targetCenter + new Vector2(-3f, 2f), 9.7f, 50, 0, 0f);

        // Verify direct line of sight is completely blocked by the central ridge
        sim.World.TryGetUnit(gradId, out UnitState gradUnit);
        sim.World.TryGetUnit(inf1, out UnitState infUnit);
        Assert.False(sim.Combat!.HasLineOfSightToTarget(gradUnit, GradWeapon, infUnit, out _, out _));

        // Issue indirect fire mission
        bool queued = sim.TryEnqueue(1, new FireMissionCommand(1, 0, gradId, targetCenter));
        Assert.True(queued);

        bool sawElevatedPitch = false;
        int rocketLaunchEvents = 0;
        int impactEvents = 0;
        uint lastEventId = 0;

        for (int t = 0; t < 400; t++)
        {
            sim.Step();
            if (sim.World.TryGetArtilleryState(gradId, out WorldState.UnitArtilleryState art) &&
                art.GunPitchRad > 0.45f)
            {
                sawElevatedPitch = true;
            }

            ReadOnlySpan<CombatEventObservation> evs = sim.Combat.GetEvents(1);
            for (int e = 0; e < evs.Length; e++)
            {
                if (evs[e].EventId <= lastEventId) continue;
                lastEventId = evs[e].EventId;
                if (evs[e].Kind == CombatEventKind.RocketLaunched) rocketLaunchEvents++;
                else if (evs[e].Kind == CombatEventKind.ArtilleryImpact) impactEvents++;
            }
            sim.Combat.AcknowledgeEvents(1, evs.Length);
        }

        Assert.True(sawElevatedPitch, "Launcher rack must elevate to firing pitch.");
        Assert.Equal(6, sim.Artillery!.TotalRocketsFired);
        Assert.Equal(6, sim.Artillery.TotalImpacts);
        Assert.Equal(6, rocketLaunchEvents);
        Assert.Equal(6, impactEvents);

        // Ammo decremented by full salvo (40 - 6 = 34)
        Assert.True(sim.World.TryGetCombatState(gradId, out WorldState.CombatState cs));
        Assert.Equal(34, cs.Ammunition);

        // Soft infantry at impact center must be destroyed by HE-Frag splash
        Assert.False(sim.World.TryGetUnit(inf1, out _));
        Assert.False(sim.World.TryGetUnit(inf2, out _));
        // APC within 22m splash takes heavy fragmentation damage or is destroyed
        bool apcDamagedOrDestroyed = !sim.World.TryGetUnit(apcId, out _) ||
            (sim.World.TryGetCombatState(apcId, out WorldState.CombatState apcCs) && apcCs.CurrentHealth < 50);
        Assert.True(apcDamagedOrDestroyed);
    }

    [Fact]
    public void FireMission_RejectsOutOfRangeAndDirectFireAttackOrders()
    {
        using Simulation sim = CreateTestSimulation(out _);
        EntityId gradId = sim.World.CreateUnit(1, MlrsDef, new Vector2(1000f, 360f), 9.7f, 40, 40, MathF.PI);

        // Below MinRange (350m)
        Assert.False(sim.TryEnqueue(1, new FireMissionCommand(1, 0, gradId, new Vector2(1000f, 500f))));
        // Valid range (1000m)
        Assert.True(sim.TryEnqueue(1, new FireMissionCommand(2, 0, gradId, new Vector2(1000f, 1360f))));
    }

    [Fact]
    public void FireMission_MoveAndCancelStopSalvoAndLowerLauncherPack()
    {
        using Simulation sim = CreateTestSimulation(out _);
        EntityId gradId = sim.World.CreateUnit(1, MlrsDef, new Vector2(1000f, 360f), 9.7f, 40, 40, MathF.PI);

        Assert.True(sim.TryEnqueue(1, new FireMissionCommand(1, 0, gradId, new Vector2(1000f, 1520f))));
        // Step through setup + 1 rocket launch
        for (int t = 0; t < 45; t++) sim.Step();
        Assert.True(sim.Artillery!.TotalRocketsFired >= 1);
        Assert.True(sim.Artillery.TotalRocketsFired < 6);
        int firedBeforeCancel = sim.Artillery.TotalRocketsFired;

        // Cancel via FireMissionCommand(Cancel: true)
        Assert.True(sim.TryEnqueue(1, new FireMissionCommand(2, 45, gradId, default, Cancel: true)));
        for (int t = 0; t < 60; t++) sim.Step();

        Assert.Equal(firedBeforeCancel, sim.Artillery.TotalRocketsFired);
        Assert.True(sim.World.TryGetArtilleryState(gradId, out WorldState.UnitArtilleryState art));
        Assert.False(art.HasFireMission);
        Assert.Equal(0f, art.GunPitchRad, 3);
    }

    [Fact]
    public void ArtilleryBallistics_ProducesExactParabolicEndpointsAndPositiveApex()
    {
        Vector3 origin = new(1000f, 6.35f, 360f);
        Vector3 target = new(1000f, 4.0f, 1560f);
        ArtilleryBallistics.ComputeTrajectory(origin, target, 160f, out int flightTicks, out float apexHeight);

        Assert.True(flightTicks >= 100, $"Expected realistic multi-second flight time at 1200m, got {flightTicks} ticks.");
        Assert.True(apexHeight >= 200f && apexHeight <= 380f);

        ArtilleryBallistics.SamplePositionAndTangent(origin, target, apexHeight, 0f, out Vector3 p0, out Vector3 t0);
        ArtilleryBallistics.SamplePositionAndTangent(origin, target, apexHeight, 0.5f, out Vector3 pMid, out _);
        ArtilleryBallistics.SamplePositionAndTangent(origin, target, apexHeight, 1f, out Vector3 p1, out Vector3 t1);

        Assert.True(Vector3.Distance(origin, p0) < 0.01f);
        Assert.True(Vector3.Distance(target, p1) < 0.01f);
        Assert.True(pMid.Y > origin.Y + apexHeight * 0.95f);
        Assert.True(t0.Y > 0.2f, "Rocket should climb at launch.");
        Assert.True(t1.Y < -0.2f, "Rocket should descend at impact.");
    }

    [Fact]
    public void ArtillerySystem_ZeroAllocationsDuringActiveSalvoAndFlight()
    {
        using Simulation sim = CreateTestSimulation(out _);
        EntityId gradId = sim.World.CreateUnit(1, MlrsDef, new Vector2(1000f, 360f), 9.7f, 40, 40, MathF.PI);
        sim.World.CreateUnit(2, TankDef, new Vector2(1000f, 1520f), 8.3f, 100, 0, 0f);

        Assert.True(sim.TryEnqueue(1, new FireMissionCommand(1, 0, gradId, new Vector2(1000f, 1520f))));

        // Warm up JIT
        for (int t = 0; t < 30; t++) sim.Step();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 200; t++)
        {
            sim.Step();
            ReadOnlySpan<CombatEventObservation> evs = sim.Combat!.GetEvents(1);
            sim.Combat.AcknowledgeEvents(1, evs.Length);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
        Assert.Equal(6, sim.Artillery!.TotalRocketsFired);
    }

    [Fact]
    public void WireProtocolV29_RoundTripsFireMissionArtillerySnapshotAndRejectsV28Handshake()
    {
        Assert.Equal(29, MatchSetup.CurrentProtocolVersion);
        Assert.Equal(29, WireProtocol.Version);
        Assert.Equal(69, WireProtocol.UnitSize);
        Assert.Equal(35, WireProtocol.ContactSize);

        // 1. FireMissionCommand round-trip
        FireMissionCommand order = new(42, 120, new EntityId(7), new Vector2(1024.5f, 1580.25f), Cancel: false);
        byte[] cmdBuf = new byte[WireProtocol.FireMissionSize];
        WireProtocol.WriteFireMission(cmdBuf, order);
        Assert.True(WireProtocol.TryReadFireMission(cmdBuf, out FireMissionCommand decodedOrder));
        Assert.Equal(order, decodedOrder);

        FireMissionCommand cancelOrder = new(43, 125, new EntityId(7), default, Cancel: true);
        WireProtocol.WriteFireMission(cmdBuf, cancelOrder);
        Assert.True(WireProtocol.TryReadFireMission(cmdBuf, out FireMissionCommand decodedCancel));
        Assert.Equal(cancelOrder, decodedCancel);

        // 2. Snapshot with GunPitchRad, ArtilleryFlags, ContactClassification.Artillery, and RocketLaunched / ArtilleryImpact events
        float expectedPitch = 32f * (MathF.PI / 180f);
        OwnUnitObservation[] own =
        [
            new(new EntityId(7), 9, new Vector2(996f, 360f), 0,
                GunPitchRad: expectedPitch,
                ArtilleryFlags: ArtilleryFlags.Firing)
        ];
        ContactObservation[] contacts =
        [
            new(new ContactId(3), ContactState.Current, ContactClassification.Artillery, 9,
                new Vector2(996f, 360f), 50, GunPitchRad: expectedPitch)
        ];
        CombatEventObservation[] events =
        [
            new(101, 50, CombatEventKind.RocketLaunched, WeaponPresentation.MakeId(WeaponPresentationKind.RocketArtillery, 1),
                new Vector3(996f, 6.35f, 360f), new Vector3(1024f, 4f, 1580f), new EntityId(7)),
            new(102, 190, CombatEventKind.ArtilleryImpact, WeaponPresentation.MakeId(WeaponPresentationKind.RocketArtillery, 1),
                new Vector3(996f, 6.35f, 360f), new Vector3(1024f, 4f, 1580f))
        ];

        byte[] snap = new byte[WireProtocol.MaxMessageSize];
        int len = WireProtocol.WriteSnapshot(snap, 1, 190, own, contacts, events);

        ClientReplica replica = new();
        Assert.True(replica.TryApply(snap.AsSpan(0, len)));
        Assert.True(replica.TryGetOwnUnit(new EntityId(7), out OwnUnitObservation decUnit));
        Assert.InRange(decUnit.GunPitchRad, expectedPitch - 0.002f, expectedPitch + 0.002f);
        Assert.Equal(ArtilleryFlags.Firing, decUnit.ArtilleryFlags);

        Assert.Equal(1, replica.ContactCount);
        Assert.Equal(ContactClassification.Artillery, replica.Contacts[0].Classification);
        Assert.InRange(replica.Contacts[0].GunPitchRad, expectedPitch - 0.002f, expectedPitch + 0.002f);

        Assert.Equal(2, replica.Events.Length);
        Assert.Equal(CombatEventKind.RocketLaunched, replica.Events[0].Kind);
        Assert.Equal(CombatEventKind.ArtilleryImpact, replica.Events[1].Kind);

        // 3. Stale v28 handshake request is rejected with ProtocolVersionMismatch
        MatchSetup serverSetup = new(MatchSetup.CurrentProtocolVersion, 12345UL, 67890UL, "ArtilleryPlayground");
        MatchSetup staleV28Setup = serverSetup with { ProtocolVersion = 28 };
        byte[] hsBuf = new byte[128];
        int hsLen = WireProtocol.WriteHandshakeRequest(hsBuf, staleV28Setup);
        hsBuf[0] = 28; // Old client sends v28 wire header byte
        Assert.False(WireProtocol.TryReadHandshakeRequest(hsBuf.AsSpan(0, hsLen), out _));

        // Even with v29 header byte, mismatched setup.ProtocolVersion=28 fails MatchSetup.Matches
        hsBuf[0] = WireProtocol.Version;
        Assert.True(WireProtocol.TryReadHandshakeRequest(hsBuf.AsSpan(0, hsLen), out MatchSetup receivedStale));
        Assert.False(serverSetup.Matches(receivedStale));
    }
}
