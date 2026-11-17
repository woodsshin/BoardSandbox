using System;
using System.Collections.Generic;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Mods;
using BoardSandbox.Core.Net;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class NetTests
    {
        private const string RaceScript = @"
local rules = {}
local count = 0
function rules.new_game(options)
    count = 0
    bse.view.spawn('mark', options.seed or 0, 1)
    bse.turn(1)
end
function rules.command(seat, cmd)
    if cmd.action == 'win' then
        bse.over({ winner = 1, reason = 'done' })
        return nil
    end
    count += 1
    bse.view.spawn('step', count, 2)
    return nil
end
function rules.snapshot() return { count = count } end
function rules.restore(data) count = data.count end
return rules";

        private sealed class ListSource : IModSource
        {
            private readonly ModPackage[] _mods;

            public ListSource(params ModPackage[] mods)
            {
                _mods = mods;
            }

            public ModOrigin Origin => ModOrigin.Local;
            public string Description => "memory";

            public ModDiscovery Discover()
            {
                ModDiscovery found = new ModDiscovery();
                found.Mods.AddRange(_mods);
                return found;
            }
        }

        private static ModPackage Race(string script = RaceScript, int players = 3)
        {
            string manifest = "{\"id\":\"race\",\"name\":\"Race\",\"api\":1,\"entry\":\"main.luau\",\"players\":{\"min\":1,\"max\":1},\"options\":{\"seed\":0}," +
                              "\"multiplayer\":{\"mode\":\"split\",\"players\":" + players + ",\"seed_option\":\"seed\"}}";
            return new ModPackage(manifest, new Dictionary<string, string> { { "main.luau", script } });
        }

        private static GameHost Host(params ModPackage[] mods)
        {
            return mods.Length == 0 ? new GameHost(new ModCatalog(new[] { TestSupport.ModsRoot })) : new GameHost(new ModCatalog(new IModSource[] { new ListSource(mods) }));
        }

        private static void Pump(params NetRoom[] rooms)
        {
            for (int round = 0; round < 4; round++)
            {
                foreach (NetRoom room in rooms)
                    room.Update();
            }
            foreach (NetRoom room in rooms)
            {
                foreach (NetBoard board in room.Boards)
                {
                    for (int i = 0; i < 400 && board.Queue.IsBusy; i++)
                        board.Queue.Update(0.1);
                }
            }
        }

        private static JsonValue Place(int x, int y)
        {
            return JsonValue.NewObject().Set("action", "place_stone").Set("x", x).Set("y", y);
        }

        // The classic sudoku puzzle and its solution, 81 digits each (0 is an empty cell).
        private const string SudokuPuzzle = "530070000600195000098000060800060003400803001700020006060000280000419005000080079";
        private const string SudokuSolved = "534678912672195348198342567859761423426853791713924856961537284287419635345286179";

        private static JsonValue PutDigit(int x, int y, int digit)
        {
            return JsonValue.NewObject().Set("action", "place").Set("x", x).Set("y", y).Set("digit", digit);
        }

        private static JsonValue Choose(int digit)
        {
            return JsonValue.NewObject().Set("action", "select").Set("digit", digit);
        }

        private static ViewObject DigitAt(ViewModel view, int x, int y)
        {
            foreach (ViewObject o in view.Objects)
            {
                if (o.Kind == "digit" && (int)o.X == x && (int)o.Y == y)
                    return o;
            }
            return null;
        }

        private static string KeyColor(ViewModel view, string label)
        {
            foreach (ViewObject o in view.Objects)
            {
                if (o.Kind == "key" && o.Props["label"].AsString() == label)
                    return o.Props["color"].AsString();
            }
            return null;
        }

        private static string Owners(GameHost host)
        {
            return host.Session.Query("state")["owners"].AsString();
        }

        private static string Table(ViewModel view)
        {
            JsonValue all = JsonValue.NewArray();
            foreach (ViewEvent e in view.Synthesize())
                all.Add(e.Raw);
            return all.ToJson();
        }

        // A game of the given manifest block, played by one or two seats, whose rules do nothing.
        private static ModPackage Custom(string multiplayer)
        {
            string manifest = "{\"id\":\"free\",\"name\":\"Free\",\"api\":1,\"entry\":\"main.luau\",\"players\":{\"min\":1,\"max\":2}," +
                              "\"multiplayer\":" + multiplayer + "}";
            return new ModPackage(manifest, new Dictionary<string, string> { { "main.luau", "local rules = {} function rules.new_game() bse.turn(1) end function rules.command() end return rules" } });
        }

        [Test]
        public void TheManifestSaysHowAGameIsShared()
        {
            ModManifest gomoku = TestSupport.LoadGomoku().Manifest;
            Assert.IsFalse(gomoku.SplitScreen);
            Assert.IsFalse(gomoku.Cooperative);
            Assert.AreEqual(2, gomoku.RoomPlayers);
            Assert.AreEqual("", gomoku.SeedOption);
            ModManifest race = Race().Manifest;
            Assert.IsTrue(race.SplitScreen);
            Assert.AreEqual(3, race.RoomPlayers);
            Assert.AreEqual("seed", race.SeedOption);
            Assert.AreEqual(ModManifest.MaxRoomPlayers, Race(RaceScript, 99).Manifest.RoomPlayers);
            Assert.AreEqual(1, Race(RaceScript, -4).Manifest.RoomPlayers);
        }

        [Test]
        public void LoopbackPeersExchangeMessagesInOrder()
        {
            LoopbackNetwork network = new LoopbackNetwork();
            Assert.Throws<InvalidOperationException>(() => network.Join("x"));
            INetTransport host = network.Host("Ann");
            Assert.Throws<InvalidOperationException>(() => network.Host("x"));
            INetTransport guest = network.Join("Bob");
            Assert.IsTrue(host.IsHost);
            Assert.IsFalse(guest.IsHost);
            Assert.AreEqual(host.LocalId, guest.HostId);
            Assert.IsTrue(host.Poll(out NetEvent joined));
            Assert.AreEqual(NetEventKind.PeerJoined, joined.Kind);
            Assert.AreEqual("Bob", joined.Name);

            byte[] data = { 1, 2, 3 };
            Assert.IsTrue(guest.Send(host.LocalId, data));
            Assert.IsTrue(guest.Send(host.LocalId, new byte[] { 4 }));
            data[0] = 9;
            Assert.IsTrue(host.Poll(out NetEvent first));
            Assert.AreEqual(1, first.Data[0]);
            Assert.AreEqual(guest.LocalId, first.Peer);
            Assert.IsTrue(host.Poll(out NetEvent second));
            Assert.AreEqual(4, second.Data[0]);
            Assert.IsFalse(host.Poll(out _));
            Assert.IsFalse(guest.Send(77, data));

            host.Dispose();
            Assert.IsTrue(guest.Poll(out _));
            Assert.IsTrue(guest.Poll(out NetEvent left));
            Assert.AreEqual(NetEventKind.PeerLeft, left.Kind);
            Assert.IsTrue(guest.Poll(out NetEvent closed));
            Assert.AreEqual(NetEventKind.Closed, closed.Kind);
            Assert.IsFalse(guest.Send(host.LocalId, data));
        }

        [Test]
        public void ASharedGameIsPlayedAcrossTheRoom()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                host.SetOption("size", JsonValue.Of(9));
                Assert.IsTrue(host.StartNewGame());
                Assert.IsTrue(room.CanAct);
                room.Submit(Place(5, 5));

                NetClientRoom guest = new NetClientRoom(host.Mod, network.Join("Bob"));
                Assert.IsFalse(guest.Joined);
                Assert.IsFalse(guest.Ready);
                Pump(room, guest);
                Assert.IsTrue(guest.Joined);
                Assert.IsTrue(guest.Ready);
                Assert.AreEqual(2, guest.Players.Count);
                Assert.AreEqual("Ann", guest.Players[0].Name);
                Assert.AreEqual(1, guest.Players[0].Seat);
                Assert.AreEqual(2, guest.LocalPlayer.Seat);
                Assert.AreEqual(1, guest.Boards.Count);
                ViewModel shown = guest.LocalBoard.Queue.Display;
                Assert.AreEqual(1, shown.ObjectCount);
                Assert.AreEqual(2, shown.Turn);
                Assert.AreEqual(9, shown.GetCustom("board").Raw["columns"].AsInt());

                // The host waits for the guest now.
                Assert.IsFalse(room.CanAct);
                room.Submit(Place(1, 1));
                Assert.AreEqual(1, host.Session.View.ObjectCount);
                Assert.IsTrue(guest.CanAct);
                Assert.AreEqual(2, guest.ActingSeat);
                guest.Submit(Place(4, 4));
                Pump(room, guest);
                Assert.AreEqual(2, host.Session.View.ObjectCount);
                Assert.AreEqual(2, shown.ObjectCount);
                Assert.AreEqual(1, shown.Turn);
                Assert.IsFalse(guest.CanAct);
                Assert.IsTrue(room.CanAct);

                // Taking a move back on the host shows everywhere.
                Assert.IsTrue(host.Undo());
                Pump(room, guest);
                Assert.AreEqual(1, shown.ObjectCount);
                Assert.AreEqual(2, shown.Turn);

                // A new game as well.
                Assert.IsTrue(host.StartNewGame());
                Pump(room, guest);
                Assert.AreEqual(0, shown.ObjectCount);
                Assert.AreEqual(1, shown.Turn);

                room.Dispose();
                Pump(guest);
                Assert.IsTrue(guest.IsClosed);
                Assert.IsTrue(room.IsClosed);
                StringAssert.Contains("host", guest.ClosedReason);
            }
        }

        [Test]
        public void TheManifestSaysWhetherTheSeatsPlayTogether()
        {
            Assert.IsTrue(Custom("{\"cooperative\":true}").Manifest.Cooperative);
            Assert.IsFalse(Custom("{\"cooperative\":false}").Manifest.Cooperative);
            Assert.IsFalse(Custom("{}").Manifest.Cooperative);
            Assert.IsFalse(Custom("{\"cooperative\":\"yes\"}").Manifest.Cooperative);

            // Every player of a split game has a table of their own, so there is nothing to play together.
            ModManifest split = Custom("{\"mode\":\"split\",\"cooperative\":true}").Manifest;
            Assert.IsTrue(split.SplitScreen);
            Assert.IsFalse(split.Cooperative);

            ModManifest sudoku = TestSupport.LoadMod("sudoku").Manifest;
            Assert.IsTrue(sudoku.Cooperative);
            Assert.AreEqual(2, sudoku.RoomPlayers);
        }

        [Test]
        public void ACooperativeGameIsPlayedByBothSeatsAtOnce()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("sudoku"));
                host.SetOption("puzzle", JsonValue.Of(SudokuPuzzle));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                NetClientRoom cy = new NetClientRoom(host.Mod, network.Join("Cy"));
                Pump(room, bob, cy);

                Assert.AreEqual(1, room.LocalPlayer.Seat);
                Assert.AreEqual(2, bob.LocalPlayer.Seat);
                Assert.AreEqual(0, cy.LocalPlayer.Seat);
                Assert.AreEqual(1, host.Session.View.Turn, "the game names a seat, but nobody waits for it");

                // Both seats may act, and the one who watches may not.
                Assert.IsTrue(room.CanAct);
                Assert.AreEqual(1, room.ActingSeat);
                Assert.IsTrue(bob.CanAct);
                Assert.AreEqual(2, bob.ActingSeat);
                Assert.IsFalse(cy.CanAct);
                Assert.AreEqual(0, cy.ActingSeat);

                // The second seat goes first and twice in a row; the host plays in between.
                bob.Submit(PutDigit(3, 1, 4));
                bob.Submit(PutDigit(4, 1, 6));
                Pump(room, bob, cy);
                room.Submit(PutDigit(7, 1, 9));
                bob.Submit(PutDigit(8, 1, 1));
                Pump(room, bob, cy);
                Assert.AreEqual("002200120", Owners(host).Substring(0, 9));
                Assert.AreEqual(2, DigitAt(host.Session.View, 3, 1).Owner);
                Assert.AreEqual(1, DigitAt(host.Session.View, 7, 1).Owner);

                // A watcher can not act, and a command that it sends anyway is turned down by the host with a note.
                cy.Submit(PutDigit(9, 1, 2));
                INetTransport eve = network.Join("Eve");
                Func<string, bool> send = text => eve.Send(eve.HostId, System.Text.Encoding.UTF8.GetBytes(text));
                send("{\"t\":\"hello\",\"proto\":1,\"mod\":\"sudoku\",\"hash\":\"" + host.Mod.ContentHash + "\",\"name\":\"Eve\"}");
                Pump(room, bob, cy);
                Assert.AreEqual(0, room.Players[3].Seat);
                send("{\"t\":\"cmd\",\"command\":{\"action\":\"place\",\"x\":9,\"y\":1,\"digit\":2}}");
                Pump(room, bob, cy);
                bool told = false;
                while (eve.Poll(out NetEvent reply))
                    told |= reply.Kind == NetEventKind.Data && System.Text.Encoding.UTF8.GetString(reply.Data).Contains("watching this game");
                Assert.IsTrue(told);
                Assert.AreEqual('0', host.Session.Query("state")["board"].AsString()[8]);

                // Everybody sees the same table, and each seat's choice on the pad is shown in its color.
                room.Submit(Choose(5));
                bob.Submit(Choose(7));
                Pump(room, bob, cy);
                Assert.AreEqual("[5,7]", host.Session.Query("state")["chosen"].ToJson());
                string table = Table(host.Session.View);
                Assert.AreEqual(table, Table(bob.LocalBoard.Queue.Display));
                Assert.AreEqual(table, Table(cy.LocalBoard.Queue.Display));
                Assert.AreEqual("#4d8fe6", KeyColor(cy.LocalBoard.Queue.Display, "5"));
                Assert.AreEqual("#e68a2e", KeyColor(cy.LocalBoard.Queue.Display, "7"));

                // The host's undo takes back the last move of whoever made it.
                Assert.IsTrue(host.Undo(2));
                Pump(room, bob, cy);
                Assert.AreEqual("[0,0]", host.Session.Query("state")["chosen"].ToJson());
                Assert.AreEqual(Table(host.Session.View), Table(bob.LocalBoard.Queue.Display));

                // A late guest takes a free seat and plays on at once.
                bob.Dispose();
                Pump(room, cy);
                Assert.AreEqual(2, cy.LocalPlayer.Seat);
                Assert.IsTrue(cy.CanAct);
                cy.Submit(PutDigit(9, 1, 2));
                Pump(room, cy);
                Assert.AreEqual("002200122", Owners(host).Substring(0, 9));
                room.Dispose();
            }
        }

        [Test]
        public void TheGameIsOverForBothSeatsOnceThePuzzleIsSolved()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("sudoku"));
                host.SetOption("puzzle", JsonValue.Of(SudokuPuzzle));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                Pump(room, bob);

                int k = 0;
                for (int i = 0; i < 81; i++)
                {
                    if (SudokuPuzzle[i] != '0')
                        continue;
                    JsonValue move = PutDigit(i % 9 + 1, i / 9 + 1, SudokuSolved[i] - '0');
                    if (k++ % 2 == 0)
                        room.Submit(move);
                    else
                        bob.Submit(move);
                    Pump(room, bob);
                }
                Assert.IsTrue(host.Session.View.IsOver);
                Assert.IsTrue(bob.LocalBoard.Queue.Display.IsOver);
                Assert.IsFalse(room.CanAct);
                Assert.IsFalse(bob.CanAct);
                Assert.AreEqual(0, room.ActingSeat);
                Assert.AreEqual(0, bob.ActingSeat);
                room.Dispose();
            }
        }

        [Test]
        public void ACooperativeSeatThatTheComputerPlaysIsLeftToTheComputer()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod(TestSupport.LoadSudokuWithComputer()));
                host.SetOption("puzzle", JsonValue.Of(SudokuPuzzle));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                Pump(room, bob);

                // The host hands its own seat to the computer: it does not play it any more, the guest does.
                Assert.IsTrue(host.SetSeatMode(1, SeatMode.Random));
                Assert.IsFalse(room.CanAct);
                Assert.AreEqual(0, room.ActingSeat);
                room.Submit(PutDigit(3, 1, 4));
                Assert.AreEqual('0', host.Session.Query("state")["board"].AsString()[2]);
                Assert.IsTrue(bob.CanAct);
                bob.Submit(PutDigit(3, 1, 4));
                Pump(room, bob);
                Assert.AreEqual('4', host.Session.Query("state")["board"].AsString()[2]);

                // And the other way round: a guest in a seat that the computer plays is not heard.
                Assert.IsTrue(host.SetSeatMode(1, SeatMode.Human));
                Assert.IsTrue(host.SetSeatMode(2, SeatMode.Random));
                bob.Submit(PutDigit(4, 1, 6));
                Pump(room, bob);
                Assert.AreEqual('0', host.Session.Query("state")["board"].AsString()[3]);
                Assert.IsTrue(room.CanAct);
                room.Dispose();
            }
        }

        [Test]
        public void LatecomersWatchAndMoveUpWhenASeatFrees()
        {
            using (GameHost host = Host())
            {
                host.SelectMod("gomoku");
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                host.StartNewGame();
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                Pump(room, bob);
                NetClientRoom cy = new NetClientRoom(host.Mod, network.Join("Cy"));
                List<string> notes = new List<string>();
                cy.Notice += (text, level) => notes.Add(text);
                Pump(room, bob, cy);

                Assert.AreEqual(3, cy.Players.Count);
                Assert.AreEqual(0, cy.LocalPlayer.Seat);
                Assert.AreEqual(3, bob.Players.Count);
                room.Submit(Place(5, 5));
                Pump(room, bob, cy);
                Assert.AreEqual(1, cy.LocalBoard.Queue.Display.ObjectCount);
                Assert.IsFalse(cy.CanAct);
                Assert.AreEqual(0, cy.ActingSeat);
                cy.Submit(Place(6, 6));
                Pump(room, bob, cy);
                Assert.AreEqual(1, host.Session.View.ObjectCount);

                bob.Dispose();
                Pump(room, cy);
                Assert.AreEqual(2, room.Players.Count);
                Assert.AreEqual(2, cy.LocalPlayer.Seat);
                Assert.IsTrue(cy.CanAct);
                cy.Submit(Place(6, 6));
                Pump(room, cy);
                Assert.AreEqual(2, host.Session.View.ObjectCount);
                Assert.AreEqual(0, notes.Count);
                room.Dispose();
            }
        }

        [Test]
        public void TheHostPlaysTheSeatsNobodyTook()
        {
            using (GameHost host = Host())
            {
                host.SelectMod("gomoku");
                NetHostRoom room = new NetHostRoom(host, new LoopbackNetwork().Host("Ann"));
                host.StartNewGame();
                room.Submit(Place(5, 5));
                Assert.IsTrue(room.CanAct);
                Assert.AreEqual(2, room.ActingSeat);
                room.Submit(Place(4, 4));
                Assert.AreEqual(2, host.Session.View.ObjectCount);
                room.Dispose();
            }
        }

        [Test]
        public void TheHostDoesNotTrustWhatItReceives()
        {
            using (GameHost host = Host())
            {
                host.SelectMod("gomoku");
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                host.StartNewGame();
                INetTransport raw = network.Join("Eve\n\u0001 with a very long name that goes on and on and on");
                ulong to = raw.HostId;
                Func<string, bool> send = text => raw.Send(to, System.Text.Encoding.UTF8.GetBytes(text));

                // Before saying hello nothing counts.
                send("{\"t\":\"cmd\",\"command\":{\"action\":\"place_stone\",\"x\":5,\"y\":5}}");
                send("not json");
                send("[1,2]");
                raw.Send(to, new byte[NetRoom.MaxClientMessageBytes + 1]);
                Pump(room);
                Assert.AreEqual(1, room.Players.Count);
                Assert.AreEqual(0, host.Session.View.ObjectCount);

                send("{\"t\":\"hello\",\"proto\":99,\"mod\":\"gomoku\",\"hash\":\"" + host.Mod.ContentHash + "\"}");
                send("{\"t\":\"hello\",\"proto\":1,\"mod\":\"gomoku\",\"hash\":\"nope\"}");
                Pump(room);
                Assert.AreEqual(1, room.Players.Count);

                send("{\"t\":\"hello\",\"proto\":1,\"mod\":\"gomoku\",\"hash\":\"" + host.Mod.ContentHash + "\",\"name\":\"Eve\\n\\u0001 with a very long name that goes on and on and on\"}");
                Pump(room);
                Assert.AreEqual(2, room.Players.Count);
                Assert.LessOrEqual(room.Players[1].Name.Length, NetRoom.MaxNameLength);
                Assert.IsFalse(room.Players[1].Name.Contains("\n"));
                Assert.AreEqual(2, room.Players[1].Seat);

                // Out of turn, for the wrong seat and nonsense.
                send("{\"t\":\"cmd\",\"command\":{\"action\":\"place_stone\",\"x\":5,\"y\":5}}");
                send("{\"t\":\"cmd\",\"command\":5}");
                send("{\"t\":\"cmd\",\"seat\":1,\"command\":{\"action\":\"place_stone\",\"x\":5,\"y\":5}}");
                send("{\"t\":\"hello\",\"proto\":1}");
                Pump(room);
                Assert.AreEqual(0, host.Session.View.ObjectCount);
                Assert.AreEqual(2, room.Players.Count);

                room.Submit(Place(5, 5));
                for (int i = 0; i < 200; i++)
                    send("{\"t\":\"cmd\",\"command\":{\"action\":\"place_stone\",\"x\":" + (1 + i % 9) + ",\"y\":1}}");
                room.Update();
                Assert.AreEqual(2, host.Session.View.ObjectCount);
                room.Dispose();
            }
        }

        [Test]
        public void ADifferentCopyOfTheGameIsTurnedAway()
        {
            using (GameHost host = Host(Race()))
            {
                host.SelectMod("race");
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                host.StartNewGame();
                NetClientRoom guest = new NetClientRoom(Race(RaceScript + "\n-- changed"), network.Join("Bob"));
                Pump(room, guest);
                Assert.IsTrue(guest.IsClosed);
                StringAssert.Contains("differs", guest.ClosedReason);
                Assert.IsFalse(guest.Joined);
                Assert.AreEqual(1, room.Players.Count);
                room.Dispose();
            }
        }

        [Test]
        public void ASplitGameGivesEveryPlayerATableWithTheSameStart()
        {
            using (GameHost host = Host(Race()))
            {
                host.SelectMod("race");
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                host.StartNewGame();
                Assert.AreEqual(0, host.NewGameOptions["seed"].AsInt(-1));
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                Pump(room, bob);
                NetClientRoom cy = new NetClientRoom(host.Mod, network.Join("Cy"));
                Pump(room, bob, cy);

                foreach (NetRoom each in new NetRoom[] { room, bob, cy })
                {
                    Assert.AreEqual(3, each.Boards.Count);
                    Assert.AreEqual(each.LocalId, each.LocalBoard.Owner);
                    Assert.IsTrue(each.CanAct);
                }
                Assert.AreEqual("Bob", cy.Boards[1].Title);

                // The seed nobody chose is the same on every table.
                double seed = FirstX(room.Boards[0]);
                Assert.Greater(seed, 0);
                foreach (NetRoom each in new NetRoom[] { room, bob, cy })
                {
                    foreach (NetBoard board in each.Boards)
                        Assert.AreEqual(seed, FirstX(board));
                }

                JsonValue step = JsonValue.NewObject().Set("action", "step");
                bob.Submit(step);
                bob.Submit(step);
                room.Submit(step);
                Pump(room, bob, cy);
                foreach (NetRoom each in new NetRoom[] { room, bob, cy })
                {
                    Assert.AreEqual(2, each.Boards[0].Queue.Display.ObjectCount);
                    Assert.AreEqual(3, each.Boards[1].Queue.Display.ObjectCount);
                    Assert.AreEqual(1, each.Boards[2].Queue.Display.ObjectCount);
                }

                // A fourth player finds no table left and watches.
                NetClientRoom dee = new NetClientRoom(host.Mod, network.Join("Dee"));
                Pump(room, bob, cy, dee);
                Assert.IsNull(dee.LocalBoard);
                Assert.IsFalse(dee.CanAct);
                Assert.AreEqual(3, dee.Boards.Count);
                Assert.AreEqual(3, dee.Boards[1].Queue.Display.ObjectCount);
                dee.Submit(step);

                // Whoever finishes first ends the race for all.
                List<string> heard = new List<string>();
                bob.Notice += (text, level) => heard.Add(text);
                cy.Submit(JsonValue.NewObject().Set("action", "win"));
                Pump(room, bob, cy, dee);
                Assert.IsTrue(room.Finished && bob.Finished && cy.Finished && dee.Finished);
                Assert.AreEqual(1, heard.Count);
                StringAssert.Contains("Cy", heard[0]);
                Assert.IsFalse(room.CanAct);
                Assert.IsFalse(bob.CanAct);
                bob.Submit(step);
                Pump(room, bob, cy, dee);
                Assert.AreEqual(3, room.Boards[1].Queue.Display.ObjectCount);

                // A leaving player takes the table along, and the watcher gets one.
                int version = cy.BoardsVersion;
                bob.Dispose();
                Pump(room, cy, dee);
                Assert.AreEqual(3, cy.Boards.Count);
                Assert.Greater(cy.BoardsVersion, version);
                Assert.IsNotNull(dee.LocalBoard);
                Assert.AreEqual("Dee", cy.Boards[2].Title);

                // The next game starts everyone again, with another seed.
                Assert.IsTrue(host.StartNewGame());
                Pump(room, cy, dee);
                Assert.IsFalse(room.Finished || cy.Finished || dee.Finished);
                double next = FirstX(room.Boards[0]);
                foreach (NetBoard board in dee.Boards)
                {
                    Assert.AreEqual(1, board.Queue.Display.ObjectCount);
                    Assert.AreEqual(next, FirstX(board));
                }
                Assert.IsTrue(dee.CanAct);
                room.Dispose();
            }
        }

        private static double FirstX(NetBoard board)
        {
            foreach (ViewObject piece in board.Queue.Display.Objects)
            {
                if (piece.Kind == "mark")
                    return piece.X;
            }
            return -1;
        }

        [Test]
        public void AChosenSeedIsKept()
        {
            using (GameHost host = Host(Race()))
            {
                host.SelectMod("race");
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                host.SetOption("seed", JsonValue.Of(42));
                host.StartNewGame();
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                Pump(room, bob);
                Assert.AreEqual(42, host.NewGameOptions["seed"].AsInt());
                Assert.AreEqual(42.0, FirstX(bob.Boards[0]));
                Assert.AreEqual(42.0, FirstX(bob.Boards[1]));
                room.Dispose();
            }
        }

        [Test]
        public void ChoosingAnotherGameClosesTheRoom()
        {
            using (GameHost host = Host())
            {
                host.SelectMod("gomoku");
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                host.StartNewGame();
                NetClientRoom guest = new NetClientRoom(host.Mod, network.Join("Bob"));
                Pump(room, guest);

                // The same game again keeps it open.
                Assert.IsTrue(host.StartGame(host.Mod));
                host.Submit(1, Place(3, 3));
                Pump(room, guest);
                Assert.IsFalse(room.IsClosed);
                Assert.AreEqual(1, guest.LocalBoard.Queue.Display.ObjectCount);

                Assert.IsTrue(host.SelectMod("cubic3"));
                Pump(room, guest);
                Assert.IsTrue(room.IsClosed);
                Assert.IsTrue(guest.IsClosed);
            }
        }

        private sealed class FakeBackend : ILobbyBackend
        {
            public readonly LoopbackNetwork Network;
            public readonly List<LobbyInfo> Open;
            private readonly List<LobbyInfo> _found = new List<LobbyInfo>();
            private INetTransport _ready;
            private Action _pending;

            public FakeBackend(string name, LoopbackNetwork network, List<LobbyInfo> open)
            {
                LocalName = name;
                Network = network;
                Open = open;
            }

            public bool Available { get; set; } = true;
            public string Status => Available ? "" : "offline";
            public string LocalName { get; }
            public bool Busy => _pending != null;
            public IReadOnlyList<LobbyInfo> Lobbies => _found;
            public string Error;
            public bool Disposed;

            public void Update()
            {
                Action run = _pending;
                _pending = null;
                run?.Invoke();
            }

            public void RequestList(string modId)
            {
                _pending = () =>
                {
                    _found.Clear();
                    _found.AddRange(Open.FindAll(lobby => lobby.ModId == modId));
                };
            }

            public void Create(LobbyInfo description)
            {
                _pending = () =>
                {
                    Open.Add(description);
                    _ready = Network.Host(LocalName);
                };
            }

            public void Join(LobbyInfo lobby)
            {
                _pending = () => _ready = Network.Join(LocalName);
            }

            public INetTransport TakeTransport()
            {
                INetTransport ready = _ready;
                _ready = null;
                return ready;
            }

            public string TakeError()
            {
                string error = Error;
                Error = null;
                return error;
            }

            public void Dispose()
            {
                Disposed = true;
            }
        }

        [Test]
        public void RoomsAreListedByGameAndJoined()
        {
            LoopbackNetwork network = new LoopbackNetwork();
            List<LobbyInfo> open = new List<LobbyInfo>();
            using (GameHost ann = Host())
            using (GameHost bob = Host())
            {
                FakeBackend bobBackend = new FakeBackend("Bob", network, open);
                Multiplayer hosting = new Multiplayer(ann, new FakeBackend("Ann", network, open));
                Multiplayer joining = new Multiplayer(bob, bobBackend);
                int changes = 0;
                joining.RoomChanged += () => changes++;

                int lost = 0;
                joining.RoomLost += () => lost++;
                hosting.RoomLost += () => lost += 100;
                Assert.IsNull(hosting.Loading);

                ModPackage gomoku = ann.Catalog.Find("gomoku");
                Assert.IsTrue(hosting.HostGame(gomoku));
                Assert.IsTrue(hosting.Busy);
                StringAssert.Contains("Opening", hosting.Loading);
                Assert.IsFalse(hosting.HostGame(gomoku));
                hosting.Update();
                Assert.IsNotNull(hosting.Room);
                Assert.IsNull(hosting.Loading);
                Assert.IsTrue(hosting.Room.IsHost);
                Assert.IsTrue(ann.Session.IsStarted);
                Assert.AreEqual("gomoku", ann.Mod.Manifest.Id);
                Assert.AreEqual(1, open.Count);
                Assert.AreEqual("Ann - " + gomoku.Manifest.Name, open[0].Name);
                Assert.AreEqual(2 + Multiplayer.MaxSpectators, open[0].Capacity);

                joining.Refresh("cubic3");
                joining.Update();
                Assert.AreEqual(0, joining.Lobbies("cubic3").Count);
                joining.Refresh("gomoku");
                Assert.AreEqual(0, joining.Lobbies("gomoku").Count);
                joining.Update();
                Assert.AreEqual(1, joining.Lobbies("gomoku").Count);
                Assert.AreEqual(0, joining.Lobbies("cubic3").Count);

                Assert.IsTrue(joining.Join(joining.Lobbies("gomoku")[0]));
                StringAssert.Contains("Joining", joining.Loading);
                joining.Update();
                StringAssert.Contains("host", joining.Loading);
                for (int i = 0; i < 4; i++)
                {
                    joining.Update();
                    hosting.Update();
                }
                Assert.IsNotNull(joining.Room);
                Assert.IsNull(joining.Loading);
                Assert.AreEqual(0, lost);
                Assert.IsFalse(joining.Room.IsHost);
                Assert.AreEqual(1, changes);
                Assert.AreEqual(2, joining.Room.Players.Count);
                Assert.AreEqual(2, hosting.Room.Players.Count);

                hosting.Leave();
                Assert.IsNull(hosting.Room);
                joining.Update();
                Assert.IsNull(joining.Room);
                Assert.AreEqual(1, lost);
                Assert.AreEqual(2, changes);
                Assert.Greater(bob.Toasts.Active(bob.Now).Count, 0);

                joining.Dispose();
                Assert.IsTrue(bobBackend.Disposed);
            }
        }

        [Test]
        public void JoiningNeedsTheSameGame()
        {
            LoopbackNetwork network = new LoopbackNetwork();
            List<LobbyInfo> open = new List<LobbyInfo>();
            using (GameHost bob = Host())
            {
                FakeBackend backend = new FakeBackend("Bob", network, open);
                Multiplayer joining = new Multiplayer(bob, backend);
                Assert.IsFalse(joining.Join(null));
                Assert.IsFalse(joining.Join(new LobbyInfo { ModId = "unknown", ModHash = "x" }));
                Assert.IsFalse(joining.Join(new LobbyInfo { ModId = "gomoku", ModHash = "x" }));
                Assert.IsFalse(joining.Busy);

                backend.Available = false;
                Assert.IsFalse(joining.Available);
                Assert.AreEqual("offline", joining.Status);
                Assert.IsFalse(joining.HostGame(bob.Catalog.Find("gomoku")));
                joining.Refresh("gomoku");
                Assert.IsFalse(joining.Busy);

                backend.Available = true;
                backend.Error = "no connection";
                joining.Update();
                Assert.IsNull(joining.Room);

                Multiplayer none = new Multiplayer(bob, null);
                Assert.IsFalse(none.Available);
                Assert.IsFalse(none.HostGame(bob.Catalog.Find("gomoku")));
                none.Update();
                none.Dispose();
            }
        }

        // ---- Rooms the computer fills -------------------------------------------------------------------------------------

        private double _time;

        // A host whose computer player thinks inside Update, on a clock the test moves.
        private GameHost ComputerHost()
        {
            GameHostOptions options = new GameHostOptions { Clock = () => _time, AiDelaySeconds = 0.5, AiSeed = 17, AiThreaded = false };
            return new GameHost(new ModCatalog(new[] { TestSupport.ModsRoot }), options);
        }

        // Lets the computer make the move that is due.
        private bool ComputerMoves(GameHost host)
        {
            host.Update();
            _time += 1;
            return host.Update();
        }

        private static string Seats(IReadOnlyList<int> seats)
        {
            return string.Join(",", seats);
        }

        private static readonly RoomOptions AgainstTheComputer = new RoomOptions { ComputerFills = true };

        [Test]
        public void ARoomAgainstTheComputerStartsWithTheHostPlayingIt()
        {
            using (GameHost host = ComputerHost())
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                NetHostRoom room = new NetHostRoom(host, new LoopbackNetwork().Host("Ann"), AgainstTheComputer);
                Assert.IsTrue(host.StartNewGame());
                Assert.IsTrue(room.ComputerFills);
                Assert.IsFalse(room.WaitsForPlayers);
                Assert.AreEqual(2, room.SeatCount);
                Assert.AreEqual("2", Seats(room.ComputerSeats));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(1));
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(2));

                Assert.IsTrue(room.CanAct);
                room.Submit(Place(1, 1));
                Assert.IsFalse(room.CanAct, "the computer is on turn");
                room.Submit(Place(15, 15));
                Assert.AreEqual(1, host.Session.View.ObjectCount, "nobody moves for the computer");
                Assert.IsTrue(ComputerMoves(host), "the computer did not answer");
                Assert.AreEqual(2, host.Session.View.ObjectCount);
                Assert.IsTrue(room.CanAct);
                room.Dispose();
            }
        }

        [Test]
        public void SomeoneWhoJoinsWatchesTheComputerGameUntilTheHostStartsTheNextOne()
        {
            using (GameHost host = ComputerHost())
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"), AgainstTheComputer);
                Assert.IsTrue(host.StartNewGame());
                room.Submit(Place(1, 1));
                Assert.IsTrue(ComputerMoves(host));

                List<string> told = new List<string>();
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                bob.Notice += (text, level) => told.Add(text);
                Pump(room, bob);
                Assert.IsTrue(bob.Ready);
                Assert.IsTrue(bob.ComputerFills);
                Assert.AreEqual(2, bob.SeatCount);
                Assert.AreEqual("2", Seats(bob.ComputerSeats));
                Assert.AreEqual(0, bob.LocalPlayer.Seat, "the game that is on is not Bob's");
                Assert.AreEqual(2, bob.LocalBoard.Queue.Display.ObjectCount);
                Assert.IsFalse(bob.CanAct);
                Assert.AreEqual(1, room.LocalPlayer.Seat);
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(2));

                // The game against the computer goes on while Bob watches, and he can not move.
                bob.Submit(Place(8, 8));
                room.Submit(Place(15, 1));
                Assert.IsTrue(ComputerMoves(host));
                Pump(room, bob);
                Assert.AreEqual(4, host.Session.View.ObjectCount);
                Assert.AreEqual(4, bob.LocalBoard.Queue.Display.ObjectCount);

                // The host starts the next game, and Bob takes the seat of the computer.
                told.Clear();
                Assert.IsTrue(host.StartNewGame());
                Pump(room, bob);
                Assert.AreEqual(2, bob.LocalPlayer.Seat);
                Assert.AreEqual(0, bob.ComputerSeats.Count);
                Assert.AreEqual(0, room.ComputerSeats.Count);
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(2));
                Assert.AreEqual(1, told.Count);
                StringAssert.Contains("You play", told[0]);
                Assert.AreEqual(0, host.Session.View.ObjectCount);
                Assert.IsTrue(room.CanAct);
                room.Submit(Place(1, 1));
                Assert.IsFalse(ComputerMoves(host), "a person plays that seat now");
                Pump(room, bob);
                Assert.IsTrue(bob.CanAct);
                Assert.IsFalse(room.CanAct);
                bob.Submit(Place(2, 2));
                Pump(room, bob);
                Assert.AreEqual(2, host.Session.View.ObjectCount);
                Assert.IsTrue(room.CanAct);
                room.Dispose();
            }
        }

        [Test]
        public void TheComputerPlaysOnFromThePositionWhenTheOpponentLeaves()
        {
            using (GameHost host = ComputerHost())
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"), AgainstTheComputer);
                List<string> told = new List<string>();
                room.Notice += (text, level) => told.Add(text);
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                Pump(room, bob);
                Assert.IsTrue(host.StartNewGame());
                Pump(room, bob);
                Assert.AreEqual(2, bob.LocalPlayer.Seat);

                room.Submit(Place(1, 1));
                Pump(room, bob);
                bob.Submit(Place(8, 8));
                Pump(room, bob);
                room.Submit(Place(15, 1));
                Pump(room, bob);
                Assert.AreEqual(3, host.Session.View.ObjectCount);
                Assert.AreEqual(2, host.Session.View.Turn);

                // Bob leaves while it is his turn: the game stays as it is and the computer takes the seat.
                told.Clear();
                bob.Dispose();
                Pump(room);
                Assert.AreEqual(1, room.Players.Count);
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(2));
                Assert.AreEqual("2", Seats(room.ComputerSeats));
                Assert.AreEqual(3, host.Session.View.ObjectCount, "the game goes on from where it was");
                Assert.AreEqual(1, told.FindAll(text => text.Contains("computer plays")).Count);
                Assert.IsFalse(room.CanAct);
                Assert.IsTrue(ComputerMoves(host), "the computer plays Bob's turn");
                Assert.AreEqual(4, host.Session.View.ObjectCount);
                Assert.IsTrue(room.CanAct);

                // Somebody else comes in; they wait for the next game.
                NetClientRoom cy = new NetClientRoom(host.Mod, network.Join("Cy"));
                Pump(room, cy);
                Assert.AreEqual(0, cy.LocalPlayer.Seat);
                Assert.AreEqual(1, room.WaitingCount);
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(2));
                Assert.IsTrue(host.StartNewGame());
                Pump(room, cy);
                Assert.AreEqual(2, cy.LocalPlayer.Seat);
                Assert.AreEqual(0, room.WaitingCount);
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(2));
                room.Dispose();
            }
        }

        [Test]
        public void AGuestWhoLeavesWithoutASeatLeavesTheComputerWhereItWas()
        {
            using (GameHost host = ComputerHost())
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"), AgainstTheComputer);
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                Pump(room, bob);
                Assert.AreEqual(0, bob.LocalPlayer.Seat);
                bob.Dispose();
                Pump(room);
                Assert.AreEqual(1, room.Players.Count);
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(2));
                Assert.AreEqual("2", Seats(room.ComputerSeats));
                Assert.IsTrue(room.CanAct);
                room.Dispose();
            }
        }

        [Test]
        public void ARoomOnlyTheHostIsInKeepsWaitingForAPersonWhenItHasNoComputer()
        {
            using (GameHost host = ComputerHost())
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"), new RoomOptions());
                Assert.IsTrue(host.StartNewGame());
                Assert.IsFalse(room.ComputerFills);
                Assert.IsTrue(room.WaitsForPlayers);
                Assert.AreEqual(0, room.ComputerSeats.Count);
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(2));

                // The host plays its own seat, and nobody plays the empty one in its place.
                room.Submit(Place(1, 1));
                Assert.IsFalse(room.CanAct);
                room.Submit(Place(15, 15));
                Assert.AreEqual(1, host.Session.View.ObjectCount);
                Assert.IsFalse(ComputerMoves(host));

                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                Pump(room, bob);
                Assert.IsTrue(bob.WaitsForPlayers);
                Assert.AreEqual(2, bob.LocalPlayer.Seat);
                Assert.IsTrue(bob.CanAct);
                bob.Submit(Place(8, 8));
                Pump(room, bob);
                Assert.AreEqual(2, host.Session.View.ObjectCount);
                Assert.IsTrue(room.CanAct);

                // And when Bob leaves, the seat is empty again and the game waits.
                bob.Dispose();
                Pump(room);
                room.Submit(Place(15, 1));
                Assert.AreEqual(3, host.Session.View.ObjectCount);
                Assert.IsFalse(room.CanAct);
                room.Dispose();
            }
        }

        [Test]
        public void ARoomOpenedLikeBeforeLetsTheHostPlayTheEmptySeats()
        {
            using (GameHost host = ComputerHost())
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                NetHostRoom room = new NetHostRoom(host, new LoopbackNetwork().Host("Ann"));
                Assert.IsTrue(host.StartNewGame());
                Assert.IsFalse(room.ComputerFills);
                Assert.IsFalse(room.WaitsForPlayers);
                room.Submit(Place(1, 1));
                Assert.IsTrue(room.CanAct);
                room.Dispose();
            }
        }

        [Test]
        public void TheComputerDoesNotFillTheRoomOfAGameItCannotPlay()
        {
            using (GameHost host = ComputerHost())
            {
                // A split game has no seats to fill, and a game without a computer player has nobody to fill them.
                Assert.IsTrue(host.SelectMod(Race()));
                NetHostRoom race = new NetHostRoom(host, new LoopbackNetwork().Host("Ann"), AgainstTheComputer);
                Assert.IsFalse(race.ComputerFills);
                Assert.IsFalse(race.WaitsForPlayers);
                Assert.IsFalse(NetHostRoom.CanFill(host));
                race.Dispose();

                Assert.IsTrue(host.SelectMod("gomoku"));
                Assert.IsTrue(NetHostRoom.CanFill(host));
                Assert.IsTrue(host.SelectMod("cubic3"));
                Assert.IsFalse(NetHostRoom.CanFill(host));
            }
        }

        [Test]
        public void AChessRoomAgainstTheComputerSeatsTheNewcomerAsBlackAtTheNextGame()
        {
            using (GameHost host = ComputerHost())
            {
                Assert.IsTrue(host.SelectMod("chess"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"), AgainstTheComputer);
                Assert.IsTrue(host.StartNewGame());
                Assert.IsTrue(room.ComputerFills);
                Assert.AreEqual(2, room.SeatCount);
                Assert.AreEqual("2", Seats(room.ComputerSeats));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(1));
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(2));
                Assert.IsTrue(room.CanAct, "white moves first, and that is the host");

                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                Pump(room, bob);
                Assert.AreEqual(0, bob.LocalPlayer.Seat, "the game that is on is not Bob's");
                Assert.IsTrue(host.StartNewGame());
                Pump(room, bob);
                Assert.AreEqual(2, bob.LocalPlayer.Seat, "Bob plays black against the host");
                Assert.AreEqual(0, room.ComputerSeats.Count);
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(2));

                // When Bob leaves, the computer plays black again, and the next game is against it.
                bob.Dispose();
                Pump(room);
                Assert.AreEqual("2", Seats(room.ComputerSeats));
                Assert.IsTrue(host.StartNewGame());
                Assert.AreEqual("2", Seats(room.ComputerSeats));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(1));
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(2));
                room.Dispose();
            }
        }

        [Test]
        public void ARoomSetupOffersWhatTheGameCanDo()
        {
            // Go-Stop: against the computer or not, and two players or three.
            RoomSetup gostop = new RoomSetup(TestSupport.LoadMod("gostop").Manifest);
            Assert.IsTrue(gostop.CanPlayComputer);
            Assert.IsTrue(gostop.ChoosesPlayers);
            Assert.IsTrue(gostop.HasChoices);
            Assert.IsTrue(gostop.PlayComputer, "against the computer is where it starts");
            Assert.AreEqual(2, gostop.FewestPlayers);
            Assert.AreEqual(3, gostop.MostPlayers);
            Assert.AreEqual(2, gostop.Players);
            RoomOptions options = gostop.ToOptions();
            Assert.IsTrue(options.ComputerFills);
            Assert.AreEqual(2, options.Players);

            gostop.Players = 3;
            gostop.PlayComputer = false;
            options = gostop.ToOptions();
            Assert.IsFalse(options.ComputerFills);
            Assert.AreEqual(3, options.Players);

            // Whatever is asked for stays one of the numbers the game takes.
            gostop.Players = 9;
            Assert.AreEqual(3, gostop.Players);
            gostop.Players = 0;
            Assert.AreEqual(2, gostop.Players);

            // Chess and Gomoku: against the computer or not; there is no number of players to choose.
            foreach (string id in new[] { "chess", "gomoku" })
            {
                RoomSetup setup = new RoomSetup(TestSupport.LoadMod(id).Manifest);
                Assert.IsTrue(setup.CanPlayComputer, id);
                Assert.IsFalse(setup.ChoosesPlayers, id);
                Assert.IsTrue(setup.HasChoices, id);
                RoomOptions chosen = setup.ToOptions();
                Assert.IsTrue(chosen.ComputerFills, id);
                Assert.AreEqual(0, chosen.Players, "the game says how many play: " + id);
                setup.PlayComputer = false;
                Assert.IsFalse(setup.ToOptions().ComputerFills, id);
            }

            // Sudoku (people solve it together, with hints for help) and the cube (a table each) have nothing to choose,
            // and a room of theirs opens at once.
            foreach (string id in new[] { "sudoku", "cubic3" })
            {
                RoomSetup setup = new RoomSetup(TestSupport.LoadMod(id).Manifest);
                Assert.IsFalse(setup.CanPlayComputer, id);
                Assert.IsFalse(setup.ChoosesPlayers, id);
                Assert.IsFalse(setup.HasChoices, id);
                Assert.IsFalse(setup.PlayComputer, id);
                Assert.IsFalse(setup.ToOptions().ComputerFills, id);
                Assert.AreEqual(0, setup.ToOptions().Players, id);
            }
            Assert.Throws<ArgumentNullException>(() => new RoomSetup(null));
        }

        [Test]
        public void ARoomSetupLeavesOutTheComputerWhereTheSeatsPlayTogether()
        {
            // A game with a computer player whose seats all play at once has no seat that the computer could fill.
            ModManifest together = ModManifest.Parse(
                "{\"id\":\"together\",\"name\":\"Together\",\"api\":1,\"entry\":\"main.luau\",\"players\":{\"min\":1,\"max\":2}," +
                "\"multiplayer\":{\"cooperative\":true},\"ai\":{\"levels\":[5]}}");
            Assert.IsTrue(together.HasComputerPlayer);
            RoomSetup setup = new RoomSetup(together);
            Assert.IsFalse(setup.CanPlayComputer);
            Assert.IsFalse(setup.HasChoices);

            // A game for one has no opponent to be.
            ModManifest alone = ModManifest.Parse(
                "{\"id\":\"alone\",\"name\":\"Alone\",\"api\":1,\"entry\":\"main.luau\",\"players\":{\"min\":1,\"max\":1},\"ai\":{\"levels\":[5]}}");
            Assert.IsFalse(new RoomSetup(alone).CanPlayComputer);
        }

        [Test]
        public void ARoomSetupOffersNoMoreNumbersThanThereAreButtonsFor()
        {
            ModManifest crowd = ModManifest.Parse(
                "{\"id\":\"crowd\",\"name\":\"Crowd\",\"api\":1,\"entry\":\"main.luau\",\"players\":{\"min\":1,\"max\":16,\"option\":\"players\"}," +
                "\"options\":{\"players\":0}}");
            RoomSetup setup = new RoomSetup(crowd);
            Assert.IsTrue(setup.ChoosesPlayers);
            Assert.IsFalse(setup.CanPlayComputer, "no computer player in this game");
            Assert.IsTrue(setup.HasChoices);
            Assert.AreEqual(1, setup.FewestPlayers);
            Assert.AreEqual(RoomSetup.MaxPlayerChoices, setup.MostPlayers);
            setup.Players = 12;
            Assert.AreEqual(RoomSetup.MaxPlayerChoices, setup.Players);
            Assert.AreEqual(RoomSetup.MaxPlayerChoices, setup.ToOptions().Players);
        }

        [Test]
        public void AGoStopRoomAgainstTheComputerSeatsTheNewcomerAtTheNextGame()
        {
            using (GameHost host = ComputerHost())
            {
                Assert.IsTrue(host.SelectMod("gostop"));
                host.SetOption("players", JsonValue.Of(3));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"), AgainstTheComputer);
                Assert.IsTrue(host.StartNewGame());
                Assert.AreEqual(3, room.SeatCount);
                Assert.AreEqual("2,3", Seats(room.ComputerSeats));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(1));
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(2));
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(3));

                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                Pump(room, bob);
                Assert.AreEqual(0, bob.LocalPlayer.Seat);
                Assert.AreEqual(3, bob.SeatCount);
                Assert.AreEqual("2,3", Seats(bob.ComputerSeats));

                // Two people play against the one computer seat that is left, and a third person has to wait.
                NetClientRoom cy = new NetClientRoom(host.Mod, network.Join("Cy"));
                NetClientRoom di = new NetClientRoom(host.Mod, network.Join("Di"));
                Pump(room, bob, cy, di);
                Assert.IsTrue(host.StartNewGame());
                Pump(room, bob, cy, di);
                Assert.AreEqual(2, bob.LocalPlayer.Seat);
                Assert.AreEqual(3, cy.LocalPlayer.Seat);
                Assert.AreEqual(0, di.LocalPlayer.Seat);
                Assert.AreEqual(0, room.ComputerSeats.Count);
                Assert.AreEqual(1, room.WaitingCount);
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(3));

                // The one who plays seat 3 leaves: the computer takes it, the waiting one is not seated before the next game.
                cy.Dispose();
                Pump(room, bob, di);
                Assert.AreEqual("3", Seats(room.ComputerSeats));
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(3));
                Assert.AreEqual(0, di.LocalPlayer.Seat);
                room.Dispose();
            }
        }

        [Test]
        public void AGoStopRoomOfTwoHasOneSeatForTheComputer()
        {
            using (GameHost host = ComputerHost())
            {
                Assert.IsTrue(host.SelectMod("gostop"));
                host.SetOption("players", JsonValue.Of(2));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"), AgainstTheComputer);
                Assert.IsTrue(host.StartNewGame());
                Assert.AreEqual(2, room.SeatCount);
                Assert.AreEqual("2", Seats(room.ComputerSeats));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(3));

                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                NetClientRoom cy = new NetClientRoom(host.Mod, network.Join("Cy"));
                Pump(room, bob, cy);
                Assert.IsTrue(host.StartNewGame());
                Pump(room, bob, cy);
                Assert.AreEqual(2, bob.LocalPlayer.Seat);
                Assert.AreEqual(0, cy.LocalPlayer.Seat, "a game of two has no seat for a third person");
                Assert.AreEqual(2, cy.SeatCount);
                room.Dispose();
            }
        }

        // Opens a room of the game on a network of its own and returns what runs it.
        private static Multiplayer OpenRoom(GameHost host, List<LobbyInfo> open, string mod, RoomOptions options)
        {
            Multiplayer hosting = new Multiplayer(host, new FakeBackend("Ann", new LoopbackNetwork(), open));
            ModPackage package = host.Catalog.Find(mod);
            Assert.IsTrue(options == null ? hosting.HostGame(package) : hosting.HostGame(package, options));
            hosting.Update();
            Assert.IsNotNull(hosting.Room);
            return hosting;
        }

        [Test]
        public void TheRoomOptionsOfTheHostReachTheRoomAndItsListing()
        {
            List<LobbyInfo> open = new List<LobbyInfo>();
            using (GameHost ann = ComputerHost())
            {
                // Three at the table, one of them a person.
                using (Multiplayer hosting = OpenRoom(ann, open, "gostop", new RoomOptions { ComputerFills = true, Players = 3 }))
                {
                    Assert.IsTrue(hosting.Room.ComputerFills);
                    Assert.AreEqual(3, hosting.Room.SeatCount);
                    Assert.AreEqual("2,3", Seats(hosting.Room.ComputerSeats));
                    Assert.AreEqual(3, ann.NewGameOptions["players"].AsInt());
                    Assert.AreEqual(3, ann.SeatCount);
                    Assert.AreEqual(1, open.Count);
                    StringAssert.Contains("(3P)", open[0].Name);
                    Assert.AreEqual(3 + Multiplayer.MaxSpectators, open[0].Capacity);
                }

                // Two at the table, people only.
                using (Multiplayer hosting = OpenRoom(ann, open, "gostop", new RoomOptions { Players = 2 }))
                {
                    Assert.IsFalse(hosting.Room.ComputerFills);
                    Assert.IsTrue(hosting.Room.WaitsForPlayers);
                    Assert.AreEqual(2, hosting.Room.SeatCount);
                    Assert.AreEqual(0, hosting.Room.ComputerSeats.Count);
                    Assert.AreEqual(2, ann.NewGameOptions["players"].AsInt());
                    Assert.AreEqual(2, open.Count);
                    StringAssert.Contains("(2P)", open[1].Name);
                    Assert.AreEqual(2 + Multiplayer.MaxSpectators, open[1].Capacity);
                }

                // A game that has no say in how many play is listed as it always was.
                ModPackage gomoku = ann.Catalog.Find("gomoku");
                using (Multiplayer hosting = OpenRoom(ann, open, "gomoku", new RoomOptions { ComputerFills = true, Players = 3 }))
                {
                    Assert.AreEqual("Ann - " + gomoku.Manifest.Name, open[2].Name);
                    Assert.AreEqual(2 + Multiplayer.MaxSpectators, open[2].Capacity);
                    Assert.AreEqual(2, hosting.Room.SeatCount);
                    Assert.AreEqual("2", Seats(hosting.Room.ComputerSeats));
                }

                // And so is a room that is opened without any options.
                using (Multiplayer hosting = OpenRoom(ann, open, "gomoku", null))
                {
                    Assert.AreEqual("Ann - " + gomoku.Manifest.Name, open[3].Name);
                    Assert.IsFalse(hosting.Room.ComputerFills);
                    Assert.IsFalse(hosting.Room.WaitsForPlayers);
                }
            }
        }

        [Test]
        public void ARoomWithAnOutOfRangeNumberOfPlayersHostsTheClosestOne()
        {
            List<LobbyInfo> open = new List<LobbyInfo>();
            using (GameHost ann = ComputerHost())
            {
                using (Multiplayer hosting = OpenRoom(ann, open, "gostop", new RoomOptions { ComputerFills = true, Players = 9 }))
                {
                    Assert.AreEqual(3, hosting.Room.SeatCount);
                    Assert.AreEqual(3, ann.NewGameOptions["players"].AsInt());
                }
                using (Multiplayer hosting = OpenRoom(ann, open, "gostop", new RoomOptions { ComputerFills = true, Players = 1 }))
                {
                    Assert.AreEqual(2, hosting.Room.SeatCount);
                    Assert.AreEqual(2, ann.NewGameOptions["players"].AsInt());
                }
            }
        }
    }
}
