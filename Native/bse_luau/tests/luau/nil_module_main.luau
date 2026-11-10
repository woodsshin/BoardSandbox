--!nocheck
local first = require("./lib/nil_module")
local second = require("./lib/nil_module")
local rules = {}

function rules.new_game()
	bse.turn(1)
end

function rules.command() end

function rules.query(name)
	return { first = first, second = second }
end

return rules
