@tool
extends Node

@export var block_export: Node
@export var heightmap: FastNoiseLite
@export var chunk_wh: int = 4
@export var generate: bool = false:
	set(value):
		_generate_mesh()
@export var reset: bool = false:
	set(value):
		chunk_map = {}
		for mesh in meshes:
			if !mesh.is_inside_tree(): continue
			mesh.queue_free()
		meshes.clear()

var meshes: Array[MeshInstance3D]
var chunk_map: Dictionary = {}
const CHUNK_SIZE_X: int = 16
const CHUNK_SIZE_Z: int = 16
const CHUNK_SIZE_Y: int = 100

var terrain_scale: int = 8

var thread1: Thread

func _ready() -> void:
	if !Engine.is_editor_hint():
		_generate_mesh()

func _exit_tree() -> void:
	thread1.wait_to_finish()

func _generate_mesh():
	thread1 = Thread.new()
	thread1.start(_threaded_generate_chunk)

func _threaded_generate_chunk():
	for x in range(chunk_wh):
		for z in range(chunk_wh):
			var chunk_pos: Vector2 = Vector2(x,z)
			var new_block_map = _populate_block_map(chunk_pos)
			chunk_map[chunk_pos] = new_block_map
			var new_mesh_data = _create_verts(chunk_pos)
			
			_chunk_generation(chunk_pos)

func _chunk_generation(chunk_pos: Vector2):
	var new_block_map = _populate_block_map(chunk_pos)
	chunk_map[chunk_pos] = new_block_map
	var new_mesh_data = _create_verts(chunk_pos)
	
	call_deferred("_create_chunk_mesh_from_data", new_mesh_data, chunk_pos)

func _create_chunk_mesh_from_data(data: Dictionary, chunk_pos: Vector2):
	var array: Array = []
	array.resize(Mesh.ARRAY_MAX)
	array[Mesh.ARRAY_VERTEX] = data["vertices"]
	array[Mesh.ARRAY_INDEX] = data["indices"]
	array[Mesh.ARRAY_TEX_UV] = data["uvs"]
	array[Mesh.ARRAY_TEX_UV2] = data["uv2s"]
	var array_mesh = ArrayMesh.new()
	array_mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, array)
	var mesh = MeshInstance3D.new()
	mesh.mesh = array_mesh
	add_child(mesh)
	mesh.owner = get_tree().edited_scene_root
	mesh.material_override = load("res://resources/materials/voxel_material_main.tres")
	mesh.position = Vector3(chunk_pos.x*16,0,chunk_pos.y*16)
	meshes.append(mesh)

func _populate_block_map(chunk_pos: Vector2) -> Array:
	var block_map: Array = []
	var surface_heights: Array[PackedInt32Array] = []
	for x in range(CHUNK_SIZE_X):
		var chunk_offset_x: int = x + int(chunk_pos.x) * CHUNK_SIZE_X
		var z_slice: PackedInt32Array = PackedInt32Array()
		for z in range(CHUNK_SIZE_Z):
			var chunk_offset_z: int = z + int(chunk_pos.y) * CHUNK_SIZE_Z
			var raw_height: float = heightmap.get_noise_2d(chunk_offset_x, chunk_offset_z)
			var surface_y: int = int(roundf(raw_height * terrain_scale)) + 1
			surface_y = clamp(surface_y, 0, CHUNK_SIZE_Y - 1)
			z_slice.append(surface_y)
		surface_heights.append(z_slice)
	for x in range(CHUNK_SIZE_X):
		var yz_slice: Array[PackedInt32Array] = []
		for y in range(CHUNK_SIZE_Y):
			var z_slice: PackedInt32Array = PackedInt32Array()
			for z in range(CHUNK_SIZE_Z):
				var surface_y: int = surface_heights[x][z]
				
				var block_id := 0
				if y > surface_y:
					block_id = 0
				elif y == surface_y:
					block_id = 1
				else:
					block_id = 2
				
				z_slice.append(block_id)
			yz_slice.append(z_slice)
		block_map.append(yz_slice)
	return block_map

const AXIS_X = 0
const AXIS_Y = 1
const AXIS_Z = 2

const DIR_POS = 1
const DIR_NEG = -1

func _create_verts(chunk_pos: Vector2) -> Dictionary:
	var c: int = 0
	
	var face_definitions = [
		{"name": "right", "axis": AXIS_X, "dir": DIR_POS},
		{"name": "left", "axis": AXIS_X, "dir": DIR_NEG},
		{"name": "top", "axis": AXIS_Y, "dir": DIR_POS},
		{"name": "bottom", "axis": AXIS_Y, "dir": DIR_NEG},
		{"name": "front", "axis": AXIS_Z, "dir": DIR_POS},
		{"name": "back", "axis": AXIS_Z, "dir": DIR_NEG},
	]
	
	var this_mesh_data: Dictionary = {
		"vertices": PackedVector3Array(),
		"indices": PackedInt32Array(),
		"uvs": PackedVector2Array(),
		"uv2s": PackedVector2Array()
	}
	
	for face in face_definitions:
		var new_mesh_data = _greedy_mesh_face(
			face.axis,
			face.dir,
			c,
			chunk_pos
		)
		c = new_mesh_data['c']
		this_mesh_data["vertices"].append_array(new_mesh_data["vertices"])
		this_mesh_data["indices"].append_array(new_mesh_data["indices"])
		this_mesh_data["uvs"].append_array(new_mesh_data["uvs"])
		this_mesh_data["uv2s"].append_array(new_mesh_data["uv2s"])
	
	return this_mesh_data

func _get_face_axes(axis: int) -> Array:
	match axis:
		AXIS_X:
			#X Axis; return [Y, Z]
			return [AXIS_Y, AXIS_Z]
		AXIS_Y:
			#Y Axis; return [Z, X]
			return [AXIS_Z, AXIS_X]
		AXIS_Z:
			#Z Axis; return [X, Y]
			return [AXIS_X, AXIS_Y]
	push_error("_get_face_axes provided an invalid axis")
	return [0,1]

func _greedy_mesh_face(axis: int, dir: int, c: int, chunk_pos: Vector2) -> Dictionary:
	var this_mesh_data: Dictionary = {
		"c": c,
		"vertices": PackedVector3Array(),
		"indices": PackedInt32Array(),
		"uvs": PackedVector2Array(),
		"uv2s": PackedVector2Array()
	}
	
	var size = [CHUNK_SIZE_X, CHUNK_SIZE_Y, CHUNK_SIZE_Z]
	var axies = _get_face_axes(axis)
	var axis1 = axies[0]
	var axis2 = axies[1]

	var main_limit = size[axis]
	var axis1_limit = size[axis1]
	var axis2_limit = size[axis2]

	for main in range(main_limit):
		#This is the mask for-loop. Its job is to populate an array of equal size/organization as the
		#block_map array, except instead of blocks it is true/false values of whether that block should
		#show its face or not, on this side/axis.
		var mask = []
		for i in range(axis1_limit):
			var row = []
			for j in range(axis2_limit):
				#Position is represented as an array so that we can perform [axis]-like
				#operations on it. More modularity essentially.
				var pos = [0,0,0]
				pos[axis] = main
				pos[axis1] = i
				pos[axis2] = j
				
				var adj = [pos[0], pos[1], pos[2]]
				adj[axis] += dir #Moves the block position in the direction of the axis * dir.
								 #So if the axis = 0 (x axis) and dir = -1, moves x-1 blocks.
				
				var current_block_exists = _does_block_exist_at(Vector3(pos[0], pos[1], pos[2]), chunk_pos)
				var adjacent_block_exists = _does_block_exist_at(Vector3(adj[0], adj[1], adj[2]), chunk_pos)
				
				#Appends a true/false value based on whether the current block is air AND the
				#adjacent block is not air
				row.append(current_block_exists and not adjacent_block_exists)
			mask.append(row)
		
		var visited = []
		for i in range(axis1_limit):
			visited.append([])
			for j in range(axis2_limit):
				visited[i].append(false)
		
		for axis1_offset in range(axis1_limit):
			for axis2_offset in range(axis2_limit):
				if mask[axis1_offset][axis2_offset] and not visited[axis1_offset][axis2_offset]:
					var this_pos = [0, 0, 0]
					this_pos[axis1] = axis1_offset
					this_pos[axis2] = axis2_offset
					this_pos[axis] = main
					var this_block = _get_block_id_at(Vector3(this_pos[0], this_pos[1], this_pos[2]), chunk_pos)
					var width = 1
					while (axis1_offset + width) < axis1_limit and mask[axis1_offset + width][axis2_offset] and not visited[axis1_offset + width][axis2_offset]:
						var adj_pos = [0, 0, 0]
						adj_pos[axis1] = axis1_offset + width
						adj_pos[axis2] = axis2_offset
						adj_pos[axis] = main
						var adj_block = _get_block_id_at(Vector3(adj_pos[0], adj_pos[1], adj_pos[2]), chunk_pos)
						if adj_block!=this_block:
							break
						width += 1
					
					var height = 1
					var done = false
					while not done and (axis2_offset + height) < axis2_limit:
						for width_offset in range(width):
							var adj_pos = [0, 0, 0]
							adj_pos[axis1] = axis1_offset + width_offset
							adj_pos[axis2] = axis2_offset + height
							adj_pos[axis] = main
							var adj_block = _get_block_id_at(Vector3(adj_pos[0], adj_pos[1], adj_pos[2]), chunk_pos)
							if adj_block!=this_block:
								done = true
								break
							if not mask[axis1_offset + width_offset][axis2_offset + height] or visited[axis1_offset + width_offset][axis2_offset + height]:
								done = true
								break
						if not done:
							height += 1
					
					for offset_x in range(width):
						for offset_y in range(height):
							visited[axis1_offset + offset_x][axis2_offset + offset_y] = true
					
					var side_name = _get_side_name_from_axis_dir(axis, dir)
					var face_verts = []
					var face_uvs = []
					#Creates a nice set of vertices based on the size of the created greedy rectangle
					for corner in [[0,0],[width,0],[width,height],[0,height]]:
						var vert = [0.0, 0.0, 0.0] #Again, another Vec3 in the form of an array, so that
												   #we can do [axis]-like operations on it.
						vert[axis] = main + (1.0 if dir == 1 else 0.0)
						vert[axis1] = axis1_offset + corner[0]
						vert[axis2] = axis2_offset + corner[1]
						face_verts.append(Vector3(vert[0], vert[1], vert[2])) #Convert back to vec3 from array
						
						var u = corner[0]
						var v = corner[1]
						#Dont even ask why I need to do this. All that needs to be known is that this
						#flips the uv by 90 degrees. Why would we need this? Because some faces are rotated
						#90 degrees wrongly. Why? Idk.
						if axis == AXIS_X or axis == AXIS_Y:
							u = corner[1]
							v = corner[0]
						
						if this_block == 1:
							if side_name == "top":
								this_mesh_data["uv2s"].append(Vector2(0,0))
							elif side_name == "left" or side_name == "right" or side_name == "front" or side_name == "back":
								this_mesh_data["uv2s"].append(Vector2(1,0))
							else:
								this_mesh_data["uv2s"].append(Vector2(2,0))
						elif this_block == 2:
							this_mesh_data["uv2s"].append(Vector2(2,0))
						
						face_uvs.append(Vector2(u, v))
					
					#Proper winding order for positive/negative facing faces
					#Also a ton of different UV appending operations because for some
					#reason each dir/axis has a mind of its own for UV order. :\
					if dir == DIR_POS:
						this_mesh_data["vertices"].append_array([face_verts[0], face_verts[1], face_verts[2], face_verts[3]])
						if axis == AXIS_Z:
							this_mesh_data["uvs"].append_array([face_uvs[3],face_uvs[2],face_uvs[1],face_uvs[0]])
						else:
							this_mesh_data["uvs"].append_array([face_uvs[2],face_uvs[3],face_uvs[0],face_uvs[1]])
					else:
						this_mesh_data["vertices"].append_array([face_verts[0], face_verts[3], face_verts[2], face_verts[1]])
						if axis == AXIS_X:
							this_mesh_data["uvs"].append_array([face_uvs[1],face_uvs[2],face_uvs[3],face_uvs[0]])
						elif axis == AXIS_Z:
							this_mesh_data["uvs"].append_array([face_uvs[2],face_uvs[1],face_uvs[0],face_uvs[3]])
						else:
							this_mesh_data["uvs"].append_array([face_uvs[3],face_uvs[0],face_uvs[1],face_uvs[2]])
					var ic = this_mesh_data['c']
					this_mesh_data["indices"].append_array([
							ic+2, ic + 1, ic,
							ic+3, ic + 2, ic
						])
					this_mesh_data['c'] = this_mesh_data['c'] + 4
	return this_mesh_data

func _get_side_name_from_axis_dir(axis: int, dir: int):
	if dir == DIR_POS:
		match axis:
			AXIS_X: return "right"
			AXIS_Y: return "top"
			AXIS_Z: return "front"
	else:
		match axis:
			AXIS_X: return "left"
			AXIS_Y: return "bottom"
			AXIS_Z: return "back"

func _does_block_exist_at(block_pos: Vector3, chunk_pos: Vector2):
	return _get_block_id_at(block_pos, chunk_pos) != 0

func _get_block_id_at(block_pos: Vector3, chunk_pos: Vector2) -> int:
	var chunk_offset_x: int = int(block_pos.x) + int(chunk_pos.x) * CHUNK_SIZE_X
	var chunk_offset_y: int = int(block_pos.y)
	var chunk_offset_z: int = int(block_pos.z) + int(chunk_pos.y) * CHUNK_SIZE_Z
	
	var chunk_x = floor(float(chunk_offset_x) / CHUNK_SIZE_X)
	var chunk_z = floor(float(chunk_offset_z) / CHUNK_SIZE_Z)
	var local_x = chunk_offset_x - chunk_x * CHUNK_SIZE_X
	var local_z = chunk_offset_z - chunk_z * CHUNK_SIZE_Z
	
	var _chunk_pos := Vector2(chunk_x, chunk_z)
	if not chunk_map.has(_chunk_pos):
		return 0
	
	var _chunk_map = chunk_map[_chunk_pos]
	if chunk_offset_x<0 or chunk_offset_z<0 or chunk_offset_y < 0 or chunk_offset_y >= CHUNK_SIZE_Y:
		return 0
	
	return _chunk_map[local_x][chunk_offset_y][local_z]
