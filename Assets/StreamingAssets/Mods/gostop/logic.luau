--!strict
local rules = {}

local DEFAULT_SIZE = 15
local DEFAULT_WIN_LENGTH = 5
local MIN_SIZE = 3
local MAX_SIZE = 25

local DIRECTIONS = { { 1, 0 }, { 0, 1 }, { 1, 1 }, { 1, -1 } }

local size = DEFAULT_SIZE
local win_length = DEFAULT_WIN_LENGTH
local exact_win = false

local cells: { number } = {}
local empties: { number } = {}
local slots: { number } = {}
local current = 1
local moves = 0
local finished = false
local last_x = 0
local last_y = 0

local function clamp_integer(value: any, fallback: number, low: number, high: number): number
	if type(value) ~= "number" or value ~= value then
		return fallback
	end
	return math.clamp(math.floor(value), low, high)
end

local function rebuild_empties()
	table.clear(empties)
	table.clear(slots)
	for index = 1, size * size do
		if cells[index] == 0 then
			local slot = #empties + 1
			empties[slot] = index
			slots[index] = slot
		end
	end
end

local function reset_board()
	table.clear(cells)
	for index = 1, size * size do
		cells[index] = 0
	end
	rebuild_empties()
	current = 1
	moves = 0
	finished = false
	last_x = 0
	last_y = 0
end

local function occupy(index: number, seat: number)
	cells[index] = seat
	local slot = slots[index]
	local tail = #empties
	local moved = empties[tail]
	empties[slot] = moved
	slots[moved] = slot
	empties[tail] = nil
	slots[index] = nil
end

local function run_length(x: number, y: number, dx: number, dy: number, seat: number): number
	local count = 0
	local cx = x + dx
	local cy = y + dy
	while cx >= 1 and cx <= size and cy >= 1 and cy <= size and cells[(cy - 1) * size + cx] == seat do
		count += 1
		cx += dx
		cy += dy
	end
	return count
end

local function winning_line(x: number, y: number, seat: number): { { number } }?
	for _, direction in DIRECTIONS do
		local dx = direction[1]
		local dy = direction[2]
		local forward = run_length(x, y, dx, dy, seat)
		local backward = run_length(x, y, -dx, -dy, seat)
		local total = 1 + forward + backward
		if total == win_length or (total > win_length and not exact_win) then
			local line = {}
			for step = -backward, forward do
				table.insert(line, { x + dx * step, y + dy * step })
			end
			return line
		end
	end
	return nil
end

local function announce_board()
	bse.emit({ t = "board", columns = size, rows = size })
	if moves > 0 then
		bse.emit({ t = "last_move", x = last_x, y = last_y, seat = cells[(last_y - 1) * size + last_x] })
	end
end

function rules.new_game(options: { [string]: any })
	size = clamp_integer(options.size, DEFAULT_SIZE, MIN_SIZE, MAX_SIZE)
	win_length = clamp_integer(options.win_length, DEFAULT_WIN_LENGTH, MIN_SIZE, size)
	exact_win = options.exact_win == true
	reset_board()
	announce_board()
	bse.turn(1)
end

function rules.command(seat: number, cmd: { [string]: any }): any
	if cmd.action ~= "place_stone" then
		return bse.reject("UNKNOWN_ACTION", "unknown action", { action = cmd.action })
	end
	if finished then
		return bse.reject("GAME_OVER", "the game is over")
	end
	if seat ~= current then
		return bse.reject("NOT_YOUR_TURN", "it is not your turn")
	end
	local x = cmd.x
	local y = cmd.y
	if type(x) ~= "number" or type(y) ~= "number" or x ~= math.floor(x) or y ~= math.floor(y) then
		return bse.reject("INVALID_ARGUMENT", "x and y must be integers")
	end
	if x < 1 or x > size or y < 1 or y > size then
		return bse.reject("OUT_OF_BOUNDS", "the point is outside the board", { x = x, y = y, size = size })
	end
	local index = (y - 1) * size + x
	if cells[index] ~= 0 then
		return bse.reject("OCCUPIED", "that point is already taken", { x = x, y = y })
	end

	occupy(index, seat)
	moves += 1
	last_x = x
	last_y = y
	bse.view.spawn("stone", x, y, { owner = seat, props = { color = if seat == 1 then "black" else "white" } })
	bse.emit({ t = "last_move", x = x, y = y, seat = seat })

	local line = winning_line(x, y, seat)
	if line then
		finished = true
		bse.over({ winner = seat, reason = "connected", cells = line })
	elseif moves == size * size then
		finished = true
		bse.over({ reason = "draw" })
	else
		current = 3 - seat
		bse.turn(current)
	end
	return nil
end

function rules.random_command(seat: number): any
	local count = #empties
	if count == 0 then
		return nil
	end
	local index = empties[math.random(count)] - 1
	return { action = "place_stone", x = index % size + 1, y = index // size + 1 }
end

function rules.legal_commands(seat: number): { any }
	local list = {}
	if finished or seat ~= current then
		return list
	end
	for _, index in empties do
		local offset = index - 1
		table.insert(list, { action = "place_stone", x = offset % size + 1, y = offset // size + 1 })
	end
	return list
end

function rules.query(name: string, args: any): any
	if name == "state" then
		return {
			size = size,
			win_length = win_length,
			exact_win = exact_win,
			current = current,
			moves = moves,
			finished = finished,
			board = table.concat(cells),
			last = if moves > 0 then { x = last_x, y = last_y } else nil,
		}
	end
	return nil
end

function rules.snapshot(): any
	return {
		size = size,
		win_length = win_length,
		exact_win = exact_win,
		board = table.concat(cells),
		current = current,
		moves = moves,
		finished = finished,
		last_x = last_x,
		last_y = last_y,
	}
end

function rules.restore(data: any)
	size = clamp_integer(data.size, DEFAULT_SIZE, MIN_SIZE, MAX_SIZE)
	win_length = clamp_integer(data.win_length, DEFAULT_WIN_LENGTH, MIN_SIZE, size)
	exact_win = data.exact_win == true
	local board = data.board
	if type(board) ~= "string" or #board ~= size * size then
		error(bse.reject("BAD_SNAPSHOT", "board does not match the board size"), 0)
	end
	table.clear(cells)
	for index = 1, size * size do
		local value = string.byte(board, index) - 48
		if value < 0 or value > 2 then
			error(bse.reject("BAD_SNAPSHOT", "board contains an invalid cell"), 0)
		end
		cells[index] = value
	end
	rebuild_empties()
	current = clamp_integer(data.current, 1, 1, 2)
	moves = clamp_integer(data.moves, 0, 0, size * size)
	finished = data.finished == true
	last_x = clamp_integer(data.last_x, 0, 0, size)
	last_y = clamp_integer(data.last_y, 0, 0, size)
	announce_board()
end

return rules
