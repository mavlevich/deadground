@tool
extends EditorScenePostImport


func _post_import(scene: Node) -> Object:
    _configure_materials(scene, get_source_file().get_file() == "t62.glb")
    return scene


func _configure_materials(node: Node, close_detail: bool) -> void:
    if node is MeshInstance3D and node.mesh != null:
        for surface in node.mesh.get_surface_count():
            var material := node.mesh.surface_get_material(surface) as StandardMaterial3D
            if material != null:
                material.vertex_color_use_as_albedo = true
                material.vertex_color_is_srgb = false
                if material.resource_name == "T62_Paint" and material.albedo_texture == null:
                    material.albedo_texture = _paint_variation()
                    material.uv1_triplanar = true
                    material.uv1_scale = Vector3(0.42, 0.42, 0.42)
                    material.roughness_texture = _roughness_variation()
                    material.roughness_texture_channel = BaseMaterial3D.TEXTURE_CHANNEL_RED
                    material.uv1_triplanar_sharpness = 4.0
                    if close_detail:
                        material.normal_enabled = true
                        material.normal_texture = _cast_grain()
                        material.normal_scale = 0.20
    for child in node.get_children():
        _configure_materials(child, close_detail)


func _paint_variation() -> NoiseTexture2D:
    # Original procedural paint mottling, with no downloaded image dependencies.
    var noise := FastNoiseLite.new()
    noise.seed = 62
    noise.noise_type = FastNoiseLite.TYPE_SIMPLEX_SMOOTH
    noise.frequency = 0.095
    noise.fractal_octaves = 3
    var gradient := Gradient.new()
    gradient.offsets = PackedFloat32Array([0.0, 0.40, 0.68, 1.0])
    gradient.colors = PackedColorArray([Color(0.89, 0.89, 0.85), Color(0.93, 0.93, 0.88), Color(0.97, 0.96, 0.91), Color(1.0, 0.98, 0.94)])
    var texture := NoiseTexture2D.new()
    texture.width = 256
    texture.height = 256
    texture.noise = noise
    texture.color_ramp = gradient
    texture.seamless = true
    texture.generate_mipmaps = true
    return texture


func _cast_grain() -> NoiseTexture2D:
    # Fine surface relief is confined to the close model, not the RTS material.
    var noise := FastNoiseLite.new()
    noise.seed = 620
    noise.noise_type = FastNoiseLite.TYPE_SIMPLEX_SMOOTH
    noise.frequency = 0.28
    noise.fractal_octaves = 2
    var texture := NoiseTexture2D.new()
    texture.width = 512
    texture.height = 512
    texture.noise = noise
    texture.as_normal_map = true
    texture.bump_strength = 0.5
    texture.seamless = true
    texture.generate_mipmaps = true
    return texture


func _roughness_variation() -> NoiseTexture2D:
    # Patchy paint reflects light differently from exposed and dusty areas.
    var noise := FastNoiseLite.new()
    noise.seed = 623
    noise.noise_type = FastNoiseLite.TYPE_SIMPLEX_SMOOTH
    noise.frequency = 0.06
    noise.fractal_octaves = 3
    var gradient := Gradient.new()
    gradient.colors = PackedColorArray([Color(0.70, 0.70, 0.70), Color(1, 1, 1)])
    var texture := NoiseTexture2D.new()
    texture.width = 256
    texture.height = 256
    texture.noise = noise
    texture.color_ramp = gradient
    texture.seamless = true
    texture.generate_mipmaps = true
    return texture
