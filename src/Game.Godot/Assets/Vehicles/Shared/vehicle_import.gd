@tool
extends EditorScenePostImport

# Puts the shared weathering shader on vehicles built with tools/model-authoring/vehicle_kit.py.
# Vertex colors carry baked ambient occlusion and the dirt mask; running gear meshes carry
# motion data in UV and UV2 (see the shader header). Markings and optics keep their glTF materials.

const WEATHERING := preload("res://Assets/Vehicles/Shared/vehicle_weathering.gdshader")
# Running gear meshes (named *_Motion_*) need the variant that reads the per-instance travel.
const WEATHERING_MOTION := preload("res://Assets/Vehicles/Shared/vehicle_weathering_motion.gdshader")


func _post_import(scene: Node) -> Object:
    _convert(scene, {})
    return scene


func _convert(node: Node, cache: Dictionary) -> void:
    if node is MeshInstance3D and node.mesh != null:
        for surface in node.mesh.get_surface_count():
            var material := node.mesh.surface_get_material(surface) as StandardMaterial3D
            if material == null:
                continue
            var material_name := material.resource_name
            if material_name.ends_with("_Markings") or material_name.ends_with("_Optics"):
                continue
            var motion: bool = node.name.contains("_Motion_")
            var key := material_name + ("|motion" if motion else "")
            if not cache.has(key):
                cache[key] = _weathered(material, WEATHERING_MOTION if motion else WEATHERING)
            node.mesh.surface_set_material(surface, cache[key])
    for child in node.get_children():
        _convert(child, cache)


func _weathered(source: StandardMaterial3D, shader: Shader) -> ShaderMaterial:
    var material := ShaderMaterial.new()
    material.resource_name = source.resource_name
    material.shader = shader
    material.set_shader_parameter("base_color", source.albedo_color)
    material.set_shader_parameter("roughness_value", source.roughness)
    material.set_shader_parameter("metallic_value", source.metallic)
    var material_name := source.resource_name
    if material_name.ends_with("_Rubber") or material_name.ends_with("_TrackSteel"):
        material.set_shader_parameter("dirt_strength", 0.75)
        material.set_shader_parameter("dust_strength", 0.15)
    elif material_name.ends_with("_Canvas"):
        material.set_shader_parameter("mottle_strength", 0.12)
        material.set_shader_parameter("dirt_strength", 0.5)
    if material_name == "M60_Paint":
        # MERDC Winter Verdant over forest green, as on US tanks in Europe from 1975.
        material.set_shader_parameter("base_color", Color("3d4632"))
        material.set_shader_parameter("camo_strength", 1.0)
    return material
