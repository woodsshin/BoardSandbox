--!strict
-- The computer player of chess: plain Luau without any bse call, on top of the rules in chess.luau.
-- logic.luau answers rules.query("think") with it.
--
-- An alpha-beta search, deepened one move at a time until the effort, the number of positions it may look at, is
-- used up. At the end of a line the captures are played out (quiescence) and the position is judged by its
-- material and by where the pieces stand.

local chess = require("./chess")

type Move = chess.Move
type Position = chess.Position

local ai = {}

local EMPTY = chess.EMPTY
local TYPE = chess.TYPE
local PAWN = chess.PAWN
local KING = chess.KING
local QUEEN = chess.QUEEN
local SQUARES = chess.SQUARES
local pseudo_moves = chess.pseudo_moves
local attacked = chess.attacked
local make = chess.make
local unmake = chess.unmake

local MATE = 1000000
local MAX_DEPTH = 32
local QUIESCENCE_DEPTH = 6

-- By piece type: pawn, knight, bishop, rook, queen, king.
local WORTH: { number } = { 100, 320, 330, 500, 900, 0 }

-- Where a piece likes to stand, seen from white's side with rank 8 first (the "simplified evaluation function"
-- tables that many small engines use). The king has one table for the middle game and one for the endgame.
local PAWN_TABLE = {
	0, 0, 0, 0, 0, 0, 0, 0,
	50, 50, 50, 50, 50, 50, 50, 50,
	10, 10, 20, 30, 30, 20, 10, 10,
	5, 5, 10, 25, 25, 10, 5, 5,
	0, 0, 0, 20, 20, 0, 0, 0,
	5, -5, -10, 0, 0, -10, -5, 5,
	5, 10, 10, -20, -20, 10, 10, 5,
	0, 0, 0, 0, 0, 0, 0, 0,
}
local KNIGHT_TABLE = {
	-50, -40, -30, -30, -30, -30, -40, -50,
	-40, -20, 0, 0, 0, 0, -20, -40,
	-30, 0, 10, 15, 15, 10, 0, -30,
	-30, 5, 15, 20, 20, 15, 5, -30,
	-30, 0, 15, 20, 20, 15, 0, -30,
	-30, 5, 10, 15, 15, 10, 5, -30,
	-40, -20, 0, 5, 5, 0, -20, -40,
	-50, -40, -30, -30, -30, -30, -40, -50,
}
local BISHOP_TABLE = {
	-20, -10, -10, -10, -10, -10, -10, -20,
	-10, 0, 0, 0, 0, 0, 0, -10,
	-10, 0, 5, 10, 10, 5, 0, -10,
	-10, 5, 5, 10, 10, 5, 5, -10,
	-10, 0, 10, 10, 10, 10, 0, -10,
	-10, 10, 10, 10, 10, 10, 10, -10,
	-10, 5, 0, 0, 0, 0, 5, -10,
	-20, -10, -10, -10, -10, -10, -10, -20,
}
local ROOK_TABLE = {
	0, 0, 0, 0, 0, 0, 0, 0,
	5, 10, 10, 10, 10, 10, 10, 5,
	-5, 0, 0, 0, 0, 0, 0, -5,
	-5, 0, 0, 0, 0, 0, 0, -5,
	-5, 0, 0, 0, 0, 0, 0, -5,
	-5, 0, 0, 0, 0, 0, 0, -5,
	-5, 0, 0, 0, 0, 0, 0, -5,
	0, 0, 0, 5, 5, 0, 0, 0,
}
local QUEEN_TABLE = {
	-20, -10, -10, -5, -5, -10, -10, -20,
	-10, 0, 0, 0, 0, 0, 0, -10,
	-10, 0, 5, 5, 5, 5, 0, -10,
	-5, 0, 5, 5, 5, 5, 0, -5,
	0, 0, 5, 5, 5, 5, 0, -5,
	-10, 5, 5, 5, 5, 5, 0, -10,
	-10, 0, 5, 0, 0, 0, 0, -10,
	-20, -10, -10, -5, -5, -10, -10, -20,
}
local KING_TABLE = {
	-30, -40, -40, -50, -50, -40, -40, -30,
	-30, -40, -40, -50, -50, -40, -40, -30,
	-30, -40, -40, -50, -50, -40, -40, -30,
	-30, -40, -40, -50, -50, -40, -40, -30,
	-20, -30, -30, -40, -40, -30, -30, -20,
	-10, -20, -20, -20, -20, -20, -20, -10,
	20, 20, 0, 0, 0, 0, 20, 20,
	20, 30, 10, 0, 0, 10, 30, 20,
}
local KING_END_TABLE = {
	-50, -40, -30, -20, -20, -30, -40, -50,
	-30, -20, -10, 0, 0, -10, -20, -30,
	-30, -10, 20, 30, 30, 20, -10, -30,
	-30, -10, 30, 40, 40, 30, -10, -30,
	-30, -10, 30, 40, 40, 30, -10, -30,
	-30, -10, 20, 30, 30, 20, -10, -30,
	-30, -30, 0, 0, 0, 0, -30, -30,
	-50, -30, -30, -30, -30, -30, -30, -50,
}

-- PLACE[piece code][square] is the worth of the piece plus what the square adds, for white and for black pieces;
-- the king's square is added separately because it depends on the phase of the game.
local PLACE: { { number } } = {}
local KING_PLACE: { { number } } = { table.create(120, 0), table.create(120, 0) }
local KING_END_PLACE: { { number } } = { table.create(120, 0), table.create(120, 0) }
do
	local tables = { PAWN_TABLE, KNIGHT_TABLE, BISHOP_TABLE, ROOK_TABLE, QUEEN_TABLE }
	for code = 1, 14 do
		PLACE[code] = table.create(120, 0)
	end
	for i = 1, 64 do
		local sq = SQUARES[i]
		local row = (i - 1) // 8
		local mirrored = (7 - row) * 8 + (i - 1) % 8 + 1
		for kind = 1, 5 do
			PLACE[kind][sq] = WORTH[kind] + tables[kind][i]
			PLACE[kind + 8][sq] = WORTH[kind] + tables[kind][mirrored]
		end
		KING_PLACE[1][sq] = KING_TABLE[i]
		KING_PLACE[2][sq] = KING_TABLE[mirrored]
		KING_END_PLACE[1][sq] = KING_END_TABLE[i]
		KING_END_PLACE[2][sq] = KING_END_TABLE[mirrored]
	end
end

-- How far a square is from the middle of the board, 0 to 6, and how far two squares are apart.
local function from_centre(sq: number): number
	local file = (sq - 22) % 10
	local row = (sq - 22) // 10
	return math.max(3 - file, file - 4) + math.max(3 - row, row - 4)
end

local function apart(a: number, b: number): number
	return math.abs((a - 22) % 10 - (b - 22) % 10) + math.abs((a - 22) // 10 - (b - 22) // 10)
end

-- What the position is worth to the side to move, in hundredths of a pawn.
local function evaluate(pos: Position): number
	local board = pos.board
	local white = 0
	local black = 0
	-- the worth of the knights, bishops, rooks and queens of each side, which tells the phase of the game
	local white_pieces = 0
	local black_pieces = 0
	local white_bishops = 0
	local black_bishops = 0
	for i = 1, 64 do
		local sq = SQUARES[i]
		local piece = board[sq]
		if piece ~= EMPTY then
			local kind = TYPE[piece]
			if kind ~= KING then
				if piece < 8 then
					white += PLACE[piece][sq]
					if kind ~= PAWN then
						white_pieces += WORTH[kind]
						if kind == 3 then
							white_bishops += 1
						end
					end
				else
					black += PLACE[piece][sq]
					if kind ~= PAWN then
						black_pieces += WORTH[kind]
						if kind == 3 then
							black_bishops += 1
						end
					end
				end
			end
		end
	end
	if white_bishops >= 2 then
		white += 30
	end
	if black_bishops >= 2 then
		black += 30
	end

	local white_king = pos.kings[1]
	local black_king = pos.kings[2]
	if white_pieces + black_pieces > 2600 then
		white += KING_PLACE[1][white_king]
		black += KING_PLACE[2][black_king]
	else
		white += KING_END_PLACE[1][white_king]
		black += KING_END_PLACE[2][black_king]
		-- With a clear lead and few pieces left the way to win is to push the other king to the edge and bring
		-- one's own king next to it; a search of a few moves does not find that without being told.
		local lead = white - black
		if lead > 300 and black_pieces <= 330 then
			white += 12 * from_centre(black_king) + 6 * (14 - apart(white_king, black_king))
		elseif lead < -300 and white_pieces <= 330 then
			black += 12 * from_centre(white_king) + 6 * (14 - apart(white_king, black_king))
		end
	end
	return if pos.side == 1 then white - black else black - white
end

-- Captures of the most valuable piece by the least valuable one first, then promotions.
local function capture_order(m: Move): number
	local score = 0
	if m.captured ~= EMPTY then
		score = 10000 + 16 * WORTH[TYPE[m.captured]] - WORTH[TYPE[m.piece]]
	end
	if m.promo ~= 0 then
		score += 9000 + WORTH[m.promo]
	end
	return score
end

local function same(a: Move?, b: Move): boolean
	return a ~= nil and a.from == b.from and a.to == b.to and a.promo == b.promo
end

export type Options = {
	-- the halfmove clock at which the game is drawn, 0 for no such rule
	halfmove_limit: number?,
	-- the positions the game has been through, as chess.position_key gives them; going back to one counts as a draw
	seen: { [string]: boolean }?,
}

export type Result = {
	-- nil when the side to move has no legal move
	move: Move?,
	-- hundredths of a pawn for the side to move; close to MATE when it mates
	score: number,
	-- moves looked ahead and positions looked at
	depth: number,
	nodes: number,
}

ai.MATE = MATE
ai.evaluate = evaluate

-- Chooses a move for the side to move. The position is left as it was. `effort` is the number of positions the
-- search may look at: a few hundred see one move and its answer, tens of thousands four or five moves.
function ai.choose(position: Position, effort: number, options: Options?): Result
	local pos = chess.copy(position)
	local board = pos.board
	local kings = pos.kings
	local halfmove_limit = if options and options.halfmove_limit then options.halfmove_limit else 0
	local seen = if options and options.seen then options.seen else {}
	local nodes = 0
	-- the positions the search may look at; lifted for the first look ahead
	local limit = effort
	local stopped = false
	-- by ply, the quiet move that last refuted a line there
	local killers: { Move } = {}
	-- how often a quiet move from one square to another was the best, by from * 128 + to
	local history: { [number]: number } = {}

	local function quiesce(alpha: number, beta: number, depth: number): number
		nodes += 1
		local stand = evaluate(pos)
		if stand >= beta or depth == 0 then
			return stand
		end
		if stand > alpha then
			alpha = stand
		end
		local us = pos.side
		local moves = pseudo_moves(pos)
		local captures: { Move } = {}
		local order: { number } = {}
		local count = 0
		for i = 1, #moves do
			local m = moves[i]
			if m.captured ~= EMPTY or m.promo == QUEEN then
				local score = capture_order(m)
				local slot = count + 1
				while slot > 1 and order[slot - 1] < score do
					captures[slot] = captures[slot - 1]
					order[slot] = order[slot - 1]
					slot -= 1
				end
				captures[slot] = m
				order[slot] = score
				count += 1
			end
		end
		for i = 1, count do
			local m = captures[i]
			local castling, ep, halfmove = make(pos, m)
			if not attacked(board, kings[us], 3 - us) then
				local score = -quiesce(-beta, -alpha, depth - 1)
				if score > alpha then
					alpha = score
				end
			end
			unmake(pos, m, castling, ep, halfmove)
			if alpha >= beta then
				break
			end
		end
		return alpha
	end

	-- The moves of the position, the most promising first: `first` (the best move of the last, shallower look), then
	-- captures and promotions, then the quiet moves that did well elsewhere.
	local function ordered(first: Move?, ply: number): { Move }
		local moves = pseudo_moves(pos)
		local killer = killers[ply]
		local scores: { number } = {}
		for i = 1, #moves do
			local m = moves[i]
			local score = capture_order(m)
			if same(first, m) then
				score = 1000000
			elseif score == 0 then
				score = if same(killer, m) then 8000 else math.min(7000, history[m.from * 128 + m.to] or 0)
			end
			scores[i] = score
		end
		-- insertion sort: the lists are short and mostly zero
		for i = 2, #moves do
			local m = moves[i]
			local score = scores[i]
			local slot = i
			while slot > 1 and scores[slot - 1] < score do
				moves[slot] = moves[slot - 1]
				scores[slot] = scores[slot - 1]
				slot -= 1
			end
			moves[slot] = m
			scores[slot] = score
		end
		return moves
	end

	local function search(depth: number, alpha: number, beta: number, ply: number): number
		if nodes >= limit then
			stopped = true
			return 0
		end
		local us = pos.side
		local them = 3 - us
		local checked = attacked(board, kings[us], them)
		if checked and ply < MAX_DEPTH then
			-- A check has few answers, and stopping in the middle of one misjudges the position.
			depth += 1
		end
		if depth <= 0 then
			return quiesce(alpha, beta, QUIESCENCE_DEPTH)
		end
		nodes += 1

		local moves = ordered(nil, ply)
		local best = -MATE
		local played = 0
		for i = 1, #moves do
			local m = moves[i]
			local castling, ep, halfmove = make(pos, m)
			if attacked(board, kings[us], them) then
				unmake(pos, m, castling, ep, halfmove)
			else
				played += 1
				local score
				if halfmove_limit > 0 and pos.halfmove >= halfmove_limit then
					score = 0
				else
					score = -search(depth - 1, -beta, -alpha, ply + 1)
				end
				unmake(pos, m, castling, ep, halfmove)
				if stopped then
					return 0
				end
				if score > best then
					best = score
					if score > alpha then
						alpha = score
						if alpha >= beta then
							if m.captured == EMPTY and m.promo == 0 then
								killers[ply] = m
								local key = m.from * 128 + m.to
								history[key] = (history[key] or 0) + depth * depth
							end
							break
						end
					end
				end
			end
		end
		if played == 0 then
			-- mate (the sooner the worse) or stalemate
			return if checked then -(MATE - ply) else 0
		end
		return best
	end

	-- The legal moves of the root in a random order, so that equal moves are not always tried in the same order
	-- and two games do not repeat each other.
	local us = pos.side
	local root: { Move } = chess.legal_moves(pos)
	for i = #root, 2, -1 do
		local j = math.random(i)
		root[i], root[j] = root[j], root[i]
	end
	if #root == 0 then
		return { move = nil, score = if chess.in_check(pos) then -MATE else 0, depth = 0, nodes = 0 }
	end
	-- captures first for the first look
	local first_scores: { number } = {}
	for i = 1, #root do
		first_scores[i] = capture_order(root[i])
	end
	for i = 2, #root do
		local m = root[i]
		local score = first_scores[i]
		local slot = i
		while slot > 1 and first_scores[slot - 1] < score do
			root[slot] = root[slot - 1]
			first_scores[slot] = first_scores[slot - 1]
			slot -= 1
		end
		root[slot] = m
		first_scores[slot] = score
	end

	-- Whether a root move leads to a position the game has seen before; worked out once.
	local repeats: { boolean } = {}
	for i = 1, #root do
		local m = root[i]
		local castling, ep, halfmove = make(pos, m)
		repeats[i] = next(seen) ~= nil and seen[chess.position_key(pos, chess.legal_moves(pos))] == true
		unmake(pos, m, castling, ep, halfmove)
	end

	local best_move = root[1]
	local best_score = 0
	local reached = 0
	for depth = 1, MAX_DEPTH do
		local alpha = -MATE
		local leader = 0
		local scores: { number } = {}
		for i = 1, #root do
			-- The first look, one move ahead, is always finished, whatever the effort.
			if depth > 1 and nodes >= effort then
				stopped = true
				break
			end
			local m = root[i]
			local castling, ep, halfmove = make(pos, m)
			local score
			if repeats[i] or (halfmove_limit > 0 and pos.halfmove >= halfmove_limit) then
				score = 0
				-- a mate still counts
				if #chess.legal_moves(pos) == 0 and chess.in_check(pos) then
					score = MATE - 1
				end
			else
				limit = if depth == 1 then math.huge else effort
				score = -search(depth - 1, -MATE, -alpha, 1)
			end
			unmake(pos, m, castling, ep, halfmove)
			if stopped then
				break
			end
			scores[i] = score
			if leader == 0 or score > alpha then
				alpha = score
				leader = i
			end
		end
		-- A deeper look that was cut short still counts when it got past the move that led before: that move was
		-- tried first, so whatever leads now was compared with it.
		if leader > 0 then
			best_move = root[leader]
			best_score = alpha
			reached = depth
		end
		if stopped or #root == 1 or best_score >= MATE - MAX_DEPTH * 2 or best_score <= -(MATE - MAX_DEPTH * 2) then
			break
		end
		-- The next look starts with the best move of this one; the others keep their order.
		if leader > 1 then
			local m = root[leader]
			local flag = repeats[leader]
			table.remove(root, leader)
			table.remove(repeats, leader)
			table.insert(root, 1, m)
			table.insert(repeats, 1, flag)
		end
	end
	if us ~= pos.side then
		error("the search did not put the position back")
	end
	return { move = best_move, score = best_score, depth = reached, nodes = nodes }
end

return ai
