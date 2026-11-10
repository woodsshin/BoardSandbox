using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Luau;
using BoardSandbox.Core.Mods;

namespace BoardSandbox.Core.Sim
{
    public sealed class SimulationOptions
    {
        public int Games = 1000;
        public int Seed = 1;
        public int MaxMovesPerGame = 10000;

        // Merged over the mod's default options.
        public JsonValue GameOptions;

        public VmOptions Vm = new VmOptions { MemoryLimitMb = 256, CallTimeoutMs = 60000 };

        // Games are run in slices of about this long so that cancellation and progress stay responsive.
        public int TargetSliceMilliseconds = 150;
    }

    public sealed class SimulationProgress
    {
        public SimulationProgress(int gamesDone, int gamesTotal, double gamesPerSecond)
        {
            GamesDone = gamesDone;
            GamesTotal = gamesTotal;
            GamesPerSecond = gamesPerSecond;
        }

        public int GamesDone { get; }
        public int GamesTotal { get; }
        public double GamesPerSecond { get; }
    }

    public sealed class SimulationResult
    {
        public int Games { get; internal set; }
        public int Draws { get; internal set; }

        // Games that ended without a result: no legal command, a stall, or the move limit.
        public int Aborted { get; internal set; }

        // Games stopped because the random agent produced a command the rules refused.
        public int Rejected { get; internal set; }

        public long Moves { get; internal set; }
        public SortedDictionary<int, int> Wins { get; } = new SortedDictionary<int, int>();
        public double ElapsedSeconds { get; internal set; }
        public bool Cancelled { get; internal set; }
        public string Error { get; internal set; }

        public bool Succeeded => string.IsNullOrEmpty(Error) && !Cancelled;
        public double GamesPerSecond => ElapsedSeconds > 0 ? Games / ElapsedSeconds : 0;
        public double AverageMoves => Games > 0 ? (double)Moves / Games : 0;

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Games.ToString(CultureInfo.InvariantCulture)).Append(" games in ").Append(ElapsedSeconds.ToString("0.00", CultureInfo.InvariantCulture)).Append(" s (")
                .Append(GamesPerSecond.ToString("0", CultureInfo.InvariantCulture)).Append(" games/s)");
            foreach (KeyValuePair<int, int> win in Wins)
                sb.Append(", seat ").Append(win.Key).Append(" won ").Append(win.Value);
            sb.Append(", draws ").Append(Draws).Append(", aborted ").Append(Aborted).Append(", rejected ").Append(Rejected);
            sb.Append(", avg moves ").Append(AverageMoves.ToString("0.0", CultureInfo.InvariantCulture));
            if (Cancelled)
                sb.Append(" [cancelled]");
            if (!string.IsNullOrEmpty(Error))
                sb.Append(" [error: ").Append(Error).Append(']');
            return sb.ToString();
        }
    }

    // Plays random games on a private VM, so a simulation never touches a running game.
    public static class SimulationRunner
    {
        public static SimulationResult Run(ModPackage mod, SimulationOptions options, Action<SimulationProgress> progress = null, CancellationToken cancel = default)
        {
            if (mod == null)
                throw new ArgumentNullException(nameof(mod));
            options = options ?? new SimulationOptions();
            int total = Math.Max(1, options.Games);

            JsonValue gameOptions = mod.Manifest.DefaultOptions.DeepClone();
            if (options.GameOptions != null && options.GameOptions.IsObject)
            {
                foreach (KeyValuePair<string, JsonValue> pair in options.GameOptions.Members)
                    gameOptions.Set(pair.Key, pair.Value);
            }

            SimulationResult result = new SimulationResult();
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                using (LuauVm vm = mod.CreateVm(options.Vm))
                {
                    int done = 0;
                    int slice = 20;
                    uint timeout = options.Vm.CallTimeoutMs;
                    while (done < total)
                    {
                        if (cancel.IsCancellationRequested)
                        {
                            result.Cancelled = true;
                            break;
                        }
                        int n = Math.Min(slice, total - done);
                        JsonValue request = JsonValue.NewObject()
                            .Set("games", n)
                            .Set("seed", options.Seed + done)
                            .Set("max_moves", options.MaxMovesPerGame)
                            .Set("options", gameOptions);
                        long sliceStart = clock.ElapsedMilliseconds;
                        VmCallResult call = vm.Call("simulate", request.ToJson(), timeout);
                        if (!call.IsOk)
                        {
                            result.Error = call.Status + ": " + call.Text;
                            break;
                        }
                        JsonValue response = JsonValue.Parse(call.Text);
                        if (!response["ok"].AsBool())
                        {
                            result.Error = response["error"]["code"].AsString("ERROR") + ": " + response["error"]["message"].AsString();
                            break;
                        }
                        Accumulate(result, response["stats"]);
                        done += n;

                        long sliceMs = Math.Max(1, clock.ElapsedMilliseconds - sliceStart);
                        double scaled = n * (double)Math.Max(10, options.TargetSliceMilliseconds) / sliceMs;
                        slice = (int)Math.Max(1, Math.Min(Math.Min(scaled, n * 4.0), 100000));
                        progress?.Invoke(new SimulationProgress(done, total, done / Math.Max(0.001, clock.Elapsed.TotalSeconds)));
                    }
                }
            }
            catch (LuauException e)
            {
                result.Error = e.Message;
            }
            catch (JsonParseException e)
            {
                result.Error = "the simulation returned malformed JSON: " + e.Message;
            }
            result.ElapsedSeconds = clock.Elapsed.TotalSeconds;
            return result;
        }

        public static Task<SimulationResult> RunAsync(ModPackage mod, SimulationOptions options, Action<SimulationProgress> progress = null,
            CancellationToken cancel = default)
        {
            return Task.Run(() => Run(mod, options, progress, cancel), CancellationToken.None);
        }

        private static void Accumulate(SimulationResult result, JsonValue stats)
        {
            result.Games += stats["games"].AsInt();
            result.Draws += stats["draws"].AsInt();
            result.Aborted += stats["aborted"].AsInt();
            result.Rejected += stats["rejected"].AsInt();
            result.Moves += stats["moves"].AsLong();
            foreach (KeyValuePair<string, JsonValue> win in stats["wins"].Members)
            {
                if (!int.TryParse(win.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seat))
                    continue;
                result.Wins.TryGetValue(seat, out int existing);
                result.Wins[seat] = existing + win.Value.AsInt();
            }
        }
    }
}
