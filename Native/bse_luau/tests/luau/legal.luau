--!strict
local rules = {}
local pile = 0
local current = 1

function rules.new_game(options: { [string]: any })
	pile = if type(options.pile) == "number" then options.pile else 5
	current = 1
	bse.turn(1)
end

function rules.command(seat: number, cmd: { [string]: any }): any
	if seat ~= current then
		return bse.reject("NOT_YOUR_TURN", "not your turn")
	end
	local take = cmd.take
	if type(take) ~= "number" or take < 1 or take > 3 or take > pile then
		return bse.reject("BAD_TAKE", "take 1 to 3 from the pile")
	end
	pile -= take
	bse.view.spawn("take", pile, take)
	if pile == 0 then
		bse.over({ winner = seat })
		return nil
	end
	current = 3 - seat
	bse.turn(current)
	return nil
end

function rules.legal_commands(seat: number): { any }
	local list = {}
	if seat == current then
		for take = 1, math.min(3, pile) do
			table.insert(list, { take = take })
		end
	end
	return list
end

function rules.snapshot(): any
	return { pile = pile, current = current }
end

function rules.restore(data: any)
	pile = data.pile
	current = data.current
end

function rules.query(name: string, args: any): any
	if name == "headless" then
		return bse.is_headless()
	end
	return nil
end

return rules
