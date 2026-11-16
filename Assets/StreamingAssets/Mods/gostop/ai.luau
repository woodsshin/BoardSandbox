--!strict
-- The computer player of Go-Stop. Two layers:
--
-- 1. A rule-based player (ai.policy): weights for taking cards that score, that finish a set (godori, the ribbon sets,
--    the brights) or that keep an opponent from finishing one, and for discarding what hurts least.
-- 2. Monte Carlo on top of it (ai.think with effort > 1): the cards the player cannot see (the other hands and the
--    stock) are dealt at random many times; in each of those games every candidate is played and the game is
--    finished by the rule-based player for every seat. The candidate with the best average result is chosen.
--
-- Uses only the rules module, never the screen.

local G = require("./gostop")

local ai = {}

type State = G.State
type Command = G.Command
type Counts = G.Counts

local MONTH = G.MONTH
local KIND = G.KIND
local JUNK = G.JUNK_VALUE
local SET = G.SET

local SET_FIELD = { "bird", "red", "grass", "blue" }

-- How much the player with counts `my` wants a card, given what the opponents hold.
local function card_value(id: number, my: Counts, opps: { Counts }): number
	local kind = KIND[id]
	local v = 0
	if kind == G.BRIGHT then
		v = 5 + my.bright * 3
		if id == G.RAIN_BRIGHT then
			v -= 1
		end
		for _, o in opps do
			if o.bright >= 2 then
				v += 5
			end
		end
	elseif kind == G.ANIMAL then
		v = 2
		if my.animal >= 3 then
			v += 1.5
		end
		if id == G.GUKJIN then
			v += 1.5
		end
	elseif kind == G.RIBBON then
		v = 2
		if my.ribbon >= 3 then
			v += 1.5
		end
	else
		v = JUNK[id] * 2.2
		if my.junk >= 7 then
			v += 1
		end
	end
	local set = SET[id]
	if set ~= 0 then
		local field = SET_FIELD[set]
		local mine: number = (my :: any)[field]
		local blocked = false
		for _, o in opps do
			local theirs: number = (o :: any)[field]
			if theirs == 2 then
				-- The card that keeps an opponent from finishing a set.
				v += 7
			end
			if theirs > 0 then
				blocked = true
			end
		end
		if set == 1 then
			v += 1
		end
		if not blocked or mine > 0 then
			v += mine * 3
		end
	end
	return v
end

-- What the player on turn knows: the counts of everyone and how many cards of each month are still unseen.
local function survey(S: State, seat: number): (Counts, { Counts }, { number })
	local my = G.count(S.cap[seat])
	local opps: { Counts } = {}
	local unseen: { number } = {}
	for month = 1, 12 do
		unseen[month] = 4 - #S.floor[month]
	end
	for other = 1, S.n do
		if other ~= seat then
			table.insert(opps, G.count(S.cap[other]))
		end
		for _, id in S.cap[other] do
			local month = MONTH[id]
			if month ~= 0 then
				unseen[month] -= 1
			end
		end
	end
	for _, id in S.hands[seat] do
		local month = MONTH[id]
		if month ~= 0 then
			unseen[month] -= 1
		end
	end
	return my, opps, unseen
end

local function best_of(pile: { number }, my: Counts, opps: { Counts }): (number, number)
	local best, best_id = -1, pile[1]
	for _, id in pile do
		local v = card_value(id, my, opps)
		if v > best then
			best, best_id = v, id
		end
	end
	return best, best_id
end

local function go_or_stop(S: State, seat: number): Command
	local limit = G.limit(S)
	local danger = 0
	for other = 1, S.n do
		if other ~= seat then
			local c = G.count(S.cap[other])
			local score = G.score(c)
			local threat = score
			if c.bright >= 2 then
				threat += 2
			end
			if c.bird == 2 or c.red == 2 or c.grass == 2 or c.blue == 2 then
				threat += 2
			end
			if c.junk >= 8 then
				threat += 1
			end
			if c.animal >= 4 or c.ribbon >= 4 then
				threat += 1
			end
			danger = math.max(danger, threat)
		end
	end
	local turns_left = #S.stock // S.n
	if turns_left >= 2 and danger <= limit - 3 then
		return { action = "go" }
	end
	return { action = "stop" }
end

type Scored = { cmd: Command, value: number }

-- Every play of the hand with what the rule-based player thinks of it, best first.
local function rank(S: State, seat: number): { Scored }
	local hand: { number } = S.hands[seat]
	local my, opps, unseen = survey(S, seat)
	local mine = { my }
	local list: { Scored } = {}
	local seen: { [number]: boolean } = {}
	for _, id in hand do
		local month = MONTH[id]
		-- Plain junk of one month is one choice.
		local key = if KIND[id] == G.JUNK and JUNK[id] == 1 then month * 100 else id
		if seen[key] then
			continue
		end
		seen[key] = true
		local pile: { number } = S.floor[month]
		local on_floor = #pile
		local held = G.count_month(hand, month)
		local own = card_value(id, my, opps)
		local value: number
		local cmd: Command = { action = "play", card = id }
		if on_floor == 3 then
			value = own + 6
			for _, other in pile do
				value += card_value(other, my, opps)
			end
		elseif on_floor >= 1 then
			if held == 3 and on_floor == 1 then
				value = own + card_value(pile[1], my, opps) + 9
				for _, other in hand do
					if other ~= id and MONTH[other] == month then
						value += card_value(other, my, opps)
					end
				end
				cmd.bomb = true
			else
				local top = best_of(pile, my, opps)
				value = own + top
				if on_floor == 1 and unseen[month] > 0 then
					-- The fourth card may be flipped onto the pair.
					value -= 0.8
				end
			end
		else
			-- Nothing to take: the card is given to the floor. It costs what an opponent gains by taking it.
			local worst = 0
			for _, o in opps do
				worst = math.max(worst, card_value(id, o, mine))
			end
			if unseen[month] == 0 then
				value = if held >= 2 then 0.5 else -0.2
			else
				value = -0.5 * own - 0.6 * worst - 0.3 * unseen[month]
				if held >= 2 then
					value += 1
				end
			end
			if G.can_shake(S, seat, month) then
				cmd.shake = true
				value += 1.5
			end
		end
		table.insert(list, { cmd = cmd, value = value })
	end
	if G.can_flip(S, seat) then
		table.insert(list, { cmd = { action = "flip" }, value = -0.6 })
	end
	table.sort(list, function(a: Scored, b: Scored): boolean
		return a.value > b.value
	end)
	return list
end

-- The move the rule-based player makes for the player on turn.
function ai.policy(S: State): Command?
	local seat: number = S.turn
	if S.phase == "gostop" then
		return go_or_stop(S, seat)
	end
	if S.phase == "pick" then
		local my, opps = survey(S, seat)
		local _, id = best_of(S.pick, my, opps)
		return { action = "pick", card = id }
	end
	if S.phase ~= "play" then
		return nil
	end

	local hand: { number } = S.hands[seat]
	for _, id in hand do
		if MONTH[id] == 0 then
			return { action = "play", card = id }
		end
	end
	if #hand == 0 then
		return { action = "flip" }
	end

	local list = rank(S, seat)
	return if list[1] then list[1].cmd else nil
end

-- Deals the cards the seat cannot see again: the other hands and the stock.
local function determinize(S: State, seat: number): State
	local D = G.clone(S)
	local pool: { number } = {}
	for other = 1, D.n do
		if other ~= seat then
			for _, id in D.hands[other] do
				table.insert(pool, id)
			end
		end
	end
	for _, id in D.stock do
		table.insert(pool, id)
	end
	for i = #pool, 2, -1 do
		local j = math.random(1, i)
		pool[i], pool[j] = pool[j], pool[i]
	end
	local at = 0
	for other = 1, D.n do
		if other ~= seat then
			local hand: { number } = D.hands[other]
			for i = 1, #hand do
				at += 1
				hand[i] = pool[at]
			end
		end
	end
	local stock: { number } = D.stock
	for i = 1, #stock do
		at += 1
		stock[i] = pool[at]
	end
	return D
end

-- What a finished game is worth to the seat: the points won or paid. Very large results are counted for less, so that
-- one game with many doublings does not outweigh all the others.
local CLIP = 40

local function payoff(S: State, seat: number): number
	local result = S.result
	if result == nil or result.winner == 0 then
		return 0
	end
	local p: number = if result.winner == seat then result.total else -result.pays[seat]
	if p > CLIP then
		return CLIP + (p - CLIP) * 0.25
	elseif p < -CLIP then
		return -CLIP + (p + CLIP) * 0.25
	end
	return p
end

local function playout(S: State)
	for _ = 1, 400 do
		if S.phase == "over" then
			return
		end
		local cmd = ai.policy(S)
		if cmd == nil or G.apply(S, cmd) ~= nil then
			return
		end
	end
end

ai.playouts = 0

-- How many of the plays the rule-based player likes best the search compares, and how much its opinion counts
-- next to the result of the games: the fewer games were played, the more.
ai.WIDTH = 5
ai.PRIOR = 1.5

-- effort: how many games the search plays to the end; 1 or less asks the rule-based player alone.
function ai.think(S: State, seat: number, effort: number): Command?
	if S.phase == "over" or S.turn ~= seat then
		return nil
	end
	local base = ai.policy(S)
	if effort <= 1 or base == nil then
		return base
	end

	local list: { Scored } = {}
	if S.phase == "play" then
		-- A bonus card is always played first; there is nothing to compare.
		if base.action == "play" and MONTH[base.card] == 0 then
			return base
		end
		local ranked = rank(S, seat)
		for i = 1, math.min(#ranked, ai.WIDTH) do
			list[i] = ranked[i]
		end
	else
		for _, cmd in G.legal(S) do
			local same = cmd.action == base.action and cmd.card == base.card
			table.insert(list, { cmd = cmd, value = if same then 1 else 0 })
		end
	end
	if #list <= 1 then
		return if list[1] then list[1].cmd else base
	end

	local rounds = math.max(2, effort // #list)
	local totals: { number } = table.create(#list, 0)
	for _ = 1, rounds do
		local D = determinize(S, seat)
		for i, item in list do
			local C = G.clone(D)
			if G.apply(C, item.cmd) == nil then
				playout(C)
				totals[i] += payoff(C, seat)
			else
				totals[i] -= 1000
			end
			ai.playouts += 1
		end
	end
	local best, best_score = list[1].cmd, -math.huge
	local weight = ai.PRIOR / math.sqrt(rounds)
	for i, item in list do
		local score = totals[i] / rounds + weight * item.value
		if score > best_score then
			best_score = score
			best = item.cmd
		end
	end
	return best
end

return ai
