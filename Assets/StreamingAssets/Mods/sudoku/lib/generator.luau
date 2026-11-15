--!strict
-- Makes puzzles: a random full grid, then clues are taken away one at a time, in random order, for as long as the
-- puzzle keeps exactly one solution. The same seed always gives the same puzzle.

local Rng = require("./rng")
local Solver = require("./solver")

local Generator = {}

export type Puzzle = {
	cells: { number }, -- the clues, 0 for an empty cell
	solution: { number },
	clues: number, -- how many cells of `cells` are filled
	nodes: number, -- how much searching it took
}

-- A full grid made by relabelling a fixed pattern. It is what the generator falls back on when the random
-- search for a full grid runs out of nodes, which it hardly ever does.
local function pattern_grid(rng: Rng.Rng): { number }
	local digits: { number } = { 1, 2, 3, 4, 5, 6, 7, 8, 9 }
	Rng.shuffle(rng, digits)

	local function order(): { number }
		local bands: { number } = { 0, 1, 2 }
		Rng.shuffle(rng, bands)
		local list: { number } = {}
		for _, band in bands do
			local inner: { number } = { 0, 1, 2 }
			Rng.shuffle(rng, inner)
			for _, k in inner do
				table.insert(list, band * 3 + k)
			end
		end
		return list
	end

	local row_order = order()
	local col_order = order()
	local cells: { number } = table.create(81, 0)
	for y = 1, 9 do
		for x = 1, 9 do
			local row = row_order[y]
			local col = col_order[x]
			cells[(y - 1) * 9 + x] = digits[(3 * (row % 3) + row // 3 + col) % 9 + 1]
		end
	end
	return cells
end

-- `target` is the number of clues to leave; the puzzle keeps more when taking another clue away would let it have
-- a second solution, or when `node_budget` runs out before that point is reached.
function Generator.generate(seed: number, target: number, node_budget: number): Puzzle
	local rng = Rng.new(seed)
	local solution, spent = Solver.random_grid(rng, node_budget)
	local full = solution or pattern_grid(rng)

	local cells = table.clone(full)
	local order: { number } = {}
	for i = 1, 81 do
		order[i] = i
	end
	Rng.shuffle(rng, order)

	local clues = 81
	for _, i in order do
		if clues <= target or spent >= node_budget then
			break
		end
		local digit = cells[i]
		cells[i] = 0
		local count, used = Solver.count(cells, 2, node_budget - spent)
		spent += used
		if count == 1 then
			clues -= 1
		else
			cells[i] = digit
		end
	end
	return { cells = cells, solution = full, clues = clues, nodes = spent }
end

return Generator
