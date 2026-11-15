--!strict
-- A backtracking solver for the grid. It always continues with the empty cell that has the fewest digits left to
-- try, which keeps the search tiny for the puzzles of this game. Every search has a budget of nodes (one node is
-- one cell it has to choose a digit for), because a script gets only a fraction of a second per call and cannot
-- read the clock.

local Grid = require("./grid")
local Rng = require("./rng")

local Solver = {}

local CELLS = Grid.CELLS
local COLUMN = Grid.COLUMN
local ROW = Grid.ROW
local BLOCK = Grid.BLOCK

-- The digits of a unit are the bits 0 to 8 of a number: digit d is the bit BIT[d].
local FULL = 0x1FF
local BIT: { number } = {}
for digit = 1, 9 do
	BIT[digit] = bit32.lshift(1, digit - 1)
end

-- POPCOUNT[mask + 1] is the number of digits in a set of digits.
local POPCOUNT: { number } = {}
for mask = 0, FULL do
	local count = 0
	local rest = mask
	while rest ~= 0 do
		count += 1
		rest = bit32.band(rest, rest - 1)
	end
	POPCOUNT[mask + 1] = count
end

-- The state of the search in progress. A search never starts another one, so it can live in upvalues.
local board: { number } = table.create(CELLS, 0)
local rows: { number } = table.create(9, 0)
local cols: { number } = table.create(9, 0)
local boxes: { number } = table.create(9, 0)
local empties: { number } = table.create(CELLS, 0)
local nodes = 0
local limit = 0
local budget = 0
local found = 0
local first: { number }? = nil
local stopped = false
local exhausted = false
local dice: Rng.Rng? = nil

-- Reads a grid into the state. Returns false when two cells of a unit hold the same digit, and the number of
-- empty cells.
local function load(cells: { number }): (boolean, number)
	for unit = 1, 9 do
		rows[unit] = 0
		cols[unit] = 0
		boxes[unit] = 0
	end
	local count = 0
	for i = 1, CELLS do
		local digit = cells[i]
		board[i] = digit
		if digit == 0 then
			count += 1
			empties[count] = i
		else
			local bit = BIT[digit]
			local r, c, b = ROW[i], COLUMN[i], BLOCK[i]
			if bit32.btest(rows[r], bit) or bit32.btest(cols[c], bit) or bit32.btest(boxes[b], bit) then
				return false, count
			end
			rows[r] = bit32.bor(rows[r], bit)
			cols[c] = bit32.bor(cols[c], bit)
			boxes[b] = bit32.bor(boxes[b], bit)
		end
	end
	return true, count
end

-- empties[1 .. remaining] are the cells still to fill. The cell chosen at each step is swapped to the end of
-- that range, so the range of the caller is the same set of cells when the step returns.
local function search(remaining: number)
	if remaining == 0 then
		found += 1
		if first == nil then
			first = table.clone(board)
		end
		if found >= limit then
			stopped = true
		end
		return
	end

	nodes += 1
	if nodes > budget then
		stopped = true
		exhausted = true
		return
	end

	local best = 0
	local best_mask = 0
	local best_count = 10
	for slot = 1, remaining do
		local i = empties[slot]
		local mask = bit32.band(bit32.bnot(bit32.bor(rows[ROW[i]], cols[COLUMN[i]], boxes[BLOCK[i]])), FULL)
		local count = POPCOUNT[mask + 1]
		if count < best_count then
			best = slot
			best_mask = mask
			best_count = count
			if count <= 1 then
				break
			end
		end
	end
	if best_count == 0 then
		return
	end

	local i = empties[best]
	empties[best] = empties[remaining]
	empties[remaining] = i
	local r, c, b = ROW[i], COLUMN[i], BLOCK[i]
	local mask = best_mask
	while mask ~= 0 do
		local pick = mask
		local rng = dice
		if rng then
			for _ = 2, Rng.below(rng, POPCOUNT[mask + 1]) do
				pick = bit32.band(pick, pick - 1)
			end
		end
		local digit = bit32.countrz(pick) + 1
		local bit = BIT[digit]
		mask = bit32.bxor(mask, bit)

		board[i] = digit
		rows[r] = bit32.bor(rows[r], bit)
		cols[c] = bit32.bor(cols[c], bit)
		boxes[b] = bit32.bor(boxes[b], bit)
		search(remaining - 1)
		rows[r] = bit32.bxor(rows[r], bit)
		cols[c] = bit32.bxor(cols[c], bit)
		boxes[b] = bit32.bxor(boxes[b], bit)
		board[i] = 0
		if stopped then
			return
		end
	end
end

local function run(cells: { number }, max: number, node_budget: number, rng: Rng.Rng?): (number?, number, { number }?)
	local consistent, remaining = load(cells)
	nodes = 0
	limit = max
	budget = node_budget
	found = 0
	first = nil
	stopped = false
	exhausted = false
	dice = rng
	if consistent then
		search(remaining)
	end
	dice = nil
	local solution = first
	first = nil
	if exhausted and found < max then
		return nil, nodes, solution
	end
	return found, nodes, solution
end

-- Counts the solutions of a grid (0 means empty cells) and stops counting at `max`. The first number is nil
-- when the search ran out of nodes before it knew; then the third, the first solution it found, may still be
-- there. The second number is how many nodes the search used.
function Solver.count(cells: { number }, max: number, node_budget: number): (number?, number, { number }?)
	return run(cells, max, node_budget, nil)
end

-- A random full grid, or nil when the search ran out of nodes.
function Solver.random_grid(rng: Rng.Rng, node_budget: number): ({ number }?, number)
	local _, used, solution = run(table.create(CELLS, 0), 1, node_budget, rng)
	return solution, used
end

return Solver
