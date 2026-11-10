--!nocheck
local other = require("./other")
local util = {}
function util.answer()
	return 40 + other.two
end
return util
