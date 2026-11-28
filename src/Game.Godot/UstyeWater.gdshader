shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_disabled;

// Muted Cold War Baltic waterway palette with dual-scrolling tileable normal maps
uniform sampler2D water_normal : hint_normal, filter_linear_mipmap, repeat_enable;
uniform sampler2D macro_tex : filter_linear_mipmap, repeat_enable;

uniform vec4 shallows_color : source_color = vec4(0.24, 0.32, 0.28, 0.78);
uniform vec4 deep_color : source_color = vec4(0.11, 0.17, 0.18, 0.96);
uniform float depth_distance : hint_range(0.1, 4.0) = 1.6;
uniform float roughness : hint_range(0.0, 1.0) = 0.14;
uniform float metallic : hint_range(0.0, 1.0) = 0.06;
uniform float flow_speed : hint_range(0.0, 1.0) = 0.12;
uniform vec4 horizon_reflect_color : source_color = vec4(0.54, 0.60, 0.64, 1.0);
uniform float horizon_reflect_strength : hint_range(0.0, 1.0) = 0.0;
uniform float rain_ripple : hint_range(0.0, 1.0) = 0.0;

uniform sampler2D depth_texture : hint_depth_texture, filter_linear_mipmap;

varying vec3 world_pos;

float hash21(vec2 p) {
    p = fract(p * vec2(127.1, 311.7));
    p += dot(p, p + 34.57);
    return fract(p.x * p.y);
}

float rain_ring_layer(vec2 xz, float scale, float speed, float time) {
    vec2 g = xz * scale;
    vec2 cell = floor(g);
    vec2 f = fract(g);
    vec2 drop_center = vec2(
        0.22 + 0.56 * hash21(cell + vec2(1.7, 9.2)),
        0.22 + 0.56 * hash21(cell + vec2(8.3, 2.8)));
    float age = fract(time * speed + hash21(cell + vec2(4.1, 5.6)));
    float dist = length(f - drop_center);
    float radius = age * 0.42;
    float ring = smoothstep(0.055, 0.0, abs(dist - radius));
    return ring * smoothstep(0.0, 0.08, age) * (1.0 - age) * (1.0 - age);
}

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    vec2 xz = world_pos.xz;
    float t = TIME * flow_speed;

    // Dual-scrolling organic capillary + current normal maps (zero sine banding)
    vec2 uv_a = xz * 0.030 + vec2(0.0, t * 0.38);
    vec2 uv_b = vec2(xz.x * 0.72 - xz.y * 0.69, xz.x * 0.69 + xz.y * 0.72) * 0.056 + vec2(t * 0.16, -t * 0.24);
    vec3 n_a = texture(water_normal, uv_a).xyz * 2.0 - 1.0;
    vec3 n_b = texture(water_normal, uv_b).xyz * 2.0 - 1.0;
    vec3 wave_n = normalize(vec3((n_a.x + n_b.x) * 0.42, 1.0, (n_a.y + n_b.y) * 0.42));
    NORMAL = normalize(mat3(VIEW_MATRIX) * wave_n);

    // Depth-based soft edge and water column coloration
    float depth_raw = textureLod(depth_texture, SCREEN_UV, 0.0).r;
    vec3 ndc = vec3(SCREEN_UV * 2.0 - 1.0, depth_raw);
    vec4 view_pos = INV_PROJECTION_MATRIX * vec4(ndc, 1.0);
    view_pos.xyz /= view_pos.w;

    float linear_depth = -view_pos.z;
    float water_depth = max(0.0, linear_depth - (-VERTEX.z));
    float depth_factor = clamp(water_depth / depth_distance, 0.0, 1.0);

    vec4 water_col = mix(shallows_color, deep_color, depth_factor);
    // Subtle wind-ripple surface sheen visible even from steep RTS camera angles
    float ripple_glint = clamp(0.5 + (n_a.x - n_b.y) * 0.55, 0.0, 1.0);
    ALBEDO = water_col.rgb * mix(0.90, 1.14, ripple_glint);

    // Grazing-angle sky horizon reflection
    if (horizon_reflect_strength > 0.001) {
        float ndotv = clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0);
        float fresnel = pow(1.0 - ndotv, 2.3);
        ALBEDO = mix(ALBEDO, horizon_reflect_color.rgb, fresnel * horizon_reflect_strength);
    }

    // Gentle expanding raindrop rings during storm weather (close/play zoom only)
    float effective_roughness = roughness;
    if (rain_ripple > 0.001) {
        float px_size = max(length(dFdx(xz)), length(dFdy(xz)));
        float ripple_fade = smoothstep(0.95, 0.10, px_size);
        if (ripple_fade > 0.001) {
            float rings = rain_ring_layer(xz, 0.42, 1.25, TIME) * rain_ripple * ripple_fade;
            vec3 ring_col = mix(horizon_reflect_color.rgb, vec3(0.78, 0.84, 0.90), 0.50);
            ALBEDO = mix(ALBEDO, ring_col, rings * 0.34);
            effective_roughness = mix(roughness, 0.24, rain_ripple * 0.40);
        }
    }

    // Organic soft shoreline blend (no artificial dotted sine foam)
    float shore_alpha = smoothstep(0.0, 0.28, water_depth);
    ALPHA = water_col.a * shore_alpha;

    if (water_depth < 0.26) {
        float foam_t = 1.0 - (water_depth / 0.26);
        float shore_noise = texture(macro_tex, xz * 0.055 + vec2(0.0, t * 0.12)).g;
        float subtle_edge = foam_t * smoothstep(0.48, 0.82, shore_noise) * 0.22;
        ALBEDO = mix(ALBEDO, vec3(0.58, 0.62, 0.58), subtle_edge);
    }

    ROUGHNESS = effective_roughness;
    METALLIC = metallic;
    SPECULAR = 0.55;
}
