using System;
using System.Collections.Generic;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Luau;
using BoardSandbox.Core.Mods;

namespace BoardSandbox.Core.Game
{
    public sealed class SessionOptions
    {
        public VmOptions Vm = new VmOptions();

        // Snapshots kept for undo; 0 turns history off.
        public int HistoryLimit = 4096;

        // After a script fault, rebuild the VM and restore the last good snapshot.
        public bool AutoRecover = true;

        public int LogLimit = 1000;
    }

    public sealed class SessionCapabilities
    {
        public bool Snapshot { get; internal set; }
        public bool Legal { get; internal set; }
        public bool Random { get; internal set; }
        public bool Query { get; internal set; }
    }

    // Owns one rules VM and the table mirror built from its events. Every change to the game goes through Submit.
    public sealed class GameSession : IDisposable
    {
        private sealed class HistoryEntry
        {
            public string Snapshot;
            public InputPacket Packet;
        }

        private readonly SessionOptions _options;
        private readonly List<ICommandInterceptor> _interceptors = new List<ICommandInterceptor>();
        private readonly List<HistoryEntry> _history = new List<HistoryEntry>();
        private readonly Queue<LogLine> _logs = new Queue<LogLine>();
        private LuauVm _vm;
        private JsonValue _gameOptions = JsonValue.NewObject();
        private int _cursor = -1;
        private long _sequence;
        private bool _historyBroken;
        private bool _disposed;

        public GameSession(ModPackage mod, SessionOptions options = null)
        {
            Mod = mod ?? throw new ArgumentNullException(nameof(mod));
            _options = options ?? new SessionOptions();
            _vm = Mod.CreateVm(_options.Vm);
            Capabilities = ReadCapabilities(_vm);
        }

        public ModPackage Mod { get; }
        public ViewModel View { get; } = new ViewModel();
        public SessionCapabilities Capabilities { get; private set; }
        public bool IsStarted { get; private set; }
        public JsonValue GameOptions => _gameOptions;
        public bool DevMode => _options.Vm.DevMode;

        public event Action<ViewEvent> EventApplied;
        public event Action<InputPacket, SubmitResult> CommandProcessed;
        public event Action<LogLine> ScriptLog;
        public event Action<Exception> HandlerFailed;

        public IReadOnlyCollection<LogLine> RecentLogs => _logs;
        public IReadOnlyList<ICommandInterceptor> Interceptors => _interceptors;
        public VmStats VmStats => _vm.GetStats();

        public bool HistoryEnabled => Capabilities.Snapshot && !_historyBroken && _options.HistoryLimit > 0;
        public int HistoryCount => _history.Count;
        public int HistoryIndex => _cursor;
        public bool CanUndo => IsStarted && HistoryEnabled && _cursor > 0;
        public bool CanRedo => IsStarted && HistoryEnabled && _cursor >= 0 && _cursor < _history.Count - 1;

        public void AddInterceptor(ICommandInterceptor interceptor)
        {
            if (interceptor == null)
                throw new ArgumentNullException(nameof(interceptor));
            _interceptors.Add(interceptor);
        }

        public bool RemoveInterceptor(ICommandInterceptor interceptor)
        {
            return _interceptors.Remove(interceptor);
        }

        // The packet that produced the state at this history index; null for the start of the game or a loaded snapshot.
        public InputPacket GetHistoryPacket(int index)
        {
            return index >= 0 && index < _history.Count ? _history[index].Packet : null;
        }

        public IReadOnlyList<ViewEvent> NewGame(JsonValue gameOptions = null)
        {
            ThrowIfDisposed();
            JsonValue merged = MergeOptions(Mod.Manifest.DefaultOptions, gameOptions);
            LuauVm fresh = Mod.CreateVm(_options.Vm);
            JsonValue response;
            try
            {
                response = CallChecked(fresh, "new_game", JsonValue.NewObject().Set("options", merged));
            }
            catch
            {
                Harvest(fresh);
                fresh.Dispose();
                throw;
            }
            SwapVm(fresh);
            Capabilities = ReadCapabilities(_vm);
            _historyBroken = false;
            _history.Clear();
            _cursor = -1;
            _gameOptions = merged;

            List<ViewEvent> events = ViewEvent.ParseList(response["events"]);
            View.Clear();
            ApplyEvents(events);
            IsStarted = true;
            PushHistory(null, null);
            Harvest(_vm);
            return events;
        }

        public SubmitResult Submit(InputPacket packet)
        {
            ThrowIfDisposed();
            if (packet == null)
                throw new ArgumentNullException(nameof(packet));
            packet.Sequence = ++_sequence;
            SubmitResult result = SubmitCore(packet);
            foreach (ICommandInterceptor interceptor in _interceptors.ToArray())
            {
                try
                {
                    interceptor.AfterSubmit(this, packet, result);
                }
                catch (Exception e)
                {
                    HandlerFailed?.Invoke(e);
                }
            }
            RaiseCommandProcessed(packet, result);
            return result;
        }

        private SubmitResult SubmitCore(InputPacket packet)
        {
            if (!IsStarted)
                return SubmitResult.Invalid("NOT_STARTED", "no game is running");
            if (packet.Command == null || !packet.Command.IsObject)
                return SubmitResult.Invalid("BAD_COMMAND", "the command must be a JSON object");
            if (packet.Seat < 1 || packet.Seat > Mod.Manifest.MaxPlayers)
                return SubmitResult.Invalid("BAD_SEAT", "seat " + packet.Seat + " does not exist in this game");

            foreach (ICommandInterceptor interceptor in _interceptors.ToArray())
            {
                SubmitResult veto = null;
                try
                {
                    veto = interceptor.BeforeSubmit(this, packet);
                }
                catch (Exception e)
                {
                    HandlerFailed?.Invoke(e);
                }
                if (veto != null)
                    return veto;
            }

            JsonValue request = JsonValue.NewObject().Set("seat", packet.Seat).Set("cmd", packet.Command);
            VmCallResult call = _vm.Call("command", request.ToJson());
            if (!call.IsOk)
                return FailAndRecover(NativeCode(call.Status), call.Text, string.Empty);

            JsonValue response;
            try
            {
                response = JsonValue.Parse(call.Text);
            }
            catch (JsonParseException e)
            {
                return FailAndRecover("BAD_RESPONSE", e.Message, string.Empty);
            }

            if (response["ok"].AsBool())
            {
                List<ViewEvent> events;
                try
                {
                    events = ViewEvent.ParseList(response["events"]);
                }
                catch (FormatException e)
                {
                    return FailAndRecover("BAD_EVENT", e.Message, string.Empty);
                }
                ApplyEvents(events);
                PushHistory(packet, null);
                Harvest(_vm);
                return SubmitResult.Accepted(events);
            }

            JsonValue error = response["error"];
            string code = error["code"].AsString("ERROR");
            string message = error["message"].AsString(code);
            Harvest(_vm);
            if (response["fault"].AsBool())
                return FailAndRecover(code, message, error["traceback"].AsString());
            return SubmitResult.Rejected(code, message, error["details"]);
        }

        private SubmitResult FailAndRecover(string code, string message, string traceback)
        {
            Harvest(_vm);
            bool recovered = _options.AutoRecover && TryRecover();
            if (!recovered && !_options.AutoRecover)
                IsStarted = false;
            return SubmitResult.Faulted(code, message, traceback, recovered);
        }

        private bool TryRecover()
        {
            return RecoverFrom(_cursor >= 0 && _cursor < _history.Count ? _history[_cursor].Snapshot : null);
        }

        // Replaces the VM with a fresh one holding the given state; the session stops when that is not possible.
        private bool RecoverFrom(string snapshot)
        {
            if (snapshot == null)
            {
                IsStarted = false;
                return false;
            }
            try
            {
                LuauVm fresh = Mod.CreateVm(_options.Vm);
                JsonValue response;
                try
                {
                    response = CallChecked(fresh, "restore", JsonValue.NewObject().Set("snapshot", snapshot));
                }
                catch
                {
                    fresh.Dispose();
                    throw;
                }
                SwapVm(fresh);
                List<ViewEvent> events = ViewEvent.ParseList(response["events"]);
                View.Clear();
                ApplyEvents(events);
                return true;
            }
            catch (Exception e) when (e is GameSessionException || e is LuauException || e is ModException || e is FormatException)
            {
                IsStarted = false;
                return false;
            }
        }

        public bool Undo(int steps = 1)
        {
            return JumpTo(_cursor - Math.Max(1, steps));
        }

        public bool Redo(int steps = 1)
        {
            return JumpTo(_cursor + Math.Max(1, steps));
        }

        public bool JumpTo(int index)
        {
            ThrowIfDisposed();
            if (!IsStarted || !HistoryEnabled || index < 0 || index >= _history.Count)
                return false;
            if (index != _cursor)
            {
                RestoreCore(_history[index].Snapshot);
                _cursor = index;
            }
            return true;
        }

        public string CaptureSnapshot()
        {
            RequireStarted();
            if (!Capabilities.Snapshot)
                throw new GameSessionException("NOT_SUPPORTED", "the mod does not implement snapshot and restore");
            return CallVm("snapshot", null)["snapshot"].AsString();
        }

        // Restores a snapshot as a new point in the history.
        public void LoadSnapshot(string snapshot)
        {
            ThrowIfDisposed();
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            if (!Capabilities.Snapshot)
                throw new GameSessionException("NOT_SUPPORTED", "the mod does not implement snapshot and restore");
            RestoreCore(snapshot);
            IsStarted = true;
            PushHistory(null, snapshot);
        }

        // A refused snapshot leaves the game as it was: a failed restore can leave the running VM half changed,
        // so the VM is rebuilt from the last good state before the error is passed on.
        private void RestoreCore(string snapshot)
        {
            JsonValue request = JsonValue.NewObject().Set("snapshot", snapshot);
            string lastGood = LastGoodSnapshot();
            JsonValue response;
            try
            {
                response = CallChecked(_vm, "restore", request);
            }
            catch (GameSessionException)
            {
                Harvest(_vm);
                LuauVm fresh = null;
                try
                {
                    fresh = Mod.CreateVm(_options.Vm);
                    response = CallChecked(fresh, "restore", request);
                }
                catch (Exception e) when (e is GameSessionException || e is LuauException || e is ModException)
                {
                    Harvest(fresh);
                    fresh?.Dispose();
                    RecoverFrom(lastGood);
                    throw;
                }
                SwapVm(fresh);
            }
            List<ViewEvent> events = ViewEvent.ParseList(response["events"]);
            View.Clear();
            ApplyEvents(events);
            Harvest(_vm);
        }

        private string LastGoodSnapshot()
        {
            if (!IsStarted)
                return null;
            if (HistoryEnabled && _cursor >= 0 && _cursor < _history.Count)
                return _history[_cursor].Snapshot;
            try
            {
                return CallChecked(_vm, "snapshot", null)["snapshot"].AsString();
            }
            catch (GameSessionException)
            {
                return null;
            }
        }

        public JsonValue Query(string name, JsonValue args = null)
        {
            RequireStarted();
            JsonValue request = JsonValue.NewObject().Set("name", name);
            if (args != null)
                request.Set("args", args);
            return CallVm("query", request)["value"];
        }

        public IReadOnlyList<JsonValue> LegalCommands(int seat)
        {
            RequireStarted();
            return CallVm("legal", JsonValue.NewObject().Set("seat", seat))["commands"].Items;
        }

        // A command chosen by the mod's random_command or from its legal_commands; Null when the seat has none.
        public JsonValue RandomCommand(int seat, int? seed = null)
        {
            RequireStarted();
            JsonValue request = JsonValue.NewObject().Set("seat", seat);
            if (seed.HasValue)
                request.Set("seed", seed.Value);
            return CallVm("random", request)["command"];
        }

        public IReadOnlyList<string> Evaluate(string code)
        {
            RequireStarted();
            if (!DevMode)
                throw new GameSessionException("DEV_ONLY", "eval needs a session created with VmOptions.DevMode");
            JsonValue values = CallVm("eval", JsonValue.NewObject().Set("code", code))["values"];
            List<string> texts = new List<string>(values.Count);
            foreach (JsonValue value in values.Items)
                texts.Add(value.AsString());
            return texts;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _vm.Dispose();
        }

        private JsonValue CallVm(string method, JsonValue request)
        {
            ThrowIfDisposed();
            try
            {
                JsonValue response = CallChecked(_vm, method, request);
                Harvest(_vm);
                return response;
            }
            catch (GameSessionException e)
            {
                Harvest(_vm);
                if (IsNativeFailure(e.Code) && _options.AutoRecover)
                    TryRecover();
                throw;
            }
        }

        private static JsonValue CallChecked(LuauVm vm, string method, JsonValue request)
        {
            VmCallResult call = vm.Call(method, request == null ? null : request.ToJson());
            if (!call.IsOk)
                throw new GameSessionException(NativeCode(call.Status), call.Text);
            JsonValue response;
            try
            {
                response = JsonValue.Parse(call.Text);
            }
            catch (JsonParseException e)
            {
                throw new GameSessionException("BAD_RESPONSE", e.Message, e);
            }
            if (response["ok"].AsBool())
                return response;
            JsonValue error = response["error"];
            string code = error["code"].AsString("ERROR");
            throw new GameSessionException(code, error["message"].AsString(code)) { Traceback = error["traceback"].AsString() };
        }

        private static SessionCapabilities ReadCapabilities(LuauVm vm)
        {
            JsonValue response = CallChecked(vm, "capabilities", null);
            return new SessionCapabilities
            {
                Snapshot = response["snapshot"].AsBool(),
                Legal = response["legal"].AsBool(),
                Random = response["random"].AsBool(),
                Query = response["query"].AsBool()
            };
        }

        private static string NativeCode(VmStatus status)
        {
            switch (status)
            {
                case VmStatus.Timeout:
                    return "TIMEOUT";
                case VmStatus.Memory:
                    return "MEMORY";
                case VmStatus.Json:
                    return "JSON";
                case VmStatus.Faulted:
                    return "VM_FAULTED";
                case VmStatus.Runtime:
                    return "RUNTIME";
                default:
                    return "VM_ERROR";
            }
        }

        private static bool IsNativeFailure(string code)
        {
            return code == "TIMEOUT" || code == "MEMORY" || code == "JSON" || code == "VM_FAULTED" || code == "RUNTIME" || code == "VM_ERROR" ||
                   code == "BAD_RESPONSE";
        }

        private static JsonValue MergeOptions(JsonValue defaults, JsonValue overrides)
        {
            JsonValue merged = defaults.IsObject ? defaults.DeepClone() : JsonValue.NewObject();
            if (overrides != null && overrides.IsObject)
            {
                foreach (KeyValuePair<string, JsonValue> pair in overrides.Members)
                    merged.Set(pair.Key, pair.Value);
            }
            return merged;
        }

        private void PushHistory(InputPacket packet, string known)
        {
            if (!HistoryEnabled)
                return;
            string snapshot = known;
            if (snapshot == null)
            {
                try
                {
                    snapshot = CallChecked(_vm, "snapshot", null)["snapshot"].AsString();
                }
                catch (GameSessionException e)
                {
                    _historyBroken = true;
                    AddLog(new LogLine(2, "history disabled: snapshot failed: " + e.Message));
                    return;
                }
            }
            if (_cursor < _history.Count - 1)
                _history.RemoveRange(_cursor + 1, _history.Count - _cursor - 1);
            _history.Add(new HistoryEntry { Snapshot = snapshot, Packet = packet });
            int overflow = _history.Count - _options.HistoryLimit;
            if (overflow > 0)
                _history.RemoveRange(0, overflow);
            _cursor = _history.Count - 1;
        }

        private void ApplyEvents(List<ViewEvent> events)
        {
            foreach (ViewEvent e in events)
            {
                View.Apply(e);
                Action<ViewEvent> handlers = EventApplied;
                if (handlers == null)
                    continue;
                foreach (Delegate handler in handlers.GetInvocationList())
                {
                    try
                    {
                        ((Action<ViewEvent>)handler)(e);
                    }
                    catch (Exception ex)
                    {
                        HandlerFailed?.Invoke(ex);
                    }
                }
            }
        }

        private void RaiseCommandProcessed(InputPacket packet, SubmitResult result)
        {
            Action<InputPacket, SubmitResult> handlers = CommandProcessed;
            if (handlers == null)
                return;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<InputPacket, SubmitResult>)handler)(packet, result);
                }
                catch (Exception ex)
                {
                    HandlerFailed?.Invoke(ex);
                }
            }
        }

        private void SwapVm(LuauVm fresh)
        {
            LuauVm old = _vm;
            _vm = fresh;
            old.Dispose();
        }

        private void Harvest(LuauVm vm)
        {
            if (vm == null || vm.IsDisposed)
                return;
            IReadOnlyList<LogLine> lines = vm.TakeLogs();
            for (int i = 0; i < lines.Count; i++)
                AddLog(lines[i]);
        }

        private void AddLog(LogLine line)
        {
            _logs.Enqueue(line);
            while (_logs.Count > _options.LogLimit)
                _logs.Dequeue();
            Action<LogLine> handlers = ScriptLog;
            if (handlers == null)
                return;
            try
            {
                handlers(line);
            }
            catch (Exception ex)
            {
                HandlerFailed?.Invoke(ex);
            }
        }

        private void RequireStarted()
        {
            ThrowIfDisposed();
            if (!IsStarted)
                throw new GameSessionException("NOT_STARTED", "no game is running");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(GameSession));
        }
    }
}
