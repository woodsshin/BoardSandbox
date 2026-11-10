--!nocheck
local rules = {}

function rules.new_game()
	bse.turn(1)
end

function rules.command(seat, cmd)
	local a = cmd.action
	if a == "levels" then
		bse.log("a", 1, nil, true)
		bse.warn("w")
		bse.err("e")
		print("p", 2.5)
	elseif a == "long" then
		print(string.rep("x", 10000))
	elseif a == "flood" then
		for _ = 1, 5000 do
			print("x")
		end
	elseif a == "badutf8" then
		print("a\255b")
	elseif a == "values" then
		print(nil, false, 12, "s")
	end
end

return rules
