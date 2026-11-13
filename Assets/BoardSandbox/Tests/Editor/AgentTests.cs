using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BoardSandbox.Core.Agents;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Mods;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class AgentTests
    {
        private const string BrokenScript = "local rules = {}\nfunction rules.new_game(options)\n  bse.turn(1\nend\nreturn rules\n";

        private const string CountingScript = @"local rules = {}
local total = 0
function rules.new_game(options)
	total = 0
	bse.turn(1)
end
function rules.command(seat, cmd)
	if cmd.action ~= ""add"" then
		return bse.reject(""UNKNOWN_ACTION"", ""unknown action"")
	end
	total += 1
	if total >= 5 then
		bse.over({ winner = seat, reason = ""counted to five"" })
	else
		bse.turn(3 - seat)
	end
	return nil
end
function rules.random_command(seat)
	return { action = ""add"" }
end
return rules
";

        private readonly List<string> _temporary = new List<string>();
        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        [TearDown]
        public void CleanUp()
        {
            foreach (IDisposable disposable in _disposables)
                disposable.Dispose();
            _disposables.Clear();
            foreach (string path in _temporary)
            {
                try
                {
                    Directory.Delete(path, true);
                }
                catch (IOException)
                {
                }
            }
            _temporary.Clear();
        }

        private string NewDirectory()
        {
            string path = TestSupport.TempDirectory();
            _temporary.Add(path);
            return path;
        }

        // An agent that does what the test tells it to, at once.
        private sealed class FakeRun : AgentRun
        {
            public void Say(string text)
            {
                Post(AgentLineKind.Output, text);
            }

            public void End(bool succeeded, string error = "", string reply = "")
            {
                Complete(succeeded, error, reply);
            }

            protected override void Stop()
            {
                Complete(false, "Cancelled.");
            }
        }

        private sealed class FakeLauncher : IAgentLauncher
        {
            public readonly List<AgentRequest> Requests = new List<AgentRequest>();
            public readonly List<AgentProfile> Profiles = new List<AgentProfile>();
            public Action<AgentRequest, FakeRun> Work = (request, run) => run.End(true);
            public string NotReady = string.Empty;

            public string Readiness(AgentProfile profile, AgentSettings settings)
            {
                return NotReady;
            }

            public AgentRun Start(AgentProfile profile, AgentSettings settings, AgentRequest request)
            {
                Requests.Add(request);
                Profiles.Add(profile);
                FakeRun run = new FakeRun();
                Work(request, run);
                return run;
            }
        }

        private ModStudio Studio(out GameHost host, out FakeLauncher launcher, out string root, AgentSettings settings = null)
        {
            root = NewDirectory();
            host = new GameHost(new ModCatalog(new IModSource[] { new FolderModSource(root, ModOrigin.Local) }));
            launcher = new FakeLauncher();
            ModStudio studio = new ModStudio(host, root, settings ?? new AgentSettings(null, name => null), launcher);
            _disposables.Add(studio);
            _disposables.Add(host);
            return studio;
        }

        private static string Text(ModStudio studio)
        {
            StringBuilder builder = new StringBuilder();
            foreach (StudioLine line in studio.Lines)
                builder.Append(line.Kind).Append(": ").Append(line.Text).Append('\n');
            return builder.ToString();
        }

        private static void Write(AgentRequest request, string relative, string text)
        {
            TestSupport.WriteFile(request.Directory, relative, text);
        }

        [Test]
        public void FileBlocksAreTakenOutOfAnAnswer()
        {
            FileBlocks blocks = FileBlocks.Parse("I made the board bigger.\r\n\r\n=== FILE: config.json ===\n{ \"a\": 1 }\n=== END FILE ===\n\n=== FILE: lib/util.luau ===\nreturn {}\n=== END FILE ===\nDone.");

            Assert.AreEqual(2, blocks.Files.Count);
            Assert.AreEqual("config.json", blocks.Files[0].Path);
            Assert.AreEqual("{ \"a\": 1 }\n", blocks.Files[0].Content);
            Assert.AreEqual("lib/util.luau", blocks.Files[1].Path);
            Assert.AreEqual("return {}\n", blocks.Files[1].Content);
            Assert.AreEqual("I made the board bigger.\n\nDone.", blocks.Commentary);
        }

        [Test]
        public void CodeFencesAroundAFileAndUnclosedBlocksAreUnderstood()
        {
            FileBlocks blocks = FileBlocks.Parse("=== FILE: `a.luau` ===\n```lua\nlocal x = 1\n```\n=== FILE: b.luau ===\nreturn 2");

            Assert.AreEqual(2, blocks.Files.Count);
            Assert.AreEqual("a.luau", blocks.Files[0].Path);
            Assert.AreEqual("local x = 1\n", blocks.Files[0].Content);
            Assert.AreEqual("return 2\n", blocks.Files[1].Content);
            Assert.AreEqual(string.Empty, blocks.Commentary);
        }

        [Test]
        public void FormattedBlocksParseBack()
        {
            FileBlocks blocks = FileBlocks.Parse(FileBlocks.Format("logic.luau", "return {}"));

            Assert.AreEqual(1, blocks.Files.Count);
            Assert.AreEqual("return {}\n", blocks.Files[0].Content);
        }

        [Test]
        public void AnAnswerWithoutBlocksIsAllCommentary()
        {
            FileBlocks blocks = FileBlocks.Parse("The game ends when the board is full.\n=== not a marker\n");

            Assert.AreEqual(0, blocks.Files.Count);
            StringAssert.Contains("board is full", blocks.Commentary);
        }

        [Test]
        public void TerminalEscapesAreRemoved()
        {
            Assert.AreEqual("green text", TerminalText.Clean("\u001b[32mgreen\u001b[0m text\r"));
            Assert.AreEqual("title", TerminalText.Clean("\u001b]0;window\u0007title"));
            Assert.AreEqual("a    b", TerminalText.Clean("a\tb"));
        }

        [Test]
        public void ArgumentsAreQuotedForACommandLine()
        {
            Assert.AreEqual("plain", CliAgentRun.Quote("plain"));
            Assert.AreEqual("\"two words\"", CliAgentRun.Quote("two words"));
            Assert.AreEqual("\"say \\\"hi\\\"\"", CliAgentRun.Quote("say \"hi\""));
            Assert.AreEqual("\"\"", CliAgentRun.Quote(string.Empty));
            Assert.AreEqual("\"ends with \\\\\"", CliAgentRun.Quote("ends with \\"));
        }

        [Test]
        public void TheKnownAgentsCoverCommandLineToolsAndFreeEndpoints()
        {
            AgentSettings settings = new AgentSettings(null, name => null);

            foreach (string id in new[] { "claude", "codex", "gemini", "grok", "gemini-api", "openrouter", "ollama" })
                Assert.IsNotNull(settings.Find(id), id);
            Assert.AreEqual(AgentKind.Cli, settings.Find("claude").Kind);
            Assert.AreEqual(AgentKind.Api, settings.Find("gemini-api").Kind);
            Assert.IsTrue(settings.Find("ollama").KeyOptional);
            foreach (AgentProfile profile in settings.Profiles)
            {
                Assert.IsTrue(profile.Hint.Length > 0, profile.Id);
                // Nothing but the pointer to the request file may come from outside the profile.
                if (profile.Kind == AgentKind.Cli && profile.Channel == PromptChannel.File)
                    Assert.IsTrue(new List<string>(profile.Arguments).Contains(AgentProfile.PromptPlaceholder), profile.Id);
            }
        }

        [Test]
        public void ProfilesSurviveJson()
        {
            AgentProfile original = new AgentProfile("mine", "My Agent", AgentKind.Cli) { Command = "my-agent", Channel = PromptChannel.File, KeyVariable = "MY_KEY", Hint = "h" }
                .WithArguments("--go", AgentProfile.PromptPlaceholder);

            AgentProfile copy = AgentProfile.FromJson(JsonValue.Parse(original.ToJson().ToJson()));

            Assert.AreEqual("mine", copy.Id);
            Assert.AreEqual("My Agent", copy.Name);
            Assert.AreEqual(AgentKind.Cli, copy.Kind);
            Assert.AreEqual("my-agent", copy.Command);
            Assert.AreEqual(PromptChannel.File, copy.Channel);
            Assert.AreEqual(2, copy.Arguments.Count);
            Assert.AreEqual("MY_KEY", copy.KeyVariable);
            Assert.IsNull(AgentProfile.FromJson(JsonValue.Parse("{\"id\":\"x\",\"kind\":\"cli\"}")));
            Assert.IsNull(AgentProfile.FromJson(JsonValue.Parse("{\"id\":\"x\",\"kind\":\"api\"}")));
            Assert.IsNull(AgentProfile.FromJson(JsonValue.Parse("[]")));
        }

        [Test]
        public void SettingsAreSavedAndReadBack()
        {
            string path = Path.Combine(NewDirectory(), "sub", "agents.json");
            AgentSettings settings = new AgentSettings(path, name => null);
            settings.Select("gemini-api");
            settings.SetKey(settings.Selected, " secret ");
            settings.SetModel(settings.Selected, "gemini-test");
            Assert.IsTrue(settings.Save());

            AgentSettings loaded = new AgentSettings(path, name => null);
            Assert.IsTrue(loaded.Load());

            Assert.AreEqual("gemini-api", loaded.SelectedId);
            Assert.AreEqual("secret", loaded.KeyFor(loaded.Selected));
            Assert.AreEqual("gemini-test", loaded.ModelFor(loaded.Selected));
            Assert.AreEqual(settings.Find("ollama").Model, loaded.ModelFor(loaded.Find("ollama")));
        }

        [Test]
        public void TheEnvironmentWinsOverAStoredKey()
        {
            AgentSettings settings = new AgentSettings(null, name => name == "GEMINI_API_KEY" ? "from-env" : null);
            AgentProfile profile = settings.Find("gemini-api");
            settings.SetKey(profile, "stored");

            Assert.AreEqual("from-env", settings.KeyFor(profile));
            Assert.AreEqual(string.Empty, settings.KeyFor(settings.Find("openai-api")));
        }

        [Test]
        public void ProfilesInTheSettingsFileReplaceAndExtendTheKnownOnes()
        {
            AgentSettings settings = new AgentSettings(null, name => null);
            settings.Apply(JsonValue.Parse(@"{ ""selected"": ""local"", ""profiles"": [
                { ""id"": ""claude"", ""kind"": ""cli"", ""command"": ""claude-next"", ""args"": [""-p""] },
                { ""id"": ""local"", ""name"": ""Local"", ""kind"": ""api"", ""endpoint"": ""http://localhost:1234/v1/chat/completions"", ""model"": ""m"", ""key_optional"": true },
                { ""id"": """", ""kind"": ""cli"" } ] }"));

            Assert.AreEqual("claude-next", settings.Find("claude").Command);
            Assert.AreEqual("local", settings.SelectedId);
            Assert.AreEqual(AgentProfile.Defaults().Count + 1, settings.Profiles.Count);
            Assert.AreEqual(2, settings.ToJson()["profiles"].Count);
        }

        [Test]
        public void AnUnreadableSettingsFileLeavesTheDefaults()
        {
            string path = Path.Combine(NewDirectory(), "agents.json");
            File.WriteAllText(path, "{ not json");
            AgentSettings settings = new AgentSettings(path, name => null);

            Assert.IsFalse(settings.Load());
            Assert.IsTrue(settings.Problem.Length > 0);
            Assert.AreEqual(AgentProfile.Defaults().Count, settings.Profiles.Count);
        }

        [Test]
        public void ANewWorkspaceHoldsAGameThatPlays()
        {
            string root = NewDirectory();
            ModWorkspace workspace = ModWorkspace.Create(root, "my-game-1", "Tryout");

            ModCheck check = workspace.Check();

            Assert.IsTrue(check.Ok, check.Error);
            Assert.AreEqual(0, check.Warnings.Count, string.Join(" | ", check.Warnings));
            Assert.AreEqual("Tryout", check.Mod.Manifest.Name);
            Assert.IsTrue(File.Exists(Path.Combine(workspace.Directory, ModWorkspace.GuideFileName)));
            Assert.AreEqual(2, workspace.ReadFiles().Count);
            Assert.Throws<IOException>(() => ModWorkspace.Create(root, "my-game-1", "Again"));
            Assert.Throws<ArgumentException>(() => ModWorkspace.Create(root, "Bad Id", "x"));
        }

        [Test]
        public void OnlyTheManifestAndScriptsInsideTheFolderCanBeWritten()
        {
            Assert.IsTrue(ModWorkspace.IsWritable("config.json"));
            Assert.IsTrue(ModWorkspace.IsWritable("logic.luau"));
            Assert.IsTrue(ModWorkspace.IsWritable("lib/util.luau"));
            foreach (string path in new[] { "../evil.luau", "/abs.luau", "notes.txt", "run.cmd", ".hidden/x.luau", "a\\b.luau", "lib/../../x.luau", "c:x.luau", "sub/config.json", "" })
                Assert.IsFalse(ModWorkspace.IsWritable(path), path);

            ModWorkspace workspace = ModWorkspace.Create(NewDirectory(), "g", "G");
            Assert.Throws<ArgumentException>(() => workspace.Write("../outside.luau", "x"));
            workspace.Write("lib/extra.luau", "return 1\n");
            Assert.IsTrue(workspace.ReadFiles().ContainsKey("lib/extra.luau"));
        }

        [Test]
        public void TheCheckSaysWhatIsWrongWithAGame()
        {
            ModWorkspace workspace = ModWorkspace.Create(NewDirectory(), "g", "G");
            SortedDictionary<string, string> good = workspace.ReadFiles();

            workspace.Write("logic.luau", BrokenScript);
            ModCheck syntax = workspace.Check();
            Assert.IsFalse(syntax.Ok);
            StringAssert.Contains("logic", syntax.Error);

            workspace.Write("logic.luau", "local rules = {}\nfunction rules.new_game(options) end\nfunction rules.command(seat, cmd) return nil end\nreturn rules\n");
            StringAssert.Contains("bse.turn", workspace.Check().Error);

            workspace.Write("logic.luau", "local rules = {}\nfunction rules.new_game(o) bse.turn(1) end\nfunction rules.command(seat, cmd) return bse.reject(\"NO\", \"never\") end\nfunction rules.random_command(seat) return { action = \"x\" } end\nreturn rules\n");
            StringAssert.Contains("rejected", workspace.Check().Error);

            workspace.Write("logic.luau", "local rules = {}\nfunction rules.new_game(o) bse.turn(1) end\nfunction rules.command(seat, cmd) error(\"boom\") end\nfunction rules.random_command(seat) return { action = \"x\" } end\nreturn rules\n");
            StringAssert.Contains("boom", workspace.Check().Error);

            workspace.Restore(good);
            workspace.Write("config.json", good["config.json"].Replace("\"g\"", "\"other\""));
            StringAssert.Contains("id", workspace.Check().Error);

            workspace.Write("config.json", "{ nope");
            Assert.IsFalse(workspace.Check().Ok);

            workspace.Restore(good);
            Assert.IsTrue(workspace.Check().Ok);
        }

        [Test]
        public void GamesWithoutInputOrComputerPlayerGetWarnings()
        {
            ModWorkspace workspace = ModWorkspace.Create(NewDirectory(), "g", "G");
            workspace.Write("config.json", "{\"id\":\"g\",\"name\":\"G\",\"api\":1,\"entry\":\"logic.luau\"}");
            workspace.Write("logic.luau", "local rules = {}\nfunction rules.new_game(o) bse.turn(1) end\nfunction rules.command(seat, cmd) return nil end\nreturn rules\n");

            ModCheck check = workspace.Check();

            Assert.IsTrue(check.Ok, check.Error);
            Assert.AreEqual(2, check.Warnings.Count);
        }

        [Test]
        public void RestoreBringsFilesBackAndRemovesNewScripts()
        {
            ModWorkspace workspace = ModWorkspace.Create(NewDirectory(), "g", "G");
            SortedDictionary<string, string> before = workspace.ReadFiles();
            workspace.Write("logic.luau", "changed");
            workspace.Write("extra.luau", "return 1");
            Assert.IsFalse(ModWorkspace.SameFiles(before, workspace.ReadFiles()));

            workspace.Restore(before);

            Assert.IsTrue(ModWorkspace.SameFiles(before, workspace.ReadFiles()));
            Assert.IsFalse(File.Exists(Path.Combine(workspace.Directory, "extra.luau")));
        }

        [Test]
        public void RequestsCarryTheGuideTheTaskAndTheFiles()
        {
            ModWorkspace workspace = ModWorkspace.Create(NewDirectory(), "g", "G");

            AgentRequest cli = AgentPrompt.ForCli(workspace, "make it 10x10", new[] { "first idea" }, null);
            StringAssert.Contains("rules.new_game", cli.Prompt);
            StringAssert.Contains("make it 10x10", cli.Prompt);
            StringAssert.Contains("first idea", cli.Prompt);
            StringAssert.Contains("`g`", cli.Prompt);
            Assert.AreEqual(workspace.Directory, cli.Directory);
            Assert.AreEqual(string.Empty, cli.System);

            AgentRequest api = AgentPrompt.ForApi(workspace, workspace.ReadFiles(), "make it 10x10", null, "syntax error near end");
            StringAssert.Contains("=== FILE: logic.luau ===", api.Prompt);
            StringAssert.Contains("=== FILE: config.json ===", api.Prompt);
            StringAssert.Contains("syntax error near end", api.Prompt);
            StringAssert.Contains("=== END FILE ===", api.System);
            StringAssert.Contains("bse.view.spawn", api.System);
            Assert.AreEqual(2, FileBlocks.Parse(api.Prompt).Files.Count);
        }

        [Test]
        public void TheFirstRequestMakesAGameAndPutsItOnTheTable()
        {
            ModStudio studio = Studio(out GameHost host, out FakeLauncher launcher, out string root);
            ModPackage applied = null;
            studio.Applied += mod => applied = mod;
            launcher.Work = (request, run) =>
            {
                Write(request, "logic.luau", CountingScript);
                run.Say("I wrote a counting game.");
                run.End(true);
            };

            studio.BeginNew();
            Assert.IsNull(studio.Workspace);
            studio.Submit("a game where players count to five");
            Assert.IsTrue(studio.Busy);
            studio.Update();

            Assert.IsFalse(studio.Busy);
            Assert.AreEqual("my-game-1", studio.Workspace.Id);
            Assert.IsTrue(Directory.Exists(Path.Combine(root, "my-game-1")));
            Assert.IsNotNull(host.Session);
            Assert.AreEqual("my-game-1", host.Mod.Manifest.Id);
            Assert.IsTrue(host.Session.IsStarted);
            Assert.AreEqual(CountingScript, host.Mod.Sources["logic.luau"]);
            Assert.AreSame(host.Mod, applied);
            StringAssert.Contains("Agent: I wrote a counting game.", Text(studio));
            StringAssert.Contains("Applied", Text(studio));
            StringAssert.Contains("count to five", launcher.Requests[0].Prompt);
        }

        [Test]
        public void ABrokenGameGoesBackToTheAgentWithTheReason()
        {
            ModStudio studio = Studio(out GameHost host, out FakeLauncher launcher, out string _);
            launcher.Work = (request, run) =>
            {
                Write(request, "logic.luau", launcher.Requests.Count == 1 ? BrokenScript : CountingScript);
                run.End(true);
            };

            studio.BeginNew();
            studio.Submit("count to five");
            studio.Update();
            Assert.IsTrue(studio.Busy, "the repair should be on its way");
            Assert.IsNull(host.Session);
            studio.Update();

            Assert.IsFalse(studio.Busy);
            Assert.AreEqual(2, launcher.Requests.Count);
            StringAssert.Contains("does not work", launcher.Requests[1].Prompt);
            StringAssert.Contains("count to five", launcher.Requests[1].Prompt);
            StringAssert.DoesNotContain("Earlier requests", launcher.Requests[1].Prompt);
            Assert.AreEqual(CountingScript, host.Mod.Sources["logic.luau"]);

            // One step back is the game from before the request, not the broken one in between.
            studio.Submit("/undo");
            Assert.AreEqual(AgentPrompt.StarterScript, host.Mod.Sources["logic.luau"]);
        }

        [Test]
        public void RepairsStopAfterAFewAttemptsAndLeaveTheTableAlone()
        {
            ModStudio studio = Studio(out GameHost host, out FakeLauncher launcher, out string _);
            studio.BeginNew();
            launcher.Work = (request, run) =>
            {
                Write(request, "logic.luau", CountingScript);
                run.End(true);
            };
            studio.Submit("count to five");
            studio.Update();
            ModPackage playing = host.Mod;

            int attempt = 0;
            launcher.Work = (request, run) =>
            {
                Write(request, "logic.luau", BrokenScript + "-- " + (++attempt) + "\n");
                run.End(true);
            };
            studio.Submit("break it");
            for (int i = 0; i < 10 && studio.Busy; i++)
                studio.Update();

            Assert.IsFalse(studio.Busy);
            Assert.AreEqual(1 + 1 + ModStudio.MaxRepairs, launcher.Requests.Count);
            Assert.AreSame(playing, host.Mod);
            StringAssert.Contains("/undo", Text(studio));

            studio.Submit("/undo");
            Assert.AreEqual(CountingScript, host.Mod.Sources["logic.luau"]);
        }

        [Test]
        public void TextAnswersAreWrittenIntoTheFolder()
        {
            AgentSettings settings = new AgentSettings(null, name => null);
            settings.Select("ollama");
            ModStudio studio = Studio(out GameHost host, out FakeLauncher launcher, out string root, settings);
            launcher.Work = (request, run) => run.End(true, string.Empty,
                "Here is the game.\n" + FileBlocks.Format("logic.luau", CountingScript) + FileBlocks.Format("../escape.luau", "return 1") + FileBlocks.Format("notes.txt", "hi"));

            studio.BeginNew();
            studio.Submit("count to five");
            studio.Update();

            Assert.AreEqual(AgentKind.Api, launcher.Profiles[0].Kind);
            StringAssert.Contains("=== FILE: logic.luau ===", launcher.Requests[0].Prompt);
            Assert.IsTrue(launcher.Requests[0].System.Length > 0);
            Assert.AreEqual(CountingScript, host.Mod.Sources["logic.luau"]);
            Assert.IsFalse(File.Exists(Path.Combine(root, "escape.luau")));
            Assert.IsFalse(File.Exists(Path.Combine(studio.Workspace.Directory, "notes.txt")));
            StringAssert.Contains("Agent: Here is the game.", Text(studio));
            StringAssert.Contains("Error: Skipped a file", Text(studio));
        }

        [Test]
        public void AnAnswerWithoutFilesChangesNothing()
        {
            AgentSettings settings = new AgentSettings(null, name => null);
            settings.Select("ollama");
            ModStudio studio = Studio(out GameHost host, out FakeLauncher launcher, out string _, settings);
            launcher.Work = (request, run) => run.End(true, string.Empty, "Which board size do you want?");

            studio.BeginNew();
            studio.Submit("make a game");
            studio.Update();

            Assert.IsNull(host.Session);
            StringAssert.Contains("Agent: Which board size do you want?", Text(studio));
            Assert.AreEqual(1, launcher.Requests.Count);
        }

        [Test]
        public void AnAgentThatIsNotReadyIsNotStarted()
        {
            ModStudio studio = Studio(out GameHost _, out FakeLauncher launcher, out string root);
            launcher.NotReady = "'claude' was not found on this computer.";

            studio.BeginNew();
            studio.Submit("a game");

            Assert.IsFalse(studio.Busy);
            Assert.AreEqual(0, launcher.Requests.Count);
            Assert.AreEqual(0, Directory.GetDirectories(root).Length);
            StringAssert.Contains("Error: 'claude' was not found", Text(studio));
            StringAssert.Contains("npm install", Text(studio));
        }

        [Test]
        public void AFailedAgentIsReportedAndTheGameStays()
        {
            ModStudio studio = Studio(out GameHost host, out FakeLauncher launcher, out string _);
            launcher.Work = (request, run) => run.End(false, "The agent ended with exit code 1.");

            studio.BeginNew();
            studio.Submit("a game");
            studio.Update();

            Assert.IsFalse(studio.Busy);
            Assert.IsNull(host.Session);
            Assert.AreEqual(1, launcher.Requests.Count);
            StringAssert.Contains("Error: The agent ended with exit code 1.", Text(studio));
        }

        [Test]
        public void CancelStopsTheAgent()
        {
            ModStudio studio = Studio(out GameHost _, out FakeLauncher launcher, out string _);
            launcher.Work = (request, run) => { };

            studio.BeginNew();
            studio.Submit("a game");
            studio.Update();
            Assert.IsTrue(studio.Busy);
            studio.Submit("another");
            StringAssert.Contains("still working", Text(studio));
            studio.Submit("/cancel");

            Assert.IsFalse(studio.Busy);
            StringAssert.Contains("Error: Cancelled.", Text(studio));
            Assert.AreEqual(1, launcher.Requests.Count);
        }

        [Test]
        public void CommandsChooseAgentsAndKeepKeysOffTheScreen()
        {
            string path = Path.Combine(NewDirectory(), "agents.json");
            AgentSettings settings = new AgentSettings(path, name => null);
            ModStudio studio = Studio(out GameHost _, out FakeLauncher _, out string _, settings);
            studio.BeginNew();

            studio.Submit("/agent gemini-api");
            studio.Submit("/key AIza-very-secret");
            studio.Submit("/model gemini-other");
            studio.Submit("/agents");
            studio.Submit("/agent nope");
            studio.Submit("/bogus");
            studio.Submit("/help");

            Assert.AreEqual("gemini-api", settings.SelectedId);
            Assert.AreEqual("AIza-very-secret", settings.KeyFor(settings.Selected));
            Assert.AreEqual("gemini-other", settings.ModelFor(settings.Selected));
            StringAssert.DoesNotContain("AIza-very-secret", Text(studio));
            StringAssert.Contains("* gemini-api", Text(studio));
            StringAssert.Contains("There is no agent 'nope'", Text(studio));
            StringAssert.Contains("There is no command '/bogus'", Text(studio));
            StringAssert.Contains("AIza-very-secret", File.ReadAllText(path));

            studio.Submit("/key clear");
            Assert.AreEqual(string.Empty, settings.KeyFor(settings.Selected));
            studio.Submit("/clear");
            Assert.AreEqual(0, studio.Lines.Count);
        }

        [Test]
        public void AnExistingGameCanBeEdited()
        {
            ModStudio studio = Studio(out GameHost host, out FakeLauncher launcher, out string root);
            ModWorkspace.Create(root, "mine", "Mine");
            host.Catalog.Refresh();
            launcher.Work = (request, run) =>
            {
                Write(request, "logic.luau", CountingScript);
                run.End(true);
            };

            Assert.IsTrue(studio.BeginEdit(host.Catalog.Find("mine")));
            studio.Submit("/files");
            studio.Submit("change the rules");
            studio.Update();

            Assert.AreEqual("mine", host.Mod.Manifest.Id);
            Assert.AreEqual(CountingScript, host.Mod.Sources["logic.luau"]);
            StringAssert.Contains("logic.luau", Text(studio));
            Assert.AreEqual(1, Directory.GetDirectories(root).Length);
            Assert.IsFalse(studio.BeginEdit(TestSupport.InlineMod("return {}")));
        }

        [Test]
        public void NewGamesGetIdsThatAreFree()
        {
            ModStudio studio = Studio(out GameHost host, out FakeLauncher launcher, out string root);
            ModWorkspace.Create(root, "my-game-1", "Taken");
            host.Catalog.Refresh();
            launcher.Work = (request, run) =>
            {
                Write(request, "logic.luau", CountingScript);
                run.End(true);
            };

            studio.BeginNew();
            studio.Submit("a game");
            studio.Update();
            studio.Submit("/new");
            studio.Submit("another game");
            studio.Update();

            Assert.AreEqual("my-game-3", studio.Workspace.Id);
            Assert.AreEqual("my-game-3", host.Mod.Manifest.Id);
        }

        private static bool Wait(AgentRun run, int milliseconds = 15000)
        {
            for (int waited = 0; run.IsRunning && waited < milliseconds; waited += 20)
                Thread.Sleep(20);
            return !run.IsRunning;
        }

        private static string Lines(AgentRun run)
        {
            List<AgentLine> lines = new List<AgentLine>();
            run.Drain(lines);
            StringBuilder builder = new StringBuilder();
            foreach (AgentLine line in lines)
                builder.Append(line.Kind).Append(": ").Append(line.Text).Append('\n');
            return builder.ToString();
        }

        // A stand-in for an agent program: it saves what it reads from its input and its arguments, and answers.
        private string FakeProgram(string name, string windowsBody, string unixBody)
        {
            string folder = NewDirectory();
            if (AgentLocator.IsWindows)
            {
                string path = Path.Combine(folder, name + ".cmd");
                File.WriteAllText(path, "@echo off\r\n" + windowsBody.Replace("\n", "\r\n") + "\r\n");
                return path;
            }
            string script = Path.Combine(folder, name);
            File.WriteAllText(script, "#!/bin/sh\n" + unixBody + "\n");
            using (System.Diagnostics.Process chmod = System.Diagnostics.Process.Start("chmod", "+x \"" + script + "\""))
                chmod.WaitForExit();
            return script;
        }

        [Test]
        public void ACommandLineAgentGetsTheRequestOnItsInputAndRunsInTheGameFolder()
        {
            string program = FakeProgram("agent", "findstr \"^\" > received.txt\necho %1 %2 > arguments.txt\necho all done\necho working 1>&2",
                "cat > received.txt\necho \"$1 $2\" > arguments.txt\necho 'all done'\necho working >&2");
            string work = NewDirectory();
            AgentProfile profile = new AgentProfile("fake", "Fake", AgentKind.Cli) { Command = program, KeyVariable = "FAKE_KEY" }.WithArguments("--one", "two");

            using (CliAgentRun run = new CliAgentRun(profile, "k", new AgentRequest(work, "첫 줄 first line\nsecond line\n")))
            {
                Assert.IsTrue(Wait(run), "the agent should finish");
                Assert.IsTrue(run.Succeeded, run.Error);
                string lines = Lines(run);
                StringAssert.Contains("Output: all done", lines);
                StringAssert.Contains("Notice: working", lines);
            }
            string received = File.ReadAllText(Path.Combine(work, "received.txt"), Encoding.UTF8);
            StringAssert.Contains("첫 줄 first line", received);
            StringAssert.Contains("second line", received);
            StringAssert.Contains("--one two", File.ReadAllText(Path.Combine(work, "arguments.txt")));
        }

        [Test]
        public void AFileDrivenAgentFindsTheRequestInTheFolder()
        {
            string program = FakeProgram("agent", "echo %~1 > argument.txt", "echo \"$1\" > argument.txt");
            string work = NewDirectory();
            AgentProfile profile = new AgentProfile("fake", "Fake", AgentKind.Cli) { Command = program, Channel = PromptChannel.File }.WithArguments(AgentProfile.PromptPlaceholder);

            using (CliAgentRun run = new CliAgentRun(profile, string.Empty, new AgentRequest(work, "the whole request; rm -rf \"$HOME\" & echo %PATH%")))
            {
                Assert.IsTrue(Wait(run));
                Assert.IsTrue(run.Succeeded, run.Error);
            }
            string request = Path.Combine(work, CliAgentRun.RequestFile.Replace('/', Path.DirectorySeparatorChar));
            StringAssert.Contains("the whole request", File.ReadAllText(request));
            Assert.AreEqual(CliAgentRun.RequestPointer, File.ReadAllText(Path.Combine(work, "argument.txt")).Trim());
        }

        [Test]
        public void AFailingOrMissingProgramIsReported()
        {
            string program = FakeProgram("agent", "echo no login 1>&2\nexit /b 3", "echo 'no login' >&2\nexit 3");
            string work = NewDirectory();

            using (CliAgentRun failing = new CliAgentRun(new AgentProfile("fake", "Fake", AgentKind.Cli) { Command = program }, string.Empty, new AgentRequest(work, "x")))
            {
                Assert.IsTrue(Wait(failing));
                Assert.IsFalse(failing.Succeeded);
                StringAssert.Contains("exit code 3", failing.Error);
                StringAssert.Contains("Notice: no login", Lines(failing));
            }
            using (CliAgentRun missing = new CliAgentRun(new AgentProfile("fake", "Fake", AgentKind.Cli) { Command = "bse-no-such-agent" }, string.Empty, new AgentRequest(work, "x")))
            {
                Assert.IsFalse(missing.IsRunning);
                Assert.IsFalse(missing.Succeeded);
                StringAssert.Contains("was not found", missing.Error);
            }
            AgentLauncher launcher = new AgentLauncher();
            AgentSettings settings = new AgentSettings(null, name => null);
            StringAssert.Contains("was not found", launcher.Readiness(new AgentProfile("fake", "Fake", AgentKind.Cli) { Command = "bse-no-such-agent" }, settings));
            Assert.AreEqual(string.Empty, launcher.Readiness(new AgentProfile("fake", "Fake", AgentKind.Cli) { Command = program }, settings));
            StringAssert.Contains("/key", launcher.Readiness(settings.Find("gemini-api"), settings));
            Assert.AreEqual(string.Empty, launcher.Readiness(settings.Find("ollama"), settings));
        }

        [Test]
        public void ARunningAgentCanBeCancelledAndTimesOut()
        {
            string program = FakeProgram("agent", "ping -n 30 127.0.0.1 > nul", "sleep 30");
            string work = NewDirectory();
            AgentProfile profile = new AgentProfile("fake", "Fake", AgentKind.Cli) { Command = program };

            using (CliAgentRun run = new CliAgentRun(profile, string.Empty, new AgentRequest(work, "x")))
            {
                Thread.Sleep(200);
                Assert.IsTrue(run.IsRunning);
                run.Cancel();
                Assert.IsTrue(Wait(run, 5000));
                Assert.IsTrue(run.Cancelled);
                Assert.AreEqual("Cancelled.", run.Error);
            }
            using (CliAgentRun slow = new CliAgentRun(profile, string.Empty, new AgentRequest(work, "x") { TimeoutSeconds = 0.3 }))
            {
                Assert.IsTrue(Wait(slow, 5000));
                Assert.IsFalse(slow.Succeeded);
                StringAssert.Contains("in time", slow.Error);
            }
        }

        [Test]
        public void ProgramsAreFoundOnTheSearchPath()
        {
            string program = FakeProgram("bse-fake-agent", "echo hi", "echo hi");
            string folder = Path.GetDirectoryName(program);

            Assert.AreEqual(Path.GetFullPath(program), AgentLocator.Find("bse-fake-agent", "nowhere" + Path.PathSeparator + folder, null));
            Assert.AreEqual(Path.GetFullPath(program), AgentLocator.Find(program, string.Empty, null));
            Assert.IsNull(AgentLocator.Find("bse-fake-agent", "nowhere", null));
            Assert.IsNull(AgentLocator.Find("  ", folder, null));
        }

        [Test]
        public void ChatAnswersAreReadFromTheFirstChoice()
        {
            Assert.AreEqual("hello", ApiAgentRun.ReplyText("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hello\"}}]}", out string none));
            Assert.AreEqual(string.Empty, none);
            Assert.AreEqual("ab", ApiAgentRun.ReplyText("{\"choices\":[{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"a\"},{\"type\":\"text\",\"text\":\"b\"}]}}]}", out string _));

            Assert.IsNull(ApiAgentRun.ReplyText("{\"error\":{\"message\":\"quota exceeded\"}}", out string quota));
            StringAssert.Contains("quota exceeded", quota);
            Assert.IsNull(ApiAgentRun.ReplyText("[{\"error\":{\"message\":\"bad key\"}}]", out string wrapped));
            StringAssert.Contains("bad key", wrapped);
            Assert.IsNull(ApiAgentRun.ReplyText("<html>", out string html));
            StringAssert.Contains("not JSON", html);
            Assert.IsNull(ApiAgentRun.ReplyText("{\"choices\":[]}", out string empty));
            StringAssert.Contains("without text", empty);
        }

        // A chat endpoint on this computer that records the request and answers with what the test gives it.
        private sealed class FakeEndpoint : IDisposable
        {
            private readonly HttpListener _listener = new HttpListener();
            private readonly Thread _thread;

            public string Address { get; }
            public string Body { get; private set; }
            public string Authorization { get; private set; }

            public FakeEndpoint(int status, string answer)
            {
                TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                Address = "http://127.0.0.1:" + port + "/v1/chat/completions";
                _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                _listener.Start();
                _thread = new Thread(() =>
                {
                    try
                    {
                        HttpListenerContext context = _listener.GetContext();
                        using (StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                            Body = reader.ReadToEnd();
                        Authorization = context.Request.Headers["Authorization"];
                        byte[] bytes = Encoding.UTF8.GetBytes(answer);
                        context.Response.StatusCode = status;
                        context.Response.ContentType = "application/json";
                        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        context.Response.Close();
                    }
                    catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException || e is InvalidOperationException)
                    {
                    }
                }) { IsBackground = true };
                _thread.Start();
            }

            public void Dispose()
            {
                _listener.Close();
                _thread.Join(2000);
            }
        }

        [Test]
        public void AChatEndpointIsAskedWithTheKeyAndTheModel()
        {
            string answer = JsonValue.NewObject().Set("choices", JsonValue.NewArray().Add(JsonValue.NewObject().Set("message",
                JsonValue.NewObject().Set("role", "assistant").Set("content", "안녕\n" + FileBlocks.Format("logic.luau", "return {}"))))).ToJson();
            using (FakeEndpoint endpoint = new FakeEndpoint(200, answer))
            using (ApiAgentRun run = new ApiAgentRun(endpoint.Address, "test-model", "secret-key", new AgentRequest(NewDirectory(), "make a game 게임", "be brief")))
            {
                Assert.IsTrue(Wait(run));
                Assert.IsTrue(run.Succeeded, run.Error);
                StringAssert.Contains("안녕", run.Reply);
                Assert.AreEqual(1, FileBlocks.Parse(run.Reply).Files.Count);

                JsonValue sent = JsonValue.Parse(endpoint.Body);
                Assert.AreEqual("test-model", sent["model"].AsString());
                Assert.AreEqual("system", sent["messages"][0]["role"].AsString());
                Assert.AreEqual("be brief", sent["messages"][0]["content"].AsString());
                Assert.AreEqual("make a game 게임", sent["messages"][1]["content"].AsString());
                Assert.AreEqual("Bearer secret-key", endpoint.Authorization);
            }
        }

        [Test]
        public void ErrorsOfAChatEndpointAreReported()
        {
            using (FakeEndpoint endpoint = new FakeEndpoint(429, "{\"error\":{\"message\":\"You exceeded your current quota\"}}"))
            using (ApiAgentRun run = new ApiAgentRun(endpoint.Address, "m", string.Empty, new AgentRequest(NewDirectory(), "x")))
            {
                Assert.IsTrue(Wait(run));
                Assert.IsFalse(run.Succeeded);
                StringAssert.Contains("429", run.Error);
                StringAssert.Contains("exceeded your current quota", run.Error);
                Assert.IsNull(endpoint.Authorization);
            }
            using (ApiAgentRun bad = new ApiAgentRun("not a url", "m", string.Empty, new AgentRequest(NewDirectory(), "x")))
            {
                Assert.IsFalse(bad.IsRunning);
                StringAssert.Contains("not a web address", bad.Error);
            }
            using (ApiAgentRun unreachable = new ApiAgentRun("http://127.0.0.1:9/v1/chat/completions", "m", string.Empty, new AgentRequest(NewDirectory(), "x")))
            {
                Assert.IsTrue(Wait(unreachable));
                Assert.IsFalse(unreachable.Succeeded);
                Assert.IsTrue(unreachable.Error.Length > 0);
            }
        }
    }
}
