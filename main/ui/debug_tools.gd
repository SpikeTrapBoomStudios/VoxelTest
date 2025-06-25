extends Control

@export var block_generation_node: BlockGen2

@onready var chunk_count_label: Label = $ChunkCountLabel
@onready var lod_label: Label = $LodLabel

func _on_generate_chunks_button_pressed() -> void:
	block_generation_node.generate = true

func _on_chunk_count_slider_value_changed(value:  float) -> void:
	block_generation_node.chunkCount = int(value)
	chunk_count_label.text = "Chunk Count: " + str(value)

func _on_lod_slider_value_changed(value:  float) -> void:
	block_generation_node.blockSizeMultiplier = int(value)
	lod_label.text = "Block LOD: " + str(value)
