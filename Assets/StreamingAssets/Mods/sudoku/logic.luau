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
-- A row, a column or a box whose nine cells all hold the digits of the solution is finished. The table says so with
-- a wave that runs outwards from the cell that was just filled, one step at a time (the tile of every cell lights
-- up, the digit grows, then both settle in green), and from then on the digits of the unit are locked: they can
-- not be changed or taken out any more. When one digit finishes several units, they are one wave.
--
-- Under every key of a digit a small number says how many of that digit are still to be put on the grid. When all
-- nine are on the grid the key goes from the pad (a digit can not be on the grid more than nine times), and comes
-- back when one of them is taken out.
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
--                                 what is chosen on the pad, how many of each digit are still to be put on the grid
--                                 (9 digits) and how many cells are locked
--            notes                the cells with notes and their digits
--            solution             the solution as 81 digits
--            conflicts            the digits of the player that clash with another digit
--            units                the rows, columns and boxes (1 to 9 each) that are finished
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
local UNITS = Grid.UNITS
local UNIT_CELLS = Grid.UNIT_CELLS
local UNITS_OF = Grid.UNITS_OF

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
local DONE_COLOR = "#1f9d6b" -- a digit of a finished unit
local DIGIT_SIZE = 0.82 -- the label_size of a digit, as in config.json

-- The pad: keys 1 to 9 and the eraser, in a row below the grid.
local KEYS = 10
local ERASER = 10
local KEY_STEP = 0.96
local KEY_ROW = 5.75
local KEY_LOOKS: { [string]: { [string]: any } } = {
	idle = { color = "#e8ecf2", label_color = "#2f343d" },
	chosen = { color = "#4d8fe6", label_color = "#ffffff" },
}

-- Under a key a small number says how many of its digit are still to be put on the grid. Its place is a share of
-- the width of the key, down from the middle of it, which puts it just below the key.
local COUNT_DY = 0.72
local COUNT_SIZE = 0.3
local COUNT_COLOR = "#69727f"

-- How a tile looks: plain, as a cell the chosen digit reaches (lightly tinted), as a cell that holds it (strongly)
-- and as a cell of a finished unit (faintly green).
local TILE_LOOKS: { [string]: { [string]: any } } = {
	plain = { color = ENTRY_COLOR, alpha = 0 },
	reach = { color = ENTRY_COLOR, alpha = 0.14 },
	holds = { color = ENTRY_COLOR, alpha = 0.45 },
	done = { color = "#34c38f", alpha = 0.16 },
}
local TILE_HEIGHT = 0.006
local NOTE_COLOR = "#69727f"
local NOTE_STEP = 0.3
local NOTE_SIZE = 0.26
local ALL_NOTES = 511

-- The wave that shows a finished unit: a cell lights up (the tile gold, the digit large), then glows green for a
-- step and then settles. A step is this long on the screen.
local STEP_SECONDS = 0.09
local FLASH_TILE = { color = "#ffd166", alpha = 0.9 }
local GLOW_TILE = { color = "#34c38f", alpha = 0.5 }
local FLASH_TEXT = "#8a5a00"
local GLOW_TEXT = "#127a50"
local POP_SIZE = 1.0

local grid: { number } = {} -- the digit of each cell, 0 for empty
local given: { boolean } = {} -- the cells that belong to the puzzle
local solution: { number } = {}
local ids: { number } = {} -- the table object of each cell, 0 for none
local flagged: { boolean } = {} -- the cells shown as conflicting
local row_count: { number } = {} -- row_count[slot(row, digit)] is how often a digit is in a row
local col_count: { number } = {}
local box_count: { number } = {}
local unit_done: { boolean } = {} -- the units (rows, then columns, then boxes) whose nine cells are right
local locked: { boolean } = {} -- the cells of the player in a finished unit, which can not be changed any more
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
local tile_look: { string } = {} -- what the table shows now, so that only changes are sent
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

-- Only the digits of the player conflict: a clue never does, the digit that clashes with it does. The same goes for
-- a digit of a finished unit, which is known to be right: it is the other digit that is wrong.
local function is_conflicting(i: number): boolean
	local digit = grid[i]
	if digit == 0 or given[i] or locked[i] then
		return false
	end
	return not fits(i, digit)
end

local function color_of(i: number, conflicting: boolean): string
	if given[i] then
		return GIVEN_COLOR
	end
	if locked[i] then
		return DONE_COLOR
	end
	return if conflicting then CONFLICT_COLOR else ENTRY_COLOR
end

-- Whether the nine cells of a unit hold the digits of the solution.
local function unit_is_right(u: number): boolean
	for _, c in UNIT_CELLS[u] do
		if grid[c] ~= solution[c] then
			return false
		end
	end
	return true
end

-- Finds the finished units of the grid, and with them the cells that are locked. Nothing is sent to the table.
local function find_finished_units()
	unit_done = {}
	for u = 1, UNITS do
		unit_done[u] = unit_is_right(u)
	end
	locked = {}
	for i = 1, CELLS do
		local units = UNITS_OF[i]
		locked[i] = not given[i] and (unit_done[units[1]] or unit_done[units[2]] or unit_done[units[3]])
	end
end

-- Counts the digits of the grid and finds the finished units and the cells that conflict; the view is not touched.
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
	find_finished_units()
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

-- Whether a cell is in a finished row, column or box.
local function in_finished_unit(i: number): boolean
	local units = UNITS_OF[i]
	return unit_done[units[1]] or unit_done[units[2]] or unit_done[units[3]]
end

-- Names how the tile of a cell looks: what the chosen digit makes of it, or else that it is in a finished unit.
local function tile_look_of(i: number): string
	local shade = shade_of(i)
	if shade == 2 then
		return "holds"
	elseif shade == 1 then
		return "reach"
	end
	return if in_finished_unit(i) then "done" else "plain"
end

-- Whether all nine of a digit are on the grid: its key is gone from the pad and it can not be chosen.
local function is_used_up(digit: number): boolean
	return digit >= 1 and digit <= 9 and placed[digit] >= 9
end

local function look_of_key(key: number): string
	if is_used_up(key) then
		return "gone"
	end
	return if key == selected then "chosen" else "idle"
end

-- Names what a key looks like: its look, and for a digit how many of it are still to come.
local function key_state(key: number): string
	local look = look_of_key(key)
	if look == "gone" or key == ERASER then
		return look
	end
	return `{look}:{9 - placed[key]}`
end

-- What the table is told about a key: it is gone, or it has its colors and, for a digit, the count below it.
local function key_props(key: number): { [string]: any }
	local look = look_of_key(key)
	if look == "gone" then
		return { visible = false }
	end
	local colors = KEY_LOOKS[look]
	local props: { [string]: any } = { visible = true, color = colors.color, label_color = colors.label_color }
	if key ~= ERASER then
		props.labels = {
			{ text = tostring(9 - placed[key]), dx = 0, dy = COUNT_DY, size = COUNT_SIZE, color = COUNT_COLOR, bold = false },
		}
	end
	return props
end

-- What the tiles and keys should show, without telling the table: after a restore the table already shows it.
local function remember_looks()
	for i = 1, CELLS do
		tile_look[i] = tile_look_of(i)
		tile_notes[i] = notes_look(i)
	end
	for key = 1, KEYS do
		key_look[key] = key_state(key)
	end
end

-- Brings the tiles and the keys up to date; only what changed is sent to the table.
local function refresh_table()
	-- A digit that has been used up can not stay chosen: its key is gone.
	if is_used_up(selected) then
		selected = 0
	end
	for i = 1, CELLS do
		local look = tile_look_of(i)
		local written = notes_look(i)
		if look ~= tile_look[i] or written ~= tile_notes[i] then
			local props: { [string]: any } = {}
			if look ~= tile_look[i] then
				tile_look[i] = look
				props.color = TILE_LOOKS[look].color
				props.alpha = TILE_LOOKS[look].alpha
			end
			if written ~= tile_notes[i] then
				tile_notes[i] = written
				props.labels = note_labels(i)
			end
			bse.view.set(tiles[i], props)
		end
	end
	for key = 1, KEYS do
		local state = key_state(key)
		if state ~= key_look[key] then
			key_look[key] = state
			bse.view.set(keys[key], key_props(key))
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
	remember_looks()
	for i = 1, CELLS do
		local look = TILE_LOOKS[tile_look[i]]
		local id = bse.view.spawn("cell", 0, 0, {
			props = { pos = { COLUMN[i] - 5, TILE_HEIGHT, ROW[i] - 5 }, color = look.color, alpha = look.alpha, labels = false },
		})
		tiles[i] = id
		cell_of_tile[id] = i
	end
	for key = 1, KEYS do
		local props = key_props(key)
		props.pos = { (key - 5.5) * KEY_STEP, 0, KEY_ROW }
		props.label = if key == ERASER then "X" else tostring(key)
		local id = bse.view.spawn("key", 0, 0, { props = props })
		keys[key] = id
		key_of_object[id] = key
	end
end

local function spawn_cell(i: number, conflicting: boolean)
	local color = color_of(i, conflicting)
	ids[i] = bse.view.spawn("digit", COLUMN[i], ROW[i], {
		owner = if given[i] then nil else 1,
		props = {
			label = tostring(grid[i]),
			color = color,
			label_color = color,
			given = given[i],
			locked = locked[i],
			conflict = conflicting,
		},
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

-- How long a step of the wave has to be on paper for the table to show it for STEP_SECONDS. The table plays the
-- steps faster while others are waiting behind them: one and a half times as fast with one waiting, twice with two
-- and so on (PlaybackQueue.Speed), so the step that has `behind` steps after it is made that much longer.
local function step_seconds(behind: number): number
	return STEP_SECONDS * math.min(12, 1 + 0.5 * behind)
end

-- Where a cell ends up when the wave has passed: the tile and the digit as they are meant to look from now on.
local function settle(c: number)
	local look = TILE_LOOKS[tile_look[c]]
	bse.view.set(tiles[c], { color = look.color, alpha = look.alpha })
	local color = color_of(c, flagged[c])
	bse.view.set(ids[c], { color = color, label_color = color, label_size = DIGIT_SIZE, locked = locked[c], conflict = flagged[c] })
end

-- Shows what the cell of the wave looks like at the given moment of it.
local function light(c: number, stage: string)
	if stage == "flash" then
		bse.view.set(tiles[c], FLASH_TILE)
		bse.view.set(ids[c], { color = FLASH_TEXT, label_color = FLASH_TEXT, label_size = POP_SIZE })
	elseif stage == "glow" then
		bse.view.set(tiles[c], GLOW_TILE)
		bse.view.set(ids[c], { color = GLOW_TEXT, label_color = GLOW_TEXT, label_size = DIGIT_SIZE })
	else
		settle(c)
	end
end

-- The units in which the digit that has just been put in a cell completes the right digits.
local function units_finished_by(i: number): { number }
	local list: { number } = {}
	for _, u in UNITS_OF[i] do
		if not unit_done[u] and unit_is_right(u) then
			table.insert(list, u)
		end
	end
	return list
end

-- Shows that the units are right and locks their cells. The wave starts at the cell that was just filled and runs
-- outwards one cell further with every step, along the row, the column and round the box: a cell lights up, glows a
-- step later and has settled a step after that. Without a screen the cells just settle.
local function celebrate(origin: number, units: { number })
	local cells: { number } = {}
	local counted: { boolean } = {}
	for _, u in units do
		unit_done[u] = true
		for _, c in UNIT_CELLS[u] do
			if not counted[c] then
				counted[c] = true
				table.insert(cells, c)
			end
		end
	end

	-- What the cells are from now on. A digit that was shown as conflicting is not any more: it is right.
	for _, c in cells do
		if not given[c] and not locked[c] then
			locked[c] = true
			local conflicting = is_conflicting(c)
			if conflicting ~= flagged[c] then
				flagged[c] = conflicting
				bad += if conflicting then 1 else -1
			end
		end
		tile_look[c] = tile_look_of(c)
	end

	if bse.is_headless() then
		for _, c in cells do
			settle(c)
		end
		return
	end

	local rings: { { number } } = {}
	local last = 0
	for _, c in cells do
		local ring = math.abs(COLUMN[c] - COLUMN[origin]) + math.abs(ROW[c] - ROW[origin])
		last = math.max(last, ring)
		-- Rings are counted from 1 here, 1 being the cell that was filled.
		local list = rings[ring + 1]
		if list == nil then
			list = {}
			rings[ring + 1] = list
		end
		table.insert(list, c)
	end
	local waits = last + 2
	for step = 0, last + 2 do
		for _, c in rings[step + 1] or {} do
			light(c, "flash")
		end
		for _, c in rings[step] or {} do
			light(c, "glow")
		end
		for _, c in rings[step - 1] or {} do
			light(c, "settle")
		end
		if step < waits then
			bse.emit({ t = "fx", kind = "wait", seconds = step_seconds(waits - step - 1) })
		end
	end
end

-- Puts a digit in a cell or takes it out, as a move of the game.
local function enter(i: number, digit: number)
	apply(i, digit)
	moves += 1
	refresh_table()
	if digit ~= 0 then
		local units = units_finished_by(i)
		if #units > 0 then
			celebrate(i, units)
		end
	end
	check_solved()
end

local function toggle_note(i: number, digit: number)
	notes[i] = bit32.bxor(notes[i], bit32.lshift(1, digit - 1))
	refresh_table()
end

-- Chooses a key of the pad, or lets go of it when it is chosen. A digit that has been used up has no key to choose.
local function choose(key: number): any
	if is_used_up(key) then
		return bse.reject("DIGIT_USED", `all nine {key}s are already on the grid`, { digit = key })
	end
	selected = if key == selected then 0 else key
	refresh_table()
	return nil
end

-- Why nobody can change the cell: it is part of the puzzle or of a finished unit.
local function why_fixed(i: number): any
	if given[i] then
		return bse.reject("GIVEN_CELL", "that cell is part of the puzzle", { x = COLUMN[i], y = ROW[i] })
	end
	return bse.reject("CELL_LOCKED", "that cell is in a row, a column or a box that is finished", { x = COLUMN[i], y = ROW[i] })
end

-- A tap on a cell: what it does depends on what is chosen on the pad and on whether it is a note.
local function tap_cell(i: number, as_note: boolean): any
	local x = COLUMN[i]
	local y = ROW[i]
	if selected == 0 then
		if grid[i] == 0 then
			return bse.reject("NOTHING_CHOSEN", "choose a digit on the pad first", { x = x, y = y })
		end
		return choose(grid[i])
	end

	if selected == ERASER then
		if given[i] or locked[i] then
			return why_fixed(i)
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
	if given[i] or locked[i] then
		-- Such a cell can not be changed; a tap on it is a way to choose its digit.
		if grid[i] == selected then
			return why_fixed(i)
		end
		return choose(grid[i])
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
		return choose(if key == 0 then selected else key)
	end

	if action == "tap" then
		local id = cmd.id
		if not is_integer(id) then
			return bse.reject("INVALID_ARGUMENT", "id must be an integer")
		end
		local key = key_of_object[id]
		if key ~= nil then
			return choose(key)
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
	if given[i] or locked[i] then
		return why_fixed(i)
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
		if is_used_up(digit) then
			return bse.reject("DIGIT_USED", `all nine {digit}s are already on the grid`, { x = x, y = y, digit = digit })
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
		until digit > 9 or (not is_used_up(digit) and (not strict or fits(i, digit)))
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
		if not given[i] and not locked[i] then
			for digit = 1, 9 do
				if digit ~= grid[i] and not is_used_up(digit) and (not strict or fits(i, digit)) then
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

-- A cell where the grid is not right yet gets the right digit. Where a wrong digit stands in the way of that, in
-- strict mode or because all nine of the digit are on the grid already, the wrong digit is taken out first.
function rules.random_command(seat: number): any
	if finished or seat ~= 1 then
		return nil
	end
	local moves_left: { any } = {}
	for i = 1, CELLS do
		if grid[i] ~= solution[i] then
			if not is_used_up(solution[i]) and (not strict or fits(i, solution[i])) then
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

-- How many of each digit are still to be put on the grid, as nine digits.
local function remaining_text(): string
	local counts: { number } = {}
	for digit = 1, 9 do
		counts[digit] = math.max(0, 9 - placed[digit])
	end
	return table.concat(counts)
end

local function locked_count(): number
	local count = 0
	for i = 1, CELLS do
		if locked[i] then
			count += 1
		end
	end
	return count
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
			remaining = remaining_text(),
			locked = locked_count(),
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
	elseif name == "units" then
		local rows: { number } = {}
		local columns: { number } = {}
		local boxes: { number } = {}
		for u = 1, 9 do
			if unit_done[u] then
				table.insert(rows, u)
			end
			if unit_done[9 + u] then
				table.insert(columns, u)
			end
			if unit_done[18 + u] then
				table.insert(boxes, u)
			end
		end
		return { rows = rows, columns = columns, boxes = boxes }
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
	-- A digit that has been used up has no key to be chosen on.
	if is_used_up(selected) then
		selected = 0
	end
	remember_looks()
	moves = clamp_integer(data.moves, 0, 0, 1000000)
	seed_used = clamp_integer(data.seed, 0, 0, MAX_SEED)
	unique = data.unique ~= false
	strict = data.strict == true
	finished = data.finished == true
	announce_board()
end

return rules
