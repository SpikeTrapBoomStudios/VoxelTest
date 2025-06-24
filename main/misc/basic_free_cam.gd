extends Camera3D

const MOUSE_SENSITIVITY = .001

var rmb_pressed: bool = false
var yaw: float = 0.0
var pitch: float = -PI/2

@onready var fps_counter: Label = $FPSCounter

func _process(delta: float) -> void:
	if !rmb_pressed:
		Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	else:
		Input.mouse_mode = Input.MOUSE_MODE_CAPTURED
	#Locomotion
	var input_dir: Vector2 = Input.get_vector("left","right","backward","forward")
	var forward = -basis.z
	var right = basis.x
	var move_dir = (forward * input_dir.y) + (right * input_dir.x).normalized()
	
	var speed = 5.0
	if Input.is_action_pressed("sprint"): speed = 60.0
	position += move_dir * speed * delta
	
	fps_counter.text = "FPS: " + str(Engine.get_frames_per_second())

func _input(event: InputEvent) -> void:
	if event is InputEventKey and event.is_pressed():
		if event.keycode == KEY_T:
			if get_viewport().debug_draw == Viewport.DEBUG_DRAW_WIREFRAME:
				get_viewport().debug_draw = Viewport.DEBUG_DRAW_DISABLED
			else:
				get_viewport().debug_draw = Viewport.DEBUG_DRAW_WIREFRAME
	elif event is InputEventMouseMotion and rmb_pressed:
		var mouse_delta = event.relative
		yaw -= MOUSE_SENSITIVITY * mouse_delta.x
		pitch -= MOUSE_SENSITIVITY * mouse_delta.y
		rotation = Vector3(pitch, yaw, 0)
	elif event is InputEventMouseButton:
		rmb_pressed = event.button_index == MOUSE_BUTTON_RIGHT and event.is_pressed()
