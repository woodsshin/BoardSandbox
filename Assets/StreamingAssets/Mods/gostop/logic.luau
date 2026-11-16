--!strict
-- Go-Stop on the table: this file shows the game of gostop.luau and turns clicks into its commands. The rules are in
-- gostop.luau and the computer player in ai.luau.
--
-- A game starts by choosing two players (Matgo, go or stop from 7 points) or three (from 3 points), unless the
-- "players" option already says so.
--
-- Commands: players { count }, play { card, shake?, bomb? }, flip, pick { card }, go, stop, and click { id }, which is
-- what the screen sends and becomes one of the others.

local G = require("./gostop")
local ai = require("./ai")
local selftest = require("./selftest")

local rules = {}

type State = G.State
type Command = G.Command

local ATLAS_ACROSS = 8
local ATLAS_W, ATLAS_H = 1680, 2380
local CELL_W, CELL_H, CELL_PAD = 210, 340, 2
local BACK = 48

local STEP_SECONDS = 0.42

local STOCK_X, STOCK_Z = -7.6, 0
local FLOOR_X = { -0.95, 0.95, -2.85, 2.85, -4.75, 4.75 }
local FLOOR_Z = { -0.95, 0.95 }

local SEAT_NAMES = { "Player 1", "Player 2", "Player 3" }

local EVENT_TEXT: { [string]: string } = {
	kiss = "쪽!",
	ppeok = "뻑!",
	ttadak = "따닥!",
	sweep = "싹쓸이!",
	bomb = "폭탄!",
	shake = "흔들기!",
	go = "고!",
	ppeok_taken = "뻑 먹기!",
	jappeok = "자뻑!",
}

type Place = { x: number, z: number, hand_scale: number, hand_step: number, cap_z: number, cap_scale: number, cap_step: number,
	groups: { number }, wrap: number, row: number, panel_x: number, panel_z: number, towards: number }

-- options
local option_players = 0
local option_bonus = 2
local view_seat = 1
local open_hands = false

local S: State? = nil
local headless = false

-- screen
local card_obj: { number } = {}
local shown: { string } = {}
local slot_of: { number } = {}
local buttons: { number } = {}
local button_cmd: { any } = {}
local panels: { number } = {}
local banner = 0
local prompt: { [string]: any }? = nil
local events: { string } = {}
local announced = false

local function rect(index: number): { number }
	local column, row = index % ATLAS_ACROSS, index // ATLAS_ACROSS
	return {
		(column * CELL_W + CELL_PAD) / ATLAS_W,
		(row * CELL_H + CELL_PAD) / ATLAS_H,
		(CELL_W - 2 * CELL_PAD) / ATLAS_W,
		(CELL_H - 2 * CELL_PAD) / ATLAS_H,
	}
end

local function face_index(id: number): number
	return if id <= 48 then id - 1 else id
end

local function place_of(state: State, seat: number): Place
	local n: number = state.n
	local viewer = if view_seat >= 1 and view_seat <= n then view_seat else 1
	local offset = (seat - viewer) % n
	if offset == 0 then
		return { x = 0, z = 6.9, hand_scale = 1, hand_step = 1.08, cap_z = 4.3, cap_scale = 0.72, cap_step = 0.4,
			groups = { -9.2, -6.9, -2.9, 1.2 }, wrap = 16, row = 0.55, panel_x = -6.4, panel_z = 5.65, towards = 1 }
	elseif n == 2 then
		return { x = 0, z = -6.9, hand_scale = 0.7, hand_step = 0.5, cap_z = -4.3, cap_scale = 0.72, cap_step = 0.4,
			groups = { -9.2, -6.9, -2.9, 1.2 }, wrap = 16, row = -0.55, panel_x = -6.4, panel_z = -5.65, towards = -1 }
	end
	-- Three players: the next player sits to the right, the one after to the left.
	local cx = if offset == 1 then 4.9 else -4.9
	return { x = cx, z = -7.0, hand_scale = 0.55, hand_step = 0.36, cap_z = -4.75, cap_scale = 0.42, cap_step = 0.2,
		groups = { cx - 4.0, cx - 2.75, cx - 0.9, cx + 0.95 }, wrap = 12, row = -0.38, panel_x = cx, panel_z = -5.95, towards = -1 }
end

local function set_card(id: number, x: number, y: number, z: number, scale: number, up: boolean, pick: boolean)
	local key = string.format("%.2f %.3f %.2f %.2f %s %s", x, y, z, scale, tostring(up), tostring(pick))
	if shown[id] == key then
		return
	end
	shown[id] = key
	bse.view.set(card_obj[id], {
		pos = { x, y, z },
		scale = scale,
		image_rect = rect(if up then face_index(id) else BACK),
		pick = pick,
		visible = true,
	})
end

local function floor_slot(month: number): number
	local slot = slot_of[month]
	if slot ~= 0 then
		return slot
	end
	local used: { [number]: boolean } = {}
	for m = 1, 12 do
		if slot_of[m] ~= 0 then
			used[slot_of[m]] = true
		end
	end
	for candidate = 1, 12 do
		if not used[candidate] then
			slot_of[month] = candidate
			return candidate
		end
	end
	return 1
end

local function seat_text(state: State, seat: number): string
	local parts: { string } = {}
	local name = SEAT_NAMES[seat]
	if state.phase ~= "over" and state.turn == seat then
		name = "▶ " .. name
	end
	table.insert(parts, name)
	table.insert(parts, `{G.score_of(state, seat)}점`)
	if state.go[seat] > 0 then
		table.insert(parts, `{state.go[seat]}고`)
	end
	if state.shake[seat] > 0 then
		table.insert(parts, `흔들기 {state.shake[seat]}`)
	end
	if state.ppeok[seat] > 0 then
		table.insert(parts, `뻑 {state.ppeok[seat]}`)
	end
	if state.pass[seat] > 0 then
		table.insert(parts, `폭탄 {state.pass[seat]}`)
	end
	return table.concat(parts, "  ")
end

local function result_lines(state: State): (string, string, string)
	local result = state.result
	if result.winner == 0 then
		return "나가리 (무승부)", "아무도 스톱하지 못했습니다", ""
	end
	local name = SEAT_NAMES[result.winner]
	local how = ""
	if result.how == "chongtong" then
		how = "총통"
	elseif result.how == "ppeok3" then
		how = "삼뻑"
	else
		how = `{result.score}점`
		if state.go[result.winner] > 0 then
			how ..= ` {state.go[result.winner]}고`
		end
	end
	if state.shake[result.winner] > 0 then
		how ..= ` 흔들기×{2 ^ state.shake[result.winner]}`
	end
	if result.meongtta then
		how ..= " 멍따×2"
	end
	local pays: { string } = {}
	for seat = 1, state.n do
		if seat ~= result.winner then
			local text = `{SEAT_NAMES[seat]} -{result.pays[seat]}`
			local f = result.flags[seat]
			if f.pibak then
				text ..= " 피박"
			end
			if f.gwangbak then
				text ..= " 광박"
			end
			if f.gobak then
				text ..= " 고박"
			end
			table.insert(pays, text)
		end
	end
	return `{name} 승리! +{result.total}점`, how, table.concat(pays, "   ")
end

local function show_button(index: number, text: string?, cmd: any, x: number)
	if text == nil then
		button_cmd[index] = false
		bse.view.set(buttons[index], { visible = false, pick = false })
	else
		button_cmd[index] = cmd
		bse.view.set(buttons[index], { visible = true, pick = true, label = text, pos = { x, 0.02, 2.85 } })
	end
end

local function sync_controls()
	local state = S
	if state == nil then
		show_button(1, "2인 맞고 (7점)", { action = "players", count = 2 }, -2.6)
		show_button(2, "3인 고스톱 (3점)", { action = "players", count = 3 }, 2.6)
		show_button(3, nil, nil, 0)
		bse.view.set(banner, { label = "몇 명이 칠까요?", labels = {} })
		return
	end

	local head, line1, line2 = "", "", ""
	if state.phase == "over" then
		head, line1, line2 = result_lines(state)
		show_button(1, nil, nil, 0)
		show_button(2, nil, nil, 0)
		show_button(3, nil, nil, 0)
	else
		head = table.concat(events, " ")
		local seat: number = state.turn
		if prompt ~= nil then
			if prompt.kind == "bomb" then
				line1 = "폭탄을 터뜨릴까요?"
				show_button(1, "폭탄", { action = "play", card = prompt.card, bomb = true }, -2)
				show_button(2, "한 장만", { action = "play", card = prompt.card }, 2)
			else
				line1 = "흔들까요?"
				show_button(1, "흔들기", { action = "play", card = prompt.card, shake = true }, -2)
				show_button(2, "그냥 내기", { action = "play", card = prompt.card }, 2)
			end
			show_button(3, nil, nil, 0)
		elseif state.phase == "gostop" then
			line1 = `{SEAT_NAMES[seat]} {G.score_of(state, seat)}점 - 고 또는 스톱`
			show_button(1, "고", { action = "go" }, -2)
			show_button(2, "스톱", { action = "stop" }, 2)
			show_button(3, nil, nil, 0)
		elseif state.phase == "pick" then
			line1 = "가져갈 패를 고르세요"
			show_button(1, nil, nil, 0)
			show_button(2, nil, nil, 0)
			show_button(3, nil, nil, 0)
		else
			show_button(1, nil, nil, 0)
			show_button(2, nil, nil, 0)
			if G.can_flip(state, seat) then
				show_button(3, "뒤집기만", { action = "flip" }, 0)
			else
				show_button(3, nil, nil, 0)
			end
		end
	end
	local small: { any } = {}
	if line1 ~= "" then
		table.insert(small, { text = line1, dx = 0, dy = 0.11, size = 0.07, color = "#f2efe2" })
	end
	if line2 ~= "" then
		table.insert(small, { text = line2, dx = 0, dy = 0.2, size = 0.06, color = "#f2efe2" })
	end
	bse.view.set(banner, { label = head, labels = small })
	for seat = 1, state.n do
		bse.view.set(panels[seat], { label = seat_text(state, seat) })
	end
end

-- Puts every card where the state says it is. Only what changed is sent.
local function sync()
	if headless then
		return
	end
	local state = S
	if state == nil then
		sync_controls()
		return
	end
	local over = state.phase == "over"
	local turn: number = state.turn
	local playing = state.phase == "play" and prompt == nil

	local can_flip = not over and G.can_flip(state, turn)
	for i, id in state.stock do
		set_card(id, STOCK_X, 0.02 + i * 0.012, STOCK_Z, 1, false, can_flip)
	end
	if state.t ~= nil then
		for i, id in state.t.bonus do
			set_card(id, STOCK_X + 1.15 + (i - 1) * 0.3, 0.02 + i * 0.03, STOCK_Z, 1, true, false)
		end
	end

	for month = 1, 12 do
		local pile: { number } = state.floor[month]
		if #pile == 0 then
			slot_of[month] = 0
		else
			local slot = floor_slot(month) - 1
			local x = FLOOR_X[slot // 2 + 1]
			local z = FLOOR_Z[slot % 2 + 1]
			local at = 0
			for _, id in pile do
				local choice = state.phase == "pick" and (state.pick[1] == id or state.pick[2] == id)
				set_card(id, x - 0.3 + at * 0.3, 0.02 + at * 0.03, z, if choice then 1.12 else 1, true, choice)
				at += 1
			end
			for _, id in state.attached[month] do
				set_card(id, x - 0.3 + at * 0.3, 0.02 + at * 0.03, z, 1, true, false)
				at += 1
			end
		end
	end

	for seat = 1, state.n do
		local p = place_of(state, seat)
		local hand: { number } = table.clone(state.hands[seat])
		table.sort(hand)
		local viewer = if view_seat > state.n then 1 else view_seat
		local up = over or open_hands or seat == viewer
		local count = #hand
		for i, id in hand do
			local x = p.x + (i - (count + 1) / 2) * p.hand_step
			local lifted = prompt ~= nil and prompt.card == id
			set_card(id, x, 0.02 + i * 0.01, p.z - (if lifted then 0.35 * p.towards else 0), p.hand_scale, up, seat == turn and (playing or prompt ~= nil) and not over)
		end

		local at = { 0, 0, 0, 0 }
		for _, id in state.cap[seat] do
			local kind = G.KIND[id]
			local index = at[kind]
			at[kind] += 1
			local row = index // p.wrap
			local x = p.groups[kind] + (index % p.wrap) * p.cap_step
			set_card(id, x, 0.02 + index * 0.008, p.cap_z + row * p.row, p.cap_scale, true, false)
		end
	end
	sync_controls()
end

local function step(_state: State, what: string, data: any)
	if what == "event" then
		table.insert(events, EVENT_TEXT[data] or tostring(data))
		return
	elseif what == "steal" then
		table.insert(events, `피 {data}장 뺏음`)
		return
	end
	if headless then
		return
	end
	sync()
	bse.emit({ t = "fx", kind = "wait", seconds = STEP_SECONDS })
end

local function spawn_table()
	if headless then
		return
	end
	banner = bse.view.spawn("text", 0, 0, { props = { pos = { 0, 0.02, -2.85 }, label = "" } })
	for i = 1, 3 do
		buttons[i] = bse.view.spawn("button", 0, 0, { props = { pos = { 0, 0.02, 2.85 }, visible = false, pick = false, label = "" } })
		button_cmd[i] = false
	end
end

local function start(players: number)
	local state = G.new(players, { bonus = option_bonus })
	S = state
	table.clear(card_obj)
	table.clear(shown)
	table.clear(panels)
	table.clear(events)
	prompt = nil
	announced = false
	for month = 1, 12 do
		slot_of[month] = 0
	end
	if not headless then
		for id = 1, G.CARDS + state.bonus do
			card_obj[id] = bse.view.spawn("card", 0, 0, { props = { pos = { STOCK_X, 0.02, STOCK_Z }, image_rect = rect(BACK), pick = false } })
			shown[id] = ""
		end
		for seat = 1, state.n do
			local p = place_of(state, seat)
			panels[seat] = bse.view.spawn("text", 0, 0, { owner = seat, props = { pos = { p.panel_x, 0.02, p.panel_z }, label = "", label_size = 0.075 } })
		end
	end
end

-- Tells the engine whose turn it is, or that the game has ended.
local function announce()
	local state = S
	if state == nil then
		bse.turn(1)
		return
	end
	if state.phase ~= "over" then
		bse.turn(state.turn)
		return
	end
	if announced then
		return
	end
	announced = true
	local head, line1, line2 = result_lines(state)
	local reason = head
	if line1 ~= "" then
		reason ..= " (" .. line1 .. ")"
	end
	if line2 ~= "" then
		reason ..= " " .. line2
	end
	bse.over({ winner = state.result.winner, reason = reason })
end

local function integer(value: any, fallback: number, low: number, high: number): number
	if type(value) ~= "number" or value ~= value then
		return fallback
	end
	return math.clamp(math.floor(value), low, high)
end

function rules.new_game(options: { [string]: any })
	headless = bse.is_headless()
	option_players = integer(options.players, 0, 0, 3)
	if option_players == 1 then
		option_players = 2
	end
	option_bonus = integer(options.bonus, 2, 0, 3)
	view_seat = integer(options.view_seat, 1, 0, 3)
	open_hands = options.open_hands == true
	S = nil
	prompt = nil
	announced = false
	table.clear(events)
	table.clear(card_obj)
	table.clear(shown)
	table.clear(panels)
	for month = 1, 12 do
		slot_of[month] = 0
	end
	spawn_table()
	if option_players ~= 0 then
		start(option_players)
	end
	sync()
	announce()
end

-- What a click on an object means; nil when it means nothing.
local function click_command(state: State?, id: any): Command?
	for i = 1, 3 do
		if buttons[i] == id and button_cmd[i] then
			return button_cmd[i]
		end
	end
	if state == nil or state.phase == "over" then
		return nil
	end
	local seat: number = state.turn
	if state.phase == "pick" then
		for _, card in state.pick do
			if card_obj[card] == id then
				return { action = "pick", card = card }
			end
		end
		return nil
	end
	if state.phase ~= "play" then
		return nil
	end
	for _, card in state.hands[seat] do
		if card_obj[card] == id then
			return { action = "play", card = card, ask = true }
		end
	end
	if G.can_flip(state, seat) then
		for _, card in state.stock do
			if card_obj[card] == id then
				return { action = "flip" }
			end
		end
	end
	return nil
end

function rules.command(seat: number, cmd: { [string]: any }): any
	local state = S
	local command: Command = cmd
	if cmd.action == "click" then
		local meant = click_command(state, cmd.id)
		if meant == nil then
			return bse.reject("NOT_CLICKABLE", "nothing happens there")
		end
		command = meant
	end

	if command.action == "players" then
		if state ~= nil then
			return bse.reject("ALREADY_STARTED", "the game has already started")
		end
		if seat ~= 1 then
			return bse.reject("NOT_YOUR_TURN", "the first player chooses")
		end
		if command.count ~= 2 and command.count ~= 3 then
			return bse.reject("INVALID_ARGUMENT", "count must be 2 or 3")
		end
		start(command.count)
		sync()
		announce()
		return nil
	end

	if state == nil then
		return bse.reject("NOT_STARTED", "choose two or three players first")
	end
	if state.phase == "over" then
		return bse.reject("GAME_OVER", "the game is over")
	end
	if seat ~= state.turn then
		return bse.reject("NOT_YOUR_TURN", "it is not your turn")
	end

	-- A click on a card that could be a bomb or a shake asks first; the buttons then send the answer.
	if command.action == "play" and command.ask == true and type(command.card) == "number" then
		local month = G.MONTH[command.card]
		if month ~= nil and month ~= 0 then
			local kind = if G.can_bomb(state, seat, month) then "bomb" elseif G.can_shake(state, seat, month) then "shake" else ""
			if kind ~= "" then
				prompt = { kind = kind, card = command.card }
				sync()
				return nil
			end
		end
	end

	-- The rules check the command on a copy first, so that a refusal leaves everything as it was.
	local trial = G.clone(state)
	local code, message = G.apply(trial, command)
	if code ~= nil then
		return bse.reject(code, message or code)
	end

	prompt = nil
	table.clear(events)
	G.apply(state, command, step)
	sync()
	announce()
	return nil
end

function rules.legal_commands(seat: number): { any }
	local state = S
	if state == nil then
		return if seat == 1 then { { action = "players", count = 2 }, { action = "players", count = 3 } } else {}
	end
	if state.phase == "over" or seat ~= state.turn then
		return {}
	end
	return G.legal(state)
end

function rules.random_command(seat: number): any
	local state = S
	if state == nil then
		return { action = "players", count = if option_players == 3 then 3 else 2 }
	end
	if state.phase == "over" or seat ~= state.turn then
		return nil
	end
	local list = G.legal(state)
	if #list == 0 then
		return nil
	end
	return list[math.random(1, #list)]
end

function rules.query(name: string, args: any): any
	local state = S
	if name == "think" then
		if type(args) ~= "table" then
			return nil
		end
		if type(args.seed) == "number" then
			math.randomseed(args.seed)
		end
		if state == nil then
			return { action = "players", count = if option_players == 3 then 3 else 2 }
		end
		return ai.think(state, integer(args.seat, state.turn, 1, 3), integer(args.effort, 1, 1, 1000000))
	elseif name == "state" then
		if state == nil then
			return { phase = "choose" }
		end
		local seats: { any } = {}
		for seat = 1, state.n do
			local c = G.count(state.cap[seat])
			seats[seat] = {
				score = G.score_of(state, seat), go = state.go[seat], shake = state.shake[seat], ppeok = state.ppeok[seat],
				pass = state.pass[seat], hand = #state.hands[seat], taken = #state.cap[seat],
				bright = c.bright, animal = c.animal, ribbon = c.ribbon, junk = c.junk,
			}
		end
		return { phase = state.phase, players = state.n, turn = state.turn, stock = #state.stock, limit = G.limit(state),
			seats = seats, result = state.result, moves = state.moves }
	elseif name == "hand" then
		if state == nil then
			return {}
		end
		return state.hands[integer(if type(args) == "table" then args.seat else nil, state.turn, 1, state.n)]
	elseif name == "selftest" then
		return selftest.run()
	elseif name == "match" and type(args) == "table" then
		-- match {"a":300,"b":1,"games":20,"players":2}: seat 1 plays with effort a against effort b.
		math.randomseed(integer(args.seed, 1, 0, 2147483647))
		if type(args.width) == "number" then ai.WIDTH = args.width end
		if type(args.prior) == "number" then ai.PRIOR = args.prior end
		return selftest.match(integer(args.a, 1, 0, 1000000), integer(args.b, 1, 0, 1000000), integer(args.games, 10, 1, 100000), integer(args.players, 2, 2, 3))
	elseif name == "credits" then
		return {
			artwork = "Hwatu overview.svg by Marcus Richert, based on Hanafuda graphics by Louie Mantia, Jr.",
			source = "https://commons.wikimedia.org/wiki/File:Hwatu_overview.svg",
			license = "CC BY-SA 4.0",
			license_url = "https://creativecommons.org/licenses/by-sa/4.0/",
			changes = "Cards cut apart with a white face behind the line art, put in Korean month order; a card back and bonus cards added.",
		}
	end
	return nil
end

function rules.snapshot(): any
	return {
		state = S or false,
		players = option_players, bonus = option_bonus, view_seat = view_seat, open_hands = open_hands,
		card_obj = card_obj, shown = shown, slot_of = slot_of, buttons = buttons, button_cmd = button_cmd, panels = panels,
		banner = banner, prompt = prompt or false, events = events, announced = announced,
	}
end

function rules.restore(data: any)
	headless = bse.is_headless()
	S = if type(data.state) == "table" then data.state else nil
	option_players = data.players
	option_bonus = data.bonus
	view_seat = data.view_seat
	open_hands = data.open_hands
	card_obj = data.card_obj or {}
	shown = data.shown or {}
	slot_of = data.slot_of or {}
	buttons = data.buttons or {}
	button_cmd = data.button_cmd or {}
	panels = data.panels or {}
	banner = data.banner
	prompt = if type(data.prompt) == "table" then data.prompt else nil
	events = data.events or {}
	announced = data.announced == true
end

return rules
