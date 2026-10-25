shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_disabled;

// Muted Cold War Baltic waterway palette
uniform vec4 shallows_color : source_color = vec4(0.24, 0.32, 0.28, 0.70);
uniform vec4 deep_color : source_color = vec4(0.11, 0.17, 0.18, 0.94);
uniform float depth_distance : hint_range(0.1, 4.0) = 1.4;
uniform float roughness : hint_range(0.0, 1.0) = 0.16;
uniform float metallic : hint_range(0.0, 1.0) = 0.08;
uniform float flow_speed : hint_range(0.0, 1.0) = 0.12;

uniform sampler2D depth_texture : hint_depth_texture, filter_linear_mipmap;

varying vec3 world_pos;

float get_wave_height(vec2 pos, float time) {
    float w1 = sin(pos.x * 0.14 + pos.y * 0.09 + time * 1.4) * 0.045;
    float w2 = sin(pos.x * -0.11 + pos.y * 0.16 - time * 1.1) * 0.035;
    float w3 = sin((pos.x + pos.y) * 0.25 + time * 1.8) * 0.020;
    return w1 + w2 + w3;
}

vec3 get_wave_normal(vec2 pos, float time) {
    float e = 0.20;
    float hL = get_wave_height(pos - vec2(e, 0.0), time);
    float hR = get_wave_height(pos + vec2(e, 0.0), time);
    float hD = get_wave_height(pos - vec2(0.0, e), time);
    float hU = get_wave_height(pos + vec2(0.0, e), time);
    return normalize(vec3(hL - hR, 2.0 * e, hD - hU));
}

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    // Flowing wave coordinates along the river current
    vec2 flow_uv = world_pos.xz + vec2(0.0, TIME * flow_speed * 12.0);
    vec3 wave_norm = get_wave_normal(flow_uv, TIME);

    // Transform wave normal to view space
    NORMAL = normalize(mat3(VIEW_MATRIX) * wave_norm);

    // Depth-based soft edge and water column coloration
    float depth_raw = textureLod(depth_texture, SCREEN_UV, 0.0).r;
    vec3 ndc = vec3(SCREEN_UV * 2.0 - 1.0, depth_raw);
    vec4 view_pos = INV_PROJECTION_MATRIX * vec4(ndc, 1.0);
    view_pos.xyz /= view_pos.w;

    float linear_depth = -view_pos.z;
    float water_depth = max(0.0, linear_depth - (-VERTEX.z));
    float depth_factor = clamp(water_depth / depth_distance, 0.0, 1.0);

    // Gradient from pale mossy shallows to deep dark slate channel
    vec4 water_col = mix(shallows_color, deep_color, depth_factor);
    ALBEDO = water_col.rgb;

    // Smooth shoreline fade: zero hard geometric seam at the bank edge
    float shore_alpha = smoothstep(0.0, 0.18, water_depth);
    ALPHA = water_col.a * shore_alpha;

    // Subtle, broken organic foam in shallow breaking zone (< 0.22m)
    if (water_depth < 0.22) {
        float foam_t = 1.0 - (water_depth / 0.22);
        float foam_break = sin(world_pos.x * 0.45 + world_pos.z * 0.35 + TIME * 0.6) * 0.5 + 0.5;
        foam_break *= sin(world_pos.x * 1.2 - world_pos.z * 0.8) * 0.5 + 0.5;
        float foam_intensity = foam_t * smoothstep(0.35, 0.70, foam_break);
        ALBEDO = mix(ALBEDO, vec3(0.72, 0.76, 0.73), foam_intensity * 0.40);
    }

    ROUGHNESS = roughness;
    METALLIC = metallic;
    SPECULAR = 0.5;
}
