--!strict
local rules = {}

local counter = 0

function rules.new_game(options: { [string]: any })
	counter = 0
	bse.turn(1)
end

function rules.command(seat: number, cmd: { [string]: any }): any
	local action = cmd.action
	if action == "fail" then
		return bse.reject("NOPE", "not allowed")
	elseif action == "boom" then
		error("boom")
	elseif action == "boom_table" then
		error({ code = "CUSTOM", message = "custom failure" })
	elseif action == "reject_after_emit" then
		bse.status("hello")
		return bse.reject("LATE", "too late")
	elseif action == "log" then
		print("hello", 1, true)
		bse.warn("w")
		return nil
	end
	counter += 1
	bse.view.spawn("stone", cmd.x, cmd.y, { owner = seat })
	bse.turn(3 - seat)
	return nil
end

function rules.query(name: string, args: any): any
	if name == "echo" then
		return args
	elseif name == "func" then
		return function() end
	elseif name == "counter" then
		return counter
	end
	return nil
end

function rules.snapshot(): any
	return { counter = counter }
end

function rules.restore(data: any)
	counter = data.counter
end

return rules
