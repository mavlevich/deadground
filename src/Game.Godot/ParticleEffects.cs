using Godot;

namespace DeadGround.Client;

// Shared particle setups for vehicle effects. Everything is procedural: one soft round sprite,
// a color ramp and a size curve per effect, so no texture files are needed.
internal static class ParticleEffects
{
    // World-space particles stay behind a moving emitter, so the culling box must cover the whole
    // trail: 6 m/s for a 2.2 s lifetime is about 13 m, plus sprite size.
    internal const float VisibilityHalfExtent = 25f;

    private static Texture2D? _softDot;
    private static QuadMesh? _drawPass;
    private static ParticleProcessMaterial? _blastDustProcess;
    private static ParticleProcessMaterial? _barrelSmokeProcess;
    private static CurveTexture? _movementDustSize;
    private static GradientTexture1D? _movementDustColor;

    internal static Texture2D SoftDot => _softDot ??= CreateSoftDot();

    // One quad and material for every emitter: they only differ in their process materials.
    internal static QuadMesh DrawPass => _drawPass ??= CreateDrawPass();

    // Ground dust thrown forward and sideways by the muzzle blast of a tank gun.
    internal static GpuParticles3D CreateBlastDust() => CreateEmitter(
        "MuzzleBlastDust",
        oneShot: true,
        amount: 44,
        lifetime: 1.9f,
        explosiveness: 0.92f,
        _blastDustProcess ??= new ParticleProcessMaterial
        {
            Direction = new Vector3(0f, 0.12f, -1f),
            Spread = 70f,
            InitialVelocityMin = 4f,
            InitialVelocityMax = 12f,
            DampingMin = 3f,
            DampingMax = 4.5f,
            Gravity = new Vector3(0f, 0.35f, 0f),
            ScaleMin = 2.4f,
            ScaleMax = 4.4f,
            ScaleCurve = Grow(0.35f),
            ColorRamp = Fade(new Color(0.52f, 0.45f, 0.33f, 0.78f)),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(1.1f, 0.05f, 0.6f),
        });

    // Gun gas drifting from the muzzle for a second or two after the shot.
    internal static GpuParticles3D CreateBarrelSmoke() => CreateEmitter(
        "BarrelSmoke",
        oneShot: true,
        amount: 14,
        lifetime: 1.6f,
        explosiveness: 0.05f,
        _barrelSmokeProcess ??= new ParticleProcessMaterial
        {
            Direction = new Vector3(0f, 0.6f, -0.4f),
            Spread = 20f,
            InitialVelocityMin = 0.3f,
            InitialVelocityMax = 0.8f,
            DampingMin = 0.4f,
            DampingMax = 0.7f,
            Gravity = new Vector3(0f, 0.25f, 0f),
            ScaleMin = 1.1f,
            ScaleMax = 1.8f,
            ScaleCurve = Grow(0.4f),
            ColorRamp = Fade(new Color(0.42f, 0.42f, 0.41f, 0.7f)),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.08f,
        });

    // Dust kicked up by tracks or wheels while driving. The view scales it with speed.
    // The emission box depends on the running gear, so only the ramps are shared.
    internal static GpuParticles3D CreateMovementDust(Vector3 boxExtents) => CreateEmitter(
        "MovementDust",
        oneShot: false,
        amount: 64,
        lifetime: 2.2f,
        explosiveness: 0f,
        new ParticleProcessMaterial
        {
            Direction = new Vector3(0f, 1f, 0f),
            Spread = 45f,
            InitialVelocityMin = 0.4f,
            InitialVelocityMax = 1.2f,
            DampingMin = 0.6f,
            DampingMax = 1f,
            Gravity = new Vector3(0f, -0.15f, 0f),
            ScaleMin = 1.6f,
            ScaleMax = 2.8f,
            ScaleCurve = _movementDustSize ??= Grow(0.45f),
            ColorRamp = _movementDustColor ??= Fade(new Color(0.5f, 0.43f, 0.32f, 0.6f)),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = boxExtents,
        });

    private static GpuParticles3D CreateEmitter(string name, bool oneShot, int amount, float lifetime, float explosiveness, ParticleProcessMaterial process)
    {
        return new GpuParticles3D
        {
            Name = name,
            Amount = amount,
            Lifetime = lifetime,
            Explosiveness = explosiveness,
            OneShot = oneShot,
            Emitting = false,
            LocalCoords = false,
            ProcessMaterial = process,
            DrawPass1 = DrawPass,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityAabb = new Aabb(
                new Vector3(-VisibilityHalfExtent, -2f, -VisibilityHalfExtent),
                new Vector3(VisibilityHalfExtent * 2f, 14f, VisibilityHalfExtent * 2f)),
        };
    }

    private static QuadMesh CreateDrawPass()
    {
        StandardMaterial3D material = new()
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            // Without this the billboard drops the per-particle scale and every sprite is 1 m.
            BillboardKeepScale = true,
            VertexColorUseAsAlbedo = true,
            VertexColorIsSrgb = true,
            AlbedoTexture = SoftDot,
            // Soft particles: sprites fade where they meet the ground instead of cutting into it.
            ProximityFadeEnabled = true,
            ProximityFadeDistance = 1.5f,
        };
        return new QuadMesh { Size = Vector2.One, Material = material };
    }
    private static Texture2D CreateSoftDot()
    {
        Gradient gradient = new()
        {
            Offsets = [0f, 0.6f, 1f],
            Colors = [new Color(1f, 1f, 1f, 1f), new Color(1f, 1f, 1f, 0.7f), new Color(1f, 1f, 1f, 0f)],
        };
        return new GradientTexture2D
        {
            Gradient = gradient,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(1f, 0.5f),
            Width = 64,
            Height = 64,
        };
    }

    // Size over lifetime: starts at `start` of full size and grows to full.
    private static CurveTexture Grow(float start)
    {
        Curve curve = new();
        curve.AddPoint(new Vector2(0f, start));
        curve.AddPoint(new Vector2(1f, 1f));
        return new CurveTexture { Curve = curve };
    }

    // Color over lifetime: the given color fading to fully transparent.
    private static GradientTexture1D Fade(Color color)
    {
        Gradient gradient = new()
        {
            Offsets = [0f, 0.25f, 1f],
            Colors = [color, color, new Color(color.R, color.G, color.B, 0f)],
        };
        return new GradientTexture1D { Gradient = gradient };
    }
}
