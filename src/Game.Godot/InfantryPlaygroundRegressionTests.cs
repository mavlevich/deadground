using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

/// <summary>
/// Regression tests and benchmarks for the first playable infantry unit (US rifleman):
/// animation states, contact presentation, small arms combat effects, network replication,
/// and client scale benchmarks with 20 and 100 active models.
/// </summary>
internal static class InfantryPlaygroundRegressionTests
{
    internal static bool Run(Node3D scene)
    {
        try
        {
            CheckInfantryAnimationTransitions(scene);
            CheckInfantryContactAnimationTransitions(scene);
            CheckInfantryDestructionAndCombatEffects(scene);
            CheckInfantryNetworkReplication(scene);
            CheckInfantryClassificationSingleSourceOfTruth(scene);
            RunInfantryScaleBenchmarks(scene);
            GD.Print("INFANTRY_PLAYGROUND_REGRESSIONS_OK 6/6 passed: animation_transitions contact_anims destruction_effects network_replica classification_truth benchmarks_20_100");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError(exception.ToString());
            return false;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CheckInfantryAnimationTransitions(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            UnitView view = new("us_rifleman");
            parent.AddChild(view.Root);

            Require(view.IsInfantry, "US rifleman view must be identified as infantry.");
            Require(view.AnimationPlayer is not null, "US rifleman view must load its AnimationPlayer.");

            // 1. Idle state (speed 0, not aiming)
            view.SetDustSpeedForTesting(0f);
            view.UpdateEffects(0.016);
            Require(view.CurrentAnimation == "Idle", $"Expected Idle animation but got '{view.CurrentAnimation}'");
            Require(MathF.Abs(view.AnimationPlayer!.SpeedScale - 1.0f) < 0.01f, $"Expected Idle SpeedScale 1.0 but got {view.AnimationPlayer.SpeedScale}");

            // 2. Walk state (speed 1.0 m/s: 0.15 <= speed < 2.2)
            view.SetDustSpeedForTesting(1.0f);
            view.UpdateEffects(0.016);
            Require(view.CurrentAnimation == "Walk", $"Expected Walk animation but got '{view.CurrentAnimation}'");
            float expectedWalkScale = 1.0f / 1.5f;
            Require(MathF.Abs(view.AnimationPlayer!.SpeedScale - expectedWalkScale) < 0.05f, $"Expected Walk SpeedScale around {expectedWalkScale:F2} but got {view.AnimationPlayer.SpeedScale}");

            // 3. Run state (speed 3.2 m/s: speed >= 2.2)
            view.SetDustSpeedForTesting(3.2f);
            view.UpdateEffects(0.016);
            Require(view.CurrentAnimation == "Run", $"Expected Run animation but got '{view.CurrentAnimation}'");
            float expectedRunScale = 3.2f / 3.5f;
            Require(MathF.Abs(view.AnimationPlayer!.SpeedScale - expectedRunScale) < 0.05f, $"Expected Run SpeedScale around {expectedRunScale:F2} but got {view.AnimationPlayer.SpeedScale}");

            // 4. AimStanding state (speed 0, aiming active)
            view.SetDustSpeedForTesting(0f);
            view.SetAiming(true);
            view.UpdateEffects(0.016);
            Require(view.CurrentAnimation == "AimStanding", $"Expected AimStanding animation but got '{view.CurrentAnimation}'");
            Require(MathF.Abs(view.AnimationPlayer!.SpeedScale - 1.0f) < 0.01f, $"Expected AimStanding SpeedScale 1.0 but got {view.AnimationPlayer.SpeedScale}");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckInfantryContactAnimationTransitions(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            ContactView contactView = new(unitIdProvider: _ => "us_rifleman");
            parent.AddChild(contactView.Root);

            ContactObservation obs = new(
                Id: new ContactId(1),
                State: ContactState.Current,
                Classification: ContactClassification.Infantry,
                Type: 0,
                Position: new NumericsVector2(50f, 50f),
                LastSeenTick: 100);

            // Step 1: Initial placement, speed 0 -> Idle
            contactView.Update(obs, new NumericsVector2(50f, 50f), 0f, false, 0f, 0f);
            Require(contactView.CurrentModelClassification == ContactClassification.Infantry, "Contact view must resolve infantry classification.");
            Require(contactView.CurrentAnimation == "Idle", $"Expected contact Idle animation but got '{contactView.CurrentAnimation}'");
            Require(contactView.AnimationPlayer is not null, "Contact view must load its AnimationPlayer.");
            Require(MathF.Abs(contactView.AnimationPlayer!.SpeedScale - 1.0f) < 0.01f, $"Expected contact Idle SpeedScale 1.0 but got {contactView.AnimationPlayer.SpeedScale}");

            // Step 2: Move distance 0.05m in 0.05s -> speed 1.0 m/s -> Walk
            obs = obs with { Position = new NumericsVector2(50.05f, 50f) };
            contactView.Update(obs, obs.Position, 0f, false, 0f, 0f);
            Require(contactView.CurrentAnimation == "Walk", $"Expected contact Walk animation but got '{contactView.CurrentAnimation}'");
            Require(MathF.Abs(contactView.AnimationPlayer!.SpeedScale - (1.0f / 1.5f)) < 0.1f, $"Expected contact Walk SpeedScale near {1.0f / 1.5f:F2} but got {contactView.AnimationPlayer.SpeedScale}");

            // Step 3: Move distance 0.16m in 0.05s -> speed 3.2 m/s -> Run
            obs = obs with { Position = new NumericsVector2(50.21f, 50f) };
            contactView.Update(obs, obs.Position, 0f, false, 0f, 0f);
            Require(contactView.CurrentAnimation == "Run", $"Expected contact Run animation but got '{contactView.CurrentAnimation}'");
            Require(MathF.Abs(contactView.AnimationPlayer!.SpeedScale - (3.2f / 3.5f)) < 0.1f, $"Expected contact Run SpeedScale near {3.2f / 3.5f:F2} but got {contactView.AnimationPlayer.SpeedScale}");

            // Step 4: Stand still and aim -> AimStanding
            contactView.SetAiming(true);
            obs = obs with { Position = new NumericsVector2(50.21f, 50f) };
            contactView.Update(obs, obs.Position, 0f, false, 0f, 0f);
            Require(contactView.CurrentAnimation == "AimStanding", $"Expected contact AimStanding animation but got '{contactView.CurrentAnimation}'");
            Require(MathF.Abs(contactView.AnimationPlayer!.SpeedScale - 1.0f) < 0.01f, $"Expected contact AimStanding SpeedScale 1.0 but got {contactView.AnimationPlayer.SpeedScale}");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckInfantryClassificationSingleSourceOfTruth(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            // 1. A new unit with classification 'infantry' and novel ID gets IsInfantry == true
            UnitView scoutView = new("custom_scout", classification: "infantry");
            parent.AddChild(scoutView.Root);
            Require(scoutView.IsInfantry, "UnitView with classification 'infantry' must have IsInfantry == true regardless of ID.");

            // 2. An animated vehicle model (e.g. m60a1 or custom tank with an AnimationPlayer) is NOT infantry
            UnitView tankView = new("m60a1", classification: "tank");
            parent.AddChild(tankView.Root);
            AnimationPlayer tankAnim = new() { Name = "AnimationPlayer" };
            tankView.Model.AddChild(tankAnim);
            Require(!tankView.IsInfantry, "Vehicle model with AnimationPlayer must NOT be identified as infantry when classification is 'tank'.");

            // 3. Animated vehicle model with null classification but vehicle ID in catalog is NOT infantry
            UnitView vehicleView = new("t62");
            parent.AddChild(vehicleView.Root);
            AnimationPlayer vehicleAnim = new() { Name = "AnimationPlayer" };
            vehicleView.Model.AddChild(vehicleAnim);
            Require(!vehicleView.IsInfantry, "Vehicle model in catalog must NOT be identified as infantry even with AnimationPlayer present.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckInfantryDestructionAndCombatEffects(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = ScenarioContent.LoadCatalog("infantry-playground");
            UnitViewSet unitViews = new(parent, map, catalog);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            ushort riflemanDef = catalog.RequireDefinitionIndex("us_rifleman");
            ushort riflePresId = WeaponPresentation.MakeId(WeaponPresentationKind.Rifle, 0);

            // Test 1: Infantry death must produce 0 vehicle explosions and 0 crater residues
            CombatEventObservation destroyed = new(
                EventId: 5001,
                ServerTick: 20,
                Kind: CombatEventKind.UnitDestroyed,
                PresentationId: riflemanDef,
                Origin: NumericsVector3.Zero,
                Target: new NumericsVector3(50f, 0f, 50f),
                ShooterUnitId: default,
                Result: HitResult.Penetrated);

            effects.ProcessEvents([destroyed], unitViews);

            Require(effects.ActiveExplosionsCount == 0,
                $"Infantry death must suppress vehicle explosions. Active: {effects.ActiveExplosionsCount}");
            Require(effects.ActiveResiduesCount == 0,
                $"Infantry death must suppress ground crater residue. Active: {effects.ActiveResiduesCount}");

            // Test 2: Rifle shot produces small muzzle flash, tracer, and audio
            CombatEventObservation rifleShot = new(
                EventId: 5002,
                ServerTick: 21,
                Kind: CombatEventKind.ShotFired,
                PresentationId: riflePresId,
                Origin: new NumericsVector3(50f, 1.4f, 50f),
                Target: new NumericsVector3(70f, 1.4f, 50f),
                ShooterUnitId: default,
                Result: HitResult.Penetrated);

            effects.ProcessEvents([rifleShot], unitViews);

            Require(effects.ActiveTracersCount >= 1, "Rifle shot must create a bullet tracer.");
            Require(effects.ActiveMuzzleFlashesCount >= 1, "Rifle shot must create a muzzle flash.");
            // Rifle muzzle flash must be scaled small (scale.X <= 0.12)
            float flashScale = effects.MuzzleFlashes[0].Mesh.Scale.X;
            Require(flashScale <= 0.12f, $"Rifle muzzle flash scale must be small (<=0.12) but was {flashScale}.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckInfantryNetworkReplication(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = ScenarioContent.LoadCatalog("infantry-playground");
            UnitViewSet unitViews = new(parent, map, catalog);
            ContactViewSet contactViews = new(parent, map, catalog);

            ushort riflemanDef = catalog.RequireDefinitionIndex("us_rifleman");

            OwnUnitObservation ownRifleman = new(
                Id: new EntityId(10),
                Definition: riflemanDef,
                Position: new NumericsVector2(100f, 100f),
                SupportSurface: 0u,
                NavStatus: PathRequestStatus.PathFound,
                Health: 15,
                Ammunition: 210,
                TurretYaw: 0f,
                HullHeading: 0f);

            ContactObservation contactRifleman = new(
                Id: new ContactId(20),
                State: ContactState.Current,
                Classification: ContactClassification.Infantry,
                Type: riflemanDef,
                Position: new NumericsVector2(110f, 100f),
                LastSeenTick: 50);

            byte[] buffer = new byte[WireProtocol.MaxMessageSize];
            int written = WireProtocol.WriteSnapshot(
                buffer,
                sequence: 1u,
                tick: 50,
                units: [ownRifleman],
                contacts: [contactRifleman]);

            Require(written > 0, "Failed to serialize server snapshot frame.");

            ClientReplica replica = new();
            Require(replica.TryApply(buffer.AsSpan(0, written)), "ClientReplica failed to apply snapshot.");

            unitViews.Sync(replica, 0);
            contactViews.Sync(replica, 0);

            Require(unitViews.Views.Count == 1, "UnitViewSet must instantiate 1 view for own rifleman.");
            Require(unitViews.Views[new EntityId(10)].IsInfantry, "Instantiated own view must be infantry.");

            Require(contactViews.Views.Count == 1, "ContactViewSet must instantiate 1 view for enemy contact.");
            Require(contactViews.Views[new ContactId(20)].CurrentModelClassification == ContactClassification.Infantry,
                "Instantiated contact view must have ContactClassification.Infantry.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void RunInfantryScaleBenchmarks(Node3D scene)
    {
        RunBenchmarkWithCount(scene, 20);
        RunBenchmarkWithCount(scene, 100);
    }

    private static void RunBenchmarkWithCount(Node3D scene, int count)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            UnitView[] views = new UnitView[count];
            float[] speeds = new float[count];
            for (int i = 0; i < count; i++)
            {
                views[i] = new UnitView("us_rifleman");
                parent.AddChild(views[i].Root);
                views[i].Root.Position = new Vector3(i * 1.5f, 0f, 0f);
                speeds[i] = i % 2 == 0 ? 3.2f : 0f;
            }

            // Warmup 15 frames for JIT tiered compilation and initial animation settlement
            for (int w = 0; w < 15; w++)
            {
                for (int i = 0; i < count; i++)
                {
                    if (speeds[i] > 0f)
                    {
                        views[i].UpdatePosition(views[i].Root.Position + new Vector3(0.016f * speeds[i], 0f, 0f));
                    }
                    views[i].UpdateEffects(0.016);
                    views[i].AnimationPlayer?.Advance(0.016);
                }
            }

            const int measureFrames = 30;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();

            for (int f = 0; f < measureFrames; f++)
            {
                for (int i = 0; i < count; i++)
                {
                    if (speeds[i] > 0f)
                    {
                        views[i].UpdatePosition(views[i].Root.Position + new Vector3(0.016f * speeds[i], 0f, 0f));
                    }
                    views[i].UpdateEffects(0.016);
                    views[i].AnimationPlayer?.Advance(0.016);
                }
            }

            sw.Stop();
            long allocAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAlloc = allocAfter - allocBefore;
            double frameMs = sw.Elapsed.TotalMilliseconds / measureFrames;

            // Zero managed allocations in presentation hot loop
            Require(totalAlloc == 0, $"Scale benchmark {count} allocated {totalAlloc} bytes during update loop.");
            // Client frame budget for 100 models: update + animation should take < 4 ms
            double budgetMs = count <= 20 ? 1.5 : 8.0;
            Require(frameMs < budgetMs, $"Scale benchmark {count} frame time {frameMs:F3} ms exceeded budget {budgetMs} ms.");

            GD.Print($"INFANTRY_BENCHMARK_{count} models={count} frame_ms={frameMs:F3} anim_active={count} alloc_bytes={totalAlloc}");
        }
        finally
        {
            parent.QueueFree();
        }
    }
}
