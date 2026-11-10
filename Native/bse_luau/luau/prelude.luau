--!strict

type Props = { [string]: any }
type Event = { [string]: any }
type RuleError = { code: string, message: string, details: Props? }
type ViewObject = { id: number, kind: string, x: number, y: number, owner: number?, props: Props? }
type SpawnOptions = { owner: number?, props: Props? }
type OverInfo = { winner: number?, reason: string?, cells: { { number } }? }

type Native = {
	log: (level: number, ...any) -> (),
	encode: (value: any) -> string,
	decode: (text: string) -> any,
	load: (path: string) -> (any, string?),
	loadstring: (name: string, source: string) -> (any, string?),
	exists: (path: string) -> boolean,
	null: any,
	dev: boolean,
	luau_version: string,
}

local native = (...) :: Native

local rules: any = nil
local events: { Event } = {}
local headless = false
local turn = 0
local over: OverInfo? = nil
local objects: { [number]: ViewObject } = {}
local next_id = 1

local RESERVED_EVENTS: { [string]: boolean } = {
	reset = true,
	spawn = true,
	move = true,
	set = true,
	remove = true,
	turn = true,
	over = true,
	status = true,
}

local function raise(code: string, message: string): never
	error({ code = code, message = message }, 0)
end

local function check_number(value: any, name: string): number
	if type(value) ~= "number" or value ~= value then
		raise("BAD_ARGUMENT", `{name} must be a number`)
	end
	return value
end

local function emit(event: Event)
	if not headless then
		events[#events + 1] = event
	end
end

local function take_events(): { Event }
	local taken = events
	events = {}
	return taken
end

local function deepcopy(value: any, level: number?): any
	if type(value) ~= "table" then
		return value
	end
	local depth = level or 0
	if depth > 64 then
		raise("TOO_DEEP", "deepcopy exceeds 64 levels")
	end
	local copy: { [any]: any } = {}
	for key, item in value :: { [any]: any } do
		copy[key] = deepcopy(item, depth + 1)
	end
	return copy
end

local view = {}

function view.spawn(kind: string, x: number, y: number, opts: SpawnOptions?): number
	local id = next_id
	next_id += 1
	if headless then
		return id
	end
	if type(kind) ~= "string" then
		raise("BAD_ARGUMENT", "kind must be a string")
	end
	check_number(x, "x")
	check_number(y, "y")
	local owner: number? = nil
	local props: Props? = nil
	if opts then
		owner = opts.owner
		if opts.props then
			props = deepcopy(opts.props)
		end
	end
	objects[id] = { id = id, kind = kind, x = x, y = y, owner = owner, props = props }
	emit({ t = "spawn", id = id, kind = kind, x = x, y = y, owner = owner, props = if props then deepcopy(props) else nil })
	return id
end

function view.move(id: number, x: number, y: number)
	if headless then
		return
	end
	local object = objects[id]
	if not object then
		raise("NO_SUCH_OBJECT", `view object {id} does not exist`)
	end
	check_number(x, "x")
	check_number(y, "y")
	object.x = x
	object.y = y
	emit({ t = "move", id = id, x = x, y = y })
end

function view.set(id: number, changes: Props)
	if headless then
		return
	end
	local object = objects[id]
	if not object then
		raise("NO_SUCH_OBJECT", `view object {id} does not exist`)
	end
	if type(changes) ~= "table" then
		raise("BAD_ARGUMENT", "changes must be a table")
	end
	local props: Props = object.props or {}
	object.props = props
	for key, value in changes do
		props[key] = deepcopy(value)
	end
	emit({ t = "set", id = id, props = deepcopy(changes) })
end

function view.remove(id: number)
	if headless then
		return
	end
	if not objects[id] then
		raise("NO_SUCH_OBJECT", `view object {id} does not exist`)
	end
	objects[id] = nil
	emit({ t = "remove", id = id })
end

function view.get(id: number): ViewObject?
	local object = objects[id]
	if not object then
		return nil
	end
	local props = object.props
	return {
		id = object.id,
		kind = object.kind,
		x = object.x,
		y = object.y,
		owner = object.owner,
		props = if props then deepcopy(props) else nil,
	}
end

function view.clear()
	table.clear(objects)
	next_id = 1
	emit({ t = "reset" })
end

table.freeze(view)

local function dump_view(): { ViewObject }
	local ids: { number } = {}
	for id in objects do
		ids[#ids + 1] = id
	end
	table.sort(ids)
	local list: { ViewObject } = {}
	for _, id in ids do
		local object = objects[id]
		local props = object.props
		list[#list + 1] = {
			id = object.id,
			kind = object.kind,
			x = object.x,
			y = object.y,
			owner = object.owner,
			props = if props then table.clone(props) else nil,
		}
	end
	return list
end

local function load_view(list: any, next_value: any)
	if type(list) ~= "table" then
		raise("BAD_SNAPSHOT", "snapshot.view must be an array")
	end
	view.clear()
	local highest = 0
	for _, item in list :: { any } do
		if type(item) ~= "table" then
			raise("BAD_SNAPSHOT", "snapshot.view contains a non-object")
		end
		local entry = item :: { [string]: any }
		local id = check_number(entry.id, "view.id")
		local x = check_number(entry.x, "view.x")
		local y = check_number(entry.y, "view.y")
		if type(entry.kind) ~= "string" then
			raise("BAD_SNAPSHOT", "view.kind must be a string")
		end
		local props = if type(entry.props) == "table" then entry.props :: Props else nil
		local owner = if type(entry.owner) == "number" then entry.owner :: number else nil
		objects[id] = { id = id, kind = entry.kind, x = x, y = y, owner = owner, props = props }
		emit({ t = "spawn", id = id, kind = entry.kind, x = x, y = y, owner = owner, props = if props then deepcopy(props) else nil })
		highest = math.max(highest, id)
	end
	next_id = if type(next_value) == "number" then next_value else highest + 1
end

local function set_turn(seat: number)
	turn = check_number(seat, "seat")
	emit({ t = "turn", seat = turn })
end

local function set_over(info: OverInfo?)
	local winner = if info and type(info.winner) == "number" then info.winner else 0
	local cells = if info and info.cells then deepcopy(info.cells) else nil
	local result: OverInfo = {
		winner = winner,
		reason = if info then info.reason else nil,
		cells = cells,
	}
	over = result
	emit({ t = "over", winner = winner, reason = result.reason, cells = if cells then deepcopy(cells) else nil })
end

local function emit_custom(event: Event)
	if type(event) ~= "table" or type(event.t) ~= "string" then
		raise("BAD_ARGUMENT", "event must be a table with a string field 't'")
	end
	if RESERVED_EVENTS[event.t] then
		raise("BAD_ARGUMENT", `event type '{event.t}' is reserved`)
	end
	emit(deepcopy(event))
end

local function status(text: any, level: string?)
	emit({ t = "status", text = tostring(text), level = level or "info" })
end

local function reject(code: string, message: string, details: Props?): RuleError
	return { code = code, message = message, details = details }
end

local api = {
	view = view,
	turn = set_turn,
	over = set_over,
	emit = emit_custom,
	status = status,
	reject = reject,
	deepcopy = deepcopy,
	is_headless = function(): boolean
		return headless
	end,
	log = function(...: any)
		native.log(0, ...)
	end,
	warn = function(...: any)
		native.log(1, ...)
	end,
	err = function(...: any)
		native.log(2, ...)
	end,
	json = table.freeze({
		encode = native.encode,
		decode = native.decode,
		null = native.null,
	}),
}
table.freeze(api)

local modules_loaded: { [string]: any } = {}
local modules_loading: { [string]: boolean } = {}

local function load_module(path: string): any
	local cached = modules_loaded[path]
	if cached ~= nil then
		return cached
	end
	if modules_loading[path] then
		raise("CYCLIC_REQUIRE", `cyclic require of '{path}'`)
	end
	local fn, message = native.load(path)
	if fn == nil then
		raise("COMPILE_ERROR", message or `cannot load '{path}'`)
	end
	modules_loading[path] = true
	local ok, result = pcall(fn :: () -> any)
	modules_loading[path] = nil
	if not ok then
		error(result, 0)
	end
	if result == nil then
		result = true
	end
	modules_loaded[path] = result
	return result
end

local function resolve_module(from: string, spec: string): string
	if string.sub(spec, 1, 2) ~= "./" and string.sub(spec, 1, 3) ~= "../" then
		raise("BAD_REQUIRE", `require path '{spec}' must start with './' or '../'`)
	end
	local segments: { string } = {}
	for segment in string.gmatch(from, "[^/]+") do
		segments[#segments + 1] = segment
	end
	segments[#segments] = nil
	for segment in string.gmatch(spec, "[^/]+") do
		if segment == ".." then
			if #segments == 0 then
				raise("BAD_REQUIRE", `require path '{spec}' escapes the mod root`)
			end
			segments[#segments] = nil
		elseif segment ~= "." then
			segments[#segments + 1] = segment
		end
	end
	local base = table.concat(segments, "/")
	if native.exists(base .. ".luau") then
		return base .. ".luau"
	end
	if native.exists(base .. "/init.luau") then
		return base .. "/init.luau"
	end
	return raise("BAD_REQUIRE", `module '{spec}' was not found (looked for {base}.luau and {base}/init.luau)`)
end

local function require_module(spec: string): any
	if type(spec) ~= "string" then
		raise("BAD_REQUIRE", "require expects a string path")
	end
	local from = debug.info(2, "s")
	if from == nil or from == "[C]" then
		raise("BAD_REQUIRE", "require cannot determine the calling module")
	end
	return load_module(resolve_module(from, spec))
end

local function normalize_error(err: any): RuleError
	if type(err) == "table" and type(err.code) == "string" then
		return {
			code = err.code,
			message = if type(err.message) == "string" then err.message else err.code,
			details = if type(err.details) == "table" and next(err.details) ~= nil then err.details else nil,
		}
	end
	return { code = "REJECTED", message = tostring(err) }
end

local function reset_session()
	view.clear()
	turn = 0
	over = nil
end

local handlers: { [string]: (args: any) -> any } = {}

handlers.ping = function(_args: any): any
	return { ok = true, luau = native.luau_version }
end

handlers.boot = function(args: any): any
	local entry = args.entry
	if type(entry) ~= "string" then
		raise("BAD_ARGUMENT", "entry must be a string")
	end
	local module = load_module(entry)
	if type(module) ~= "table" then
		raise("BAD_MODULE", "the entry module must return a table")
	end
	local candidate = module :: { [string]: any }
	for _, name in { "new_game", "command" } do
		if type(candidate[name]) ~= "function" then
			raise("BAD_MODULE", `rules.{name} must be a function`)
		end
	end
	rules = candidate
	return { ok = true }
end

handlers.new_game = function(args: any): any
	local options = if type(args.options) == "table" then args.options else {}
	reset_session()
	rules.new_game(options)
	return { ok = true, events = take_events() }
end

handlers.command = function(args: any): any
	local seat = check_number(args.seat, "seat")
	local cmd = args.cmd
	if type(cmd) ~= "table" then
		raise("BAD_ARGUMENT", "cmd must be an object")
	end
	events = {}
	local err = rules.command(seat, cmd)
	if err == nil or err == true then
		return { ok = true, events = take_events() }
	end
	if #events > 0 then
		events = {}
		raise("INVALID_REJECTION", "a command was rejected after it emitted events; validate before changing state")
	end
	return { ok = false, error = normalize_error(err) }
end

handlers.snapshot = function(_args: any): any
	if type(rules.snapshot) ~= "function" then
		raise("NOT_SUPPORTED", "rules.snapshot is not implemented")
	end
	local data = {
		v = 1,
		mod = rules.snapshot(),
		view = dump_view(),
		next_id = next_id,
		turn = turn,
		over = over,
	}
	return { ok = true, snapshot = native.encode(data) }
end

handlers.restore = function(args: any): any
	if type(rules.restore) ~= "function" then
		raise("NOT_SUPPORTED", "rules.restore is not implemented")
	end
	local text = args.snapshot
	if type(text) ~= "string" then
		raise("BAD_ARGUMENT", "snapshot must be a string")
	end
	local ok, decoded = pcall(native.decode, text)
	if not ok or type(decoded) ~= "table" then
		raise("BAD_SNAPSHOT", "snapshot is not valid JSON")
	end
	local snapshot = decoded :: { [string]: any }
	if snapshot.v ~= 1 then
		raise("BAD_SNAPSHOT", "unsupported snapshot version")
	end
	events = {}
	load_view(snapshot.view, snapshot.next_id)
	turn = if type(snapshot.turn) == "number" then snapshot.turn else 0
	over = if type(snapshot.over) == "table" then snapshot.over :: OverInfo else nil
	rules.restore(snapshot.mod)
	if turn > 0 then
		emit({ t = "turn", seat = turn })
	end
	local finished: OverInfo? = over
	if finished then
		emit({ t = "over", winner = finished.winner, reason = finished.reason, cells = finished.cells })
	end
	return { ok = true, events = take_events() }
end

handlers.query = function(args: any): any
	local name = args.name
	if type(name) ~= "string" then
		raise("BAD_ARGUMENT", "name must be a string")
	end
	if name == "view" then
		return { ok = true, value = dump_view() }
	end
	if name == "session" then
		return { ok = true, value = { turn = turn, over = over } }
	end
	if type(rules.query) ~= "function" then
		raise("NOT_SUPPORTED", "rules.query is not implemented")
	end
	return { ok = true, value = rules.query(name, args.args) }
end

handlers.legal = function(args: any): any
	if type(rules.legal_commands) ~= "function" then
		raise("NOT_SUPPORTED", "rules.legal_commands is not implemented")
	end
	local seat = check_number(args.seat, "seat")
	return { ok = true, commands = rules.legal_commands(seat) }
end

handlers.capabilities = function(_args: any): any
	return {
		ok = true,
		snapshot = type(rules.snapshot) == "function" and type(rules.restore) == "function",
		legal = type(rules.legal_commands) == "function",
		random = type(rules.random_command) == "function" or type(rules.legal_commands) == "function",
		query = type(rules.query) == "function",
	}
end

handlers.random = function(args: any): any
	local seat = check_number(args.seat, "seat")
	if type(args.seed) == "number" then
		math.randomseed(args.seed)
	end
	local command: any = nil
	if type(rules.random_command) == "function" then
		local pick = rules.random_command :: (number) -> any
		command = pick(seat)
	elseif type(rules.legal_commands) == "function" then
		local list_moves = rules.legal_commands :: (number) -> { any }
		local candidates = list_moves(seat)
		if #candidates > 0 then
			command = candidates[math.random(#candidates)]
		end
	else
		raise("NOT_SUPPORTED", "random needs rules.random_command or rules.legal_commands")
	end
	return { ok = true, command = command }
end

handlers.simulate = function(args: any): any
	local pick: ((number) -> any)? = nil
	if type(rules.random_command) == "function" then
		pick = rules.random_command :: (number) -> any
	end
	local list_moves: ((number) -> { any })? = nil
	if type(rules.legal_commands) == "function" then
		list_moves = rules.legal_commands :: (number) -> { any }
	end
	if pick == nil and list_moves == nil then
		raise("NOT_SUPPORTED", "simulate needs rules.random_command or rules.legal_commands")
	end
	local games = math.clamp(math.floor(tonumber(args.games) or 100), 1, 1000000)
	local seed = math.floor(tonumber(args.seed) or 1)
	local max_moves = math.clamp(math.floor(tonumber(args.max_moves) or 10000), 1, 1000000)
	local options = if type(args.options) == "table" then args.options else {}

	local wins: { [string]: number } = {}
	local draws = 0
	local aborted = 0
	local rejected = 0
	local total_moves = 0

	headless = true
	for game = 1, games do
		math.randomseed(seed + game)
		reset_session()
		rules.new_game(options)
		if turn == 0 then
			raise("NOT_SUPPORTED", "simulate needs rules.new_game to call bse.turn")
		end
		local moves = 0
		while over == nil and moves < max_moves do
			local seat = turn
			local cmd: any = nil
			if pick ~= nil then
				cmd = pick(seat)
			elseif list_moves ~= nil then
				local candidates = list_moves(seat)
				if #candidates > 0 then
					cmd = candidates[math.random(#candidates)]
				end
			end
			if cmd == nil then
				break
			end
			if rules.command(seat, cmd) ~= nil then
				rejected += 1
				break
			end
			moves += 1
		end
		total_moves += moves
		local finished = over
		if finished == nil then
			aborted += 1
		elseif finished.winner ~= nil and finished.winner > 0 then
			local key = tostring(finished.winner)
			wins[key] = (wins[key] or 0) + 1
		else
			draws += 1
		end
	end
	headless = false
	reset_session()

	return {
		ok = true,
		stats = {
			games = games,
			wins = wins,
			draws = draws,
			aborted = aborted,
			rejected = rejected,
			moves = total_moves,
		},
	}
end

handlers.eval = function(args: any): any
	if not native.dev then
		raise("DEV_ONLY", "eval is only available in dev mode")
	end
	local code = args.code
	if type(code) ~= "string" then
		raise("BAD_ARGUMENT", "code must be a string")
	end
	local fn, message = native.loadstring("eval", "local rules = ... return " .. code)
	if fn == nil then
		fn, message = native.loadstring("eval", "local rules = ... " .. code)
	end
	if fn == nil then
		raise("COMPILE_ERROR", tostring(message))
	end
	local results = table.pack((fn :: (any) -> ...any)(rules))
	local values: { string } = {}
	for i = 1, results.n do
		local ok, text = pcall(native.encode, results[i])
		values[i] = if ok then text else tostring(results[i])
	end
	return { ok = true, values = values }
end

local function on_error(err: any): any
	local trace = debug.traceback("", 2)
	if type(err) == "table" and type((err :: any).code) == "string" then
		local copy = table.clone(err :: { [string]: any })
		if type(copy.message) ~= "string" then
			copy.message = copy.code
		end
		copy.traceback = trace
		return copy
	end
	return { code = "SCRIPT_ERROR", message = tostring(err), traceback = trace }
end

local function dispatch(method: string, args: any): any
	events = {}
	local handler = handlers[method]
	if handler == nil then
		return { ok = false, error = { code = "NO_SUCH_METHOD", message = `unknown method '{method}'` } }
	end
	local ok, result = xpcall(handler, on_error, if args == nil then {} else args)
	if ok then
		return result
	end
	events = {}
	headless = false
	return { ok = false, fault = true, error = result }
end

return {
	dispatch = dispatch,
	bse = api,
	require = require_module,
}
