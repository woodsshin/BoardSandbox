--!strict
-- Checks of the rules and the computer player that run inside the mod: rules.query("selftest") returns the list of
-- failures, which is empty when everything holds. Positions are built by hand so that every special case is met.

local G = require("./gostop")
local ai = require("./ai")

local selftest = {}

type State = G.State

local failures: { string } = {}

local function check(condition: boolean, what: string)
	if not condition then
		table.insert(failures, what)
	end
end

-- A game with the given cards and nothing else; stock is listed bottom first, so its last card is flipped first.
local function position(n: number, hands: { { number } }, floor: { number }, stock: { number }): State
	local S = G.new(n, { bonus = 0 })
	S.phase = "play"
	S.result = nil
	S.hands = hands
	S.stock = stock
	for month = 1, 12 do
		S.floor[month] = {}
		S.attached[month] = {}
		S.owner[month] = 0
	end
	for _, id in floor do
		table.insert(S.floor[G.MONTH[id]], id)
	end
	for seat = 1, n do
		S.cap[seat] = {}
	end
	return S
end

local function has(list: { number }, id: number): boolean
	return table.find(list, id) ~= nil
end

local function play(S: State, cmd: any): string?
	local code = G.apply(S, cmd)
	return code
end

-- Every card is in exactly one place.
local function conserved(S: State): boolean
	local seen: { [number]: number } = {}
	local total = 0
	local function add(list: { number })
		for _, id in list do
			seen[id] = (seen[id] or 0) + 1
			total += 1
		end
	end
	for seat = 1, S.n do
		add(S.hands[seat])
		add(S.cap[seat])
	end
	for month = 1, 12 do
		add(S.floor[month])
		add(S.attached[month])
	end
	add(S.stock)
	if S.t ~= nil then
		add(S.t.bonus)
	end
	if total ~= 48 + S.bonus then
		return false
	end
	for id = 1, 48 + S.bonus do
		if seen[id] ~= 1 then
			return false
		end
	end
	return true
end

local function floor_empty_for_test(S: State): boolean
	for month = 1, 12 do
		if #S.floor[month] > 0 then
			return false
		end
	end
	return true
end

local function scoring()
	local function score(cards: { number }): number
		local s = G.score(G.count(cards))
		return s
	end
	check(score({ 1, 9, 29 }) == 3, "three brights are 3")
	check(score({ 1, 9, 45 }) == 2, "three brights with the rain are 2")
	check(score({ 1, 9, 29, 41 }) == 4, "four brights are 4")
	check(score({ 1, 9, 29, 41, 45 }) == 15, "five brights are 15")
	check(score({ 5, 13, 30 }) == 5, "godori is 5")
	check(score({ 2, 6, 10 }) == 3, "red ribbons are 3")
	check(score({ 14, 18, 26 }) == 3, "grass ribbons are 3")
	check(score({ 22, 34, 38 }) == 3, "blue ribbons are 3")
	check(score({ 2, 6, 10, 14, 18 }) == 4, "five ribbons with the red set are 4")
	check(score({ 17, 21, 25, 37, 46 }) == 1, "five animals are 1")
	check(score({ 3, 4, 7, 8, 11, 12, 15, 16, 19 }) == 0, "nine junk are nothing")
	check(score({ 3, 4, 7, 8, 11, 12, 15, 16, 42 }) == 1, "a double junk makes ten")
	check(score({ 3, 4, 7, 8, 11, 12, 15, 16, 33 }) == 1, "the chrysanthemum cup counts as two junk when that is more")
	check(score({ 17, 21, 25, 37, 33 }) == 1, "the chrysanthemum cup counts as an animal when that is more")
	check(score({ 49, 50, 51, 3, 4, 7 }) == 1, "bonus cards are two, two and three junk")
end

local function turns()
	-- A plain pair: 3 takes 4, the flip (5) stays.
	local S = position(2, { { 3, 20 }, { 24, 28 } }, { 4 }, { 40, 36, 5 })
	check(play(S, { action = "play", card = 3 }) == nil, "a card of the hand is played")
	check(has(S.cap[1], 3) and has(S.cap[1], 4) and #S.cap[1] == 2, "a pair is taken")
	check(#S.floor[2] == 1 and S.turn == 2, "the flipped card stays and the turn passes")
	check(play(S, { action = "play", card = 3 }) == "NOT_IN_HAND", "only cards of the hand are played")

	-- A kiss: the flipped card matches the card just laid; one junk is stolen.
	S = position(2, { { 3, 20 }, { 24, 28 } }, { 16 }, { 40, 36, 4 })
	S.cap[2] = { 7, 8 }
	play(S, { action = "play", card = 3 })
	check(has(S.cap[1], 3) and has(S.cap[1], 4), "a kiss takes both cards")
	check(#S.cap[2] == 1 and #S.cap[1] == 3, "a kiss steals one junk")

	-- Ppeok: the pair meets the third card; all three stay. The fourth takes them and steals.
	S = position(2, { { 3, 20 }, { 2, 28 } }, { 4, 16 }, { 40, 36, 12, 1 })
	S.cap[1] = { 7 }
	play(S, { action = "play", card = 3 })
	check(#S.floor[1] == 3 and #S.cap[1] == 1, "three of a month stay on the floor")
	check(S.ppeok[1] == 1 and S.owner[1] == 1, "the ppeok is counted")
	play(S, { action = "play", card = 2 })
	check(#S.floor[1] == 0 and #S.cap[2] == 5, "the fourth card takes the ppeok and one junk")
	check(#S.cap[1] == 0, "the junk came from the opponent")

	-- Ttadak: two on the floor, the played card takes one and the flip is the fourth.
	S = position(2, { { 1, 20 }, { 24, 28 } }, { 3, 4, 16 }, { 40, 36, 2 })
	S.cap[2] = { 7 }
	play(S, { action = "play", card = 1 })
	check(#S.cap[1] == 5 and #S.cap[2] == 0, "ttadak takes four cards and one junk")

	-- Two different cards of the month on the floor: the player chooses.
	S = position(2, { { 3, 20 }, { 24, 28 } }, { 1, 2, 16 }, { 40, 36, 12 })
	play(S, { action = "play", card = 3 })
	check(S.phase == "pick" and S.turn == 1, "two different cards ask for a choice")
	check(play(S, { action = "pick", card = 16 }) == "NOT_A_CHOICE", "only the two cards can be chosen")
	play(S, { action = "pick", card = 1 })
	check(has(S.cap[1], 1) and has(S.cap[1], 3) and #S.floor[1] == 1, "the chosen card is taken, the other stays")

	-- The same choice for the flipped card.
	S = position(2, { { 20, 19 }, { 24, 28 } }, { 1, 2, 16 }, { 40, 36, 3 })
	play(S, { action = "play", card = 20 })
	check(S.phase == "pick", "a flipped card with two different matches asks for a choice")
	play(S, { action = "pick", card = 2 })
	check(has(S.cap[1], 2) and has(S.cap[1], 3) and S.turn == 2, "the flipped card takes the chosen one")

	-- A sweep: the floor is empty after the turn.
	S = position(2, { { 3, 20 }, { 24, 28 } }, { 4, 16 }, { 40, 36, 32, 15 })
	S.cap[2] = { 7 }
	play(S, { action = "play", card = 3 })
	check(floor_empty_for_test(S) and #S.cap[2] == 0, "a sweep steals one junk")
end

local function specials()
	-- A bomb: three of a month in the hand and the fourth on the floor.
	local S = position(2, { { 1, 2, 3, 20 }, { 24, 28 } }, { 4, 16 }, { 40, 36, 32, 12 })
	S.cap[2] = { 7 }
	check(play(S, { action = "play", card = 20, bomb = true }) == "NO_BOMB", "a bomb needs three of the month")
	play(S, { action = "play", card = 1, bomb = true })
	check(#S.hands[1] == 1 and #S.cap[1] == 5, "a bomb takes all four and a junk")
	check(S.shake[1] == 1 and S.pass[1] == 2, "a bomb doubles and gives two turns without a card")
	play(S, { action = "play", card = 24 })
	check(play(S, { action = "flip" }) == nil, "after a bomb the player may only flip")
	check(S.pass[1] == 1 and #S.hands[1] == 1, "flipping uses one of the two turns")

	-- Shaking: three of a month in the hand, none on the floor.
	S = position(2, { { 1, 2, 3, 20 }, { 24, 28 } }, { 16 }, { 40, 36, 32, 12 })
	check(play(S, { action = "play", card = 20, shake = true }) == "NO_SHAKE", "shaking needs three of the month")
	play(S, { action = "play", card = 1, shake = true })
	check(S.shake[1] == 1 and #S.floor[1] == 1, "shaking doubles and the card is laid down")

	-- A bonus card from the hand is taken and replaced; the turn goes on.
	S = position(2, { { 49, 20 }, { 24, 28 } }, { 16 }, { 40, 36, 32, 12 })
	S.bonus = 2
	play(S, { action = "play", card = 49 })
	check(has(S.cap[1], 49) and has(S.hands[1], 12) and S.turn == 1 and S.phase == "play", "a bonus card is replaced from the stock")

	-- A flipped bonus card is taken and another card is flipped.
	S = position(2, { { 3, 20 }, { 24, 28 } }, { 16 }, { 40, 36, 32, 50 })
	S.bonus = 2
	play(S, { action = "play", card = 3 })
	check(has(S.cap[1], 50) and #S.floor[8] == 1, "a flipped bonus card is taken and the next card flipped")

	-- A flipped bonus card stays with a ppeok.
	S = position(2, { { 3, 20 }, { 24, 28 } }, { 4, 16 }, { 40, 36, 1, 50 })
	S.bonus = 2
	play(S, { action = "play", card = 3 })
	check(#S.cap[1] == 0 and #S.attached[1] == 1, "a bonus card flipped before a ppeok stays with it")

	-- Three ppeoks win.
	S = position(2, { { 3, 20 }, { 24, 28 } }, { 4, 16 }, { 40, 36, 32, 1 })
	S.ppeok[1] = 2
	play(S, { action = "play", card = 3 })
	check(S.phase == "over" and S.result.winner == 1 and S.result.how == "ppeok3", "three ppeoks win the game")
end

local function endings()
	-- Seven points with two players: go or stop.
	local S = position(2, { { 9, 20 }, { 24, 28 } }, { 10, 16 }, { 44, 40, 36, 12, 32 })
	S.cap[1] = { 5, 13, 30, 1, 29 }
	S.cap[2] = { 41, 3, 4, 7, 8, 11, 15, 19 }
	check(G.score_of(S, 1) == 5, "godori before the turn")
	play(S, { action = "play", card = 9 })
	check(S.phase == "gostop" and S.turn == 1, "reaching seven points asks go or stop")
	local T = G.clone(S)
	play(T, { action = "stop" })
	check(T.phase == "over" and T.result.winner == 1 and T.result.total == 8, "stop wins the points")
	play(S, { action = "go" })
	check(S.go[1] == 1 and S.turn == 2 and S.phase == "play", "go continues the game")
	check(G.score_of(S, 1) == 8, "brights and godori are 8")

	-- Below seven nothing is asked with two players, but three points are enough with three.
	S = position(3, { { 9, 20 }, { 24, 28 }, { 23, 27 } }, { 10, 16 }, { 44, 40, 36, 12, 32 })
	S.cap[1] = { 1, 29 }
	play(S, { action = "play", card = 9 })
	check(S.phase == "gostop", "three points ask go or stop with three players")
	S = position(2, { { 9, 20 }, { 24, 28 } }, { 10, 16 }, { 44, 40, 36, 12, 32 })
	S.cap[1] = { 1, 29 }
	play(S, { action = "play", card = 9 })
	check(S.phase == "play" and S.turn == 2, "three points ask nothing with two players")

	-- Penalties: no brights against brights, few junk against junk, and a go that failed.
	S = position(2, { { 9, 20 }, { 24, 28 } }, { 10, 16 }, { 44, 40, 36, 32 })
	S.cap[1] = { 1, 29, 41, 3, 4, 7, 8, 11, 12, 15, 19, 23, 27, 31, 35 }
	S.cap[2] = { 43 }
	S.go[2] = 1
	play(S, { action = "play", card = 9 })
	play(S, { action = "stop" })
	local r = S.result
	check(r.score == 7 and r.flags[2].gwangbak == true and r.flags[2].pibak == true and r.flags[2].gobak == true, "gwangbak, pibak and gobak are found")
	check(r.pays[2] == 56 and r.total == 56, "each penalty doubles")

	-- With three players the one who said go pays for the other loser.
	S = position(3, { { 9, 20 }, { 24, 28 }, { 23, 27 } }, { 10, 16 }, { 44, 40, 36, 12, 32 })
	S.cap[1] = { 1, 29 }
	S.cap[2] = { 41, 3, 4, 7, 8, 11, 15 }
	S.cap[3] = { 45, 19, 31, 35, 39, 43, 47 }
	S.go[2] = 1
	play(S, { action = "play", card = 9 })
	play(S, { action = "stop" })
	check(S.result.pays[2] == 6 and S.result.pays[3] == 0, "the player who said go pays for both")

	-- Nobody stops before the stock is empty: nagari.
	S = position(2, { { 3 }, { 24 } }, { 16 }, { 36, 12 })
	play(S, { action = "play", card = 3 })
	play(S, { action = "play", card = 24 })
	check(S.phase == "over" and S.result.winner == 0, "an empty stock without a stop is a draw")

	-- On the last turn the limit ends the game without asking.
	S = position(2, { { 9 }, { 24 } }, { 10, 16 }, { 32 })
	S.cap[1] = { 5, 13, 30, 1, 29 }
	play(S, { action = "play", card = 9 })
	check(S.phase == "over" and S.result.winner == 1, "the last turn stops by itself")
end

local function games()
	-- Whole games: random moves and the rule-based player, two and three players. Nothing is lost and they all end.
	for round = 1, 60 do
		local n = if round % 2 == 0 then 2 else 3
		local S = G.new(n, { bonus = round % 4 })
		local steps = 0
		while S.phase ~= "over" and steps < 500 do
			steps += 1
			local list = G.legal(S)
			if #list == 0 then
				check(false, "a game without a legal move")
				break
			end
			local cmd: any = if round <= 30 then list[math.random(1, #list)] else ai.policy(S)
			if cmd == nil then
				check(false, "the rule-based player has no move")
				break
			end
			local code = G.apply(S, cmd)
			if code ~= nil then
				check(false, "a legal move was refused: " .. code)
				break
			end
			if not conserved(S) then
				check(false, "a card was lost or doubled")
				break
			end
		end
		check(S.phase == "over", "a game did not end")
		if S.phase == "over" and S.result.winner ~= 0 then
			check(S.result.total > 0, "a win pays something")
		end
	end
end

local function players()
	-- The rule-based player takes the card that finishes a set rather than a plain pair.
	local S = position(2, { { 31, 12 }, { 24, 28 } }, { 30, 11 }, { 44, 40, 36, 32 })
	S.cap[1] = { 5, 13 }
	local cmd: any = ai.policy(S)
	check(cmd ~= nil and cmd.card == 31, "the rule-based player goes for godori")
	-- It plays a bonus card first and a bomb when it can.
	S = position(2, { { 20, 49 }, { 24, 28 } }, { 16 }, { 44, 40, 36, 32 })
	cmd = ai.policy(S)
	check(cmd ~= nil and cmd.card == 49, "a bonus card is played first")
	S = position(2, { { 1, 2, 3, 20 }, { 24, 28 } }, { 4, 16 }, { 44, 40, 36, 32 })
	cmd = ai.policy(S)
	check(cmd ~= nil and cmd.bomb == true, "the rule-based player uses a bomb")
	-- The search stops when going on can only lose, and what it returns is always allowed.
	S = position(2, { { 9, 20 }, { 26, 38 } }, { 10, 16, 25, 37 }, { 44, 40, 36, 12, 32 })
	S.cap[1] = { 5, 13, 30, 1, 29 }
	S.cap[2] = { 2, 6, 14, 18, 22, 34, 41, 45 }
	play(S, { action = "play", card = 9 })
	cmd = ai.think(S, 1, 200)
	check(cmd ~= nil and cmd.action == "stop", "the search stops against a strong opponent")
	for _ = 1, 10 do
		local R = G.new(2, { bonus = 2 })
		local steps = 0
		while R.phase ~= "over" and steps < 200 do
			steps += 1
			local before = G.clone(R)
			local move: any = ai.think(R, R.turn, 30)
			check(move ~= nil, "the search found no move")
			if move == nil then
				break
			end
			local same = true
			for seat = 1, R.n do
				same = same and #R.hands[seat] == #before.hands[seat] and #R.cap[seat] == #before.cap[seat]
			end
			check(same and #R.stock == #before.stock, "thinking changed the game")
			if G.apply(R, move) ~= nil then
				check(false, "the search returned a move that is not allowed")
				break
			end
		end
	end
end

-- Plays games between players of different strength and reports the points seat 1 made: effort 0 plays at random,
-- 1 is the rule-based player, more is the search with that many games per move. The other seats all use `b`.
function selftest.match(a: number, b: number, games: number, players: number): { [string]: number }
	local wins, losses, draws, points = 0, 0, 0, 0
	local thinks = 0
	for _ = 1, games do
		local S = G.new(players, { bonus = 2 })
		local steps = 0
		while S.phase ~= "over" and steps < 500 do
			steps += 1
			local effort = if S.turn == 1 then a else b
			local cmd: any
			if effort <= 0 then
				local list = G.legal(S)
				cmd = list[math.random(1, #list)]
			else
				cmd = ai.think(S, S.turn, effort)
				if S.turn == 1 then
					thinks += 1
				end
			end
			if cmd == nil or G.apply(S, cmd) ~= nil then
				break
			end
		end
		local result = S.result
		if result == nil or result.winner == 0 then
			draws += 1
		elseif result.winner == 1 then
			wins += 1
			points += result.total
		else
			losses += 1
			points -= result.pays[1]
		end
	end
	return { wins = wins, losses = losses, draws = draws, points = points, thinks = thinks }
end

function selftest.run(): { string }
	table.clear(failures)
	scoring()
	turns()
	specials()
	endings()
	games()
	players()
	return table.clone(failures)
end

return selftest
