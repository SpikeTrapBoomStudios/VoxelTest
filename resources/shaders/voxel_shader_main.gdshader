shader_type spatial;
render_mode cull_back;

uniform sampler2DArray atlas_texture_near: filter_nearest, hint_default_black, repeat_disable;
uniform sampler2DArray atlas_texture_far: filter_nearest, hint_default_black, repeat_disable;

varying vec3 normal;

void vertex() {
	normal = NORMAL;
}

void fragment() {
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
	METALLIC = 0.0;
	
	vec2 uv = UV;
	uv = clamp(fract(uv), vec2(0.), vec2(1.));
	
	vec4 nearTex = texture(atlas_texture_near, vec3(uv, UV2.x));
	vec4 farTex = texture(atlas_texture_far, vec3(uv, UV2.x));
	
	vec4 upos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, FRAGCOORD.z, 1.0);
	vec3 pos = upos.xyz / upos.w;
	float cam_distance = -pos.z;
	
	ALBEDO = mix(nearTex, farTex, smoothstep(20.0, 45.0, cam_distance)).rgb;
}
