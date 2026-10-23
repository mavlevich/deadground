@tool
extends EditorScenePostImport

# Keep the authored colors and cyclic clips intact after a clean glTF import.
const LOOPING_CLIPS := ["Idle", "AimStanding", "AimKneeling", "AimProne", "Walk", "Run"]


func _post_import(scene: Node) -> Object:
	_configure(scene)
	return scene


func _configure(node: Node) -> void:
	if node is MeshInstance3D and node.mesh != null:
		for surface in node.mesh.get_surface_count():
			var material := node.mesh.surface_get_material(surface) as BaseMaterial3D
			var has_colors: bool = (node.mesh.surface_get_format(surface) & Mesh.ARRAY_FORMAT_COLOR) != 0
			if material != null and has_colors:
				material.vertex_color_use_as_albedo = true
	if node is AnimationPlayer:
		for clip in LOOPING_CLIPS:
			if node.has_animation(clip):
				node.get_animation(clip).loop_mode = Animation.LOOP_LINEAR
	for child in node.get_children():
		_configure(child)
