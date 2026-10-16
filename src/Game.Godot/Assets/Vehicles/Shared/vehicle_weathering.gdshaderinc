// Weathering for procedural vehicles without UVs.
// Vertex color rgb: tint times baked ambient occlusion (linear multiplier).
// Vertex color alpha: baked dirt mask (lower hull, running gear, crevices).
// Noise uses mesh-local positions, so patterns stay fixed to the vehicle
// while it drives and to the turret while it turns.
// Running gear motion (meshes exported by vehicle_kit.emit_motion_parts):
//   wheel: UV = axle center (y, z), UV2 = (rolling radius, 0)
//   link:  UV = position one cycle later (y, z), UV2 = (link turn over the cycle, cycle length)
// Only vehicle_weathering_motion.gdshader (VEHICLE_MOTION) runs this; static meshes use
// vehicle_weathering.gdshader and carry no per-instance parameter.

uniform vec4 base_color : source_color = vec4(0.32, 0.33, 0.24, 1.0);
uniform float roughness_value : hint_range(0.0, 1.0) = 0.82;
uniform float metallic_value : hint_range(0.0, 1.0) = 0.06;
uniform vec4 dirt_color : source_color = vec4(0.30, 0.26, 0.20, 1.0);
uniform vec4 dust_color : source_color = vec4(0.60, 0.56, 0.46, 1.0);
uniform float dirt_strength : hint_range(0.0, 1.0) = 0.65;
uniform float dust_strength : hint_range(0.0, 1.0) = 0.12;
uniform float mottle_strength : hint_range(0.0, 0.3) = 0.11;
uniform float streak_strength : hint_range(0.0, 0.5) = 0.18;
uniform float fade_strength : hint_range(0.0, 0.5) = 0.10;
uniform float specular_value : hint_range(0.0, 1.0) = 0.3;
uniform float noise_scale = 1.0;
#ifdef VEHICLE_MOTION
// Track travel of this side in model meters, set per mesh instance by the client.
instance uniform float track_travel = 0.0;
#endif
// Optional four-color pattern (for example MERDC Winter Verdant), painted over base_color.
uniform float camo_strength : hint_range(0.0, 1.0) = 0.0;
uniform vec4 camo_color_a : source_color = vec4(0.43, 0.36, 0.24, 1.0);
uniform vec4 camo_color_b : source_color = vec4(0.65, 0.55, 0.41, 1.0);
uniform vec4 camo_color_c : source_color = vec4(0.11, 0.11, 0.10, 1.0);
uniform float camo_scale = 0.38;

varying vec3 local_pos;
varying vec3 world_normal;

float hash31(vec3 p) {
	p = fract(p * 0.3183099 + vec3(0.71, 0.113, 0.419));
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float value_noise(vec3 x) {
	vec3 i = floor(x);
	vec3 f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	return mix(
		mix(mix(hash31(i), hash31(i + vec3(1, 0, 0)), f.x),
			mix(hash31(i + vec3(0, 1, 0)), hash31(i + vec3(1, 1, 0)), f.x), f.y),
		mix(mix(hash31(i + vec3(0, 0, 1)), hash31(i + vec3(1, 0, 1)), f.x),
			mix(hash31(i + vec3(0, 1, 1)), hash31(i + vec3(1, 1, 1)), f.x), f.y),
		f.z);
}

float fbm(vec3 p) {
	float value = 0.0;
	float amplitude = 0.5;
	for (int i = 0; i < 4; i++) {
		value += amplitude * value_noise(p);
		p *= 2.03;
		amplitude *= 0.5;
	}
	return value;
}

// Rotates a (y, z) vector about the x axis; the angle is measured as atan2(y, z).
vec2 rotate_yz(vec2 yz, float a) {
	float c = cos(a);
	float s = sin(a);
	return vec2(yz.y * s + yz.x * c, yz.y * c - yz.x * s);
}

void vertex() {
	// Noise follows the rest position, so paint and dirt stay attached to moving parts.
	local_pos = VERTEX * noise_scale;
#ifdef VEHICLE_MOTION
	if (UV2.y > 0.0) {
		float phase = fract(track_travel / UV2.y);
		VERTEX.yz = mix(VERTEX.yz, UV, phase);
		NORMAL.yz = rotate_yz(NORMAL.yz, UV2.x * phase);
	} else if (UV2.x > 0.0) {
		float angle = track_travel / UV2.x;
		VERTEX.yz = UV + rotate_yz(VERTEX.yz - UV, angle);
		NORMAL.yz = rotate_yz(NORMAL.yz, angle);
	}
#endif
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	vec3 tint = COLOR.rgb;
	float dirt_mask = COLOR.a;

	vec3 paint = base_color.rgb;
	if (camo_strength > 0.0) {
		// Large hard-edged blotches with a slightly soft spray edge, plus thin black bands.
		vec3 p = local_pos * camo_scale;
		float n1 = fbm(p + vec3(7.1, 2.3, 0.0));
		float n2 = fbm(p * 1.3 + vec3(1.9, 9.4, 4.2));
		float n3 = fbm(p * 1.7 + vec3(4.4, 0.7, 8.8));
		// Roughly 45 % green, 45 % field drab, small sand and black patches, as in MERDC.
		float a = smoothstep(0.465, 0.485, n1);
		float b = smoothstep(0.665, 0.685, n2) * (1.0 - a);
		float k = smoothstep(0.655, 0.675, n3) * (1.0 - b);
		vec3 camo = mix(paint, camo_color_a.rgb, a);
		camo = mix(camo, camo_color_b.rgb, b);
		camo = mix(camo, camo_color_c.rgb, k);
		paint = mix(paint, camo, camo_strength);
	}
	float mottle = fbm(local_pos * 1.7);
	vec3 color = paint * tint * (1.0 + (mottle - 0.5) * 2.0 * mottle_strength);

	float grime = fbm(local_pos * 5.0 + vec3(3.1, 0.0, 1.7));
	float dirt = clamp(dirt_mask * (0.25 + 1.3 * grime), 0.0, 1.0) * dirt_strength;
	color = mix(color, dirt_color.rgb * (0.55 + 0.45 * tint), dirt);

	// Rain and grime streaks run down walls: noise stretched along the vertical axis.
	float wall = 1.0 - smoothstep(0.35, 0.8, abs(world_normal.y));
	// A broad mask keeps them in patches under hatches and edges instead of covering every wall.
	float streak = fbm(vec3(local_pos.x * 7.0, local_pos.y * 0.6, local_pos.z * 7.0) + vec3(5.3));
	float patches = smoothstep(0.42, 0.62, fbm(local_pos * 0.9 + vec3(21.0)));
	streak = smoothstep(0.40, 0.72, streak) * patches * wall * streak_strength;
	color *= 1.0 - streak;

	// Sun and weather fade the paint on top surfaces toward a dry, lighter olive.
	float up = smoothstep(0.5, 0.95, world_normal.y);
	color = mix(color, color * vec3(1.10, 1.08, 1.0) + vec3(0.012), up * fade_strength * (0.6 + 0.8 * mottle));
	float dust = up * smoothstep(0.35, 0.90, fbm(local_pos * 2.2 + vec3(11.0))) * dust_strength;
	color = mix(color, dust_color.rgb * tint, dust);

	ALBEDO = color;
	ROUGHNESS = clamp(mix(roughness_value, 0.97, max(dirt, dust)), 0.0, 1.0);
	SPECULAR = specular_value * (1.0 - 0.6 * dirt);
	METALLIC = metallic_value * (1.0 - dirt);
}
