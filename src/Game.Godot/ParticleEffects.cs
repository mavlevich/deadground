using Godot;

namespace DeadGround.Client;

// Shared procedural particle systems and sculpted volumetric cloud atlases for
// vehicles, direct-fire weapons, and indirect-fire rocket artillery.
// Everything is generated deterministically in code with zero external texture files.
internal static class ParticleEffects
{
    internal const float VisibilityHalfExtent = 25f;

    // Approximate world-space sun direction matching the daylight environment profile.
    private static readonly Vector3 DefaultSunDir = new Vector3(0.46f, 0.74f, 0.49f).Normalized();

    internal readonly struct CraterDecalTextures(Texture2D albedo, Texture2D normal, Texture2D orm)
    {
        public Texture2D Albedo { get; } = albedo;
        public Texture2D Normal { get; } = normal;
        public Texture2D Orm { get; } = orm;
    }

    internal const int CraterDecalVariantCount = 4;

    private static Texture2D? _softDot;
    private static Texture2D? _cloudPuff;
    private static Texture2D? _sculptedPuffAtlas;
    private static Texture2D? _clodAtlas;
    private static Texture2D? _hotGlow;
    private static Texture2D? _craterTexture;
    private static CraterDecalTextures[]? _craterDecalVariants;

    private static QuadMesh? _drawPass;
    private static QuadMesh? _litCloudDrawPass;
    private static QuadMesh? _litDebrisDrawPass;
    private static QuadMesh? _ejectaJetDrawPass;
    private static QuadMesh? _additiveFireDrawPass;
    private static QuadMesh? _additiveSparkDrawPass;

    private static ParticleProcessMaterial? _blastDustProcess;
    private static ParticleProcessMaterial? _barrelSmokeProcess;
    private static ParticleProcessMaterial? _wreckFireProcess;
    private static ParticleProcessMaterial? _wreckSmokeProcess;
    private static ParticleProcessMaterial? _rocketBackblastProcess;
    private static ParticleProcessMaterial? _rocketLaunchGroundBankProcess;
    private static ParticleProcessMaterial? _rocketContrailProcess;
    private static ParticleProcessMaterial? _artilleryFireballProcess;
    private static ParticleProcessMaterial? _artillerySparksProcess;
    private static ParticleProcessMaterial? _artilleryDirtPlumeProcess;
    private static ParticleProcessMaterial? _artilleryDebrisProcess;
    private static ParticleProcessMaterial? _artilleryEjectaJetsProcess;
    private static ParticleProcessMaterial? _artilleryGroundDustRingProcess;
    private static ParticleProcessMaterial? _impactSparksProcess;
    private static ParticleProcessMaterial? _samContrailProcess;
    private static ParticleProcessMaterial? _flareSmokeTrailProcess;
    private static ParticleProcessMaterial? _airburstSmokeProcess;
    private static ParticleProcessMaterial? _airburstShrapnelProcess;

    private static CurveTexture? _movementDustSize;
    private static CurveTexture? _wreckFireSize;
    private static CurveTexture? _wreckSmokeSize;
    private static GradientTexture1D? _movementDustColor;
    private static GradientTexture1D? _wreckFireColor;
    private static GradientTexture1D? _wreckSmokeColor;

    internal static Texture2D SoftDot => _softDot ??= CreateSoftDot();
    internal static Texture2D CloudPuff => _cloudPuff ??= CreateCloudPuffTexture();
    internal static Texture2D SculptedPuffAtlas => _sculptedPuffAtlas ??= CreateSculptedPuffAtlas();
    internal static Texture2D ClodAtlas => _clodAtlas ??= CreateClodAtlas();
    internal static Texture2D HotGlow => _hotGlow ??= CreateHotGlowTexture();
    internal static Texture2D CraterTexture => _craterTexture ??= CreateCraterTexture();
    internal static CraterDecalTextures[] CraterDecalVariants => _craterDecalVariants ??= CreateCraterDecalVariants();

    internal static QuadMesh DrawPass => _drawPass ??= CreateDrawPass();
    internal static QuadMesh LitCloudDrawPass => _litCloudDrawPass ??= CreateLitCloudDrawPass();
    internal static QuadMesh LitDebrisDrawPass => _litDebrisDrawPass ??= CreateLitDebrisDrawPass();
    internal static QuadMesh EjectaJetDrawPass => _ejectaJetDrawPass ??= CreateEjectaJetDrawPass();
    internal static QuadMesh AdditiveFireDrawPass => _additiveFireDrawPass ??= CreateAdditiveFireDrawPass();
    internal static QuadMesh AdditiveSparkDrawPass => _additiveSparkDrawPass ??= CreateAdditiveSparkDrawPass();

    // 4-variant scorched earth crater decal shader with deterministic per-position tile selection.
    internal static ShaderMaterial CreateCraterMaterial()
    {
        Shader shader = new()
        {
            Code = """
shader_type spatial;
render_mode unshaded, depth_draw_never, cull_disabled;
uniform sampler2D crater_atlas : source_color, filter_linear_mipmap;
varying float v_tile;
void vertex() {
    v_tile = floor(fract(sin(dot(MODEL_MATRIX[3].xz, vec2(19.19, 73.37))) * 43758.5453) * 4.0);
}
void fragment() {
    float cx = mod(v_tile, 2.0);
    float cy = floor(v_tile * 0.5);
    vec2 uv = (vec2(cx, cy) + clamp(UV, 0.01, 0.99)) * 0.5;
    vec4 tex = texture(crater_atlas, uv);
    ALBEDO = tex.rgb;
    ALPHA = tex.a;
}
"""
        };
        ShaderMaterial mat = new() { Shader = shader };
        mat.SetShaderParameter("crater_atlas", CraterTexture);
        return mat;
    }

    // Shared sun-lit volumetric billboard material with soft depth-fade for standalone pooled smoke quads.
    internal static ShaderMaterial CreateLitSmokeQuadMaterial(Color baseTint, float maxAlpha = 0.80f)
    {
        Shader shader = new()
        {
            Code = """
shader_type spatial;
render_mode unshaded, depth_draw_never, cull_disabled;
uniform sampler2D puff_atlas : filter_linear_mipmap;
uniform sampler2D depth_tex : hint_depth_texture, filter_linear_mipmap;
uniform vec3 sun_dir = vec3(0.46, 0.74, 0.49);
uniform vec3 sun_color = vec3(1.06, 1.00, 0.91);
uniform vec3 shadow_color = vec3(0.30, 0.34, 0.41);
uniform vec4 tint = vec4(0.82, 0.80, 0.76, 0.72);
varying float v_tile;
void vertex() {
    mat4 mat_world = mat4(
        normalize(INV_VIEW_MATRIX[0]),
        normalize(INV_VIEW_MATRIX[1]),
        normalize(INV_VIEW_MATRIX[2]),
        MODEL_MATRIX[3]
    );
    vec3 scale_vec = vec3(
        length(MODEL_MATRIX[0].xyz),
        length(MODEL_MATRIX[1].xyz),
        length(MODEL_MATRIX[2].xyz)
    );
    mat_world[0] *= scale_vec.x;
    mat_world[1] *= scale_vec.y;
    mat_world[2] *= scale_vec.z;
    MODELVIEW_MATRIX = VIEW_MATRIX * mat_world;
    // Deterministic tile index from world position so adjacent quads never stamp the same cloud
    v_tile = floor(fract(sin(dot(MODEL_MATRIX[3].xz, vec2(12.9898, 78.233))) * 43758.5453) * 16.0);
}
void fragment() {
    float col_idx = mod(v_tile, 4.0);
    float row_idx = floor(v_tile * 0.25);
    vec2 tile_uv = (vec2(col_idx, row_idx) + clamp(UV, 0.02, 0.98)) * 0.25;
    vec4 tex = texture(puff_atlas, tile_uv);
    vec2 n_xy = tex.rg * 2.0 - 1.0;
    float n_z = sqrt(max(0.06, 1.0 - dot(n_xy, n_xy)));
    vec3 n_world = normalize(
        INV_VIEW_MATRIX[0].xyz * n_xy.x +
        INV_VIEW_MATRIX[1].xyz * (-n_xy.y) +
        INV_VIEW_MATRIX[2].xyz * n_z
    );
    float ndl = dot(n_world, normalize(sun_dir)) * 0.5 + 0.5;
    vec3 lit = mix(shadow_color, sun_color, smoothstep(0.16, 0.88, ndl));
    lit *= mix(0.80, 1.05, ndl * (1.0 - tex.b * 0.28));

    // Soft-particle ground blend (works in both orthographic and perspective projections)
    float scene_depth = texture(depth_tex, SCREEN_UV).r;
    vec4 upos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, scene_depth, 1.0);
    float soft_fade = clamp((-upos.z / upos.w - (-VERTEX.z)) / 1.8, 0.0, 1.0);

    ALBEDO = tint.rgb * lit;
    ALPHA = clamp(tex.a * tint.a * soft_fade, 0.0, 0.90);
}
"""
        };
        ShaderMaterial mat = new() { Shader = shader };
        mat.SetShaderParameter("puff_atlas", SculptedPuffAtlas);
        mat.SetShaderParameter("sun_dir", DefaultSunDir);
        mat.SetShaderParameter("tint", new Color(baseTint.R, baseTint.G, baseTint.B, maxAlpha));
        return mat;
    }

    // Ground dust thrown forward and sideways by the muzzle blast of a tank gun.
    internal static GpuParticles3D CreateBlastDust() => CreateEmitter(
        "MuzzleBlastDust",
        oneShot: true,
        amount: 52,
        lifetime: 2.6f,
        explosiveness: 0.92f,
        _blastDustProcess ??= new ParticleProcessMaterial
        {
            Direction = new Vector3(0f, 0.12f, -1f),
            Spread = 74f,
            InitialVelocityMin = 4.5f,
            InitialVelocityMax = 14.5f,
            DampingMin = 3.2f,
            DampingMax = 5.2f,
            Gravity = new Vector3(0.40f, 0.28f, 0.16f),
            AngleMin = -180f,
            AngleMax = 180f,
            AngularVelocityMin = -22f,
            AngularVelocityMax = 22f,
            TurbulenceEnabled = true,
            TurbulenceNoiseStrength = 0.85f,
            TurbulenceNoiseScale = 3.2f,
            TurbulenceInfluenceMin = 0.06f,
            TurbulenceInfluenceMax = 0.16f,
            ScaleMin = 2.6f,
            ScaleMax = 5.6f,
            ScaleCurve = Grow(0.28f),
            ColorInitialRamp = CreateDustVariationRamp(),
            ColorRamp = Fade(new Color(0.66f, 0.59f, 0.47f, 0.72f)),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(1.1f, 0.08f, 0.6f),
        },
        DrawPass);

    // Pale grey cordite gun smoke drifting out of the muzzle after firing.
    internal static GpuParticles3D CreateBarrelSmoke() => CreateEmitter(
        "BarrelSmoke",
        oneShot: true,
        amount: 26,
        lifetime: 3.2f,
        explosiveness: 0.14f,
        _barrelSmokeProcess ??= new ParticleProcessMaterial
        {
            Direction = new Vector3(0f, 0.42f, -0.58f),
            Spread = 24f,
            InitialVelocityMin = 0.7f,
            InitialVelocityMax = 2.4f,
            DampingMin = 0.8f,
            DampingMax = 1.4f,
            Gravity = new Vector3(0.32f, 0.30f, 0.12f),
            AngleMin = -180f,
            AngleMax = 180f,
            AngularVelocityMin = -18f,
            AngularVelocityMax = 18f,
            TurbulenceEnabled = true,
            TurbulenceNoiseStrength = 0.65f,
            TurbulenceNoiseScale = 2.6f,
            TurbulenceInfluenceMin = 0.05f,
            TurbulenceInfluenceMax = 0.14f,
            ScaleMin = 1.3f,
            ScaleMax = 3.0f,
            ScaleCurve = Grow(0.26f),
            ColorRamp = Fade(new Color(0.72f, 0.71f, 0.68f, 0.60f)),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.14f,
        },
        DrawPass);

    // Dust kicked up by tracks or wheels while driving.
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
            AngleMin = -180f,
            AngleMax = 180f,
            ScaleMin = 1.6f,
            ScaleMax = 2.8f,
            ScaleCurve = _movementDustSize ??= Grow(0.45f),
            ColorRamp = _movementDustColor ??= Fade(new Color(0.56f, 0.49f, 0.38f, 0.56f)),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = boxExtents,
        },
        DrawPass);

    // Short lingering flame rising from engine deck or turret ring after vehicle destruction.
    internal static GpuParticles3D CreateWreckFire() => CreateEmitter(
        "WreckFire",
        oneShot: false,
        amount: 16,
        lifetime: 0.50f,
        explosiveness: 0.0f,
        _wreckFireProcess ??= new ParticleProcessMaterial
        {
            Direction = new Vector3(0f, 1f, 0f),
            Spread = 20f,
            InitialVelocityMin = 0.8f,
            InitialVelocityMax = 1.8f,
            DampingMin = 0.5f,
            DampingMax = 1.0f,
            Gravity = new Vector3(0f, 0.6f, 0f),
            AngleMin = -180f,
            AngleMax = 180f,
            AngularVelocityMin = -35f,
            AngularVelocityMax = 35f,
            ScaleMin = 0.55f,
            ScaleMax = 1.10f,
            ScaleCurve = _wreckFireSize ??= Shrink(0.65f),
            ColorRamp = _wreckFireColor ??= CreateFireRamp(),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(0.45f, 0.15f, 0.65f),
        },
        AdditiveFireDrawPass);

    // Heavy oily black-grey turbulent smoke column rising from a destroyed armored vehicle.
    internal static GpuParticles3D CreateWreckSmoke() => CreateEmitter(
        "WreckSmoke",
        oneShot: false,
        amount: 40,
        lifetime: 4.2f,
        explosiveness: 0.0f,
        _wreckSmokeProcess ??= new ParticleProcessMaterial
        {
            Direction = new Vector3(0.12f, 1f, 0.06f),
            Spread = 16f,
            InitialVelocityMin = 1.0f,
            InitialVelocityMax = 2.4f,
            DampingMin = 0.30f,
            DampingMax = 0.60f,
            Gravity = new Vector3(0.42f, 0.45f, 0.18f),
            AngleMin = -180f,
            AngleMax = 180f,
            AngularVelocityMin = -16f,
            AngularVelocityMax = 16f,
            TurbulenceEnabled = true,
            TurbulenceNoiseStrength = 1.25f,
            TurbulenceNoiseScale = 3.8f,
            TurbulenceInfluenceMin = 0.08f,
            TurbulenceInfluenceMax = 0.22f,
            ScaleMin = 1.3f,
            ScaleMax = 4.4f,
            ScaleCurve = _wreckSmokeSize ??= Grow(0.26f),
            ColorInitialRamp = CreateSootVariationRamp(),
            ColorRamp = _wreckSmokeColor ??= CreateSmokeRamp(),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(0.6f, 0.15f, 0.8f),
        },
        LitCloudDrawPass);

    // Violent downward-angled propellant backblast from BM-21 tubes lingering ~8.5s around the firing position.
    internal static GpuParticles3D CreateRocketBackblast() => CreateEmitter(
        "RocketBackblast",
        oneShot: true,
        amount: 64,
        lifetime: 8.5f,
        explosiveness: 0.92f,
        _rocketBackblastProcess ??= new ParticleProcessMaterial
        {
            Direction = new Vector3(0f, 0f, 1f),
            Spread = 24f,
            InitialVelocityMin = 14f,
            InitialVelocityMax = 38f,
            DampingMin = 6.2f,
            DampingMax = 10.5f,
            Gravity = new Vector3(0.65f, 0.85f, 0.28f),
            AngleMin = -180f,
            AngleMax = 180f,
            AngularVelocityMin = -26f,
            AngularVelocityMax = 26f,
            TurbulenceEnabled = true,
            TurbulenceNoiseStrength = 1.9f,
            TurbulenceNoiseScale = 3.8f,
            TurbulenceInfluenceMin = 0.08f,
            TurbulenceInfluenceMax = 0.24f,
            ScaleMin = 3.6f,
            ScaleMax = 11.5f,
            ScaleCurve = Grow(0.16f),
            ColorInitialRamp = CreateDustVariationRamp(),
            ColorRamp = CreateBackblastRamp(),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.45f,
        },
        LitCloudDrawPass,
        visibilityHalfExtent: 75f);

    // Dense ground-deflected dust & propellant bank blooming radially where the downward exhaust hits the dirt.
    internal static GpuParticles3D CreateRocketLaunchGroundBankParticles() => CreateEmitter(
        "RocketLaunchGroundBank",
        oneShot: true,
        amount: 42,
        lifetime: 8.2f,
        explosiveness: 0.90f,
        _rocketLaunchGroundBankProcess ??= new ParticleProcessMaterial
        {
            Direction = new Vector3(0f, 0.14f, 0f),
            Spread = 180f,
            Flatness = 0.84f,
            InitialVelocityMin = 6f,
            InitialVelocityMax = 22f,
            DampingMin = 5.5f,
            DampingMax = 9.0f,
            Gravity = new Vector3(0.70f, 0.65f, 0.30f),
            AngleMin = -180f,
            AngleMax = 180f,
            AngularVelocityMin = -22f,
            AngularVelocityMax = 22f,
            TurbulenceEnabled = true,
            TurbulenceNoiseStrength = 1.7f,
            TurbulenceNoiseScale = 3.6f,
            TurbulenceInfluenceMin = 0.08f,
            TurbulenceInfluenceMax = 0.22f,
            ScaleMin = 3.2f,
            ScaleMax = 10.2f,
            ScaleCurve = Grow(0.20f),
            ColorInitialRamp = CreateDustVariationRamp(),
            ColorRamp = CreateLaunchGroundBankRamp(),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring,
            EmissionRingRadius = 1.8f,
            EmissionRingInnerRadius = 0.4f,
            EmissionRingHeight = 0.35f,
            EmissionRingAxis = Vector3.Up,
        },
        LitCloudDrawPass,
        visibilityHalfExtent: 75f);

    // Continuous, dense, razor-tight-to-billowing sun-lit white rocket smoke ribbon lingering ~13.5s in the sky.
    internal static GpuParticles3D CreateRocketContrail() => CreateEmitter(
        "RocketContrail",
        oneShot: false,
        amount: 420,
        lifetime: 13.5f,
        explosiveness: 0.0f,
        _rocketContrailProcess ??= new ParticleProcessMaterial
        {
            Direction = new Vector3(0f, 0.03f, 1f),
            Spread = 6f,
            InitialVelocityMin = 0.5f,
            InitialVelocityMax = 1.8f,
            DampingMin = 2.2f,
            DampingMax = 3.8f,
            Gravity = new Vector3(0.75f, 0.20f, 0.32f),
            AngleMin = -180f,
            AngleMax = 180f,
            AngularVelocityMin = -12f,
            AngularVelocityMax = 12f,
            TurbulenceEnabled = true,
            TurbulenceNoiseStrength = 2.1f,
            TurbulenceNoiseScale = 4.8f,
            TurbulenceInfluenceMin = 0.04f,
            TurbulenceInfluenceMax = 0.20f,
            ScaleMin = 4.6f,
            ScaleMax = 6.8f,
            ScaleCurve = CreateContrailScaleCurve(),
            ColorRamp = CreateRocketContrailRamp(),
            // Tight cross-section at nozzle + elongated along tail axis so fast 160 m/s flight leaves a crisp continuous ribbon
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(0.09f, 0.09f, 2.2f),
        },
        LitCloudDrawPass,
        visibilityHalfExtent: 1200f);

    // Brief, violent, ragged incandescent detonation flash tongues bursting out from the base of the blast column.
    internal static GpuParticles3D CreateArtilleryFireballParticles() => CreateEmitter(
        "ArtilleryFireball",
        oneShot: true,
        amount: 32,
        lifetime: 0.22f,
        explosiveness: 0.99f,
        _artilleryFireballProcess ??= new ParticleProcessMaterial
        {
            Direction = new Vector3(0f, 1f, 0f),
            Spread = 72f,
            InitialVelocityMin = 4.5f,
            InitialVelocityMax = 17.0f,
            DampingMin = 10.5f,
            DampingMax = 16.0f,
            Gravity = new Vector3(0f, 1.2f, 0f),
            AngleMin = -180f,
            AngleMax = 180f,
            AngularVelocityMin = -85f,
            AngularVelocityMax = 85f,
            ScaleMin = 1.1f,
            ScaleMax = 3.4f,
            ScaleCurve = CreateFireballScaleCurve(),
            ColorRamp = CreateExplosionFireRamp(),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.65f,
        },
        AdditiveFireDrawPass,
        visibilityHalfExtent: 35f);

    // Crisp, velocity-stretched incandescent steel/shrapnel streaks.
    internal static GpuParticles3D CreateArtillerySparksParticles() => CreateEmitter(
        "ArtillerySparks",
        oneShot: true,
        amount: 28,
        lifetime: 0.72f,
        explosiveness: 0.98f,
        _artillerySparksProcess ??= new ParticleProcessMaterial
        {
            ParticleFlagAlignY = true,
            Direction = new Vector3(0f, 1f, 0f),
            Spread = 64f,
            InitialVelocityMin = 20f,
            InitialVelocityMax = 48f,
            DampingMin = 0.5f,
            DampingMax = 1.4f,
            Gravity = new Vector3(0f, -24f, 0f),
            ScaleMin = 0.55f,
            ScaleMax = 1.25f,
            ScaleCurve = Shrink(0.22f),
            ColorRamp = CreateSparksRamp(),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.5f,
        },
        AdditiveSparkDrawPass,
        visibilityHalfExtent: 60f);

    // Parabolic asymmetric dark soil, turf, and dust fountain streaks erupting in a jagged crown and raining down.
    internal static GpuParticles3D CreateArtilleryEjectaJetsParticles() => CreateEmitter(
        "ArtilleryEjectaJets",
        oneShot: true,
        amount: 26,
        lifetime: 1.45f,
        explosiveness: 0.96f,
        _artilleryEjectaJetsProcess ??= new ParticleProcessMaterial
        {
            ParticleFlagAlignY = true,
            Direction = new Vector3(0f, 1f, 0f),
            Spread = 44f,
            InitialVelocityMin = 14f,
            InitialVelocityMax = 36f,
            DampingMin = 0.65f,
            DampingMax = 1.85f,
            Gravity = new Vector3(0f, -27f, 0f),
            ScaleMin = 0.75f,
            ScaleMax = 1.65f,
            ScaleCurve = CreateEjectaJetScaleCurve(),
            ColorInitialRamp = CreateClodVariationRamp(),
            ColorRamp = CreateEjectaJetRamp(),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.95f,
        },
        EjectaJetDrawPass,
        visibilityHalfExtent: 65f);

    // Realistic-sized tumbling dark soil clods and torn turf chunks arcing out parabolically and raining onto the grass.
    internal static GpuParticles3D CreateArtilleryDebrisParticles() => CreateEmitter(
        "ArtilleryDebris",
        oneShot: true,
        amount: 115,
        lifetime: 2.65f,
        explosiveness: 0.95f,
        _artilleryDebrisProcess ??= new ParticleProcessMaterial
        {
            Direction = new Vector3(0f, 1f, 0f),
            Spread = 46f,
            InitialVelocityMin = 11f,
            InitialVelocityMax = 31f,
            DampingMin = 0.18f,
            DampingMax = 0.65f,
            Gravity = new Vector3(0f, -25f, 0f),
            AngleMin = -180f,
            AngleMax = 180f,
            AngularVelocityMin = -360f,
            AngularVelocityMax = 360f,
            ScaleMin = 0.18f,
            ScaleMax = 0.64f,
            ScaleCurve = CreateDebrisScaleCurve(),
            ColorInitialRamp = CreateClodVariationRamp(),
            ColorRamp = CreateDebrisRamp(),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 1.3f,
        },
        LitDebrisDrawPass,
        visibilityHalfExtent: 70f);

    // Ground-hugging 3D radial dust skirt blasted violently sideways across the grass by the blast overpressure.
    internal static GpuParticles3D CreateArtilleryGroundDustRingParticles() => CreateEmitter(
        "ArtilleryGroundDustRing",
        oneShot: true,
        amount: 54,
        lifetime: 4.2f,
        explosiveness: 0.95f,
        _artilleryGroundDustRingProcess ??= new ParticleProcessMaterial
        {
            Direction = new Vector3(0f, 0.12f, 0f),
            Spread = 180f,
            Flatness = 0.88f,
            InitialVelocityMin = 12f,
            InitialVelocityMax = 32f,
            DampingMin = 7.0f,
            DampingMax = 11.5f,
            Gravity = new Vector3(0.55f, 0.42f, 0.24f),
            AngleMin = -180f,
            AngleMax = 180f,
            AngularVelocityMin = -26f,
            AngularVelocityMax = 26f,
            TurbulenceEnabled = true,
            TurbulenceNoiseStrength = 1.55f,
            TurbulenceNoiseScale = 3.4f,
            TurbulenceInfluenceMin = 0.08f,
            TurbulenceInfluenceMax = 0.24f,
            ScaleMin = 2.8f,
            ScaleMax = 9.5f,
            ScaleCurve = Grow(0.20f),
            ColorInitialRamp = CreateDustVariationRamp(),
            ColorRamp = Fade(new Color(0.62f, 0.56f, 0.45f, 0.66f)),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring,
            EmissionRingRadius = 2.4f,
            EmissionRingInnerRadius = 1.2f,
            EmissionRingHeight = 0.4f,
            EmissionRingAxis = Vector3.Up,
        },
        LitCloudDrawPass,
        visibilityHalfExtent: 75f);

    // Violent explosive punch -> aerodynamic brake -> heavy boiling dark soot & earth cauliflower cap (9.5s).
    internal static GpuParticles3D CreateArtilleryDirtPlumeParticles() => CreateEmitter(
        "ArtilleryDirtPlume",
        oneShot: true,
        amount: 96,
        lifetime: 9.5f,
        explosiveness: 0.92f,
        _artilleryDirtPlumeProcess ??= new ParticleProcessMaterial
        {
            Direction = new Vector3(0f, 1f, 0f),
            Spread = 22f,
            InitialVelocityMin = 10.0f,
            InitialVelocityMax = 34.0f,
            DampingMin = 4.6f,
            DampingMax = 8.4f,
            Gravity = new Vector3(0.75f, 0.42f, 0.30f),
            AngleMin = -180f,
            AngleMax = 180f,
            AngularVelocityMin = -20f,
            AngularVelocityMax = 20f,
            TurbulenceEnabled = true,
            TurbulenceNoiseStrength = 2.4f,
            TurbulenceNoiseScale = 4.4f,
            TurbulenceInfluenceMin = 0.08f,
            TurbulenceInfluenceMax = 0.28f,
            ScaleMin = 4.2f,
            ScaleMax = 15.0f,
            ScaleCurve = CreatePlumeScaleCurve(),
            ColorInitialRamp = CreateArtilleryPlumeVariationRamp(),
            ColorRamp = CreateArtilleryPlumeRamp(),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 1.8f,
        },
        LitCloudDrawPass,
        visibilityHalfExtent: 110f);

    // Crisp velocity-stretched hot sparks for direct-fire armor hits and ricochets.
    internal static GpuParticles3D CreateImpactSparksParticles() => CreateEmitter(
        "ImpactSparks",
        oneShot: true,
        amount: 24,
        lifetime: 0.44f,
        explosiveness: 0.96f,
        _impactSparksProcess ??= new ParticleProcessMaterial
        {
            ParticleFlagAlignY = true,
            Direction = new Vector3(0f, 0.7f, -0.7f),
            Spread = 52f,
            InitialVelocityMin = 8f,
            InitialVelocityMax = 24f,
            DampingMin = 1.2f,
            DampingMax = 2.5f,
            Gravity = new Vector3(0f, -20f, 0f),
            ScaleMin = 0.42f,
            ScaleMax = 0.92f,
            ScaleCurve = Shrink(0.20f),
            ColorRamp = CreateSparksRamp(),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.22f,
        },
        AdditiveSparkDrawPass,
        visibilityHalfExtent: 25f);

    // Dense, crisp sunlit white-silver contrail left across the sky by a 9M37M SAM motor.
    internal static GpuParticles3D CreateSamContrail() => CreateEmitter(
        "SamContrail",
        oneShot: false,
        amount: 190,
        lifetime: 5.5f,
        explosiveness: 0f,
        _samContrailProcess ??= new ParticleProcessMaterial
        {
            Direction = new Vector3(0f, 0f, 1f),
            Spread = 10f,
            InitialVelocityMin = 1.0f,
            InitialVelocityMax = 3.0f,
            DampingMin = 2.8f,
            DampingMax = 4.6f,
            Gravity = new Vector3(0.35f, 0.22f, 0.18f),
            AngleMin = -180f,
            AngleMax = 180f,
            AngularVelocityMin = -16f,
            AngularVelocityMax = 16f,
            TurbulenceEnabled = true,
            TurbulenceNoiseStrength = 0.95f,
            TurbulenceNoiseScale = 3.4f,
            TurbulenceInfluenceMin = 0.05f,
            TurbulenceInfluenceMax = 0.15f,
            ScaleMin = 1.05f,
            ScaleMax = 4.0f,
            ScaleCurve = Grow(0.28f),
            ColorRamp = CreateSamContrailRamp(),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.18f,
        },
        LitCloudDrawPass,
        visibilityHalfExtent: 640f);

    // Curling sunlit white magnesium smoke ribbon trailing behind each falling decoy flare.
    internal static GpuParticles3D CreateFlareSmokeTrail() => CreateEmitter(
        "FlareSmokeTrail",
        oneShot: false,
        amount: 40,
        lifetime: 2.6f,
        explosiveness: 0f,
        _flareSmokeTrailProcess ??= new ParticleProcessMaterial
        {
            Direction = new Vector3(0f, 0.35f, 0f),
            Spread = 32f,
            InitialVelocityMin = 0.4f,
            InitialVelocityMax = 1.6f,
            DampingMin = 2.0f,
            DampingMax = 3.6f,
            Gravity = new Vector3(0.38f, 0.35f, 0.18f),
            AngleMin = -180f,
            AngleMax = 180f,
            AngularVelocityMin = -24f,
            AngularVelocityMax = 24f,
            TurbulenceEnabled = true,
            TurbulenceNoiseStrength = 1.0f,
            TurbulenceNoiseScale = 3.6f,
            TurbulenceInfluenceMin = 0.05f,
            TurbulenceInfluenceMax = 0.16f,
            ScaleMin = 0.95f,
            ScaleMax = 3.2f,
            ScaleCurve = Grow(0.28f),
            ColorRamp = CreateFlareSmokeRamp(),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.16f,
        },
        LitCloudDrawPass,
        visibilityHalfExtent: 140f);

    // Characteristic mid-air dark-gray AA proximity-fuze HE-Frag flak cloud.
    internal static GpuParticles3D CreateAirburstSmokePuff() => CreateEmitter(
        "AirburstSmokePuff",
        oneShot: true,
        amount: 26,
        lifetime: 4.8f,
        explosiveness: 0.95f,
        _airburstSmokeProcess ??= new ParticleProcessMaterial
        {
            Direction = new Vector3(0f, 0.15f, 0f),
            Spread = 180f,
            InitialVelocityMin = 4.5f,
            InitialVelocityMax = 15.5f,
            DampingMin = 4.8f,
            DampingMax = 7.6f,
            Gravity = new Vector3(0.55f, 0.35f, 0.25f),
            AngleMin = -180f,
            AngleMax = 180f,
            AngularVelocityMin = -25f,
            AngularVelocityMax = 25f,
            TurbulenceEnabled = true,
            TurbulenceNoiseStrength = 1.6f,
            TurbulenceNoiseScale = 3.8f,
            TurbulenceInfluenceMin = 0.08f,
            TurbulenceInfluenceMax = 0.24f,
            ScaleMin = 2.6f,
            ScaleMax = 7.2f,
            ScaleCurve = Grow(0.36f),
            ColorRamp = CreateAirburstFlakRamp(),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 1.1f,
        },
        LitCloudDrawPass,
        visibilityHalfExtent: 45f);

    // 360-degree incandescent white-gold shrapnel needle burst for SAM airbursts.
    internal static GpuParticles3D CreateAirburstShrapnelSparks() => CreateEmitter(
        "AirburstShrapnelSparks",
        oneShot: true,
        amount: 42,
        lifetime: 0.52f,
        explosiveness: 0.98f,
        _airburstShrapnelProcess ??= new ParticleProcessMaterial
        {
            ParticleFlagAlignY = true,
            Direction = new Vector3(0f, 0f, 0f),
            Spread = 180f,
            InitialVelocityMin = 18f,
            InitialVelocityMax = 44f,
            DampingMin = 3.5f,
            DampingMax = 5.5f,
            Gravity = new Vector3(0f, -12f, 0f),
            ScaleMin = 0.55f,
            ScaleMax = 1.25f,
            ScaleCurve = Shrink(0.16f),
            ColorRamp = CreateSparksRamp(),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.6f,
        },
        AdditiveSparkDrawPass,
        visibilityHalfExtent: 38f);

    private static GradientTexture1D MakeGradient(params (float Offset, Color Color)[] stops)
    {
        float[] offsets = new float[stops.Length];
        Color[] colors = new Color[stops.Length];
        for (int i = 0; i < stops.Length; i++)
        {
            offsets[i] = stops[i].Offset;
            colors[i] = stops[i].Color;
        }
        return new GradientTexture1D
        {
            Gradient = new Gradient
            {
                Offsets = offsets,
                Colors = colors
            }
        };
    }

    private static GradientTexture1D CreateSamContrailRamp() => MakeGradient(
        (0.00f, new Color(0.98f, 0.96f, 0.92f, 0.88f)),
        (0.18f, new Color(0.93f, 0.92f, 0.90f, 0.72f)),
        (0.55f, new Color(0.86f, 0.86f, 0.85f, 0.44f)),
        (0.85f, new Color(0.82f, 0.83f, 0.84f, 0.16f)),
        (1.00f, new Color(0.80f, 0.82f, 0.84f, 0.00f)));

    private static GradientTexture1D CreateFlareSmokeRamp() => MakeGradient(
        (0.00f, new Color(1.00f, 0.97f, 0.88f, 0.84f)),
        (0.22f, new Color(0.95f, 0.94f, 0.90f, 0.62f)),
        (0.65f, new Color(0.88f, 0.89f, 0.88f, 0.28f)),
        (1.00f, new Color(0.84f, 0.86f, 0.87f, 0.00f)));

    private static GradientTexture1D CreateAirburstFlakRamp() => MakeGradient(
        (0.00f, new Color(0.88f, 0.82f, 0.74f, 0.88f)),
        (0.14f, new Color(0.32f, 0.30f, 0.29f, 0.84f)),
        (0.52f, new Color(0.24f, 0.24f, 0.25f, 0.62f)),
        (0.82f, new Color(0.30f, 0.31f, 0.32f, 0.26f)),
        (1.00f, new Color(0.36f, 0.37f, 0.38f, 0.00f)));

    private static GpuParticles3D CreateEmitter(
        string name,
        bool oneShot,
        int amount,
        float lifetime,
        float explosiveness,
        ParticleProcessMaterial process,
        QuadMesh? drawPass = null,
        float visibilityHalfExtent = VisibilityHalfExtent)
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
            DrawPass1 = drawPass ?? DrawPass,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityAabb = new Aabb(
                new Vector3(-visibilityHalfExtent, -6f, -visibilityHalfExtent),
                new Vector3(visibilityHalfExtent * 2f, MathF.Max(36f, visibilityHalfExtent * 0.85f), visibilityHalfExtent * 2f)),
        };
    }

    private static QuadMesh CreateDrawPass()
    {
        StandardMaterial3D material = new()
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            BillboardKeepScale = true,
            VertexColorUseAsAlbedo = true,
            VertexColorIsSrgb = true,
            AlbedoTexture = CloudPuff,
            ProximityFadeEnabled = true,
            ProximityFadeDistance = 1.8f,
        };
        return new QuadMesh { Size = Vector2.One, Material = material };
    }

    // Sun-lit 16-variant sculpted 3D-normal volumetric cloud billboard shader with
    // true depth-buffer soft-particle terrain blending and age-based wispy dissolution.
    private static QuadMesh CreateLitCloudDrawPass()
    {
        Shader shader = new()
        {
            Code = """
shader_type spatial;
render_mode unshaded, depth_draw_never, cull_disabled;
uniform sampler2D puff_atlas : filter_linear_mipmap;
uniform sampler2D depth_tex : hint_depth_texture, filter_linear_mipmap;
uniform vec3 sun_dir = vec3(0.46, 0.74, 0.49);
uniform vec3 sun_color = vec3(1.07, 1.01, 0.91);
uniform vec3 shadow_color = vec3(0.28, 0.32, 0.39);
varying vec4 v_color;
varying float v_rot;
varying float v_age;
varying float v_tile;
void vertex() {
    mat4 mat_world = mat4(
        normalize(INV_VIEW_MATRIX[0]),
        normalize(INV_VIEW_MATRIX[1]),
        normalize(INV_VIEW_MATRIX[2]),
        MODEL_MATRIX[3]
    );
    float angle = INSTANCE_CUSTOM.x;
    float c = cos(angle);
    float s = sin(angle);
    mat4 rot_mat = mat4(
        vec4(c, -s, 0.0, 0.0),
        vec4(s,  c, 0.0, 0.0),
        vec4(0.0, 0.0, 1.0, 0.0),
        vec4(0.0, 0.0, 0.0, 1.0)
    );
    vec3 scale_vec = vec3(
        length(MODEL_MATRIX[0].xyz),
        length(MODEL_MATRIX[1].xyz),
        length(MODEL_MATRIX[2].xyz)
    );
    mat_world = mat_world * rot_mat;
    mat_world[0] *= scale_vec.x;
    mat_world[1] *= scale_vec.y;
    mat_world[2] *= scale_vec.z;
    MODELVIEW_MATRIX = VIEW_MATRIX * mat_world;
    v_color = COLOR;
    v_rot = angle;
    v_age = INSTANCE_CUSTOM.y;
    v_tile = float(INSTANCE_ID % 16);
}
void fragment() {
    float col_idx = mod(v_tile, 4.0);
    float row_idx = floor(v_tile * 0.25);
    // Subtle internal rolling UV shift as the puff ages so clouds boil instead of looking static
    vec2 local_uv = clamp(UV + vec2(sin(v_age * 3.1 + v_tile) * 0.035, -v_age * 0.045), 0.02, 0.98);
    vec2 tile_uv = (vec2(col_idx, row_idx) + local_uv) * 0.25;
    vec4 tex = texture(puff_atlas, tile_uv);

    // Secondary detail sample from neighboring tile to break up any silhouette repetition
    float tile2 = mod(v_tile + 7.0, 16.0);
    vec2 tile_uv2 = (vec2(mod(tile2, 4.0), floor(tile2 * 0.25)) + clamp(UV.yx, 0.02, 0.98)) * 0.25;
    float detail_mask = texture(puff_atlas, tile_uv2).a;

    // Reconstruct sculpted 3D cauliflower normal rotated back into world space so tops are always sunlit
    vec2 n_xy = tex.rg * 2.0 - 1.0;
    float c = cos(v_rot);
    float s = sin(v_rot);
    vec2 n_view = vec2(n_xy.x * c - n_xy.y * s, n_xy.x * s + n_xy.y * c);
    float n_z = sqrt(max(0.06, 1.0 - dot(n_view, n_view)));
    vec3 n_world = normalize(
        INV_VIEW_MATRIX[0].xyz * n_view.x +
        INV_VIEW_MATRIX[1].xyz * (-n_view.y) +
        INV_VIEW_MATRIX[2].xyz * n_z
    );

    float ndl = dot(n_world, normalize(sun_dir)) * 0.5 + 0.5;
    // Soft powder-like forward/silver-lining scattering at thin cloud rims
    float rim = pow(1.0 - clamp(tex.b, 0.0, 1.0), 1.6) * 0.16;
    vec3 lit = mix(shadow_color, sun_color, smoothstep(0.14, 0.88, ndl)) + sun_color * rim * ndl;
    lit *= mix(0.78, 1.06, ndl * (1.0 - tex.b * 0.30));

    // Soft-particle terrain blending (works seamlessly in orthographic RTS camera)
    float scene_depth = texture(depth_tex, SCREEN_UV).r;
    vec4 upos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, scene_depth, 1.0);
    float soft_fade = clamp((-upos.z / upos.w - (-VERTEX.z)) / 2.2, 0.0, 1.0);

    // Organic wispy age erosion: thin edges dissolve first while denser cores linger
    float density = mix(tex.a, tex.a * (0.68 + 0.52 * detail_mask), 0.38);
    float erode_cut = v_age * 0.58;
    float eroded = smoothstep(erode_cut, erode_cut + 0.42, density);

    // Subsurface internal blast fire glow inside dense soot folds during first ~0.35s of detonation
    float warmth = max(0.0, v_color.r - max(v_color.g, v_color.b) * 1.18);
    float fire_fade = 1.0 - smoothstep(0.0, 0.16, v_age);
    float crevice_glow = smoothstep(0.24, 0.82, tex.b) * warmth * fire_fade;
    vec3 internal_fire = vec3(3.1, 0.92, 0.16) * crevice_glow * 1.85;

    ALBEDO = v_color.rgb * lit + internal_fire;
    ALPHA = clamp(eroded * v_color.a * soft_fade, 0.0, 0.92);
}
"""
        };
        ShaderMaterial material = new() { Shader = shader };
        material.SetShaderParameter("puff_atlas", SculptedPuffAtlas);
        material.SetShaderParameter("sun_dir", DefaultSunDir);
        return new QuadMesh { Size = Vector2.One, Material = material };
    }

    // 4-variant jagged dark earth clods & turf chunks with sunlit top shading.
    private static QuadMesh CreateLitDebrisDrawPass()
    {
        Shader shader = new()
        {
            Code = """
shader_type spatial;
render_mode unshaded, cull_disabled;
uniform sampler2D clod_atlas : filter_linear_mipmap;
varying vec4 v_color;
varying float v_tile;
void vertex() {
    mat4 mat_world = mat4(
        normalize(INV_VIEW_MATRIX[0]),
        normalize(INV_VIEW_MATRIX[1]),
        normalize(INV_VIEW_MATRIX[2]),
        MODEL_MATRIX[3]
    );
    float angle = INSTANCE_CUSTOM.x;
    float c = cos(angle);
    float s = sin(angle);
    mat4 rot_mat = mat4(
        vec4(c, -s, 0.0, 0.0),
        vec4(s,  c, 0.0, 0.0),
        vec4(0.0, 0.0, 1.0, 0.0),
        vec4(0.0, 0.0, 0.0, 1.0)
    );
    vec3 scale_vec = vec3(
        length(MODEL_MATRIX[0].xyz),
        length(MODEL_MATRIX[1].xyz),
        length(MODEL_MATRIX[2].xyz)
    );
    mat_world = mat_world * rot_mat;
    mat_world[0] *= scale_vec.x;
    mat_world[1] *= scale_vec.y;
    mat_world[2] *= scale_vec.z;
    MODELVIEW_MATRIX = VIEW_MATRIX * mat_world;
    v_color = COLOR;
    v_tile = float(INSTANCE_ID % 4);
}
void fragment() {
    float cx = mod(v_tile, 2.0);
    float cy = floor(v_tile * 0.5);
    vec2 uv = (vec2(cx, cy) + clamp(UV, 0.02, 0.98)) * 0.5;
    vec4 tex = texture(clod_atlas, uv);
    if (tex.a < 0.28) discard;
    // Directional warm sunlight on upper side of tumbling dirt clod vs cool shadow underside
    float top_lit = mix(0.55, 1.15, clamp(1.0 - UV.y, 0.0, 1.0));
    vec3 sun_tint = mix(vec3(0.78, 0.82, 0.90), vec3(1.08, 1.00, 0.88), clamp(1.0 - UV.y, 0.0, 1.0));
    ALBEDO = v_color.rgb * tex.rgb * top_lit * sun_tint;
    ALPHA = tex.a * v_color.a;
}
"""
        };
        ShaderMaterial material = new() { Shader = shader };
        material.SetShaderParameter("clod_atlas", ClodAtlas);
        return new QuadMesh { Size = Vector2.One, Material = material };
    }

    // 16-variant ragged high-contrast blackbody flame tongues with rapid tearing & soft ground blending.
    private static QuadMesh CreateAdditiveFireDrawPass()
    {
        Shader shader = new()
        {
            Code = """
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, fog_disabled;
uniform sampler2D puff_atlas : filter_linear_mipmap;
uniform sampler2D depth_tex : hint_depth_texture, filter_linear_mipmap;
varying vec4 v_color;
varying float v_age;
varying float v_tile;
void vertex() {
    mat4 mat_world = mat4(
        normalize(INV_VIEW_MATRIX[0]),
        normalize(INV_VIEW_MATRIX[1]),
        normalize(INV_VIEW_MATRIX[2]),
        MODEL_MATRIX[3]
    );
    float angle = INSTANCE_CUSTOM.x;
    float c = cos(angle);
    float s = sin(angle);
    mat4 rot_mat = mat4(
        vec4(c, -s, 0.0, 0.0),
        vec4(s,  c, 0.0, 0.0),
        vec4(0.0, 0.0, 1.0, 0.0),
        vec4(0.0, 0.0, 0.0, 1.0)
    );
    vec3 scale_vec = vec3(
        length(MODEL_MATRIX[0].xyz),
        length(MODEL_MATRIX[1].xyz),
        length(MODEL_MATRIX[2].xyz)
    );
    mat_world = mat_world * rot_mat;
    mat_world[0] *= scale_vec.x;
    mat_world[1] *= scale_vec.y;
    mat_world[2] *= scale_vec.z;
    MODELVIEW_MATRIX = VIEW_MATRIX * mat_world;
    v_color = COLOR;
    v_age = INSTANCE_CUSTOM.y;
    v_tile = float(INSTANCE_ID % 16);
}
void fragment() {
    float col_idx = mod(v_tile, 4.0);
    float row_idx = floor(v_tile * 0.25);
    vec2 tile_uv = (vec2(col_idx, row_idx) + clamp(UV, 0.02, 0.98)) * 0.25;
    vec4 tex = texture(puff_atlas, tile_uv);

    float tile2 = mod(v_tile + 11.0, 16.0);
    vec2 tile_uv2 = (vec2(mod(tile2, 4.0), floor(tile2 * 0.25)) + clamp(UV.yx, 0.02, 0.98)) * 0.25;
    float shred = texture(puff_atlas, tile_uv2).a;

    // Shred flame tongues rapidly as they expand so zero smooth round glow puffs appear
    float flame_mask = tex.a * (0.45 + 0.85 * shred);
    float cut = 0.22 + v_age * 0.52;
    float ragged = smoothstep(cut, cut + 0.34, flame_mask);

    float scene_depth = texture(depth_tex, SCREEN_UV).r;
    vec4 upos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, scene_depth, 1.0);
    float soft_fade = clamp((-upos.z / upos.w - (-VERTEX.z)) / 1.0, 0.0, 1.0);

    // Hot incandescent core in thick center, deeper crimson-amber at torn edges
    float hot_core = smoothstep(0.32, 0.84, tex.b * ragged);
    vec3 edge_col = v_color.rgb * vec3(1.0, 0.45, 0.14);
    vec3 core_col = v_color.rgb * vec3(1.25, 1.15, 0.85);
    ALBEDO = mix(edge_col, core_col, hot_core);
    ALPHA = clamp(ragged * v_color.a * soft_fade, 0.0, 0.92);
}
"""
        };
        ShaderMaterial material = new() { Shader = shader };
        material.SetShaderParameter("puff_atlas", SculptedPuffAtlas);
        return new QuadMesh { Size = Vector2.One, Material = material };
    }

    // Velocity-aligned cylindrical billboard shader for jagged dirt/fragmentation ejecta spikes.
    private static QuadMesh CreateEjectaJetDrawPass()
    {
        Shader shader = new()
        {
            Code = """
shader_type spatial;
render_mode unshaded, depth_draw_never, cull_disabled;
uniform sampler2D puff_atlas : filter_linear_mipmap;
uniform sampler2D depth_tex : hint_depth_texture, filter_linear_mipmap;
uniform vec3 sun_dir = vec3(0.46, 0.74, 0.49);
varying vec4 v_color;
varying float v_age;
varying float v_tile;
void vertex() {
    vec3 vel_axis = MODEL_MATRIX[1].xyz;
    float scale_y = max(0.001, length(vel_axis));
    float scale_x = max(0.001, length(MODEL_MATRIX[0].xyz));
    vec3 up_dir = vel_axis / scale_y;
    vec3 cam_dir = normalize(INV_VIEW_MATRIX[2].xyz);
    vec3 right_dir = cross(up_dir, cam_dir);
    if (dot(right_dir, right_dir) < 0.0001) {
        right_dir = normalize(INV_VIEW_MATRIX[0].xyz);
    } else {
        right_dir = normalize(right_dir);
    }
    vec3 fwd_dir = normalize(cross(right_dir, up_dir));
    mat4 mat_world = mat4(
        vec4(right_dir * scale_x, 0.0),
        vec4(up_dir * scale_y, 0.0),
        vec4(fwd_dir * scale_x, 0.0),
        MODEL_MATRIX[3]
    );
    MODELVIEW_MATRIX = VIEW_MATRIX * mat_world;
    v_color = COLOR;
    v_age = INSTANCE_CUSTOM.y;
    v_tile = float(INSTANCE_ID % 16);
}
void fragment() {
    // Shredded fibrous dirt-and-clod spray fountain: dense dark earth clump at tip (UV.y -> 0), granular soil spray tail (UV.y -> 1)
    float along = UV.y;
    float half_w = mix(0.11, 0.46, pow(along, 0.68));
    float radial = abs(UV.x - 0.5) / max(0.03, half_w);
    float col_idx = mod(v_tile, 4.0);
    float row_idx = floor(v_tile * 0.25);
    vec2 stretched_uv = vec2(clamp(UV.x, 0.04, 0.96), fract(UV.y * 1.65 - v_age * 0.35));
    vec2 tile_uv = (vec2(col_idx, row_idx) + clamp(stretched_uv, 0.02, 0.98)) * 0.25;
    float noise = texture(puff_atlas, tile_uv).a;

    float tile2 = mod(v_tile + 5.0, 16.0);
    vec2 grain_uv = (vec2(mod(tile2, 4.0), floor(tile2 * 0.25)) + fract(UV * vec2(2.4, 3.8))) * 0.25;
    float grain = texture(puff_atlas, grain_uv).a;

    float shredded = noise * (0.42 + 0.88 * grain);
    float core_shape = (1.0 - smoothstep(0.24, 0.98, radial + (0.52 - shredded) * 0.62));
    float tip_fade = smoothstep(0.0, 0.09, along) * (1.0 - smoothstep(0.52, 1.0, along));
    float alpha = core_shape * tip_fade * (0.48 + 0.65 * shredded);

    float scene_depth = texture(depth_tex, SCREEN_UV).r;
    vec4 upos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, scene_depth, 1.0);
    float soft_fade = clamp((-upos.z / upos.w - (-VERTEX.z)) / 1.4, 0.0, 1.0);

    // Rich dark soil clod head grading into dusty upturned loam spray along the trailing curtain
    vec3 tip_col = vec3(0.11, 0.085, 0.062);
    vec3 tail_col = vec3(0.38, 0.31, 0.23);
    vec3 base_col = mix(tip_col, tail_col, smoothstep(0.10, 0.72, along));
    float top_light = mix(0.62, 1.12, clamp(1.0 - along * 0.45 + (grain - 0.5) * 0.45, 0.0, 1.0));

    ALBEDO = base_col * v_color.rgb * top_light;
    ALPHA = clamp(alpha * v_color.a * soft_fade * 0.86, 0.0, 0.86);
}
"""
        };
        ShaderMaterial material = new() { Shader = shader };
        material.SetShaderParameter("puff_atlas", SculptedPuffAtlas);
        material.SetShaderParameter("sun_dir", DefaultSunDir);
        return new QuadMesh { Size = new Vector2(0.85f, 5.2f), Material = material };
    }

    private static QuadMesh CreateAdditiveSparkDrawPass()
    {
        // Velocity-aligned incandescent shrapnel needle streak (never vanishes edge-on from any RTS angle).
        Shader shader = new()
        {
            Code = """
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, fog_disabled;
varying vec4 v_color;
void vertex() {
    vec3 vel_axis = MODEL_MATRIX[1].xyz;
    float scale_y = max(0.001, length(vel_axis));
    float scale_x = max(0.001, length(MODEL_MATRIX[0].xyz));
    vec3 up_dir = vel_axis / scale_y;
    vec3 cam_dir = normalize(INV_VIEW_MATRIX[2].xyz);
    vec3 right_dir = cross(up_dir, cam_dir);
    if (dot(right_dir, right_dir) < 0.0001) {
        right_dir = normalize(INV_VIEW_MATRIX[0].xyz);
    } else {
        right_dir = normalize(right_dir);
    }
    vec3 fwd_dir = normalize(cross(right_dir, up_dir));
    mat4 mat_world = mat4(
        vec4(right_dir * scale_x, 0.0),
        vec4(up_dir * scale_y, 0.0),
        vec4(fwd_dir * scale_x, 0.0),
        MODEL_MATRIX[3]
    );
    MODELVIEW_MATRIX = VIEW_MATRIX * mat_world;
    v_color = COLOR;
}
void fragment() {
    float dx = abs(UV.x - 0.5) * 2.0;
    float radial = pow(clamp(1.0 - dx, 0.0, 1.0), 2.4);
    float along = clamp(1.0 - UV.y, 0.0, 1.0);
    float streak = smoothstep(0.0, 0.14, UV.y) * pow(along, 0.65);
    ALBEDO = v_color.rgb;
    ALPHA = radial * streak * v_color.a;
}
"""
        };
        ShaderMaterial material = new() { Shader = shader };
        return new QuadMesh { Size = new Vector2(0.18f, 1.65f), Material = material };
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

    // 4x4 (16 unique tiles) 512x512 sculpted volumetric cumulus atlas:
    // RG = 3D cauliflower bump normal, B = internal thickness, A = ragged cloud density.
    private static Texture2D CreateSculptedPuffAtlas()
    {
        const int tileSize = 128;
        const int grid = 4;
        const int atlasSize = tileSize * grid;
        byte[] pixels = new byte[atlasSize * atlasSize * 4];
        float[] heightField = new float[tileSize * tileSize];
        float[] alphaField = new float[tileSize * tileSize];

        for (int ty = 0; ty < grid; ty++)
        {
            for (int tx = 0; tx < grid; tx++)
            {
                int tileIdx = ty * grid + tx;
                float seedX = (tileIdx * 17.31f) % 97f + 3.1f;
                float seedY = (tileIdx * 29.73f) % 89f + 6.7f;
                float aspectX = 0.88f + (tileIdx % 3) * 0.11f;
                float aspectY = 0.88f + ((tileIdx / 3) % 3) * 0.11f;

                for (int y = 0; y < tileSize; y++)
                {
                    float ny = (y + 0.5f) / tileSize * 2f - 1f;
                    for (int x = 0; x < tileSize; x++)
                    {
                        float nx = (x + 0.5f) / tileSize * 2f - 1f;
                        float ex = nx * aspectX;
                        float ey = ny * aspectY;
                        float r = MathF.Sqrt(ex * ex + ey * ey);

                        // Domain-warped multi-lobe cumulus billows unique to this atlas cell
                        float warpX = (Fbm2D(nx * 2.0f + seedX, ny * 2.0f + seedY) - 0.5f) * 0.48f;
                        float warpY = (Fbm2D(nx * 2.0f + seedY + 13.1f, ny * 2.0f + seedX + 5.9f) - 0.5f) * 0.48f;
                        float lobes = Fbm2D((nx + warpX) * 2.9f + seedX * 0.7f, (ny + warpY) * 2.9f + seedY * 0.7f);
                        float wisps = Fbm2D(nx * 6.8f + seedX * 1.3f, ny * 6.8f + seedY * 1.1f);

                        float edge = 1f - SmoothStep(0.24f + (lobes - 0.5f) * 0.48f, 0.94f, r);
                        float core = 1f - SmoothStep(0.0f, 0.68f, r);
                        float density = Math.Clamp(edge * (0.44f + 0.64f * lobes) + core * 0.22f, 0f, 1f);
                        density = density * density * (3f - 2f * density);

                        float sphereH = MathF.Sqrt(MathF.Max(0f, 1f - MathF.Min(1f, r * r)));
                        float h = sphereH * 0.50f + lobes * 0.34f + wisps * 0.16f;

                        int localIdx = y * tileSize + x;
                        alphaField[localIdx] = density;
                        heightField[localIdx] = h * (0.32f + 0.68f * density);
                    }
                }

                for (int y = 0; y < tileSize; y++)
                {
                    int ym = Math.Max(0, y - 1);
                    int yp = Math.Min(tileSize - 1, y + 1);
                    int ay = ty * tileSize + y;
                    for (int x = 0; x < tileSize; x++)
                    {
                        int xm = Math.Max(0, x - 1);
                        int xp = Math.Min(tileSize - 1, x + 1);
                        float dhdx = (heightField[y * tileSize + xp] - heightField[y * tileSize + xm]) * 7.2f;
                        float dhdy = (heightField[yp * tileSize + x] - heightField[ym * tileSize + x]) * 7.2f;

                        float nx = (x + 0.5f) / tileSize * 2f - 1f;
                        float ny = (y + 0.5f) / tileSize * 2f - 1f;
                        Vector3 normal = new Vector3(nx * 0.52f - dhdx, ny * 0.52f - dhdy, 0.84f).Normalized();

                        int ax = tx * tileSize + x;
                        int pIdx = (ay * atlasSize + ax) * 4;
                        pixels[pIdx] = (byte)MathF.Round(Math.Clamp(normal.X * 0.5f + 0.5f, 0f, 1f) * 255f);
                        pixels[pIdx + 1] = (byte)MathF.Round(Math.Clamp(normal.Y * 0.5f + 0.5f, 0f, 1f) * 255f);
                        pixels[pIdx + 2] = (byte)MathF.Round(Math.Clamp(heightField[y * tileSize + x], 0f, 1f) * 255f);
                        pixels[pIdx + 3] = (byte)MathF.Round(Math.Clamp(alphaField[y * tileSize + x], 0f, 1f) * 255f);
                    }
                }
            }
        }

        Image img = Image.CreateFromData(atlasSize, atlasSize, useMipmaps: false, Image.Format.Rgba8, pixels);
        return ImageTexture.CreateFromImage(img);
    }

    // 2x2 (4 distinct irregular shapes) 128x128 dark soil clod & turf debris atlas.
    private static Texture2D CreateClodAtlas()
    {
        const int tileSize = 64;
        const int atlasSize = tileSize * 2;
        byte[] pixels = new byte[atlasSize * atlasSize * 4];
        for (int ty = 0; ty < 2; ty++)
        {
            for (int tx = 0; tx < 2; tx++)
            {
                int idx = ty * 2 + tx;
                float sx = 2.3f + idx * 11.7f;
                float sy = 9.1f + idx * 7.3f;
                float ax = 0.85f + (idx % 2) * 0.35f;
                float ay = 1.15f - (idx % 2) * 0.28f;
                for (int y = 0; y < tileSize; y++)
                {
                    float ny = (y + 0.5f) / tileSize * 2f - 1f;
                    for (int x = 0; x < tileSize; x++)
                    {
                        float nx = (x + 0.5f) / tileSize * 2f - 1f;
                        float r = MathF.Sqrt(nx * nx * ax + ny * ny * ay);
                        float n = Fbm2D(nx * 3.8f + sx, ny * 3.8f + sy);
                        float mask = 1f - SmoothStep(0.32f + (n - 0.5f) * 0.44f, 0.46f + (n - 0.5f) * 0.44f, r);
                        float shade = Math.Clamp(0.68f - ny * 0.28f + (n - 0.5f) * 0.36f, 0.22f, 1.0f);
                        byte v = (byte)MathF.Round(shade * 255f);
                        int p = ((ty * tileSize + y) * atlasSize + (tx * tileSize + x)) * 4;
                        pixels[p] = v;
                        pixels[p + 1] = v;
                        pixels[p + 2] = v;
                        pixels[p + 3] = (byte)MathF.Round(mask * 255f);
                    }
                }
            }
        }
        Image img = Image.CreateFromData(atlasSize, atlasSize, useMipmaps: false, Image.Format.Rgba8, pixels);
        return ImageTexture.CreateFromImage(img);
    }

    // Ragged multi-octave cloud sprite for tank dust and additive flame tongues.
    private static Texture2D CreateCloudPuffTexture()
    {
        const int size = 128;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            float ny = (y + 0.5f) / size * 2f - 1f;
            for (int x = 0; x < size; x++)
            {
                float nx = (x + 0.5f) / size * 2f - 1f;
                float r = MathF.Sqrt(nx * nx + ny * ny);
                float n = Fbm2D(nx * 3.0f + 4.1f, ny * 3.0f + 7.3f);
                float detail = Fbm2D(nx * 6.5f + 13.7f, ny * 6.5f + 2.9f);

                float edge = 1f - SmoothStep(0.24f + (n - 0.5f) * 0.42f, 0.94f, r);
                float alpha = Math.Clamp(edge * (0.50f + 0.55f * n * (0.7f + 0.3f * detail)), 0f, 1f);
                alpha *= alpha * (3f - 2f * alpha);

                byte lum = (byte)MathF.Round(Math.Clamp(0.76f - ny * 0.16f + (detail - 0.5f) * 0.24f, 0.42f, 1f) * 255f);
                int idx = (y * size + x) * 4;
                pixels[idx] = lum;
                pixels[idx + 1] = lum;
                pixels[idx + 2] = lum;
                pixels[idx + 3] = (byte)MathF.Round(alpha * 255f);
            }
        }
        Image img = Image.CreateFromData(size, size, useMipmaps: false, Image.Format.Rgba8, pixels);
        return ImageTexture.CreateFromImage(img);
    }

    // Tight intense hot-core flare for sparks, muzzle cores, and rocket nozzles.
    private static Texture2D CreateHotGlowTexture()
    {
        const int size = 64;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            float ny = (y + 0.5f) / size * 2f - 1f;
            for (int x = 0; x < size; x++)
            {
                float nx = (x + 0.5f) / size * 2f - 1f;
                float r = MathF.Sqrt(nx * nx + ny * ny);
                float core = MathF.Exp(-r * r * 11.5f);
                float halo = MathF.Exp(-r * r * 3.4f) * 0.28f;
                float alpha = Math.Clamp(core + halo, 0f, 1f);
                int idx = (y * size + x) * 4;
                pixels[idx] = 255;
                pixels[idx + 1] = 255;
                pixels[idx + 2] = 255;
                pixels[idx + 3] = (byte)MathF.Round(alpha * 255f);
            }
        }
        Image img = Image.CreateFromData(size, size, useMipmaps: false, Image.Format.Rgba8, pixels);
        return ImageTexture.CreateFromImage(img);
    }

    // 2x2 (4 distinct high-res variants) 512x512 scorched earth crater atlas with radial blast streaks,
    // charred pit, torn dark-brown soil rim, and directional sunlit rim bevel for perceived 3D depth.
    private static Texture2D CreateCraterTexture()
    {
        const int tileSize = 256;
        const int atlasSize = tileSize * 2;
        byte[] pixels = new byte[atlasSize * atlasSize * 4];
        for (int ty = 0; ty < 2; ty++)
        {
            for (int tx = 0; tx < 2; tx++)
            {
                int variant = ty * 2 + tx;
                float seedX = 9.2f + variant * 13.7f;
                float seedY = 3.4f + variant * 19.3f;
                float rayFreq = 11f + variant * 3f;
                float rayPhase = variant * 1.4f;

                for (int y = 0; y < tileSize; y++)
                {
                    float ny = (y + 0.5f) / tileSize * 2f - 1f;
                    for (int x = 0; x < tileSize; x++)
                    {
                        float nx = (x + 0.5f) / tileSize * 2f - 1f;
                        float r = MathF.Sqrt(nx * nx + ny * ny);
                        float angle = MathF.Atan2(ny, nx);
                        float n1 = Fbm2D(nx * 4.4f + seedX, ny * 4.4f + seedY);
                        float n2 = Fbm2D(nx * 10.5f + seedY, ny * 10.5f + seedX);
                        float rays = 0.5f + 0.5f * MathF.Sin(angle * rayFreq + rayPhase + n1 * 5.2f);
                        float fineRays = 0.5f + 0.5f * MathF.Cos(angle * (rayFreq * 2.1f) - n2 * 4.0f);

                        float warpR = r + (n1 - 0.5f) * 0.14f;
                        float pit = 1f - SmoothStep(0.10f, 0.36f, warpR);
                        float tornRim = SmoothStep(0.24f, 0.42f, warpR) * (1f - SmoothStep(0.46f, 0.72f, warpR));
                        float ejecta = (1f - SmoothStep(0.26f, 0.96f, r)) * (0.32f + 0.48f * rays + 0.20f * fineRays) * (0.45f + 0.55f * n1);
                        float alpha = Math.Clamp(MathF.Max(MathF.Max(pit * 0.94f, tornRim * (0.68f + 0.28f * n2)), ejecta * 0.54f), 0f, 0.92f);

                        // Subtle directional sun relief on the raised crater lip (sun from top-left)
                        float bevel = (nx * 0.55f + ny * 0.65f) * tornRim * 0.085f;
                        float rimFactor = SmoothStep(0.18f, 0.68f, warpR);
                        float dirtGrain = (n2 - 0.5f) * 0.045f;

                        float red = Math.Clamp(0.036f + rimFactor * 0.185f + bevel + dirtGrain, 0.02f, 0.34f);
                        float green = Math.Clamp(0.033f + rimFactor * 0.145f + bevel * 0.88f + dirtGrain * 0.85f, 0.02f, 0.28f);
                        float blue = Math.Clamp(0.030f + rimFactor * 0.100f + bevel * 0.72f + dirtGrain * 0.70f, 0.02f, 0.22f);

                        int idx = ((ty * tileSize + y) * atlasSize + (tx * tileSize + x)) * 4;
                        pixels[idx] = (byte)MathF.Round(red * 255f);
                        pixels[idx + 1] = (byte)MathF.Round(green * 255f);
                        pixels[idx + 2] = (byte)MathF.Round(blue * 255f);
                        pixels[idx + 3] = (byte)MathF.Round(alpha * 255f);
                    }
                }
            }
        }
        Image img = Image.CreateFromData(atlasSize, atlasSize, useMipmaps: false, Image.Format.Rgba8, pixels);
        return ImageTexture.CreateFromImage(img);
    }

    // 4 distinct 256x256 PBR crater Decal texture sets (Albedo + 3D tangent-space Normal + AO/Roughness ORM)
    // so terrain-projected craters drape seamlessly over slopes and catch real directional sunlight.
    private static CraterDecalTextures[] CreateCraterDecalVariants()
    {
        const int size = 256;
        CraterDecalTextures[] variants = new CraterDecalTextures[CraterDecalVariantCount];
        float[] heightField = new float[size * size];

        for (int v = 0; v < CraterDecalVariantCount; v++)
        {
            byte[] albedoPixels = new byte[size * size * 4];
            byte[] normalPixels = new byte[size * size * 4];
            byte[] ormPixels = new byte[size * size * 3];

            float seedX = 11.4f + v * 17.9f;
            float seedY = 5.2f + v * 23.1f;
            float rayFreq = 9f + v * 3f;
            float rayPhase = v * 1.65f;
            float ovalX = 0.94f + (v % 2) * 0.13f;
            float ovalY = 1.06f - (v % 2) * 0.11f;

            for (int y = 0; y < size; y++)
            {
                float ny = (y + 0.5f) / size * 2f - 1f;
                for (int x = 0; x < size; x++)
                {
                    float nx = (x + 0.5f) / size * 2f - 1f;
                    float ex = nx * ovalX;
                    float ey = ny * ovalY;
                    float r = MathF.Sqrt(ex * ex + ey * ey);
                    float angle = MathF.Atan2(ey, ex);

                    float n1 = Fbm2D(nx * 3.8f + seedX, ny * 3.8f + seedY);
                    float n2 = Fbm2D(nx * 10.2f + seedY, ny * 10.2f + seedX);
                    float n3 = Fbm2D(nx * 22.0f + seedX * 0.5f, ny * 22.0f + seedY * 0.5f);
                    float rays = 0.5f + 0.5f * MathF.Sin(angle * rayFreq + rayPhase + n1 * 4.8f);
                    float fineRays = 0.5f + 0.5f * MathF.Cos(angle * (rayFreq * 2.2f) - n2 * 3.6f);

                    float warpR = r + (n1 - 0.5f) * 0.16f + (n2 - 0.5f) * 0.05f;

                    // Real excavated crater profile: concave pit (-1.0) -> raised lumpy turf lip (+0.42) -> flat ground (0.0)
                    float bowl = -(1f - SmoothStep(0.04f, 0.42f, warpR));
                    float lip = SmoothStep(0.26f, 0.46f, warpR) * (1f - SmoothStep(0.48f, 0.72f, warpR));
                    float clodRelief = (n2 - 0.5f) * 0.28f + (n3 - 0.5f) * 0.14f;
                    float radialFurrow = (rays - 0.5f) * 0.12f * (1f - SmoothStep(0.32f, 0.88f, r));
                    float h = bowl * 1.15f + lip * (0.48f + clodRelief) + radialFurrow;
                    heightField[y * size + x] = h;

                    // Mask & feathered outer blend onto grass/dirt
                    float pitMask = 1f - SmoothStep(0.18f, 0.46f, warpR);
                    float rimMask = lip * (0.72f + 0.34f * n2);
                    float ejectaMask = (1f - SmoothStep(0.30f, 0.96f, r)) * (0.24f + 0.52f * rays + 0.24f * fineRays) * (0.38f + 0.62f * n1);
                    float outerFeather = 1f - SmoothStep(0.76f, 0.99f, r);
                    float alpha = Math.Clamp(MathF.Max(MathF.Max(pitMask * 0.96f, rimMask * 0.86f), ejectaMask * 0.56f) * outerFeather, 0f, 0.95f);

                    // Realistic palette: deep charred earth center -> torn dark loam/clay rim -> dusty blasted soil fringe
                    float charBlend = SmoothStep(0.10f, 0.44f, warpR);
                    float soilMix = SmoothStep(0.38f, 0.78f, warpR);
                    float grain = (n3 - 0.5f) * 0.055f + (n2 - 0.5f) * 0.045f;

                    // Charred pit (very dark brown-black)
                    float cR = 0.038f, cG = 0.032f, cB = 0.026f;
                    // Upturned damp loam & torn turf lip
                    float lR = 0.195f, lG = 0.142f, lB = 0.092f;
                    // Outer dusty scorched grass/earth halo
                    float oR = 0.245f, oG = 0.195f, oB = 0.130f;

                    float red = (cR + (lR - cR) * charBlend);
                    float green = (cG + (lG - cG) * charBlend);
                    float blue = (cB + (lB - cB) * charBlend);
                    red = Math.Clamp(red + (oR - red) * soilMix + grain, 0.02f, 0.42f);
                    green = Math.Clamp(green + (oG - green) * soilMix + grain * 0.86f, 0.02f, 0.36f);
                    blue = Math.Clamp(blue + (oB - blue) * soilMix + grain * 0.72f, 0.02f, 0.28f);

                    int p4 = (y * size + x) * 4;
                    albedoPixels[p4] = (byte)MathF.Round(red * 255f);
                    albedoPixels[p4 + 1] = (byte)MathF.Round(green * 255f);
                    albedoPixels[p4 + 2] = (byte)MathF.Round(blue * 255f);
                    albedoPixels[p4 + 3] = (byte)MathF.Round(alpha * 255f);

                    // ORM: deep ambient occlusion inside crater pit, high matte roughness everywhere, zero metallic
                    float ao = Math.Clamp(0.24f + 0.76f * SmoothStep(0.08f, 0.54f, warpR), 0.20f, 1.0f);
                    int p3 = (y * size + x) * 3;
                    ormPixels[p3] = (byte)MathF.Round(ao * 255f);
                    ormPixels[p3 + 1] = 248; // 0.97 roughness (matte soil & ash)
                    ormPixels[p3 + 2] = 0;   // 0.0 metallic
                }
            }

            // Compute tangent-space normal map from crater height field so sun casts real 3D relief across the pit & rim
            for (int y = 0; y < size; y++)
            {
                int ym = Math.Max(0, y - 1);
                int yp = Math.Min(size - 1, y + 1);
                for (int x = 0; x < size; x++)
                {
                    int xm = Math.Max(0, x - 1);
                    int xp = Math.Min(size - 1, x + 1);
                    float dhdx = (heightField[y * size + xp] - heightField[y * size + xm]) * 18.0f;
                    float dhdy = (heightField[yp * size + x] - heightField[ym * size + x]) * 18.0f;
                    Vector3 n = new Vector3(-dhdx, -dhdy, 1.0f).Normalized();

                    int p4 = (y * size + x) * 4;
                    normalPixels[p4] = (byte)MathF.Round(Math.Clamp(n.X * 0.5f + 0.5f, 0f, 1f) * 255f);
                    normalPixels[p4 + 1] = (byte)MathF.Round(Math.Clamp(n.Y * 0.5f + 0.5f, 0f, 1f) * 255f);
                    normalPixels[p4 + 2] = (byte)MathF.Round(Math.Clamp(n.Z * 0.5f + 0.5f, 0f, 1f) * 255f);
                    normalPixels[p4 + 3] = albedoPixels[p4 + 3];
                }
            }

            Image albedoImg = Image.CreateFromData(size, size, useMipmaps: false, Image.Format.Rgba8, albedoPixels);
            Image normalImg = Image.CreateFromData(size, size, useMipmaps: false, Image.Format.Rgba8, normalPixels);
            Image ormImg = Image.CreateFromData(size, size, useMipmaps: false, Image.Format.Rgb8, ormPixels);
            variants[v] = new CraterDecalTextures(
                ImageTexture.CreateFromImage(albedoImg),
                ImageTexture.CreateFromImage(normalImg),
                ImageTexture.CreateFromImage(ormImg));
        }

        return variants;
    }

    private static float Hash2D(int ix, int iy)
    {
        uint h = (uint)(ix * 374761393 + iy * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0x00FFFFFFu) * (1f / 16777216f);
    }

    private static float ValueNoise2D(float x, float y)
    {
        int ix = (int)MathF.Floor(x);
        int iy = (int)MathF.Floor(y);
        float fx = x - ix;
        float fy = y - iy;
        float u = fx * fx * (3f - 2f * fx);
        float v = fy * fy * (3f - 2f * fy);
        float a = Hash2D(ix, iy);
        float b = Hash2D(ix + 1, iy);
        float c = Hash2D(ix, iy + 1);
        float d = Hash2D(ix + 1, iy + 1);
        return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
    }

    private static float Fbm2D(float x, float y)
    {
        float sum = 0f;
        float amp = 0.5f;
        float freq = 1f;
        for (int o = 0; o < 4; o++)
        {
            sum += amp * ValueNoise2D(x * freq, y * freq);
            freq *= 2.03f;
            amp *= 0.5f;
        }
        return sum;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / MathF.Max(1e-5f, edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static CurveTexture Grow(float start)
    {
        Curve curve = new();
        curve.AddPoint(new Vector2(0f, start));
        curve.AddPoint(new Vector2(1f, 1f));
        return new CurveTexture { Curve = curve };
    }

    private static CurveTexture CreatePlumeScaleCurve()
    {
        // Fast explosive initial expansion (0..0.8s) transitioning into slow billowing drift
        Curve curve = new();
        curve.AddPoint(new Vector2(0f, 0.14f));
        curve.AddPoint(new Vector2(0.08f, 0.52f));
        curve.AddPoint(new Vector2(0.35f, 0.84f));
        curve.AddPoint(new Vector2(1f, 1.0f));
        return new CurveTexture { Curve = curve };
    }

    private static CurveTexture CreateContrailScaleCurve()
    {
        Curve curve = new();
        curve.AddPoint(new Vector2(0f, 0.08f));
        curve.AddPoint(new Vector2(0.08f, 0.28f));
        curve.AddPoint(new Vector2(0.38f, 0.64f));
        curve.AddPoint(new Vector2(1f, 1.0f));
        return new CurveTexture { Curve = curve };
    }

    private static CurveTexture CreateEjectaJetScaleCurve()
    {
        Curve curve = new();
        curve.AddPoint(new Vector2(0f, 0.38f));
        curve.AddPoint(new Vector2(0.22f, 1.0f));
        curve.AddPoint(new Vector2(0.75f, 0.92f));
        curve.AddPoint(new Vector2(1f, 0.45f));
        return new CurveTexture { Curve = curve };
    }

    private static GradientTexture1D CreateEjectaJetRamp()
    {
        Gradient gradient = new()
        {
            Offsets = [0f, 0.18f, 0.65f, 1f],
            Colors =
            [
                new Color(0.95f, 0.88f, 0.78f, 0.92f),
                new Color(0.92f, 0.86f, 0.78f, 0.84f),
                new Color(0.85f, 0.80f, 0.72f, 0.44f),
                new Color(0.80f, 0.76f, 0.68f, 0f),
            ],
        };
        return new GradientTexture1D { Gradient = gradient };
    }

    private static CurveTexture CreateFireballScaleCurve()
    {
        Curve curve = new();
        curve.AddPoint(new Vector2(0f, 0.32f));
        curve.AddPoint(new Vector2(0.18f, 1.0f));
        curve.AddPoint(new Vector2(0.58f, 0.80f));
        curve.AddPoint(new Vector2(1f, 0.20f));
        return new CurveTexture { Curve = curve };
    }

    private static CurveTexture CreateDebrisScaleCurve()
    {
        Curve curve = new();
        curve.AddPoint(new Vector2(0f, 0.9f));
        curve.AddPoint(new Vector2(0.75f, 1.0f));
        curve.AddPoint(new Vector2(1f, 0.4f));
        return new CurveTexture { Curve = curve };
    }

    private static GradientTexture1D Fade(Color color)
    {
        Gradient gradient = new()
        {
            Offsets = [0f, 0.18f, 1f],
            Colors = [color, color, new Color(color.R, color.G, color.B, 0f)],
        };
        return new GradientTexture1D { Gradient = gradient };
    }

    private static CurveTexture Shrink(float endScale)
    {
        Curve curve = new();
        curve.AddPoint(new Vector2(0f, 1f));
        curve.AddPoint(new Vector2(1f, endScale * 0.3f));
        return new CurveTexture { Curve = curve };
    }

    // Per-particle initial variation ramps so no two puffs in a column share identical monochrome tint.
    private static GradientTexture1D CreateDustVariationRamp()
    {
        Gradient g = new()
        {
            Offsets = [0f, 0.5f, 1f],
            Colors = [new Color(0.84f, 0.80f, 0.72f), new Color(1.0f, 0.98f, 0.94f), new Color(0.74f, 0.68f, 0.58f)],
        };
        return new GradientTexture1D { Gradient = g };
    }

    private static GradientTexture1D CreateSootVariationRamp()
    {
        Gradient g = new()
        {
            Offsets = [0f, 0.5f, 1f],
            Colors = [new Color(0.70f, 0.70f, 0.72f), new Color(1.0f, 1.0f, 1.0f), new Color(0.52f, 0.50f, 0.48f)],
        };
        return new GradientTexture1D { Gradient = g };
    }

    private static GradientTexture1D CreateClodVariationRamp()
    {
        Gradient g = new()
        {
            Offsets = [0f, 0.5f, 1f],
            Colors = [new Color(0.75f, 0.65f, 0.52f), new Color(1.0f, 0.96f, 0.90f), new Color(0.45f, 0.40f, 0.34f)],
        };
        return new GradientTexture1D { Gradient = g };
    }

    private static GradientTexture1D CreateArtilleryPlumeVariationRamp()
    {
        Gradient g = new()
        {
            Offsets = [0f, 0.45f, 1f],
            Colors = [new Color(0.55f, 0.50f, 0.45f), new Color(0.95f, 0.90f, 0.84f), new Color(0.75f, 0.65f, 0.52f)],
        };
        return new GradientTexture1D { Gradient = g };
    }

    private static GradientTexture1D CreateFireRamp()
    {
        Gradient gradient = new()
        {
            Offsets = [0f, 0.25f, 0.65f, 1f],
            Colors =
            [
                new Color(1.9f, 1.35f, 0.46f, 0.82f),
                new Color(1.3f, 0.44f, 0.07f, 0.62f),
                new Color(0.60f, 0.12f, 0.02f, 0.30f),
                new Color(0.10f, 0.03f, 0.01f, 0f),
            ],
        };
        return new GradientTexture1D { Gradient = gradient };
    }

    private static GradientTexture1D CreateExplosionFireRamp()
    {
        Gradient gradient = new()
        {
            Offsets = [0f, 0.14f, 0.44f, 0.80f, 1f],
            Colors =
            [
                new Color(3.2f, 2.35f, 1.05f, 0.90f),
                new Color(2.0f, 0.72f, 0.12f, 0.74f),
                new Color(0.95f, 0.22f, 0.03f, 0.40f),
                new Color(0.28f, 0.06f, 0.02f, 0.12f),
                new Color(0.05f, 0.02f, 0.01f, 0f),
            ],
        };
        return new GradientTexture1D { Gradient = gradient };
    }

    private static GradientTexture1D CreateSparksRamp()
    {
        Gradient gradient = new()
        {
            Offsets = [0f, 0.22f, 0.68f, 1f],
            Colors =
            [
                new Color(3.8f, 3.0f, 1.4f, 1.0f),
                new Color(2.5f, 1.0f, 0.18f, 0.85f),
                new Color(1.2f, 0.30f, 0.04f, 0.42f),
                new Color(0.35f, 0.06f, 0.01f, 0f),
            ],
        };
        return new GradientTexture1D { Gradient = gradient };
    }

    private static GradientTexture1D CreateDebrisRamp()
    {
        Gradient gradient = new()
        {
            Offsets = [0f, 0.72f, 1f],
            Colors =
            [
                new Color(0.22f, 0.17f, 0.12f, 0.96f),
                new Color(0.26f, 0.21f, 0.15f, 0.88f),
                new Color(0.30f, 0.25f, 0.19f, 0f),
            ],
        };
        return new GradientTexture1D { Gradient = gradient };
    }

    private static GradientTexture1D CreateBackblastRamp()
    {
        Gradient gradient = new()
        {
            Offsets = [0f, 0.05f, 0.22f, 0.62f, 1f],
            Colors =
            [
                new Color(1.0f, 0.74f, 0.42f, 0.88f),
                new Color(0.86f, 0.83f, 0.77f, 0.82f),
                new Color(0.76f, 0.74f, 0.69f, 0.66f),
                new Color(0.68f, 0.67f, 0.63f, 0.30f),
                new Color(0.64f, 0.63f, 0.61f, 0f),
            ],
        };
        return new GradientTexture1D { Gradient = gradient };
    }

    private static GradientTexture1D CreateLaunchGroundBankRamp()
    {
        Gradient gradient = new()
        {
            Offsets = [0f, 0.14f, 0.56f, 1f],
            Colors =
            [
                new Color(0.72f, 0.66f, 0.56f, 0.78f),
                new Color(0.70f, 0.66f, 0.58f, 0.68f),
                new Color(0.66f, 0.64f, 0.59f, 0.34f),
                new Color(0.62f, 0.61f, 0.58f, 0f),
            ],
        };
        return new GradientTexture1D { Gradient = gradient };
    }

    private static GradientTexture1D CreateRocketContrailRamp()
    {
        Gradient gradient = new()
        {
            Offsets = [0f, 0.04f, 0.24f, 0.62f, 0.88f, 1f],
            Colors =
            [
                new Color(0.98f, 0.93f, 0.84f, 0.88f),
                new Color(0.92f, 0.91f, 0.89f, 0.80f),
                new Color(0.86f, 0.86f, 0.85f, 0.54f),
                new Color(0.80f, 0.81f, 0.82f, 0.24f),
                new Color(0.76f, 0.78f, 0.80f, 0.08f),
                new Color(0.74f, 0.76f, 0.78f, 0f),
            ],
        };
        return new GradientTexture1D { Gradient = gradient };
    }

    // Four-phase daytime 122mm HE plume: molten inner flash -> deep charcoal soot -> sunlit earth column -> battlefield haze.
    private static GradientTexture1D CreateArtilleryPlumeRamp()
    {
        Gradient gradient = new()
        {
            Offsets = [0f, 0.045f, 0.18f, 0.48f, 0.80f, 1f],
            Colors =
            [
                new Color(0.70f, 0.34f, 0.14f, 0.95f),
                new Color(0.17f, 0.14f, 0.12f, 0.93f),
                new Color(0.20f, 0.17f, 0.15f, 0.86f),
                new Color(0.32f, 0.29f, 0.25f, 0.58f),
                new Color(0.44f, 0.42f, 0.38f, 0.22f),
                new Color(0.49f, 0.47f, 0.44f, 0f),
            ],
        };
        return new GradientTexture1D { Gradient = gradient };
    }

    private static GradientTexture1D CreateSmokeRamp()
    {
        Gradient gradient = new()
        {
            Offsets = [0f, 0.22f, 0.68f, 1f],
            Colors =
            [
                new Color(0.11f, 0.11f, 0.11f, 0.84f),
                new Color(0.18f, 0.18f, 0.18f, 0.66f),
                new Color(0.28f, 0.28f, 0.28f, 0.32f),
                new Color(0.36f, 0.36f, 0.36f, 0f),
            ],
        };
        return new GradientTexture1D { Gradient = gradient };
    }
}
