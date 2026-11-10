--!nocheck
local rules = {}

function rules.new_game()
	bse.turn(1)
end

function rules.command(seat, cmd)
	local a = cmd.action
	if a == "spin" then
		while true do
		end
	elseif a == "spin_pcall" then
		while true do
			pcall(function()
				while true do
				end
			end)
		end
	elseif a == "spin_recursive" then
		local function f()
			while true do
				pcall(f)
			end
		end
		f()
	elseif a == "hog" then
		local t = {}
		for i = 1, 1e8 do
			t[i] = { i }
		end
	elseif a == "bigstring" then
		local s = string.rep("x", 2 ^ 30)
	elseif a == "recurse" then
		local function r(n)
			return 1 + r(n + 1)
		end
		r(1)
	elseif a == "yield" then
		coroutine.yield(1)
	elseif a == "pattern" then
		string.find(string.rep("a", 28), string.rep("a*", 12) .. "b")
	elseif a == "gsub_spin" then
		string.gsub(string.rep("a", 20000), "a*a*a*a*b", "")
	elseif a == "alloc" then
		local t = {}
		for i = 1, 100000 do
			t[i] = i
		end
	end
end

function rules.query(name)
	if name == "probe" then
		return {
			os = os ~= nil,
			io = io ~= nil,
			loadstring = loadstring ~= nil,
			getfenv = getfenv ~= nil,
			setfenv = setfenv ~= nil,
			dofile = dofile ~= nil,
			require = type(require),
			debug_info = type(debug.info),
			write_global = pcall(function()
				newglobal = 1
			end),
			write_bse = pcall(function()
				bse.x = 1
			end),
			write_view = pcall(function()
				bse.view.spawn = nil
			end),
			write_string = pcall(function()
				string.len = nil
			end),
			write_math = pcall(function()
				math.floor = nil
			end),
			set_string_meta = pcall(function()
				getmetatable("").__index = {}
			end),
			rawset_g = pcall(rawset, _G, "x", 1),
			os_via_g = (_G.os ~= nil),
		}
	end
	return nil
end

return rules
