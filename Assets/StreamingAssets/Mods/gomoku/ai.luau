--!strict
-- The computer player of gomoku: plain Luau without any bse call, so it can be loaded and tested on its own.
-- logic.luau answers rules.query("think") with it.
--
-- A "window" is one run of win_length cells in a row, column or diagonal; a row of that many stones fills exactly
-- one. The player counts the stones of each colour in every window and keeps the counts up to date as it places and
-- takes back stones, which gives it
--   * the worth of a position: every window that only one colour has stones in counts for that colour, the more
--     stones the more (VALUE), and
--   * the order to try moves in: a point is good when it adds to one's own windows or sits in the opponent's
--     (ATTACK and DEFEND).
-- On top of that sits an alpha-beta search over the best few points near the stones, deepened one move at a time
-- until the effort, the number of stones the search may place, is used up.

local ai = {}

local DIRECTIONS = { { 1, 0 }, { 0, 1 }, { 1, 1 }, { 1, -1 } }

-- How far from a stone a point may be to be considered.
local REACH = 2
-- Points tried at the root and further down.
local ROOT_WIDTH = 14
local WIDTH = 9
local MAX_DEPTH = 12
-- Forced answers are followed this many stones beyond the depth of the search.
local EXTRA_PLY = 6
local WIN = 1000000000
local MUST_WIN = 10000000
local MUST_BLOCK = 1000000

type Layout = {
	size: number,
	win_length: number,
	windows: number,
	-- for every cell the windows it lies in
	cell_windows: { { number } },
	-- for every cell the cells within REACH of it
	near: { { number } },
	value: { number },
	attack: { number },
	defend: { number },
}

local layouts: { [number]: Layout } = {}

local function layout_for(size: number, win_length: number): Layout
	local key = size * 100 + win_length
	local known = layouts[key]
	if known then
		return known
	end

	local cell_windows: { { number } } = {}
	local near: { { number } } = {}
	for index = 1, size * size do
		cell_windows[index] = {}
		near[index] = {}
	end
	local windows = 0
	for _, direction in DIRECTIONS do
		local dx = direction[1]
		local dy = direction[2]
		for y = 1, size do
			for x = 1, size do
				local ex = x + dx * (win_length - 1)
				local ey = y + dy * (win_length - 1)
				if ex >= 1 and ex <= size and ey >= 1 and ey <= size then
					windows += 1
					for step = 0, win_length - 1 do
						table.insert(cell_windows[(y + dy * step - 1) * size + x + dx * step], windows)
					end
				end
			end
		end
	end
	for y = 1, size do
		for x = 1, size do
			local list = near[(y - 1) * size + x]
			for ny = math.max(1, y - REACH), math.min(size, y + REACH) do
				for nx = math.max(1, x - REACH), math.min(size, x + REACH) do
					if nx ~= x or ny ~= y then
						table.insert(list, (ny - 1) * size + nx)
					end
				end
			end
		end
	end

	-- Indexed by the stones in a window plus one. A window that is one stone short decides the game, so the point
	-- that fills one's own comes before the point that fills the opponent's, and both before everything else.
	local value: { number } = { 0 }
	local attack: { number } = { 1 }
	local defend: { number } = { 0 }
	for count = 1, win_length do
		value[count + 1] = 7 ^ (count - 1)
		attack[count + 1] = 8 ^ count
		defend[count + 1] = math.floor(0.6 * 8 ^ count)
	end
	attack[win_length] = MUST_WIN
	defend[win_length] = MUST_BLOCK

	local made: Layout = {
		size = size,
		win_length = win_length,
		windows = windows,
		cell_windows = cell_windows,
		near = near,
		value = value,
		attack = attack,
		defend = defend,
	}
	layouts[key] = made
	return made
end

export type Result = {
	-- the index of the chosen cell, (y - 1) * size + x; 0 when there is no empty cell
	move: number,
	-- what the search expects for the seat: above zero is good, WIN minus a few is a win it has seen
	score: number,
	-- moves looked ahead and stones placed while searching
	depth: number,
	nodes: number,
}

-- Chooses a point for `seat` (1 or 2). `cells` holds 0, 1 or 2 for every cell, row by row, and is not changed.
-- `effort` is the number of stones the search may place: a few dozen look one move ahead, thousands several.
function ai.choose(cells: { number }, size: number, win_length: number, exact_win: boolean, seat: number, effort: number): Result
	local layout = layout_for(size, win_length)
	local cell_windows = layout.cell_windows
	local near_cells = layout.near
	local value = layout.value
	local attack = layout.attack
	local defend = layout.defend
	local area = size * size

	local board: { number } = table.create(area, 0)
	-- stones of seat 1 and of seat 2 in every window
	local mine: { { number } } = { table.create(layout.windows, 0), table.create(layout.windows, 0) }
	-- the worth of the windows that belong to seat 1 and to seat 2 alone
	local worth: { number } = { 0, 0 }
	-- the windows of seat 1 and of seat 2 alone that are one stone short of full
	local short: { number } = { 0, 0 }
	local almost = win_length - 1
	-- for every cell the stones within REACH
	local near: { number } = table.create(area, 0)
	local empty = area
	local nodes = 0
	local stopped = false
	-- the stones from the position up to which forced answers are followed
	local horizon = 0

	local function place(index: number, who: number)
		local own = mine[who]
		local other = mine[3 - who]
		local gained = 0
		local lost = 0
		for _, window in cell_windows[index] do
			local count = own[window]
			local theirs = other[window]
			if theirs == 0 then
				gained += value[count + 2] - value[count + 1]
				if count + 1 == almost then
					short[who] += 1
				elseif count == almost then
					short[who] -= 1
				end
			elseif count == 0 then
				lost += value[theirs + 1]
				if theirs == almost then
					short[3 - who] -= 1
				end
			end
			own[window] = count + 1
		end
		worth[who] += gained
		worth[3 - who] -= lost
		board[index] = who
		for _, cell in near_cells[index] do
			near[cell] += 1
		end
		empty -= 1
	end

	local function take_back(index: number, who: number)
		local own = mine[who]
		local other = mine[3 - who]
		local gained = 0
		local lost = 0
		for _, window in cell_windows[index] do
			local count = own[window] - 1
			local theirs = other[window]
			own[window] = count
			if theirs == 0 then
				gained += value[count + 2] - value[count + 1]
				if count + 1 == almost then
					short[who] -= 1
				elseif count == almost then
					short[who] += 1
				end
			elseif count == 0 then
				lost += value[theirs + 1]
				if theirs == almost then
					short[3 - who] += 1
				end
			end
		end
		worth[who] -= gained
		worth[3 - who] += lost
		board[index] = 0
		for _, cell in near_cells[index] do
			near[cell] -= 1
		end
		empty += 1
	end

	local function run(index: number, step_x: number, step_y: number, who: number): number
		local x = (index - 1) % size + 1 + step_x
		local y = (index - 1) // size + 1 + step_y
		local count = 0
		while x >= 1 and x <= size and y >= 1 and y <= size and board[(y - 1) * size + x] == who do
			count += 1
			x += step_x
			y += step_y
		end
		return count
	end

	-- Whether the stone just placed on the cell made a winning row, by the rules of the game.
	local function wins(index: number, who: number): boolean
		for _, direction in DIRECTIONS do
			local total = 1 + run(index, direction[1], direction[2], who) + run(index, -direction[1], -direction[2], who)
			if total == win_length or (total > win_length and not exact_win) then
				return true
			end
		end
		return false
	end

	-- How much the point is worth trying for the seat.
	local function urgency(index: number, who: number): number
		local own = mine[who]
		local other = mine[3 - who]
		local sum = 0
		for _, window in cell_windows[index] do
			local theirs = other[window]
			if theirs == 0 then
				sum += attack[own[window] + 1]
			elseif own[window] == 0 then
				sum += defend[theirs + 1]
			end
		end
		return sum
	end

	-- The best points for the seat, best first: the points and how many there are. When a row can be finished or
	-- must be stopped, nothing else is worth a look.
	local function candidates(who: number, width: number, jitter: boolean): ({ number }, number)
		local points: { number } = {}
		local scores: { number } = {}
		local count = 0
		for index = 1, area do
			if board[index] == 0 and near[index] > 0 then
				local score = urgency(index, who)
				if jitter then
					score += math.random() * 0.5
				end
				-- insertion into the short sorted list
				local slot = count
				if count < width then
					count += 1
					slot = count
				elseif score <= scores[count] then
					slot = 0
				end
				if slot > 0 then
					while slot > 1 and scores[slot - 1] < score do
						points[slot] = points[slot - 1]
						scores[slot] = scores[slot - 1]
						slot -= 1
					end
					points[slot] = index
					scores[slot] = score
				end
			end
		end
		if exact_win then
			-- A full window may be part of a row that is too long to win, so nothing is certain enough to skip the rest.
			return points, count
		end
		if count > 1 and scores[1] >= MUST_WIN then
			count = 1
		elseif count > 2 and scores[1] >= MUST_BLOCK then
			local forced = 1
			while forced < count and forced < 3 and scores[forced + 1] >= MUST_BLOCK do
				forced += 1
			end
			count = forced
		end
		return points, count
	end

	local search: (number, number, number, number, number) -> number

	-- What the position is worth to `who`, who has just placed a stone that did not end the game, after `ply` stones.
	local function after(depth: number, alpha: number, beta: number, who: number, ply: number): number
		if short[3 - who] > 0 and not exact_win then
			-- The other seat fills a row with its next stone.
			return -(WIN - ply - 1)
		end
		if short[who] > 0 and ply < horizon then
			-- A row one stone short has to be answered at once. The answer does not count as a move looked ahead, so
			-- the look goes on with the stone after it; rows of four are read to their end this way.
			return -search(math.max(depth, 2), -beta, -alpha, 3 - who, ply + 1)
		end
		if depth > 1 then
			return -search(depth - 1, -beta, -alpha, 3 - who, ply + 1)
		end
		-- The other seat is to move, which is worth a part of what it has on the board.
		return worth[who] - 1.25 * worth[3 - who]
	end

	function search(depth: number, alpha: number, beta: number, who: number, ply: number): number
		local points, count = candidates(who, WIDTH, false)
		local best = -WIN
		for i = 1, count do
			if nodes >= effort then
				stopped = true
				return 0
			end
			local index = points[i]
			nodes += 1
			place(index, who)
			local score
			if wins(index, who) then
				score = WIN - ply
			elseif empty == 0 then
				score = 0
			else
				score = after(depth, alpha, beta, who, ply)
			end
			take_back(index, who)
			if stopped then
				return 0
			end
			if score > best then
				best = score
				if score > alpha then
					alpha = score
					if alpha >= beta then
						break
					end
				end
			end
		end
		return best
	end

	for index = 1, area do
		local who = cells[index]
		if who == 1 or who == 2 then
			place(index, who)
		end
	end
	if empty == 0 then
		return { move = 0, score = 0, depth = 0, nodes = 0 }
	end
	if empty == area then
		return { move = ((size + 1) // 2 - 1) * size + (size + 1) // 2, score = 0, depth = 0, nodes = 0 }
	end

	local points, count = candidates(seat, ROOT_WIDTH, true)
	local best_move = points[1]
	local best_score = 0
	local reached = 0
	if count > 1 then
		for depth = 1, math.min(MAX_DEPTH, empty) do
			local alpha = -WIN
			local leader = 0
			horizon = depth + EXTRA_PLY
			local scores: { number } = {}
			for i = 1, count do
				-- The first look ahead is always finished, whatever the effort.
				if depth > 1 and nodes >= effort then
					stopped = true
					break
				end
				local index = points[i]
				nodes += 1
				place(index, seat)
				local score
				if wins(index, seat) then
					score = WIN - 1
				elseif empty == 0 then
					score = 0
				else
					score = after(depth, alpha, WIN, seat, 1)
				end
				take_back(index, seat)
				if stopped then
					break
				end
				scores[i] = score
				if leader == 0 or score > alpha then
					alpha = score
					leader = i
				end
			end
			-- A deeper look that was cut short still counts when it got past the move that led before: that move
			-- was tried first, so whatever leads now was compared with it.
			if leader > 0 then
				best_move = points[leader]
				best_score = alpha
				reached = depth
			end
			if stopped or best_score >= WIN - MAX_DEPTH - EXTRA_PLY - 2 or best_score <= -(WIN - MAX_DEPTH - EXTRA_PLY - 2) then
				break
			end
			-- The next look starts with the moves that did best in this one.
			local order: { number } = {}
			for i = 1, count do
				order[i] = i
			end
			table.sort(order, function(a: number, b: number): boolean
				if scores[a] ~= scores[b] then
					return scores[a] > scores[b]
				end
				return a < b
			end)
			local sorted: { number } = {}
			for i = 1, count do
				sorted[i] = points[order[i]]
			end
			points = sorted
		end
	end
	return { move = best_move, score = best_score, depth = reached, nodes = nodes }
end

return ai
