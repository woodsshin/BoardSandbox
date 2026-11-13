using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Luau;
using BoardSandbox.Core.Mods;

namespace BoardSandbox.Core.Agents
{
    // What trying out a game folder found.
    public sealed class ModCheck
    {
        internal ModCheck(ModPackage mod, string error, List<string> warnings)
        {
            Mod = mod;
            Error = error ?? string.Empty;
            Warnings = warnings ?? new List<string>();
        }

        public bool Ok => Error.Length == 0;

        // The loaded game; null when it could not even be read.
        public ModPackage Mod { get; }

        // Why the game cannot be played, worded so that it can be handed back to the agent that wrote it.
        public string Error { get; }

        // Things that work but are probably not meant.
        public IReadOnlyList<string> Warnings { get; }
    }

    // The folder of the game an agent works on: its config.json and its .luau files.
    public sealed class ModWorkspace
    {
        public const string GuideFileName = "AGENTS.md";

        private const int MaxFiles = 64;
        private const int MaxFileBytes = 512 * 1024;
        private const int CheckMoves = 60;
        private const int CheckMilliseconds = 1500;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private ModWorkspace(string directory, string id)
        {
            Directory = directory;
            Id = id;
        }

        public string Directory { get; }

        // The id of the game, which has to stay what it was when the folder was opened.
        public string Id { get; }

        // Makes the folder of a new game with a small game in it that already runs, so that the first request
        // changes something that works.
        public static ModWorkspace Create(string modsRoot, string id, string name)
        {
            if (string.IsNullOrWhiteSpace(modsRoot))
                throw new ArgumentException("a folder for the games is required", nameof(modsRoot));
            if (!ModManifest.IsValidId(id))
                throw new ArgumentException("'" + id + "' is not a valid game id", nameof(id));
            string directory = Path.GetFullPath(Path.Combine(modsRoot, id));
            if (System.IO.Directory.Exists(directory))
                throw new IOException("the folder '" + directory + "' already exists.");

            System.IO.Directory.CreateDirectory(directory);
            ModWorkspace workspace = new ModWorkspace(directory, id);
            File.WriteAllText(Path.Combine(directory, ModLoader.ManifestFileName), AgentPrompt.StarterManifest(id, name), Utf8);
            File.WriteAllText(Path.Combine(directory, "logic.luau"), AgentPrompt.StarterScript, Utf8);
            workspace.WriteGuide();
            return workspace;
        }

        // Opens the folder of a game that is already there.
        public static ModWorkspace Open(string directory)
        {
            ModPackage mod = ModLoader.LoadDirectory(directory);
            ModWorkspace workspace = new ModWorkspace(Path.GetFullPath(directory), mod.Manifest.Id);
            workspace.WriteGuide();
            return workspace;
        }

        // Agents that read a project guide on their own find the rules of the engine there.
        private void WriteGuide()
        {
            try
            {
                File.WriteAllText(Path.Combine(Directory, GuideFileName), AgentPrompt.Guide, Utf8);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // The request carries the guide as well.
            }
        }

        // Whether an agent may write this file: the manifest and scripts, nothing else and nowhere else.
        public static bool IsWritable(string relative)
        {
            if (relative == ModLoader.ManifestFileName)
                return true;
            if (!ModManifest.IsValidModulePath(relative))
                return false;
            foreach (string part in relative.Split('/'))
            {
                if (part.StartsWith(".", StringComparison.Ordinal) || part.IndexOf(':') >= 0)
                    return false;
            }
            return true;
        }

        // The manifest and every script by their path inside the folder, with forward slashes.
        public SortedDictionary<string, string> ReadFiles()
        {
            SortedDictionary<string, string> files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            string manifest = Path.Combine(Directory, ModLoader.ManifestFileName);
            if (File.Exists(manifest))
                files[ModLoader.ManifestFileName] = File.ReadAllText(manifest, Utf8);
            if (!System.IO.Directory.Exists(Directory))
                return files;
            foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*" + ModLoader.ScriptExtension, SearchOption.AllDirectories))
            {
                string relative = Path.GetFullPath(file).Substring(Directory.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                if (!IsWritable(relative) || files.Count >= MaxFiles || new FileInfo(file).Length > MaxFileBytes)
                    continue;
                files[relative] = File.ReadAllText(file, Utf8);
            }
            return files;
        }

        public void Write(string relative, string content)
        {
            if (!IsWritable(relative))
                throw new ArgumentException("'" + relative + "' is not a file of a game. Only config.json and .luau files can be written.", nameof(relative));
            content = content ?? string.Empty;
            if (Utf8.GetByteCount(content) > MaxFileBytes)
                throw new ArgumentException("'" + relative + "' is larger than " + MaxFileBytes + " bytes.", nameof(content));
            string path = Path.Combine(Directory, relative.Replace('/', Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content, Utf8);
        }

        // Puts the folder back to what ReadFiles returned earlier: the files of then come back and scripts that were
        // added since go away.
        public void Restore(IDictionary<string, string> files)
        {
            if (files == null)
                throw new ArgumentNullException(nameof(files));
            foreach (string existing in ReadFiles().Keys)
            {
                if (!files.ContainsKey(existing))
                    File.Delete(Path.Combine(Directory, existing.Replace('/', Path.DirectorySeparatorChar)));
            }
            foreach (KeyValuePair<string, string> pair in files)
                Write(pair.Key, pair.Value);
        }

        public static bool SameFiles(IDictionary<string, string> a, IDictionary<string, string> b)
        {
            if (a.Count != b.Count)
                return false;
            foreach (KeyValuePair<string, string> pair in a)
            {
                if (!b.TryGetValue(pair.Key, out string other) || !string.Equals(pair.Value, other, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        // Loads the game, starts it and lets random players make some moves, as far as the game offers them.
        public ModCheck Check(SessionOptions options = null)
        {
            ModPackage mod;
            try
            {
                mod = ModLoader.LoadDirectory(Directory);
            }
            catch (ModException e)
            {
                return new ModCheck(null, "The game could not be read: " + e.Message, null);
            }
            if (!string.Equals(mod.Manifest.Id, Id, StringComparison.Ordinal))
                return new ModCheck(mod, "config.json changed the id to '" + mod.Manifest.Id + "'. The id has to stay '" + Id + "'.", null);

            List<string> warnings = new List<string>();
            try
            {
                using (GameSession session = new GameSession(mod, options))
                {
                    session.NewGame();
                    if (session.View.Turn <= 0 && !session.View.IsOver)
                        return new ModCheck(mod, "rules.new_game did not say whose turn it is. Call bse.turn(seat) at the end of new_game.", warnings);
                    string problem = PlayRandomly(session, warnings);
                    if (problem != null)
                        return new ModCheck(mod, problem, warnings);
                }
            }
            catch (Exception e) when (e is LuauException || e is GameSessionException || e is ModException)
            {
                return new ModCheck(mod, "The game script failed: " + Describe(e), warnings);
            }

            JsonValue client = mod.Manifest.Client;
            bool playable = client != null && (client["click"]["action"].AsString().Length > 0 || client["drag"]["action"].AsString().Length > 0 || client["keys"].Count > 0);
            if (!playable)
                warnings.Add("The game takes no clicks, drags or keys (config.json has no client.click.action), so it can only be played from the console.");
            return new ModCheck(mod, null, warnings);
        }

        private static string PlayRandomly(GameSession session, List<string> warnings)
        {
            if (!session.Capabilities.Random)
            {
                warnings.Add("The game has no random_command or legal_commands, so it has no computer player and could not be played through.");
                return null;
            }
            Stopwatch clock = Stopwatch.StartNew();
            for (int move = 0; move < CheckMoves && !session.View.IsOver && clock.ElapsedMilliseconds < CheckMilliseconds; move++)
            {
                int seat = session.View.Turn;
                if (seat <= 0)
                    return "After move " + move + " nobody is on turn and the game is not over. Call bse.turn(seat) or bse.over(...) in rules.command.";
                JsonValue command = session.RandomCommand(seat, move + 1);
                if (command == null || !command.IsObject)
                {
                    warnings.Add("Seat " + seat + " had no move after " + move + " moves although the game was not over.");
                    return null;
                }
                SubmitResult result = session.Submit(new InputPacket(seat, command, "check"));
                if (result.Status == SubmitStatus.Faulted)
                    return "rules.command failed on " + command.ToJson() + " from seat " + seat + ": " + result.Code + ": " + result.Message + Trace(result.Traceback);
                if (!result.IsAccepted)
                    return "rules.command rejected the command " + command.ToJson() + " that the game itself offered to seat " + seat + ": " + result.Code + ": " + result.Message;
            }
            return null;
        }

        private static string Describe(Exception e)
        {
            GameSessionException session = e as GameSessionException;
            if (session == null)
                return e.Message;
            return session.Code + ": " + session.Detail + Trace(session.Traceback);
        }

        private static string Trace(string traceback)
        {
            return string.IsNullOrWhiteSpace(traceback) ? string.Empty : "\n" + traceback.Trim();
        }
    }
}
