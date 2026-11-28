shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_disabled, diffuse_lambert;

// 0 = asphalt highway, 1 = rural gravel road, 2 = dirt track/footpath
uniform int road_type = 0;
uniform sampler2D asphalt_albedo : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D asphalt_normal : hint_normal, filter_linear_mipmap, repeat_enable;
uniform sampler2D gravel_albedo : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D gravel_normal : hint_normal, filter_linear_mipmap, repeat_enable;
uniform sampler2D macro_tex : filter_linear_mipmap, repeat_enable;

uniform vec4 base_color : source_color = vec4(0.22, 0.23, 0.24, 1.0);
uniform vec4 shoulder_color : source_color = vec4(0.36, 0.33, 0.28, 1.0);
uniform float wetness : hint_range(0.0, 1.0) = 0.0;
uniform sampler2D fog_texture : filter_linear, repeat_disable;
uniform vec2 map_size = vec2(5000.0, 5000.0);
uniform bool fog_enabled = false;

varying vec3 world_pos;
varying vec2 road_right_xz;

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    road_right_xz = UV2;
}

void fragment() {
    float u = UV.x;
    float dist_m = UV.y;
    float dist_from_center = abs(u - 0.5) * 2.0; // 0 at center, 1 at outer verge
    vec2 xz = world_pos.xz;
    float px_size = max(length(dFdx(xz)), length(dFdy(xz)));

    // Single mipmapped macro lookup drives organic edge breakup and longitudinal wear
    vec4 macro = texture(macro_tex, xz * 0.025);
    float edge_var = (macro.a - 0.5) * 0.12 + (macro.g - 0.5) * 0.05;

    vec2 uv_asph = vec2(u * 1.6, dist_m * 0.14);
    vec2 uv_grav = xz * 0.12;

    vec3 asph_tex = texture(asphalt_albedo, uv_asph).rgb;
    vec2 asph_n_uv = texture(asphalt_normal, uv_asph).xy * 2.0 - 1.0;
    vec3 grav_tex = texture(gravel_albedo, uv_grav).rgb;
    vec2 grav_n_world = texture(gravel_normal, uv_grav).xy * 2.0 - 1.0;

    // Transform road-aligned asphalt normal into world XZ using per-vertex road basis (UV2 = +UV.x in XZ)
    float rlen = length(road_right_xz);
    vec2 right_xz = rlen > 0.001 ? (road_right_xz / rlen) : vec2(1.0, 0.0);
    vec2 fwd_xz = vec2(right_xz.y, -right_xz.x);
    vec2 asph_n_world = right_xz * asph_n_uv.x + fwd_xz * asph_n_uv.y;

    // Normalize against linear equivalents of default sRGB profile anchors (#353638 and #585246)
    vec3 asph_tint = clamp(base_color.rgb / vec3(0.036, 0.037, 0.040), vec3(0.55), vec3(1.55));
    vec3 grav_tint = clamp(shoulder_color.rgb / vec3(0.097, 0.084, 0.062), vec3(0.55), vec3(1.55));

    // Sun-faded 1980s European highway tarmac tone
    vec3 col = asph_tex * asph_tint * 1.32;
    vec2 world_n = asph_n_world;
    float rough = 0.88;
    float spec = 0.08;
    float alpha = 1.0;

    if (road_type == 0) {
        // Asphalt highway: weathered grey tarmac + dusty tire paths + subtle oil strip + weathered Soviet dashed centerline + gravel verge
        col *= mix(0.88, 1.12, macro.r);

        float rut_l = exp(-pow((u - 0.33) / 0.075, 2.0));
        float rut_r = exp(-pow((u - 0.67) / 0.075, 2.0));
        float rut = clamp(rut_l + rut_r, 0.0, 1.0);
        // Worn aggregate in tire tracks reads slightly cooler/lighter dry, darker glossy when wet
        col *= mix(1.0, mix(1.09, 0.82, wetness), rut * 0.72);

        // Weathered dashed centerline (~14cm width), chipped by pavement roughness
        float center_band = 1.0 - smoothstep(0.006, 0.013, abs(u - 0.5));
        float dash_cycle = fract(dist_m / 9.5);
        float dash = step(0.42, dash_cycle) * (1.0 - step(0.84, dash_cycle));
        float asph_luma = dot(asph_tex, vec3(0.299, 0.587, 0.114));
        float paint_wear = smoothstep(0.18, 0.64, macro.g * 0.62 + macro.a * 0.24 + clamp(asph_luma * 2.8, 0.0, 0.25));
        float line_fade = smoothstep(1.20, 0.25, px_size);
        vec3 line_col = vec3(0.52, 0.50, 0.44);
        col = mix(col, line_col, center_band * dash * paint_wear * 0.56 * line_fade);

        // Real dusty gravel shoulder outside asphalt crown
        float shoulder_blend = smoothstep(0.68, 0.80, dist_from_center + edge_var * 0.45);
        vec3 sh_col = grav_tex * grav_tint * mix(0.92, 1.12, macro.g);
        col = mix(col, sh_col, shoulder_blend);
        world_n = mix(asph_n_world, grav_n_world, shoulder_blend);

        alpha = 1.0 - smoothstep(0.84, 1.0, dist_from_center + edge_var);

        // Wet asphalt darkens naturally and catches overcast sky sheen in tire tracks
        col *= mix(1.0, 0.68, wetness);
        float wet_rut = clamp(rut * 0.75 + (1.0 - shoulder_blend) * 0.35, 0.0, 1.0) * wetness;
        rough = mix(mix(0.88, 0.94, shoulder_blend), 0.20, wet_rut);
        spec = mix(0.08, 0.68, wet_rut);
    } else if (road_type == 1) {
        // Rural gravel road with compacted ruts and soft grassy verge
        vec3 road_tint = clamp(base_color.rgb / vec3(0.097, 0.084, 0.062), vec3(0.55), vec3(1.55));
        col = grav_tex * road_tint * mix(0.90, 1.10, macro.r);
        world_n = grav_n_world;

        float rut_l = exp(-pow((u - 0.34) / 0.095, 2.0));
        float rut_r = exp(-pow((u - 0.66) / 0.095, 2.0));
        float rut = clamp(rut_l + rut_r, 0.0, 1.0);
        col *= mix(1.04, 0.84, rut);

        alpha = 1.0 - smoothstep(0.75, 1.0, dist_from_center + edge_var * 1.3);
        col *= mix(1.0, 0.76, wetness);
        rough = mix(0.94, 0.42, wetness * (0.5 + 0.5 * rut));
        spec = mix(0.05, 0.42, wetness * (0.4 + 0.6 * rut));
    } else {
        // Dirt track / footpath
        vec3 dirt_tint = clamp(base_color.rgb / vec3(0.128, 0.104, 0.072), vec3(0.55), vec3(1.55));
        col = grav_tex * dirt_tint * mix(0.86, 1.06, macro.g);
        world_n = grav_n_world;

        float center_grass = (1.0 - smoothstep(0.08, 0.30, dist_from_center)) * smoothstep(0.32, 0.72, macro.r);
        col = mix(col, vec3(0.06, 0.08, 0.03), center_grass * 0.45);
        alpha = 1.0 - smoothstep(0.68, 1.0, dist_from_center + edge_var * 1.5);

        col *= mix(1.0, 0.76, wetness);
        rough = mix(0.96, 0.50, wetness);
        spec = mix(0.04, 0.32, wetness);
    }

    vec3 perturbed = normalize(vec3(world_n.x * 0.35, 1.0, world_n.y * 0.35));
    NORMAL = normalize(mat3(VIEW_MATRIX) * perturbed);

    if (fog_enabled) {
        vec2 fog_uv = clamp(world_pos.xz / map_size, vec2(0.0), vec2(1.0));
        float vis = texture(fog_texture, fog_uv).r;
        float gray = dot(col, vec3(0.299, 0.587, 0.114));
        vec3 desaturated = mix(col, vec3(gray), 0.35);
        vec3 unseen_color = desaturated * 0.45;
        col = mix(unseen_color, col, vis);
    }

    ALBEDO = col;
    ALPHA = clamp(alpha, 0.0, 1.0);
    ROUGHNESS = rough;
    SPECULAR = spec;
}
