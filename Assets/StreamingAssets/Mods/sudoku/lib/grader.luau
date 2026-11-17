--!strict
-- Says how hard a puzzle is, by solving it the way a person does: with reasons, never with a guess. The reasons are
-- in three steps, and the grade of a puzzle is the highest step it can not be solved without.
--
--   1  singles: a cell in which only one digit is still possible, or a digit that has only one cell left in a
--      row, a column or a box
--   2  locked candidates (a digit that a box holds in one row or column only, or a row or column in one box only),
--      naked and hidden pairs, and naked triples
--   3  none of these is enough: the puzzle asks for an X-wing, a chain or something of that kind
--
-- Everything is worked out on sets of digits (bits 0 to 8 of a number), so grading a puzzle costs about as much as
-- a few dozen passes over the grid.

local Grid = require("./grid")

local Grader = {}

Grader.SINGLES = 1
Grader.PAIRS = 2
Grader.BEYOND = 3

local CELLS = Grid.CELLS
local COLUMN = Grid.COLUMN
local ROW = Grid.ROW
local BLOCK = Grid.BLOCK
local PEERS = Grid.PEERS
local UNITS = Grid.UNITS
local UNIT_CELLS = Grid.UNIT_CELLS

local FULL = 0x1FF

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

-- Where a row or a column crosses a box: the three cells they share, the six other cells of the line and the six
-- other cells of the box.
type Crossing = { shared: { number }, line: { number }, box: { number } }
local CROSSINGS: { Crossing } = {}
for line = 1, 18 do
	for box = 19, 27 do
		local in_box: { [number]: boolean } = {}
		for _, i in UNIT_CELLS[box] do
			in_box[i] = true
		end
		local shared: { number } = {}
		local line_rest: { number } = {}
		for _, i in UNIT_CELLS[line] do
			if in_box[i] then
				table.insert(shared, i)
			else
				table.insert(line_rest, i)
			end
		end
		if #shared > 0 then
			local box_rest: { number } = {}
			for _, i in UNIT_CELLS[box] do
				if not table.find(shared, i) then
					table.insert(box_rest, i)
				end
			end
			table.insert(CROSSINGS, { shared = shared, line = line_rest, box = box_rest })
		end
	end
end

-- The grid being graded: the digit of every cell (0 when it is empty) and the digits still possible in it (0 for
-- a filled cell).
local value: { number } = table.create(CELLS, 0)
local open: { number } = table.create(CELLS, 0)
local left = 0
local broken = false

local function put(i: number, digit: number)
	local bit = bit32.lshift(1, digit - 1)
	value[i] = digit
	open[i] = 0
	left -= 1
	local keep = bit32.bnot(bit)
	for _, p in PEERS[i] do
		local mask = open[p]
		if bit32.btest(mask, bit) then
			mask = bit32.band(mask, keep)
			open[p] = mask
			if mask == 0 then
				broken = true
			end
		end
	end
end

local used_row: { number } = table.create(9, 0)
local used_column: { number } = table.create(9, 0)
local used_box: { number } = table.create(9, 0)

local function load(cells: { number })
	left = 0
	broken = false
	for unit = 1, 9 do
		used_row[unit] = 0
		used_column[unit] = 0
		used_box[unit] = 0
	end
	for i = 1, CELLS do
		local digit = cells[i]
		value[i] = digit
		if digit ~= 0 then
			local bit = bit32.lshift(1, digit - 1)
			local r, c, b = ROW[i], COLUMN[i], BLOCK[i]
			if bit32.btest(bit32.bor(used_row[r], used_column[c], used_box[b]), bit) then
				broken = true
			end
			used_row[r] = bit32.bor(used_row[r], bit)
			used_column[c] = bit32.bor(used_column[c], bit)
			used_box[b] = bit32.bor(used_box[b], bit)
		end
	end
	for i = 1, CELLS do
		if value[i] ~= 0 then
			open[i] = 0
		else
			local mask = bit32.band(bit32.bnot(bit32.bor(used_row[ROW[i]], used_column[COLUMN[i]], used_box[BLOCK[i]])), FULL)
			open[i] = mask
			left += 1
			if mask == 0 then
				broken = true
			end
		end
	end
end

-- Fills every cell that a single settles, again and again until none is left. True when it filled any.
local function singles(): boolean
	local any = false
	local again = true
	while again and not broken and left > 0 do
		again = false
		for i = 1, CELLS do
			local mask = open[i]
			if mask ~= 0 and bit32.band(mask, mask - 1) == 0 then
				put(i, bit32.countrz(mask) + 1)
				again = true
				if broken then
					return true
				end
			end
		end
		for u = 1, UNITS do
			local cells = UNIT_CELLS[u]
			local once = 0
			local more = 0
			for k = 1, 9 do
				local mask = open[cells[k]]
				more = bit32.bor(more, bit32.band(once, mask))
				once = bit32.bor(once, mask)
			end
			local alone = bit32.band(once, bit32.bnot(more))
			while alone ~= 0 do
				local bit = bit32.band(alone, bit32.bnot(alone - 1))
				alone = bit32.bxor(alone, bit)
				for k = 1, 9 do
					local i = cells[k]
					if bit32.btest(open[i], bit) then
						put(i, bit32.countrz(bit) + 1)
						again = true
						break
					end
				end
				if broken then
					return true
				end
			end
		end
		any = any or again
	end
	return any
end

-- Takes the digits of `mask` out of the cells. True when a cell lost one.
local function strike(cells: { number }, mask: number): boolean
	local any = false
	local keep = bit32.bnot(mask)
	for _, i in cells do
		local before = open[i]
		if bit32.btest(before, mask) then
			local after = bit32.band(before, keep)
			open[i] = after
			any = true
			if after == 0 then
				broken = true
			end
		end
	end
	return any
end

local function union(cells: { number }): number
	local mask = 0
	for _, i in cells do
		mask = bit32.bor(mask, open[i])
	end
	return mask
end

-- A digit that a box has room for only where a line crosses it can not be anywhere else on the line, and the other
-- way around.
local function locked(): boolean
	local any = false
	for _, crossing in CROSSINGS do
		local shared = union(crossing.shared)
		if shared ~= 0 then
			local on_line = union(crossing.line)
			local in_box = union(crossing.box)
			local pointing = bit32.band(shared, bit32.bnot(in_box), on_line)
			if pointing ~= 0 and strike(crossing.line, pointing) then
				any = true
			end
			local claiming = bit32.band(shared, bit32.bnot(on_line), in_box)
			if claiming ~= 0 and strike(crossing.box, claiming) then
				any = true
			end
		end
	end
	return any
end

local spots: { number } = table.create(9, 0)

-- Two cells of a unit that hold the same two digits keep them from the rest of the unit, and so do three cells that
-- hold three digits between them. Two digits that have the same two cells left keep every other digit out of them.
local function subsets(): boolean
	local any = false
	for u = 1, UNITS do
		local cells = UNIT_CELLS[u]
		for a = 1, 8 do
			local first = open[cells[a]]
			local size = POPCOUNT[first + 1]
			if size == 2 or size == 3 then
				for b = a + 1, 9 do
					local second = open[cells[b]]
					if second ~= 0 then
						local two = bit32.bor(first, second)
						local width = POPCOUNT[two + 1]
						if width == 2 then
							for k = 1, 9 do
								if k ~= a and k ~= b then
									local i = cells[k]
									local before = open[i]
									if bit32.btest(before, two) then
										open[i] = bit32.band(before, bit32.bnot(two))
										any = true
										if open[i] == 0 then
											broken = true
										end
									end
								end
							end
						elseif width == 3 then
							for c = b + 1, 9 do
								local third = open[cells[c]]
								if third ~= 0 and bit32.band(third, bit32.bnot(two)) == 0 then
									for k = 1, 9 do
										if k ~= a and k ~= b and k ~= c then
											local i = cells[k]
											local before = open[i]
											if bit32.btest(before, two) then
												open[i] = bit32.band(before, bit32.bnot(two))
												any = true
												if open[i] == 0 then
													broken = true
												end
											end
										end
									end
								end
							end
						end
					end
				end
			end
		end

		-- spots[d] is the set of places (1 to 9) of the unit where digit d can still go.
		for d = 1, 9 do
			spots[d] = 0
		end
		for k = 1, 9 do
			local mask = open[cells[k]]
			local place = bit32.lshift(1, k - 1)
			while mask ~= 0 do
				local d = bit32.countrz(mask) + 1
				mask = bit32.band(mask, mask - 1)
				spots[d] = bit32.bor(spots[d], place)
			end
		end
		for d = 1, 8 do
			local where = spots[d]
			if POPCOUNT[where + 1] == 2 then
				for e = d + 1, 9 do
					if spots[e] == where then
						local pair = bit32.bor(bit32.lshift(1, d - 1), bit32.lshift(1, e - 1))
						local rest = where
						while rest ~= 0 do
							local i = cells[bit32.countrz(rest) + 1]
							rest = bit32.band(rest, rest - 1)
							if bit32.band(open[i], bit32.bnot(pair)) ~= 0 then
								open[i] = bit32.band(open[i], pair)
								any = true
							end
						end
					end
				end
			end
		end
	end
	return any
end

-- Reasons as far as they go. The grid ends up solved (left == 0), contradicted (broken) or stuck; the number is
-- the highest step that was needed.
local function settle(): number
	local grade = Grader.SINGLES
	while true do
		singles()
		if broken or left == 0 then
			return grade
		end
		if not locked() and not subsets() then
			return Grader.BEYOND
		end
		if broken then
			return grade
		end
		grade = Grader.PAIRS
	end
end

-- The grade of a puzzle (0 for an empty cell): Grader.SINGLES, Grader.PAIRS or Grader.BEYOND. A grid that
-- contradicts itself is graded BEYOND, because no reason solves it.
function Grader.grade(cells: { number }): number
	load(cells)
	local grade = settle()
	if broken then
		return Grader.BEYOND
	end
	return grade
end

-- Where singles end, a search goes on: it tries the digits of the cell that has the fewest left, and fills in
-- singles from each. One node is one cell whose digits it tries.
local kept_value: { { number } } = {}
local kept_open: { { number } } = {}
local nodes = 0
local budget = 0
local found = false
local spent_out = false

local function explore(depth: number)
	-- Singles only: the harder reasons cost more here than the few nodes they save.
	singles()
	if broken then
		return
	end
	if left == 0 then
		found = true
		return
	end
	nodes += 1
	if nodes > budget then
		spent_out = true
		return
	end

	local cell = 0
	local fewest = 10
	for i = 1, CELLS do
		local mask = open[i]
		if mask ~= 0 then
			local count = POPCOUNT[mask + 1]
			if count < fewest then
				cell = i
				fewest = count
				if count == 2 then
					break
				end
			end
		end
	end

	local values = kept_value[depth]
	local opens = kept_open[depth]
	if values == nil then
		values = table.create(CELLS, 0)
		opens = table.create(CELLS, 0)
		kept_value[depth] = values
		kept_open[depth] = opens
	end
	table.move(value, 1, CELLS, 1, values)
	table.move(open, 1, CELLS, 1, opens)
	local left_here = left

	local mask = open[cell]
	while mask ~= 0 do
		local digit = bit32.countrz(mask) + 1
		mask = bit32.band(mask, mask - 1)
		put(cell, digit)
		if not broken then
			explore(depth + 1)
			if found or spent_out then
				return
			end
		end
		table.move(values, 1, CELLS, 1, value)
		table.move(opens, 1, CELLS, 1, open)
		left = left_here
		broken = false
	end
	broken = true
end

-- Whether the grid has a solution in which the empty cell `i` holds another digit than `digit`: true or false, or
-- nil when the search used up its `node_budget` before it knew. The second number is the nodes it used. This is
-- how a puzzle is tested for a second solution when one is known: a cell of the known one is told to differ.
function Grader.differs(cells: { number }, i: number, digit: number, node_budget: number): (boolean?, number)
	load(cells)
	nodes = 0
	budget = node_budget
	found = false
	spent_out = false
	if not broken then
		local mask = bit32.band(open[i], bit32.bnot(bit32.lshift(1, digit - 1)))
		open[i] = mask
		if mask ~= 0 then
			explore(1)
		end
	end
	if found then
		return true, nodes
	end
	if spent_out then
		return nil, nodes
	end
	return false, nodes
end

return Grader
