--!nocheck
local rules = {}

function rules.new_game()
	bse.turn(1)
end

function rules.command() end

function rules.query(name)
	if name == "holes" then
		return { [1] = 1, [3] = 3 }
	elseif name == "mixed" then
		return { 1, a = 2 }
	elseif name == "nan" then
		return 0 / 0
	elseif name == "inf" then
		return math.huge
	elseif name == "cyclic" then
		local t = {}
		t.t = t
		return t
	elseif name == "badutf8" then
		return "\255"
	elseif name == "badkey" then
		return { ["\255"] = 1 }
	elseif name == "negkey" then
		return { [-1] = 1 }
	elseif name == "floatkey" then
		return { [1.5] = 1 }
	elseif name == "boolkey" then
		return { [true] = 1 }
	elseif name == "nested" then
		return { a = { b = { c = function() end } } }
	elseif name == "big" then
		return string.rep("x", 17 * 1024 * 1024)
	elseif name == "thread" then
		return coroutine.create(function() end)
	elseif name == "buffer" then
		return buffer.create(4)
	elseif name == "vector" then
		return vector.create(1, 2, 3)
	elseif name == "null" then
		return bse.json.null
	elseif name == "nulls" then
		return { bse.json.null, 1, bse.json.null }
	elseif name == "roundtrip" then
		return bse.json.decode(bse.json.encode({ a = { 1, 2, { b = true } }, s = "x" }))
	elseif name == "decode_error" then
		return bse.json.decode("{")
	elseif name == "encode_error" then
		return bse.json.encode({ f = print })
	elseif name == "numbers" then
		return { 0.5, -0.25, 1 / 3, 2 ^ 53, 2 ^ 63, 1e300, 5e-324, 123456789012, -7 }
	end
	return nil
end

return rules
