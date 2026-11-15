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
                Assert.IsTrue(host.SelectMod("sudoku"));
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
    }
}
