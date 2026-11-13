--!strict
-- Standard chess as a Board Sandbox mod. The rules are in chess.luau; this file connects them to the table:
-- it keeps the view objects in step with the position, turns clicks into moves and implements the commands.
--
-- Coordinates: x is the file (1 = a) and y the row (1 = rank 8), so white starts at the near side of the camera.
--
-- Commands (seat 1 is white, seat 2 is black):
--   { action = "click", x, y }                    the board click of the client: select a piece, then a target
--   { action = "move", from = "e2", to = "e4" }   optional promotion = "q" | "r" | "b" | "n"
--   { action = "move", uci = "e7e8q" }            or san = "Nf3"
--   { action = "resign" }                         either seat, at any time
--   { action = "draw" }                           offer a draw; the other seat repeats it to accept
-- Options: fen (start position, "" = standard), promotion (the piece a click promotes to), repetition (the number
-- of occurrences that draws, 0 = never) and halfmove_limit (plies without capture or pawn move that draw, 100 =
-- the fifty-move rule, 0 = never).
--
-- A click that only selects or deselects a piece is an accepted command, because the selection is drawn on the
-- table (a tile under the piece, dots and tiles on its targets). It therefore takes a step in the undo history, and
-- a snapshot carries it along. Queries: state, fen, board, moves, pgn, legal_moves and perft (args { depth, fen }).
--
-- The look of the pieces and of the marks is in pieces.luau; the camera looks straight down (see config.json).

local chess = require("./chess")
local pieces = require("./pieces")

type Move = chess.Move
type Position = chess.Position

local WHITE = chess.WHITE
local BLACK = chess.BLACK
local EMPTY = chess.EMPTY
local COLOR = chess.COLOR
local TYPE = chess.TYPE
local SQUARES = chess.SQUARES

local SIDE_NAMES: { string } = { "White", "Black" }

-- The colours and strength of the tiles and dots on the board.
local LAST_COLOR = "#e3d24f"
local CHECK_COLOR = "#e5392c"
local SELECT_COLOR = "#f2c23a"
local TARGET_COLOR = "#e5503c"
local HINT_COLOR = "#3fbf6e"
-- how high a knight rises while it jumps, in squares
local KNIGHT_HOP = 0.5

local PROMOTION_WORDS: { [string]: number } = {
	q = chess.QUEEN,
	queen = chess.QUEEN,
	r = chess.ROOK,
	rook = chess.ROOK,
	b = chess.BISHOP,
	bishop = chess.BISHOP,
	n = chess.KNIGHT,
	knight = chess.KNIGHT,
}

local rules = {}

local pos: Position = chess.start_position()
-- always the legal moves of pos
local legal: { Move } = {}
-- the ids of the view objects of the piece on every square (a piece is several of them), empty for none; indexed like
-- the board
local NONE: { number } = table.freeze({} :: { number })
local groups: { { number } } = table.create(120, NONE)
-- the ids of the tiles and dots that mark squares: the last move, a king in check, the selected piece and its targets
local marks: { number } = {}
local selected = 0
-- the keys of the positions since the last capture or pawn move, the current one last
local rep: { string } = {}
-- the moves played so far in SAN
local history: { string } = {}
local start_fen = chess.START_FEN
local last_from = 0
local last_to = 0
local last_seat = 0
local last_san = ""
local last_uci = ""
local finished = false
local result = "*"
local draw_offer = 0
local promotion = chess.QUEEN
local repetition_limit = 3
local halfmove_limit = 100

local function clamp_integer(value: any, fallback: number, low: number, high: number): number
	if type(value) ~= "number" or value ~= value then
		return fallback
	end
	return math.clamp(math.floor(value), low, high)
end

local function is_integer(value: any): boolean
	return type(value) == "number" and value == math.floor(value)
end

local function parse_promotion(value: any): number?
	if type(value) ~= "string" then
		return nil
	end
	return PROMOTION_WORDS[string.lower(value)]
end

-- A short piece of the player's own text for a message; long or odd input is left out.
local function quoted(text: string): string
	if #text <= 16 then
		return "'" .. text .. "'"
	end
	return "the given text"
end

local function spawn_piece(sq: number, piece: number): { number }
	local x, y = chess.xy(sq)
	return pieces.spawn(TYPE[piece], COLOR[piece], x, y)
end

-- The seconds a piece takes to glide from one square to another: longer for longer ways.
local function glide_seconds(fx: number, fy: number, tx: number, ty: number): number
	local dx = tx - fx
	local dy = ty - fy
	return math.min(0.5, 0.16 + 0.05 * math.sqrt(dx * dx + dy * dy))
end

-- Draws everything that marks squares, from the state alone: the squares of the last move, a king in check, the
-- selected piece with a dot on every empty square it can go to and a tile on every piece it can take. The old marks
-- go first, so the table never shows a stale one.
local function draw_marks()
	if bse.is_headless() then
		return
	end
	for i = 1, #marks do
		bse.view.remove(marks[i])
	end
	table.clear(marks)

	if last_from ~= 0 then
		local fx, fy = chess.xy(last_from)
		local tx, ty = chess.xy(last_to)
		marks[#marks + 1] = pieces.tile(fx, fy, LAST_COLOR, 0.4, 0)
		marks[#marks + 1] = pieces.tile(tx, ty, LAST_COLOR, 0.4, 0)
	end
	if chess.in_check(pos) then
		local kx, ky = chess.xy(pos.kings[pos.side])
		marks[#marks + 1] = pieces.tile(kx, ky, CHECK_COLOR, 0.6, 1)
	end
	if selected == 0 then
		return
	end
	local sx, sy = chess.xy(selected)
	marks[#marks + 1] = pieces.tile(sx, sy, SELECT_COLOR, 0.6, 2)
	local seen: { [number]: boolean } = {}
	for i = 1, #legal do
		local m = legal[i]
		if m.from == selected and not seen[m.to] then
			seen[m.to] = true
			local tx, ty = chess.xy(m.to)
			if pos.board[m.to] ~= EMPTY then
				marks[#marks + 1] = pieces.tile(tx, ty, TARGET_COLOR, 0.6, 2)
			else
				marks[#marks + 1] = pieces.dot(tx, ty, HINT_COLOR, 0.85, 0.3)
				if m.kind == chess.EN_PASSANT then
					-- the pawn that is taken does not stand on the square the pawn goes to
					local taken = if pos.side == WHITE then m.to + 10 else m.to - 10
					local ex, ey = chess.xy(taken)
					marks[#marks + 1] = pieces.tile(ex, ey, TARGET_COLOR, 0.6, 2)
				end
			end
		end
	end
end

local function select_square(sq: number)
	selected = sq
	draw_marks()
end

local function emit_last_move()
	if last_from ~= 0 then
		local fx, fy = chess.xy(last_from)
		local tx, ty = chess.xy(last_to)
		bse.emit({ t = "last_move", x = tx, y = ty, from_x = fx, from_y = fy, seat = last_seat, san = last_san, uci = last_uci })
	end
end

-- What a view needs to know besides the pieces; a restore starts from an empty table, so it is sent again.
local function announce()
	bse.emit({ t = "board", columns = 8, rows = 8 })
	emit_last_move()
end

local function has_moves(sq: number): boolean
	for i = 1, #legal do
		if legal[i].from == sq then
			return true
		end
	end
	return false
end

-- The legal move from one square to another; for a promotion the one that makes the given piece type.
local function find_move(from: number, to: number, promo: number): Move?
	for i = 1, #legal do
		local m = legal[i]
		if m.from == from and m.to == to and (m.promo == 0 or m.promo == promo) then
			return m
		end
	end
	return nil
end

local function normalize_san(text: string): string
	local cleaned = string.gsub(text, "[%+#!%?=%s]", "")
	cleaned = string.gsub(cleaned, "0", "O")
	return cleaned
end

local function find_san(text: string): Move?
	local wanted = normalize_san(text)
	for i = 1, #legal do
		if normalize_san(chess.san_base(legal[i], legal)) == wanted then
			return legal[i]
		end
	end
	return nil
end

local function side_name(seat: number): string
	return SIDE_NAMES[seat] or ("Seat " .. tostring(seat))
end

local function finish(winner: number, reason: string, cells: { { number } }?)
	finished = true
	draw_offer = 0
	result = if winner == WHITE then "1-0" elseif winner == BLACK then "0-1" else "1/2-1/2"
	bse.over({ winner = if winner > 0 then winner else nil, reason = reason, cells = cells })
end

-- Shows a move on the table and keeps the groups of the board in step with it: the piece glides to its square (a knight
-- hops), the rook of a castling move glides along with the king, a piece that is taken goes when the other arrives
-- and a pawn that promotes turns into its new piece on arrival.
local function show_move(m: Move, seat: number)
	local from = m.from
	local to = m.to
	local fx, fy = chess.xy(from)
	local tx, ty = chess.xy(to)
	local seconds = glide_seconds(fx, fy, tx, ty)
	local mover = TYPE[m.piece]
	local mover_group = groups[from]
	groups[from] = NONE

	if m.kind == chess.CASTLE_KING or m.kind == chess.CASTLE_QUEEN then
		local rook_from = if m.kind == chess.CASTLE_KING then to + 1 else to - 2
		local rook_to = if m.kind == chess.CASTLE_KING then to - 1 else to + 1
		local rfx, rfy = chess.xy(rook_from)
		local rtx, rty = chess.xy(rook_to)
		pieces.move(groups[rook_from], pieces.ROOK, seat, rfx, rfy, rtx, rty, seconds, 0)
		groups[rook_to] = groups[rook_from]
		groups[rook_from] = NONE
	end

	if mover == pieces.KNIGHT then
		pieces.move(mover_group, mover, seat, fx, fy, tx, ty, seconds + 0.14, KNIGHT_HOP)
	else
		pieces.move(mover_group, mover, seat, fx, fy, tx, ty, seconds, 0)
	end
	if m.promo ~= 0 then
		if not bse.is_headless() then
			bse.emit({ t = "fx", kind = "wait", seconds = seconds })
		end
		pieces.remove(mover_group)
	end

	if m.kind == chess.EN_PASSANT then
		local taken = if seat == WHITE then to + 10 else to - 10
		pieces.remove(groups[taken])
		groups[taken] = NONE
	elseif m.captured ~= EMPTY then
		pieces.remove(groups[to])
		groups[to] = NONE
	end

	if m.promo ~= 0 then
		groups[to] = spawn_piece(to, m.promo + (if seat == BLACK then 8 else 0))
	else
		groups[to] = mover_group
	end
end

-- Plays a legal move of the seat on turn: updates the view, the position and the game state, and decides whether
-- the game goes on.
local function commit(m: Move, seat: number)
	local headless = bse.is_headless()
	selected = 0
	draw_offer = 0

	local from = m.from
	local to = m.to
	show_move(m, seat)

	local notation = if headless then "" else chess.san_base(m, legal)
	chess.make(pos, m)
	legal = chess.legal_moves(pos)
	local check = chess.in_check(pos)
	if check and not headless then
		notation ..= if #legal == 0 then "#" else "+"
	end
	if not headless then
		history[#history + 1] = notation
	end
	last_from = from
	last_to = to
	last_seat = seat
	last_san = notation
	if not headless then
		last_uci = chess.uci(m)
		emit_last_move()
	end

	local reason: string? = nil
	local winner = 0
	if #legal == 0 then
		if check then
			reason = "checkmate"
			winner = seat
		else
			reason = "stalemate"
		end
	elseif chess.insufficient_material(pos) then
		reason = "insufficient_material"
	else
		local key = chess.position_key(pos, legal)
		if pos.halfmove == 0 then
			table.clear(rep)
		end
		rep[#rep + 1] = key
		local seen = 0
		for i = 1, #rep do
			if rep[i] == key then
				seen += 1
			end
		end
		if repetition_limit > 0 and seen >= repetition_limit then
			reason = "repetition"
		elseif halfmove_limit > 0 and pos.halfmove >= halfmove_limit then
			reason = "fifty_move_rule"
		end
	end

	if reason ~= nil then
		local cells: { { number } }? = nil
		if reason == "checkmate" then
			local king_square = pos.kings[pos.side]
			local kx, ky = chess.xy(king_square)
			cells = { { kx, ky } }
			for _, attacker in chess.attackers(pos.board, king_square, seat) do
				local ax, ay = chess.xy(attacker)
				table.insert(cells, { ax, ay })
			end
		end
		finish(winner, reason, cells)
	else
		bse.turn(pos.side)
		if check and not headless then
			bse.status(side_name(pos.side) .. " is in check", "warn")
		end
	end
	draw_marks()
end

-- The move a command asks for, or nil and the rejection to return.
local function resolve_move(cmd: { [string]: any }): (Move?, any)
	local promo = promotion
	if cmd.promotion ~= nil then
		local chosen = parse_promotion(cmd.promotion)
		if chosen == nil then
			return nil, bse.reject("INVALID_ARGUMENT", "promotion must be q, r, b or n")
		end
		promo = chosen
	end

	local san_text: any = cmd.san
	if type(san_text) == "string" then
		local found = find_san(san_text)
		if found == nil then
			return nil, bse.reject("ILLEGAL_MOVE", "no legal move is written " .. quoted(san_text), { san = if #san_text <= 16 then san_text else nil })
		end
		return found, nil
	end

	local from_text: any = cmd.from
	local to_text: any = cmd.to
	local uci_text: any = cmd.uci
	if type(uci_text) == "string" then
		if #uci_text ~= 4 and #uci_text ~= 5 then
			return nil, bse.reject("INVALID_ARGUMENT", "uci must look like e2e4 or e7e8q")
		end
		from_text = string.sub(uci_text, 1, 2)
		to_text = string.sub(uci_text, 3, 4)
		if #uci_text == 5 then
			local chosen = parse_promotion(string.sub(uci_text, 5, 5))
			if chosen == nil then
				return nil, bse.reject("INVALID_ARGUMENT", "promotion must be q, r, b or n")
			end
			promo = chosen
		end
	end
	if type(from_text) ~= "string" or type(to_text) ~= "string" then
		return nil, bse.reject("INVALID_ARGUMENT", "a move needs from and to (like e2 and e4), uci or san")
	end
	local from = chess.parse_square(from_text)
	local to = chess.parse_square(to_text)
	if from == nil or to == nil then
		return nil, bse.reject("INVALID_ARGUMENT", "from and to must be squares from a1 to h8")
	end

	local found = find_move(from, to, promo)
	if found ~= nil then
		return found, nil
	end
	local piece = pos.board[from]
	local message = "that move is not legal"
	if COLOR[piece] ~= pos.side then
		message = "there is no piece of yours on " .. chess.square_name(from)
	elseif chess.in_check(pos) then
		message = "that move does not get your king out of check"
	end
	return nil, bse.reject("ILLEGAL_MOVE", message, { from = chess.square_name(from), to = chess.square_name(to) })
end

local function do_move(seat: number, cmd: { [string]: any }): any
	local m, problem = resolve_move(cmd)
	if m == nil then
		return problem
	end
	commit(m, seat)
	return nil
end

local function do_click(seat: number, cmd: { [string]: any }): any
	local x: any = cmd.x
	local y: any = cmd.y
	if not is_integer(x) or not is_integer(y) then
		return bse.reject("INVALID_ARGUMENT", "x and y must be integers")
	end
	if x < 1 or x > 8 or y < 1 or y > 8 then
		return bse.reject("OUT_OF_BOUNDS", "the point is outside the board", { x = x, y = y })
	end

	local sq = chess.from_xy(x, y)
	local piece = pos.board[sq]
	local own = COLOR[piece] == seat

	if selected ~= 0 then
		local m = find_move(selected, sq, promotion)
		if m ~= nil then
			commit(m, seat)
			return nil
		end
		if sq == selected then
			selected = 0
			draw_marks()
			return nil
		end
		if not own then
			return bse.reject("ILLEGAL_MOVE", "the selected piece cannot go there", { x = x, y = y })
		end
	end

	if own then
		if not has_moves(sq) then
			return bse.reject("NO_MOVES", "that piece has no legal move", { x = x, y = y })
		end
		select_square(sq)
		return nil
	end
	if piece == EMPTY then
		return bse.reject("EMPTY_SQUARE", "select one of your pieces first", { x = x, y = y })
	end
	return bse.reject("NOT_YOUR_PIECE", "that is not your piece", { x = x, y = y })
end

local function do_resign(seat: number): any
	selected = 0
	if not bse.is_headless() then
		bse.status(side_name(seat) .. " resigns")
	end
	finish(3 - seat, "resignation", nil)
	draw_marks()
	return nil
end

local function do_draw(seat: number): any
	if draw_offer == seat then
		return bse.reject("DRAW_ALREADY_OFFERED", "you have already offered a draw")
	end
	if draw_offer ~= 0 then
		selected = 0
		finish(0, "agreement", nil)
		draw_marks()
		return nil
	end
	draw_offer = seat
	bse.status(side_name(seat) .. " offers a draw. " .. side_name(3 - seat) .. " accepts by sending the draw command.")
	return nil
end

-- Puts the starting position on the table.
local function begin_game(position: Position)
	pos = position
	start_fen = chess.to_fen(pos)
	groups = table.create(120, NONE)
	table.clear(marks)
	table.clear(history)
	selected = 0
	last_from = 0
	last_to = 0
	last_seat = 0
	last_san = ""
	last_uci = ""
	finished = false
	result = "*"
	draw_offer = 0

	announce()
	for i = 1, 64 do
		local sq = SQUARES[i]
		local piece = pos.board[sq]
		if piece ~= EMPTY then
			groups[sq] = spawn_piece(sq, piece)
		end
	end

	legal = chess.legal_moves(pos)
	rep = { chess.position_key(pos, legal) }
	-- a simulation needs the turn even when the position is over at once
	bse.turn(pos.side)
	if #legal == 0 then
		if chess.in_check(pos) then
			finish(3 - pos.side, "checkmate", nil)
		else
			finish(0, "stalemate", nil)
		end
	elseif chess.insufficient_material(pos) then
		finish(0, "insufficient_material", nil)
	end
	draw_marks()
end

function rules.new_game(options: { [string]: any })
	promotion = parse_promotion(options.promotion) or chess.QUEEN
	repetition_limit = clamp_integer(options.repetition, 3, 0, 100)
	halfmove_limit = clamp_integer(options.halfmove_limit, 100, 0, 100000)

	local fen = chess.START_FEN
	local text: any = options.fen
	if type(text) == "string" and string.match(text, "%S") ~= nil then
		fen = text
	end
	local position, problem = chess.from_fen(fen)
	if position == nil then
		error(bse.reject("BAD_OPTION", "invalid fen: " .. tostring(problem)), 0)
	end
	begin_game(position)
end

function rules.command(seat: number, cmd: { [string]: any }): any
	local action = cmd.action
	if action ~= "click" and action ~= "move" and action ~= "resign" and action ~= "draw" then
		return bse.reject("UNKNOWN_ACTION", "unknown action", { action = action })
	end
	if finished then
		return bse.reject("GAME_OVER", "the game is over")
	end
	if action == "resign" then
		return do_resign(seat)
	elseif action == "draw" then
		return do_draw(seat)
	end
	if seat ~= pos.side then
		return bse.reject("NOT_YOUR_TURN", "it is not your turn")
	end
	if action == "click" then
		return do_click(seat, cmd)
	end
	return do_move(seat, cmd)
end

local function command_for(m: Move): { [string]: any }
	local command: { [string]: any } = { action = "move", from = chess.square_name(m.from), to = chess.square_name(m.to) }
	if m.promo ~= 0 then
		command.promotion = string.lower(chess.CHARS[m.promo])
	end
	return command
end

function rules.random_command(seat: number): any
	if finished or seat ~= pos.side or #legal == 0 then
		return nil
	end
	return command_for(legal[math.random(#legal)])
end

function rules.legal_commands(seat: number): { any }
	local list = {}
	if finished or seat ~= pos.side then
		return list
	end
	for i = 1, #legal do
		table.insert(list, command_for(legal[i]))
	end
	return list
end

local function board_lines(): { string }
	local lines: { string } = {}
	for row = 0, 7 do
		local line = tostring(8 - row) .. " "
		for file = 0, 7 do
			line ..= " " .. chess.CHARS[pos.board[22 + 10 * row + file]]
		end
		lines[#lines + 1] = line
	end
	lines[#lines + 1] = "   a b c d e f g h"
	return lines
end

local function pgn_text(): string
	local first = chess.from_fen(start_fen)
	local side = if first ~= nil then first.side else WHITE
	local number = if first ~= nil then first.fullmove else 1
	local parts: { string } = {}
	for i = 1, #history do
		if side == WHITE then
			parts[#parts + 1] = tostring(number) .. ". " .. history[i]
		else
			parts[#parts + 1] = if i == 1 then tostring(number) .. "... " .. history[i] else history[i]
			number += 1
		end
		side = 3 - side
	end
	parts[#parts + 1] = result
	return table.concat(parts, " ")
end

function rules.query(name: string, args: any): any
	if name == "fen" then
		return chess.to_fen(pos)
	elseif name == "board" then
		return board_lines()
	elseif name == "moves" then
		return table.clone(history)
	elseif name == "pgn" then
		return pgn_text()
	elseif name == "legal_moves" then
		local list: { string } = {}
		for i = 1, #legal do
			list[i] = chess.san(pos, legal[i], legal)
		end
		return list
	elseif name == "state" then
		return {
			fen = chess.to_fen(pos),
			turn = pos.side,
			in_check = chess.in_check(pos),
			legal_moves = #legal,
			halfmove = pos.halfmove,
			fullmove = pos.fullmove,
			plies = #history,
			finished = finished,
			result = result,
			selected = if selected ~= 0 then chess.square_name(selected) else nil,
			draw_offer = if draw_offer ~= 0 then draw_offer else nil,
		}
	elseif name == "perft" then
		local depth = 1
		local target = pos
		if type(args) == "table" then
			depth = clamp_integer(args.depth, 1, 0, 6)
			if type(args.fen) == "string" then
				local parsed, problem = chess.from_fen(args.fen)
				if parsed == nil then
					error(bse.reject("INVALID_ARGUMENT", "invalid fen: " .. tostring(problem)), 0)
				end
				target = parsed
			end
		end
		return { depth = depth, nodes = chess.perft(chess.copy(target), depth) }
	end
	return nil
end

function rules.snapshot(): any
	local packed: { { number } } = table.create(64, NONE)
	for i = 1, 64 do
		packed[i] = groups[SQUARES[i]]
	end
	return {
		fen = chess.to_fen(pos),
		start_fen = start_fen,
		groups = packed,
		marks = table.clone(marks),
		selected = selected,
		rep = table.clone(rep),
		moves = table.clone(history),
		last_from = last_from,
		last_to = last_to,
		last_seat = last_seat,
		last_san = last_san,
		last_uci = last_uci,
		finished = finished,
		result = result,
		draw_offer = draw_offer,
		promotion = promotion,
		repetition = repetition_limit,
		halfmove_limit = halfmove_limit,
	}
end

local function bad_snapshot(message: string): never
	error(bse.reject("BAD_SNAPSHOT", message), 0)
end

local function number_list(value: any, name: string, count: number?): { number }
	if type(value) ~= "table" then
		bad_snapshot(name .. " must be a list")
	end
	local list: { number } = {}
	for key, item in value do
		if type(key) ~= "number" or type(item) ~= "number" then
			bad_snapshot(name .. " must be a list of numbers")
		end
		list[key] = item
	end
	if count ~= nil and #list ~= count then
		bad_snapshot(name .. " has the wrong length")
	end
	return list
end

-- One list of ids for each of the 64 squares.
local function id_groups(value: any, name: string): { { number } }
	if type(value) ~= "table" then
		bad_snapshot(name .. " must be a list")
	end
	local list: { { number } } = {}
	for key, item in value do
		if type(key) ~= "number" then
			bad_snapshot(name .. " must be a list of lists")
		end
		list[key] = number_list(item, name, nil)
	end
	if #list ~= 64 then
		bad_snapshot(name .. " has the wrong length")
	end
	return list
end

local function text_list(value: any, name: string): { string }
	if type(value) ~= "table" then
		bad_snapshot(name .. " must be a list")
	end
	local list: { string } = {}
	for key, item in value do
		if type(key) ~= "number" or type(item) ~= "string" then
			bad_snapshot(name .. " must be a list of strings")
		end
		list[key] = item
	end
	return list
end

function rules.restore(data: any)
	if type(data) ~= "table" or type(data.fen) ~= "string" or type(data.start_fen) ~= "string" then
		bad_snapshot("the snapshot has no position")
	end
	local position, problem = chess.from_fen(data.fen)
	if position == nil then
		error(bse.reject("BAD_SNAPSHOT", "the position is invalid: " .. tostring(problem)), 0)
	end
	local packed = id_groups(data.groups, "groups")
	local restored_marks = number_list(data.marks, "marks", nil)
	local restored_rep = text_list(data.rep, "rep")
	local restored_history = text_list(data.moves, "moves")

	pos = position
	start_fen = data.start_fen
	groups = table.create(120, NONE)
	for i = 1, 64 do
		groups[SQUARES[i]] = packed[i]
	end
	marks = restored_marks
	selected = clamp_integer(data.selected, 0, 0, 120)
	rep = restored_rep
	history = restored_history
	last_from = clamp_integer(data.last_from, 0, 0, 120)
	last_to = clamp_integer(data.last_to, 0, 0, 120)
	last_seat = clamp_integer(data.last_seat, 0, 0, 2)
	last_san = if type(data.last_san) == "string" then data.last_san else ""
	last_uci = if type(data.last_uci) == "string" then data.last_uci else ""
	finished = data.finished == true
	result = if type(data.result) == "string" then data.result else "*"
	draw_offer = clamp_integer(data.draw_offer, 0, 0, 2)
	promotion = parse_promotion(data.promotion) or chess.QUEEN
	repetition_limit = clamp_integer(data.repetition, 3, 0, 100)
	halfmove_limit = clamp_integer(data.halfmove_limit, 100, 0, 100000)
	legal = chess.legal_moves(pos)
	announce()
end

return rules
