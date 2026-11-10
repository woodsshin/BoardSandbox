--!nocheck
local util = require("./lib/util")
local pkg = require("./lib/pkg")
local rules = {}

function rules.new_game()
	bse.turn(1)
end

function rules.command(seat, cmd) end

function rules.query(name)
	if name == "answer" then
		return util.answer() + pkg.value
	elseif name == "tail" then
		local function f()
			return require("./lib/util")
		end
		return f().answer()
	elseif name == "absolute" then
		return require("lib/util")
	elseif name == "escape" then
		return require("../../etc/passwd")
	elseif name == "missing" then
		return require("./lib/nothing")
	elseif name == "cached" then
		return util == require("./lib/util")
	end
	return nil
end

return rules
