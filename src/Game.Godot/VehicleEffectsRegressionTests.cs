using DeadGround.Engine;
using Godot;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

// Vehicle effects: muzzle blast dust, barrel smoke, hull rock and movement dust.
internal static class VehicleEffectsRegressionTests
{
    private const float Step = 1f / 60f;

    internal static void Run(Node3D scene)
    {
        CheckParticleMaterials();
        CheckGunParticles(scene);
        CheckGunParticleEvents(scene);
        CheckHullRock(scene);
        CheckMovementDust(scene);
    }

    // Billboards drop the per-particle scale unless keep scale is on, and every sprite becomes 1 m.
    private static void CheckParticleMaterials()
    {
        GpuParticles3D[] emitters =
        [
            ParticleEffects.CreateBlastDust(),
            ParticleEffects.CreateBarrelSmoke(),
            ParticleEffects.CreateMovementDust(Vector3.One),
        ];
        try
        {
            foreach (GpuParticles3D emitter in emitters)
            {
                StandardMaterial3D material = (StandardMaterial3D)((QuadMesh)emitter.DrawPass1).Material;
                Require(material.BillboardMode == BaseMaterial3D.BillboardModeEnum.Particles && material.BillboardKeepScale,
                    $"{emitter.Name} billboard must keep the particle scale.");
                Require(!emitter.LocalCoords, $"{emitter.Name} must emit in world space so it stays behind.");
                Require(emitter.ProcessMaterial is ParticleProcessMaterial { ScaleMin: >= 1f },
                    $"{emitter.Name} sprites are too small to read from the game camera.");
                // World-space dust trails behind the emitter; a small box culls the trail early.
                Require(emitter.VisibilityAabb.Size.X >= 2f * UnitView.FullDustSpeed * emitter.Lifetime + 4f,
                    $"{emitter.Name} visibility box is smaller than its trail.");
                Require(ReferenceEquals(emitter.DrawPass1, emitters[0].DrawPass1), "Emitters must share one draw pass.");
            }
            GpuParticles3D second = ParticleEffects.CreateBlastDust();
            bool shared = ReferenceEquals(second.ProcessMaterial, emitters[0].ProcessMaterial);
            second.Free();
            Require(shared, "Blast dust emitters must share their process material.");
        }
        finally
        {
            foreach (GpuParticles3D emitter in emitters) emitter.Free();
        }
    }

    private static void CheckGunParticles(Node3D scene)
    {
        HeightMap map = HeightMap.CreatePrototype();
        CombatEffects effects = new(map);
        scene.AddChild(effects);
        try
        {
            float ground = map.GetHeight(20f, 20f);
            Vector3 low = new(20f, ground + 2f, 20f);
            int children = effects.GetChildCount();

            effects.SpawnDirectShot(low, low + new Vector3(40f, 0f, 0f));
            GpuParticles3D dust = effects.BlastDust[0];
            Require(dust.Emitting, "A low gun did not raise muzzle blast dust.");
            Require(dust.AmountRatio > 0.35f && dust.AmountRatio < 1f, "Blast dust strength must follow muzzle height.");
            Require(Mathf.Abs(dust.GlobalPosition.Y - (ground + 1f)) < 0.01f, "Blast dust must start near the ground.");
            Require(dust.GlobalPosition.X < low.X, "Blast dust must start behind the muzzle.");
            Require(effects.BarrelSmoke[0].Emitting, "The shot did not start barrel smoke.");
            Require(effects.BarrelSmoke[0].GlobalPosition.IsEqualApprox(low), "Barrel smoke must start at the muzzle.");

            for (int i = 0; i < CombatEffects.MaxBlastDust; i++)
                effects.SpawnDirectShot(low, low + new Vector3(0f, 0f, -40f));
            Require(effects.GetChildCount() == children, "Gun particles must come from fixed pools.");
            Require(effects.BlastDust[1].Emitting, "The blast dust pool did not wrap around.");

            effects.Clear();
            Vector3 high = new(20f, ground + CombatEffects.BlastDustMaxMuzzleHeight + 1f, 20f);
            effects.SpawnDirectShot(high, high + new Vector3(40f, 0f, 0f));
            bool anyDust = false;
            foreach (GpuParticles3D blast in effects.BlastDust) anyDust |= blast.Emitting;
            Require(!anyDust, "A gun high above the ground must not raise blast dust.");
            bool anySmoke = false;
            foreach (GpuParticles3D smoke in effects.BarrelSmoke) anySmoke |= smoke.Emitting;
            Require(anySmoke, "A high gun still needs barrel smoke.");

            effects.Clear();
            foreach (GpuParticles3D blast in effects.BlastDust) Require(!blast.Emitting, "Clear left blast dust running.");
            foreach (GpuParticles3D smoke in effects.BarrelSmoke) Require(!smoke.Emitting, "Clear left barrel smoke running.");
        }
        finally { effects.Free(); }
    }

    // Network shots go through ProcessEvents: muzzle from the shooter view, age gate, fast-forward.
    private static void CheckGunParticleEvents(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet views = new(parent, map);
            CombatEffects effects = new(map);
            parent.AddChild(effects);
            UnitView tank = new("prototype_tank", "soviet");
            views.Views.Add(new EntityId(41), tank);
            parent.AddChild(tank.Root);
            tank.UpdatePosition(new Vector3(20f, map.GetHeight(20f, 20f), 20f));
            tank.AimAt(new Vector3(60f, 0f, 20f));
            Require(tank.TryGetMuzzlePosition(out Vector3 muzzle), "Tank has no muzzle.");

            effects.ProcessEvents([Shot(1, 100, muzzle)], views, 102);
            GpuParticles3D smoke = effects.BarrelSmoke[0];
            Require(smoke.Emitting && smoke.GlobalPosition.DistanceTo(muzzle) < 0.01f, "A network shot did not start smoke at the muzzle.");
            Require(Mathf.IsEqualApprox(smoke.Preprocess, 2 * Simulation.TickSeconds), "A late shot must fast-forward its particles.");
            Require(effects.BlastDust[0].Emitting, "A network shot did not raise blast dust.");

            effects.Clear();
            int ticks = Mathf.CeilToInt(CombatEffects.GunParticlesMaxEventAge / Simulation.TickSeconds) + 1;
            effects.ProcessEvents([Shot(2, 100, muzzle)], views, 100 + ticks);
            foreach (GpuParticles3D blast in effects.BlastDust) Require(!blast.Emitting, "An old shot must not raise blast dust.");
            foreach (GpuParticles3D gas in effects.BarrelSmoke) Require(!gas.Emitting, "An old shot must not start barrel smoke.");
        }
        finally { parent.Free(); }
    }

    private static CombatEventObservation Shot(uint id, int tick, Vector3 origin) => new(
        EventId: id,
        ServerTick: tick,
        Kind: CombatEventKind.ShotFired,
        PresentationId: 1,
        Origin: new NumericsVector3(origin.X, origin.Y, origin.Z),
        Target: new NumericsVector3(origin.X + 40f, origin.Y, origin.Z),
        ShooterUnitId: new EntityId(41));

    private static void CheckHullRock(Node3D scene)
    {
        UnitView view = new("prototype_tank", "soviet");
        scene.AddChild(view.Root);
        try
        {
            float scale = view.Model.Scale.X;
            view.UpdatePosition(new Vector3(0f, 0f, 0f));
            view.UpdatePosition(new Vector3(5f, 0f, 0f));
            Basis rest = view.Model.Basis;

            view.ApplyRecoil(Vector3.Right);
            view.UpdateRecoil(0.05);
            Require(view.RockAngle > 0f, "The shot did not rock the hull.");
            Vector3 nose = view.Model.Basis * Vector3.Forward;
            Require(nose.Y > 0f, "A shot over the front must lift the nose.");

            float peak = 0f;
            for (int i = 0; i < 60; i++)
            {
                view.UpdateRecoil(Step);
                peak = Mathf.Max(peak, Mathf.Abs(view.RockAngle));
            }
            Require(peak < 0.05f, "Hull rock must stay a few degrees at most.");
            Require(Mathf.IsEqualApprox(view.Model.Scale.X, scale), "Hull rock changed the model scale.");

            // A long frame must not make the spring blow up.
            view.ApplyRecoil(Vector3.Right);
            view.UpdateRecoil(1.0);
            Require(Mathf.Abs(view.RockAngle) < 0.05f, "A long frame made the hull rock unstable.");

            for (int i = 0; i < 300; i++) view.UpdateRecoil(Step);
            Require(view.RockAngle == 0f, "Hull rock did not settle.");
            Require(view.Model.Basis.IsEqualApprox(rest), "A settled hull must return to its heading pose.");

            view.ApplyRecoil(Vector3.Right);
            view.UpdateRecoil(0.05);
            view.ResetRecoil();
            Require(view.RockAngle == 0f && view.Model.Basis.IsEqualApprox(rest), "Reset left the hull rocked.");
        }
        finally { view.Root.Free(); }
    }

    private static void CheckMovementDust(Node3D scene)
    {
        UnitView view = new("prototype_tank", "soviet");
        UnitView plain = new("prototype_tank");
        scene.AddChild(view.Root);
        scene.AddChild(plain.Root);
        try
        {
            GpuParticles3D dust = view.LeftDust ?? throw new InvalidOperationException("Tracked vehicle has no movement dust.");
            Require(!dust.Emitting, "Movement dust must start idle.");

            float x = Drive(view, 0f, 6f, 1f);
            Require(dust.Emitting && dust.AmountRatio > 0.8f, "Driving at full speed did not raise dust.");
            float fast = dust.AmountRatio;

            Drive(view, x, 0f, 2f);
            Require(!dust.Emitting && view.DustSpeed < 0.3f, "A stopped vehicle kept raising dust.");

            // A correction snap is not driving.
            x += 8f;
            view.UpdatePosition(new Vector3(x, 0f, 0f));
            Drive(view, x, 0f, 0.3f);
            Require(!dust.Emitting && view.DustSpeed < 0.3f, "A position snap raised movement dust.");

            x = Drive(view, x, 2f, 1f);
            Require(dust.Emitting && dust.AmountRatio < fast, "Dust must follow speed.");

            view.UpdateLod(110f);
            Drive(view, x, 6f, 0.5f);
            Require(!dust.Emitting, "Movement dust must stop on the distant zoom.");

            Require(plain.LeftDust is null, "The prototype box has no running gear for dust.");
            Drive(plain, 0f, 6f, 0.2f);
        }
        finally
        {
            view.Root.Free();
            plain.Root.Free();
        }
    }

    private static float Drive(UnitView view, float x, float speed, float seconds)
    {
        int frames = Mathf.RoundToInt(seconds / Step);
        for (int i = 0; i < frames; i++)
        {
            x += speed * Step;
            view.UpdatePosition(new Vector3(x, 0f, 0f));
            view.UpdateEffects(Step);
        }
        return x;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
