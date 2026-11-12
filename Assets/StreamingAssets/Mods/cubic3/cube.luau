--!strict
-- The model of a 3x3 face-turning cube puzzle. It knows nothing about the platform: where every sticker sits,
-- what the eighteen face moves do to them and to the 26 little cubes, how the cube unfolds into a net of text,
-- scrambles and the solved test.
--
-- Stickers are numbered like in the usual facelet notation: faces U R F D L B, nine stickers each, read row by row
-- as the face looks from outside in the unfolded net below. A "slot" is one of the 54 places of a sticker.
--
--           U
--       L   F   R   B
--           D

export type Move = {
	face: number, -- 1..6, an index into Cube.FACES
	turns: number, -- 1 = clockwise, 2 = half turn, 3 = counter-clockwise
	name: string, -- "R", "R2" or "R'"
	perm: { number }, -- the sticker in slot i moves to slot perm[i]
}

local Cube = {}

Cube.FACES = { "U", "R", "F", "D", "L", "B" }
Cube.SLOTS = 54

-- The size of the unfolded net. Every face is a block of 3x3 points; the blocks are one point apart.
Cube.COLUMNS = 15
Cube.ROWS = 12

-- Face i is solved when all its stickers have color i. The letters never clash with the face letters.
local COLORS = {
	{ name = "pink", letter = "P", hex = "#f06aa8" },
	{ name = "clay", letter = "C", hex = "#c8553d" },
	{ name = "sky", letter = "S", hex = "#46c8f2" },
	{ name = "lime", letter = "G", hex = "#90e03c" },
	{ name = "violet", letter = "V", hex = "#5a2ca0" },
	{ name = "slate", letter = "K", hex = "#2d3347" },
}

local FACE_INDEX: { [string]: number } = { U = 1, R = 2, F = 3, D = 4, L = 5, B = 6 }
local OPPOSITE = { 4, 5, 6, 1, 2, 3 }

-- x points right, y up and z towards the viewer. For every face: the direction it looks to, and the directions in
-- which the columns and the rows of its 3x3 block grow in the unfolded net.
local NORMALS = { { 0, 1, 0 }, { 1, 0, 0 }, { 0, 0, 1 }, { 0, -1, 0 }, { -1, 0, 0 }, { 0, 0, -1 } }
local RIGHTS = { { 1, 0, 0 }, { 0, 0, -1 }, { 1, 0, 0 }, { 1, 0, 0 }, { 0, 0, 1 }, { -1, 0, 0 } }
local DOWNS = { { 0, 0, 1 }, { 0, -1, 0 }, { 0, -1, 0 }, { 0, 0, -1 }, { 0, -1, 0 }, { 0, -1, 0 } }

-- Column and row of the top left sticker of every face in the net.
local ORIGIN_X = { 5, 9, 5, 5, 1, 13 }
local ORIGIN_Y = { 2, 6, 6, 10, 6, 6 }

-- A sticker is told apart by the little cube it sits on (each coordinate -1, 0 or 1) and the way it looks.
local function key(p: { number }, n: { number }): number
	return (((p[1] + 1) * 3 + (p[2] + 1)) * 3 + (p[3] + 1)) * 27 + ((n[1] + 1) * 3 + (n[2] + 1)) * 3 + (n[3] + 1)
end

local slot_position: { { number } } = {}
local slot_normal: { { number } } = {}
local slot_by_key: { [number]: number } = {}

for face = 1, 6 do
	local normal, right, down = NORMALS[face], RIGHTS[face], DOWNS[face]
	for row = 1, 3 do
		for column = 1, 3 do
			local slot = (face - 1) * 9 + (row - 1) * 3 + column
			local p = {
				normal[1] + (column - 2) * right[1] + (row - 2) * down[1],
				normal[2] + (column - 2) * right[2] + (row - 2) * down[2],
				normal[3] + (column - 2) * right[3] + (row - 2) * down[3],
			}
			slot_position[slot] = p
			slot_normal[slot] = normal
			slot_by_key[key(p, normal)] = slot
		end
	end
end

-- A quarter turn clockwise around the axis, as seen from the tip of the axis looking at the origin.
local function quarter_clockwise(axis: { number }, v: { number }): { number }
	local along = axis[1] * v[1] + axis[2] * v[2] + axis[3] * v[3]
	local cx = axis[2] * v[3] - axis[3] * v[2]
	local cy = axis[3] * v[1] - axis[1] * v[3]
	local cz = axis[1] * v[2] - axis[2] * v[1]
	return { axis[1] * along - cx, axis[2] * along - cy, axis[3] * along - cz }
end

-- The outer layer of a face turns: every sticker on a little cube of that layer lands on another slot.
local function quarter_permutation(face: number): { number }
	local axis = NORMALS[face]
	local perm: { number } = {}
	for slot = 1, Cube.SLOTS do
		local p = slot_position[slot]
		local n = slot_normal[slot]
		if p[1] * axis[1] + p[2] * axis[2] + p[3] * axis[3] == 1 then
			p = quarter_clockwise(axis, p)
			n = quarter_clockwise(axis, n)
		end
		perm[slot] = slot_by_key[key(p, n)]
	end
	return perm
end

-- First one permutation, then the other.
local function compose(first: { number }, second: { number }): { number }
	local out: { number } = {}
	for slot = 1, Cube.SLOTS do
		out[slot] = second[first[slot]]
	end
	return out
end

local MOVES: { Move } = {}

for face = 1, 6 do
	local quarter = quarter_permutation(face)
	local half = compose(quarter, quarter)
	local counter = compose(half, quarter)
	local letter = Cube.FACES[face]
	MOVES[(face - 1) * 3 + 1] = { face = face, turns = 1, name = letter, perm = quarter }
	MOVES[(face - 1) * 3 + 2] = { face = face, turns = 2, name = letter .. "2", perm = half }
	MOVES[(face - 1) * 3 + 3] = { face = face, turns = 3, name = letter .. "'", perm = counter }
end

local SUFFIX_TURNS: { [string]: number } = { [""] = 1, ["'"] = 3, ["2"] = 2, ["2'"] = 2, ["'2"] = 2 }

-- The eighteen moves, face by face: clockwise, half turn, counter-clockwise.
function Cube.all_moves(): { Move }
	return MOVES
end

function Cube.move(face: number, turns: number): Move
	return MOVES[(face - 1) * 3 + turns]
end

function Cube.inverse(move: Move): Move
	return MOVES[(move.face - 1) * 3 + 4 - move.turns]
end

-- "R", "R'", "R2" (and "R2'"); nil for anything else.
function Cube.parse(token: string): Move?
	local letter, suffix = string.match(token, "^([URFDLB])([2']*)$")
	if letter == nil or suffix == nil then
		return nil
	end
	local turns = SUFFIX_TURNS[suffix]
	if turns == nil then
		return nil
	end
	return MOVES[(FACE_INDEX[letter] - 1) * 3 + turns]
end

-- Moves separated by blanks or commas. When a token is not a move, the result is nil and the token.
function Cube.parse_sequence(text: string): ({ Move }?, string?)
	local list: { Move } = {}
	for token in string.gmatch(text, "[^%s,]+") do
		local move = Cube.parse(token)
		if move == nil then
			return nil, token
		end
		list[#list + 1] = move
	end
	return list, nil
end

function Cube.sequence_text(sequence: { Move }): string
	local names: { string } = {}
	for index, move in sequence do
		names[index] = move.name
	end
	return table.concat(names, " ")
end

function Cube.solved(): { number }
	local cells: { number } = {}
	for slot = 1, Cube.SLOTS do
		cells[slot] = (slot - 1) // 9 + 1
	end
	return cells
end

-- Moves whatever sits in the slots (colors, ids of drawn pieces) the way the move moves the stickers.
function Cube.permute(list: { number }, move: Move): { number }
	local perm = move.perm
	local out: { number } = {}
	for slot = 1, Cube.SLOTS do
		out[perm[slot]] = list[slot]
	end
	return out
end

function Cube.is_solved(cells: { number }): boolean
	for face = 0, 5 do
		local first = cells[face * 9 + 1]
		for offset = 2, 9 do
			if cells[face * 9 + offset] ~= first then
				return false
			end
		end
	end
	return true
end

-- Random turns of random faces; the same face never comes twice in a row and a pair of opposite faces never
-- surrounds a turn of the same face, so no part of a scramble cancels against another.
function Cube.scramble(count: number): { Move }
	local result: { Move } = {}
	local last = 0
	local before = 0
	for _ = 1, count do
		local face = math.random(6)
		while face == last or (face == before and last == OPPOSITE[face]) do
			face = math.random(6)
		end
		result[#result + 1] = MOVES[(face - 1) * 3 + math.random(3)]
		before = last
		last = face
	end
	return result
end

-- Where a slot is in the net: a column and a row, both counted from 1.
function Cube.cell(slot: number): (number, number)
	local face = (slot - 1) // 9 + 1
	local offset = (slot - 1) % 9
	return ORIGIN_X[face] + offset % 3, ORIGIN_Y[face] + offset // 3
end

function Cube.is_center(slot: number): boolean
	return (slot - 1) % 9 == 4
end

function Cube.color_hex(color: number): string
	return COLORS[color].hex
end

-- One string of nine color letters per face, in the order the stickers are numbered.
function Cube.faces(cells: { number }): { [string]: string }
	local faces: { [string]: string } = {}
	for face = 1, 6 do
		local letters: { string } = {}
		for offset = 1, 9 do
			letters[offset] = COLORS[cells[(face - 1) * 9 + offset]].letter
		end
		faces[Cube.FACES[face]] = table.concat(letters)
	end
	return faces
end

-- The 54 stickers as a string of face letters (U1..U9, R1..R9, F1..F9, D1..D9, L1..L9, B1..B9), each color named
-- after the face that has it in the middle. This is what common solvers read.
function Cube.facelets(cells: { number }): string
	local face_of_color: { [number]: string } = {}
	for face = 1, 6 do
		face_of_color[cells[(face - 1) * 9 + 5]] = Cube.FACES[face]
	end
	local letters: { string } = {}
	for slot = 1, Cube.SLOTS do
		letters[slot] = face_of_color[cells[slot]] or "?"
	end
	return table.concat(letters)
end

-- The cube unfolded, one string per row.
function Cube.net(cells: { number }): { string }
	local grid: { { string } } = {}
	for y = 1, Cube.ROWS do
		local row: { string } = {}
		for x = 1, Cube.COLUMNS do
			row[x] = " "
		end
		grid[y] = row
	end
	for slot = 1, Cube.SLOTS do
		local x, y = Cube.cell(slot)
		grid[y][x] = COLORS[cells[slot]].letter
	end
	local lines: { string } = {}
	for y = 1, Cube.ROWS do
		lines[y] = table.concat(grid[y])
	end
	return lines
end

-- The little cubes. Each one remembers where it belongs (home), where it is (pos) and how it is turned: rot is a
-- 3x3 matrix, row by row, that takes a direction of the cubie at home to the direction it points to now.
export type Cubie = { home: { number }, pos: { number }, rot: { number } }

local SIDE_NAMES = { "px", "nx", "py", "ny", "pz", "nz" }
local face_by_normal: { [number]: number } = {}
for face = 1, 6 do
	local n = NORMALS[face]
	face_by_normal[((n[1] + 1) * 3 + (n[2] + 1)) * 3 + (n[3] + 1)] = face
end

local function multiply(a: { number }, b: { number }): { number }
	local out: { number } = {}
	for row = 0, 2 do
		for column = 1, 3 do
			out[row * 3 + column] = a[row * 3 + 1] * b[column] + a[row * 3 + 2] * b[3 + column] + a[row * 3 + 3] * b[6 + column]
		end
	end
	return out
end

local function apply(m: { number }, v: { number }): { number }
	return {
		m[1] * v[1] + m[2] * v[2] + m[3] * v[3],
		m[4] * v[1] + m[5] * v[2] + m[6] * v[3],
		m[7] * v[1] + m[8] * v[2] + m[9] * v[3],
	}
end

local QUARTERS: { { number } } = {}
for face = 1, 6 do
	local x = quarter_clockwise(NORMALS[face], { 1, 0, 0 })
	local y = quarter_clockwise(NORMALS[face], { 0, 1, 0 })
	local z = quarter_clockwise(NORMALS[face], { 0, 0, 1 })
	QUARTERS[face] = { x[1], y[1], z[1], x[2], y[2], z[2], x[3], y[3], z[3] }
end

-- The direction a face looks to.
function Cube.normal(face: number): { number }
	return NORMALS[face]
end

-- The face that looks along a direction; nil when the direction is not close to an axis.
function Cube.face_of(direction: { number }): number?
	local n = { 0, 0, 0 }
	for axis = 1, 3 do
		local value = direction[axis]
		if type(value) ~= "number" or value ~= value then
			return nil
		end
		if math.abs(value) > 0.9 then
			n[axis] = math.sign(value)
		elseif math.abs(value) > 0.1 then
			return nil
		end
	end
	return face_by_normal[((n[1] + 1) * 3 + (n[2] + 1)) * 3 + (n[3] + 1)]
end

-- The 26 cubies of a solved cube.
function Cube.new_cubies(): { Cubie }
	local list: { Cubie } = {}
	for x = -1, 1 do
		for y = -1, 1 do
			for z = -1, 1 do
				if x ~= 0 or y ~= 0 or z ~= 0 then
					list[#list + 1] = { home = { x, y, z }, pos = { x, y, z }, rot = { 1, 0, 0, 0, 1, 0, 0, 0, 1 } }
				end
			end
		end
	end
	return list
end

-- Turns the cubies of a layer; the result lists which entries of the list moved.
function Cube.turn_cubies(cubies: { Cubie }, move: Move): { number }
	local axis = NORMALS[move.face]
	local quarter = QUARTERS[move.face]
	local moved: { number } = {}
	for index, cubie in cubies do
		local p = cubie.pos
		if p[1] * axis[1] + p[2] * axis[2] + p[3] * axis[3] == 1 then
			moved[#moved + 1] = index
			for _ = 1, move.turns do
				cubie.pos = apply(quarter, cubie.pos)
				cubie.rot = multiply(quarter, cubie.rot)
			end
		end
	end
	return moved
end

-- The color in every slot as the cubies show it; nil when they do not form a cube.
function Cube.cubie_cells(cubies: { Cubie }): { number }?
	local cells: { number } = {}
	local filled = 0
	for _, cubie in cubies do
		for axis = 1, 3 do
			local side = cubie.home[axis]
			if side ~= 0 then
				local own = { 0, 0, 0 }
				own[axis] = side
				local slot = slot_by_key[key(cubie.pos, apply(cubie.rot, own))]
				if slot == nil or cells[slot] ~= nil then
					return nil
				end
				cells[slot] = face_by_normal[((own[1] + 1) * 3 + (own[2] + 1)) * 3 + (own[3] + 1)]
				filled += 1
			end
		end
	end
	return if filled == Cube.SLOTS then cells else nil
end

-- The sticker colors of a cubie, named by the side of the cubie they are on.
function Cube.cubie_faces(home: { number }): { [string]: string }
	local faces: { [string]: string } = {}
	for axis = 1, 3 do
		local side = home[axis]
		if side ~= 0 then
			local own = { 0, 0, 0 }
			own[axis] = side
			local face = face_by_normal[((own[1] + 1) * 3 + (own[2] + 1)) * 3 + (own[3] + 1)]
			faces[SIDE_NAMES[(axis - 1) * 2 + (if side > 0 then 1 else 2)]] = COLORS[face].hex
		end
	end
	return faces
end

-- True for the 24 ways a cubie can be turned.
function Cube.is_rotation(rot: any): boolean
	if type(rot) ~= "table" or #rot ~= 9 then
		return false
	end
	for index = 1, 9 do
		local value = rot[index]
		if value ~= -1 and value ~= 0 and value ~= 1 then
			return false
		end
	end
	local det = rot[1] * (rot[5] * rot[9] - rot[6] * rot[8])
		- rot[2] * (rot[4] * rot[9] - rot[6] * rot[7])
		+ rot[3] * (rot[4] * rot[8] - rot[5] * rot[7])
	if det ~= 1 then
		return false
	end
	for row = 0, 2 do
		local a, b, c = rot[row * 3 + 1], rot[row * 3 + 2], rot[row * 3 + 3]
		if a * a + b * b + c * c ~= 1 then
			return false
		end
	end
	return true
end

return Cube
