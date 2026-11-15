--!strict
-- The shape of the grid: the 81 cells are numbered from 1 at the top left, row by row, and every cell knows its
-- column (x), its row (y), its 3x3 box and the 20 other cells that share a row, a column or a box with it.

local Grid = {}

Grid.SIZE = 9
Grid.BOX = 3
Grid.CELLS = 81

local COLUMN: { number } = {}
local ROW: { number } = {}
local BLOCK: { number } = {}
local PEERS: { { number } } = {}

for i = 1, 81 do
	local x = (i - 1) % 9 + 1
	local y = (i - 1) // 9 + 1
	COLUMN[i] = x
	ROW[i] = y
	BLOCK[i] = (y - 1) // 3 * 3 + (x - 1) // 3 + 1
end

for i = 1, 81 do
	local peers: { number } = {}
	for j = 1, 81 do
		if j ~= i and (COLUMN[j] == COLUMN[i] or ROW[j] == ROW[i] or BLOCK[j] == BLOCK[i]) then
			table.insert(peers, j)
		end
	end
	PEERS[i] = peers
end

-- The 27 units, in which every digit has to be exactly once: the rows are 1 to 9, the columns 10 to 18 and the boxes
-- 19 to 27. A unit lists its nine cells (a row from left to right, a column from top to bottom, a box row by row);
-- a cell is in three units, its row, its column and its box.
local UNIT_CELLS: { { number } } = {}
local UNITS_OF: { { number } } = {}

for u = 1, 27 do
	UNIT_CELLS[u] = {}
end

for i = 1, 81 do
	local units = { ROW[i], 9 + COLUMN[i], 18 + BLOCK[i] }
	UNITS_OF[i] = units
	for _, u in units do
		table.insert(UNIT_CELLS[u], i)
	end
end

Grid.COLUMN = COLUMN
Grid.ROW = ROW
Grid.BLOCK = BLOCK
Grid.PEERS = PEERS
Grid.UNITS = 27
Grid.UNIT_CELLS = UNIT_CELLS
Grid.UNITS_OF = UNITS_OF

function Grid.index(x: number, y: number): number
	return (y - 1) * 9 + x
end

return Grid
