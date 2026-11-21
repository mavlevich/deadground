using DeadGround.Engine;
using DeadGround.Engine.Net;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;

namespace DeadGround.Client;

/// <summary>
/// Allocation-free 3D tactical smoke screen renderer using a single pooled MultiMeshInstance3D.
/// Chains consecutive smoke clouds along a vehicle's path into continuous ribbon segments whose
/// bloom, density, wind shear, and dissipation interpolate smoothly from predecessor to successor,
/// combined with a 5-puff continuous rear-exhaust plume anchored to actively emitting units.
/// Evaluates at native display FPS using fractional renderTick and frame delta (no 10 Hz stepping).
/// Reads only client-permitted records from ClientReplica.
/// Zero shadow casting, zero shader instance uniform slots, 1 draw call for all active clouds.
/// </summary>
internal sealed partial class SmokeView : Node3D
{
    public const int PuffsPerCloud = 12;
    private const int ExhaustPuffsPerEmitter = 5;
    private const int MaxExhaustPuffs = 40;
    public const int MaxPuffs = WorldState.MaxSmokeClouds * PuffsPerCloud + MaxExhaustPuffs;

    private const int ShadowLayerCount = 1;
    private const int VeilLayerCount = 2;
    private const int CoreLayerCount = 7;
    private const int BillowTexSize = 256;

    // Consistent subtle battlefield breeze direction (normalized XZ)
    private const float WindDirX = 0.84f;
    private const float WindDirZ = 0.54f;

    private static readonly StringName NoiseTexParam = "noise_tex";
    private static readonly StringName SunDirParam = "sun_dir";
    private static readonly StringName SunLitColorParam = "sun_lit_color";
    private static readonly StringName AmbientShadowColorParam = "ambient_shadow_color";
    private static readonly StringName GroundShadowColorParam = "ground_shadow_color";
    private static readonly StringName HazeTintColorParam = "haze_tint_color";
    private static readonly StringName CastShadowColorParam = "cast_shadow_color";

    private static ImageTexture? _sharedBillowTexture;

    private const string SmokeShaderCode = @"
shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_disabled, unshaded, skip_vertex_transform;

uniform sampler2D depth_texture : hint_depth_texture, repeat_disable, filter_nearest;
uniform sampler2D noise_tex : repeat_enable, filter_linear_mipmap;
uniform vec3 sun_dir = vec3(0.42, 0.74, 0.52);
uniform vec3 sun_lit_color : source_color = vec3(0.71, 0.73, 0.71);
uniform vec3 ambient_shadow_color : source_color = vec3(0.44, 0.48, 0.50);
uniform vec3 ground_shadow_color : source_color = vec3(0.31, 0.34, 0.34);
uniform vec3 haze_tint_color : source_color = vec3(0.55, 0.61, 0.65);
uniform vec3 cast_shadow_color : source_color = vec3(0.08, 0.11, 0.10);

varying vec4 v_custom;
varying vec4 v_rot_erode;
varying vec4 v_uv_offsets;
varying vec3 v_cam_right;
varying vec3 v_cam_up;
varying vec3 v_cam_back;
varying vec3 v_sun_dir;
varying float v_top_down;

void vertex() {
    vec3 world_center = MODEL_MATRIX[3].xyz;
    float puff_width = length(MODEL_MATRIX[0].xyz);
    float puff_height = length(MODEL_MATRIX[1].xyz);

    vec3 cam_right = normalize(INV_VIEW_MATRIX[0].xyz);
    vec3 cam_up = normalize(INV_VIEW_MATRIX[1].xyz);
    vec3 cam_back = normalize(INV_VIEW_MATRIX[2].xyz);
    float top_down = abs(cam_back.y);

    float seed = INSTANCE_CUSTOM.x;
    float base_rot = INSTANCE_CUSTOM.y;
    float layer = INSTANCE_CUSTOM.z;
    float erosion = INSTANCE_CUSTOM.w;

    vec3 world_pos;
    if (layer < -0.5) {
        float shadow_h = mix(puff_height, puff_width * 0.92, smoothstep(0.08, 0.68, top_down));
        world_pos = world_center
            + cam_right * (VERTEX.x * puff_width)
            + cam_up * (VERTEX.y * shadow_h);
    } else {
        float screen_h = mix(puff_height, puff_width * 0.96, smoothstep(0.22, 0.86, top_down));
        float sway = sin(TIME * 0.42 + seed * 6.2831853) * 0.34;
        float lift = cos(TIME * 0.31 + seed * 4.7123889) * 0.18;
        world_pos = world_center
            + cam_right * (VERTEX.x * puff_width + sway)
            + cam_up * (VERTEX.y * screen_h + lift);
    }

    vec4 view_pos = VIEW_MATRIX * vec4(world_pos, 1.0);
    float z_bias = layer < -0.5 ? -0.45 : (layer * 0.55);
    view_pos.z += z_bias;
    VERTEX = view_pos.xyz;

    // Precompute per-puff trig, UV scroll offsets, and erosion bounds once per vertex
    float spin_dir = seed < 0.5 ? 1.0 : -1.0;
    float angle = base_rot + TIME * (0.045 + 0.030 * fract(seed * 13.7)) * spin_dir;
    float erode_lo = mix(0.54, 0.10, erosion);
    float erode_hi = mix(0.92, 0.56, erosion);

    v_custom = INSTANCE_CUSTOM;
    v_rot_erode = vec4(cos(angle), sin(angle), erode_lo, erode_hi);
    v_uv_offsets = vec4(
        seed * 4.31 - TIME * 0.016,
        fract(seed * 9.17) * 5.13 - TIME * 0.012,
        fract(seed * 7.73) * 6.1 + TIME * 0.024 * spin_dir,
        seed * 3.19 - TIME * 0.018);
    v_cam_right = cam_right;
    v_cam_up = cam_up;
    v_cam_back = cam_back;
    v_sun_dir = normalize(sun_dir);
    v_top_down = top_down;
}

void fragment() {
    vec2 p = vec2(UV.x * 2.0 - 1.0, (1.0 - UV.y) * 2.0 - 1.0);
    float r_sq = dot(p, p);
    if (r_sq >= 0.985) {
        discard;
    }
    float r = sqrt(r_sq);

    float layer = v_custom.z;
    float erosion = v_custom.w;
    float cs = v_rot_erode.x;
    float sn = v_rot_erode.y;

    vec2 p_rot = vec2(p.x * cs - p.y * sn, p.x * sn + p.y * cs);
    vec2 uv_rot = p_rot * 0.5 + 0.5;

    vec4 s1 = texture(noise_tex, uv_rot * 0.64 + v_uv_offsets.xy);
    vec2 warp = (s1.ba * 2.0 - 1.0) * 0.085;
    vec4 s2 = texture(noise_tex, uv_rot * 1.18 + warp + v_uv_offsets.zw);

    float macro_billow = s1.r;
    float silky_wisp = s2.g;

    if (layer < -0.5) {
        float fluid_field = macro_billow * 0.72 + silky_wisp * 0.28;
        float shadow_radial = smoothstep(0.99, 0.08, r);
        float shadow_mask = smoothstep(0.14, 0.82, shadow_radial * (0.62 + 0.58 * fluid_field)) * erosion;
        if (shadow_mask <= 0.005) {
            discard;
        }

        float scene_depth_raw = texture(depth_texture, SCREEN_UV).r;
        vec3 scene_ndc = vec3(SCREEN_UV * 2.0 - 1.0, scene_depth_raw);
        vec4 scene_view = INV_PROJECTION_MATRIX * vec4(scene_ndc, 1.0);
        float safe_w = max(abs(scene_view.w), 0.0001) * (scene_view.w < 0.0 ? -1.0 : 1.0);
        scene_view.xyz /= safe_w;
        float shadow_soft = smoothstep(-0.3, 0.9, VERTEX.z - scene_view.z);

        ALBEDO = cast_shadow_color;
        ALPHA = clamp(COLOR.a * shadow_mask * shadow_soft, 0.0, 0.36);
    } else if (layer < 0.14) {
        float veil_radial = smoothstep(0.99, 0.05, r);
        float veil_shape = veil_radial * (0.48 + 0.72 * silky_wisp);
        float veil_mask = smoothstep(mix(0.45, 0.08, erosion), mix(0.88, 0.62, erosion), veil_shape);
        if (veil_mask <= 0.005) {
            discard;
        }

        float scene_depth_raw = texture(depth_texture, SCREEN_UV).r;
        vec3 scene_ndc = vec3(SCREEN_UV * 2.0 - 1.0, scene_depth_raw);
        vec4 scene_view = INV_PROJECTION_MATRIX * vec4(scene_ndc, 1.0);
        float safe_w = max(abs(scene_view.w), 0.0001) * (scene_view.w < 0.0 ? -1.0 : 1.0);
        scene_view.xyz /= safe_w;
        float veil_soft = smoothstep(0.0, 1.8, VERTEX.z - scene_view.z);

        vec3 veil_col = mix(ambient_shadow_color, haze_tint_color, 0.45);
        veil_col = mix(veil_col, sun_lit_color, 0.28);
        ALBEDO = veil_col * COLOR.rgb;
        ALPHA = clamp(COLOR.a * veil_mask * veil_soft, 0.0, 0.42);
    } else {
        float radial_core = smoothstep(0.98, 0.10, r);
        float edge_zone = smoothstep(0.32, 0.96, r);
        float fluid_cut = mix(0.76, 0.16, edge_zone) + macro_billow * 0.54 + (silky_wisp - 0.48) * 0.48 * edge_zone;
        float cloud_mask = smoothstep(v_rot_erode.z, v_rot_erode.w, radial_core * fluid_cut);
        if (cloud_mask <= 0.005) {
            discard;
        }

        vec2 slope_local = (s1.ba * 2.0 - 1.0) * 0.85 + (s2.ba * 2.0 - 1.0) * 0.22;
        vec2 slope_screen = vec2(slope_local.x * cs + slope_local.y * sn, -slope_local.x * sn + slope_local.y * cs);

        vec2 dome = p * 0.68 + slope_screen * 0.56;
        float dome_len_sq = dot(dome, dome);
        if (dome_len_sq > 0.88) {
            dome *= inversesqrt(dome_len_sq) * 0.938;
            dome_len_sq = 0.88;
        }
        float dome_z = sqrt(max(0.10, 1.0 - dome_len_sq));
        vec3 world_n = normalize(
            v_cam_right * dome.x +
            v_cam_up * dome.y +
            v_cam_back * dome_z +
            vec3(0.0, 0.24, 0.0));

        float ndotl = dot(world_n, v_sun_dir);
        float sun_wrap = smoothstep(-0.48, 0.82, ndotl);
        float broad_fold = smoothstep(0.18, 0.85, macro_billow);

        float vertical_grad = mix(p.y * 0.20, 0.05, v_top_down);
        float height_ao = clamp(0.28 + layer * 0.46 + vertical_grad + (broad_fold - 0.46) * 0.30, 0.12, 1.0);

        vec3 shadow_col = mix(ground_shadow_color, ambient_shadow_color, height_ao);
        vec3 lit_col = mix(shadow_col, sun_lit_color, sun_wrap * (0.28 + 0.72 * height_ao));

        float edge_thin = (1.0 - cloud_mask) * (0.45 + 0.55 * edge_zone);
        vec3 edge_air = mix(haze_tint_color, sun_lit_color, sun_wrap);
        ALBEDO = mix(lit_col, edge_air, edge_thin * 0.32) * COLOR.rgb;

        float scene_depth_raw = texture(depth_texture, SCREEN_UV).r;
        vec3 scene_ndc = vec3(SCREEN_UV * 2.0 - 1.0, scene_depth_raw);
        vec4 scene_view = INV_PROJECTION_MATRIX * vec4(scene_ndc, 1.0);
        float safe_w = max(abs(scene_view.w), 0.0001) * (scene_view.w < 0.0 ? -1.0 : 1.0);
        scene_view.xyz /= safe_w;
        float soft_fade = smoothstep(0.0, 1.6, VERTEX.z - scene_view.z);

        ALPHA = clamp(COLOR.a * cloud_mask * soft_fade, 0.0, 0.88);
    }
}
";

    private readonly HeightMap _map;
    private readonly StaticGeometry? _geometry;
    private readonly MultiMesh _multiMesh;
    private readonly MultiMeshInstance3D _instance;
    private readonly Shader _shader;
    private readonly ShaderMaterial _material;
    private readonly QuadMesh _puffMesh;
    private Vector3 _sunDir = new Vector3(0.42f, 0.74f, 0.52f).Normalized();
    private float _shadowProjectionScale = 0.52f;
    private float _shadowAlphaScale = 0.32f;
    private float _visualTimeSec;

    public int ActiveCloudCount { get; private set; }
    public int ActivePuffCount => _multiMesh.VisibleInstanceCount;
    internal MultiMeshInstance3D MultiMeshInstance => _instance;

    public SmokeView(HeightMap map, StaticGeometry? geometry = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        _map = map;
        _geometry = geometry;
        Name = "SmokeView";

        _shader = new Shader { Code = SmokeShaderCode };
        _material = new ShaderMaterial { Shader = _shader };
        _material.SetShaderParameter(NoiseTexParam, GetOrCreateBillowTexture());
        ApplyDefaultPalette();

        _puffMesh = new QuadMesh
        {
            Size = Vector2.One,
            Material = _material
        };

        _multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            UseCustomData = true,
            Mesh = _puffMesh,
            InstanceCount = MaxPuffs,
            VisibleInstanceCount = 0
        };

        _instance = new MultiMeshInstance3D
        {
            Name = "TacticalSmokeMultiMesh",
            Multimesh = _multiMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 45.0f
        };
        AddChild(_instance);
    }

    /// <summary>
    /// Harmonizes tactical diesel smoke lighting, shadow projection, and atmospheric haze tint
    /// with the active EnvironmentProfile. Called only on profile load or change (zero per-frame overhead).
    /// </summary>
    public void ApplyEnvironment(EnvironmentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        Basis sunBasis = Basis.FromEuler(new Vector3(
            Mathf.DegToRad(profile.Sun.Pitch),
            Mathf.DegToRad(profile.Sun.Yaw),
            0f));
        Vector3 sunDir = sunBasis.Z.Normalized();
        if (sunDir.Y < 0.15f)
        {
            sunDir = new Vector3(sunDir.X, 0.35f, sunDir.Z).Normalized();
        }
        _sunDir = sunDir;

        float horizLen = MathF.Sqrt(sunDir.X * sunDir.X + sunDir.Z * sunDir.Z);
        float cotElevation = horizLen / MathF.Max(0.35f, sunDir.Y);
        _shadowProjectionScale = Math.Clamp(cotElevation * 0.56f, 0.36f, 0.76f);
        _shadowAlphaScale = Math.Clamp(0.24f + profile.Sun.Energy * 0.08f, 0.26f, 0.36f);

        Color sunColor = EnvironmentProfile.TryParseHexColor(profile.Sun.ColorHex, out Color parsedSun)
            ? parsedSun
            : new Color(0.86f, 0.89f, 0.91f);
        Color skyHorizon = EnvironmentProfile.TryParseHexColor(profile.Sky.SkyHorizonColorHex, out Color parsedSky)
            ? parsedSky
            : new Color(0.54f, 0.60f, 0.64f);
        Color groundHorizon = EnvironmentProfile.TryParseHexColor(profile.Sky.GroundHorizonColorHex, out Color parsedGround)
            ? parsedGround
            : new Color(0.43f, 0.46f, 0.41f);
        Color ambientColor = EnvironmentProfile.TryParseHexColor(profile.Ambient.ColorHex, out Color parsedAmbient)
            ? parsedAmbient
            : new Color(0.46f, 0.52f, 0.58f);
        Color hazeColor = EnvironmentProfile.TryParseHexColor(profile.Haze.ColorHex, out Color parsedHaze)
            ? parsedHaze
            : new Color(0.55f, 0.61f, 0.65f);

        Color dieselAlbedo = new(0.73f, 0.73f, 0.69f);
        float energyBoost = Mathf.Clamp(0.86f + profile.Sun.Energy * 0.15f, 0.90f, 1.12f);

        Color sunLit = new Color(
            Mathf.Clamp(dieselAlbedo.R * sunColor.R * energyBoost, 0.60f, 0.82f),
            Mathf.Clamp(dieselAlbedo.G * sunColor.G * energyBoost, 0.60f, 0.80f),
            Mathf.Clamp(dieselAlbedo.B * sunColor.B * energyBoost, 0.58f, 0.76f))
            .Lerp(skyHorizon, 0.08f);

        Color midShadow = new Color(0.43f, 0.47f, 0.49f)
            .Lerp(ambientColor, 0.32f)
            .Lerp(skyHorizon, 0.14f);

        Color groundShadow = new Color(0.30f, 0.33f, 0.33f)
            .Lerp(groundHorizon * 0.72f, 0.28f)
            .Lerp(ambientColor * 0.70f, 0.18f);

        Color castShadow = new Color(0.08f, 0.11f, 0.10f)
            .Lerp(ambientColor * 0.22f, 0.25f);

        _material.SetShaderParameter(SunDirParam, _sunDir);
        _material.SetShaderParameter(SunLitColorParam, sunLit);
        _material.SetShaderParameter(AmbientShadowColorParam, midShadow);
        _material.SetShaderParameter(GroundShadowColorParam, groundShadow);
        _material.SetShaderParameter(HazeTintColorParam, hazeColor);
        _material.SetShaderParameter(CastShadowColorParam, castShadow);
    }

    private void ApplyDefaultPalette()
    {
        _sunDir = new Vector3(0.42f, 0.74f, 0.52f).Normalized();
        _shadowProjectionScale = 0.52f;
        _shadowAlphaScale = 0.31f;
        _material.SetShaderParameter(SunDirParam, _sunDir);
        _material.SetShaderParameter(SunLitColorParam, new Color(0.71f, 0.73f, 0.71f));
        _material.SetShaderParameter(AmbientShadowColorParam, new Color(0.44f, 0.48f, 0.50f));
        _material.SetShaderParameter(GroundShadowColorParam, new Color(0.31f, 0.34f, 0.34f));
        _material.SetShaderParameter(HazeTintColorParam, new Color(0.55f, 0.61f, 0.65f));
        _material.SetShaderParameter(CastShadowColorParam, new Color(0.08f, 0.11f, 0.10f));
    }

    public void Update(float delta, ClientReplica replica, double renderTick)
    {
        _visualTimeSec += Math.Clamp(delta, 0f, 0.05f);

        ReadOnlySpan<SmokeCloudObservation> clouds = replica.SmokeClouds;
        ReadOnlySpan<OwnUnitObservation> ownUnits = replica.OwnUnits;
        bool hasActiveEmitter = false;
        for (int u = 0; u < ownUnits.Length; u++)
        {
            if (ownUnits[u].SmokeActive)
            {
                hasActiveEmitter = true;
                break;
            }
        }

        if (clouds.Length == 0 && !hasActiveEmitter)
        {
            if (ActiveCloudCount != 0 || _multiMesh.VisibleInstanceCount != 0)
            {
                ActiveCloudCount = 0;
                _multiMesh.VisibleInstanceCount = 0;
            }
            return;
        }

        double smoothTick = renderTick > 0.0 ? renderTick : replica.LatestTick;
        int activeClouds = 0;
        int puffIndex = 0;

        float shadowDirX = -_sunDir.X;
        float shadowDirZ = -_sunDir.Z;
        float shadowLen = MathF.Sqrt(shadowDirX * shadowDirX + shadowDirZ * shadowDirZ);
        if (shadowLen > 0.001f)
        {
            shadowDirX /= shadowLen;
            shadowDirZ /= shadowLen;
        }

        const float windPerpX = -WindDirZ;
        const float windPerpZ = WindDirX;

        for (int i = 0; i < clouds.Length && puffIndex + PuffsPerCloud <= MaxPuffs; i++)
        {
            ref readonly SmokeCloudObservation cloud = ref clouds[i];
            EvaluateContinuous(in cloud, smoothTick, out float effectiveRadius, out float effectiveDensity, out float ageSec, out float remainingSec);
            if (effectiveRadius <= 0.05f || effectiveDensity <= 0.001f)
            {
                continue;
            }

            activeClouds++;

            // Find the immediate trail predecessor so consecutive clouds laid by a moving vehicle
            // render as one continuous ribbon segment (prev -> curr) rather than discrete clumps.
            int prevIdx = FindTrailPredecessor(clouds, i);
            bool hasPrev = false;
            float prevX = cloud.Position.X;
            float prevZ = cloud.Position.Y;
            float prevRadius = effectiveRadius;
            float prevHeight = cloud.Height;
            float prevDensityRatio = 0f;
            float prevFadeProgress = 0f;
            float prevErosionBase = 0f;
            float prevBreeze = 0f;
            float segPerpX = windPerpX;
            float segPerpZ = windPerpZ;

            if (prevIdx >= 0)
            {
                ref readonly SmokeCloudObservation prev = ref clouds[prevIdx];
                EvaluateContinuous(in prev, smoothTick, out float pRad, out float pDens, out float pAge, out float pRem);
                if (pRad > 0.05f && pDens > 0.001f)
                {
                    float sdx = cloud.Position.X - prev.Position.X;
                    float sdz = cloud.Position.Y - prev.Position.Y;
                    float sLen = MathF.Sqrt(sdx * sdx + sdz * sdz);
                    if (sLen > 1.5f)
                    {
                        hasPrev = true;
                        prevX = prev.Position.X;
                        prevZ = prev.Position.Y;
                        prevDensityRatio = Math.Clamp(pDens / MathF.Max(0.01f, prev.OpticalDensity), 0f, 1f);
                        float prevRadRatio = Math.Clamp(pRad / MathF.Max(0.1f, prev.Radius), 0f, 1f);
                        float prevFresh = Math.Clamp(1.0f - pAge / 4.5f, 0f, 1f);
                        prevRadius = pRad * (1.0f + 0.22f * prevFresh * (1.0f - prevRadRatio));
                        prevHeight = prev.Height * (0.56f + 0.44f * prevRadRatio);
                        prevFadeProgress = pRem < 15.0f ? 1.0f - pRem / 15.0f : 0f;
                        prevErosionBase = Math.Clamp(MathF.Pow(prevDensityRatio, 0.70f), 0f, 1f);
                        prevBreeze = MathF.Min(pAge * 0.18f, pRad * 0.24f) + prevFadeProgress * 3.0f;
                        segPerpX = -sdz / sLen;
                        segPerpZ = sdx / sLen;
                    }
                }
            }

            float densityRatio = Math.Clamp(effectiveDensity / MathF.Max(0.01f, cloud.OpticalDensity), 0f, 1f);
            float radiusRatio = Math.Clamp(effectiveRadius / MathF.Max(0.1f, cloud.Radius), 0f, 1f);
            float fadeProgress = remainingSec < 15.0f ? 1.0f - remainingSec / 15.0f : 0f;

            float breeze = MathF.Min(ageSec * 0.18f, effectiveRadius * 0.24f) + fadeProgress * 3.0f;
            float freshExhaust = Math.Clamp(1.0f - ageSec / 4.5f, 0f, 1f);
            float visualRadius = effectiveRadius * (1.0f + 0.22f * freshExhaust * (1.0f - radiusRatio));
            float coreHeight = cloud.Height * (0.56f + 0.44f * radiusRatio);
            float erosionBase = Math.Clamp(MathF.Pow(densityRatio, 0.70f), 0f, 1f);

            uint cloudHash = HashU32(cloud.Id.Value * 747796405u + 2891336453u);

            // Order: 1 ground shadow apron -> 2 atmospheric mist veils -> 7 silky core billows -> 2 sunlit wisps
            for (int p = 0; p < PuffsPerCloud; p++)
            {
                uint h0 = HashU32(cloudHash ^ (uint)(p * 2654435761u));
                uint h1 = HashU32(h0 * 1597334677u + 1013904223u);
                uint h2 = HashU32(h1 * 2246822519u + 3266489917u);

                float r0 = (h0 & 0xFFFFu) / 65535f;
                float r1 = ((h0 >> 16) & 0xFFFFu) / 65535f;
                float r2 = (h1 & 0xFFFFu) / 65535f;
                float r3 = ((h1 >> 16) & 0xFFFFu) / 65535f;
                float r4 = (h2 & 0xFFFFu) / 65535f;

                if (p < ShadowLayerCount)
                {
                    float tSeg = hasPrev ? 0.56f : 1.0f;
                    float anchorX = hasPrev ? prevX + (cloud.Position.X - prevX) * tSeg : cloud.Position.X;
                    float anchorZ = hasPrev ? prevZ + (cloud.Position.Y - prevZ) * tSeg : cloud.Position.Y;
                    float localRad = hasPrev ? prevRadius + (visualRadius - prevRadius) * tSeg : visualRadius;
                    float localH = hasPrev ? prevHeight + (coreHeight - prevHeight) * tSeg : coreHeight;
                    float localDens = hasPrev ? prevDensityRatio + (densityRatio - prevDensityRatio) * tSeg : densityRatio;
                    float localFade = hasPrev ? prevFadeProgress + (fadeProgress - prevFadeProgress) * tSeg : fadeProgress;
                    float localErode = hasPrev ? prevErosionBase + (erosionBase - prevErosionBase) * tSeg : erosionBase;
                    float localBreeze = hasPrev ? prevBreeze + (breeze - prevBreeze) * tSeg : breeze;

                    float throwDist = localRad * (_shadowProjectionScale * 0.88f);
                    float sx = anchorX + shadowDirX * throwDist + WindDirX * (localBreeze * 0.45f);
                    float sz = anchorZ + shadowDirZ * throwDist + WindDirZ * (localBreeze * 0.45f);
                    float sy = SampleSurfaceHeight(sx, sz, cloud.SupportSurface) + 0.85f;
                    float shadowWidth = localRad * 2.02f;
                    float shadowHeight = localH * 0.54f;
                    float shadowAlpha = Math.Clamp(_shadowAlphaScale * localDens * (1.0f - localFade * 0.72f), 0f, 0.36f);

                    _multiMesh.SetInstanceTransform(
                        puffIndex,
                        new Transform3D(Basis.Identity.Scaled(new Vector3(shadowWidth, shadowHeight, 1.0f)), new Vector3(sx, sy, sz)));
                    _multiMesh.SetInstanceColor(
                        puffIndex,
                        new Color(1f, 1f, 1f, shadowAlpha));
                    _multiMesh.SetInstanceCustomData(
                        puffIndex,
                        new Color(r4, r0 * MathF.Tau, -1.0f, localErode));
                    puffIndex++;
                    continue;
                }

                int bodyIdx = p - ShadowLayerCount;

                if (bodyIdx < VeilLayerCount)
                {
                    float tSeg = hasPrev ? (bodyIdx == 0 ? 0.34f : 0.82f) : 1.0f;
                    float anchorX = hasPrev ? prevX + (cloud.Position.X - prevX) * tSeg : cloud.Position.X;
                    float anchorZ = hasPrev ? prevZ + (cloud.Position.Y - prevZ) * tSeg : cloud.Position.Y;
                    float localRad = hasPrev ? prevRadius + (visualRadius - prevRadius) * tSeg : visualRadius;
                    float localH = hasPrev ? prevHeight + (coreHeight - prevHeight) * tSeg : coreHeight;
                    float localDens = hasPrev ? prevDensityRatio + (densityRatio - prevDensityRatio) * tSeg : densityRatio;
                    float localFade = hasPrev ? prevFadeProgress + (fadeProgress - prevFadeProgress) * tSeg : fadeProgress;
                    float localErode = hasPrev ? prevErosionBase + (erosionBase - prevErosionBase) * tSeg : erosionBase;
                    float localBreeze = hasPrev ? prevBreeze + (breeze - prevBreeze) * tSeg : breeze;

                    float alongWind = (bodyIdx == 0 ? 0.08f : 0.42f) * localRad;
                    float lateral = (bodyIdx == 0 ? -0.22f : 0.24f) * localRad + (r1 - 0.5f) * 0.22f * localRad;
                    float vx = anchorX + WindDirX * (alongWind + localBreeze * 0.95f) + segPerpX * lateral;
                    float vz = anchorZ + WindDirZ * (alongWind + localBreeze * 0.95f) + segPerpZ * lateral;
                    float vy = SampleSurfaceHeight(vx, vz, cloud.SupportSurface) + localH * (0.28f + 0.10f * r3) + (localFade * 2.8f) * 0.65f;
                    float veilWidth = localRad * (1.62f + 0.26f * r2);
                    float veilHeight = localH * (0.94f + 0.20f * r3);
                    float veilAlpha = Math.Clamp((0.28f + 0.07f * r4) * localDens * (1.0f - localFade * 0.45f), 0f, 0.36f);

                    _multiMesh.SetInstanceTransform(
                        puffIndex,
                        new Transform3D(Basis.Identity.Scaled(new Vector3(veilWidth, veilHeight, 1.0f)), new Vector3(vx, vy, vz)));
                    _multiMesh.SetInstanceColor(
                        puffIndex,
                        new Color(0.98f, 0.99f, 1.01f, veilAlpha));
                    _multiMesh.SetInstanceCustomData(
                        puffIndex,
                        new Color(r4, r0 * MathF.Tau, 0.06f, Math.Clamp(localErode - localFade * 0.15f, 0f, 1f)));
                    puffIndex++;
                    continue;
                }

                int denseIdx = bodyIdx - VeilLayerCount;
                float posX;
                float posZ;
                float puffWidth;
                float puffHeight;
                float heightFrac;
                float layerNorm;
                float alphaWeight;
                float erosionOffset;
                float valueTint;
                float puffDens;
                float puffFade;
                float puffErodeBase;
                float puffCoreH;

                if (denseIdx < CoreLayerCount)
                {
                    if (hasPrev)
                    {
                        // Distribute puffs continuously along the path segment prev -> curr so the
                        // smoke curtain extends as one fluid ribbon and dissipates smoothly along the track.
                        float tSeg = (denseIdx + 0.45f + (r0 - 0.5f) * 0.30f) / CoreLayerCount;
                        tSeg = Math.Clamp(tSeg, 0.08f, 0.98f);

                        float anchorX = prevX + (cloud.Position.X - prevX) * tSeg;
                        float anchorZ = prevZ + (cloud.Position.Y - prevZ) * tSeg;
                        float localRad = prevRadius + (visualRadius - prevRadius) * tSeg;
                        puffCoreH = prevHeight + (coreHeight - prevHeight) * tSeg;
                        puffDens = prevDensityRatio + (densityRatio - prevDensityRatio) * tSeg;
                        puffFade = prevFadeProgress + (fadeProgress - prevFadeProgress) * tSeg;
                        puffErodeBase = prevErosionBase + (erosionBase - prevErosionBase) * tSeg;
                        float localBreeze = prevBreeze + (breeze - prevBreeze) * tSeg;

                        float sideSign = (denseIdx & 1) == 0 ? 1f : -1f;
                        float sideDist = localRad * (0.08f + 0.34f * r1) * sideSign;
                        float swirl = MathF.Sin(ageSec * (0.14f + 0.08f * r4) + r0 * MathF.Tau) * (localRad * 0.06f);
                        posX = anchorX + segPerpX * (sideDist + swirl) + WindDirX * (localBreeze * (0.48f + 0.34f * r2));
                        posZ = anchorZ + segPerpZ * (sideDist + swirl) + WindDirZ * (localBreeze * (0.48f + 0.34f * r2));

                        bool isLower = (denseIdx % 3) == 0;
                        puffWidth = localRad * (isLower ? (1.26f + 0.22f * r2) : (1.04f + 0.26f * r2));
                        puffHeight = puffCoreH * (isLower ? (1.14f + 0.20f * r3) : (1.24f + 0.24f * r3));
                        heightFrac = isLower ? (0.34f + 0.12f * r3) : (0.48f + 0.22f * r3);
                        layerNorm = isLower ? (0.20f + 0.14f * r2) : (0.46f + 0.24f * r2);
                        alphaWeight = isLower ? 0.72f : 0.65f;
                        erosionOffset = isLower ? 0.08f : 0.02f;
                        valueTint = isLower ? (0.93f + 0.06f * r4) : (0.97f + 0.07f * r4);
                    }
                    else
                    {
                        // Standalone/origin cloud: wind-elongated organic billowing blanket (never a sphere)
                        float angle = (denseIdx * (MathF.Tau / CoreLayerCount)) + (r0 - 0.5f) * 0.90f + (cloudHash & 0x3FFu) * 0.0061f;
                        float radialFrac = denseIdx == 0 ? 0.10f * r1 : 0.24f + 0.36f * r1;
                        float slowSwirl = ageSec * (0.016f + 0.012f * r4) * (r0 < 0.5f ? 1f : -1f);
                        float rawX = MathF.Cos(angle + slowSwirl) * (visualRadius * radialFrac);
                        float rawZ = MathF.Sin(angle + slowSwirl) * (visualRadius * radialFrac);

                        float projWind = rawX * WindDirX + rawZ * WindDirZ;
                        float stretch = projWind > 0f ? 0.34f : -0.10f;
                        posX = cloud.Position.X + rawX + WindDirX * (projWind * stretch + breeze * (0.45f + 0.35f * r2));
                        posZ = cloud.Position.Y + rawZ + WindDirZ * (projWind * stretch + breeze * (0.45f + 0.35f * r2));

                        bool isLower = denseIdx < 3;
                        puffWidth = visualRadius * (isLower ? (1.24f + 0.24f * r2) : (1.00f + 0.28f * r2));
                        puffHeight = coreHeight * (isLower ? (1.14f + 0.20f * r3) : (1.24f + 0.24f * r3));
                        heightFrac = isLower ? (0.34f + 0.12f * r3) : (0.48f + 0.22f * r3);
                        layerNorm = isLower ? (0.20f + 0.14f * r2) : (0.46f + 0.24f * r2);
                        alphaWeight = isLower ? 0.74f : 0.66f;
                        erosionOffset = isLower ? 0.08f : 0.02f;
                        valueTint = isLower ? (0.93f + 0.06f * r4) : (0.97f + 0.07f * r4);
                        puffDens = densityRatio;
                        puffFade = fadeProgress;
                        puffErodeBase = erosionBase;
                        puffCoreH = coreHeight;
                    }
                }
                else
                {
                    int topCount = PuffsPerCloud - ShadowLayerCount - VeilLayerCount - CoreLayerCount;
                    int topIdx = denseIdx - CoreLayerCount;
                    float tSeg = hasPrev ? (topIdx == 0 ? 0.38f : 0.84f) : 1.0f;
                    float anchorX = hasPrev ? prevX + (cloud.Position.X - prevX) * tSeg : cloud.Position.X;
                    float anchorZ = hasPrev ? prevZ + (cloud.Position.Y - prevZ) * tSeg : cloud.Position.Y;
                    float localRad = hasPrev ? prevRadius + (visualRadius - prevRadius) * tSeg : visualRadius;
                    puffCoreH = hasPrev ? prevHeight + (coreHeight - prevHeight) * tSeg : coreHeight;
                    puffDens = hasPrev ? prevDensityRatio + (densityRatio - prevDensityRatio) * tSeg : densityRatio;
                    puffFade = hasPrev ? prevFadeProgress + (fadeProgress - prevFadeProgress) * tSeg : fadeProgress;
                    puffErodeBase = hasPrev ? prevErosionBase + (erosionBase - prevErosionBase) * tSeg : erosionBase;
                    float localBreeze = hasPrev ? prevBreeze + (breeze - prevBreeze) * tSeg : breeze;

                    float angle = (topIdx * (MathF.Tau / Math.Max(1, topCount))) + r0 * 1.6f + (cloudHash & 0xFFu) * 0.025f;
                    float radialFrac = 0.14f + 0.36f * r1;
                    float slowSwirl = ageSec * (0.018f + 0.012f * r4) * (r0 < 0.5f ? 1f : -1f);
                    posX = anchorX + MathF.Cos(angle + slowSwirl) * (localRad * radialFrac) + WindDirX * (localRad * 0.24f + localBreeze * 1.25f);
                    posZ = anchorZ + MathF.Sin(angle + slowSwirl) * (localRad * radialFrac) + WindDirZ * (localRad * 0.24f + localBreeze * 1.25f);

                    puffWidth = localRad * (0.96f + 0.30f * r2);
                    puffHeight = puffCoreH * (1.12f + 0.24f * r3);
                    heightFrac = 0.74f + 0.24f * r3;
                    layerNorm = 0.80f + 0.20f * r2;
                    alphaWeight = 0.50f;
                    erosionOffset = -0.14f;
                    valueTint = 1.00f + 0.06f * r4;
                }

                float puffBaseY = SampleSurfaceHeight(posX, posZ, cloud.SupportSurface);
                float posY = puffBaseY + puffCoreH * heightFrac + (puffFade * 2.8f) * (0.68f + 0.55f * layerNorm);

                float warmShift = freshExhaust * 0.03f;
                float baseTint = valueTint - freshExhaust * 0.04f;
                float puffAlpha = Math.Clamp(alphaWeight * (0.24f + 0.76f * puffDens) * (1.0f - puffFade * 0.35f), 0f, 0.84f);
                float puffErosion = Math.Clamp(puffErodeBase + erosionOffset - puffFade * 0.24f, 0f, 1f);

                _multiMesh.SetInstanceTransform(
                    puffIndex,
                    new Transform3D(Basis.Identity.Scaled(new Vector3(puffWidth, puffHeight, 1.0f)), new Vector3(posX, posY, posZ)));
                _multiMesh.SetInstanceColor(
                    puffIndex,
                    new Color(baseTint + warmShift, baseTint + warmShift * 0.4f, baseTint - warmShift * 0.3f, puffAlpha));
                _multiMesh.SetInstanceCustomData(
                    puffIndex,
                    new Color(r4, r0 * MathF.Tau, layerNorm, puffErosion));
                puffIndex++;
            }
        }

        // Continuous 5-puff rear-exhaust ribbon behind actively emitting friendly vehicles,
        // bridging the full 10m inter-cloud spacing so moving TDA smoke extends smoothly without gaps.
        for (int u = 0; u < ownUnits.Length && puffIndex + ExhaustPuffsPerEmitter <= MaxPuffs; u++)
        {
            ref readonly OwnUnitObservation unit = ref ownUnits[u];
            if (!unit.SmokeActive)
            {
                continue;
            }

            NumericsVector2 smoothPos = replica.SamplePosition(unit.Id, smoothTick);
            replica.SampleYaws(unit.Id, smoothTick, out _, out float smoothHullHeading, out _, out _, out _);

            float rearX = MathF.Sin(smoothHullHeading);
            float rearZ = MathF.Cos(smoothHullHeading);
            float perpX = -rearZ;
            float perpZ = rearX;
            uint unitHash = HashU32(unit.Id.Value * 2654435761u + 97u);

            for (int e = 0; e < ExhaustPuffsPerEmitter; e++)
            {
                float phase = ((_visualTimeSec * 0.72f) + e / (float)ExhaustPuffsPerEmitter) % 1.0f;
                float dist = 2.2f + phase * 11.2f;
                float sway = MathF.Sin(_visualTimeSec * 1.35f + e * 1.45f) * (0.32f + phase * 1.45f);
                float ex = smoothPos.X + rearX * dist + perpX * sway + WindDirX * (phase * 2.2f);
                float ez = smoothPos.Y + rearZ * dist + perpZ * sway + WindDirZ * (phase * 2.2f);
                float ey = SampleSurfaceHeight(ex, ez, unit.SupportSurface) + 1.15f + phase * 2.8f;

                float width = 3.2f + phase * 14.5f;
                float height = 2.5f + phase * 5.8f;
                float envelope = MathF.Sin(phase * MathF.PI);
                float alpha = Math.Clamp(0.68f * envelope, 0f, 0.72f);
                float seed = (HashU32(unitHash ^ (uint)(e * 374761393)) & 0xFFFFu) / 65535f;

                _multiMesh.SetInstanceTransform(
                    puffIndex,
                    new Transform3D(Basis.Identity.Scaled(new Vector3(width, height, 1.0f)), new Vector3(ex, ey, ez)));
                _multiMesh.SetInstanceColor(
                    puffIndex,
                    new Color(0.95f, 0.94f, 0.91f, alpha));
                _multiMesh.SetInstanceCustomData(
                    puffIndex,
                    new Color(seed, (e * 1.25f) + phase * 0.8f, 0.34f + phase * 0.36f, 0.86f - phase * 0.14f));
                puffIndex++;
            }
        }

        ActiveCloudCount = activeClouds;
        _multiMesh.VisibleInstanceCount = puffIndex;
    }

    private static int FindTrailPredecessor(ReadOnlySpan<SmokeCloudObservation> clouds, int index)
    {
        ref readonly SmokeCloudObservation curr = ref clouds[index];
        int bestIdx = -1;
        int bestTickDelta = 81;
        float bestDistSq = 28f * 28f;

        for (int j = 0; j < clouds.Length; j++)
        {
            if (j == index)
            {
                continue;
            }

            ref readonly SmokeCloudObservation other = ref clouds[j];
            int tickDelta = curr.CreatedTick - other.CreatedTick;
            if (tickDelta < 0 || tickDelta > 80)
            {
                continue;
            }
            if (tickDelta == 0 && other.Id.Value >= curr.Id.Value)
            {
                continue;
            }

            float dx = curr.Position.X - other.Position.X;
            float dz = curr.Position.Y - other.Position.Y;
            float dSq = dx * dx + dz * dz;
            if (dSq < 9f || dSq > 28f * 28f)
            {
                continue;
            }

            if (tickDelta < bestTickDelta || (tickDelta == bestTickDelta && dSq < bestDistSq))
            {
                bestTickDelta = tickDelta;
                bestDistSq = dSq;
                bestIdx = j;
            }
        }

        return bestIdx;
    }

    internal static void EvaluateContinuous(
        in SmokeCloudObservation cloud,
        double smoothTick,
        out float effectiveRadius,
        out float effectiveDensity,
        out float ageSec,
        out float remainingSec)
    {
        if (smoothTick >= cloud.ExpiresTick)
        {
            effectiveRadius = 0f;
            effectiveDensity = 0f;
            ageSec = 0f;
            remainingSec = 0f;
            return;
        }

        double ageTicks = Math.Max(0.0, smoothTick - cloud.CreatedTick);
        double remainingTicks = Math.Max(0.0, cloud.ExpiresTick - smoothTick);
        ageSec = (float)(ageTicks * Simulation.TickSeconds);
        remainingSec = (float)(remainingTicks * Simulation.TickSeconds);

        float radiusFactor = 1f;
        float densityFactor = 1f;

        if (cloud.BloomTicks > 0 && ageTicks < cloud.BloomTicks)
        {
            float linearT = (float)(ageTicks / cloud.BloomTicks);
            float inv = 1.0f - linearT;
            float easedT = 1.0f - inv * inv * inv;
            radiusFactor = SmokeCloudEvaluation.MinBloomRadiusFraction + (1f - SmokeCloudEvaluation.MinBloomRadiusFraction) * easedT;
            densityFactor = MathF.Max(0.05f, easedT);
        }

        if (cloud.FadeTicks > 0 && remainingTicks < cloud.FadeTicks)
        {
            float fadeT = (float)(remainingTicks / cloud.FadeTicks);
            float smoothFade = fadeT * fadeT * (3.0f - 2.0f * fadeT);
            if (smoothFade < densityFactor)
            {
                densityFactor = smoothFade;
            }
        }

        effectiveRadius = cloud.Radius * radiusFactor;
        effectiveDensity = cloud.OpticalDensity * densityFactor;
    }

    private float SampleSurfaceHeight(float x, float z, uint supportSurface)
    {
        return _geometry is not null
            ? _geometry.GetSurfaceHeight(new NumericsVector2(x, z), supportSurface)
            : _map.GetHeight(x, z);
    }

    public void Clear()
    {
        ActiveCloudCount = 0;
        _multiMesh.VisibleInstanceCount = 0;
    }

    public override void _ExitTree()
    {
        Clear();
        base._ExitTree();
    }

    private static ImageTexture GetOrCreateBillowTexture()
    {
        if (_sharedBillowTexture is not null)
        {
            return _sharedBillowTexture;
        }

        const int size = BillowTexSize;
        float[] billowField = new float[size * size];
        float[] wispField = new float[size * size];

        Vector2[] cells3 = BuildPeriodicCells(3, 101u);
        Vector2[] cells6 = BuildPeriodicCells(6, 211u);
        Vector2[] cells12 = BuildPeriodicCells(12, 307u);

        float[] val4 = BuildPeriodicValueGrid(4, 409u);
        float[] val8 = BuildPeriodicValueGrid(8, 521u);
        float[] val16 = BuildPeriodicValueGrid(16, 631u);
        float[] val32 = BuildPeriodicValueGrid(32, 743u);

        float minB = float.MaxValue;
        float maxB = float.MinValue;
        float minW = float.MaxValue;
        float maxW = float.MinValue;

        for (int y = 0; y < size; y++)
        {
            float v = (y + 0.5f) / size;
            int row = y * size;
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;

                float w3 = SmoothBillow(1.0f - SamplePeriodicWorley(u, v, 3, cells3));
                float w6 = SmoothBillow(1.0f - SamplePeriodicWorley(u, v, 6, cells6));
                float w12 = SmoothBillow(1.0f - SamplePeriodicWorley(u, v, 12, cells12));
                float v4 = SamplePeriodicValue(u, v, 4, val4);
                float v8 = SamplePeriodicValue(u, v, 8, val8);
                float v16 = SamplePeriodicValue(u, v, 16, val16);
                float v32 = SamplePeriodicValue(u, v, 32, val32);

                float billow = w3 * 0.48f + w6 * 0.30f + w12 * 0.10f + v4 * 0.12f;
                float wisp = v8 * 0.46f + v16 * 0.34f + v32 * 0.20f;

                billowField[row + x] = billow;
                wispField[row + x] = wisp;

                if (billow < minB) minB = billow;
                if (billow > maxB) maxB = billow;
                if (wisp < minW) minW = wisp;
                if (wisp > maxW) maxW = wisp;
            }
        }

        float invB = 1.0f / MathF.Max(0.0001f, maxB - minB);
        float invW = 1.0f / MathF.Max(0.0001f, maxW - minW);

        for (int i = 0; i < billowField.Length; i++)
        {
            billowField[i] = Math.Clamp((billowField[i] - minB) * invB, 0f, 1f);
            wispField[i] = Math.Clamp((wispField[i] - minW) * invW, 0f, 1f);
        }

        byte[] rgba = new byte[size * size * 4];
        const int stride = 5;
        for (int y = 0; y < size; y++)
        {
            int ym = (y - stride + size) % size;
            int yp = (y + stride) % size;
            int row = y * size;
            for (int x = 0; x < size; x++)
            {
                int xm = (x - stride + size) % size;
                int xp = (x + stride) % size;

                float b = billowField[row + x];
                float w = wispField[row + x];

                float dx = (billowField[row + xp] - billowField[row + xm]) * 1.9f;
                float dy = (billowField[ym * size + x] - billowField[yp * size + x]) * 1.9f;

                int dst = (row + x) * 4;
                rgba[dst] = (byte)Math.Clamp((int)(b * 255f + 0.5f), 0, 255);
                rgba[dst + 1] = (byte)Math.Clamp((int)(w * 255f + 0.5f), 0, 255);
                rgba[dst + 2] = (byte)Math.Clamp((int)((dx * 0.5f + 0.5f) * 255f + 0.5f), 0, 255);
                rgba[dst + 3] = (byte)Math.Clamp((int)((dy * 0.5f + 0.5f) * 255f + 0.5f), 0, 255);
            }
        }

        Image image = Image.CreateFromData(size, size, useMipmaps: false, Image.Format.Rgba8, rgba);
        image.GenerateMipmaps();
        _sharedBillowTexture = ImageTexture.CreateFromImage(image);
        return _sharedBillowTexture;
    }

    private static float SmoothBillow(float t)
    {
        float c = Math.Clamp(t, 0f, 1f);
        return c * c * (3.0f - 2.0f * c);
    }

    private static Vector2[] BuildPeriodicCells(int grid, uint seed)
    {
        Vector2[] cells = new Vector2[grid * grid];
        for (int y = 0; y < grid; y++)
        {
            for (int x = 0; x < grid; x++)
            {
                uint h = HashU32(seed ^ (uint)(x * 374761393 + y * 668265263));
                float fx = 0.14f + 0.72f * ((h & 0xFFFFu) / 65535f);
                float fy = 0.14f + 0.72f * (((h >> 16) & 0xFFFFu) / 65535f);
                cells[y * grid + x] = new Vector2(fx, fy);
            }
        }
        return cells;
    }

    private static float SamplePeriodicWorley(float u, float v, int grid, Vector2[] cells)
    {
        float gx = u * grid;
        float gy = v * grid;
        int ix = (int)MathF.Floor(gx);
        int iy = (int)MathF.Floor(gy);
        float fx = gx - ix;
        float fy = gy - iy;

        float minDistSq = 4.0f;
        for (int oy = -1; oy <= 1; oy++)
        {
            int cy = (iy + oy + grid) % grid;
            int row = cy * grid;
            for (int ox = -1; ox <= 1; ox++)
            {
                int cx = (ix + ox + grid) % grid;
                Vector2 pt = cells[row + cx];
                float dx = ox + pt.X - fx;
                float dy = oy + pt.Y - fy;
                float dSq = dx * dx + dy * dy;
                if (dSq < minDistSq)
                {
                    minDistSq = dSq;
                }
            }
        }

        return MathF.Min(1.0f, MathF.Sqrt(minDistSq));
    }

    private static float[] BuildPeriodicValueGrid(int grid, uint seed)
    {
        float[] values = new float[grid * grid];
        for (int y = 0; y < grid; y++)
        {
            for (int x = 0; x < grid; x++)
            {
                uint h = HashU32(seed ^ (uint)(x * 2246822519u + y * 3266489917u));
                values[y * grid + x] = (h & 0xFFFFu) / 65535f;
            }
        }
        return values;
    }

    private static float SamplePeriodicValue(float u, float v, int grid, float[] values)
    {
        float gx = u * grid;
        float gy = v * grid;
        int x0 = ((int)MathF.Floor(gx)) % grid;
        int y0 = ((int)MathF.Floor(gy)) % grid;
        int x1 = (x0 + 1) % grid;
        int y1 = (y0 + 1) % grid;

        float tx = gx - MathF.Floor(gx);
        float ty = gy - MathF.Floor(gy);
        tx = tx * tx * (3.0f - 2.0f * tx);
        ty = ty * ty * (3.0f - 2.0f * ty);

        float a = values[y0 * grid + x0];
        float b = values[y0 * grid + x1];
        float c = values[y1 * grid + x0];
        float d = values[y1 * grid + x1];

        float top = a + (b - a) * tx;
        float bottom = c + (d - c) * tx;
        return top + (bottom - top) * ty;
    }

    private static uint HashU32(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;
        return x;
    }
}
