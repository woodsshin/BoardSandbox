--!strict
local rules = {}
local saved = 0

function rules.new_game(options: { [string]: any })
	saved = 0
	bse.turn(1)
end

function rules.command(seat: number, cmd: { [string]: any }): any
	local action = cmd.action
	if action == "lifecycle" then
		local id = bse.view.spawn("piece", 2, 3, { owner = seat, props = { color = "red" } })
		bse.view.move(id, 4, 5)
		bse.view.set(id, { color = "blue", n = 2 })
		local object = bse.view.get(id)
		assert(object ~= nil)
		bse.status(`{object.x},{object.y}`, "warn")
		saved = id
		return nil
	elseif action == "remove" then
		bse.view.remove(saved)
		return nil
	elseif action == "remove_missing" then
		bse.view.remove(999)
		return nil
	elseif action == "move_missing" then
		bse.view.move(999, 1, 1)
		return nil
	elseif action == "set_missing" then
		bse.view.set(999, { a = 1 })
		return nil
	elseif action == "emit" then
		bse.emit({ t = "sound", name = "click", volume = 0.5 })
		return nil
	elseif action == "emit_reserved" then
		bse.emit({ t = "turn", seat = 1 })
		return nil
	elseif action == "emit_bad" then
		bse.emit({ name = "x" })
		return nil
	elseif action == "over" then
		bse.over({ winner = seat, reason = "five", cells = { { 1, 1 }, { 2, 2 } } })
		return nil
	elseif action == "draw" then
		bse.over({ reason = "board full" })
		return nil
	elseif action == "over_nil" then
		bse.over()
		return nil
	elseif action == "deepcopy" then
		local a = { x = { y = { 1, 2, 3 } } }
		local b = bse.deepcopy(a)
		b.x.y[1] = 99
		bse.status(`{a.x.y[1]}/{b.x.y[1]}`)
		return nil
	elseif action == "get_copy" then
		local object = bse.view.get(saved)
		assert(object ~= nil)
		object.x = 100
		local props = object.props
		assert(props ~= nil)
		props.color = "green"
		local again = bse.view.get(saved)
		assert(again ~= nil and again.props ~= nil)
		bse.status(`{again.x},{again.props.color}`)
		return nil
	elseif action == "spawn_bad" then
		bse.view.spawn("piece", "x" :: any, 1)
		return nil
	elseif action == "spawn_bad_kind" then
		bse.view.spawn(5 :: any, 1, 1)
		return nil
	elseif action == "clear" then
		bse.view.clear()
		return nil
	elseif action == "turn_nan" then
		bse.turn(0 / 0)
		return nil
	elseif action == "turn_str" then
		bse.turn("a" :: any)
		return nil
	end
	return bse.reject("UNKNOWN", "unknown action", { action = action })
end

function rules.snapshot(): any
	return { saved = saved }
end

function rules.restore(data: any)
	saved = data.saved
end

return rules
