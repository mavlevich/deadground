using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;

namespace DeadGround.Client;

internal partial class CombatEffects : Node3D
{
    public const int MaxTracers = 16;
    public const int MaxMuzzleFlashes = 32;
    public const int MaxSmokePuffs = 224;
    public const int MaxImpacts = 32;
    public const int MaxSparks = 24;
    public const int MaxExplosions = 48;
    public const int MaxShockwaves = 32;
    public const int MaxArtilleryPlumes = 32;
    public const int MaxRockets = 48;
    public const int MaxResidues = 64;
    public const int MaxArtilleryCraters = 96;
    public const int MaxPendingImpacts = 16;
    public const int MaxAudioPlayers = 8;
    public const int MaxBlastDust = 6;
    public const int MaxBarrelSmoke = 6;
    public const int MaxRocketBackblasts = 16;
    public const int MaxArtilleryBursts = 16;
    public const int MaxImpactSparkBursts = 8;
    public const int MaxFlashLights = 8;
    public const int EventHistorySize = 256;

    public const float TracerSpeed = 160f;
    public const float TracerLength = 3.2f;
    public const float TracerThickness = 0.28f;
    public const float MinTracerDuration = 0.08f;
    public const float MaxTracerDuration = 0.22f;

    public const float MuzzleFlashLifetime = 0.08f;
    public const float SmokePuffLifetime = 0.40f;
    public const float RocketTrailSmokeLifetime = 1.65f;
    public const float RocketTrailEmitInterval = 0.065f;
    public const float ImpactLifetime = 0.16f;
    public const float SparksLifetime = 0.20f;
    public const float ExplosionLifetime = 0.36f;
    public const float ArtilleryFireballLifetime = 0.16f;
    public const float ShockwaveLifetime = 0.20f;
    public const float ArtilleryPlumeLifetime = 4.60f;
    public const float ResidueLifetime = 15.0f;
    public const float ResidueFadeStart = 4.0f;
    public const float ArtilleryCraterLifetime = 240.0f;
    public const float ArtilleryCraterFadeStart = 25.0f;
    // Muzzle blast dust is strongest for a gun close to the ground and fades out above this height.
    public const float BlastDustMaxMuzzleHeight = 3.5f;
    // Shot particles are skipped for events older than this; younger ones are fast-forwarded.
    public const float GunParticlesMaxEventAge = 0.2f;

    private readonly HeightMap _map;
    private readonly StaticGeometry? _geometry;
    private readonly GpuParticles3D[] _blastDust = new GpuParticles3D[MaxBlastDust];
    private readonly GpuParticles3D[] _barrelSmoke = new GpuParticles3D[MaxBarrelSmoke];
    private readonly GpuParticles3D[] _rocketBackblasts = new GpuParticles3D[MaxRocketBackblasts];
    private readonly GpuParticles3D[] _rocketLaunchGroundBanks = new GpuParticles3D[MaxRocketBackblasts];
    private readonly GpuParticles3D[] _artilleryFireballs = new GpuParticles3D[MaxArtilleryBursts];
    private readonly GpuParticles3D[] _artillerySparks = new GpuParticles3D[MaxArtilleryBursts];
    private readonly GpuParticles3D[] _artilleryDebris = new GpuParticles3D[MaxArtilleryBursts];
    private readonly GpuParticles3D[] _artilleryEjectaJets = new GpuParticles3D[MaxArtilleryBursts];
    private readonly GpuParticles3D[] _artilleryGroundDustRings = new GpuParticles3D[MaxArtilleryBursts];
    private readonly GpuParticles3D[] _artilleryDirtPlumes = new GpuParticles3D[MaxArtilleryBursts];
    private readonly GpuParticles3D[] _impactSparkBursts = new GpuParticles3D[MaxImpactSparkBursts];
    private readonly TimedLight[] _flashLights = new TimedLight[MaxFlashLights];
    private int _nextBlastDust;
    private int _nextBarrelSmoke;
    private int _nextRocketBackblast;
    private int _nextArtilleryBurst;
    private int _nextImpactSparkBurst;

    public Action<Vector3, float>? OnBlastImpulse { get; set; }

    internal GpuParticles3D[] BlastDust => _blastDust;
    internal GpuParticles3D[] BarrelSmoke => _barrelSmoke;
    private readonly uint[] _recentEventIds = new uint[EventHistorySize];
    private int _recentEventIndex;

    private readonly Random _presentationRng = new(42);

    internal struct TimedTracer
    {
        public MeshInstance3D Mesh;
        public Vector3 Origin;
        public Vector3 Target;
        public Vector3 Direction;
        public float TotalDistance;
        public float TotalDuration;
        public float ElapsedTime;
        public float StreakLength;
        public float Thickness;
        public bool Active;
    }

    internal struct TimedMesh
    {
        public MeshInstance3D Mesh;
        public float Lifetime;
        public float MaxLifetime;
        public Vector3 Velocity;
        public float InitialScale;
        public float TargetScale;
        public Vector3 InitialScaleVector;
        public bool Active;
        public bool ScaleDown;
        public bool ScaleUp;
        public bool FadeAlpha;
    }

    internal struct TimedRocket
    {
        public Node3D Root;
        public MeshInstance3D BodyMesh;
        public MeshInstance3D FlameMesh;
        public MeshInstance3D GlowMesh;
        public GpuParticles3D Contrail;
        public System.Numerics.Vector3 Origin;
        public System.Numerics.Vector3 Target;
        public float ApexHeight;
        public float TotalDuration;
        public float ElapsedTime;
        public float SmokeAccumulator;
        public bool IncomingSoundPlayed;
        public bool Active;
    }

    private struct TimedLight
    {
        public OmniLight3D Light;
        public float Lifetime;
        public float MaxLifetime;
        public float InitialEnergy;
        public bool Active;
    }

    private struct TimedDecal
    {
        public Decal Node;
        public float Lifetime;
        public bool Active;
    }

    internal struct PendingImpact
    {
        public Vector3 Target;
        public Vector3 IncomingDirection;
        public HitResult Result;
        public bool IsMachineGun;
        public float DelayRemaining;
        public bool Active;
    }

    internal enum SoundPriority
    {
        TookFire = 1,
        MachineGunBurst = 2,
        Cannon = 3,
        Explosion = 4
    }

    private struct ActiveVoice
    {
        public SoundPriority Priority;
        public float StartTime;
    }

    internal TimedTracer[] Tracers => _tracers;
    internal TimedMesh[] MuzzleFlashes => _muzzleFlashes;
    internal TimedMesh[] SmokePuffs => _smokePuffs;
    internal TimedMesh[] Sparks => _sparks;
    internal TimedMesh[] Impacts => _impacts;
    internal TimedMesh[] Explosions => _explosions;
    internal TimedMesh[] Shockwaves => _shockwaves;
    internal TimedMesh[] ArtilleryPlumes => _artilleryPlumes;
    internal TimedRocket[] Rockets => _rockets;
    internal TimedMesh[] Residues => _residues;
    internal PendingImpact[] PendingImpactsQueue => _pendingImpacts;
    internal AudioStreamPlayer3D[] AudioPlayers => _audioPlayers;

    private readonly TimedTracer[] _tracers = new TimedTracer[MaxTracers];
    private readonly TimedMesh[] _muzzleFlashes = new TimedMesh[MaxMuzzleFlashes];
    private readonly TimedMesh[] _smokePuffs = new TimedMesh[MaxSmokePuffs];
    private readonly TimedMesh[] _sparks = new TimedMesh[MaxSparks];
    private readonly TimedMesh[] _impacts = new TimedMesh[MaxImpacts];
    private readonly TimedMesh[] _explosions = new TimedMesh[MaxExplosions];
    private readonly TimedMesh[] _shockwaves = new TimedMesh[MaxShockwaves];
    private readonly TimedMesh[] _artilleryPlumes = new TimedMesh[MaxArtilleryPlumes];
    private readonly TimedRocket[] _rockets = new TimedRocket[MaxRockets];
    private readonly TimedMesh[] _residues = new TimedMesh[MaxResidues];
    private readonly TimedDecal[] _artilleryCraters = new TimedDecal[MaxArtilleryCraters];
    private readonly PendingImpact[] _pendingImpacts = new PendingImpact[MaxPendingImpacts];

    private readonly AudioStreamPlayer3D[] _audioPlayers = new AudioStreamPlayer3D[MaxAudioPlayers];
    private readonly ActiveVoice[] _activeVoices = new ActiveVoice[MaxAudioPlayers];
    private readonly AudioListener3D _audioListener;
    // A few seeds of the same gun report, so a series of shots does not repeat one sample.
    public const int CannonVariantCount = 3;
    private readonly AudioStreamWav[] _cannonVariants = new AudioStreamWav[CannonVariantCount];
    internal AudioStreamWav[] CannonVariants => _cannonVariants;
    public const int MgBurstVariantCount = 3;
    private readonly AudioStreamWav[] _mgBurstVariants = new AudioStreamWav[MgBurstVariantCount];
    internal AudioStreamWav[] MgBurstVariants => _mgBurstVariants;
    public const int RifleVariantCount = 3;
    private readonly AudioStreamWav[] _rifleVariants = new AudioStreamWav[RifleVariantCount];
    internal AudioStreamWav[] RifleVariants => _rifleVariants;
    public const int RocketLaunchVariantCount = 3;
    private readonly AudioStreamWav[] _rocketLaunchVariants = new AudioStreamWav[RocketLaunchVariantCount];
    internal AudioStreamWav[] RocketLaunchVariants => _rocketLaunchVariants;
    public const int ArtilleryImpactVariantCount = 3;
    private readonly AudioStreamWav[] _artilleryImpactVariants = new AudioStreamWav[ArtilleryImpactVariantCount];
    internal AudioStreamWav[] ArtilleryImpactVariants => _artilleryImpactVariants;
    public const int RocketIncomingVariantCount = 3;
    private readonly AudioStreamWav[] _rocketIncomingVariants = new AudioStreamWav[RocketIncomingVariantCount];
    internal AudioStreamWav[] RocketIncomingVariants => _rocketIncomingVariants;
    internal SoundPriority GetVoicePriority(int index) => _activeVoices[index].Priority;
    private AudioStreamWav? _tookFireAudio;
    private AudioStreamWav? _groundImpactAudio;
    private AudioStreamWav? _explosionAudio;

    internal AudioStreamWav? TookFireAudio => _tookFireAudio;
    internal AudioStreamWav? GroundImpactAudio => _groundImpactAudio;

    private int _droppedMeshEffectsCount;
    private int _droppedAudioVoicesCount;
    private int _peakEffectsCount;
    private int _totalAudioRequests;

    public int ActiveTracersCount => CountActiveTracers();
    public int ActiveMuzzleFlashesCount => CountActiveMeshPool(_muzzleFlashes);
    public int ActiveSmokePuffsCount => CountActiveMeshPool(_smokePuffs);
    public int ActiveSparksCount => CountActiveMeshPool(_sparks);
    public int ActiveImpactsCount => CountActiveMeshPool(_impacts);
    public int ActiveExplosionsCount => CountActiveMeshPool(_explosions);
    public int ActiveShockwavesCount => CountActiveMeshPool(_shockwaves);
    public int ActiveArtilleryPlumesCount => CountActiveMeshPool(_artilleryPlumes);
    public int ActiveRocketsCount => CountActiveRockets();
    public int ActiveResiduesCount => CountActiveMeshPool(_residues);
    public int ActiveArtilleryCratersCount => CountActiveDecalPool(_artilleryCraters);
    public int PendingImpactsCount => CountActivePendingImpacts();

    public int ActiveEffectsCount => CalculateActiveEffectsCount();
    public int DroppedMeshEffectsCount => _droppedMeshEffectsCount;
    public int DroppedAudioVoicesCount => _droppedAudioVoicesCount;
    public int DroppedEffectsCount => _droppedMeshEffectsCount + _droppedAudioVoicesCount;
    public int PeakEffectsCount => _peakEffectsCount;
    public int TotalAudioRequests => _totalAudioRequests;

    public CombatEffects(HeightMap map, StaticGeometry? geometry = null)
    {
        _map = map;
        _geometry = geometry;
        InitTracers();
        InitMuzzleFlashes();
        InitSmokePuffs();
        InitSparks();
        InitImpacts();
        InitExplosions();
        InitShockwaves();
        InitArtilleryPlumes();
        InitRockets();
        InitResidues();
        InitArtilleryCraters();
        for (int i = 0; i < MaxBlastDust; i++)
        {
            _blastDust[i] = ParticleEffects.CreateBlastDust();
            AddChild(_blastDust[i]);
        }
        for (int i = 0; i < MaxBarrelSmoke; i++)
        {
            _barrelSmoke[i] = ParticleEffects.CreateBarrelSmoke();
            AddChild(_barrelSmoke[i]);
        }
        for (int i = 0; i < MaxRocketBackblasts; i++)
        {
            _rocketBackblasts[i] = ParticleEffects.CreateRocketBackblast();
            AddChild(_rocketBackblasts[i]);
            _rocketLaunchGroundBanks[i] = ParticleEffects.CreateRocketLaunchGroundBankParticles();
            AddChild(_rocketLaunchGroundBanks[i]);
        }
        for (int i = 0; i < MaxArtilleryBursts; i++)
        {
            _artilleryFireballs[i] = ParticleEffects.CreateArtilleryFireballParticles();
            AddChild(_artilleryFireballs[i]);
            _artillerySparks[i] = ParticleEffects.CreateArtillerySparksParticles();
            AddChild(_artillerySparks[i]);
            _artilleryDebris[i] = ParticleEffects.CreateArtilleryDebrisParticles();
            AddChild(_artilleryDebris[i]);
            _artilleryEjectaJets[i] = ParticleEffects.CreateArtilleryEjectaJetsParticles();
            AddChild(_artilleryEjectaJets[i]);
            _artilleryGroundDustRings[i] = ParticleEffects.CreateArtilleryGroundDustRingParticles();
            AddChild(_artilleryGroundDustRings[i]);
            _artilleryDirtPlumes[i] = ParticleEffects.CreateArtilleryDirtPlumeParticles();
            AddChild(_artilleryDirtPlumes[i]);
        }
        for (int i = 0; i < MaxImpactSparkBursts; i++)
        {
            _impactSparkBursts[i] = ParticleEffects.CreateImpactSparksParticles();
            AddChild(_impactSparkBursts[i]);
        }
        InitFlashLights();

        _audioListener = new AudioListener3D { Name = "CombatAudioListener" };
        AddChild(_audioListener);
        try
        {
            _audioListener.MakeCurrent();
        }
        catch
        {
            // Headless safe
        }

        InitAudio();
    }

    public void UpdateListener(Vector3 cameraFocus)
    {
        _audioListener.Position = cameraFocus;
    }

    private void InitFlashLights()
    {
        for (int i = 0; i < MaxFlashLights; i++)
        {
            OmniLight3D light = new()
            {
                Name = $"CombatFlashLight_{i}",
                LightColor = new Color(1.0f, 0.68f, 0.28f),
                LightEnergy = 0f,
                OmniRange = 28f,
                OmniAttenuation = 1.6f,
                ShadowEnabled = false,
                Visible = false
            };
            AddChild(light);
            _flashLights[i] = new TimedLight { Light = light, Active = false };
        }
    }

    private void InitTracers()
    {
        Shader tracerShader = new()
        {
            Code = """
shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never, fog_disabled;
varying vec3 v_local;
void vertex() {
    v_local = VERTEX;
}
void fragment() {
    float radial = 1.0 - clamp(length(v_local.xy) * 2.3, 0.0, 1.0);
    radial = pow(radial, 2.2);
    float along = clamp(0.5 - v_local.z, 0.0, 1.0);
    float tail = pow(along, 0.85);
    float core = pow(radial, 2.6) * pow(along, 0.40);
    // Natural daylight tracer pyrotechnic glow: crisp warm-white core with pale amber-ochre sheath
    vec3 col = mix(vec3(1.85, 0.92, 0.34), vec3(3.6, 3.25, 2.25), core);
    ALBEDO = col;
    ALPHA = radial * tail * 0.88;
}
"""
        };
        ShaderMaterial tracerMat = new() { Shader = tracerShader };
        BoxMesh unitBox = new() { Size = new Vector3(1.0f, 1.0f, 1.0f) };
        for (int i = 0; i < MaxTracers; i++)
        {
            MeshInstance3D mesh = new()
            {
                Mesh = unitBox,
                MaterialOverride = tracerMat,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(mesh);
            _tracers[i] = new TimedTracer { Mesh = mesh, Active = false };
        }
    }

    private void InitMuzzleFlashes()
    {
        // Multi-lobe anisotropic muzzle-brake flash: tight forward jet + twin lateral brake vents + hot core.
        Shader flashShader = new()
        {
            Code = """
shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never, fog_disabled;
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
    float s = max(scale_vec.x, max(scale_vec.y, scale_vec.z));
    mat_world[0] *= s;
    mat_world[1] *= s;
    mat_world[2] *= s;
    MODELVIEW_MATRIX = VIEW_MATRIX * mat_world;
}
void fragment() {
    vec2 p = UV * 2.0 - 1.0;
    float r = length(p);
    float ang = atan(p.y, p.x);
    // 4-point crisp asymmetric muzzle blast lobes + intense incandescent heart
    float star = 0.58 + 0.42 * pow(abs(cos(ang * 2.0)), 2.8);
    float core = exp(-r * r * 14.0);
    float lobes = exp(-pow(r / max(0.25, star), 2.0) * 4.8) * 0.72;
    float intensity = clamp(core + lobes, 0.0, 1.0);
    vec3 col = mix(vec3(2.5, 0.92, 0.22), vec3(4.2, 3.5, 2.0), clamp(core * 1.4, 0.0, 1.0));
    ALBEDO = col;
    ALPHA = intensity * 0.92;
}
"""
        };
        ShaderMaterial flashMat = new() { Shader = flashShader };
        QuadMesh flashQuad = new() { Size = new Vector2(2.15f, 2.15f) };
        for (int i = 0; i < MaxMuzzleFlashes; i++)
        {
            MeshInstance3D mesh = new()
            {
                Mesh = flashQuad,
                MaterialOverride = flashMat,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(mesh);
            _muzzleFlashes[i] = new TimedMesh { Mesh = mesh, Active = false };
        }
    }

    private void InitSmokePuffs()
    {
        ShaderMaterial smokeMat = ParticleEffects.CreateLitSmokeQuadMaterial(
            new Color(0.84f, 0.82f, 0.78f),
            maxAlpha: 0.62f);
        QuadMesh smokeQuad = new() { Size = new Vector2(2.4f, 2.4f) };
        for (int i = 0; i < MaxSmokePuffs; i++)
        {
            MeshInstance3D mesh = new()
            {
                Mesh = smokeQuad,
                MaterialOverride = smokeMat,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(mesh);
            _smokePuffs[i] = new TimedMesh { Mesh = mesh, Active = false };
        }
    }

    private void InitSparks()
    {
        StandardMaterial3D sparksMat = new()
        {
            AlbedoColor = new Color(3.2f, 1.8f, 0.45f, 0.92f),
            AlbedoTexture = ParticleEffects.HotGlow,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            BillboardKeepScale = true,
            DisableFog = true
        };
        QuadMesh sparksQuad = new() { Size = new Vector2(1.4f, 1.4f) };
        for (int i = 0; i < MaxSparks; i++)
        {
            MeshInstance3D mesh = new()
            {
                Mesh = sparksQuad,
                MaterialOverride = sparksMat,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(mesh);
            _sparks[i] = new TimedMesh { Mesh = mesh, Active = false };
        }
    }

    private void InitImpacts()
    {
        StandardMaterial3D impactMat = new()
        {
            AlbedoColor = new Color(3.6f, 2.2f, 0.72f, 0.94f),
            AlbedoTexture = ParticleEffects.HotGlow,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            BillboardKeepScale = true,
            DisableFog = true
        };
        QuadMesh impactQuad = new() { Size = new Vector2(2.2f, 2.2f) };
        for (int i = 0; i < MaxImpacts; i++)
        {
            MeshInstance3D mesh = new()
            {
                Mesh = impactQuad,
                MaterialOverride = impactMat,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(mesh);
            _impacts[i] = new TimedMesh { Mesh = mesh, Active = false };
        }
    }

    private void InitExplosions()
    {
        // Turbulent vertex-displaced detonation flash (keeps SphereMesh for test compatibility while
        // eliminating any spherical silhouette via 3D boiling displacement, grazing fade, and soft ground blend).
        Shader fireballShader = new()
        {
            Code = """
shader_type spatial;
render_mode unshaded, cull_back, depth_draw_never, fog_disabled;
uniform sampler2D cloud_tex : source_color;
uniform sampler2D depth_tex : hint_depth_texture, filter_linear_mipmap;
varying float v_disp;
void vertex() {
    vec3 p = VERTEX * 2.5 + vec3(TIME * 5.2, -TIME * 5.8, TIME * 3.9);
    float w1 = sin(p.x * 2.7 + cos(p.y * 3.1)) * cos(p.z * 2.9 - sin(p.x * 2.3));
    float w2 = sin(p.y * 5.5 - p.z * 4.9) * cos(p.x * 5.1 + p.y * 4.1);
    float disp = w1 * 0.46 + w2 * 0.24;
    VERTEX += NORMAL * disp;
    v_disp = disp;
}
void fragment() {
    vec2 uv1 = UV * 3.2 + vec2(TIME * 1.15, -TIME * 1.35);
    vec2 uv2 = UV * 6.4 + vec2(-TIME * 0.85, TIME * 1.05);
    float n1 = texture(cloud_tex, uv1).a;
    float n2 = texture(cloud_tex, uv2).a;
    float shred = n1 * 0.52 + n2 * 0.48;
    float fresnel = pow(clamp(dot(NORMAL, VIEW), 0.0, 1.0), 2.3);
    // High-contrast soot-crusted blackbody tearing: dark black blast soot veins cutting right across blinding fire
    float heat = clamp(fresnel * 0.72 + shred * 0.62 + v_disp * 0.42 - 0.30, 0.0, 1.0);
    vec3 soot = vec3(0.045, 0.038, 0.032);
    vec3 crimson = vec3(1.45, 0.28, 0.04);
    vec3 flame = vec3(2.85, 0.96, 0.16);
    vec3 core = vec3(4.6, 3.8, 2.2);
    vec3 col = mix(soot, crimson, smoothstep(0.24, 0.46, heat));
    col = mix(col, flame, smoothstep(0.46, 0.70, heat));
    col = mix(col, core, smoothstep(0.74, 0.94, heat));

    float scene_depth = texture(depth_tex, SCREEN_UV).r;
    vec4 upos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, scene_depth, 1.0);
    float soft_fade = clamp((-upos.z / upos.w - (-VERTEX.z)) / 1.4, 0.0, 1.0);

    ALBEDO = col;
    ALPHA = clamp(smoothstep(0.26, 0.62, shred * fresnel + 0.04) * soft_fade, 0.0, 0.90);
}
"""
        };
        ShaderMaterial explosionMat = new() { Shader = fireballShader };
        explosionMat.SetShaderParameter("cloud_tex", ParticleEffects.CloudPuff);
        SphereMesh explosionSphere = new() { Radius = 1.6f, Height = 3.2f, RadialSegments = 20, Rings = 14 };
        for (int i = 0; i < MaxExplosions; i++)
        {
            MeshInstance3D mesh = new()
            {
                Mesh = explosionSphere,
                MaterialOverride = explosionMat,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(mesh);
            _explosions[i] = new TimedMesh { Mesh = mesh, Active = false };
        }
    }

    private void InitShockwaves()
    {
        // Ultra-subtle, fleeting earthy pressure-dust veil (no white sci-fi circle; 3D GPU dust ring carries the volume).
        Shader shockShader = new()
        {
            Code = """
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;
uniform sampler2D cloud_tex : source_color;
uniform sampler2D depth_tex : hint_depth_texture, filter_linear_mipmap;
void fragment() {
    vec2 p = UV * 2.0 - 1.0;
    float ang = atan(p.y, p.x);
    float n1 = texture(cloud_tex, UV * 2.8 + vec2(0.17, 0.31)).a;
    float n2 = texture(cloud_tex, UV * 5.4 - vec2(0.23, 0.11)).a;
    float warp = sin(ang * 5.0 + 0.7) * 0.045 + cos(ang * 9.0 - 1.3) * 0.032 + (n1 - 0.5) * 0.18 + (n2 - 0.5) * 0.08;
    float r = length(p) + warp;
    float dustVeil = smoothstep(0.42, 0.80, r) * (1.0 - smoothstep(0.82, 0.99, r)) * (n1 * n2 * 1.45);

    float scene_depth = texture(depth_tex, SCREEN_UV).r;
    vec4 upos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, scene_depth, 1.0);
    float soft_fade = clamp((-upos.z / upos.w - (-VERTEX.z)) / 0.95, 0.0, 1.0);

    ALBEDO = vec3(0.52, 0.46, 0.37);
    ALPHA = clamp(dustVeil * 0.14 * soft_fade, 0.0, 0.14);
}
"""
        };
        ShaderMaterial shockMat = new() { Shader = shockShader };
        shockMat.SetShaderParameter("cloud_tex", ParticleEffects.CloudPuff);
        QuadMesh shockQuad = new() { Size = new Vector2(2.0f, 2.0f) };
        for (int i = 0; i < MaxShockwaves; i++)
        {
            MeshInstance3D mesh = new()
            {
                Mesh = shockQuad,
                MaterialOverride = shockMat,
                Rotation = new Vector3(-Mathf.Pi * 0.5f, 0f, 0f),
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(mesh);
            _shockwaves[i] = new TimedMesh { Mesh = mesh, Active = false };
        }
    }

    private void InitArtilleryPlumes()
    {
        ShaderMaterial plumeMat = ParticleEffects.CreateLitSmokeQuadMaterial(
            new Color(0.26f, 0.23f, 0.19f),
            maxAlpha: 0.78f);
        QuadMesh plumeQuad = new() { Size = new Vector2(3.2f, 3.4f) };
        for (int i = 0; i < MaxArtilleryPlumes; i++)
        {
            MeshInstance3D mesh = new()
            {
                Mesh = plumeQuad,
                MaterialOverride = plumeMat,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(mesh);
            _artilleryPlumes[i] = new TimedMesh { Mesh = mesh, Active = false };
        }
    }

    private void InitRockets()
    {
        StandardMaterial3D bodyMat = new()
        {
            AlbedoColor = new Color(0.22f, 0.25f, 0.19f),
            Roughness = 0.65f,
            Metallic = 0.28f
        };
        Shader flameShader = new()
        {
            Code = """
shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never, fog_disabled;
varying vec3 v_local;
void vertex() {
    v_local = VERTEX;
}
void fragment() {
    float along = clamp(v_local.y + 0.5, 0.0, 1.0);
    float core = pow(along, 2.3);
    // Supersonic solid-rocket Mach shock diamonds pulsing down the exhaust cone
    float shock_diamonds = 0.84 + 0.16 * pow(0.5 + 0.5 * cos(along * 26.0 - TIME * 42.0), 2.0);
    float flicker = (0.90 + 0.10 * sin(TIME * 76.0 + along * 14.0)) * shock_diamonds;
    vec3 col = mix(vec3(2.6, 0.88, 0.16), vec3(5.0, 4.4, 3.0), core) * flicker;
    ALBEDO = col;
    ALPHA = pow(along, 0.82) * 0.92;
}
"""
        };
        ShaderMaterial flameMat = new() { Shader = flameShader };
        StandardMaterial3D glowHaloMat = new()
        {
            AlbedoColor = new Color(3.6f, 2.1f, 0.75f, 0.82f),
            AlbedoTexture = ParticleEffects.HotGlow,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            BillboardKeepScale = true,
            DisableFog = true
        };
        CylinderMesh bodyMesh = new() { TopRadius = 0.11f, BottomRadius = 0.13f, Height = 2.6f, RadialSegments = 8 };
        CylinderMesh flameMesh = new() { TopRadius = 0.22f, BottomRadius = 0.02f, Height = 2.2f, RadialSegments = 10 };
        QuadMesh glowQuad = new() { Size = new Vector2(1.45f, 1.45f) };

        for (int i = 0; i < MaxRockets; i++)
        {
            Node3D root = new() { Name = $"ArtilleryRocket_{i}", Visible = false };
            MeshInstance3D body = new()
            {
                Mesh = bodyMesh,
                MaterialOverride = bodyMat,
                Rotation = new Vector3(Mathf.Pi * 0.5f, 0f, 0f),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            MeshInstance3D flame = new()
            {
                Mesh = flameMesh,
                MaterialOverride = flameMat,
                Position = new Vector3(0f, 0f, 2.35f),
                Rotation = new Vector3(-Mathf.Pi * 0.5f, 0f, 0f),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            MeshInstance3D glow = new()
            {
                Mesh = glowQuad,
                MaterialOverride = glowHaloMat,
                Position = new Vector3(0f, 0f, 1.45f),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            GpuParticles3D contrail = ParticleEffects.CreateRocketContrail();
            contrail.Position = new Vector3(0f, 0f, 1.40f);
            root.AddChild(body);
            root.AddChild(flame);
            root.AddChild(glow);
            root.AddChild(contrail);
            AddChild(root);
            _rockets[i] = new TimedRocket
            {
                Root = root,
                BodyMesh = body,
                FlameMesh = flame,
                GlowMesh = glow,
                Contrail = contrail,
                Active = false
            };
        }
    }

    private void InitResidues()
    {
        ShaderMaterial residueMat = ParticleEffects.CreateCraterMaterial();
        QuadMesh craterQuad = new() { Size = new Vector2(4.4f, 4.4f) };
        for (int i = 0; i < MaxResidues; i++)
        {
            MeshInstance3D mesh = new()
            {
                Mesh = craterQuad,
                MaterialOverride = residueMat,
                Rotation = new Vector3(-Mathf.Pi * 0.5f, 0f, 0f),
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(mesh);
            _residues[i] = new TimedMesh { Mesh = mesh, Active = false };
        }
    }

    private void InitArtilleryCraters()
    {
        ParticleEffects.CraterDecalTextures[] variants = ParticleEffects.CraterDecalVariants;
        for (int i = 0; i < MaxArtilleryCraters; i++)
        {
            ParticleEffects.CraterDecalTextures tex = variants[i % variants.Length];
            Decal decal = new()
            {
                Name = $"ArtilleryCraterDecal_{i}",
                TextureAlbedo = tex.Albedo,
                TextureNormal = tex.Normal,
                TextureOrm = tex.Orm,
                AlbedoMix = 1.0f,
                UpperFade = 0.35f,
                LowerFade = 0.35f,
                NormalFade = 0.45f,
                Size = new Vector3(8.5f, 2.4f, 8.5f),
                Modulate = Colors.White,
                Visible = false
            };
            AddChild(decal);
            _artilleryCraters[i] = new TimedDecal { Node = decal, Active = false };
        }
    }

    private void InitAudio()
    {
        for (int i = 0; i < CannonVariantCount; i++)
            _cannonVariants[i] = CannonSound.CreateStream(1337 + i);
        for (int i = 0; i < MgBurstVariantCount; i++)
            _mgBurstVariants[i] = CannonSound.CreateBurstStream(2000 + i * 17);
        for (int i = 0; i < RifleVariantCount; i++)
            _rifleVariants[i] = CannonSound.CreateRifleStream(3000 + i * 29);
        for (int i = 0; i < RocketLaunchVariantCount; i++)
            _rocketLaunchVariants[i] = CannonSound.CreateRocketLaunchStream(4000 + i * 31);
        for (int i = 0; i < ArtilleryImpactVariantCount; i++)
            _artilleryImpactVariants[i] = CannonSound.CreateArtilleryImpactStream(5000 + i * 37);
        for (int i = 0; i < RocketIncomingVariantCount; i++)
            _rocketIncomingVariants[i] = CannonSound.CreateRocketIncomingStream(6000 + i * 41);
        _tookFireAudio = SynthesizeTookFireSound();
        _groundImpactAudio = CannonSound.CreateGroundImpactStream(1337);
        _explosionAudio = SynthesizeExplosionSound();

        for (int i = 0; i < MaxAudioPlayers; i++)
        {
            AudioStreamPlayer3D player = new()
            {
                MaxDistance = 250f,
                UnitSize = 15f
            };
            AddChild(player);
            _audioPlayers[i] = player;
        }
    }

    private static AudioStreamWav SynthesizeTookFireSound()
    {
        const int sampleRate = 44100;
        const float duration = 0.45f;
        int count = (int)(sampleRate * duration);
        byte[] data = new byte[count * 2];
        Random rand = new(999);

        float lpState = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / sampleRate;

            // 1. Ultra-sharp ballistic strike transient (0-10ms)
            float snapEnv = MathF.Exp(-t * 240.0f);
            float snap = (((float)rand.NextDouble() * 2f - 1f) * 0.7f + MathF.Sin(2f * MathF.PI * 1850.0f * t) * 0.5f) * snapEnv;

            // 2. Kinetic mass momentum transfer thud (0-150ms)
            float thudEnv = MathF.Exp(-t * 24.0f);
            float thudFreq = 45.0f + 75.0f * MathF.Exp(-t * 35.0f);
            float thud = MathF.Sin(2f * MathF.PI * thudFreq * t) * thudEnv * 0.9f;

            // 3. Heavy rolled homogeneous armor ringing (inharmonic steel modes)
            float ringEnv = MathF.Exp(-t * 16.0f);
            float ring = (MathF.Sin(2f * MathF.PI * 380.0f * t) * 0.40f +
                          MathF.Sin(2f * MathF.PI * 690.0f * t) * 0.35f +
                          MathF.Sin(2f * MathF.PI * 1280.0f * t) * 0.25f +
                          MathF.Sin(2f * MathF.PI * 2450.0f * t) * 0.15f) * ringEnv;

            // 4. Spall and metal fragment scatter
            float white = (float)rand.NextDouble() * 2f - 1f;
            lpState += (white - lpState) * 0.35f;
            float sizzle = lpState * MathF.Exp(-t * 30.0f) * 0.35f;

            float raw = snap * 1.0f + thud * 1.1f + ring * 0.8f + sizzle * 0.5f;
            float sat = MathF.Tanh(raw * 1.5f) * 0.88f;

            short pcm = (short)(sat * 28000);
            data[i * 2] = (byte)(pcm & 0xFF);
            data[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
        }

        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = sampleRate,
            Stereo = false,
            Data = data
        };
    }


    private static AudioStreamWav SynthesizeExplosionSound()
    {
        const int sampleRate = 44100;
        const float duration = 1.8f;
        int count = (int)(sampleRate * duration);
        byte[] data = new byte[count * 2];
        Random rand = new(777);

        float lpState = 0f;
        float lpState2 = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / sampleRate;

            // 1. Initial explosive blast wave transient (0-30ms)
            float crackEnv = MathF.Exp(-t * 120.0f);
            float crack = (((float)rand.NextDouble() * 2f - 1f) * 0.8f + MathF.Sin(2f * MathF.PI * 180.0f * MathF.Exp(-t * 60.0f) * t) * 0.6f) * crackEnv;

            // 2. Catastrophic ammo cook-off & fuel fireball roar (20ms - 600ms)
            float roarEnv = MathF.Pow(MathF.Sin(MathF.Min(1.0f, t / 0.05f) * (MathF.PI * 0.5f)), 2f) * MathF.Exp(-t * 5.5f);
            float subRumble = MathF.Sin(2f * MathF.PI * (32.0f + 18.0f * MathF.Exp(-t * 4.0f)) * t);
            float midRumble = MathF.Sin(2f * MathF.PI * (52.0f + 26.0f * MathF.Exp(-t * 6.0f)) * t) * 0.5f;
            float roar = (subRumble + midRumble) * roarEnv;

            // 3. Structural metal tear / shudder
            float shudderEnv = MathF.Exp(-t * 14.0f);
            float shudder = (MathF.Sin(2f * MathF.PI * 120.0f * t) * 0.4f + MathF.Sin(2f * MathF.PI * 280.0f * t) * 0.3f) * shudderEnv;

            // 4. Long rolling shockwave rumble & debris tail (50ms - 1.8s)
            float white = (float)rand.NextDouble() * 2f - 1f;
            float cutoff = MathF.Max(0.012f, 0.16f * MathF.Exp(-t * 2.2f));
            lpState += (white - lpState) * cutoff;
            lpState2 += (lpState - lpState2) * cutoff;

            float tailEnv = MathF.Pow(MathF.Sin(MathF.Min(1.0f, t / 0.07f) * (MathF.PI * 0.5f)), 2f) * MathF.Exp(-t * 2.8f) * 0.8f;
            float tail = lpState2 * tailEnv;

            float raw = crack * 0.95f + roar * 1.3f + shudder * 0.4f + tail * 1.1f;
            float sat = MathF.Tanh(raw * 1.7f) * 0.94f;

            short pcm = (short)(sat * 31000);
            data[i * 2] = (byte)(pcm & 0xFF);
            data[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
        }

        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = sampleRate,
            Stereo = false,
            Data = data
        };
    }

    private void PlaySound(AudioStreamWav? stream, Vector3 position, SoundPriority priority, float maxDistance = 250f, float unitSize = 15f)
    {
        if (stream is null) return;
        _totalAudioRequests++;

        try
        {
            int selectedPlayer = -1;

            for (int i = 0; i < MaxAudioPlayers; i++)
            {
                if (!_audioPlayers[i].Playing)
                {
                    selectedPlayer = i;
                    break;
                }
            }

            if (selectedPlayer == -1)
            {
                int lowestPriorityIdx = -1;
                SoundPriority lowestPrio = SoundPriority.Explosion + 1;
                for (int i = 0; i < MaxAudioPlayers; i++)
                {
                    if (_activeVoices[i].Priority < lowestPrio)
                    {
                        lowestPrio = _activeVoices[i].Priority;
                        lowestPriorityIdx = i;
                    }
                }

                if (lowestPriorityIdx != -1 && lowestPrio < priority)
                {
                    selectedPlayer = lowestPriorityIdx;
                }
                else
                {
                    _droppedAudioVoicesCount++;
                    return;
                }
            }

            AudioStreamPlayer3D player = _audioPlayers[selectedPlayer];
            player.MaxDistance = maxDistance;
            player.UnitSize = unitSize;
            player.Position = position;
            player.Stream = stream;
            player.PitchScale = 0.96f + (float)(_presentationRng.NextDouble() * 0.08);
            player.Play();

            _activeVoices[selectedPlayer] = new ActiveVoice
            {
                Priority = priority,
                StartTime = (float)Godot.Time.GetTicksMsec() / 1000f
            };
        }
        catch
        {
            // Headless dummy audio driver fallback
        }
    }

    public void ProcessEvents(ReadOnlySpan<CombatEventObservation> events, UnitViewSet unitViews, int currentTick = 0, ContactViewSet? contactViews = null)
    {
        for (int i = 0; i < events.Length; i++)
        {
            ref readonly CombatEventObservation ev = ref events[i];
            if (HasSeenEvent(ev.EventId)) continue;
            RecordSeenEvent(ev.EventId);

            float ageSeconds = 0f;
            if (currentTick > 0 && ev.ServerTick > 0 && currentTick >= ev.ServerTick)
            {
                ageSeconds = (currentTick - ev.ServerTick) * 0.05f;
            }

            Vector3 origin = new(ev.Origin.X, ev.Origin.Y, ev.Origin.Z);
            Vector3 target = new(ev.Target.X, ev.Target.Y, ev.Target.Z);
            Vector3 shotDir = (target - origin).LengthSquared() > 0.0001f
                ? (target - origin).Normalized()
                : Vector3.Forward;

            if (ev.Kind == CombatEventKind.ShotFired)
            {
                bool isRifle = WeaponPresentation.IsRifle(ev.PresentationId);
                bool isMg = WeaponPresentation.IsMachineGun(ev.PresentationId);
                float distance = origin.DistanceTo(target);
                float flightDuration = Mathf.Clamp(distance / TracerSpeed, MinTracerDuration, MaxTracerDuration);

                // If entire shot sequence including impact has expired, discard
                if (ageSeconds >= flightDuration + SmokePuffLifetime)
                {
                    continue;
                }

                if (ev.ShooterUnitId.Value != 0 && unitViews.Views.TryGetValue(ev.ShooterUnitId, out UnitView? shooter))
                {
                    if (shooter.TryGetMuzzleTransform(out Transform3D muzzleXform))
                    {
                        origin = muzzleXform.Origin;
                        if (origin != target)
                        {
                            shotDir = (target - origin).Normalized();
                        }
                    }
                    if (ageSeconds < 0.10f)
                    {
                        shooter.ApplyRecoil(shotDir);
                    }
                }
                else if (contactViews is not null)
                {
                    foreach (ContactView cv in contactViews.Views.Values)
                    {
                        if (cv.Observation.IsCurrent && cv.Root.Position.DistanceTo(new Vector3(origin.X, cv.Root.Position.Y, origin.Z)) < 4.0f)
                        {
                            if (cv.TryGetMuzzleTransform(out Transform3D muzzleXform))
                            {
                                origin = muzzleXform.Origin;
                                if (origin != target)
                                {
                                    shotDir = (target - origin).Normalized();
                                }
                            }
                            if (ageSeconds < 0.10f)
                            {
                                cv.SetAiming(true);
                            }
                            break;
                        }
                    }
                }

                if (ageSeconds < MuzzleFlashLifetime)
                {
                    SpawnMuzzleFlash(origin, shotDir, isMachineGun: isMg, isRifle: isRifle, elapsedOffset: ageSeconds);
                    if (!isRifle && !isMg)
                    {
                        SpawnFlashLight(origin, energy: 6.0f, range: 24f, lifetime: 0.16f);
                        OnBlastImpulse?.Invoke(origin, 0.16f);
                    }
                }

                if (!isRifle && ageSeconds < GunParticlesMaxEventAge)
                {
                    SpawnGunParticles(origin, shotDir, ageSeconds);
                }

                if (!isRifle && ageSeconds < SmokePuffLifetime)
                {
                    SpawnSmokePuff(origin, isImpactSmoke: false, ageSeconds);
                }

                if (ageSeconds < flightDuration)
                {
                    SpawnTracer(origin, target, flightDuration, isMachineGun: isMg, isRifle: isRifle, elapsedOffset: ageSeconds);
                    QueuePendingImpact(target, shotDir, ev.Result, flightDuration - ageSeconds, isMachineGun: isMg, isRifle: isRifle);
                }
                else
                {
                    float hitElapsed = ageSeconds - flightDuration;
                    if (hitElapsed < SmokePuffLifetime)
                    {
                        if (ev.Result == HitResult.Deflected)
                        {
                            TriggerRicochetFeedback(target, shotDir, hitElapsed, isMachineGun: isMg || isRifle);
                        }
                        else if (ev.Result == HitResult.Miss)
                        {
                            TriggerGroundImpactFeedback(target, hitElapsed);
                        }
                        else
                        {
                            TriggerImpactFeedback(target, hitElapsed);
                        }
                    }
                }

                if (ageSeconds < 0.15f)
                {
                    if (isRifle)
                    {
                        PlaySound(_rifleVariants[_presentationRng.Next(RifleVariantCount)], origin, SoundPriority.MachineGunBurst);
                    }
                    else if (isMg)
                    {
                        PlaySound(_mgBurstVariants[_presentationRng.Next(MgBurstVariantCount)], origin, SoundPriority.MachineGunBurst);
                    }
                    else
                    {
                        PlaySound(_cannonVariants[_presentationRng.Next(CannonVariantCount)], origin, SoundPriority.Cannon);
                    }
                }
            }
            else if (ev.Kind == CombatEventKind.UnitDestroyed)
            {
                if (ageSeconds >= ResidueLifetime)
                {
                    continue;
                }

                CancelPendingImpactsNear(target, 3.0f);

                // Infantry units leave no vehicle wreck and produce no vehicle explosion, burning hull, or crater
                if (!IsInfantryVictim(ev, unitViews))
                {
                    if (ageSeconds < ExplosionLifetime)
                    {
                        SpawnExplosion(target, ageSeconds);
                        SpawnArtilleryBurstParticles(target, ageSeconds, scale: 0.72f);
                        SpawnFlashLight(target + new Vector3(0f, 2.2f, 0f), energy: 7.5f, range: 32f, lifetime: 0.35f);
                        if (ageSeconds < 0.15f)
                        {
                            OnBlastImpulse?.Invoke(target, 0.72f);
                        }
                    }

                    SpawnResidue(target, ageSeconds, scaleMultiplier: 0.72f);
                    SpawnArtilleryCrater(target, ageSeconds, baseDiameter: 5.6f);

                    if (ageSeconds < 0.20f)
                    {
                        PlaySound(_explosionAudio, target, SoundPriority.Explosion);
                    }
                }
            }
            else if (ev.Kind == CombatEventKind.TookFire)
            {
                if (ageSeconds < SmokePuffLifetime)
                {
                    bool isRifle = WeaponPresentation.IsRifle(ev.PresentationId);
                    bool isMg = WeaponPresentation.IsMachineGun(ev.PresentationId);
                    if (ev.Result == HitResult.Deflected)
                    {
                        TriggerRicochetFeedback(target, shotDir, ageSeconds, isMachineGun: isMg || isRifle);
                    }
                    else if (ev.Result == HitResult.Miss)
                    {
                        TriggerGroundImpactFeedback(target, ageSeconds);
                    }
                    else
                    {
                        TriggerImpactFeedback(target, ageSeconds);
                    }
                }
            }
            else if (ev.Kind == CombatEventKind.RocketLaunched)
            {
                System.Numerics.Vector3 simOrigin = ev.Origin;
                System.Numerics.Vector3 simTarget = ev.Target;
                ArtilleryBallistics.ComputeTrajectory(simOrigin, simTarget, ArtilleryBallistics.DefaultRocketSpeed, out int flightTicks, out float apexHeight);
                float flightDuration = flightTicks * Simulation.TickSeconds;

                if (ageSeconds >= flightDuration + RocketTrailSmokeLifetime)
                {
                    continue;
                }

                Vector3 launchOrigin = origin;
                Vector3 backblastPos = origin - shotDir * 3.0f;
                Basis tubeBasis = Basis.Identity;
                bool hasBackblast = false;

                if (ev.ShooterUnitId.Value != 0 && unitViews.Views.TryGetValue(ev.ShooterUnitId, out UnitView? shooter))
                {
                    if (shooter.TryGetNextLaunchTubeTransform(out Transform3D tubeXform))
                    {
                        launchOrigin = tubeXform.Origin;
                        tubeBasis = tubeXform.Basis.Orthonormalized();
                        // Tube bore faces -Z; rear exhaust backblast exits +Z ~3.2m behind muzzle
                        backblastPos = tubeXform.Origin + tubeBasis.Z * 3.2f;
                        hasBackblast = true;
                    }
                    if (ageSeconds < 0.15f)
                    {
                        shooter.ApplyRocketLaunchRecoil(shotDir);
                    }
                }

                if (ageSeconds < MuzzleFlashLifetime * 2.2f)
                {
                    SpawnMuzzleFlash(launchOrigin, shotDir, isMachineGun: false, isRifle: false, elapsedOffset: ageSeconds * 0.45f);
                    SpawnFlashLight(launchOrigin + new Vector3(0f, 1.0f, 0f), energy: 5.5f, range: 26f, lifetime: 0.22f);
                    if (ageSeconds < 0.12f)
                    {
                        OnBlastImpulse?.Invoke(launchOrigin, 0.14f);
                    }
                    if (hasBackblast)
                    {
                        SpawnMuzzleFlash(backblastPos, tubeBasis.Z, isMachineGun: false, isRifle: false, elapsedOffset: ageSeconds * 0.45f);
                        SpawnRocketBackblastParticles(backblastPos, tubeBasis, ageSeconds);
                    }
                }

                if (ageSeconds < SmokePuffLifetime)
                {
                    SpawnRocketTrailSmoke(launchOrigin, initialScale: 0.90f, targetScale: 4.0f, lifetime: 3.2f, elapsedOffset: ageSeconds);
                    if (hasBackblast)
                    {
                        SpawnRocketTrailSmoke(backblastPos, initialScale: 1.40f, targetScale: 5.8f, lifetime: 4.8f, elapsedOffset: ageSeconds);
                    }
                }

                if (ageSeconds < flightDuration)
                {
                    System.Numerics.Vector3 visualOrigin = new(launchOrigin.X, launchOrigin.Y, launchOrigin.Z);
                    SpawnRocket(visualOrigin, simTarget, apexHeight, flightDuration, ageSeconds);
                }

                if (ageSeconds < 0.20f)
                {
                    PlaySound(
                        _rocketLaunchVariants[_presentationRng.Next(RocketLaunchVariantCount)],
                        launchOrigin,
                        SoundPriority.Cannon,
                        maxDistance: 950f,
                        unitSize: 48f);
                }
            }
            else if (ev.Kind == CombatEventKind.ArtilleryImpact)
            {
                if (ageSeconds >= ArtilleryCraterLifetime)
                {
                    continue;
                }

                if (ageSeconds < ArtilleryFireballLifetime)
                {
                    SpawnArtilleryFireball(target, ageSeconds);
                    SpawnArtilleryBurstParticles(target, ageSeconds, scale: 1.0f);
                    SpawnFlashLight(target + new Vector3(0f, 2.2f, 0f), energy: 8.5f, range: 36f, lifetime: 0.16f);
                    if (ageSeconds < 0.15f)
                    {
                        OnBlastImpulse?.Invoke(target, 1.0f);
                    }
                }

                if (ageSeconds < ShockwaveLifetime)
                {
                    SpawnShockwave(target, ageSeconds);
                }

                if (ageSeconds < ArtilleryPlumeLifetime)
                {
                    SpawnArtilleryPlume(target, ageSeconds);
                }

                SpawnArtilleryCrater(target, ageSeconds, baseDiameter: 8.8f);

                if (ageSeconds < 0.25f)
                {
                    PlaySound(
                        _artilleryImpactVariants[_presentationRng.Next(ArtilleryImpactVariantCount)],
                        target,
                        SoundPriority.Explosion,
                        maxDistance: 1350f,
                        unitSize: 72f);
                }
            }
        }
    }

    private static readonly Lazy<UnitCatalog> LazyColdWarCatalog = new(() => ColdWarContent.LoadCatalog());

    private static bool IsInfantryVictim(CombatEventObservation ev, UnitViewSet? unitViews)
    {
        UnitCatalog? catalog = unitViews?.Catalog ?? UnitViewSet.GetDefaultCatalog();
        if (catalog != null && ev.PresentationId < catalog.Units.Length)
        {
            if (string.Equals(catalog.Units[ev.PresentationId].Classification, "infantry", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        UnitCatalog cwCatalog = LazyColdWarCatalog.Value;
        if (ev.PresentationId < cwCatalog.Units.Length)
        {
            if (string.Equals(cwCatalog.Units[ev.PresentationId].Classification, "infantry", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    internal void SpawnDirectShot(Vector3 origin, Vector3 target)
    {
        Vector3 shotDir = (target - origin).LengthSquared() > 0.0001f
            ? (target - origin).Normalized()
            : Vector3.Forward;

        float distance = origin.DistanceTo(target);
        float flightDuration = Mathf.Clamp(distance / TracerSpeed, MinTracerDuration, MaxTracerDuration);

        SpawnMuzzleFlash(origin, shotDir);
        SpawnSmokePuff(origin, isImpactSmoke: false);
        SpawnGunParticles(origin, shotDir);
        SpawnTracer(origin, target, flightDuration);
        QueuePendingImpact(target, shotDir, HitResult.Penetrated, flightDuration);
        PlaySound(_cannonVariants[_presentationRng.Next(CannonVariantCount)], origin, SoundPriority.Cannon);
    }

    // Ground dust from the muzzle blast and gun gas drifting from the barrel. Presentation only.
    private void SpawnGunParticles(Vector3 muzzle, Vector3 shotDir, float ageSeconds = 0f)
    {
        Vector3 flat = new(shotDir.X, 0f, shotDir.Z);
        flat = flat.LengthSquared() > 0.0001f ? flat.Normalized() : Vector3.Forward;
        Basis facing = Basis.LookingAt(flat, Vector3.Up);

        float ground = _map.GetHeight(muzzle.X, muzzle.Z);
        if (_geometry is not null && _geometry.Bridges.Count > 0)
        {
            System.Numerics.Vector2 muzzle2D = new(muzzle.X, muzzle.Z);
            for (int b = 0; b < _geometry.Bridges.Count; b++)
            {
                BridgeStructure bridge = _geometry.Bridges[b];
                if (bridge.Contains(muzzle2D))
                {
                    float bridgeH = bridge.GetHeight(muzzle2D);
                    if (MathF.Abs(muzzle.Y - bridgeH) < BlastDustMaxMuzzleHeight)
                    {
                        ground = bridgeH;
                        break;
                    }
                }
            }
        }
        float strength = Mathf.Clamp(1f - (muzzle.Y - ground) / BlastDustMaxMuzzleHeight, 0f, 1f);
        if (strength > 0.05f)
        {
            GpuParticles3D dust = _blastDust[_nextBlastDust];
            _nextBlastDust = (_nextBlastDust + 1) % MaxBlastDust;
            dust.Transform = new Transform3D(facing, new Vector3(muzzle.X, ground + 1.0f, muzzle.Z) - flat * 0.6f);
            dust.AmountRatio = 0.35f + 0.65f * strength;
            dust.Preprocess = ageSeconds;
            dust.Restart();
        }

        GpuParticles3D smoke = _barrelSmoke[_nextBarrelSmoke];
        _nextBarrelSmoke = (_nextBarrelSmoke + 1) % MaxBarrelSmoke;
        smoke.Transform = new Transform3D(facing, muzzle);
        smoke.Preprocess = ageSeconds;
        smoke.Restart();
    }

    private void TriggerImpactFeedback(Vector3 position, float elapsedOffset = 0f)
    {
        if (elapsedOffset >= SmokePuffLifetime)
        {
            return;
        }

        SpawnImpact(position, elapsedOffset);
        SpawnSparks(position, elapsedOffset);
        SpawnImpactSparkBurst(position, Vector3.Up, elapsedOffset);
        SpawnFlashLight(position + new Vector3(0f, 0.8f, 0f), energy: 4.5f, range: 18f, lifetime: 0.14f);
        SpawnSmokePuff(position, isImpactSmoke: true, elapsedOffset);
        if (elapsedOffset < 0.1f)
        {
            PlaySound(_tookFireAudio, position, SoundPriority.TookFire);
        }
    }

    internal void TriggerGroundImpactFeedback(Vector3 position, float elapsedOffset = 0f)
    {
        if (elapsedOffset >= SmokePuffLifetime)
        {
            return;
        }

        SpawnSmokePuff(position, isImpactSmoke: true, elapsedOffset);
        if (elapsedOffset < 0.12f)
        {
            // Kick up a compact soil clod & dust geyser for missed direct-fire rounds hitting earth
            int burstIdx = _nextArtilleryBurst;
            _nextArtilleryBurst = (_nextArtilleryBurst + 1) % MaxArtilleryBursts;
            GpuParticles3D debris = _artilleryDebris[burstIdx];
            debris.Position = position + new Vector3(0f, 0.2f, 0f);
            debris.Scale = Vector3.One * 0.42f;
            debris.AmountRatio = 0.28f;
            debris.Preprocess = elapsedOffset;
            debris.Restart();

            GpuParticles3D dust = _blastDust[_nextBlastDust];
            _nextBlastDust = (_nextBlastDust + 1) % MaxBlastDust;
            dust.Transform = new Transform3D(Basis.Identity, position + new Vector3(0f, 0.4f, 0f));
            dust.AmountRatio = 0.42f;
            dust.Preprocess = elapsedOffset;
            dust.Restart();
        }
        if (elapsedOffset < 0.1f)
        {
            PlaySound(_groundImpactAudio, position, SoundPriority.TookFire);
        }
    }

    internal void TriggerRicochetFeedback(Vector3 position, Vector3 incomingDir, float elapsedOffset = 0f, bool isMachineGun = false)
    {
        if (elapsedOffset >= SparksLifetime)
        {
            return;
        }

        SpawnSparks(position, elapsedOffset);
        if (elapsedOffset < 0.08f)
        {
            SpawnImpactSparkBurst(position, Vector3.Up, elapsedOffset);
        }

        // Reflect bounce off hit surface: keep most forward momentum, add upward bounce and random lateral deflection
        Vector3 flatIncoming = new Vector3(incomingDir.X, 0f, incomingDir.Z);
        Vector3 lateral = flatIncoming.LengthSquared() > 0.0001f
            ? new Vector3(-flatIncoming.Z, 0f, flatIncoming.X).Normalized()
            : Vector3.Right;

        float lateralOffset = (float)(_presentationRng.NextDouble() * 2.0 - 1.0) * 0.45f;
        Vector3 bounceDir = (incomingDir * 0.65f + Vector3.Up * 0.45f + lateral * lateralOffset).Normalized();
        Vector3 ricochetTarget = position + bounceDir * (isMachineGun ? 10.0f : 16.0f);
        SpawnTracer(position, ricochetTarget, 0.12f, isMachineGun: isMachineGun, elapsedOffset: elapsedOffset);
    }

    private bool HasSeenEvent(uint id)
    {
        for (int i = 0; i < _recentEventIds.Length; i++)
        {
            if (_recentEventIds[i] == id) return true;
        }
        return false;
    }

    private void RecordSeenEvent(uint id)
    {
        _recentEventIds[_recentEventIndex] = id;
        _recentEventIndex = (_recentEventIndex + 1) % _recentEventIds.Length;
    }

    private void SpawnMuzzleFlash(Vector3 position, Vector3 direction, bool isMachineGun = false, bool isRifle = false, float elapsedOffset = 0f)
    {
        float remainingLifetime = MuzzleFlashLifetime - elapsedOffset;
        if (remainingLifetime <= 0f) return;

        int idx = AcquireMeshSlot(_muzzleFlashes, MaxMuzzleFlashes);
        ref TimedMesh tm = ref _muzzleFlashes[idx];
        tm.Active = true;
        tm.Lifetime = remainingLifetime;
        tm.MaxLifetime = MuzzleFlashLifetime;
        tm.Velocity = Vector3.Zero;
        tm.ScaleDown = true;
        tm.ScaleUp = false;
        tm.FadeAlpha = false;
        tm.InitialScaleVector = isRifle
            ? new Vector3(0.08f, 0.08f, 0.16f)
            : (isMachineGun ? new Vector3(0.22f, 0.22f, 0.45f) : new Vector3(0.5f, 0.5f, 1.1f));
        tm.Mesh.Position = position;
        float factor = MathF.Max(0.01f, remainingLifetime / MuzzleFlashLifetime);
        tm.Mesh.Scale = tm.InitialScaleVector * factor;
        if (direction != Vector3.Zero)
        {
            tm.Mesh.LookAt(position + direction, Vector3.Up);
        }
        tm.Mesh.Transparency = 0f;
        tm.Mesh.Visible = true;
    }

    private void SpawnSmokePuff(Vector3 position, bool isImpactSmoke = false, float elapsedOffset = 0f)
    {
        float remainingLifetime = SmokePuffLifetime - elapsedOffset;
        if (remainingLifetime <= 0f) return;

        int idx = AcquireMeshSlot(_smokePuffs, MaxSmokePuffs);
        ref TimedMesh tm = ref _smokePuffs[idx];
        tm.Active = true;
        tm.Lifetime = remainingLifetime;
        tm.MaxLifetime = SmokePuffLifetime;
        tm.Velocity = isImpactSmoke ? new Vector3(0, 1.2f, 0) : new Vector3(0, 0.7f, 0);
        tm.ScaleDown = false;
        tm.ScaleUp = true;
        tm.FadeAlpha = true;
        tm.InitialScale = isImpactSmoke ? 0.65f : 0.55f;
        tm.TargetScale = isImpactSmoke ? 2.2f : 1.7f;
        tm.InitialScaleVector = Vector3.Zero;
        tm.Mesh.Position = position + tm.Velocity * elapsedOffset;
        tm.Mesh.Scale = Vector3.One * tm.InitialScale;
        tm.Mesh.Transparency = 0f;
        tm.Mesh.Visible = true;
    }

    private static void CalculateTracerSegment(
        Vector3 origin, Vector3 target, Vector3 direction, float totalDistance,
        float streakLength, float alpha,
        out Vector3 mid, out float segLen, out Vector3 front)
    {
        front = origin + direction * (alpha * totalDistance);
        Vector3 back = front - direction * streakLength;

        if ((back - origin).Dot(direction) < 0f)
        {
            back = origin;
        }
        if ((front - target).Dot(direction) > 0f)
        {
            front = target;
        }

        Vector3 segment = front - back;
        segLen = segment.Length();
        mid = (front + back) * 0.5f;
    }

    private void SpawnTracer(Vector3 origin, Vector3 target, float flightDuration, bool isMachineGun = false, bool isRifle = false, float elapsedOffset = 0f)
    {
        float distance = origin.DistanceTo(target);
        if (distance < 0.1f) return;
        if (elapsedOffset >= flightDuration) return;

        int idx = AcquireTracerSlot();
        ref TimedTracer tracer = ref _tracers[idx];
        tracer.Active = true;
        tracer.Origin = origin;
        tracer.Target = target;
        tracer.TotalDistance = distance;
        tracer.Direction = (target - origin) / distance;
        tracer.TotalDuration = flightDuration;
        tracer.ElapsedTime = elapsedOffset;
        tracer.Thickness = isRifle ? TracerThickness * 0.35f : (isMachineGun ? TracerThickness * 0.5f : TracerThickness);
        tracer.StreakLength = isRifle ? Mathf.Min(0.9f, distance * 0.8f) : (isMachineGun ? Mathf.Min(1.5f, distance * 0.8f) : Mathf.Min(TracerLength, distance * 0.8f));

        float alpha = elapsedOffset / flightDuration;
        CalculateTracerSegment(
            origin, target, tracer.Direction, distance, tracer.StreakLength, alpha,
            out Vector3 mid, out float segLen, out Vector3 front);

        tracer.Mesh.Position = mid;
        tracer.Mesh.Scale = new Vector3(tracer.Thickness, tracer.Thickness, Mathf.Max(0.1f, segLen));
        tracer.Mesh.LookAt(front + tracer.Direction * 5f, Vector3.Up);
        tracer.Mesh.Visible = true;
    }

    private void QueuePendingImpact(Vector3 target, Vector3 incomingDirection, HitResult result, float delay, bool isMachineGun = false, bool isRifle = false)
    {
        int idx = AcquirePendingImpactSlot();
        ref PendingImpact pi = ref _pendingImpacts[idx];
        pi.Active = true;
        pi.Target = target;
        pi.IncomingDirection = incomingDirection;
        pi.Result = result;
        pi.IsMachineGun = isMachineGun || isRifle;
        pi.DelayRemaining = delay;
    }

    private void CancelPendingImpactsNear(Vector3 position, float radius)
    {
        float rSq = radius * radius;
        for (int i = 0; i < MaxPendingImpacts; i++)
        {
            if (_pendingImpacts[i].Active && _pendingImpacts[i].Target.DistanceSquaredTo(position) <= rSq)
            {
                _pendingImpacts[i].Active = false;
            }
        }
    }

    private void SpawnSparks(Vector3 position, float elapsedOffset = 0f)
    {
        float remainingLifetime = SparksLifetime - elapsedOffset;
        if (remainingLifetime <= 0f) return;

        int idx = AcquireMeshSlot(_sparks, MaxSparks);
        ref TimedMesh tm = ref _sparks[idx];
        tm.Active = true;
        tm.Lifetime = remainingLifetime;
        tm.MaxLifetime = SparksLifetime;
        tm.Velocity = new Vector3(0, 2.0f, 0);
        tm.ScaleDown = true;
        tm.ScaleUp = false;
        tm.FadeAlpha = false;
        tm.InitialScale = 1.0f;
        tm.InitialScaleVector = Vector3.Zero;
        tm.Mesh.Position = position + tm.Velocity * elapsedOffset;
        tm.Mesh.Scale = Vector3.One;
        tm.Mesh.Transparency = 0f;
        tm.Mesh.Visible = true;
    }

    private void SpawnImpact(Vector3 position, float elapsedOffset = 0f)
    {
        float remainingLifetime = ImpactLifetime - elapsedOffset;
        if (remainingLifetime <= 0f) return;

        int idx = AcquireMeshSlot(_impacts, MaxImpacts);
        ref TimedMesh tm = ref _impacts[idx];
        tm.Active = true;
        tm.Lifetime = remainingLifetime;
        tm.MaxLifetime = ImpactLifetime;
        tm.Velocity = Vector3.Zero;
        tm.ScaleDown = true;
        tm.ScaleUp = false;
        tm.FadeAlpha = false;
        tm.InitialScale = 1.0f;
        tm.InitialScaleVector = Vector3.Zero;
        tm.Mesh.Position = position;
        tm.Mesh.Scale = Vector3.One * 1.0f;
        tm.Mesh.Transparency = 0f;
        tm.Mesh.Visible = true;
    }

    private void SpawnExplosion(Vector3 position, float elapsedOffset = 0f)
    {
        float remainingLifetime = ExplosionLifetime - elapsedOffset;
        if (remainingLifetime <= 0f) return;

        int idx = AcquireMeshSlot(_explosions, MaxExplosions);
        ref TimedMesh tm = ref _explosions[idx];
        tm.Active = true;
        tm.Lifetime = remainingLifetime;
        tm.MaxLifetime = ExplosionLifetime;
        tm.Velocity = new Vector3(0, 0.9f, 0);
        tm.ScaleDown = false;
        tm.ScaleUp = true;
        tm.FadeAlpha = true;
        tm.InitialScale = 0.55f;
        tm.TargetScale = 2.15f;
        tm.InitialScaleVector = Vector3.Zero;
        tm.Mesh.Position = position + tm.Velocity * elapsedOffset;
        float t = elapsedOffset / ExplosionLifetime;
        float factor = Mathf.Lerp(tm.InitialScale, tm.TargetScale, t);
        tm.Mesh.Scale = Vector3.One * factor;
        tm.Mesh.Transparency = t;
        tm.Mesh.Visible = true;
    }

    private void SpawnResidue(Vector3 position, float elapsedOffset = 0f, float scaleMultiplier = 1.0f)
    {
        float remainingLifetime = ResidueLifetime - elapsedOffset;
        if (remainingLifetime <= 0f) return;

        int idx = AcquireMeshSlot(_residues, MaxResidues);
        ref TimedMesh tm = ref _residues[idx];
        tm.Active = true;
        tm.Lifetime = remainingLifetime;
        tm.MaxLifetime = ResidueLifetime;
        tm.Velocity = Vector3.Zero;
        tm.ScaleDown = false;
        tm.ScaleUp = false;
        tm.FadeAlpha = false;
        tm.InitialScaleVector = Vector3.Zero;

        float groundY = position.Y + 0.03f;
        tm.Mesh.Position = new Vector3(position.X, groundY, position.Z);
        tm.Mesh.Rotation = new Vector3(-Mathf.Pi * 0.5f, (float)(_presentationRng.NextDouble() * Mathf.Tau), 0f);
        tm.Mesh.Scale = new Vector3(scaleMultiplier, scaleMultiplier, 1f);
        if (remainingLifetime < ResidueFadeStart)
        {
            float fadeRatio = remainingLifetime / ResidueFadeStart;
            tm.Mesh.Transparency = 1f - fadeRatio;
        }
        else
        {
            tm.Mesh.Transparency = 0f;
        }
        tm.Mesh.Visible = true;
    }

    private void SpawnArtilleryCrater(Vector3 position, float elapsedOffset = 0f, float baseDiameter = 8.8f)
    {
        float remainingLifetime = ArtilleryCraterLifetime - elapsedOffset;
        if (remainingLifetime <= 0f) return;

        int idx = AcquireArtilleryCraterSlot();
        ref TimedDecal crater = ref _artilleryCraters[idx];
        crater.Active = true;
        crater.Lifetime = remainingLifetime;

        float groundY = _map.GetHeight(position.X, position.Z);
        float jitter = 0.86f + (float)_presentationRng.NextDouble() * 0.30f;
        float diameter = baseDiameter * jitter;

        crater.Node.Position = new Vector3(position.X, groundY, position.Z);
        crater.Node.Rotation = new Vector3(0f, (float)(_presentationRng.NextDouble() * Mathf.Tau), 0f);
        crater.Node.Size = new Vector3(diameter, 2.4f, diameter);
        if (remainingLifetime < ArtilleryCraterFadeStart)
        {
            float alpha = Math.Clamp(remainingLifetime / ArtilleryCraterFadeStart, 0f, 1f);
            crater.Node.Modulate = new Color(1f, 1f, 1f, alpha);
        }
        else
        {
            crater.Node.Modulate = Colors.White;
        }
        crater.Node.Visible = true;
    }

    private int AcquireArtilleryCraterSlot()
    {
        for (int i = 0; i < MaxArtilleryCraters; i++)
        {
            if (!_artilleryCraters[i].Active) return i;
        }

        int oldestIdx = 0;
        float minLifetime = float.MaxValue;
        for (int i = 0; i < MaxArtilleryCraters; i++)
        {
            if (_artilleryCraters[i].Lifetime < minLifetime)
            {
                minLifetime = _artilleryCraters[i].Lifetime;
                oldestIdx = i;
            }
        }
        return oldestIdx;
    }

    private void SpawnRocketTrailSmoke(
        Vector3 position,
        float initialScale = 0.55f,
        float targetScale = 2.6f,
        float lifetime = RocketTrailSmokeLifetime,
        float elapsedOffset = 0f)
    {
        float remainingLifetime = lifetime - elapsedOffset;
        if (remainingLifetime <= 0f) return;

        int idx = AcquireMeshSlot(_smokePuffs, MaxSmokePuffs);
        ref TimedMesh tm = ref _smokePuffs[idx];
        tm.Active = true;
        tm.Lifetime = remainingLifetime;
        tm.MaxLifetime = lifetime;
        tm.Velocity = new Vector3(0f, 0.85f, 0f);
        tm.ScaleDown = false;
        tm.ScaleUp = true;
        tm.FadeAlpha = true;
        tm.InitialScale = initialScale;
        tm.TargetScale = targetScale;
        tm.InitialScaleVector = Vector3.Zero;
        float t = elapsedOffset / lifetime;
        tm.Mesh.Position = position + tm.Velocity * elapsedOffset;
        tm.Mesh.Scale = Vector3.One * Mathf.Lerp(initialScale, targetScale, t);
        tm.Mesh.Transparency = Mathf.Clamp(t, 0f, 1f);
        tm.Mesh.Visible = true;
    }

    private void SpawnArtilleryFireball(Vector3 position, float elapsedOffset = 0f)
    {
        float remainingLifetime = ArtilleryFireballLifetime - elapsedOffset;
        if (remainingLifetime <= 0f) return;

        int idx = AcquireMeshSlot(_explosions, MaxExplosions);
        ref TimedMesh tm = ref _explosions[idx];
        tm.Active = true;
        tm.Lifetime = remainingLifetime;
        tm.MaxLifetime = ArtilleryFireballLifetime;
        tm.Velocity = new Vector3(0f, 1.3f, 0f);
        tm.ScaleDown = false;
        tm.ScaleUp = true;
        tm.FadeAlpha = true;
        tm.InitialScale = 0.36f;
        tm.TargetScale = 1.65f;
        tm.InitialScaleVector = Vector3.Zero;
        float t = elapsedOffset / ArtilleryFireballLifetime;
        tm.Mesh.Position = position + new Vector3(0f, 0.55f, 0f) + tm.Velocity * elapsedOffset;
        tm.Mesh.Scale = Vector3.One * Mathf.Lerp(tm.InitialScale, tm.TargetScale, t);
        tm.Mesh.Transparency = Mathf.Clamp(t, 0f, 1f);
        tm.Mesh.Visible = true;
    }

    private void SpawnShockwave(Vector3 position, float elapsedOffset = 0f)
    {
        float remainingLifetime = ShockwaveLifetime - elapsedOffset;
        if (remainingLifetime <= 0f) return;

        int idx = AcquireMeshSlot(_shockwaves, MaxShockwaves);
        ref TimedMesh tm = ref _shockwaves[idx];
        tm.Active = true;
        tm.Lifetime = remainingLifetime;
        tm.MaxLifetime = ShockwaveLifetime;
        tm.Velocity = Vector3.Zero;
        tm.ScaleDown = false;
        tm.ScaleUp = true;
        tm.FadeAlpha = true;
        tm.InitialScale = 1.8f;
        tm.TargetScale = 24.0f;
        tm.InitialScaleVector = Vector3.Zero;
        float t = elapsedOffset / ShockwaveLifetime;
        float radius = Mathf.Lerp(tm.InitialScale, tm.TargetScale, t);
        tm.Mesh.Position = new Vector3(position.X, position.Y + 0.35f, position.Z);
        tm.Mesh.Scale = new Vector3(radius, radius, 1f);
        tm.Mesh.Transparency = Mathf.Clamp(t, 0f, 1f);
        tm.Mesh.Visible = true;
    }

    private void SpawnRocketBackblastParticles(Vector3 backblastPos, Basis tubeBasis, float ageSeconds)
    {
        int idx = _nextRocketBackblast;
        _nextRocketBackblast = (_nextRocketBackblast + 1) % MaxRocketBackblasts;

        // Fire rear exhaust along the true 3D elevated tube axis (+Z angles backward and DOWN into the dirt)
        GpuParticles3D blast = _rocketBackblasts[idx];
        blast.Transform = new Transform3D(tubeBasis, backblastPos);
        blast.Preprocess = ageSeconds;
        blast.Restart();

        // Project the downward exhaust jet onto the terrain behind the truck and bloom a lingering ground dust/smoke bank
        Vector3 rearDir = tubeBasis.Z.Normalized();
        float groundAtBreech = _map.GetHeight(backblastPos.X, backblastPos.Z);
        float dropDist = rearDir.Y < -0.15f
            ? Math.Clamp((backblastPos.Y - groundAtBreech) / (-rearDir.Y), 2.0f, 11.0f)
            : 5.0f;
        Vector3 hitEst = backblastPos + rearDir * dropDist;
        float groundY = _map.GetHeight(hitEst.X, hitEst.Z);
        Vector3 groundHit = new(hitEst.X, groundY + 0.35f, hitEst.Z);

        GpuParticles3D bank = _rocketLaunchGroundBanks[idx];
        bank.Position = groundHit;
        bank.Preprocess = ageSeconds;
        bank.Restart();
    }

    private void SpawnArtilleryBurstParticles(Vector3 position, float ageSeconds, float scale)
    {
        int idx = _nextArtilleryBurst;
        _nextArtilleryBurst = (_nextArtilleryBurst + 1) % MaxArtilleryBursts;

        Vector3 burstPos = position + new Vector3(0f, 0.45f, 0f);
        Vector3 s = Vector3.One * scale;

        GpuParticles3D fireball = _artilleryFireballs[idx];
        fireball.Position = burstPos;
        fireball.Scale = s;
        fireball.Preprocess = ageSeconds;
        fireball.Restart();

        GpuParticles3D sparks = _artillerySparks[idx];
        sparks.Position = burstPos;
        sparks.Scale = s;
        sparks.Preprocess = ageSeconds;
        sparks.Restart();

        GpuParticles3D debris = _artilleryDebris[idx];
        debris.Position = burstPos;
        debris.Scale = s;
        debris.AmountRatio = 1.0f;
        debris.Preprocess = ageSeconds;
        debris.Restart();

        GpuParticles3D ejectaJets = _artilleryEjectaJets[idx];
        ejectaJets.Position = burstPos;
        ejectaJets.Scale = s;
        ejectaJets.Preprocess = ageSeconds;
        ejectaJets.Restart();

        GpuParticles3D groundRing = _artilleryGroundDustRings[idx];
        groundRing.Position = position + new Vector3(0f, 0.25f, 0f);
        groundRing.Scale = s;
        groundRing.Preprocess = ageSeconds;
        groundRing.Restart();

        GpuParticles3D dirt = _artilleryDirtPlumes[idx];
        dirt.Position = burstPos;
        dirt.Scale = s;
        dirt.Preprocess = ageSeconds;
        dirt.Restart();
    }

    private void SpawnImpactSparkBurst(Vector3 position, Vector3 normalDir, float ageSeconds)
    {
        GpuParticles3D burst = _impactSparkBursts[_nextImpactSparkBurst];
        _nextImpactSparkBurst = (_nextImpactSparkBurst + 1) % MaxImpactSparkBursts;
        burst.Position = position;
        burst.Preprocess = ageSeconds;
        burst.Restart();
    }

    private void SpawnFlashLight(Vector3 position, float energy, float range, float lifetime)
    {
        int selected = -1;
        float minRemaining = float.MaxValue;
        for (int i = 0; i < MaxFlashLights; i++)
        {
            if (!_flashLights[i].Active)
            {
                selected = i;
                break;
            }
            if (_flashLights[i].Lifetime < minRemaining)
            {
                minRemaining = _flashLights[i].Lifetime;
                selected = i;
            }
        }
        if (selected < 0) return;

        ref TimedLight tl = ref _flashLights[selected];
        tl.Active = true;
        tl.Lifetime = lifetime;
        tl.MaxLifetime = lifetime;
        tl.InitialEnergy = energy;
        tl.Light.Position = position;
        tl.Light.OmniRange = range;
        tl.Light.LightEnergy = energy;
        tl.Light.Visible = true;
    }

    private void SpawnArtilleryPlume(Vector3 position, float elapsedOffset = 0f)
    {
        float remainingLifetime = ArtilleryPlumeLifetime - elapsedOffset;
        if (remainingLifetime <= 0f) return;

        int idx = AcquireMeshSlot(_artilleryPlumes, MaxArtilleryPlumes);
        ref TimedMesh tm = ref _artilleryPlumes[idx];
        tm.Active = true;
        tm.Lifetime = remainingLifetime;
        tm.MaxLifetime = ArtilleryPlumeLifetime;
        tm.Velocity = new Vector3(0f, 1.1f, 0f);
        tm.ScaleDown = false;
        tm.ScaleUp = true;
        tm.FadeAlpha = true;
        tm.InitialScale = 0.85f;
        tm.TargetScale = 2.6f;
        tm.InitialScaleVector = Vector3.Zero;
        float t = elapsedOffset / ArtilleryPlumeLifetime;
        tm.Mesh.Position = position + new Vector3(0f, 0.8f, 0f) + tm.Velocity * elapsedOffset;
        tm.Mesh.Scale = Vector3.One * Mathf.Lerp(tm.InitialScale, tm.TargetScale, t);
        tm.Mesh.Transparency = Mathf.Clamp(t, 0f, 1f);
        tm.Mesh.Visible = true;
    }

    private void SpawnRocket(
        System.Numerics.Vector3 origin,
        System.Numerics.Vector3 target,
        float apexHeight,
        float flightDuration,
        float elapsedOffset = 0f)
    {
        if (elapsedOffset >= flightDuration) return;

        int idx = AcquireRocketSlot();
        ref TimedRocket rocket = ref _rockets[idx];
        rocket.Active = true;
        rocket.Origin = origin;
        rocket.Target = target;
        rocket.ApexHeight = apexHeight;
        rocket.TotalDuration = flightDuration;
        rocket.ElapsedTime = elapsedOffset;
        rocket.SmokeAccumulator = 0f;
        rocket.IncomingSoundPlayed = (flightDuration - elapsedOffset) < 0.45f;

        PositionRocket(ref rocket);
        rocket.BodyMesh.Visible = true;
        rocket.Root.Visible = true;
        rocket.Contrail.AmountRatio = 1.0f;
        rocket.Contrail.Emitting = true;
    }

    private static void PositionRocket(ref TimedRocket rocket)
    {
        float t = Mathf.Clamp(rocket.ElapsedTime / rocket.TotalDuration, 0f, 1f);
        ArtilleryBallistics.SamplePositionAndTangent(
            rocket.Origin,
            rocket.Target,
            rocket.ApexHeight,
            t,
            out System.Numerics.Vector3 pos,
            out System.Numerics.Vector3 tangent);

        Vector3 gPos = new(pos.X, pos.Y, pos.Z);
        Vector3 gDir = new(tangent.X, tangent.Y, tangent.Z);
        rocket.Root.Position = gPos;
        if (gDir.LengthSquared() > 1e-6f)
        {
            Vector3 up = MathF.Abs(gDir.Y) > 0.96f ? Vector3.Right : Vector3.Up;
            rocket.Root.LookAt(gPos + gDir, up);
        }

        // Authentic 9M22U two-phase flight: bright ~2.1s solid-fuel boost, rapid sputter burnout, then dark ballistic coast
        const float burnEnd = 0.26f;
        const float taperEnd = 0.32f;
        bool motorBurning = t < taperEnd;
        rocket.FlameMesh.Visible = motorBurning;
        rocket.GlowMesh.Visible = motorBurning;
        if (motorBurning)
        {
            float burnFade = t <= burnEnd
                ? 1.0f
                : Math.Clamp(1.0f - (t - burnEnd) / (taperEnd - burnEnd), 0f, 1f);
            float flicker = (0.86f + 0.28f * MathF.Sin(rocket.ElapsedTime * 68f)) * burnFade;
            rocket.FlameMesh.Scale = new Vector3(0.70f + 0.30f * burnFade, MathF.Max(0.05f, flicker), 0.70f + 0.30f * burnFade);
            rocket.GlowMesh.Scale = Vector3.One * MathF.Max(0.05f, (0.90f + 0.22f * flicker) * burnFade);
        }
    }

    private int AcquireRocketSlot()
    {
        for (int i = 0; i < MaxRockets; i++)
        {
            if (!_rockets[i].Active) return i;
        }

        _droppedMeshEffectsCount++;
        int oldestIdx = 0;
        float maxElapsed = -1f;
        for (int i = 0; i < MaxRockets; i++)
        {
            if (_rockets[i].ElapsedTime > maxElapsed)
            {
                maxElapsed = _rockets[i].ElapsedTime;
                oldestIdx = i;
            }
        }
        return oldestIdx;
    }

    private int AcquireMeshSlot(TimedMesh[] pool, int max)
    {
        for (int i = 0; i < max; i++)
        {
            if (!pool[i].Active) return i;
        }

        _droppedMeshEffectsCount++;
        int oldestIdx = 0;
        float minLifetime = float.MaxValue;
        for (int i = 0; i < max; i++)
        {
            if (pool[i].Lifetime < minLifetime)
            {
                minLifetime = pool[i].Lifetime;
                oldestIdx = i;
            }
        }
        return oldestIdx;
    }

    private int AcquireTracerSlot()
    {
        for (int i = 0; i < MaxTracers; i++)
        {
            if (!_tracers[i].Active) return i;
        }

        _droppedMeshEffectsCount++;
        int oldestIdx = 0;
        float maxElapsed = -1f;
        for (int i = 0; i < MaxTracers; i++)
        {
            if (_tracers[i].ElapsedTime > maxElapsed)
            {
                maxElapsed = _tracers[i].ElapsedTime;
                oldestIdx = i;
            }
        }
        return oldestIdx;
    }

    private int AcquirePendingImpactSlot()
    {
        for (int i = 0; i < MaxPendingImpacts; i++)
        {
            if (!_pendingImpacts[i].Active) return i;
        }

        _droppedMeshEffectsCount++;
        int oldestIdx = 0;
        float minDelay = float.MaxValue;
        for (int i = 0; i < MaxPendingImpacts; i++)
        {
            if (_pendingImpacts[i].DelayRemaining < minDelay)
            {
                minDelay = _pendingImpacts[i].DelayRemaining;
                oldestIdx = i;
            }
        }
        return oldestIdx;
    }

    public void Clear()
    {
        System.Array.Clear(_recentEventIds, 0, _recentEventIds.Length);
        _recentEventIndex = 0;

        for (int i = 0; i < MaxTracers; i++)
        {
            _tracers[i].Active = false;
            _tracers[i].Mesh.Visible = false;
        }
        for (int i = 0; i < MaxRockets; i++)
        {
            _rockets[i].Active = false;
            _rockets[i].Contrail.Emitting = false;
            _rockets[i].Root.Visible = false;
        }
        for (int i = 0; i < MaxFlashLights; i++)
        {
            _flashLights[i].Active = false;
            _flashLights[i].Light.Visible = false;
            _flashLights[i].Light.LightEnergy = 0f;
        }
        for (int i = 0; i < MaxPendingImpacts; i++)
        {
            _pendingImpacts[i].Active = false;
        }

        ResetPool(_muzzleFlashes);
        ResetPool(_smokePuffs);
        ResetPool(_sparks);
        ResetPool(_impacts);
        ResetPool(_explosions);
        ResetPool(_shockwaves);
        ResetPool(_artilleryPlumes);
        ResetPool(_residues);
        for (int i = 0; i < MaxArtilleryCraters; i++)
        {
            _artilleryCraters[i].Active = false;
            _artilleryCraters[i].Node.Visible = false;
            _artilleryCraters[i].Node.Modulate = Colors.White;
        }
        foreach (GpuParticles3D dust in _blastDust)
            dust.Emitting = false;
        foreach (GpuParticles3D smoke in _barrelSmoke)
            smoke.Emitting = false;
        foreach (GpuParticles3D blast in _rocketBackblasts)
            blast.Emitting = false;
        foreach (GpuParticles3D bank in _rocketLaunchGroundBanks)
            bank.Emitting = false;
        foreach (GpuParticles3D fireball in _artilleryFireballs)
            fireball.Emitting = false;
        foreach (GpuParticles3D sparks in _artillerySparks)
            sparks.Emitting = false;
        foreach (GpuParticles3D debris in _artilleryDebris)
            debris.Emitting = false;
        foreach (GpuParticles3D ejectaJets in _artilleryEjectaJets)
            ejectaJets.Emitting = false;
        foreach (GpuParticles3D groundRing in _artilleryGroundDustRings)
            groundRing.Emitting = false;
        foreach (GpuParticles3D dirt in _artilleryDirtPlumes)
            dirt.Emitting = false;
        foreach (GpuParticles3D impSparks in _impactSparkBursts)
            impSparks.Emitting = false;

        for (int i = 0; i < MaxAudioPlayers; i++)
        {
            if (_audioPlayers[i].Playing)
            {
                _audioPlayers[i].Stop();
            }
            _audioPlayers[i].Stream = null;
            _activeVoices[i] = default;
        }
    }

    public void ResetCounters()
    {
        _droppedMeshEffectsCount = 0;
        _droppedAudioVoicesCount = 0;
        _peakEffectsCount = CalculateActiveEffectsCount();
        _totalAudioRequests = 0;
    }

    private static void ResetPool(TimedMesh[] pool)
    {
        for (int i = 0; i < pool.Length; i++)
        {
            pool[i].Active = false;
            pool[i].Mesh.Visible = false;
            pool[i].InitialScaleVector = Vector3.Zero;
            pool[i].Velocity = Vector3.Zero;
            pool[i].Mesh.Transparency = 0f;
        }
    }

    public void Update(double delta)
    {
        float dt = (float)delta;

        UpdateTracers(dt);
        UpdateRockets(dt);
        UpdateMeshPool(_muzzleFlashes, dt);
        UpdateMeshPool(_smokePuffs, dt);
        UpdateMeshPool(_sparks, dt);
        UpdateMeshPool(_impacts, dt);
        UpdateMeshPool(_explosions, dt);
        UpdateShockwavePool(dt);
        UpdateMeshPool(_artilleryPlumes, dt);
        UpdateResiduePool(dt);
        UpdateArtilleryCraterPool(dt);
        UpdateFlashLights(dt);

        // Update pending impacts after existing mesh pools so newly spawned effects are not aged in the same frame
        UpdatePendingImpacts(dt);

        int currentActive = CalculateActiveEffectsCount();
        if (currentActive > _peakEffectsCount)
        {
            _peakEffectsCount = currentActive;
        }
    }

    private void UpdateTracers(float dt)
    {
        for (int i = 0; i < MaxTracers; i++)
        {
            ref TimedTracer tracer = ref _tracers[i];
            if (!tracer.Active) continue;

            tracer.ElapsedTime += dt;
            if (tracer.ElapsedTime >= tracer.TotalDuration)
            {
                tracer.Active = false;
                tracer.Mesh.Visible = false;
                continue;
            }

            float alpha = tracer.ElapsedTime / tracer.TotalDuration;
            CalculateTracerSegment(
                tracer.Origin, tracer.Target, tracer.Direction, tracer.TotalDistance,
                tracer.StreakLength, alpha,
                out Vector3 mid, out float segLen, out Vector3 front);

            tracer.Mesh.Position = mid;
            tracer.Mesh.Scale = new Vector3(tracer.Thickness, tracer.Thickness, Mathf.Max(0.1f, segLen));
            if (segLen > 0.001f)
            {
                tracer.Mesh.LookAt(front + tracer.Direction * 5f, Vector3.Up);
            }
        }
    }

    private void UpdatePendingImpacts(float dt)
    {
        for (int i = 0; i < MaxPendingImpacts; i++)
        {
            ref PendingImpact pi = ref _pendingImpacts[i];
            if (!pi.Active) continue;

            pi.DelayRemaining -= dt;
            if (pi.DelayRemaining <= 0f)
            {
                float overshoot = -pi.DelayRemaining;
                pi.Active = false;
                if (pi.Result == HitResult.Deflected)
                {
                    TriggerRicochetFeedback(pi.Target, pi.IncomingDirection, overshoot, isMachineGun: pi.IsMachineGun);
                }
                else if (pi.Result == HitResult.Miss)
                {
                    TriggerGroundImpactFeedback(pi.Target, overshoot);
                }
                else
                {
                    TriggerImpactFeedback(pi.Target, overshoot);
                }
            }
        }
    }

    private static void UpdateMeshPool(TimedMesh[] pool, float dt)
    {
        for (int i = 0; i < pool.Length; i++)
        {
            ref TimedMesh tm = ref pool[i];
            if (!tm.Active) continue;

            tm.Lifetime -= dt;
            if (tm.Lifetime <= 0f)
            {
                tm.Active = false;
                tm.Mesh.Visible = false;
                tm.InitialScaleVector = Vector3.Zero;
                continue;
            }

            if (tm.Velocity != Vector3.Zero)
            {
                tm.Mesh.Position += tm.Velocity * dt;
            }

            float t = 1f - (tm.Lifetime / tm.MaxLifetime);

            if (tm.ScaleDown)
            {
                float factor = MathF.Max(0.01f, 1f - t);
                if (tm.InitialScaleVector != Vector3.Zero)
                {
                    tm.Mesh.Scale = tm.InitialScaleVector * factor;
                }
                else
                {
                    tm.Mesh.Scale = Vector3.One * (tm.InitialScale * factor);
                }
            }
            else if (tm.ScaleUp)
            {
                float factor = Mathf.Lerp(tm.InitialScale, tm.TargetScale, t);
                tm.Mesh.Scale = Vector3.One * factor;
            }

            if (tm.FadeAlpha)
            {
                tm.Mesh.Transparency = t;
            }
        }
    }

    private void UpdateResiduePool(float dt)
    {
        for (int i = 0; i < MaxResidues; i++)
        {
            ref TimedMesh residue = ref _residues[i];
            if (!residue.Active) continue;

            residue.Lifetime -= dt;
            if (residue.Lifetime <= 0f)
            {
                residue.Active = false;
                residue.Mesh.Visible = false;
                continue;
            }

            if (residue.Lifetime < ResidueFadeStart)
            {
                float fadeRatio = residue.Lifetime / ResidueFadeStart;
                residue.Mesh.Transparency = 1f - fadeRatio;
            }
            else
            {
                residue.Mesh.Transparency = 0f;
            }
        }
    }

    private void UpdateArtilleryCraterPool(float dt)
    {
        for (int i = 0; i < MaxArtilleryCraters; i++)
        {
            ref TimedDecal crater = ref _artilleryCraters[i];
            if (!crater.Active) continue;

            crater.Lifetime -= dt;
            if (crater.Lifetime <= 0f)
            {
                crater.Active = false;
                crater.Node.Visible = false;
                crater.Node.Modulate = Colors.White;
                continue;
            }

            if (crater.Lifetime < ArtilleryCraterFadeStart)
            {
                float alpha = Math.Clamp(crater.Lifetime / ArtilleryCraterFadeStart, 0f, 1f);
                crater.Node.Modulate = new Color(1f, 1f, 1f, alpha);
            }
        }
    }

    private static int CountActiveDecalPool(TimedDecal[] pool)
    {
        int count = 0;
        for (int i = 0; i < pool.Length; i++)
        {
            if (pool[i].Active) count++;
        }
        return count;
    }

    private int CountActiveTracers()
    {
        int count = 0;
        for (int i = 0; i < MaxTracers; i++)
        {
            if (_tracers[i].Active) count++;
        }
        return count;
    }

    private static int CountActiveMeshPool(TimedMesh[] pool)
    {
        int count = 0;
        for (int i = 0; i < pool.Length; i++)
        {
            if (pool[i].Active) count++;
        }
        return count;
    }

    private int CountActivePendingImpacts()
    {
        int count = 0;
        for (int i = 0; i < MaxPendingImpacts; i++)
        {
            if (_pendingImpacts[i].Active) count++;
        }
        return count;
    }

    private int CalculateActiveEffectsCount()
    {
        return CountActiveTracers() +
               CountActiveRockets() +
               CountActiveMeshPool(_muzzleFlashes) +
               CountActiveMeshPool(_smokePuffs) +
               CountActiveMeshPool(_sparks) +
               CountActiveMeshPool(_impacts) +
               CountActiveMeshPool(_explosions) +
               CountActiveMeshPool(_shockwaves) +
               CountActiveMeshPool(_artilleryPlumes) +
               CountActiveMeshPool(_residues) +
               CountActiveDecalPool(_artilleryCraters) +
               CountActivePendingImpacts();
    }

    private void UpdateRockets(float dt)
    {
        for (int i = 0; i < MaxRockets; i++)
        {
            ref TimedRocket rocket = ref _rockets[i];
            if (!rocket.Active) continue;

            rocket.ElapsedTime += dt;
            if (rocket.ElapsedTime >= rocket.TotalDuration)
            {
                rocket.Active = false;
                rocket.Contrail.Emitting = false;
                rocket.BodyMesh.Visible = false;
                rocket.FlameMesh.Visible = false;
                rocket.GlowMesh.Visible = false;
                continue;
            }

            PositionRocket(ref rocket);

            float remaining = rocket.TotalDuration - rocket.ElapsedTime;
            if (!rocket.IncomingSoundPlayed && remaining <= CannonSound.RocketIncomingDuration && remaining > 0.40f)
            {
                rocket.IncomingSoundPlayed = true;
                Vector3 targetPos = new(rocket.Target.X, rocket.Target.Y, rocket.Target.Z);
                if (_audioListener.Position.DistanceSquaredTo(targetPos) <= 680f * 680f)
                {
                    PlaySound(
                        _rocketIncomingVariants[_presentationRng.Next(RocketIncomingVariantCount)],
                        targetPos,
                        SoundPriority.TookFire,
                        maxDistance: 720f,
                        unitSize: 44f);
                }
            }

            float t = rocket.ElapsedTime / rocket.TotalDuration;
            if (t < 0.28f)
            {
                rocket.Contrail.AmountRatio = 1.0f;
                rocket.Contrail.Emitting = true;
            }
            else if (t < 0.34f)
            {
                // Brief thinning smolder tail immediately after propellant exhaustion
                float wisp = 1.0f - (t - 0.28f) / 0.06f;
                rocket.Contrail.AmountRatio = Math.Clamp(0.18f + 0.82f * wisp, 0.15f, 1.0f);
                rocket.Contrail.Emitting = true;
            }
            else
            {
                rocket.Contrail.Emitting = false;
            }
        }
    }

    private void UpdateShockwavePool(float dt)
    {
        for (int i = 0; i < MaxShockwaves; i++)
        {
            ref TimedMesh tm = ref _shockwaves[i];
            if (!tm.Active) continue;

            tm.Lifetime -= dt;
            if (tm.Lifetime <= 0f)
            {
                tm.Active = false;
                tm.Mesh.Visible = false;
                continue;
            }

            float t = 1f - (tm.Lifetime / tm.MaxLifetime);
            // Rapid initial blast wave expansion with smooth deceleration
            float eased = 1f - (1f - t) * (1f - t);
            float radius = Mathf.Lerp(tm.InitialScale, tm.TargetScale, eased);
            tm.Mesh.Scale = new Vector3(radius, radius, 1f);
            tm.Mesh.Transparency = Mathf.Clamp(t * t, 0f, 1f);
        }
    }

    private void UpdateFlashLights(float dt)
    {
        for (int i = 0; i < MaxFlashLights; i++)
        {
            ref TimedLight tl = ref _flashLights[i];
            if (!tl.Active) continue;

            tl.Lifetime -= dt;
            if (tl.Lifetime <= 0f)
            {
                tl.Active = false;
                tl.Light.Visible = false;
                tl.Light.LightEnergy = 0f;
                continue;
            }

            float ratio = tl.Lifetime / tl.MaxLifetime;
            tl.Light.LightEnergy = tl.InitialEnergy * ratio * ratio;
        }
    }

    private int CountActiveRockets()
    {
        int count = 0;
        for (int i = 0; i < MaxRockets; i++)
        {
            if (_rockets[i].Active) count++;
        }
        return count;
    }
}
