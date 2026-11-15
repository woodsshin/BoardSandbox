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

Grid.COLUMN = COLUMN
Grid.ROW = ROW
Grid.BLOCK = BLOCK
Grid.PEERS = PEERS

function Grid.index(x: number, y: number): number
	return (y - 1) * 9 + x
end

return Grid
