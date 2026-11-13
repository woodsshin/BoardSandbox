--!strict
-- The chess set, drawn from nothing but the basic solids of the client (cylinders, spheres, cones and boxes). A
-- piece is a handful of them stacked on a square, and each solid is a free view object of its own, so the pieces can
-- be told apart from straight above as well as from the side:
--   pawn    a ball on a narrow neck; the smallest piece
--   knight  a horse head that looks at the other side of the board
--   bishop  an egg-shaped mitre with a golden tip and a slanted dark slit, standing on a diamond-shaped foot
--   rook    a square tower with four merlons, standing on a square foot that lies straight with the board
--   queen   a crown of eight pearls around a golden ball
--   king    a crown that carries a golden cross
-- The shapes are simple geometry and copy no existing set.
--
-- The models are written for white, whose side of the board is the one near the camera, in units of one square: x
-- runs to the right, z toward the camera and the height is measured from the board. The knight's head looks to +x.
-- Black's pieces are white's turned by half a circle about the vertical axis. The marks that show a selection (tiles
-- and dots) are drawn here too.

local pieces = {}

export type Part = {
	shape: string,
	tone: number,
	-- the middle of the solid, relative to the middle of the square: x, height above the board, z
	at: { number },
	size: { number },
	-- degrees about x, y and z, applied in that order
	turn: { number }?,
	shadow: boolean?,
}

local BODY = 1
local TRIM = 2
local DARK = 3
local GOLD = 4

-- body, trim, dark and gold of white and of black
local PALETTE: { { string } } = {
	{ "#f5efe2", "#b7a984", "#5f5746", "#dcb44e" },
	{ "#383841", "#6c6c7a", "#0b0b0e", "#dcb44e" },
}

local PAWN = 1
local KNIGHT = 2
local BISHOP = 3
local ROOK = 4
local QUEEN = 5
local KING = 6

local NAMES: { string } = { "pawn", "knight", "bishop", "rook", "queen", "king" }
local SQUARES = 8
local FOOT = 0.01

-- A solid that stands on the given height; depth is the width unless it is given.
local function solid(shape: string, tone: number, base: number, height: number, width: number, depth: number?, x: number?, z: number?): Part
	return { shape = shape, tone = tone, at = { x or 0, base + height * 0.5, z or 0 }, size = { width, height, depth or width } }
end

-- The foot of a piece; it also casts the shade on the board.
local function foot(shape: string, width: number, height: number): Part
	local part = solid(shape, TRIM, 0, height, width)
	part.shadow = true
	return part
end

local function tilted(part: Part, x: number, y: number, z: number): Part
	part.turn = { x, y, z }
	return part
end

local function build(): { { Part } }
	local models: { { Part } } = {}

	models[PAWN] = {
		foot("cylinder", 0.60, 0.08),
		solid("cone", BODY, 0.08, 0.44, 0.52),
		solid("sphere", BODY, 0.38, 0.34, 0.34),
	}

	-- a square foot that lies straight with the board, under a square tower with a merlon on each corner
	local rook = {
		foot("box", 0.72, 0.08),
		solid("box", BODY, 0.08, 0.52, 0.52),
		solid("box", TRIM, 0.60, 0.10, 0.66),
		solid("box", DARK, 0.70, 0.012, 0.24),
	}
	for _, sx in { -1, 1 } do
		for _, sz in { -1, 1 } do
			table.insert(rook, solid("box", BODY, 0.70, 0.14, 0.22, 0.22, sx * 0.22, sz * 0.22))
		end
	end
	models[ROOK] = rook

	-- the knight looks to the right, which shows its profile to the camera
	models[KNIGHT] = {
		foot("cylinder", 0.72, 0.08),
		solid("cone", BODY, 0.08, 0.44, 0.58),
		tilted({ shape = "sphere", tone = BODY, at = { 0.05, 0.70, 0 }, size = { 0.32, 0.60, 0.30 } }, 0, 0, -22),
		tilted({ shape = "sphere", tone = BODY, at = { 0.21, 0.95, 0 }, size = { 0.56, 0.28, 0.28 } }, 0, 0, -22),
		tilted({ shape = "sphere", tone = DARK, at = { -0.08, 0.74, 0 }, size = { 0.08, 0.58, 0.12 } }, 0, 0, -22),
		tilted({ shape = "cone", tone = BODY, at = { -0.02, 1.12, -0.07 }, size = { 0.10, 0.19, 0.10 } }, 0, 0, 12),
		tilted({ shape = "cone", tone = BODY, at = { -0.02, 1.12, 0.07 }, size = { 0.10, 0.19, 0.10 } }, 0, 0, 12),
		solid("sphere", DARK, 0.945, 0.055, 0.055, nil, 0.22, 0.135),
		solid("sphere", DARK, 0.945, 0.055, 0.055, nil, 0.22, -0.135),
	}

	-- the foot is a diamond; the mitre is an egg with a slanting slit on both faces and a golden ball on top
	models[BISHOP] = {
		tilted(foot("box", 0.54, 0.08), 0, 45, 0),
		solid("cone", BODY, 0.08, 0.32, 0.54),
		solid("cylinder", TRIM, 0.38, 0.05, 0.40),
		solid("sphere", BODY, 0.40, 0.48, 0.34),
		solid("sphere", GOLD, 0.86, 0.10, 0.10),
		tilted({ shape = "sphere", tone = DARK, at = { 0, 0.68, 0.158 }, size = { 0.24, 0.05, 0.05 } }, 0, 0, 40),
		tilted({ shape = "sphere", tone = DARK, at = { 0, 0.68, -0.158 }, size = { 0.24, 0.05, 0.05 } }, 0, 0, 40),
	}

	-- a crown of eight pearls around a golden ball
	local queen = {
		foot("cylinder", 0.80, 0.08),
		solid("cone", BODY, 0.08, 0.80, 0.66),
		solid("cylinder", TRIM, 0.66, 0.07, 0.52),
		solid("sphere", GOLD, 0.74, 0.20, 0.20),
	}
	for i = 0, 7 do
		local angle = math.rad(i * 45)
		table.insert(queen, solid("sphere", GOLD, 0.69, 0.13, 0.13, nil, math.cos(angle) * 0.22, math.sin(angle) * 0.22))
	end
	models[QUEEN] = queen

	-- a golden cross with an arm in every direction
	models[KING] = {
		foot("cylinder", 0.80, 0.08),
		solid("cone", BODY, 0.08, 0.80, 0.66),
		solid("cylinder", TRIM, 0.66, 0.12, 0.48),
		solid("box", GOLD, 0.78, 0.26, 0.11),
		solid("box", GOLD, 0.93, 0.09, 0.42, 0.11),
		solid("box", GOLD, 0.93, 0.09, 0.11, 0.42),
	}
	return models
end

local MODELS = build()

local function side_index(side: number): number
	return if side == 2 then 2 else 1
end

-- The square's middle in the space of the table: columns and rows count from 1, and 8 squares are centred on 0.
local function center(x: number, y: number): (number, number)
	return x - (SQUARES + 1) * 0.5, y - (SQUARES + 1) * 0.5
end

-- Black's pieces are white's turned by half a circle about the vertical axis.
local function offset(part: Part, side: number, cx: number, cz: number): { number }
	local turned = if side == 2 then -1 else 1
	return { cx + part.at[1] * turned, FOOT + part.at[2], cz + part.at[3] * turned }
end

-- Where the parts of a piece are when it stands on the square (x, y).
local function positions(kind: number, side: number, x: number, y: number): { { number } }
	local cx, cz = center(x, y)
	local list: { { number } } = {}
	local model = MODELS[kind]
	for i = 1, #model do
		list[i] = offset(model[i], side, cx, cz)
	end
	return list
end

-- Puts a piece on the square (x, y) and returns the ids of its parts.
function pieces.spawn(kind: number, side: number, x: number, y: number): { number }
	local group: { number } = {}
	if bse.is_headless() then
		return group
	end
	local colors = PALETTE[side_index(side)]
	local cx, cz = center(x, y)
	local model = MODELS[kind]
	for i = 1, #model do
		local part = model[i]
		local props: { [string]: any } = {
			shape = part.shape,
			color = colors[part.tone],
			size = part.size,
			pos = offset(part, side, cx, cz),
		}
		if part.shadow == true then
			props.shadow = true
		end
		local turn = part.turn
		if turn ~= nil then
			-- turning the piece by 180 degrees about y adds that to the turn about y and reverses the one about z
			props.rot = if side == 2 then { turn[1], turn[2] + 180, -turn[3] } else { turn[1], turn[2], turn[3] }
		end
		group[i] = bse.view.spawn(NAMES[kind], x, y, { owner = side, props = props })
	end
	return group
end

-- Slides the parts of a piece from the square (from_x, from_y) to the square (x, y) in the given time. A piece that hops
-- first rises by that much over the middle between the squares and then comes down, like a knight that jumps.
function pieces.move(group: { number }, kind: number, side: number, from_x: number, from_y: number, x: number, y: number, seconds: number, hop: number)
	if #group == 0 then
		return
	end
	local leg = seconds
	if hop > 0 then
		leg = seconds * 0.5
		local up = positions(kind, side, (from_x + x) * 0.5, (from_y + y) * 0.5)
		for i = 1, #group do
			local at = up[i]
			at[2] += hop
			bse.view.set(group[i], { pos = at, tween = leg })
		end
		bse.emit({ t = "fx", kind = "wait", seconds = leg })
	end
	local where = positions(kind, side, x, y)
	for i = 1, #group do
		bse.view.set(group[i], { pos = where[i], tween = leg })
	end
end

function pieces.remove(group: { number })
	for i = 1, #group do
		bse.view.remove(group[i])
	end
end

-- A flat coloured tile on a square; layer lifts it above the tiles of lower layers.
function pieces.tile(x: number, y: number, color: string, alpha: number, layer: number): number
	local cx, cz = center(x, y)
	return bse.view.spawn("mark", x, y, {
		props = {
			shape = "box",
			color = color,
			alpha = alpha,
			size = { 0.96, 0.01, 0.96 },
			pos = { cx, 0.006 + 0.004 * layer, cz },
		},
	})
end

-- A round dot in the middle of a square.
function pieces.dot(x: number, y: number, color: string, alpha: number, width: number): number
	local cx, cz = center(x, y)
	return bse.view.spawn("hint", x, y, {
		props = {
			shape = "cylinder",
			color = color,
			alpha = alpha,
			size = { width, 0.01, width },
			pos = { cx, 0.03, cz },
		},
	})
end

pieces.PAWN = PAWN
pieces.KNIGHT = KNIGHT
pieces.BISHOP = BISHOP
pieces.ROOK = ROOK
pieces.QUEEN = QUEEN
pieces.KING = KING

return pieces
