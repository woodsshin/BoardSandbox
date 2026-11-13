using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Mods;

namespace BoardSandbox.Core.Agents
{
    public enum StudioLineKind
    {
        // What the player typed.
        Prompt,

        // What the agent answered.
        Agent,

        // What the console itself has to say.
        Info,

        Error
    }

    public readonly struct StudioLine
    {
        public StudioLine(StudioLineKind kind, string text)
        {
            Kind = kind;
            Text = text ?? string.Empty;
        }

        public StudioLineKind Kind { get; }
        public string Text { get; }
    }

    // The console in which a player has an AI agent write a game: it keeps the conversation, hands each request to
    // the chosen agent, tries out what comes back and puts a game that works on the table. It draws nothing and is
    // driven by Submit and Update from one thread.
    public sealed class ModStudio : IDisposable
    {
        public const int MaxRepairs = 2;

        private const int MaxLines = 600;
        private const int MaxUndo = 20;
        private const int MaxEarlier = 12;
        private const double ReadinessSeconds = 5;

        private static readonly StringComparison PathComparison =
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private readonly GameHost _host;
        private readonly string _modsRoot;
        private readonly IAgentLauncher _launcher;
        private readonly List<StudioLine> _lines = new List<StudioLine>();
        private readonly List<AgentLine> _drained = new List<AgentLine>();
        private readonly List<string> _earlier = new List<string>();
        private readonly List<SortedDictionary<string, string>> _undo = new List<SortedDictionary<string, string>>();

        private AgentRun _run;
        private AgentProfile _runProfile;
        private SortedDictionary<string, string> _before;
        private string _request = string.Empty;
        private int _repairs;
        private bool _repairing;
        private string _readiness = string.Empty;
        private double _readinessAt = double.NegativeInfinity;
        private bool _disposed;

        // modsRoot is where new games are put; it has to be one of the folders the catalog of the host reads.
        public ModStudio(GameHost host, string modsRoot, AgentSettings settings, IAgentLauncher launcher = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            if (string.IsNullOrWhiteSpace(modsRoot))
                throw new ArgumentException("a folder for the games is required", nameof(modsRoot));
            _modsRoot = Path.GetFullPath(modsRoot);
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _launcher = launcher ?? new AgentLauncher();
        }

        public AgentSettings Settings { get; }

        // The game being worked on; null until the first request of a new game.
        public ModWorkspace Workspace { get; private set; }

        public IReadOnlyList<StudioLine> Lines => _lines;

        // Grows with every change of Lines.
        public int Version { get; private set; }

        // True while an agent is at work.
        public bool Busy => _run != null;

        // When the request in progress was sent, on the clock of the host.
        public double BusySince { get; private set; }

        // Why the chosen agent cannot be used right now; empty when it is ready. Looked up again every few seconds,
        // so that an agent installed in the meantime is noticed.
        public string Readiness
        {
            get
            {
                double now = _host.Now;
                if (now - _readinessAt > ReadinessSeconds)
                {
                    _readinessAt = now;
                    _readiness = _launcher.Readiness(Settings.Selected, Settings) ?? string.Empty;
                }
                return _readiness;
            }
        }

        // Raised after a changed game was put on the table.
        public event Action<ModPackage> Applied;

        // Starts over with a game that does not exist yet. Its folder is made with the first request.
        public void BeginNew()
        {
            Reset(null);
            Say("Describe the game you want. The agent writes its rules and the game appears behind this window.");
            Say("Each further request changes the game. Type /help for the commands.");
            DescribeAgent();
        }

        // Continues work on a game that is already in a folder.
        public bool BeginEdit(ModPackage mod)
        {
            if (mod == null)
                throw new ArgumentNullException(nameof(mod));
            if (mod.Directory == null)
            {
                Fail("'" + mod.Manifest.Name + "' is not in a folder and cannot be edited.");
                return false;
            }
            try
            {
                ModWorkspace workspace = ModWorkspace.Open(mod.Directory);
                Reset(workspace);
            }
            catch (Exception e) when (e is ModException || e is IOException || e is UnauthorizedAccessException)
            {
                Fail("The game could not be opened: " + e.Message);
                return false;
            }
            Say("Editing '" + mod.Manifest.Name + "' in " + Workspace.Directory + ".");
            Say("Say what to change. Type /help for the commands.");
            DescribeAgent();
            return true;
        }

        private void Reset(ModWorkspace workspace)
        {
            Cancel();
            Workspace = workspace;
            _lines.Clear();
            _earlier.Clear();
            _undo.Clear();
            _repairs = 0;
            Version++;
        }

        // A line that starts with a slash is a command for the console; anything else goes to the agent.
        public void Submit(string text)
        {
            ThrowIfDisposed();
            text = text == null ? string.Empty : text.Trim();
            if (text.Length == 0)
                return;
            if (text[0] == '/')
            {
                // A key must not stay on screen.
                Add(StudioLineKind.Prompt, text.StartsWith("/key ", StringComparison.OrdinalIgnoreCase) ? "/key ••••" : text);
                RunCommand(text);
                return;
            }
            Add(StudioLineKind.Prompt, text);
            if (Busy)
            {
                Fail("The agent is still working. Wait for it or stop it with /cancel.");
                return;
            }
            _repairs = 0;
            _repairing = false;
            Send(text, null);
        }

        public void SelectAgent(string id)
        {
            if (!Settings.Select(id))
            {
                Fail("There is no agent '" + id + "'. /agents lists them.");
                return;
            }
            Settings.Save();
            _readinessAt = double.NegativeInfinity;
            DescribeAgent();
        }

        public void Cancel()
        {
            if (_run == null)
                return;
            _run.Cancel();
            Finish();
        }

        // Collects what the agent wrote since the last call and, once it is done, tries the game and applies it.
        public void Update()
        {
            if (_disposed || _run == null)
                return;
            Collect();
            if (!_run.IsRunning)
                Finish();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _run?.Dispose();
            _run = null;
        }

        private void Send(string request, string failure)
        {
            AgentProfile profile = Settings.Selected;
            string notReady = _launcher.Readiness(profile, Settings) ?? string.Empty;
            _readiness = notReady;
            _readinessAt = _host.Now;
            if (notReady.Length > 0)
            {
                Fail(notReady);
                if (profile.Hint.Length > 0)
                    Say(profile.Hint);
                Say("/agents lists the other agents.");
                return;
            }

            try
            {
                if (Workspace == null)
                {
                    Workspace = ModWorkspace.Create(_modsRoot, NextId(), "New Game");
                    Say("Created the game folder " + Workspace.Directory + ".");
                }
                _before = Workspace.ReadFiles();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                Fail("The game folder could not be prepared: " + e.Message);
                return;
            }

            // While a request is being repaired it is not one of the earlier ones.
            List<string> earlier = _earlier;
            if (failure != null && earlier.Count > 0)
                earlier = earlier.GetRange(0, earlier.Count - 1);
            AgentRequest message = profile.Kind == AgentKind.Cli
                ? AgentPrompt.ForCli(Workspace, request, earlier, failure)
                : AgentPrompt.ForApi(Workspace, _before, request, earlier, failure);
            _request = request;
            _runProfile = profile;
            BusySince = _host.Now;
            try
            {
                _run = _launcher.Start(profile, Settings, message);
            }
            catch (Exception e) when (e is IOException || e is InvalidOperationException || e is ArgumentException)
            {
                Fail(profile.Name + " could not be started: " + e.Message);
                _run = null;
            }
        }

        private void Collect()
        {
            _drained.Clear();
            _run.Drain(_drained);
            foreach (AgentLine line in _drained)
                Add(line.Kind == AgentLineKind.Output ? StudioLineKind.Agent : StudioLineKind.Info, line.Text);
        }

        private void Finish()
        {
            AgentRun run = _run;
            AgentProfile profile = _runProfile;
            _run = null;
            _runProfile = null;
            _drained.Clear();
            run.Drain(_drained);
            foreach (AgentLine line in _drained)
                Add(line.Kind == AgentLineKind.Output ? StudioLineKind.Agent : StudioLineKind.Info, line.Text);

            bool succeeded = run.Succeeded;
            string error = run.Error;
            string reply = run.Reply;
            bool cancelled = run.Cancelled;
            run.Dispose();

            if (!succeeded)
            {
                Fail(error.Length > 0 ? error : "The agent failed.");
                if (!cancelled && profile != null && profile.Hint.Length > 0)
                    Say(profile.Hint);
            }
            else if (reply.Length > 0 && !WriteReply(reply))
            {
                return;
            }

            SortedDictionary<string, string> after;
            try
            {
                after = Workspace.ReadFiles();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Fail("The game folder could not be read: " + e.Message);
                return;
            }
            if (ModWorkspace.SameFiles(_before, after))
            {
                if (succeeded)
                    Say(_repairing ? "The agent changed nothing, so the game still does not work. Describe a fix, or use /undo." : "No file of the game was changed.");
                return;
            }

            // A repair belongs to the request it repairs: undo goes back to before that request.
            if (!_repairing)
            {
                Remember(_before);
                _earlier.Add(_request);
                if (_earlier.Count > MaxEarlier)
                    _earlier.RemoveAt(0);
            }
            if (!succeeded)
                Say("The files were changed before the agent stopped. /undo brings the previous version back.");
            Try(succeeded);
        }

        // Writes the files of a text answer into the folder. False when nothing is left to do with the answer.
        private bool WriteReply(string reply)
        {
            FileBlocks blocks = FileBlocks.Parse(reply);
            if (blocks.Commentary.Length > 0)
                Add(StudioLineKind.Agent, blocks.Commentary);
            if (blocks.Files.Count == 0)
                return false;
            foreach (FileBlock file in blocks.Files)
            {
                try
                {
                    Workspace.Write(file.Path, file.Content);
                    Say("Wrote " + file.Path + ".");
                }
                catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException)
                {
                    Fail("Skipped a file: " + e.Message);
                }
            }
            return true;
        }

        // Tries the game in the folder and puts it on the table when it works. A game that does not work goes back
        // to the agent with the reason, a limited number of times.
        private void Try(bool mayRepair)
        {
            ModCheck check = Workspace.Check(_host.SessionOptions);
            foreach (string warning in check.Warnings)
                Say("Note: " + warning);
            if (check.Ok)
            {
                Apply();
                return;
            }

            Fail(check.Error);
            if (mayRepair && _repairs < MaxRepairs)
            {
                _repairs++;
                _repairing = true;
                Say("Asking the agent to fix it (attempt " + _repairs + " of " + MaxRepairs + ") …");
                Send(_request, check.Error);
                return;
            }
            Say("The game on the table is unchanged. Describe a fix, or bring the previous version back with /undo.");
        }

        private void Apply()
        {
            _host.Catalog.Refresh();
            ModEntry entry = _host.Catalog.FindEntry(Workspace.Id);
            if (entry == null || entry.Mod.Directory == null || !string.Equals(Path.GetFullPath(entry.Mod.Directory), Workspace.Directory, PathComparison))
            {
                Fail("The game list did not pick up " + Workspace.Directory + ". Its id '" + Workspace.Id + "' may belong to another game.");
                return;
            }
            if (!_host.StartGame(entry.Mod))
            {
                Fail("The game could not be started.");
                return;
            }
            Say("Applied: '" + entry.Name + "' is on the table with the new rules.");
            Applied?.Invoke(entry.Mod);
        }

        // Whether the game is one of the AI console: a folder directly inside the folder for new games that the
        // console has worked in. Games that come with the app or lie elsewhere are never deleted from here.
        public bool CanDelete(ModPackage mod)
        {
            if (mod == null || mod.Directory == null)
                return false;
            try
            {
                string directory = Path.GetFullPath(mod.Directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.Equals(Path.GetDirectoryName(directory), _modsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), PathComparison))
                    return false;
                if (!File.Exists(Path.Combine(directory, ModWorkspace.GuideFileName)) || !File.Exists(Path.Combine(directory, ModLoader.ManifestFileName)))
                    return false;
                // A link is not followed: what it points at is not ours to remove.
                return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            {
                return false;
            }
        }

        // Removes the folder of a game for good and takes the game off the list. A game that is on the table keeps
        // running from memory until another one is started. The message says what happened either way.
        public bool Delete(ModPackage mod, out string message)
        {
            ThrowIfDisposed();
            if (!CanDelete(mod))
            {
                message = "Only games made with the AI console can be deleted.";
                return false;
            }
            string directory = Path.GetFullPath(mod.Directory);
            bool current = Workspace != null && string.Equals(Path.GetFullPath(Workspace.Directory), directory, PathComparison);
            if (current && Busy)
            {
                message = "The agent is still working on this game. Stop it with /cancel first.";
                return false;
            }
            try
            {
                Directory.Delete(directory, true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                message = "'" + mod.Manifest.Name + "' could not be deleted: " + e.Message;
                _host.Catalog.Refresh();
                return false;
            }
            _host.Catalog.Refresh();
            message = "Deleted '" + mod.Manifest.Name + "'.";
            if (current)
            {
                Workspace = null;
                _earlier.Clear();
                _undo.Clear();
                _repairs = 0;
                Say(message + " The next request starts a new game.");
            }
            return true;
        }

        private void DeleteCurrent(string argument)
        {
            if (!HasWorkspace())
                return;
            ModEntry entry = _host.Catalog.FindEntry(Workspace.Id);
            ModPackage mod = entry != null && entry.Mod.Directory != null && string.Equals(Path.GetFullPath(entry.Mod.Directory), Workspace.Directory, PathComparison) ? entry.Mod : null;
            if (mod == null)
            {
                try
                {
                    mod = ModLoader.LoadDirectory(Workspace.Directory);
                }
                catch (ModException)
                {
                    Fail("The game cannot be read, so it is not deleted from here. Its folder is " + Workspace.Directory + ".");
                    return;
                }
            }
            if (!string.Equals(argument, "confirm", StringComparison.OrdinalIgnoreCase))
            {
                Say("This removes '" + mod.Manifest.Name + "' and its folder " + Workspace.Directory + " for good. Type /delete confirm to do it.");
                return;
            }
            if (!Delete(mod, out string message))
                Fail(message);
        }

        private void Remember(SortedDictionary<string, string> files)
        {
            _undo.Add(files);
            if (_undo.Count > MaxUndo)
                _undo.RemoveAt(0);
        }

        private string NextId()
        {
            for (int number = 1; ; number++)
            {
                string id = "my-game-" + number;
                if (_host.Catalog.FindEntry(id) == null && !Directory.Exists(Path.Combine(_modsRoot, id)))
                    return id;
            }
        }

        private void RunCommand(string line)
        {
            int space = line.IndexOf(' ');
            string name = (space < 0 ? line : line.Substring(0, space)).ToLowerInvariant();
            string argument = space < 0 ? string.Empty : line.Substring(space + 1).Trim();
            switch (name)
            {
                case "/help":
                case "/?":
                    Say("Type what the game should be or what to change; the agent edits the game and it restarts.");
                    Say("/agents            list the agents");
                    Say("/agent <id>        choose an agent");
                    Say("/key <key>         store the key of the chosen agent (/key clear forgets it)");
                    Say("/model <name>      choose the model of the chosen agent (/model alone shows it)");
                    Say("/new               start another new game");
                    Say("/files             list the files of the game");
                    Say("/check             try the game and put it on the table");
                    Say("/undo              bring back the files from before the last change");
                    Say("/delete            delete this game and its folder for good");
                    Say("/cancel            stop the agent");
                    Say("/clear             empty this window");
                    break;
                case "/agents":
                    ListAgents();
                    break;
                case "/agent":
                    if (argument.Length == 0)
                        DescribeAgent();
                    else if (Guard())
                        SelectAgent(argument);
                    break;
                case "/key":
                    SetKey(argument);
                    break;
                case "/model":
                    SetModel(argument);
                    break;
                case "/new":
                    if (Guard())
                        BeginNew();
                    break;
                case "/files":
                    ListFiles();
                    break;
                case "/check":
                    if (Guard() && HasWorkspace())
                        Try(false);
                    break;
                case "/undo":
                    if (Guard() && HasWorkspace())
                        Undo();
                    break;
                case "/delete":
                    if (Guard())
                        DeleteCurrent(argument);
                    break;
                case "/cancel":
                    if (Busy)
                        Cancel();
                    else
                        Say("No agent is working.");
                    break;
                case "/clear":
                    _lines.Clear();
                    Version++;
                    break;
                default:
                    Fail("There is no command '" + name + "'. /help lists the commands.");
                    break;
            }
        }

        private bool Guard()
        {
            if (!Busy)
                return true;
            Fail("The agent is still working. Wait for it or stop it with /cancel.");
            return false;
        }

        private bool HasWorkspace()
        {
            if (Workspace != null)
                return true;
            Say("There is no game yet. Describe the game you want first.");
            return false;
        }

        private void ListAgents()
        {
            foreach (AgentProfile profile in Settings.Profiles)
            {
                string state = _launcher.Readiness(profile, Settings) ?? string.Empty;
                string mark = string.Equals(profile.Id, Settings.SelectedId, StringComparison.Ordinal) ? "* " : "  ";
                Say(mark + profile.Id.PadRight(12) + " " + profile.Name + (state.Length == 0 ? "  (ready)" : string.Empty));
            }
            Say("Choose one with /agent <id>.");
        }

        private void DescribeAgent()
        {
            AgentProfile profile = Settings.Selected;
            string state = Readiness;
            StringBuilder text = new StringBuilder("Agent: " + profile.Name);
            if (profile.Kind == AgentKind.Api)
                text.Append(" · model ").Append(Settings.ModelFor(profile));
            text.Append(state.Length == 0 ? " · ready" : " · " + state);
            Say(text.ToString());
            if (state.Length > 0 && profile.Hint.Length > 0)
                Say(profile.Hint);
        }

        private void SetKey(string key)
        {
            AgentProfile profile = Settings.Selected;
            if (key.Length == 0)
            {
                Say(Settings.KeyFor(profile).Length > 0 ? profile.Name + " has a key." : profile.Name + " has no key. Store one with /key <key>.");
                return;
            }
            bool clear = string.Equals(key, "clear", StringComparison.OrdinalIgnoreCase);
            Settings.SetKey(profile, clear ? string.Empty : key);
            bool saved = Settings.Save();
            _readinessAt = double.NegativeInfinity;
            if (clear)
                Say("The key of " + profile.Name + " was forgotten.");
            else
                Say("The key of " + profile.Name + " was stored" + (Settings.Path != null && saved ? " in " + Settings.Path + "." : " for this session."));
            if (!saved)
                Fail(Settings.Problem);
        }

        private void SetModel(string model)
        {
            AgentProfile profile = Settings.Selected;
            if (profile.Kind != AgentKind.Api)
            {
                Say(profile.Name + " chooses its model itself; change it in the settings of that tool.");
                return;
            }
            if (model.Length > 0)
            {
                Settings.SetModel(profile, string.Equals(model, "default", StringComparison.OrdinalIgnoreCase) ? string.Empty : model);
                if (!Settings.Save())
                    Fail(Settings.Problem);
                _readinessAt = double.NegativeInfinity;
            }
            Say(profile.Name + " uses the model " + Settings.ModelFor(profile) + ".");
        }

        private void ListFiles()
        {
            if (!HasWorkspace())
                return;
            Say(Workspace.Directory);
            try
            {
                foreach (KeyValuePair<string, string> file in Workspace.ReadFiles())
                    Say("  " + file.Key + "  (" + file.Value.Split('\n').Length + " lines)");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Fail("The game folder could not be read: " + e.Message);
            }
        }

        private void Undo()
        {
            if (_undo.Count == 0)
            {
                Say("There is nothing to undo.");
                return;
            }
            SortedDictionary<string, string> files = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            if (_earlier.Count > 0)
                _earlier.RemoveAt(_earlier.Count - 1);
            try
            {
                Workspace.Restore(files);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                Fail("The previous version could not be restored: " + e.Message);
                return;
            }
            Say("The files from before the last change are back.");
            Try(false);
        }

        private void Say(string text)
        {
            Add(StudioLineKind.Info, text);
        }

        private void Fail(string text)
        {
            Add(StudioLineKind.Error, text);
        }

        private void Add(StudioLineKind kind, string text)
        {
            foreach (string line in (text ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
                _lines.Add(new StudioLine(kind, line));
            if (_lines.Count > MaxLines)
                _lines.RemoveRange(0, _lines.Count - MaxLines);
            Version++;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ModStudio));
        }
    }
}
