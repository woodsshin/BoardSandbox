--!strict
-- Chess rules: board, move generation, legality, SAN, FEN and draw detection.
-- Plain Luau without any bse call, so it can be loaded and tested on its own; logic.luau puts it on the table.
--
-- The board is a 10x12 mailbox of 120 cells. A playable square has the index 22 + 10 * row + file, where row 0 is
-- rank 8 (the far side of the default camera) and file 0 is the a-file. Every cell that a ray, a knight jump or a
-- pawn capture can reach from there lies inside 1..120, and the cells around the board hold OFF.

export type Move = {
	from: number,
	to: number,
	piece: number,
	-- the piece taken (also for en passant), EMPTY when nothing is taken
	captured: number,
	-- the piece type a pawn becomes, 0 when the move is not a promotion
	promo: number,
	kind: number,
}

export type Position = {
	board: { number },
	-- 1 = white, 2 = black
	side: number,
	-- bit 1 = white O-O, 2 = white O-O-O, 4 = black O-O, 8 = black O-O-O
	castling: number,
	-- the square a pawn may capture on en passant, 0 for none
	ep: number,
	halfmove: number,
	fullmove: number,
	-- square of the white and of the black king
	kings: { number },
}

local WHITE = 1
local BLACK = 2

local PAWN = 1
local KNIGHT = 2
local BISHOP = 3
local ROOK = 4
local QUEEN = 5
local KING = 6

-- A piece code is its type for white and its type + 8 for black. 7 and 8 are unused by pieces, so EMPTY and OFF
-- fit into the same dense lookup tables.
local WP, WN, WB, WR, WQ, WK = 1, 2, 3, 4, 5, 6
local BP, BN, BB, BR, BQ, BK = 9, 10, 11, 12, 13, 14
local EMPTY = 7
local OFF = 8

local NORMAL = 0
local DOUBLE = 1
local EN_PASSANT = 2
local CASTLE_KING = 3
local CASTLE_QUEEN = 4

-- Indexed by piece code: the side (1, 2) a piece belongs to, 0 for EMPTY and 3 for OFF.
local COLOR: { number } = { 1, 1, 1, 1, 1, 1, 0, 3, 2, 2, 2, 2, 2, 2 }
local TYPE: { number } = { 1, 2, 3, 4, 5, 6, 0, 0, 1, 2, 3, 4, 5, 6 }
local CHARS: { string } = { "P", "N", "B", "R", "Q", "K", ".", "#", "p", "n", "b", "r", "q", "k" }

local PIECE_FROM_CHAR: { [string]: number } = {
	P = WP, N = WN, B = WB, R = WR, Q = WQ, K = WK,
	p = BP, n = BN, b = BB, r = BR, q = BQ, k = BK,
}

local START_FEN = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"

local FILES = "abcdefgh"
local SQUARES: { number } = {}
local NAMES: { string } = {}
for row = 0, 7 do
	for file = 0, 7 do
		local sq = 22 + 10 * row + file
		SQUARES[#SQUARES + 1] = sq
		NAMES[sq] = string.sub(FILES, file + 1, file + 1) .. tostring(8 - row)
	end
end

local function square(file: string, rank: number): number
	return 22 + 10 * (8 - rank) + (string.byte(file) - 97)
end

local A1, B1, C1, D1, E1, F1, G1, H1 = square("a", 1), square("b", 1), square("c", 1), square("d", 1), square("e", 1), square("f", 1), square("g", 1), square("h", 1)
local A8, B8, C8, D8, E8, F8, G8, H8 = square("a", 8), square("b", 8), square("c", 8), square("d", 8), square("e", 8), square("f", 8), square("g", 8), square("h", 8)

-- Moving from or to one of these squares takes the castling rights in the mask away (the mask is ANDed in).
local CASTLE_MASK: { number } = table.create(120, 15)
CASTLE_MASK[E1] = 12
CASTLE_MASK[H1] = 14
CASTLE_MASK[A1] = 13
CASTLE_MASK[E8] = 3
CASTLE_MASK[H8] = 11
CASTLE_MASK[A8] = 7

local KNIGHT_DELTAS: { number } = { -21, -19, -12, -8, 8, 12, 19, 21 }
local KING_DELTAS: { number } = { -11, -10, -9, -1, 1, 9, 10, 11 }
local ROOK_DIRS: { number } = { -10, -1, 1, 10 }
local BISHOP_DIRS: { number } = { -11, -9, 9, 11 }

local function square_name(sq: number): string
	return NAMES[sq]
end

-- "e4" -> index, or nil when the text is not a square.
local function parse_square(text: string): number?
	if #text ~= 2 then
		return nil
	end
	local lowered = string.lower(text)
	local file = string.byte(lowered, 1) - 96
	local rank = string.byte(lowered, 2) - 48
	if file < 1 or file > 8 or rank < 1 or rank > 8 then
		return nil
	end
	return 22 + 10 * (8 - rank) + (file - 1)
end

-- The coordinates of a square as the table shows them: x is the file (1 = a), y the row (1 = rank 8).
local function xy(sq: number): (number, number)
	local offset = sq - 22
	return offset % 10 + 1, offset // 10 + 1
end

local function from_xy(x: number, y: number): number
	return 22 + 10 * (y - 1) + (x - 1)
end

-- Whether a piece of side `by` attacks the square.
local function attacked(board: { number }, sq: number, by: number): boolean
	if by == WHITE then
		if board[sq + 9] == WP or board[sq + 11] == WP then
			return true
		end
	elseif board[sq - 9] == BP or board[sq - 11] == BP then
		return true
	end

	local base = (by - 1) * 8
	local knight = KNIGHT + base
	local king = KING + base
	for i = 1, 8 do
		if board[sq + KNIGHT_DELTAS[i]] == knight or board[sq + KING_DELTAS[i]] == king then
			return true
		end
	end

	local rook = ROOK + base
	local bishop = BISHOP + base
	local queen = QUEEN + base
	for i = 1, 4 do
		local step = ROOK_DIRS[i]
		local s = sq + step
		local piece = board[s]
		while piece == EMPTY do
			s += step
			piece = board[s]
		end
		if piece == rook or piece == queen then
			return true
		end

		step = BISHOP_DIRS[i]
		s = sq + step
		piece = board[s]
		while piece == EMPTY do
			s += step
			piece = board[s]
		end
		if piece == bishop or piece == queen then
			return true
		end
	end
	return false
end

-- The squares of the pieces of side `by` that attack the square.
local function attackers(board: { number }, sq: number, by: number): { number }
	local found: { number } = {}
	local n = 0
	local base = (by - 1) * 8

	local pawn = PAWN + base
	local first = if by == WHITE then sq + 9 else sq - 9
	local second = if by == WHITE then sq + 11 else sq - 11
	if board[first] == pawn then
		n += 1
		found[n] = first
	end
	if board[second] == pawn then
		n += 1
		found[n] = second
	end

	local knight = KNIGHT + base
	local king = KING + base
	for i = 1, 8 do
		local k = sq + KNIGHT_DELTAS[i]
		if board[k] == knight then
			n += 1
			found[n] = k
		end
		local g = sq + KING_DELTAS[i]
		if board[g] == king then
			n += 1
			found[n] = g
		end
	end

	local rook = ROOK + base
	local bishop = BISHOP + base
	local queen = QUEEN + base
	for i = 1, 4 do
		local step = ROOK_DIRS[i]
		local s = sq + step
		local piece = board[s]
		while piece == EMPTY do
			s += step
			piece = board[s]
		end
		if piece == rook or piece == queen then
			n += 1
			found[n] = s
		end

		step = BISHOP_DIRS[i]
		s = sq + step
		piece = board[s]
		while piece == EMPTY do
			s += step
			piece = board[s]
		end
		if piece == bishop or piece == queen then
			n += 1
			found[n] = s
		end
	end
	return found
end

-- Moves the pieces of a move on the board and updates the king squares; nothing else changes.
local function apply(pos: Position, m: Move)
	local board = pos.board
	local to = m.to
	local piece = m.piece
	local kind = m.kind
	board[m.from] = EMPTY
	if kind == NORMAL or kind == DOUBLE then
		if m.promo == 0 then
			board[to] = piece
		else
			board[to] = m.promo + (if piece > 8 then 8 else 0)
		end
	elseif kind == EN_PASSANT then
		board[to] = piece
		board[if piece == WP then to + 10 else to - 10] = EMPTY
	elseif kind == CASTLE_KING then
		board[to] = piece
		board[to + 1] = EMPTY
		board[to - 1] = if piece == WK then WR else BR
	else
		board[to] = piece
		board[to - 2] = EMPTY
		board[to + 1] = if piece == WK then WR else BR
	end
	if piece == WK then
		pos.kings[WHITE] = to
	elseif piece == BK then
		pos.kings[BLACK] = to
	end
end

local function revert(pos: Position, m: Move)
	local board = pos.board
	local to = m.to
	local piece = m.piece
	local kind = m.kind
	board[m.from] = piece
	if kind == NORMAL or kind == DOUBLE then
		board[to] = m.captured
	elseif kind == EN_PASSANT then
		board[to] = EMPTY
		board[if piece == WP then to + 10 else to - 10] = m.captured
	elseif kind == CASTLE_KING then
		board[to] = EMPTY
		board[to + 1] = if piece == WK then WR else BR
		board[to - 1] = EMPTY
	else
		board[to] = EMPTY
		board[to - 2] = if piece == WK then WR else BR
		board[to + 1] = EMPTY
	end
	if piece == WK then
		pos.kings[WHITE] = m.from
	elseif piece == BK then
		pos.kings[BLACK] = m.from
	end
end

-- Plays a move. The old castling rights, en passant square and halfmove clock are returned for unmake.
local function make(pos: Position, m: Move): (number, number, number)
	local castling = pos.castling
	local ep = pos.ep
	local halfmove = pos.halfmove
	apply(pos, m)
	pos.castling = bit32.band(castling, CASTLE_MASK[m.from], CASTLE_MASK[m.to])
	pos.ep = if m.kind == DOUBLE then (m.from + m.to) // 2 else 0
	pos.halfmove = if TYPE[m.piece] == PAWN or m.captured ~= EMPTY then 0 else halfmove + 1
	if pos.side == BLACK then
		pos.fullmove += 1
	end
	pos.side = 3 - pos.side
	return castling, ep, halfmove
end

local function unmake(pos: Position, m: Move, castling: number, ep: number, halfmove: number)
	pos.side = 3 - pos.side
	if pos.side == BLACK then
		pos.fullmove -= 1
	end
	pos.castling = castling
	pos.ep = ep
	pos.halfmove = halfmove
	revert(pos, m)
end

local function add_pawn_move(moves: { Move }, n: number, from: number, to: number, piece: number, captured: number, promotes: boolean): number
	if promotes then
		for promo = QUEEN, KNIGHT, -1 do
			n += 1
			moves[n] = { from = from, to = to, piece = piece, captured = captured, promo = promo, kind = NORMAL }
		end
	else
		n += 1
		moves[n] = { from = from, to = to, piece = piece, captured = captured, promo = 0, kind = NORMAL }
	end
	return n
end

local function slide(moves: { Move }, n: number, board: { number }, from: number, piece: number, dirs: { number }, them: number): number
	for i = 1, 4 do
		local step = dirs[i]
		local to = from + step
		local target = board[to]
		while target == EMPTY do
			n += 1
			moves[n] = { from = from, to = to, piece = piece, captured = EMPTY, promo = 0, kind = NORMAL }
			to += step
			target = board[to]
		end
		if COLOR[target] == them then
			n += 1
			moves[n] = { from = from, to = to, piece = piece, captured = target, promo = 0, kind = NORMAL }
		end
	end
	return n
end

-- Every move that obeys the movement rules, whether or not it leaves the own king in check.
local function pseudo_moves(pos: Position): { Move }
	local board = pos.board
	local us = pos.side
	local them = 3 - us
	local moves: { Move } = {}
	local n = 0
	local forward = if us == WHITE then -10 else 10
	local ep = pos.ep
	local castling = pos.castling

	for i = 1, 64 do
		local from = SQUARES[i]
		local piece = board[from]
		if COLOR[piece] == us then
			local kind = TYPE[piece]
			if kind == PAWN then
				local to = from + forward
				local promotes = if us == WHITE then to <= 29 else to >= 92
				if board[to] == EMPTY then
					n = add_pawn_move(moves, n, from, to, piece, EMPTY, promotes)
					local row = (from - 22) // 10
					if row == (if us == WHITE then 6 else 1) then
						local to2 = to + forward
						if board[to2] == EMPTY then
							n += 1
							moves[n] = { from = from, to = to2, piece = piece, captured = EMPTY, promo = 0, kind = DOUBLE }
						end
					end
				end
				for step = -1, 1, 2 do
					local target_sq = to + step
					local target = board[target_sq]
					if COLOR[target] == them then
						n = add_pawn_move(moves, n, from, target_sq, piece, target, promotes)
					elseif target_sq == ep then
						n += 1
						moves[n] = { from = from, to = target_sq, piece = piece, captured = if us == WHITE then BP else WP, promo = 0, kind = EN_PASSANT }
					end
				end
			elseif kind == KNIGHT then
				for k = 1, 8 do
					local to = from + KNIGHT_DELTAS[k]
					local target = board[to]
					if target == EMPTY or COLOR[target] == them then
						n += 1
						moves[n] = { from = from, to = to, piece = piece, captured = target, promo = 0, kind = NORMAL }
					end
				end
			elseif kind == BISHOP then
				n = slide(moves, n, board, from, piece, BISHOP_DIRS, them)
			elseif kind == ROOK then
				n = slide(moves, n, board, from, piece, ROOK_DIRS, them)
			elseif kind == QUEEN then
				n = slide(moves, n, board, from, piece, BISHOP_DIRS, them)
				n = slide(moves, n, board, from, piece, ROOK_DIRS, them)
			else
				for k = 1, 8 do
					local to = from + KING_DELTAS[k]
					local target = board[to]
					if target == EMPTY or COLOR[target] == them then
						n += 1
						moves[n] = { from = from, to = to, piece = piece, captured = target, promo = 0, kind = NORMAL }
					end
				end
				-- The squares the king crosses must be free and not attacked; the landing square is checked with
				-- every other move, after the rook has been moved as well.
				if us == WHITE and from == E1 then
					if bit32.band(castling, 1) ~= 0 and board[H1] == WR and board[F1] == EMPTY and board[G1] == EMPTY
						and not attacked(board, E1, BLACK) and not attacked(board, F1, BLACK) then
						n += 1
						moves[n] = { from = E1, to = G1, piece = piece, captured = EMPTY, promo = 0, kind = CASTLE_KING }
					end
					if bit32.band(castling, 2) ~= 0 and board[A1] == WR and board[D1] == EMPTY and board[C1] == EMPTY and board[B1] == EMPTY
						and not attacked(board, E1, BLACK) and not attacked(board, D1, BLACK) then
						n += 1
						moves[n] = { from = E1, to = C1, piece = piece, captured = EMPTY, promo = 0, kind = CASTLE_QUEEN }
					end
				elseif us == BLACK and from == E8 then
					if bit32.band(castling, 4) ~= 0 and board[H8] == BR and board[F8] == EMPTY and board[G8] == EMPTY
						and not attacked(board, E8, WHITE) and not attacked(board, F8, WHITE) then
						n += 1
						moves[n] = { from = E8, to = G8, piece = piece, captured = EMPTY, promo = 0, kind = CASTLE_KING }
					end
					if bit32.band(castling, 8) ~= 0 and board[A8] == BR and board[D8] == EMPTY and board[C8] == EMPTY and board[B8] == EMPTY
						and not attacked(board, E8, WHITE) and not attacked(board, D8, WHITE) then
						n += 1
						moves[n] = { from = E8, to = C8, piece = piece, captured = EMPTY, promo = 0, kind = CASTLE_QUEEN }
					end
				end
			end
		end
	end
	return moves
end

-- The moves of the side to move that do not leave its own king attacked.
local function legal_moves(pos: Position): { Move }
	local pseudo = pseudo_moves(pos)
	local board = pos.board
	local kings = pos.kings
	local us = pos.side
	local them = 3 - us
	local legal: { Move } = {}
	local n = 0
	for i = 1, #pseudo do
		local m = pseudo[i]
		apply(pos, m)
		if not attacked(board, kings[us], them) then
			n += 1
			legal[n] = m
		end
		revert(pos, m)
	end
	return legal
end

local function in_check(pos: Position): boolean
	return attacked(pos.board, pos.kings[pos.side], 3 - pos.side)
end

local function copy(pos: Position): Position
	return {
		board = table.clone(pos.board),
		side = pos.side,
		castling = pos.castling,
		ep = pos.ep,
		halfmove = pos.halfmove,
		fullmove = pos.fullmove,
		kings = table.clone(pos.kings),
	}
end

local function uci(m: Move): string
	local text = NAMES[m.from] .. NAMES[m.to]
	if m.promo ~= 0 then
		text ..= string.lower(CHARS[m.promo])
	end
	return text
end

-- Standard algebraic notation without the check or mate sign; `legal` is the list of legal moves before the move.
local function san_base(m: Move, legal: { Move }): string
	local kind = m.kind
	if kind == CASTLE_KING then
		return "O-O"
	elseif kind == CASTLE_QUEEN then
		return "O-O-O"
	end

	local piece_type = TYPE[m.piece]
	local capture = m.captured ~= EMPTY
	if piece_type == PAWN then
		local text = ""
		if capture then
			text = string.sub(NAMES[m.from], 1, 1) .. "x"
		end
		text ..= NAMES[m.to]
		if m.promo ~= 0 then
			text ..= "=" .. CHARS[m.promo]
		end
		return text
	end

	local text = CHARS[piece_type]
	local ambiguous = false
	local same_file = false
	local same_row = false
	local file = (m.from - 22) % 10
	local row = (m.from - 22) // 10
	for i = 1, #legal do
		local other = legal[i]
		if other.to == m.to and other.piece == m.piece and other.from ~= m.from then
			ambiguous = true
			if (other.from - 22) % 10 == file then
				same_file = true
			end
			if (other.from - 22) // 10 == row then
				same_row = true
			end
		end
	end
	if ambiguous then
		local name = NAMES[m.from]
		if not same_file then
			text ..= string.sub(name, 1, 1)
		elseif not same_row then
			text ..= string.sub(name, 2, 2)
		else
			text ..= name
		end
	end
	if capture then
		text ..= "x"
	end
	return text .. NAMES[m.to]
end

-- The move in SAN including "+" or "#"; the position is left as it was.
local function san(pos: Position, m: Move, legal: { Move }): string
	local text = san_base(m, legal)
	local castling, ep, halfmove = make(pos, m)
	local suffix = ""
	if in_check(pos) then
		suffix = if #legal_moves(pos) == 0 then "#" else "+"
	end
	unmake(pos, m, castling, ep, halfmove)
	return text .. suffix
end

-- True when neither side can ever give mate: bare kings, a lone minor piece, or bishops that all stand on one colour.
local function insufficient_material(pos: Position): boolean
	local board = pos.board
	local knights = 0
	local light_bishops = 0
	local dark_bishops = 0
	for i = 1, 64 do
		local piece_type = TYPE[board[SQUARES[i]]]
		if piece_type == PAWN or piece_type == ROOK or piece_type == QUEEN then
			return false
		elseif piece_type == KNIGHT then
			knights += 1
		elseif piece_type == BISHOP then
			-- a8 is a light square, and the square colour alternates with the row and the file
			if ((i - 1) // 8 + (i - 1) % 8) % 2 == 0 then
				light_bishops += 1
			else
				dark_bishops += 1
			end
		end
	end
	if knights + light_bishops + dark_bishops <= 1 then
		return true
	end
	return knights == 0 and (light_bishops == 0 or dark_bishops == 0)
end

-- Identifies a position for the repetition rule: the pieces, the side to move, the castling rights and an en
-- passant square only when a capture there is possible. `legal` holds the legal moves of the position.
local function position_key(pos: Position, legal: { Move }): string
	local board = pos.board
	local cells: { string } = table.create(64, ".")
	for i = 1, 64 do
		cells[i] = CHARS[board[SQUARES[i]]]
	end
	local ep = "-"
	if pos.ep ~= 0 then
		for i = 1, #legal do
			if legal[i].kind == EN_PASSANT then
				ep = NAMES[pos.ep]
				break
			end
		end
	end
	return table.concat(cells) .. (if pos.side == WHITE then "w" else "b") .. tostring(pos.castling) .. ep
end

local function to_fen(pos: Position): string
	local rows: { string } = {}
	for row = 0, 7 do
		local text = ""
		local gap = 0
		for file = 0, 7 do
			local piece = pos.board[22 + 10 * row + file]
			if piece == EMPTY then
				gap += 1
			else
				if gap > 0 then
					text ..= tostring(gap)
					gap = 0
				end
				text ..= CHARS[piece]
			end
		end
		if gap > 0 then
			text ..= tostring(gap)
		end
		rows[row + 1] = text
	end

	local rights = ""
	if bit32.band(pos.castling, 1) ~= 0 then
		rights ..= "K"
	end
	if bit32.band(pos.castling, 2) ~= 0 then
		rights ..= "Q"
	end
	if bit32.band(pos.castling, 4) ~= 0 then
		rights ..= "k"
	end
	if bit32.band(pos.castling, 8) ~= 0 then
		rights ..= "q"
	end
	if rights == "" then
		rights = "-"
	end
	local ep = if pos.ep ~= 0 then NAMES[pos.ep] else "-"
	return table.concat(rows, "/") .. " " .. (if pos.side == WHITE then "w" else "b") .. " " .. rights .. " " .. ep .. " "
		.. tostring(pos.halfmove) .. " " .. tostring(pos.fullmove)
end

-- Reads a FEN. The last fields may be left out; castling rights and an en passant square that do not fit the pieces
-- are dropped. Returns the position, or nil and a message.
local function from_fen(fen: string): (Position?, string?)
	local fields: { string } = {}
	for token in string.gmatch(fen, "%S+") do
		fields[#fields + 1] = token
	end
	if #fields == 0 then
		return nil, "the FEN is empty"
	end

	local board: { number } = table.create(120, OFF)
	for i = 1, 64 do
		board[SQUARES[i]] = EMPTY
	end
	local kings: { number } = { 0, 0 }
	local king_count: { number } = { 0, 0 }

	local rows = string.split(fields[1], "/")
	if #rows ~= 8 then
		return nil, "the piece placement needs 8 ranks"
	end
	for r = 1, 8 do
		local row = rows[r]
		local file = 0
		for i = 1, #row do
			local char = string.sub(row, i, i)
			local code = PIECE_FROM_CHAR[char]
			if code ~= nil then
				if file > 7 then
					return nil, "a rank has more than 8 squares"
				end
				if TYPE[code] == PAWN and (r == 1 or r == 8) then
					return nil, "a pawn cannot stand on the first or the last rank"
				end
				local sq = 22 + 10 * (r - 1) + file
				board[sq] = code
				if TYPE[code] == KING then
					local color = COLOR[code]
					king_count[color] += 1
					kings[color] = sq
				end
				file += 1
			else
				local gap = tonumber(char)
				if gap == nil or gap < 1 or gap > 8 then
					return nil, "the piece placement has a character that is not a piece or a number"
				end
				file += gap
			end
		end
		if file ~= 8 then
			return nil, "a rank does not have 8 squares"
		end
	end
	if king_count[WHITE] ~= 1 or king_count[BLACK] ~= 1 then
		return nil, "each side needs exactly one king"
	end

	local side = WHITE
	local side_field = fields[2]
	if side_field ~= nil then
		if side_field == "b" then
			side = BLACK
		elseif side_field ~= "w" then
			return nil, "the side to move must be w or b"
		end
	end

	local castling = 0
	local castling_field = fields[3]
	if castling_field ~= nil and castling_field ~= "-" then
		for i = 1, #castling_field do
			local char = string.sub(castling_field, i, i)
			if char == "K" then
				castling = bit32.bor(castling, 1)
			elseif char == "Q" then
				castling = bit32.bor(castling, 2)
			elseif char == "k" then
				castling = bit32.bor(castling, 4)
			elseif char == "q" then
				castling = bit32.bor(castling, 8)
			else
				return nil, "the castling field may only hold K, Q, k and q"
			end
		end
	end
	if board[E1] ~= WK or board[H1] ~= WR then
		castling = bit32.band(castling, 14)
	end
	if board[E1] ~= WK or board[A1] ~= WR then
		castling = bit32.band(castling, 13)
	end
	if board[E8] ~= BK or board[H8] ~= BR then
		castling = bit32.band(castling, 11)
	end
	if board[E8] ~= BK or board[A8] ~= BR then
		castling = bit32.band(castling, 7)
	end

	local ep = 0
	local ep_field = fields[4]
	if ep_field ~= nil and ep_field ~= "-" then
		local target = parse_square(ep_field)
		if target == nil then
			return nil, "the en passant field is not a square"
		end
		local row = (target - 22) // 10
		if side == WHITE and row == 2 and board[target] == EMPTY and board[target + 10] == BP and board[target - 10] == EMPTY then
			ep = target
		elseif side == BLACK and row == 5 and board[target] == EMPTY and board[target - 10] == WP and board[target + 10] == EMPTY then
			ep = target
		end
	end

	local halfmove = 0
	if fields[5] ~= nil then
		local value = tonumber(fields[5])
		if value == nil or value < 0 or value > 100000 or value ~= math.floor(value) then
			return nil, "the halfmove clock must be a whole number"
		end
		halfmove = value
	end
	local fullmove = 1
	if fields[6] ~= nil then
		local value = tonumber(fields[6])
		if value == nil or value < 1 or value > 100000 or value ~= math.floor(value) then
			return nil, "the move number must be a whole number of at least 1"
		end
		fullmove = value
	end

	if attacked(board, kings[3 - side], side) then
		return nil, "the side that is not to move is in check"
	end
	return {
		board = board,
		side = side,
		castling = castling,
		ep = ep,
		halfmove = halfmove,
		fullmove = fullmove,
		kings = kings,
	}, nil
end

local function start_position(): Position
	local position = from_fen(START_FEN)
	assert(position ~= nil)
	return position
end

-- Counts the leaf nodes of the legal move tree; the position is left as it was.
local function perft(pos: Position, depth: number): number
	if depth <= 0 then
		return 1
	end
	local moves = legal_moves(pos)
	if depth == 1 then
		return #moves
	end
	local nodes = 0
	for i = 1, #moves do
		local m = moves[i]
		local castling, ep, halfmove = make(pos, m)
		nodes += perft(pos, depth - 1)
		unmake(pos, m, castling, ep, halfmove)
	end
	return nodes
end

return {
	WHITE = WHITE,
	BLACK = BLACK,
	PAWN = PAWN,
	KNIGHT = KNIGHT,
	BISHOP = BISHOP,
	ROOK = ROOK,
	QUEEN = QUEEN,
	KING = KING,
	EMPTY = EMPTY,
	NORMAL = NORMAL,
	DOUBLE = DOUBLE,
	EN_PASSANT = EN_PASSANT,
	CASTLE_KING = CASTLE_KING,
	CASTLE_QUEEN = CASTLE_QUEEN,
	START_FEN = START_FEN,
	SQUARES = SQUARES,
	COLOR = COLOR,
	TYPE = TYPE,
	CHARS = CHARS,
	square_name = square_name,
	parse_square = parse_square,
	xy = xy,
	from_xy = from_xy,
	from_fen = from_fen,
	to_fen = to_fen,
	start_position = start_position,
	copy = copy,
	legal_moves = legal_moves,
	make = make,
	unmake = unmake,
	in_check = in_check,
	attackers = attackers,
	san = san,
	san_base = san_base,
	uci = uci,
	insufficient_material = insufficient_material,
	position_key = position_key,
	perft = perft,
}
