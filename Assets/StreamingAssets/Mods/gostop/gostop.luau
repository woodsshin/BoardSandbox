--!strict
-- The rules of Go-Stop (hwatu) for two players (Matgo) or three, without anything of the screen: the state of a game
-- is one plain table, so the computer player can copy it and play it to the end thousands of times.
--
-- Cards 1..48 are (month - 1) * 4 + slot; 49 and 50 are bonus cards worth two junk, 51 is worth three.
-- A turn: play a card from the hand (or a bomb, or only flip after a bomb), flip the top of the stock, take what
-- matches, steal junk for a kiss, a ttadak, a sweep, a bomb or a taken ppeok, then choose go or stop once the
-- score reaches the limit (7 for two players, 3 for three).

local G = {}

export type Command = any
export type State = { [string]: any }

G.BRIGHT = 1
G.ANIMAL = 2
G.RIBBON = 3
G.JUNK = 4

G.RAIN_BRIGHT = 45
G.GUKJIN = 33
G.CARDS = 48
G.FIRST_BONUS = 49
G.LAST_BONUS = 51

local MONTH: { number } = {}
local KIND: { number } = {}
local JUNK: { number } = {}
-- 0 none, 1 godori bird, 2 red ribbon, 3 grass ribbon, 4 blue ribbon
local SET: { number } = {}

for id = 1, 48 do
	MONTH[id] = (id - 1) // 4 + 1
	KIND[id] = G.JUNK
	JUNK[id] = 1
	SET[id] = 0
end
for id = 49, 51 do
	MONTH[id] = 0
	KIND[id] = G.JUNK
	JUNK[id] = if id == 51 then 3 else 2
	SET[id] = 0
end
for _, id in { 1, 9, 29, 41, 45 } do
	KIND[id] = G.BRIGHT
end
for _, id in { 5, 13, 17, 21, 25, 30, 33, 37, 46 } do
	KIND[id] = G.ANIMAL
end
for _, id in { 2, 6, 10, 14, 18, 22, 26, 34, 38, 47 } do
	KIND[id] = G.RIBBON
end
for id = 1, 48 do
	if KIND[id] ~= G.JUNK then
		JUNK[id] = 0
	end
end
JUNK[42] = 2
JUNK[48] = 2
for _, id in { 5, 13, 30 } do
	SET[id] = 1
end
for _, id in { 2, 6, 10 } do
	SET[id] = 2
end
for _, id in { 14, 18, 26 } do
	SET[id] = 3
end
for _, id in { 22, 34, 38 } do
	SET[id] = 4
end

G.MONTH = MONTH
G.KIND = KIND
G.JUNK_VALUE = JUNK
G.SET = SET

export type Counts = {
	bright: number,
	rain: number,
	animal: number,
	ribbon: number,
	junk: number,
	bird: number,
	red: number,
	grass: number,
	blue: number,
	gukjin: number,
}

function G.count(cards: { number }): Counts
	local c: Counts = { bright = 0, rain = 0, animal = 0, ribbon = 0, junk = 0, bird = 0, red = 0, grass = 0, blue = 0, gukjin = 0 }
	for _, id in cards do
		local kind = KIND[id]
		if kind == G.JUNK then
			c.junk += JUNK[id]
		elseif kind == G.BRIGHT then
			c.bright += 1
			if id == G.RAIN_BRIGHT then
				c.rain = 1
			end
		elseif kind == G.ANIMAL then
			c.animal += 1
			if id == G.GUKJIN then
				c.gukjin = 1
			end
			if SET[id] == 1 then
				c.bird += 1
			end
		else
			c.ribbon += 1
			local set = SET[id]
			if set == 2 then
				c.red += 1
			elseif set == 3 then
				c.grass += 1
			elseif set == 4 then
				c.blue += 1
			end
		end
	end
	return c
end

local function points(c: Counts, animal: number, junk: number): number
	local s = 0
	if c.bright == 5 then
		s += 15
	elseif c.bright == 4 then
		s += 4
	elseif c.bright == 3 then
		s += if c.rain == 1 then 2 else 3
	end
	if animal >= 5 then
		s += animal - 4
	end
	if c.bird == 3 then
		s += 5
	end
	if c.ribbon >= 5 then
		s += c.ribbon - 4
	end
	if c.red == 3 then
		s += 3
	end
	if c.grass == 3 then
		s += 3
	end
	if c.blue == 3 then
		s += 3
	end
	if junk >= 10 then
		s += junk - 9
	end
	return s
end

-- The points of what a player has taken, and the animals and junk they were counted with: the chrysanthemum cup
-- (gukjin) counts as an animal or as two junk, whichever gives more.
function G.score(c: Counts): (number, number, number)
	local plain = points(c, c.animal, c.junk)
	if c.gukjin == 1 then
		local swapped = points(c, c.animal - 1, c.junk + 2)
		if swapped > plain then
			return swapped, c.animal - 1, c.junk + 2
		end
	end
	return plain, c.animal, c.junk
end

function G.limit(S: State): number
	return if S.n == 2 then 7 else 3
end

function G.score_of(S: State, seat: number): number
	local s = G.score(G.count(S.cap[seat]))
	return s
end

local function shuffle(list: { number })
	for i = #list, 2, -1 do
		local j = math.random(1, i)
		list[i], list[j] = list[j], list[i]
	end
end

local function remove_value(list: { number }, value: number): boolean
	for i = 1, #list do
		if list[i] == value then
			table.remove(list, i)
			return true
		end
	end
	return false
end

local function contains(list: { number }, value: number): boolean
	for i = 1, #list do
		if list[i] == value then
			return true
		end
	end
	return false
end

function G.count_month(cards: { number }, month: number): number
	local n = 0
	for _, id in cards do
		if MONTH[id] == month then
			n += 1
		end
	end
	return n
end

-- Two cards nobody would choose between: both plain junk.
local function alike(a: number, b: number): boolean
	return KIND[a] == G.JUNK and KIND[b] == G.JUNK and JUNK[a] == JUNK[b]
end

local hook: ((State, string, any) -> ())? = nil

local function tell(S: State, what: string, data: any)
	if hook then
		hook(S, what, data)
	end
end

local function deal(S: State, bonus: number): boolean
	local n: number = S.n
	local deck: { number } = {}
	for id = 1, 48 + bonus do
		deck[id] = id
	end
	shuffle(deck)
	local hand_size = if n == 2 then 10 else 7
	local floor_size = if n == 2 then 8 else 6
	S.hands = {}
	S.cap = {}
	for seat = 1, n do
		local hand: { number } = {}
		for _ = 1, hand_size do
			table.insert(hand, table.remove(deck) :: number)
		end
		S.hands[seat] = hand
		S.cap[seat] = {}
	end
	S.floor = {}
	S.attached = {}
	for month = 1, 12 do
		S.floor[month] = {}
		S.attached[month] = {}
	end
	local placed = 0
	while placed < floor_size do
		local id = table.remove(deck) :: number
		if MONTH[id] == 0 then
			-- A bonus card dealt to the floor goes to the first player, and another card takes its place.
			table.insert(S.cap[1], id)
		else
			table.insert(S.floor[MONTH[id]], id)
			placed += 1
		end
	end
	S.stock = deck
	for month = 1, 12 do
		if #S.floor[month] == 4 then
			return false
		end
	end
	return true
end

-- options: bonus (0..3 bonus cards). The first player is seat 1.
function G.new(players: number, options: { [string]: any }?): State
	local n = if players == 3 then 3 else 2
	local bonus = 2
	if options and type(options.bonus) == "number" then
		bonus = math.clamp(math.floor(options.bonus), 0, 3)
	end
	local S: State = { n = n, bonus = bonus }
	repeat
		local ok = deal(S, bonus)
	until ok
	S.turn = 1
	S.phase = "play"
	S.go = {}
	S.go_score = {}
	S.shake = {}
	S.shaken = {}
	S.ppeok = {}
	S.pass = {}
	S.owner = {}
	for seat = 1, n do
		S.go[seat] = 0
		S.go_score[seat] = 0
		S.shake[seat] = 0
		S.shaken[seat] = {}
		S.ppeok[seat] = 0
		S.pass[seat] = 0
	end
	for month = 1, 12 do
		S.owner[month] = 0
	end
	S.pick = {}
	S.pick_stage = ""
	S.moves = 0
	-- Four cards of one month in a hand (chongtong) win at once.
	for seat = 1, n do
		for month = 1, 12 do
			if G.count_month(S.hands[seat], month) == 4 then
				G.finish(S, seat, "chongtong")
				return S
			end
		end
	end
	return S
end

function G.clone(S: State): State
	local function copy(value: any): any
		if type(value) ~= "table" then
			return value
		end
		local out = table.clone(value)
		for key, item in out do
			if type(item) == "table" then
				out[key] = copy(item)
			end
		end
		return out
	end
	return copy(S)
end

local function capture(S: State, seat: number, cards: { number })
	local cap = S.cap[seat]
	for _, id in cards do
		table.insert(cap, id)
	end
	S.t.took += #cards
end

-- Takes one junk card from a player: a plain one if there is one, else a double, else a triple.
local function steal(S: State, from: number, to: number): boolean
	local cap = S.cap[from]
	for want = 1, 3 do
		for i = #cap, 1, -1 do
			local id = cap[i]
			if KIND[id] == G.JUNK and JUNK[id] == want then
				table.remove(cap, i)
				table.insert(S.cap[to], id)
				return true
			end
		end
	end
	return false
end

local function take_pile(S: State, seat: number, month: number)
	local pile = S.floor[month]
	capture(S, seat, pile)
	capture(S, seat, S.attached[month])
	S.floor[month] = {}
	S.attached[month] = {}
	local owner = S.owner[month]
	if owner ~= 0 then
		-- Taking a ppeok steals a junk card; taking one's own steals two.
		S.t.steal += if owner == seat then 2 else 1
		S.owner[month] = 0
		tell(S, "event", if owner == seat then "jappeok" else "ppeok_taken")
	end
end

-- Settles the card played from the hand once the flipped card turned out to be of another month.
local function resolve_hand(S: State)
	local T = S.t
	local seat: number = S.turn
	local kind = T.kind
	if kind == "pair" or kind == "pair2" then
		local pile = S.floor[T.month]
		remove_value(pile, T.card)
		remove_value(pile, T.target)
		capture(S, seat, { T.card, T.target })
	elseif kind == "quad" then
		take_pile(S, seat, T.month)
	end
	if kind ~= "ppeok" then
		T.kind = "done"
	end
end

local function floor_empty(S: State): boolean
	for month = 1, 12 do
		if #S.floor[month] > 0 then
			return false
		end
	end
	return true
end

function G.finish(S: State, winner: number, how: string)
	local n: number = S.n
	local pays: { number } = {}
	local flags: { any } = {}
	for seat = 1, n do
		pays[seat] = 0
		flags[seat] = {}
	end
	local result: { [string]: any } = { winner = winner, how = how, pays = pays, flags = flags, total = 0, points = 0, score = 0 }
	S.result = result
	S.phase = "over"
	S.pick = {}
	if winner == 0 then
		return
	end

	local c = G.count(S.cap[winner])
	local score, animal, junk = G.score(c)
	local go: number = S.go[winner]
	local pts = score
	local flat = how == "chongtong" or how == "ppeok3"
	if how == "chongtong" then
		pts = 10
	elseif how == "ppeok3" then
		pts = math.max(score, G.limit(S))
	end
	if go <= 2 then
		pts += go
	else
		pts = (pts + 2) * 2 ^ (go - 2)
	end
	local mult = 2 ^ S.shake[winner]
	if not flat and animal >= 7 then
		mult *= 2
		result.meongtta = true
	end
	result.score = score
	result.points = pts
	result.mult = mult

	local junk_safe = if n == 2 then 8 else 6
	local gobak = 0
	for seat = 1, n do
		if seat ~= winner then
			local m = mult
			local f = flags[seat]
			if not flat then
				local lc = G.count(S.cap[seat])
				local loser_junk = lc.junk + lc.gukjin * 2
				if junk >= 10 and loser_junk < junk_safe then
					m *= 2
					f.pibak = true
				end
				if c.bright >= 3 and lc.bright == 0 then
					m *= 2
					f.gwangbak = true
				end
			end
			if S.go[seat] > 0 then
				f.gobak = true
				gobak += 1
				if n == 2 then
					m *= 2
				end
			end
			pays[seat] = pts * m
		end
	end
	-- With three players, the one who said go and lost pays for the other loser too.
	if n == 3 and gobak == 1 then
		local sum = 0
		for seat = 1, n do
			sum += pays[seat]
		end
		for seat = 1, n do
			if seat ~= winner then
				pays[seat] = if flags[seat].gobak then sum else 0
			end
		end
	end
	local total = 0
	for seat = 1, n do
		total += pays[seat]
	end
	result.total = total
end

local function next_turn(S: State)
	S.turn = S.turn % S.n + 1
	S.phase = "play"
	S.t = nil
	if #S.stock == 0 then
		-- Nobody stopped before the cards ran out: nagari.
		G.finish(S, 0, "nagari")
	end
end

local function end_turn(S: State)
	local T = S.t
	local seat: number = S.turn
	local n: number = S.n
	if #T.bonus > 0 then
		capture(S, seat, T.bonus)
		T.bonus = {}
	end
	-- The player has no turn after this one when the stock does not go round once more.
	local last = #S.stock < n
	if T.kind ~= "ppeok" and T.took > 0 and floor_empty(S) then
		T.soft += 1
		tell(S, "event", "sweep")
	end
	local count: number = T.steal + (if last then 0 else T.soft)
	if count > 0 then
		local stolen = 0
		for other = 1, n do
			if other ~= seat then
				for _ = 1, count do
					if steal(S, other, seat) then
						stolen += 1
					end
				end
			end
		end
		if stolen > 0 then
			tell(S, "steal", stolen)
		end
	end
	tell(S, "turn_end", nil)

	if S.ppeok[seat] >= 3 then
		G.finish(S, seat, "ppeok3")
		return
	end
	local score = G.score_of(S, seat)
	if score >= G.limit(S) and score > S.go_score[seat] then
		if last then
			G.finish(S, seat, "stop")
		else
			S.phase = "gostop"
		end
		return
	end
	next_turn(S)
end

local function flip(S: State)
	local T = S.t
	local seat: number = S.turn
	while #S.stock > 0 do
		local id = table.remove(S.stock) :: number
		T.flip = id
		local month = MONTH[id]
		if month == 0 then
			table.insert(T.bonus, id)
			tell(S, "flip", id)
			continue
		end
		local pile = S.floor[month]
		if month == T.month and (T.kind == "lay" or T.kind == "pair" or T.kind == "pair2") then
			table.insert(pile, id)
			tell(S, "flip", id)
			if T.kind == "lay" then
				-- The flipped card matches the card just laid down: a kiss.
				S.floor[month] = {}
				capture(S, seat, pile)
				T.soft += 1
				T.kind = "done"
				tell(S, "event", "kiss")
			elseif T.kind == "pair" then
				-- Three of a month meet: they stay on the floor until someone brings the fourth.
				S.owner[month] = seat
				S.ppeok[seat] += 1
				for _, b in T.bonus do
					table.insert(S.attached[month], b)
				end
				T.bonus = {}
				T.kind = "ppeok"
				tell(S, "event", "ppeok")
			else
				S.floor[month] = {}
				capture(S, seat, pile)
				T.soft += 1
				T.kind = "done"
				tell(S, "event", "ttadak")
			end
			break
		end

		resolve_hand(S)
		local before = #pile
		table.insert(pile, id)
		tell(S, "flip", id)
		if before == 1 then
			S.floor[month] = {}
			capture(S, seat, pile)
		elseif before == 2 then
			if alike(pile[1], pile[2]) then
				local other = pile[1]
				table.remove(pile, 3)
				table.remove(pile, 1)
				capture(S, seat, { id, other })
			else
				S.phase = "pick"
				S.pick = { pile[1], pile[2] }
				S.pick_stage = "flip"
				return
			end
		elseif before == 3 then
			take_pile(S, seat, month)
		end
		break
	end
	if T.kind ~= "done" and T.kind ~= "ppeok" then
		resolve_hand(S)
	end
	end_turn(S)
end

local function new_turn(): { [string]: any }
	return { kind = "none", month = 0, card = 0, target = 0, bonus = {}, steal = 0, soft = 0, took = 0, flip = 0 }
end

local function can_shake(S: State, seat: number, month: number): boolean
	return G.count_month(S.hands[seat], month) >= 3 and #S.floor[month] == 0 and not contains(S.shaken[seat], month)
end

local function can_bomb(S: State, seat: number, month: number): boolean
	return G.count_month(S.hands[seat], month) == 3 and #S.floor[month] == 1
end

G.can_shake = can_shake
G.can_bomb = can_bomb

-- Whether the player may flip without playing a card: after a bomb, or with nothing left to play.
function G.can_flip(S: State, seat: number): boolean
	return S.phase == "play" and S.turn == seat and (S.pass[seat] > 0 or #S.hands[seat] == 0)
end

-- Every command the player on turn may give.
function G.legal(S: State): { Command }
	local list: { Command } = {}
	local seat: number = S.turn
	if S.phase == "play" then
		for _, id in S.hands[seat] do
			local month = MONTH[id]
			table.insert(list, { action = "play", card = id })
			if month ~= 0 then
				if can_bomb(S, seat, month) then
					table.insert(list, { action = "play", card = id, bomb = true })
				elseif can_shake(S, seat, month) then
					table.insert(list, { action = "play", card = id, shake = true })
				end
			end
		end
		if G.can_flip(S, seat) then
			table.insert(list, { action = "flip" })
		end
	elseif S.phase == "pick" then
		for _, id in S.pick do
			table.insert(list, { action = "pick", card = id })
		end
	elseif S.phase == "gostop" then
		table.insert(list, { action = "go" })
		table.insert(list, { action = "stop" })
	end
	return list
end

-- Applies a command of the player on turn. Returns nil, or a code and a message when the command is not allowed;
-- nothing has changed then. on_step, when given, is called at every step worth showing.
function G.apply(S: State, cmd: Command, on_step: ((State, string, any) -> ())?): (string?, string?)
	local seat: number = S.turn
	local action = cmd.action
	if S.phase == "over" then
		return "GAME_OVER", "the game is over"
	end
	if action == "play" then
		if S.phase ~= "play" then
			return "WRONG_PHASE", "a card cannot be played now"
		end
		local id = cmd.card
		local hand: { number } = S.hands[seat]
		if type(id) ~= "number" or not contains(hand, id) then
			return "NOT_IN_HAND", "that card is not in the hand"
		end
		local month = MONTH[id]
		if cmd.bomb == true and (month == 0 or not can_bomb(S, seat, month)) then
			return "NO_BOMB", "a bomb needs three cards of a month in the hand and one on the floor"
		end
		if cmd.shake == true and (month == 0 or not can_shake(S, seat, month)) then
			return "NO_SHAKE", "shaking needs three cards of a month in the hand and none on the floor"
		end

		hook = on_step
		S.moves += 1
		if month == 0 then
			-- A bonus card goes straight to the taken cards and a card from the stock replaces it in the hand.
			remove_value(hand, id)
			table.insert(S.cap[seat], id)
			if #S.stock > 0 then
				table.insert(hand, table.remove(S.stock) :: number)
			end
			tell(S, "bonus", id)
			hook = nil
			return nil, nil
		end

		local T = new_turn()
		S.t = T
		T.month = month
		T.card = id
		local pile: { number } = S.floor[month]
		if cmd.bomb == true then
			local taken: { number } = {}
			for i = #hand, 1, -1 do
				if MONTH[hand[i]] == month then
					table.insert(taken, table.remove(hand, i) :: number)
				end
			end
			table.insert(taken, pile[1])
			S.floor[month] = {}
			capture(S, seat, taken)
			T.kind = "done"
			T.steal += 1
			S.shake[seat] += 1
			S.pass[seat] += 2
			tell(S, "event", "bomb")
		else
			if cmd.shake == true then
				S.shake[seat] += 1
				table.insert(S.shaken[seat], month)
				tell(S, "event", "shake")
			end
			remove_value(hand, id)
			local before = #pile
			table.insert(pile, id)
			if before == 0 then
				T.kind = "lay"
			elseif before == 1 then
				T.kind = "pair"
				T.target = pile[1]
			elseif before == 2 then
				if alike(pile[1], pile[2]) then
					T.kind = "pair2"
					T.target = pile[1]
				else
					S.phase = "pick"
					S.pick = { pile[1], pile[2] }
					S.pick_stage = "hand"
					tell(S, "played", id)
					hook = nil
					return nil, nil
				end
			else
				T.kind = "quad"
			end
		end
		tell(S, "played", id)
		flip(S)
		hook = nil
		return nil, nil
	elseif action == "flip" then
		if not G.can_flip(S, seat) then
			return "NO_FLIP", "a card has to be played"
		end
		hook = on_step
		S.moves += 1
		if S.pass[seat] > 0 then
			S.pass[seat] -= 1
		end
		S.t = new_turn()
		flip(S)
		hook = nil
		return nil, nil
	elseif action == "pick" then
		if S.phase ~= "pick" then
			return "WRONG_PHASE", "there is nothing to choose"
		end
		local id = cmd.card
		if type(id) ~= "number" or not contains(S.pick, id) then
			return "NOT_A_CHOICE", "choose one of the two cards"
		end
		hook = on_step
		S.moves += 1
		local T = S.t
		S.pick = {}
		S.phase = "play"
		if S.pick_stage == "hand" then
			T.kind = "pair2"
			T.target = id
			flip(S)
		else
			local pile: { number } = S.floor[MONTH[id]]
			remove_value(pile, id)
			remove_value(pile, T.flip)
			capture(S, seat, { T.flip, id })
			end_turn(S)
		end
		hook = nil
		return nil, nil
	elseif action == "go" or action == "stop" then
		if S.phase ~= "gostop" then
			return "WRONG_PHASE", "go and stop are chosen after reaching the limit"
		end
		hook = on_step
		S.moves += 1
		if action == "go" then
			S.go[seat] += 1
			S.go_score[seat] = G.score_of(S, seat)
			tell(S, "event", "go")
			next_turn(S)
		else
			G.finish(S, seat, "stop")
		end
		hook = nil
		return nil, nil
	end
	return "UNKNOWN_ACTION", "unknown action"
end

return G
