using DeadGround.Engine;
using DeadGround.Engine.Net;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

internal static class CombatFeedbackRegressionTests
{
    private static readonly ushort CannonPresentationId = WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0);
    private static readonly ushort MgPresentationId = WeaponPresentation.MakeId(WeaponPresentationKind.MachineGun, 0);

    internal static bool Run(Node3D scene)
    {
        CheckShotFiredSequenceAndDeduplication(scene);
        CheckOwnTankMuzzleTransformAndRecoilIsolation(scene);
        CheckTookFireTargetFeedbackOnly(scene);
        CheckUnitDestroyedExplosionAndResidue(scene);
        CheckRepeatedBatchesAndDestructionNearPendingImpact(scene);
        CheckPoolSaturationAndBurstSurvives(scene);
        CheckClearAndRetryStopsAudioAndCleansState(scene);
        CheckFrameRateIndependenceAndLargeDelta(scene);
        CheckWarmedPathAllocationsZero(scene);
        CheckMultipleSnapshotsBetweenFramesRetainAllEvents(scene);
        CheckStaleBufferedEventsDiscardExpiredFeedback(scene);
        CheckTracerMonotonicForwardProgression(scene);
        CheckMachineGunBurstFeedback(scene);
        CheckNonPenetratingRicochetFeedback(scene);
        CheckMachineGunBurstSoundPriority(scene);
        CheckMissFeedback(scene);
        GD.Print("COMBAT_FEEDBACK_REGRESSIONS_OK 16/16 passed");
        return true;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CheckShotFiredSequenceAndDeduplication(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(parent, map);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            OwnUnitObservation tank = new(
                new EntityId(10), 0, new NumericsVector2(20, 20), 0, Health: 120);

            CombatEventObservation shotEvent = new(
                EventId: 1001,
                ServerTick: 5,
                Kind: CombatEventKind.ShotFired,
                PresentationId: CannonPresentationId,
                Origin: new NumericsVector3(20, 1.9f, 20),
                Target: new NumericsVector3(40, 1.5f, 20),
                ShooterUnitId: tank.Id);

            int size = WireProtocol.WriteSnapshot(bytes, 1, 5, [tank], [], [shotEvent]);
            Require(replica.TryApply(bytes.AsSpan(0, size)), "Snapshot apply failed.");
            unitViews.Sync(replica, 5);

            effects.ProcessEvents(replica.Events, unitViews);

            // Case 1: ShotFired produces muzzle effect, shot cue, pending impact and one audio request
            Require(effects.ActiveMuzzleFlashesCount == 1, "Muzzle flash was not activated.");
            Require(effects.ActiveTracersCount == 1, "Tracer shot cue was not activated.");
            Require(effects.PendingImpactsCount == 1, "Pending cosmetic impact was not queued.");
            Require(effects.TotalAudioRequests == 1, "Shot did not make exactly one audio request.");
            Require(unitViews.Views[tank.Id].RecoilOffset != Vector3.Zero, "Shooter tank did not receive recoil.");

            Vector3 recoilBefore = unitViews.Views[tank.Id].RecoilOffset;

            // Submitting same EventId again does not replay effects, audio, or restart recoil
            effects.ProcessEvents(replica.Events, unitViews);
            Require(effects.ActiveMuzzleFlashesCount == 1, "Duplicate event spawned second muzzle flash.");
            Require(effects.ActiveTracersCount == 1, "Duplicate event spawned second tracer.");
            Require(effects.TotalAudioRequests == 1, "Duplicate event repeated audio request.");
            Require(unitViews.Views[tank.Id].RecoilOffset == recoilBefore, "Duplicate event altered recoil.");

            // Step time forward past flight duration (10 frames @ 0.016s = 0.16s) to trigger impact feedback
            for (int i = 0; i < 10; i++)
            {
                effects.Update(0.016);
            }
            Require(effects.ActiveImpactsCount >= 1 || effects.ActiveSparksCount >= 1,
                "Impact feedback was not triggered when tracer arrived.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckOwnTankMuzzleTransformAndRecoilIsolation(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(parent, map);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            UnitView tank = new("prototype_tank");
            unitViews.Views.Add(new EntityId(501), tank);
            parent.AddChild(tank.Root);
            tank.UpdatePosition(new Vector3(30, 0, 30));
            tank.Model.Rotation = new Vector3(0, Mathf.Pi * 0.25f, 0); // 45 deg hull yaw
            tank.AimAt(new Vector3(60, 0, 30)); // turret rotated independently
            tank.Model.Scale = new Vector3(1.2f, 1.2f, 1.2f); // non-unit model scale

            UnitView recon = new("recon_vehicle");
            unitViews.Views.Add(new EntityId(502), recon);
            parent.AddChild(recon.Root);
            recon.UpdatePosition(new Vector3(32, 0, 30)); // 2m away

            Require(tank.TryGetMuzzleTransform(out Transform3D muzzleXform), "Tank did not expose Muzzle transform.");

            CombatEventObservation ownShot = new(
                EventId: 2001,
                ServerTick: 10,
                Kind: CombatEventKind.ShotFired,
                PresentationId: CannonPresentationId,
                Origin: new NumericsVector3(30, 1.9f, 30), // Coarse delivered origin
                Target: new NumericsVector3(60, 1.5f, 30),
                ShooterUnitId: new EntityId(501));

            effects.ProcessEvents([ownShot], unitViews);

            // Muzzle flash uses transformed Muzzle position accounting for hull, turret and scale
            Vector3 flashPos = effects.MuzzleFlashes[0].Mesh.Position;
            Require(flashPos.DistanceTo(muzzleXform.Origin) < 0.2f,
                $"Muzzle flash position ({flashPos}) did not match transformed muzzle ({muzzleXform.Origin}).");

            // Recoil isolation: shooter recoils, adjacent recon does not
            Require(tank.RecoilOffset != Vector3.Zero, "Shooter tank did not receive recoil.");
            Require(recon.RecoilOffset == Vector3.Zero, "Adjacent recon vehicle wrongly received recoil.");

            // Unknown / missing shooter ID safely uses event endpoints
            CombatEventObservation enemyShot = new(
                EventId: 2002,
                ServerTick: 11,
                Kind: CombatEventKind.ShotFired,
                PresentationId: CannonPresentationId,
                Origin: new NumericsVector3(80, 2f, 80),
                Target: new NumericsVector3(30, 0f, 30),
                ShooterUnitId: default);

            effects.ProcessEvents([enemyShot], unitViews);
            Require(effects.ActiveTracersCount == 2, "Enemy shot without shooter ID failed to spawn tracer.");
            Require(effects.Tracers[1].Origin == new Vector3(80, 2f, 80), "Enemy shot did not use delivered origin.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckTookFireTargetFeedbackOnly(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(parent, map);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            Vector3 syntheticOrigin = new(10, 0, 10);
            Vector3 target = new(50, 0, 50);

            CombatEventObservation tookFire = new(
                EventId: 3001,
                ServerTick: 20,
                Kind: CombatEventKind.TookFire,
                PresentationId: CannonPresentationId,
                Origin: new NumericsVector3(syntheticOrigin.X, syntheticOrigin.Y, syntheticOrigin.Z),
                Target: new NumericsVector3(target.X, target.Y, target.Z),
                ShooterUnitId: default);

            effects.ProcessEvents([tookFire], unitViews);

            // TookFire: target feedback only
            Require(effects.ActiveImpactsCount >= 1 || effects.ActiveSparksCount >= 1,
                "TookFire did not create target impact feedback.");
            Require(effects.ActiveMuzzleFlashesCount == 0,
                "TookFire wrongly placed a muzzle flash at synthetic origin.");
            Require(effects.ActiveTracersCount == 0,
                "TookFire wrongly placed a tracer from synthetic origin.");

            // Verify synthetic origin is not used as an exact shooter position
            for (int i = 0; i < CombatEffects.MaxImpacts; i++)
            {
                if (effects.Impacts[i].Active)
                {
                    Require(effects.Impacts[i].Mesh.Position.DistanceTo(syntheticOrigin) > 5f,
                        "Impact was placed at synthetic bearing origin instead of target.");
                }
            }
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckUnitDestroyedExplosionAndResidue(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(parent, map);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            Vector3 target = new(45, 0, 45);
            CombatEventObservation destroyed = new(
                EventId: 4001,
                ServerTick: 30,
                Kind: CombatEventKind.UnitDestroyed,
                PresentationId: 9999, // Unknown presentation key must be handled safely
                Origin: default,
                Target: new NumericsVector3(target.X, target.Y, target.Z),
                ShooterUnitId: default);

            effects.ProcessEvents([destroyed], unitViews);

            Require(effects.ActiveExplosionsCount == 1, "UnitDestroyed did not spawn explosion.");
            Require(effects.ActiveResiduesCount == 1, "UnitDestroyed did not spawn ground residue.");

            // Explosion is larger than standard impact
            float explosionRadius = ((SphereMesh)effects.Explosions[0].Mesh.Mesh).Radius;
            Require(explosionRadius > 1.2f, "Explosion was not visually distinct from ordinary impact.");

            // Residue is placed on the terrain surface
            float expectedY = map.GetHeight(target.X, target.Z) + 0.03f;
            Require(MathF.Abs(effects.Residues[0].Mesh.Position.Y - expectedY) < 0.05f,
                "Residue was not placed at ground height.");

            // Live model is not recreated
            Require(unitViews.Views.Count == 0, "UnitDestroyed recreated or added a live vehicle view.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckRepeatedBatchesAndDestructionNearPendingImpact(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(parent, map);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            Vector3 target = new(35, 0, 35);
            CombatEventObservation shot = new(
                EventId: 5001,
                ServerTick: 40,
                Kind: CombatEventKind.ShotFired,
                PresentationId: CannonPresentationId,
                Origin: new NumericsVector3(10, 1.9f, 35),
                Target: new NumericsVector3(target.X, target.Y, target.Z),
                ShooterUnitId: default);

            effects.ProcessEvents([shot], unitViews);
            Require(effects.PendingImpactsCount == 1, "Pending impact was not queued.");

            // Destruction at the target arrives before cosmetic impact delivers
            CombatEventObservation destroyed = new(
                EventId: 5002,
                ServerTick: 41,
                Kind: CombatEventKind.UnitDestroyed,
                PresentationId: CannonPresentationId,
                Origin: default,
                Target: new NumericsVector3(target.X, target.Y, target.Z),
                ShooterUnitId: default);

            effects.ProcessEvents([destroyed], unitViews);

            // Pending impact at destruction location cancelled to avoid duplicate feedback
            Require(effects.PendingImpactsCount == 0,
                "Destruction did not cancel pending cosmetic impact at the same location.");
            Require(effects.ActiveExplosionsCount == 1, "Destruction explosion was not spawned.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckPoolSaturationAndBurstSurvives(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(parent, map);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            // 100 unique shots burst beyond capacity
            CombatEventObservation[] burst = new CombatEventObservation[100];
            for (uint i = 0; i < 100; i++)
            {
                burst[i] = new CombatEventObservation(
                    EventId: 6000 + i,
                    ServerTick: 50,
                    Kind: CombatEventKind.ShotFired,
                    PresentationId: CannonPresentationId,
                    Origin: new NumericsVector3(10 + i * 0.1f, 1.9f, 10),
                    Target: new NumericsVector3(40 + i * 0.1f, 1.5f, 40),
                    ShooterUnitId: default);
            }

            effects.ProcessEvents(burst, unitViews);

            // Active pools stay strictly within capacity limits
            Require(effects.ActiveTracersCount <= CombatEffects.MaxTracers, "Tracer pool capacity exceeded.");
            Require(effects.ActiveMuzzleFlashesCount <= CombatEffects.MaxMuzzleFlashes, "Muzzle flash pool capacity exceeded.");
            Require(effects.ActiveSmokePuffsCount <= CombatEffects.MaxSmokePuffs, "Smoke puff pool capacity exceeded.");
            Require(effects.PendingImpactsCount <= CombatEffects.MaxPendingImpacts, "Pending impact pool capacity exceeded.");
            Require(effects.DroppedEffectsCount > 0, "Saturation did not increment dropped effects counter.");

            // Advance time until all transient effects expire
            effects.Update(1.0);
            Require(effects.ActiveTracersCount == 0, "Tracers did not expire after 1.0s.");
            Require(effects.ActiveMuzzleFlashesCount == 0, "Muzzle flashes did not expire after 1.0s.");
            Require(effects.PendingImpactsCount == 0, "Pending impacts remained after 1.0s.");

            // Advance time until ground residues expire (15.0s lifetime)
            effects.Update(16.0);
            Require(effects.ActiveResiduesCount == 0, "Residues did not expire after 16.0s.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckClearAndRetryStopsAudioAndCleansState(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(parent, map);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            UnitView tank = new("prototype_tank");
            EntityId tankId = new(710);
            unitViews.Views.Add(tankId, tank);
            parent.AddChild(tank.Root);
            tank.UpdatePosition(new Vector3(20, 0, 20));

            int initialChildCount = effects.GetChildCount();

            // Run 3 retry cycles verifying state is cleaned and audio is stopped
            for (int cycle = 0; cycle < 3; cycle++)
            {
                CombatEventObservation shot = new(
                    EventId: (uint)(7001 + cycle * 10), ServerTick: 60, Kind: CombatEventKind.ShotFired, PresentationId: CannonPresentationId,
                    Origin: new NumericsVector3(20, 1.9f, 20), Target: new NumericsVector3(50, 1.5f, 20),
                    ShooterUnitId: tankId);
                CombatEventObservation destroyed = new(
                    EventId: (uint)(7002 + cycle * 10), ServerTick: 60, Kind: CombatEventKind.UnitDestroyed, PresentationId: CannonPresentationId,
                    Origin: default, Target: new NumericsVector3(50, 0, 20), ShooterUnitId: default);

                effects.ProcessEvents([shot, destroyed], unitViews);
                Require(effects.ActiveEffectsCount > 0, $"Cycle {cycle}: Effects were not active before Clear.");
                Require(tank.RecoilOffset != Vector3.Zero, $"Cycle {cycle}: Tank did not recoil.");

                // Trigger Clear and ResetRecoil
                effects.Clear();
                unitViews.ResetRecoil();

                Require(effects.ActiveEffectsCount == 0, $"Cycle {cycle}: Clear did not reset all active effects to 0.");
                Require(effects.PendingImpactsCount == 0, $"Cycle {cycle}: Clear did not reset pending impacts to 0.");
                Require(effects.ActiveResiduesCount == 0, $"Cycle {cycle}: Clear did not clear ground residue.");
                Require(tank.RecoilOffset == Vector3.Zero, $"Cycle {cycle}: Recoil was not reset to zero.");

                for (int p = 0; p < CombatEffects.MaxAudioPlayers; p++)
                {
                    Require(!effects.AudioPlayers[p].Playing, $"Cycle {cycle}: Audio player {p} was not stopped.");
                }

                Require(effects.GetChildCount() == initialChildCount,
                    $"Cycle {cycle}: Clear accumulated or leaked child nodes ({effects.GetChildCount()} vs {initialChildCount}).");
            }
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckFrameRateIndependenceAndLargeDelta(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(parent, map);
            CombatEffects effects30 = new(map);
            CombatEffects effects144 = new(map);
            parent.AddChild(effects30);
            parent.AddChild(effects144);

            CombatEventObservation shot = new(
                EventId: 8001, ServerTick: 70, Kind: CombatEventKind.ShotFired, PresentationId: CannonPresentationId,
                Origin: new NumericsVector3(20, 1.9f, 20), Target: new NumericsVector3(50, 1.5f, 20),
                ShooterUnitId: default);

            effects30.ProcessEvents([shot], unitViews);
            effects144.ProcessEvents([shot], unitViews);

            // Simulate 1.0s at 30 FPS vs 144 FPS
            for (int i = 0; i < 30; i++) effects30.Update(1.0 / 30.0);
            for (int i = 0; i < 144; i++) effects144.Update(1.0 / 144.0);

            Require(effects30.ActiveTracersCount == 0 && effects144.ActiveTracersCount == 0,
                "Tracers did not both expire after 1.0s across different FPS.");

            // Huge delta stall step (10.0 seconds) leaves no stuck effects or crash
            CombatEffects effectsStall = new(map);
            parent.AddChild(effectsStall);
            effectsStall.ProcessEvents([shot], unitViews);
            effectsStall.Update(20.0);

            Require(effectsStall.ActiveEffectsCount == 0, "Large delta left stuck active effects.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckWarmedPathAllocationsZero(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(parent, map);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            CombatEventObservation[] batch = [
                new CombatEventObservation(9001, 80, CombatEventKind.ShotFired, CannonPresentationId,
                    new NumericsVector3(20, 1.9f, 20), new NumericsVector3(40, 1.5f, 20), default),
                new CombatEventObservation(9002, 80, CombatEventKind.TookFire, CannonPresentationId,
                    new NumericsVector3(10, 0, 10), new NumericsVector3(30, 0, 30), default)
            ];

            // Warm up
            for (int i = 0; i < 10; i++)
            {
                effects.ProcessEvents(batch, unitViews);
                effects.Update(0.016);
            }
            effects.Clear();

            // Measure allocations in warmed hot path
            long before = GC.GetAllocatedBytesForCurrentThread();

            for (uint i = 0; i < 20; i++)
            {
                batch[0] = new CombatEventObservation(9100 + i, 80, CombatEventKind.ShotFired, CannonPresentationId,
                    new NumericsVector3(20, 1.9f, 20), new NumericsVector3(40, 1.5f, 20), default);
                batch[1] = new CombatEventObservation(9200 + i, 80, CombatEventKind.TookFire, CannonPresentationId,
                    new NumericsVector3(10, 0, 10), new NumericsVector3(30, 0, 30), default);

                effects.ProcessEvents(batch, unitViews);
                effects.Update(0.016);
            }

            long after = GC.GetAllocatedBytesForCurrentThread();
            long allocated = after - before;

            Require(allocated == 0, $"Warmed hot path allocated {allocated} bytes (expected 0 bytes).");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckMultipleSnapshotsBetweenFramesRetainAllEvents(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(parent, map);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            OwnUnitObservation tank = new(
                new EntityId(10), 0, new NumericsVector2(20, 20), 0, Health: 120);

            CombatEventObservation shotEvent = new(
                EventId: 3001, ServerTick: 1, Kind: CombatEventKind.ShotFired, PresentationId: CannonPresentationId,
                Origin: new NumericsVector3(20, 1.9f, 20), Target: new NumericsVector3(40, 1.5f, 20),
                ShooterUnitId: tank.Id);

            CombatEventObservation tookFireEvent = new(
                EventId: 3002, ServerTick: 2, Kind: CombatEventKind.TookFire, PresentationId: CannonPresentationId,
                Origin: new NumericsVector3(0.5f, 0f, 0.866f), Target: new NumericsVector3(20, 1.8f, 20),
                ShooterUnitId: default);

            // Snapshot 1 arrives with ShotFired
            int size1 = WireProtocol.WriteSnapshot(bytes, 1, 1, [tank], [], [shotEvent]);
            Require(replica.TryApply(bytes.AsSpan(0, size1)), "Snapshot 1 apply failed.");

            // Snapshot 2 arrives before client frame processes events (frame hitch / burst)
            int size2 = WireProtocol.WriteSnapshot(bytes, 2, 2, [tank], [], [tookFireEvent]);
            Require(replica.TryApply(bytes.AsSpan(0, size2)), "Snapshot 2 apply failed.");

            // Snapshot 3 arrives with empty events
            int size3 = WireProtocol.WriteSnapshot(bytes, 3, 3, [tank], [], []);
            Require(replica.TryApply(bytes.AsSpan(0, size3)), "Snapshot 3 apply failed.");

            // Client frame finally executes and processes replica events
            Require(replica.EventCount == 2, $"Expected 2 accumulated events, got {replica.EventCount}.");
            effects.ProcessEvents(replica.Events, unitViews);

            // Both events must have been presented without dropping either
            Require(effects.ActiveTracersCount == 1, "Tracer from Snapshot 1 was dropped.");
            Require(effects.ActiveSparksCount >= 1, "Impact sparks from Snapshot 2 were dropped.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckStaleBufferedEventsDiscardExpiredFeedback(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(parent, map);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            UnitView tankView = new("prototype_tank");
            EntityId tankId = new(10);
            unitViews.Views.Add(tankId, tankView);
            parent.AddChild(tankView.Root);
            tankView.UpdatePosition(new Vector3(20, 0, 20));

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            OwnUnitObservation tank = new(
                tankId, 0, new NumericsVector2(20, 20), 0, Health: 120);

            // Case 1: Snapshot at tick 1 containing a shot.
            CombatEventObservation oldShot = new(
                EventId: 10001,
                ServerTick: 1,
                Kind: CombatEventKind.ShotFired,
                PresentationId: CannonPresentationId,
                Origin: new NumericsVector3(20, 1.9f, 20),
                Target: new NumericsVector3(40, 1.5f, 20),
                ShooterUnitId: tank.Id);

            int size1 = WireProtocol.WriteSnapshot(bytes, 1, 1, [tank], [], [oldShot]);
            Require(replica.TryApply(bytes.AsSpan(0, size1)), "Snapshot 1 apply failed.");

            // Empty snapshot arrives at tick 401 (20 seconds later) before client presentation runs.
            int size2 = WireProtocol.WriteSnapshot(bytes, 2, 401, [tank], [], []);
            Require(replica.TryApply(bytes.AsSpan(0, size2)), "Snapshot 2 apply failed.");

            // Consume replica events at tick 401
            effects.ProcessEvents(replica.Events, unitViews, currentTick: 401);

            // Stale shot from tick 1 (20 seconds old) must be completely discarded:
            Require(effects.ActiveMuzzleFlashesCount == 0, "Stale shot spawned muzzle flash after stall.");
            Require(effects.ActiveTracersCount == 0, "Stale shot spawned tracer after stall.");
            Require(effects.ActiveSmokePuffsCount == 0, "Stale shot spawned smoke puff after stall.");
            Require(effects.PendingImpactsCount == 0, "Stale shot queued pending impact after stall.");
            Require(effects.TotalAudioRequests == 0, "Stale shot played audio after stall.");
            Require(tankView.RecoilOffset == Vector3.Zero, "Stale shot triggered recoil on tank.");

            effects.Clear();
            effects.ResetCounters();

            // Case 2: Snapshot at tick 1 containing UnitDestroyed, consumed at tick 401 (20 seconds old)
            CombatEventObservation veryOldDestroyed = new(
                EventId: 10002,
                ServerTick: 1,
                Kind: CombatEventKind.UnitDestroyed,
                PresentationId: CannonPresentationId,
                Origin: default,
                Target: new NumericsVector3(30, 0, 30),
                ShooterUnitId: default);

            effects.ProcessEvents([veryOldDestroyed], unitViews, currentTick: 401);
            Require(effects.ActiveExplosionsCount == 0, "Stale destroyed event spawned explosion.");
            Require(effects.ActiveResiduesCount == 0, "Stale destroyed event spawned residue after 20s.");
            Require(effects.TotalAudioRequests == 0, "Stale destroyed event played audio.");

            effects.Clear();
            effects.ResetCounters();

            // Case 3: Snapshot at tick 361 containing UnitDestroyed, consumed at tick 401 (40 ticks = 2.0s gap)
            // Explosion (0.60s) and audio (0.20s) expired, but residue lasts 15.0s and is spawned at elapsed age 2.0s.
            CombatEventObservation twoSecondOldDestroyed = new(
                EventId: 10003,
                ServerTick: 361,
                Kind: CombatEventKind.UnitDestroyed,
                PresentationId: CannonPresentationId,
                Origin: default,
                Target: new NumericsVector3(30, 0, 30),
                ShooterUnitId: default);

            effects.ProcessEvents([twoSecondOldDestroyed], unitViews, currentTick: 401);
            Require(effects.ActiveExplosionsCount == 0, "Explosion was not expired for 2s old destruction.");
            Require(effects.ActiveResiduesCount == 1, "Residue was not initialized for 2s old destruction.");
            Require(effects.TotalAudioRequests == 0, "Audio was not suppressed for 2s old destruction.");
            Require(effects.Residues[0].Lifetime > 1.9f, "Residue did not start at elapsed age.");

            effects.Clear();
            effects.ResetCounters();

            // Case 4: Multiple serialized snapshots with long gap before consumption
            ClientReplica batchReplica = new();
            CombatEventObservation tick10Shot = new(10004, 10, CombatEventKind.ShotFired, 1,
                new NumericsVector3(20, 1.9f, 20), new NumericsVector3(40, 1.5f, 20), default);
            CombatEventObservation tick50Shot = new(10005, 50, CombatEventKind.ShotFired, 1,
                new NumericsVector3(20, 1.9f, 20), new NumericsVector3(40, 1.5f, 20), default);
            CombatEventObservation tick400Shot = new(10006, 400, CombatEventKind.ShotFired, 1,
                new NumericsVector3(20, 1.9f, 20), new NumericsVector3(40, 1.5f, 20), default);

            int b1 = WireProtocol.WriteSnapshot(bytes, 1, 10, [tank], [], [tick10Shot]);
            Require(batchReplica.TryApply(bytes.AsSpan(0, b1)), "Batch snap 1 failed.");
            int b2 = WireProtocol.WriteSnapshot(bytes, 2, 50, [tank], [], [tick50Shot]);
            Require(batchReplica.TryApply(bytes.AsSpan(0, b2)), "Batch snap 2 failed.");
            int b3 = WireProtocol.WriteSnapshot(bytes, 3, 400, [tank], [], [tick400Shot]);
            Require(batchReplica.TryApply(bytes.AsSpan(0, b3)), "Batch snap 3 failed.");

            // Consume all accumulated events at currentTick = 401
            effects.ProcessEvents(batchReplica.Events, unitViews, currentTick: 401);

            Require(effects.ActiveTracersCount == 1,
                $"Expected exactly 1 fresh tracer, got {effects.ActiveTracersCount}.");
            Require(effects.ActiveMuzzleFlashesCount == 1,
                $"Expected exactly 1 fresh muzzle flash, got {effects.ActiveMuzzleFlashesCount}.");
            Require(effects.TotalAudioRequests == 1,
                $"Expected exactly 1 fresh audio request, got {effects.TotalAudioRequests}.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckTracerMonotonicForwardProgression(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(parent, map);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            Vector3 origin = new(10, 1.9f, 10);
            Vector3 target = new(70, 1.5f, 10); // 60m shot
            Vector3 direction = (target - origin).Normalized();

            CombatEventObservation shot = new(
                EventId: 20001, ServerTick: 1, Kind: CombatEventKind.ShotFired, PresentationId: CannonPresentationId,
                Origin: new NumericsVector3(origin.X, origin.Y, origin.Z),
                Target: new NumericsVector3(target.X, target.Y, target.Z),
                ShooterUnitId: default);

            effects.ProcessEvents([shot], unitViews);

            Require(effects.ActiveTracersCount == 1, "Tracer was not spawned.");
            MeshInstance3D tracerMesh = effects.Tracers[0].Mesh;

            // 1. Verify BoxMesh unit size and effective transverse scale
            BoxMesh boxMesh = (BoxMesh)tracerMesh.Mesh;
            Require(boxMesh.Size == new Vector3(1.0f, 1.0f, 1.0f),
                $"Tracer BoxMesh size must be (1, 1, 1) unit mesh, got {boxMesh.Size}.");
            Require(MathF.Abs(tracerMesh.Scale.X - CombatEffects.TracerThickness) < 0.001f,
                $"Tracer scale X ({tracerMesh.Scale.X}) does not match TracerThickness ({CombatEffects.TracerThickness}).");
            Require(MathF.Abs(tracerMesh.Scale.Y - CombatEffects.TracerThickness) < 0.001f,
                $"Tracer scale Y ({tracerMesh.Scale.Y}) does not match TracerThickness ({CombatEffects.TracerThickness}).");

            // Effective transverse dimension: Mesh.Size.X * Scale.X = 1.0 * 0.28 = 0.28m
            float effectiveWidth = boxMesh.Size.X * tracerMesh.Scale.X;
            float effectiveHeight = boxMesh.Size.Y * tracerMesh.Scale.Y;
            Require(MathF.Abs(effectiveWidth - 0.28f) < 0.001f,
                $"Effective tracer width {effectiveWidth} is not 0.28m.");
            Require(MathF.Abs(effectiveHeight - 0.28f) < 0.001f,
                $"Effective tracer height {effectiveHeight} is not 0.28m.");

            // 2. Monotonic forward progression test at different frame rates: 30, 60, 120, 144 FPS
            int[] frameRates = [30, 60, 120, 144];
            uint eventIdCounter = 20002;

            foreach (int fps in frameRates)
            {
                effects.Clear();
                CombatEventObservation testShot = new(
                    EventId: eventIdCounter++, ServerTick: 1, Kind: CombatEventKind.ShotFired, PresentationId: CannonPresentationId,
                    Origin: new NumericsVector3(origin.X, origin.Y, origin.Z),
                    Target: new NumericsVector3(target.X, target.Y, target.Z),
                    ShooterUnitId: default);

                effects.ProcessEvents([testShot], unitViews);
                Require(effects.ActiveTracersCount == 1, "Tracer not spawned for FPS test.");

                Vector3 prevPos = effects.Tracers[0].Mesh.Position;
                float dt = 1.0f / fps;
                int maxSteps = (int)(effects.Tracers[0].TotalDuration * fps) + 5;

                for (int step = 0; step < maxSteps && effects.Tracers[0].Active; step++)
                {
                    effects.Update(dt);
                    if (!effects.Tracers[0].Active) break;

                    Vector3 curPos = effects.Tracers[0].Mesh.Position;
                    Vector3 delta = curPos - prevPos;
                    float progress = delta.Dot(direction);

                    Require(progress >= -0.0001f,
                        $"Tracer moved backwards at {fps} FPS on step {step}: progress={progress:F5} along direction.");

                    Require(MathF.Abs(effects.Tracers[0].Mesh.Scale.X - CombatEffects.TracerThickness) < 0.001f,
                        $"Update altered tracer scale X at step {step}.");
                    Require(MathF.Abs(effects.Tracers[0].Mesh.Scale.Y - CombatEffects.TracerThickness) < 0.001f,
                        $"Update altered tracer scale Y at step {step}.");

                    prevPos = curPos;
                }
            }
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckMachineGunBurstFeedback(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(parent, map);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            CombatEventObservation mgEvent = new(
                EventId: 5001,
                ServerTick: 5,
                Kind: CombatEventKind.ShotFired,
                PresentationId: MgPresentationId,
                Origin: new NumericsVector3(20, 1.9f, 20),
                Target: new NumericsVector3(40, 1.5f, 20),
                ShooterUnitId: default,
                Result: HitResult.Penetrated);

            effects.ProcessEvents([mgEvent], unitViews);

            Require(effects.ActiveMuzzleFlashesCount == 1, "MG burst did not spawn muzzle flash.");
            Require(effects.ActiveTracersCount == 1, "MG burst did not spawn tracer.");
            Require(effects.MuzzleFlashes[0].InitialScaleVector.X < 0.3f, "MG muzzle flash should have lighter scale than cannon.");
            Require(effects.Tracers[0].Thickness < CombatEffects.TracerThickness, "MG tracer should have thinner scale than cannon.");
            Require(effects.TotalAudioRequests == 1, "MG burst did not make an audio request.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckNonPenetratingRicochetFeedback(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(parent, map);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            // Deflected TookFire event: should spawn sparks and a ricochet tracer, but no impact mesh or smoke puff
            CombatEventObservation deflectedTookFire = new(
                EventId: 6001,
                ServerTick: 5,
                Kind: CombatEventKind.TookFire,
                PresentationId: MgPresentationId,
                Origin: new NumericsVector3(20, 1.9f, 20),
                Target: new NumericsVector3(40, 1.5f, 20),
                ShooterUnitId: default,
                Result: HitResult.Deflected);

            effects.ProcessEvents([deflectedTookFire], unitViews);

            Require(effects.ActiveSparksCount >= 1, "Deflected hit did not spawn sparks.");
            Require(effects.ActiveTracersCount >= 1, "Deflected hit did not spawn ricochet tracer.");
            Require(effects.ActiveImpactsCount == 0, "Deflected hit should not spawn heavy impact mesh.");
            Require(effects.ActiveSmokePuffsCount == 0, "Deflected hit should not spawn impact smoke puff.");

            // Penetrating TookFire event: spawns impact mesh and smoke puff
            CombatEffects penetratingEffects = new(map);
            parent.AddChild(penetratingEffects);

            CombatEventObservation penetratedTookFire = new(
                EventId: 6002,
                ServerTick: 5,
                Kind: CombatEventKind.TookFire,
                PresentationId: CannonPresentationId,
                Origin: new NumericsVector3(20, 1.9f, 20),
                Target: new NumericsVector3(40, 1.5f, 20),
                ShooterUnitId: default,
                Result: HitResult.Penetrated);

            penetratingEffects.ProcessEvents([penetratedTookFire], unitViews);

            Require(penetratingEffects.ActiveImpactsCount >= 1, "Penetrating hit must spawn impact mesh.");
            Require(penetratingEffects.ActiveSmokePuffsCount >= 1, "Penetrating hit must spawn smoke puff.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckMachineGunBurstSoundPriority(Node3D scene)
    {
        Require(CombatEffects.SoundPriority.MachineGunBurst < CombatEffects.SoundPriority.Cannon,
            "MachineGunBurst priority must be strictly lower than Cannon.");

        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(parent, map);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            int playerSlots = CombatEffects.MaxAudioPlayers;

            CombatEventObservation[] cannonEvents = new CombatEventObservation[playerSlots];
            for (uint i = 0; i < playerSlots; i++)
            {
                cannonEvents[i] = new CombatEventObservation(
                    EventId: 7001 + i,
                    ServerTick: 5,
                    Kind: CombatEventKind.ShotFired,
                    PresentationId: CannonPresentationId,
                    Origin: new NumericsVector3(20 + i, 1.9f, 20),
                    Target: new NumericsVector3(40 + i, 1.5f, 20),
                    ShooterUnitId: default);
            }

            effects.ProcessEvents(cannonEvents, unitViews);

            Require(effects.DroppedAudioVoicesCount == 0, "No audio voices should be dropped during initial fill.");
            for (int i = 0; i < playerSlots; i++)
            {
                Require(effects.GetVoicePriority(i) == CombatEffects.SoundPriority.Cannon,
                    $"Audio player {i} must have Cannon priority.");
            }

            CombatEventObservation mgBurst = new(
                EventId: 7009,
                ServerTick: 5,
                Kind: CombatEventKind.ShotFired,
                PresentationId: MgPresentationId,
                Origin: new NumericsVector3(25, 1.9f, 25),
                Target: new NumericsVector3(45, 1.5f, 25),
                ShooterUnitId: default);

            effects.ProcessEvents([mgBurst], unitViews);

            Require(effects.DroppedAudioVoicesCount == 1,
                "Machine gun burst should be dropped when all voices are playing higher-priority cannon.");
            for (int i = 0; i < playerSlots; i++)
            {
                Require(effects.GetVoicePriority(i) == CombatEffects.SoundPriority.Cannon,
                    $"Audio player {i} must retain Cannon priority after machine gun burst.");
            }

            CombatEffects effectsMg = new(map);
            parent.AddChild(effectsMg);

            CombatEventObservation[] mgEvents = new CombatEventObservation[playerSlots];
            for (uint i = 0; i < playerSlots; i++)
            {
                mgEvents[i] = new CombatEventObservation(
                    EventId: 7010 + i,
                    ServerTick: 5,
                    Kind: CombatEventKind.ShotFired,
                    PresentationId: MgPresentationId,
                    Origin: new NumericsVector3(20 + i, 1.9f, 20),
                    Target: new NumericsVector3(40 + i, 1.5f, 20),
                    ShooterUnitId: default);
            }

            effectsMg.ProcessEvents(mgEvents, unitViews);

            Require(effectsMg.DroppedAudioVoicesCount == 0, "No audio voices should be dropped during initial MG fill.");
            for (int i = 0; i < playerSlots; i++)
            {
                Require(effectsMg.GetVoicePriority(i) == CombatEffects.SoundPriority.MachineGunBurst,
                    $"Audio player {i} must have MachineGunBurst priority.");
            }

            CombatEventObservation cannonShot = new(
                EventId: 7020,
                ServerTick: 5,
                Kind: CombatEventKind.ShotFired,
                PresentationId: CannonPresentationId,
                Origin: new NumericsVector3(25, 1.9f, 25),
                Target: new NumericsVector3(45, 1.5f, 25),
                ShooterUnitId: default);

            effectsMg.ProcessEvents([cannonShot], unitViews);

            Require(effectsMg.DroppedAudioVoicesCount == 0,
                "Cannon shot successfully evicted an MG voice and was not dropped.");
            int cannonCount = 0;
            for (int i = 0; i < playerSlots; i++)
            {
                if (effectsMg.GetVoicePriority(i) == CombatEffects.SoundPriority.Cannon)
                {
                    cannonCount++;
                }
            }
            Require(cannonCount == 1, "Exactly one audio player must now play Cannon sound.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckMissFeedback(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(parent, map);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            // 1. TookFire event with HitResult.Miss: triggers ground impact feedback (dust puff),
            // but no metal sparks and no vehicle penetration mesh
            CombatEventObservation missTookFire = new(
                EventId: 8001,
                ServerTick: 5,
                Kind: CombatEventKind.TookFire,
                PresentationId: CannonPresentationId,
                Origin: new NumericsVector3(20, 1.9f, 20),
                Target: new NumericsVector3(40, 0.0f, 20),
                ShooterUnitId: default,
                Result: HitResult.Miss);

            effects.ProcessEvents([missTookFire], unitViews);

            Require(effects.ActiveSmokePuffsCount >= 1, "Miss took-fire must spawn ground impact smoke or dust puff.");
            Require(effects.ActiveSparksCount == 0, "Miss took-fire must not spawn vehicle sparks.");
            Require(effects.ActiveImpactsCount == 0, "Miss took-fire must not spawn vehicle impact mesh.");

            // Ground impact sound must play GroundImpactAudio, never the metallic TookFireAudio
            bool playedGroundAudio = false;
            bool playedMetallicAudio = false;
            for (int i = 0; i < effects.AudioPlayers.Length; i++)
            {
                if (effects.AudioPlayers[i].Stream == effects.GroundImpactAudio)
                {
                    playedGroundAudio = true;
                }
                if (effects.AudioPlayers[i].Stream == effects.TookFireAudio)
                {
                    playedMetallicAudio = true;
                }
            }
            Require(playedGroundAudio, "Miss took-fire must play ground impact audio stream.");
            Require(!playedMetallicAudio, "Miss took-fire must never play metallic took-fire audio stream.");

            // 2. ShotFired with HitResult.Miss: tracer flies to impact point, pending impact triggers ground puff
            CombatEffects shotEffects = new(map);
            parent.AddChild(shotEffects);

            CombatEventObservation missShot = new(
                EventId: 8002,
                ServerTick: 5,
                Kind: CombatEventKind.ShotFired,
                PresentationId: CannonPresentationId,
                Origin: new NumericsVector3(20, 1.9f, 20),
                Target: new NumericsVector3(45, 0.0f, 22),
                ShooterUnitId: default,
                Result: HitResult.Miss);

            shotEffects.ProcessEvents([missShot], unitViews);

            Require(shotEffects.ActiveTracersCount == 1, "Miss shot must spawn tracer.");
            Require(shotEffects.PendingImpactsCount == 1, "Miss shot must queue pending ground impact.");

            for (int i = 0; i < 15; i++)
            {
                shotEffects.Update(0.016);
            }

            Require(shotEffects.ActiveSmokePuffsCount >= 1, "Miss pending impact must trigger ground smoke or dust puff.");
            Require(shotEffects.ActiveSparksCount == 0, "Miss pending impact must not spawn vehicle sparks.");
            Require(shotEffects.ActiveImpactsCount == 0, "Miss pending impact must not spawn vehicle impact mesh.");
        }
        finally
        {
            parent.QueueFree();
        }
    }
}

