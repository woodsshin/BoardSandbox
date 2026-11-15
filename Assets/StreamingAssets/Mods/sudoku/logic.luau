--!strict
-- Sudoku for one player: put 1 to 9 in every row, column and 3x3 box exactly once.
--
-- Coordinates are x = column and y = row, both 1 to 9, row 1 on top. Every filled cell is a "digit" on the table
-- whose "label" is its digit; the clues of the puzzle are locked ("given"), the digits the player enters can be
-- changed and taken out again.
--
-- The table also holds what the player touches: a "cell" tile under every cell, which is tinted to point things
-- out and carries the notes of the cell as small labels, and below the grid a pad of "key"s, 1 to 9 and X.
-- Choosing a digit on the pad tints every cell that holds it strongly and every cell of their rows, columns and
-- boxes lightly, so the cells where the digit can still go are the ones left plain.
--
-- Commands:  place {x, y, digit}   put a digit in a cell, over the digit that was there
--            clear {x, y}          take a digit the player entered out of its cell
--            cycle {x, y}          the digit after the one in the cell (1 to 9, then empty)
--            note {x, y, digit}    write a candidate in an empty cell, or rub it out
--            select {digit}        choose a digit (1 to 9), the eraser (10) or nothing (0); again to let go
--            tap {id, hold}        what a click sends: on a key, select it; on a cell, enter the chosen digit
--                                  (again to take it out), or with hold, shift or the right button note it.
--                                  A tap on a filled cell with nothing chosen chooses its digit.
--
-- Options:   clues   how many clues the puzzle is left with (it keeps more when it cannot be made any thinner)
--            seed    the same seed always gives the same puzzle; 0 deals a new one
--            strict  refuse digits that are already in the row, the column or the box of the cell
--            puzzle  81 digits, 0 or . for an empty cell, to play a puzzle of your own instead of a dealt one
--
-- Queries:   state                the clues, how far the puzzle is, the grid and the puzzle as 81 digits (0 is empty),
--                                 and what is chosen on the pad
--            notes                the cells with notes and their digits
--            solution             the solution as 81 digits
--            conflicts            the digits of the player that clash with another digit
--            candidates {x, y}    the digits that are not yet in the row, the column or the box of a cell
--
-- The computer player knows the solution: it puts the right digit in a cell where the grid is wrong.

local Grid = require("./lib/grid")
local Generator = require("./lib/generator")
local Solver = require("./lib/solver")

local rules = {}

local SIZE = Grid.SIZE
local CELLS = Grid.CELLS
local COLUMN = Grid.COLUMN
local ROW = Grid.ROW
local BLOCK = Grid.BLOCK
local PEERS = Grid.PEERS

local DEFAULT_CLUES = 36
local MIN_CLUES = 17
local MAX_CLUES = 80
local MAX_SEED = 2147483646

-- A script gets about a quarter of a second per call, and these searches stop long before that.
local GENERATION_NODES = 40000
local SOLVE_NODES = 50000

local GIVEN_COLOR = "#2f343d"
local ENTRY_COLOR = "#4d8fe6"
local CONFLICT_COLOR = "#ff5c5c"

-- The pad: keys 1 to 9 and the eraser, in a row below the grid.
local KEYS = 10
local ERASER = 10
local KEY_STEP = 0.96
local KEY_ROW = 5.75
local KEY_LOOKS = {
	idle = { color = "#e8ecf2", label_color = "#2f343d" },
	chosen = { color = "#4d8fe6", label_color = "#ffffff" },
	done = { color = "#c3c9d2", label_color = "#98a1ae" }, -- all nine of the digit are on the grid
}

-- How strongly a tile is tinted: not at all, as a cell the chosen digit reaches, as a cell that holds it.
local TILE_ALPHA = { [0] = 0, [1] = 0.14, [2] = 0.45 }
local TILE_HEIGHT = 0.006
local NOTE_COLOR = "#69727f"
local NOTE_STEP = 0.3
local NOTE_SIZE = 0.26
local ALL_NOTES = 511

local grid: { number } = {} -- the digit of each cell, 0 for empty
local given: { boolean } = {} -- the cells that belong to the puzzle
local solution: { number } = {}
local ids: { number } = {} -- the table object of each cell, 0 for none
local flagged: { boolean } = {} -- the cells shown as conflicting
local row_count: { number } = {} -- row_count[slot(row, digit)] is how often a digit is in a row
local col_count: { number } = {}
local box_count: { number } = {}
local filled = 0
local bad = 0 -- how many cells conflict
local clues = 0
local moves = 0
local seed_used = 0
local unique = true
local strict = false
local finished = false

local selected = 0 -- the digit chosen on the pad, ERASER, or 0 for none
local notes: { number } = {} -- the candidates written in each cell, bit digit - 1 for each
local placed: { number } = {} -- how often each digit is on the grid
local tiles: { number } = {} -- the tile object of each cell
local keys: { number } = {} -- the object of each key
local cell_of_tile: { [number]: number } = {}
local key_of_object: { [number]: number } = {}
local tile_shade: { number } = {} -- what the table shows now, so that only changes are sent
local tile_notes: { string } = {}
local key_look: { string } = {}

local function clamp_integer(value: any, fallback: number, low: number, high: number): number
	if type(value) ~= "number" or value ~= value then
		return fallback
	end
	return math.clamp(math.floor(value), low, high)
end

local function is_integer(value: any): boolean
	return type(value) == "number" and value == math.floor(value)
end

local function slot(unit: number, digit: number): number
	return (unit - 1) * 9 + digit
end

-- Whether the digit is free in the row, the column and the box of the cell. A digit the cell holds itself does
-- not count against it.
local function fits(i: number, digit: number): boolean
	local own = if grid[i] == digit then 1 else 0
	return row_count[slot(ROW[i], digit)] == own and col_count[slot(COLUMN[i], digit)] == own and box_count[slot(BLOCK[i], digit)] == own
end

-- Only the digits of the player conflict: a clue never does, the digit that clashes with it does.
local function is_conflicting(i: number): boolean
	local digit = grid[i]
	if digit == 0 or given[i] then
		return false
	end
	return not fits(i, digit)
end

local function color_of(i: number, conflicting: boolean): string
	if given[i] then
		return GIVEN_COLOR
	end
	return if conflicting then CONFLICT_COLOR else ENTRY_COLOR
end

-- Counts the digits of the grid and finds the cells that conflict; the view is not touched.
local function recount()
	row_count = table.create(81, 0)
	col_count = table.create(81, 0)
	box_count = table.create(81, 0)
	placed = table.create(9, 0)
	filled = 0
	for i = 1, CELLS do
		local digit = grid[i]
		if digit ~= 0 then
			filled += 1
			placed[digit] += 1
			row_count[slot(ROW[i], digit)] += 1
			col_count[slot(COLUMN[i], digit)] += 1
			box_count[slot(BLOCK[i], digit)] += 1
		end
	end
	bad = 0
	for i = 1, CELLS do
		flagged[i] = is_conflicting(i)
		if flagged[i] then
			bad += 1
		end
	end
end

local function set_puzzle(cells: { number }, answer: { number })
	grid = table.clone(cells)
	solution = table.clone(answer)
	given = {}
	ids = {}
	flagged = {}
	notes = table.create(CELLS, 0)
	selected = 0
	clues = 0
	for i = 1, CELLS do
		given[i] = cells[i] ~= 0
		ids[i] = 0
		if cells[i] ~= 0 then
			clues += 1
		end
	end
	recount()
	moves = 0
	finished = false
end

local function announce_board()
	bse.emit({ t = "board", columns = SIZE, rows = SIZE, block = Grid.BOX })
end

-- 0 for a cell the chosen digit has nothing to do with, 1 for a cell in a row, column or box that holds the
-- digit, 2 for a cell that holds it.
local function shade_of(i: number): number
	if selected < 1 or selected > 9 then
		return 0
	end
	if grid[i] == selected then
		return 2
	end
	if
		row_count[slot(ROW[i], selected)] > 0
		or col_count[slot(COLUMN[i], selected)] > 0
		or box_count[slot(BLOCK[i], selected)] > 0
	then
		return 1
	end
	return 0
end

-- Names what the notes of a cell look like: the notes, and the chosen digit when it is one of them.
local function notes_look(i: number): string
	local mask = notes[i]
	if mask == 0 then
		return ""
	end
	local lit = selected >= 1 and selected <= 9 and bit32.btest(mask, bit32.lshift(1, selected - 1))
	return `{mask}:{if lit then selected else 0}`
end

-- The notes of a cell as small labels in three rows of three, the chosen digit set off from the others.
local function note_labels(i: number): any
	local mask = notes[i]
	if mask == 0 then
		return false
	end
	local list = {}
	for digit = 1, 9 do
		if bit32.btest(mask, bit32.lshift(1, digit - 1)) then
			local lit = digit == selected
			table.insert(list, {
				text = tostring(digit),
				dx = ((digit - 1) % 3 - 1) * NOTE_STEP,
				dy = ((digit - 1) // 3 - 1) * NOTE_STEP,
				size = NOTE_SIZE,
				color = if lit then ENTRY_COLOR else NOTE_COLOR,
				bold = lit,
			})
		end
	end
	return list
end

local function look_of_key(key: number): string
	if key == selected then
		return "chosen"
	end
	if key <= 9 and placed[key] >= 9 then
		return "done"
	end
	return "idle"
end

-- What the tiles and keys should show, without telling the table: after a restore the table already shows it.
local function remember_looks()
	for i = 1, CELLS do
		tile_shade[i] = shade_of(i)
		tile_notes[i] = notes_look(i)
	end
	for key = 1, KEYS do
		key_look[key] = look_of_key(key)
	end
end

-- Brings the tiles and the keys up to date; only what changed is sent to the table.
local function refresh_table()
	for i = 1, CELLS do
		local shade = shade_of(i)
		local look = notes_look(i)
		if shade ~= tile_shade[i] or look ~= tile_notes[i] then
			local props: { [string]: any } = {}
			if shade ~= tile_shade[i] then
				tile_shade[i] = shade
				props.alpha = TILE_ALPHA[shade]
			end
			if look ~= tile_notes[i] then
				tile_notes[i] = look
				props.labels = note_labels(i)
			end
			bse.view.set(tiles[i], props)
		end
	end
	for key = 1, KEYS do
		local look = look_of_key(key)
		if look ~= key_look[key] then
			key_look[key] = look
			bse.view.set(keys[key], KEY_LOOKS[look])
		end
	end
end

-- The tiles under the cells and the pad below the grid. Mods place free objects with x to the right and z
-- towards the player, the middle of the grid at the origin.
local function spawn_table()
	tiles = {}
	keys = {}
	cell_of_tile = {}
	key_of_object = {}
	for i = 1, CELLS do
		local id = bse.view.spawn("cell", 0, 0, {
			props = { pos = { COLUMN[i] - 5, TILE_HEIGHT, ROW[i] - 5 }, alpha = 0, labels = false },
		})
		tiles[i] = id
		cell_of_tile[id] = i
	end
	remember_looks()
	for key = 1, KEYS do
		local look = KEY_LOOKS[key_look[key]]
		local id = bse.view.spawn("key", 0, 0, {
			props = {
				pos = { (key - 5.5) * KEY_STEP, 0, KEY_ROW },
				label = if key == ERASER then "X" else tostring(key),
				color = look.color,
				label_color = look.label_color,
			},
		})
		keys[key] = id
		key_of_object[id] = key
	end
end

local function spawn_cell(i: number, conflicting: boolean)
	local color = color_of(i, conflicting)
	ids[i] = bse.view.spawn("digit", COLUMN[i], ROW[i], {
		owner = if given[i] then nil else 1,
		props = { label = tostring(grid[i]), color = color, label_color = color, given = given[i], conflict = conflicting },
	})
end

-- A peer that now sees another set of digits may start or stop to conflict.
local function refresh_flag(i: number)
	local now = is_conflicting(i)
	if now == flagged[i] then
		return
	end
	flagged[i] = now
	bad += if now then 1 else -1
	local color = color_of(i, now)
	bse.view.set(ids[i], { conflict = now, color = color, label_color = color })
end

-- Puts a digit in a cell (0 takes it out) and brings the table up to date. Everything that can be refused has
-- been checked before this is called.
local function apply(i: number, digit: number)
	local old = grid[i]
	local r = ROW[i]
	local c = COLUMN[i]
	local b = BLOCK[i]
	if old ~= 0 then
		row_count[slot(r, old)] -= 1
		col_count[slot(c, old)] -= 1
		box_count[slot(b, old)] -= 1
		placed[old] -= 1
		filled -= 1
	end
	if digit ~= 0 then
		row_count[slot(r, digit)] += 1
		col_count[slot(c, digit)] += 1
		box_count[slot(b, digit)] += 1
		placed[digit] += 1
		filled += 1
		-- The cell is decided, and the digit is no longer a candidate where it can be seen from here.
		notes[i] = 0
		local keep = bit32.bxor(ALL_NOTES, bit32.lshift(1, digit - 1))
		for _, j in PEERS[i] do
			notes[j] = bit32.band(notes[j], keep)
		end
	end
	grid[i] = digit

	local conflicting = is_conflicting(i)
	if conflicting ~= flagged[i] then
		flagged[i] = conflicting
		bad += if conflicting then 1 else -1
	end
	if digit == 0 then
		bse.view.remove(ids[i])
		ids[i] = 0
	elseif ids[i] == 0 then
		spawn_cell(i, conflicting)
	else
		local color = color_of(i, conflicting)
		bse.view.set(ids[i], { label = tostring(digit), color = color, label_color = color, conflict = conflicting })
	end

	for _, j in PEERS[i] do
		local other = grid[j]
		if other ~= 0 and (other == old or other == digit) then
			refresh_flag(j)
		end
	end
end

local function check_solved()
	if filled == CELLS and bad == 0 then
		finished = true
		bse.status(`Solved in {moves} moves`)
		bse.over({ winner = 1, reason = "solved" })
	end
end

local function pick_seed(value: any): number
	if type(value) == "number" and value == value and value ~= 0 then
		local seed = math.floor(math.abs(value)) % MAX_SEED
		return if seed == 0 then 1 else seed
	end
	return math.random(1, MAX_SEED)
end

local function bad_puzzle(message: string): never
	error(bse.reject("BAD_PUZZLE", message), 0)
end

-- The clues of a puzzle given as text, and its solution. The puzzle must have one; when it has more than one the
-- first the solver finds is the one the computer plays towards.
local function load_puzzle(text: string): ({ number }, { number }, boolean)
	local compact = string.gsub(text, "%s", "")
	if #compact ~= CELLS then
		bad_puzzle(`a puzzle has {CELLS} digits (0 or . for an empty cell), this one has {#compact}`)
	end

	local cells: { number } = table.create(CELLS, 0)
	local in_row: { number } = table.create(9, 0)
	local in_col: { number } = table.create(9, 0)
	local in_box: { number } = table.create(9, 0)
	for i = 1, CELLS do
		local char = string.sub(compact, i, i)
		local digit = 0
		if char ~= "." then
			digit = string.byte(char) - 48
			if digit < 0 or digit > 9 then
				bad_puzzle(`'{char}' is not a digit (character {i})`)
			end
		end
		if digit ~= 0 then
			local bit = bit32.lshift(1, digit - 1)
			local r = ROW[i]
			local c = COLUMN[i]
			local b = BLOCK[i]
			if bit32.btest(in_row[r], bit) or bit32.btest(in_col[c], bit) or bit32.btest(in_box[b], bit) then
				bad_puzzle(`the clues contradict each other: the {digit} in column {c}, row {r} is already in its row, column or box`)
			end
			in_row[r] = bit32.bor(in_row[r], bit)
			in_col[c] = bit32.bor(in_col[c], bit)
			in_box[b] = bit32.bor(in_box[b], bit)
		end
		cells[i] = digit
	end

	local count, _, answer = Solver.count(cells, 2, SOLVE_NODES)
	if answer == nil then
		if count == nil then
			error(bse.reject("BAD_PUZZLE", "the puzzle is too hard to solve here: the search gave up"), 0)
		end
		error(bse.reject("BAD_PUZZLE", "the puzzle has no solution"), 0)
	end
	return cells, answer, count == 1
end

function rules.new_game(options: { [string]: any })
	strict = options.strict == true

	local cells: { number }
	local answer: { number }
	local loaded = false
	local text = options.puzzle
	if type(text) == "string" and text ~= "" then
		loaded = true
		seed_used = 0
		cells, answer, unique = load_puzzle(text)
	else
		seed_used = pick_seed(options.seed)
		unique = true
		local puzzle = Generator.generate(seed_used, clamp_integer(options.clues, DEFAULT_CLUES, MIN_CLUES, MAX_CLUES), GENERATION_NODES)
		cells = puzzle.cells
		answer = puzzle.solution
	end

	set_puzzle(cells, answer)
	announce_board()
	spawn_table()
	for i = 1, CELLS do
		if given[i] then
			spawn_cell(i, false)
		end
	end
	bse.status(if loaded then `Puzzle with {clues} clues` else `Puzzle {seed_used}, {clues} clues`)
	if not unique then
		bse.status("This puzzle has more than one solution", "warning")
	end
	bse.turn(1)
	check_solved()
end

-- Why the digit may not be put in the cell, or nil when it may. Only the strict game refuses digits.
local function conflict_of(i: number, digit: number): any
	if not strict or fits(i, digit) then
		return nil
	end
	for _, j in PEERS[i] do
		if grid[j] == digit then
			return bse.reject("CONFLICT", "that digit is already in the row, the column or the box", {
				x = COLUMN[i],
				y = ROW[i],
				digit = digit,
				with = { x = COLUMN[j], y = ROW[j] },
			})
		end
	end
	return nil
end

-- Puts a digit in a cell or takes it out, as a move of the game.
local function enter(i: number, digit: number)
	apply(i, digit)
	moves += 1
	refresh_table()
	check_solved()
end

local function toggle_note(i: number, digit: number)
	notes[i] = bit32.bxor(notes[i], bit32.lshift(1, digit - 1))
	refresh_table()
end

local function choose(key: number)
	selected = if key == selected then 0 else key
	refresh_table()
end

-- A tap on a cell: what it does depends on what is chosen on the pad and on whether it is a note.
local function tap_cell(i: number, as_note: boolean): any
	local x = COLUMN[i]
	local y = ROW[i]
	if selected == 0 then
		if grid[i] == 0 then
			return bse.reject("NOTHING_CHOSEN", "choose a digit on the pad first", { x = x, y = y })
		end
		choose(grid[i])
		return nil
	end

	if selected == ERASER then
		if given[i] then
			return bse.reject("GIVEN_CELL", "that cell is part of the puzzle", { x = x, y = y })
		end
		if grid[i] ~= 0 and not as_note then
			enter(i, 0)
			return nil
		end
		if notes[i] == 0 then
			return bse.reject("ALREADY_EMPTY", "that cell is already empty", { x = x, y = y })
		end
		notes[i] = 0
		refresh_table()
		return nil
	end

	if as_note then
		if grid[i] ~= 0 then
			return bse.reject("CELL_FILLED", "notes go in empty cells", { x = x, y = y })
		end
		toggle_note(i, selected)
		return nil
	end
	if given[i] then
		-- A clue cannot be changed; a tap on it is a way to choose its digit.
		if grid[i] == selected then
			return bse.reject("GIVEN_CELL", "that cell is part of the puzzle", { x = x, y = y })
		end
		choose(grid[i])
		return nil
	end
	if grid[i] == selected then
		enter(i, 0)
		return nil
	end
	local conflict = conflict_of(i, selected)
	if conflict ~= nil then
		return conflict
	end
	enter(i, selected)
	return nil
end

function rules.command(_seat: number, cmd: { [string]: any }): any
	local action = cmd.action
	if action ~= "place" and action ~= "clear" and action ~= "cycle" and action ~= "note" and action ~= "select" and action ~= "tap" then
		return bse.reject("UNKNOWN_ACTION", "unknown action", { action = if type(action) == "string" then action else nil })
	end
	if finished then
		return bse.reject("GAME_OVER", "the puzzle is solved")
	end

	if action == "select" then
		local key = cmd.digit
		if not is_integer(key) then
			return bse.reject("INVALID_ARGUMENT", "digit must be an integer")
		end
		if key < 0 or key > ERASER then
			return bse.reject("BAD_DIGIT", "choose 1 to 9, 10 for the eraser or 0 for nothing", { digit = key })
		end
		if key == 0 and selected == 0 then
			return bse.reject("UNCHANGED", "nothing is chosen")
		end
		choose(if key == 0 then selected else key)
		return nil
	end

	if action == "tap" then
		local id = cmd.id
		if not is_integer(id) then
			return bse.reject("INVALID_ARGUMENT", "id must be an integer")
		end
		local key = key_of_object[id]
		if key ~= nil then
			choose(key)
			return nil
		end
		local cell = cell_of_tile[id]
		if cell == nil then
			return bse.reject("NOT_A_CELL", "tap a cell of the grid or a key of the pad", { id = id })
		end
		return tap_cell(cell, cmd.hold == true or cmd.shift == true or cmd.button == 1)
	end

	local x = cmd.x
	local y = cmd.y
	if not is_integer(x) or not is_integer(y) then
		return bse.reject("INVALID_ARGUMENT", "x and y must be integers")
	end
	if x < 1 or x > SIZE or y < 1 or y > SIZE then
		return bse.reject("OUT_OF_BOUNDS", "the cell is outside the grid", { x = x, y = y, size = SIZE })
	end
	local i = Grid.index(x, y)
	if given[i] then
		return bse.reject("GIVEN_CELL", "that cell is part of the puzzle", { x = x, y = y })
	end

	local digit = 0
	if action == "place" or action == "note" then
		digit = cmd.digit
		if not is_integer(digit) then
			return bse.reject("INVALID_ARGUMENT", "digit must be an integer")
		end
		if digit < 1 or digit > 9 then
			return bse.reject("BAD_DIGIT", "the digit must be 1 to 9", { digit = digit })
		end
		if action == "note" then
			if grid[i] ~= 0 then
				return bse.reject("CELL_FILLED", "notes go in empty cells", { x = x, y = y })
			end
			toggle_note(i, digit)
			return nil
		end
		if digit == grid[i] then
			return bse.reject("UNCHANGED", "that cell already holds that digit", { x = x, y = y, digit = digit })
		end
		local conflict = conflict_of(i, digit)
		if conflict ~= nil then
			return conflict
		end
	elseif action == "clear" then
		if grid[i] == 0 then
			return bse.reject("ALREADY_EMPTY", "that cell is already empty", { x = x, y = y })
		end
	else
		digit = grid[i]
		repeat
			digit += 1
		until digit > 9 or not strict or fits(i, digit)
		if digit > 9 then
			digit = 0
		end
		if digit == grid[i] then
			return bse.reject("NO_CANDIDATE", "no digit fits that cell", { x = x, y = y })
		end
	end

	enter(i, digit)
	return nil
end

function rules.legal_commands(seat: number): { any }
	local list = {}
	if finished or seat ~= 1 then
		return list
	end
	for i = 1, CELLS do
		if not given[i] then
			for digit = 1, 9 do
				if digit ~= grid[i] and (not strict or fits(i, digit)) then
					table.insert(list, { action = "place", x = COLUMN[i], y = ROW[i], digit = digit })
				end
			end
			if grid[i] ~= 0 then
				table.insert(list, { action = "clear", x = COLUMN[i], y = ROW[i] })
			end
		end
	end
	return list
end

-- A cell where the grid is not right yet gets the right digit. Where a wrong digit stands in the way of that in
-- strict mode, the wrong digit is taken out first.
function rules.random_command(seat: number): any
	if finished or seat ~= 1 then
		return nil
	end
	local moves_left: { any } = {}
	for i = 1, CELLS do
		if grid[i] ~= solution[i] then
			if not strict or fits(i, solution[i]) then
				table.insert(moves_left, { action = "place", x = COLUMN[i], y = ROW[i], digit = solution[i] })
			elseif grid[i] ~= 0 then
				table.insert(moves_left, { action = "clear", x = COLUMN[i], y = ROW[i] })
			end
		end
	end
	if #moves_left == 0 then
		return nil
	end
	return moves_left[math.random(#moves_left)]
end

local function puzzle_text(): string
	local digits: { number } = {}
	for i = 1, CELLS do
		digits[i] = if given[i] then grid[i] else 0
	end
	return table.concat(digits)
end

function rules.query(name: string, args: any): any
	if name == "state" then
		return {
			seed = seed_used,
			strict = strict,
			unique = unique,
			clues = clues,
			filled = filled,
			empty = CELLS - filled,
			conflicts = bad,
			moves = moves,
			solved = finished,
			board = table.concat(grid),
			puzzle = puzzle_text(),
			selected = selected,
		}
	elseif name == "notes" then
		local list = {}
		for i = 1, CELLS do
			if notes[i] ~= 0 then
				local digits = {}
				for digit = 1, 9 do
					if bit32.btest(notes[i], bit32.lshift(1, digit - 1)) then
						table.insert(digits, digit)
					end
				end
				table.insert(list, { x = COLUMN[i], y = ROW[i], digits = digits })
			end
		end
		return list
	elseif name == "solution" then
		return table.concat(solution)
	elseif name == "conflicts" then
		local list = {}
		for i = 1, CELLS do
			if flagged[i] then
				table.insert(list, { x = COLUMN[i], y = ROW[i], digit = grid[i] })
			end
		end
		return list
	elseif name == "candidates" then
		if type(args) ~= "table" or not is_integer(args.x) or not is_integer(args.y) then
			return nil
		end
		local x: number = args.x
		local y: number = args.y
		if x < 1 or x > SIZE or y < 1 or y > SIZE then
			return nil
		end
		local i = Grid.index(x, y)
		local list = {}
		if given[i] then
			table.insert(list, grid[i])
			return list
		end
		for digit = 1, 9 do
			if fits(i, digit) then
				table.insert(list, digit)
			end
		end
		return list
	end
	return nil
end

function rules.snapshot(): any
	local locked: { number } = {}
	for i = 1, CELLS do
		locked[i] = if given[i] then 1 else 0
	end
	return {
		grid = table.concat(grid),
		given = table.concat(locked),
		solution = table.concat(solution),
		ids = table.clone(ids),
		tiles = table.clone(tiles),
		keys = table.clone(keys),
		notes = table.clone(notes),
		selected = selected,
		seed = seed_used,
		unique = unique,
		strict = strict,
		finished = finished,
		moves = moves,
	}
end

local function refuse_snapshot(message: string): never
	error(bse.reject("BAD_SNAPSHOT", message), 0)
end

function rules.restore(data: any)
	local grid_text = data.grid
	local given_text = data.given
	local solution_text = data.solution
	local id_list = data.ids
	if
		type(grid_text) ~= "string"
		or #grid_text ~= CELLS
		or type(given_text) ~= "string"
		or #given_text ~= CELLS
		or type(solution_text) ~= "string"
		or #solution_text ~= CELLS
		or type(id_list) ~= "table"
		or #id_list ~= CELLS
	then
		refuse_snapshot("the grid does not match the size of the puzzle")
	end

	-- Everything is checked before anything is changed.
	local new_grid: { number } = {}
	local new_given: { boolean } = {}
	local new_solution: { number } = {}
	local new_ids: { number } = {}
	local new_clues = 0
	for i = 1, CELLS do
		local digit = string.byte(grid_text, i) - 48
		local answer = string.byte(solution_text, i) - 48
		local locked = string.byte(given_text, i) - 48
		local id = id_list[i]
		if digit < 0 or digit > 9 or answer < 1 or answer > 9 or (locked ~= 0 and locked ~= 1) then
			refuse_snapshot("the grid contains an invalid cell")
		end
		if type(id) ~= "number" or (id ~= 0) ~= (digit ~= 0) or (locked == 1 and digit ~= answer) then
			refuse_snapshot("the cells do not match the table")
		end
		if id ~= 0 then
			local object = bse.view.get(id)
			if object == nil or object.kind ~= "digit" or object.x ~= COLUMN[i] or object.y ~= ROW[i] then
				refuse_snapshot("the cells do not match the table")
			end
		end
		new_grid[i] = digit
		new_given[i] = locked == 1
		new_solution[i] = answer
		new_ids[i] = id
		if locked == 1 then
			new_clues += 1
		end
	end

	local tile_list = data.tiles
	local key_list = data.keys
	local note_list = data.notes
	local chosen = data.selected
	if
		type(tile_list) ~= "table"
		or #tile_list ~= CELLS
		or type(key_list) ~= "table"
		or #key_list ~= KEYS
		or type(note_list) ~= "table"
		or #note_list ~= CELLS
		or not is_integer(chosen)
		or chosen < 0
		or chosen > ERASER
	then
		refuse_snapshot("the pad and the notes do not match the puzzle")
	end
	local new_cell_of_tile: { [number]: number } = {}
	local new_key_of_object: { [number]: number } = {}
	for i = 1, CELLS do
		local id = tile_list[i]
		local mask = note_list[i]
		local object = if type(id) == "number" then bse.view.get(id) else nil
		if object == nil or object.kind ~= "cell" or not is_integer(mask) or mask < 0 or mask > ALL_NOTES or (mask ~= 0 and new_grid[i] ~= 0) then
			refuse_snapshot("the notes do not match the table")
		end
		new_cell_of_tile[id] = i
	end
	for key = 1, KEYS do
		local id = key_list[key]
		local object = if type(id) == "number" then bse.view.get(id) else nil
		if object == nil or object.kind ~= "key" then
			refuse_snapshot("the pad does not match the table")
		end
		new_key_of_object[id] = key
	end

	grid = new_grid
	given = new_given
	solution = new_solution
	ids = new_ids
	tiles = table.clone(tile_list)
	keys = table.clone(key_list)
	notes = table.clone(note_list)
	cell_of_tile = new_cell_of_tile
	key_of_object = new_key_of_object
	selected = chosen
	clues = new_clues
	recount()
	remember_looks()
	moves = clamp_integer(data.moves, 0, 0, 1000000)
	seed_used = clamp_integer(data.seed, 0, 0, MAX_SEED)
	unique = data.unique ~= false
	strict = data.strict == true
	finished = data.finished == true
	announce_board()
end

return rules
