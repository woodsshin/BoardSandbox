--!strict
local rules = {}

local scores = { 0, 0 }
local current = 1

function rules.new_game(options: { [string]: any })
	scores = { 0, 0 }
	current = 1
	bse.turn(1)
end

function rules.command(seat: number, cmd: { [string]: any }): any
	if seat ~= current then
		return bse.reject("NOT_YOUR_TURN", "not your turn")
	end
	local amount = cmd.amount
	if type(amount) ~= "number" then
		return bse.reject("BAD_AMOUNT", "amount must be a number")
	end
	scores[seat] += amount
	if scores[seat] >= 10 then
		bse.over({ winner = seat })
		return nil
	end
	current = 3 - seat
	bse.turn(current)
	return nil
end

function rules.random_command(seat: number): any
	return { action = "add", amount = math.random(1, 3) }
end

function rules.snapshot(): any
	return { scores = scores, current = current }
end

function rules.restore(data: any)
	scores = data.scores
	current = data.current
end

return rules
