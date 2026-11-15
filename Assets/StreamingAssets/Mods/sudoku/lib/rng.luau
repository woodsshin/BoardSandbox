--!strict
-- A small random generator of its own (xorshift32). The puzzle of a seed must not depend on the state of
-- math.random or on the version of Luau, so the game does not use them to build a puzzle.

local Rng = {}

export type Rng = { state: number }

-- The next number, 0 to 2^32 - 1.
function Rng.next(rng: Rng): number
	local x = rng.state
	x = bit32.bxor(x, bit32.lshift(x, 13))
	x = bit32.bxor(x, bit32.rshift(x, 17))
	x = bit32.bxor(x, bit32.lshift(x, 5))
	rng.state = x
	return x
end

-- A whole number from 1 to n.
function Rng.below(rng: Rng, n: number): number
	return Rng.next(rng) % n + 1
end

function Rng.shuffle<T>(rng: Rng, list: { T })
	for i = #list, 2, -1 do
		local j = Rng.below(rng, i)
		list[i], list[j] = list[j], list[i]
	end
end

function Rng.new(seed: number): Rng
	local state = bit32.bxor(bit32.band(math.floor(seed), 0xFFFFFFFF), 0x9E3779B9)
	if state == 0 then
		state = 0x9E3779B9
	end
	local rng: Rng = { state = state }
	-- Seeds that differ in one bit start out alike; a few rounds pull them apart.
	for _ = 1, 24 do
		Rng.next(rng)
	end
	return rng
end

return Rng
