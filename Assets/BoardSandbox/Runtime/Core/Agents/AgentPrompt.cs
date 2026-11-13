using System.Collections.Generic;
using System.Text;
using BoardSandbox.Core.Json;

namespace BoardSandbox.Core.Agents
{
    // The words an agent gets: what a game of this engine is made of, the game folder as it is and what the player
    // wants. The guide is the contract of the engine in the README, shortened for a reader that writes code.
    public static class AgentPrompt
    {
        private const int MaxEarlierRequests = 6;
        private const int MaxEarlierLength = 400;

        public const string Guide = @"# Writing a game for Board Sandbox

You are writing a board game for Board Sandbox, a tabletop engine. A game is one folder with a `config.json`
and Luau scripts. The engine runs the rules in a sandboxed Luau VM and draws the result in 3D. You only edit
`config.json` and `.luau` files in the game folder. Do not create other kinds of files, do not touch anything
outside the folder and do not run shell commands. After you finish, the app loads the game, starts it and
plays random moves; if that fails you get the error back.

## config.json

```json
{
  ""id"": ""my-game"",
  ""name"": ""My Game"",
  ""version"": ""1.0.0"",
  ""description"": ""One or two sentences."",
  ""api"": 1,
  ""entry"": ""logic.luau"",
  ""players"": { ""min"": 2, ""max"": 2 },
  ""seats"": [ { ""name"": ""Black"", ""color"": ""#161616"" }, { ""name"": ""White"", ""color"": ""#f3efe6"" } ],
  ""options"": { ""size"": 8 },
  ""client"": {
    ""board"": { ""style"": ""checker"", ""columns"": 8, ""rows"": 8 },
    ""click"": { ""action"": ""place"" },
    ""pieces"": { ""token"": { ""shape"": ""disc"" } }
  }
}
```

- `id`: never change the id you were given.
- `name`, `description`: shown in the game list. Give the game a fitting name and explain its rules briefly.
- `api` is always 1. `entry` is the script that returns the rules table.
- `players`: 1 <= min <= max <= 16. `seats` names and colors the players in seat order.
- `options`: defaults handed to `rules.new_game`; the player can edit numbers, strings and booleans.
- `client`: how the game looks and takes input (see below).

The file must be plain JSON (no comments). Seat colors are `#rrggbb`.

## The rules script

The entry script returns a table of functions. State lives in local variables of the script.

| function | required | what it does |
|---|---|---|
| `rules.new_game(options)` | yes | Sets up a new game. The view starts empty. Must end by calling `bse.turn(seat)`. |
| `rules.command(seat, cmd)` | yes | Validates and applies one input. Returns `nil` on success or `bse.reject(code, message)` to refuse. |
| `rules.random_command(seat)` | strongly recommended | Returns one legal command for the seat, or `nil` when there is none. Powers the computer player and the automatic check. |
| `rules.legal_commands(seat)` | optional | Returns the list of all legal commands. |
| `rules.snapshot()` / `rules.restore(data)` | strongly recommended | Save and load the rule state as a JSON-like value. Both together enable undo and redo. |
| `rules.query(name, args)` | optional | Answers console queries. |

Rules of the contract:

- Seats are numbers starting at 1. Board coordinates are cell numbers starting at 1; column 1 is on the left
  and row 1 is the far side of the board.
- Reject before changing anything. Returning `bse.reject` after a `bse.view` call or `bse.emit` is an error.
- After an accepted command either call `bse.turn(nextSeat)` or `bse.over({ winner = seat, reason = ""..."" })`
  (`winner` nil or 0 means a draw). Never leave the game with nobody on turn.
- Every command `rules.random_command` returns must be accepted by `rules.command`.
- Decide wins from your own state, not from `bse.view`: in headless simulations `bse.view.*` creates nothing.
- The engine saves and restores the pieces, the turn and the result itself. `snapshot` holds only your own
  state. `restore` must re-emit custom events such as `board` and `last_move`.
- Use `math.random` for randomness; the engine seeds it.
- Sandbox: no `io`, `os`, `loadstring`, `dofile`, `getfenv`, `setfenv`. A call may take 250 ms and the VM
  64 MB. `require(""./other"")` loads `other.luau` relative to the calling file.
- Luau is typed Lua 5.1 with extras (`+=`, `continue`, string interpolation with backticks, `if a then b else c`
  expressions, `table.clear`, `table.clone`, `math.clamp`, `//`). Start scripts with `--!strict` only if you
  annotate types correctly; otherwise leave it out.

## The bse API

| call | meaning |
|---|---|
| `bse.view.spawn(kind, x, y, { owner = seat, props = {...} })` | Creates a piece on cell x,y and returns its id. The last argument is optional. |
| `bse.view.move(id, x, y)` | Moves a piece to another cell. |
| `bse.view.set(id, changes)` | Merges `changes` into the props of a piece. |
| `bse.view.remove(id)`, `bse.view.clear()` | Removes one piece, or all. |
| `bse.view.get(id)` | A copy `{ id, kind, x, y, owner, props }` or nil. |
| `bse.turn(seat)` | Says whose turn it is. |
| `bse.over({ winner = seat, reason = ""text"", cells = { {x, y}, ... } })` | Ends the game; `cells` highlights a winning line. |
| `bse.emit({ t = ""name"", ... })` | A custom event. The client understands `board`, `last_move`, `stage` and `fx`. |
| `bse.status(text, level)` | A message for the players; level is `info`, `warning` or `error`. |
| `bse.reject(code, message, details)` | The value `rules.command` returns to refuse a command. |
| `bse.deepcopy(value)`, `bse.is_headless()` | Deep copy; whether this is a simulation without display. |
| `bse.log(...)`, `bse.warn(...)`, `bse.err(...)` | Console logging. |
| `bse.json.encode(value)`, `bse.json.decode(text)` | JSON. |

Events the client understands:

- `bse.emit({ t = ""board"", columns = n, rows = m })` sets the board size. Emit it in `new_game` and `restore`.
- `bse.emit({ t = ""last_move"", x = x, y = y, seat = seat })` marks the last move.
- `bse.emit({ t = ""stage"", ... })` changes the scene (same keys as `client.stage` below).
- `bse.emit({ t = ""fx"", kind = ""wait"", seconds = 0.3 })` pauses the display;
  `{ t = ""fx"", kind = ""rotate"", ids = {...}, axis = {0,1,0}, pivot = {0,0,0}, degrees = 90, seconds = 0.2 }`
  rotates objects for show (then `bse.view.set` their final `pos` and `rot`).

## client hints in config.json

| key | meaning |
|---|---|
| `client.board.style` | `lines` (pieces sit on line crossings, like Go) or `checker` (pieces sit in squares). |
| `client.board.columns`, `rows`, `colors.slab/line/light/dark`, `stars` | Board size and look. |
| `client.click.action` | A click on a cell submits `{ action = <this>, x = column, y = row }`. Without it the game takes no clicks. |
| `client.click.target = ""object""` | Clicks hit pieces instead: `{ action, id, point, normal, button, shift }`. |
| `client.click.preview` | A piece kind shown as a ghost under the cursor. |
| `client.drag.action` | Dragging a piece submits `{ action, id, point, normal, drag }`. |
| `client.keys` | An object mapping key names (`""u""`, `""shift+u""`, `""space""`) to command objects. |
| `client.pieces.<kind>` | Default props for every piece of that kind. |
| `client.stage` | `background`, `table`, `board`, `light`, `camera` (`yaw`, `pitch`, `fov`, `distance`), `markers`. `camera.pitch = 89` looks straight down. |

Piece props (in `bse.view.spawn`/`set` or `client.pieces.<kind>`):

- `shape`: `stone`, `disc`, `cube`, `box`, `sphere`, `cylinder`, `cone`.
- `color`: `#rgb`, `#rrggbb`, `#rrggbbaa` or a name (`black white red green blue yellow orange purple brown
  gray cyan pink`). Defaults to the color of the owner seat.
- `size` (number or `{x, y, z}`), `scale`, `alpha`, `gloss`, `visible`, `pick`, `shadow`.
- `pos = {x, y, z}` places a piece freely in space instead of on a cell (x right, y up, z toward the viewer,
  one unit is one cell); `rot` is three angles in degrees; `tween` is the seconds a `pos` change glides.
- There are no text labels, images or 3D models: tell pieces apart by shape, color and size.

## Good habits

- A typical game: keep a grid in a local table, validate in `rules.command`, spawn/move/remove pieces to
  mirror the state, emit `last_move`, then `bse.turn` or `bse.over`.
- For selecting then moving a piece, use one click action and remember the selection in the rule state;
  highlight with `bse.view.set(id, { color = ... })` or marker pieces with `pick = false`.
- Keep `random_command` cheap and always legal. Keep `snapshot` small.
- Explain the rules of the game in `description` in config.json.
";

        // The reply format for agents that cannot write files themselves.
        public const string ReplyFormat = @"
## How to answer

You cannot write files yourself. Answer with the complete new content of every file you add or change, each in
a block of exactly this form, and nothing else around the content:

=== FILE: logic.luau ===
<the whole file>
=== END FILE ===

Always send whole files, never fragments or diffs. Leave out files that stay as they are. Outside the blocks,
say in two or three sentences what you did, in the language of the request. If the request is only a question,
answer it without any file block.
";

        private const string CliClosing = @"
When you are done, say in two or three sentences what you changed, in the language of the request.";

        public const string StarterScript = @"-- A starting point: two players take turns putting tokens on an empty board.
local rules = {}

local columns = 8
local rows = 8
local cells = {}
local current = 1
local placed = 0

local function index(x, y)
	return (y - 1) * columns + x
end

local function announce()
	bse.emit({ t = ""board"", columns = columns, rows = rows })
end

function rules.new_game(options)
	columns = math.clamp(math.floor(tonumber(options.columns) or 8), 2, 24)
	rows = math.clamp(math.floor(tonumber(options.rows) or 8), 2, 24)
	cells = {}
	current = 1
	placed = 0
	announce()
	bse.turn(current)
end

function rules.command(seat, cmd)
	if cmd.action ~= ""place"" then
		return bse.reject(""UNKNOWN_ACTION"", ""unknown action"")
	end
	if seat ~= current then
		return bse.reject(""NOT_YOUR_TURN"", ""it is not your turn"")
	end
	local x, y = cmd.x, cmd.y
	if type(x) ~= ""number"" or type(y) ~= ""number"" or x % 1 ~= 0 or y % 1 ~= 0 or x < 1 or x > columns or y < 1 or y > rows then
		return bse.reject(""OUT_OF_BOUNDS"", ""that is not a cell of the board"")
	end
	if cells[index(x, y)] then
		return bse.reject(""OCCUPIED"", ""that cell is taken"")
	end

	cells[index(x, y)] = seat
	placed += 1
	bse.view.spawn(""token"", x, y, { owner = seat })
	bse.emit({ t = ""last_move"", x = x, y = y, seat = seat })
	if placed == columns * rows then
		bse.over({ reason = ""the board is full"" })
	else
		current = 3 - seat
		bse.turn(current)
	end
	return nil
end

function rules.random_command(seat)
	local free = {}
	for y = 1, rows do
		for x = 1, columns do
			if not cells[index(x, y)] then
				table.insert(free, { action = ""place"", x = x, y = y })
			end
		end
	end
	if #free == 0 then
		return nil
	end
	return free[math.random(#free)]
end

function rules.snapshot()
	local list = {}
	for i = 1, columns * rows do
		list[i] = cells[i] or 0
	end
	return { columns = columns, rows = rows, cells = list, current = current, placed = placed }
end

function rules.restore(data)
	columns = data.columns
	rows = data.rows
	cells = {}
	for i, seat in data.cells do
		if seat ~= 0 then
			cells[i] = seat
		end
	end
	current = data.current
	placed = data.placed
	announce()
end

return rules
";

        public static string StarterManifest(string id, string name)
        {
            JsonValue seats = JsonValue.NewArray()
                .Add(JsonValue.NewObject().Set("name", "Red").Set("color", "#d8443c"))
                .Add(JsonValue.NewObject().Set("name", "Blue").Set("color", "#3c78d8"));
            JsonValue client = JsonValue.NewObject()
                .Set("board", JsonValue.NewObject().Set("style", "checker"))
                .Set("click", JsonValue.NewObject().Set("action", "place").Set("preview", "token"))
                .Set("pieces", JsonValue.NewObject().Set("token", JsonValue.NewObject().Set("shape", "disc")));
            return JsonValue.NewObject()
                .Set("id", id)
                .Set("name", string.IsNullOrWhiteSpace(name) ? "New Game" : name.Trim())
                .Set("version", "0.1.0")
                .Set("description", "A new game made with the AI console.")
                .Set("api", 1)
                .Set("entry", "logic.luau")
                .Set("players", JsonValue.NewObject().Set("min", 2).Set("max", 2))
                .Set("seats", seats)
                .Set("options", JsonValue.NewObject().Set("columns", 8).Set("rows", 8))
                .Set("client", client)
                .ToJson(true) + "\n";
        }

        // The request for an agent that works in the folder itself.
        public static AgentRequest ForCli(ModWorkspace workspace, string request, IReadOnlyList<string> earlier, string failure)
        {
            StringBuilder prompt = new StringBuilder(Guide);
            prompt.Append("\n# Your task\n\n");
            prompt.Append("The game is in the current folder and its id is `").Append(workspace.Id).Append("`. Read `config.json` and the `.luau` files first, ");
            prompt.Append("then change them to do what is asked.\n");
            AppendTask(prompt, request, earlier, failure);
            prompt.Append(CliClosing).Append('\n');
            return new AgentRequest(workspace.Directory, prompt.ToString());
        }

        // The request for an agent that answers with text: it gets the files and sends back the ones it changes.
        public static AgentRequest ForApi(ModWorkspace workspace, IDictionary<string, string> files, string request, IReadOnlyList<string> earlier, string failure)
        {
            StringBuilder prompt = new StringBuilder();
            prompt.Append("The id of the game is `").Append(workspace.Id).Append("`. These are its files now:\n\n");
            foreach (KeyValuePair<string, string> file in files)
                prompt.Append(FileBlocks.Format(file.Key, file.Value)).Append('\n');
            AppendTask(prompt, request, earlier, failure);
            return new AgentRequest(workspace.Directory, prompt.ToString(), Guide + ReplyFormat);
        }

        private static void AppendTask(StringBuilder prompt, string request, IReadOnlyList<string> earlier, string failure)
        {
            if (earlier != null && earlier.Count > 0)
            {
                prompt.Append("\nEarlier requests in this session, already carried out, oldest first:\n");
                for (int i = System.Math.Max(0, earlier.Count - MaxEarlierRequests); i < earlier.Count; i++)
                {
                    string text = earlier[i].Replace('\n', ' ');
                    prompt.Append("- ").Append(text.Length > MaxEarlierLength ? text.Substring(0, MaxEarlierLength) + " …" : text).Append('\n');
                }
            }
            if (!string.IsNullOrEmpty(failure))
            {
                prompt.Append("\nThe app tried the game after your last change and it does not work:\n\n").Append(failure.Trim()).Append("\n\n");
                prompt.Append("Fix the files so that the game loads and can be played. The request you were working on was:\n\n");
            }
            else
            {
                prompt.Append("\nThe request:\n\n");
            }
            prompt.Append(request.Trim()).Append('\n');
        }
    }
}
