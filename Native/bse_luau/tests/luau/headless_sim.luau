--!strict
local rules = {}
local mode = "win"

function rules.new_game(options: { [string]: any })
	mode = if type(options.mode) == "string" then options.mode else "win"
	bse.turn(1)
end

function rules.command(seat: number, cmd: { [string]: any }): any
	if cmd.stall == true then
		return nil
	end
	if cmd.draw == true then
		bse.over({ reason = "draw" })
		return nil
	end
	if cmd.ok == true then
		bse.over({ winner = seat })
		return nil
	end
	return bse.reject("BAD", "bad command")
end

function rules.random_command(seat: number): any
	if mode == "none" then
		return nil
	elseif mode == "stall" then
		return { stall = true }
	elseif mode == "draw" then
		return { draw = true }
	end
	return { ok = mode == "win" and bse.is_headless() }
end

return rules
