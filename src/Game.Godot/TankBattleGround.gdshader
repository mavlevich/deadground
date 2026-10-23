shader_type spatial;
render_mode cull_back;

uniform sampler2D soviet_fog : filter_linear, repeat_disable;
uniform sampler2D us_fog : filter_linear, repeat_disable;
uniform float map_size = 3200.0;
uniform int fog_mode = 0;

varying vec3 world_position;

float border(sampler2D mask, vec2 uv) {
    float center = texture(mask, uv).r;
    float width = max(fwidth(center) * 0.8, 0.01);
    return 1.0 - smoothstep(width, width * 2.0, abs(center - 0.5));
}

float hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float noise(vec2 p) {
    vec2 cell = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(cell), hash(cell + vec2(1.0, 0.0)), f.x),
        mix(hash(cell + vec2(0.0, 1.0)), hash(cell + vec2(1.0, 1.0)), f.x), f.y);
}

void vertex() {
    world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    vec2 p = world_position.xz;
    float patch = noise(p * 0.035) * 0.65 + noise(p * 0.21) * 0.25 + noise(p * 1.2) * 0.1;
    vec3 grass = mix(vec3(0.095, 0.135, 0.065), vec3(0.24, 0.25, 0.13), patch);
    float track = 1.0 - smoothstep(2.3, 4.6, abs(abs(p.y - map_size * 0.5) - 13.0));
    vec3 soil = mix(vec3(0.13, 0.11, 0.075), vec3(0.23, 0.20, 0.135), patch);
    ALBEDO = mix(grass, soil, track * 0.8);
    vec2 uv = p / map_size;
    if (fog_mode != 3 && all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0)))) {
        float soviet = texture(soviet_fog, uv).r;
        float us = texture(us_fog, uv).r;
        float visible = fog_mode == 1 ? soviet : fog_mode == 2 ? us : max(soviet, us);
        ALBEDO *= mix(0.25, 1.0, visible);
        vec3 amber = vec3(1.0, 0.47, 0.08);
        vec3 cyan = vec3(0.1, 0.7, 1.0);
        if (fog_mode != 2) {
            ALBEDO = mix(ALBEDO, amber, soviet * 0.06);
            EMISSION += amber * border(soviet_fog, uv) * 0.7;
        }
        if (fog_mode != 1) {
            ALBEDO = mix(ALBEDO, cyan, us * 0.06);
            EMISSION += cyan * border(us_fog, uv) * 0.7;
        }
    }
    ROUGHNESS = 0.98;
}
