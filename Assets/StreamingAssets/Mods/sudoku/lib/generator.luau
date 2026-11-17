--!strict
-- Makes puzzles: a random full grid, then clues are taken away one at a time, in random order, for as long as the
-- puzzle keeps exactly one solution. The same seed always gives the same puzzle.
--
-- A puzzle of a grade (see grader.luau) is made the same way, with the grader deciding what may go: a puzzle for
-- pairs loses clues only while pairs and locked candidates still solve it, and a puzzle beyond them loses every
-- clue it can. Thinning does not always end at the grade that was asked for; then some clues are put back and the
-- puzzle is thinned again in another order, until it does.

local Grader = require("./grader")
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

export type Graded = Puzzle & {
	grade: number, -- what the grader says of `cells`; it is the grade asked for unless every try missed it
	tries: number, -- how many times the clues were thinned
}

-- While the puzzle has this many clues or more, they are taken away several at a time: so many clues nearly
-- always leave it easy, and one look at the lot is cheaper than a look at each.
local BATCH = 8
local BATCH_ABOVE = 48

-- A try that missed the grade does not start over: it puts this many clues back and thins the puzzle again in a
-- new order, which is a third of the work and ends somewhere else.
local PUT_BACK = 16

-- Each search for a second solution may use this many nodes; they use a few dozen.
local SEARCH_NODES = 5000

-- Takes away the clues of `order` that may go. For a puzzle for pairs a clue may go while pairs and locked
-- candidates still solve the puzzle (which also shows that it has one solution). For a puzzle beyond them every
-- clue goes that leaves the puzzle with one solution.
local function thin(cells: { number }, full: { number }, order: { number }, wanted: number, batches: boolean): number
	local spent = 0
	local clues = 0
	for i = 1, 81 do
		if cells[i] ~= 0 then
			clues += 1
		end
	end

	local taken: { number } = {}
	local at = 1
	local count = #order
	while at <= count do
		if batches and clues - BATCH >= BATCH_ABOVE then
			table.clear(taken)
			while #taken < BATCH and at <= count do
				local i = order[at]
				at += 1
				if cells[i] ~= 0 then
					cells[i] = 0
					table.insert(taken, i)
				end
			end
			if Grader.grade(cells) <= Grader.PAIRS then
				clues -= #taken
			else
				for _, i in taken do
					cells[i] = full[i]
				end
				for _, i in taken do
					cells[i] = 0
					if Grader.grade(cells) <= Grader.PAIRS then
						clues -= 1
					else
						cells[i] = full[i]
					end
				end
			end
		else
			local i = order[at]
			at += 1
			local digit = cells[i]
			if digit ~= 0 then
				cells[i] = 0
				local gone: boolean
				if wanted <= Grader.PAIRS then
					gone = Grader.grade(cells) <= Grader.PAIRS
				else
					local other, nodes = Grader.differs(cells, i, digit, SEARCH_NODES)
					spent += nodes
					gone = other == false
				end
				if gone then
					clues -= 1
				else
					cells[i] = digit
				end
			end
		end
	end
	return spent
end

-- A puzzle of the grade `wanted` (Grader.PAIRS or Grader.BEYOND) that can not lose another clue and stay what it
-- is. Thinning does not always end at the grade, so it is done up to `tries` times; when all of them miss, the
-- puzzle is the one that came closest (the hardest that is not harder than asked for).
function Generator.generate_graded(seed: number, wanted: number, tries: number, node_budget: number): Graded
	local rng = Rng.new(seed)
	local solution, spent = Solver.random_grid(rng, node_budget)
	local full = solution or pattern_grid(rng)

	local order: { number } = {}
	for i = 1, 81 do
		order[i] = i
	end
	Rng.shuffle(rng, order)

	local cells = table.clone(full)
	spent += thin(cells, full, order, wanted, true)
	local grade = Grader.grade(cells)
	local best = cells
	local best_grade = grade
	local used = 1

	local empties: { number } = {}
	while grade ~= wanted and used < tries do
		used += 1
		table.clear(empties)
		for i = 1, 81 do
			if cells[i] == 0 then
				table.insert(empties, i)
			end
		end
		Rng.shuffle(rng, empties)
		for k = 1, math.min(PUT_BACK, #empties) do
			local i = empties[k]
			cells[i] = full[i]
		end
		Rng.shuffle(rng, order)
		spent += thin(cells, full, order, wanted, false)
		grade = Grader.grade(cells)
		if grade == wanted or (grade < wanted and grade > best_grade) or best_grade > wanted then
			best = table.clone(cells)
			best_grade = grade
		end
	end

	local clues = 0
	for i = 1, 81 do
		if best[i] ~= 0 then
			clues += 1
		end
	end
	return { cells = best, solution = full, clues = clues, nodes = spent, grade = best_grade, tries = used }
end

return Generator
