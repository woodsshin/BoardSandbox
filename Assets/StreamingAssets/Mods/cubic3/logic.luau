--!strict
-- Cubic 3x3, a puzzle for one player, shown as a cube of 26 little cubes that turn in space. How a cube works is in
-- cube.luau, this file is the game around it: the stage, the commands and what is drawn.
local Cube = require("./cube")

type Move = Cube.Move
type Cubie = Cube.Cubie

local rules = {}

local SEAT = 1
local DEFAULT_SCRAMBLE = 25
local MAX_SCRAMBLE = 200
local MAX_SEQUENCE = 500
local QUARTER_SECONDS = 0.22
local HALF_SECONDS = 0.32

local cubies: { Cubie } = Cube.new_cubies()
local ids: { number } = {} -- the drawn object of every cubie
local index_by_id: { [number]: number } = {}
local cells: { number } = Cube.solved() -- the color in every slot
local moves = 0
local finished = false
local scramble_text = ""

local function clamp_integer(value: any, fallback: number, low: number, high: number): number
	if type(value) ~= "number" or value ~= value then
		return fallback
	end
	return math.clamp(math.floor(value), low, high)
end

local function moves_text(count: number): string
	return if count == 1 then "1 move" else `{count} moves`
end

-- Everything on screen besides the cubies: no board, no table, a camera that circles the cube.
local function announce_stage()
	bse.emit({
		t = "stage",
		background = "#10131a",
		board = { visible = false },
		table = { visible = false },
		light = { direction = { -0.4, 0.8, 0.55 }, ambient = 0.5, intensity = 0.6 },
		camera = {
			yaw = 32,
			pitch = 26,
			radius = 3.1,
			free_pitch = true,
			left_drag = "orbit",
			right_drag = "orbit",
			middle_drag = "none",
		},
	})
end

local function index_ids()
	index_by_id = {}
	for index, id in ids do
		index_by_id[id] = index
	end
end

local function spawn_cubies()
	ids = {}
	for index, cubie in cubies do
		ids[index] = bse.view.spawn("cubie", 0, 0, {
			props = { pos = cubie.pos, rot = cubie.rot, faces = Cube.cubie_faces(cubie.home) },
		})
	end
	index_ids()
end

-- Plays moves that are known to be fine: every one turns its layer on screen, one after the other.
local function perform(sequence: { Move })
	for _, move in sequence do
		local moved = Cube.turn_cubies(cubies, move)
		if not bse.is_headless() then
			local turning: { number } = {}
			for slot, index in moved do
				turning[slot] = ids[index]
			end
			bse.emit({
				t = "fx",
				kind = "rotate",
				ids = turning,
				axis = Cube.normal(move.face),
				pivot = { 0, 0, 0 },
				degrees = if move.turns == 3 then 90 else -90 * move.turns,
				seconds = if move.turns == 2 then HALF_SECONDS else QUARTER_SECONDS,
			})
			for _, index in moved do
				bse.view.set(ids[index], { pos = cubies[index].pos, rot = cubies[index].rot })
			end
		end
	end
	cells = Cube.cubie_cells(cubies) :: { number }

	moves += #sequence
	if Cube.is_solved(cells) then
		finished = true
		bse.over({ winner = SEAT, reason = "solved in " .. moves_text(moves) })
		bse.status("Solved in " .. moves_text(moves) .. "!")
	end
end

local function clicked_cubie(cmd: { [string]: any }): (Cubie?, number?)
	local index = if type(cmd.id) == "number" then index_by_id[cmd.id] else nil
	if index == nil or type(cmd.normal) ~= "table" then
		return nil, nil
	end
	return cubies[index], Cube.face_of(cmd.normal)
end

-- A drag along a sticker turns the layer that the sticker would move in. The layer turns around the axis that is
-- square to both the sticker and the drag.
local function move_for_drag(cubie: Cubie, face: number, drag: any): (Move?, string?)
	if type(drag) ~= "table" then
		return nil, "drag must be a direction"
	end
	local n = Cube.normal(face)
	local d = { drag[1], drag[2], drag[3] }
	for axis = 1, 3 do
		if type(d[axis]) ~= "number" or d[axis] ~= d[axis] then
			return nil, "drag must be a direction"
		end
	end
	local cross = { n[2] * d[3] - n[3] * d[2], n[3] * d[1] - n[1] * d[3], n[1] * d[2] - n[2] * d[1] }
	local axis = 1
	for candidate = 2, 3 do
		if math.abs(cross[candidate]) > math.abs(cross[axis]) then
			axis = candidate
		end
	end
	if math.abs(cross[axis]) < 0.5 then
		return nil, "the drag has no clear direction"
	end
	local layer = cubie.pos[axis]
	if layer == 0 then
		return nil, "only the outer layers turn"
	end
	local outward = { 0, 0, 0 }
	outward[axis] = layer
	local turned = Cube.face_of(outward) :: number
	-- Where the sticker goes when the layer turns clockwise, compared with where the drag goes.
	local p = { cubie.pos[1] + n[1] * 0.5, cubie.pos[2] + n[2] * 0.5, cubie.pos[3] + n[3] * 0.5 }
	local a = { -outward[1], -outward[2], -outward[3] }
	local v = { a[2] * p[3] - a[3] * p[2], a[3] * p[1] - a[1] * p[3], a[1] * p[2] - a[2] * p[1] }
	local along = v[1] * d[1] + v[2] * d[2] + v[3] * d[3]
	return Cube.move(turned, if along >= 0 then 1 else 3), nil
end

function rules.new_game(options: { [string]: any })
	local count = clamp_integer(options.scramble, DEFAULT_SCRAMBLE, 1, MAX_SCRAMBLE)
	local seed = clamp_integer(options.seed, 0, 0, 2147483647)
	if seed > 0 then
		math.randomseed(seed)
	end

	local sequence: { Move } = {}
	local attempts = 0
	repeat
		sequence = Cube.scramble(count)
		cubies = Cube.new_cubies()
		for _, move in sequence do
			Cube.turn_cubies(cubies, move)
		end
		cells = Cube.cubie_cells(cubies) :: { number }
		attempts += 1
	until not Cube.is_solved(cells) or attempts >= 20

	moves = 0
	finished = false
	scramble_text = Cube.sequence_text(sequence)

	announce_stage()
	spawn_cubies()
	bse.turn(SEAT)
end

function rules.command(seat: number, cmd: { [string]: any }): any
	local action = cmd.action
	if action ~= "turn_face" and action ~= "turn_drag" and action ~= "move" and action ~= "alg" then
		return bse.reject("UNKNOWN_ACTION", "unknown action", { action = action })
	end
	if finished then
		return bse.reject("GAME_OVER", "the cube is solved already")
	end

	if action == "turn_face" or action == "turn_drag" then
		local cubie, face = clicked_cubie(cmd)
		if cubie == nil or face == nil then
			return bse.reject("NO_FACE", "that is not a face of the cube")
		end
		if action == "turn_face" then
			local counter = cmd.button == 1 or cmd.shift == true
			perform({ Cube.move(face, if counter then 3 else 1) })
			return nil
		end
		local move, problem = move_for_drag(cubie, face, cmd.drag)
		if move == nil then
			return bse.reject("NO_TURN", problem or "nothing turns that way")
		end
		perform({ move })
		return nil
	end

	if action == "move" then
		local text = cmd.move
		if type(text) ~= "string" then
			return bse.reject("INVALID_ARGUMENT", "move must be a string like R, R' or R2")
		end
		local move = Cube.parse(text)
		if move == nil then
			return bse.reject("BAD_MOVE", "not a move: " .. string.sub(text, 1, 20))
		end
		perform({ move })
		return nil
	end

	local text = cmd.moves
	if type(text) ~= "string" then
		return bse.reject("INVALID_ARGUMENT", "moves must be a string like R U R' U'")
	end
	local sequence, bad = Cube.parse_sequence(text)
	if sequence == nil then
		return bse.reject("BAD_MOVE", "not a move: " .. string.sub(tostring(bad), 1, 20))
	end
	if #sequence == 0 then
		return bse.reject("INVALID_ARGUMENT", "there is no move in moves")
	end
	if #sequence > MAX_SEQUENCE then
		return bse.reject("TOO_LONG", `at most {MAX_SEQUENCE} moves at once`)
	end
	perform(sequence)
	return nil
end

function rules.legal_commands(seat: number): { any }
	local list = {}
	if finished then
		return list
	end
	for _, move in Cube.all_moves() do
		table.insert(list, { action = "move", move = move.name })
	end
	return list
end

function rules.random_command(seat: number): any
	if finished then
		return nil
	end
	local all = Cube.all_moves()
	return { action = "move", move = all[math.random(#all)].name }
end

function rules.query(name: string, args: any): any
	if name == "state" then
		return {
			moves = moves,
			solved = finished,
			scramble = scramble_text,
			facelets = Cube.facelets(cells),
			faces = Cube.faces(cells),
		}
	elseif name == "net" then
		return { rows = Cube.net(cells) }
	elseif name == "cubies" then
		local list = {}
		for index, cubie in cubies do
			list[index] = { id = ids[index], home = cubie.home, pos = cubie.pos, rot = cubie.rot }
		end
		return { cubies = list, cells = table.concat(cells) }
	end
	return nil
end

function rules.snapshot(): any
	local list = {}
	for index, cubie in cubies do
		list[index] = { id = ids[index], home = cubie.home, pos = cubie.pos, rot = cubie.rot }
	end
	return {
		cubies = list,
		moves = moves,
		finished = finished,
		scramble = scramble_text,
	}
end

local function is_place(value: any): boolean
	if type(value) ~= "table" or #value ~= 3 then
		return false
	end
	for axis = 1, 3 do
		local part = value[axis]
		if part ~= -1 and part ~= 0 and part ~= 1 then
			return false
		end
	end
	return true
end

function rules.restore(data: any)
	if type(data) ~= "table" or type(data.cubies) ~= "table" or #data.cubies ~= 26 then
		error(bse.reject("BAD_SNAPSHOT", "the snapshot does not describe a cube"), 0)
	end

	local restored: { Cubie } = {}
	local restored_ids: { number } = {}
	local seen: { [number]: boolean } = {}
	for index, entry in data.cubies :: { any } do
		if type(entry) ~= "table" or type(entry.id) ~= "number" or seen[entry.id] or bse.view.get(entry.id) == nil then
			error(bse.reject("BAD_SNAPSHOT", "the cube contains an invalid piece"), 0)
		end
		if not is_place(entry.home) or not is_place(entry.pos) or not Cube.is_rotation(entry.rot) then
			error(bse.reject("BAD_SNAPSHOT", "the cube contains an invalid piece"), 0)
		end
		seen[entry.id] = true
		restored[index] = { home = table.clone(entry.home), pos = table.clone(entry.pos), rot = table.clone(entry.rot) }
		restored_ids[index] = entry.id
	end
	local restored_cells = Cube.cubie_cells(restored)
	if restored_cells == nil then
		error(bse.reject("BAD_SNAPSHOT", "the pieces do not form a cube"), 0)
	end

	cubies = restored
	ids = restored_ids
	index_ids()
	cells = restored_cells :: { number }
	moves = clamp_integer(data.moves, 0, 0, 1000000000)
	finished = data.finished == true
	scramble_text = if type(data.scramble) == "string" then data.scramble else ""
	announce_stage()
end

return rules
