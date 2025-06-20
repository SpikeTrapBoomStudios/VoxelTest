extends Node

var block_dict: Dictionary = {}

var block_id: int = 1

func _init() -> void:
	register_block(GrassBlock.new())
	register_block(DirtBlock.new())

func register_block(block: BlockDefinition):
	block_dict[block_id] = block
	block_id+=1

func block_to_id(block: BlockDefinition):
	var key = block_dict.find_key(block)
	if key == null: push_warning("Attempted to retrieve non-existent id for block: "+str(block))
	return key

func id_to_block(id: int):
	var block = block_dict.get(id)
	if block == null: push_warning("Attempted to retreive non-existent block for id: "+str(id))
	return block
