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
    // The parts of a room over a service that sends small packets (Epic Online Services on Android): the ids of the people, the
    // cutting of messages into packets, and the room that is made of them.
    [TestFixture]
    public class PacketTransportTests
    {
        // ---- helpers -------------------------------------------------------------------------------------------------

        private static byte[] Bytes(int length, int seed)
        {
            byte[] data = new byte[length];
            for (int i = 0; i < length; i++)
                data[i] = (byte)(i * 131 + i / 7 + seed);
            return data;
        }

        private static void AssertSame(byte[] expected, byte[] actual)
        {
            Assert.IsNotNull(actual, "a message was expected");
            Assert.AreEqual(expected.Length, actual.Length, "the length of the message");
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                    Assert.Fail("the messages differ at byte " + i);
            }
        }

        // Ids use all 64 bits, and the comparison of the numbers in AreEqual may go through doubles, which keep fewer of them.
        private static void AssertId(ulong expected, ulong actual)
        {
            Assert.IsTrue(expected == actual, "expected the id " + expected.ToString("x16") + " but it was " + actual.ToString("x16"));
        }

        // The packets that a message turns into.
        private static List<byte[]> Packets(byte[] message, int maxPacketBytes)
        {
            List<byte[]> packets = new List<byte[]>();
            byte[] buffer = new byte[maxPacketBytes];
            int offset = 0;
            while (offset < message.Length)
            {
                int length = PacketFraming.Write(message, ref offset, buffer, maxPacketBytes);
                byte[] packet = new byte[length];
                Buffer.BlockCopy(buffer, 0, packet, 0, length);
                packets.Add(packet);
            }
            return packets;
        }

        // Packets as a sender who does it by the book makes them, or as a bad one does.
        private static byte[] WholePacket(int size)
        {
            byte[] packet = new byte[PacketFraming.WholeHeaderBytes + size];
            packet[0] = PacketFraming.Whole;
            for (int i = PacketFraming.WholeHeaderBytes; i < packet.Length; i++)
                packet[i] = 9;
            return packet;
        }

        private static byte[] FirstPacket(uint total, int piece)
        {
            byte[] packet = new byte[PacketFraming.FirstHeaderBytes + piece];
            packet[0] = PacketFraming.First;
            packet[1] = (byte)total;
            packet[2] = (byte)(total >> 8);
            packet[3] = (byte)(total >> 16);
            packet[4] = (byte)(total >> 24);
            for (int i = PacketFraming.FirstHeaderBytes; i < packet.Length; i++)
                packet[i] = 7;
            return packet;
        }

        private static byte[] NextPacket(int piece)
        {
            byte[] packet = new byte[PacketFraming.NextHeaderBytes + piece];
            packet[0] = PacketFraming.Next;
            for (int i = PacketFraming.NextHeaderBytes; i < packet.Length; i++)
                packet[i] = 8;
            return packet;
        }

        private static byte[] Add(PacketAssembler assembler, byte[] packet)
        {
            return assembler.Add(packet, packet.Length);
        }

        private static List<NetEvent> Drain(INetTransport transport)
        {
            List<NetEvent> events = new List<NetEvent>();
            while (transport.Poll(out NetEvent e))
                events.Add(e);
            return events;
        }

        // ---- ids -----------------------------------------------------------------------------------------------------

        [Test]
        public void ATextBecomesTheSameIdOnEveryDevice()
        {
            // The ids are 64 bit FNV-1a hashes. The values were worked out apart from the code (for "a" and "foobar" they are the
            // ones that the definition of the hash publishes).
            AssertId(0xaf63dc4c8601ec8cUL, NetIds.FromText("a"));
            AssertId(0xaf63df4c8601f1a5UL, NetIds.FromText("b"));
            AssertId(0xe71fa2190541574bUL, NetIds.FromText("abc"));
            AssertId(0x85944171f73967e8UL, NetIds.FromText("foobar"));

            // The same account, written in capitals or not, is the same person.
            AssertId(0x2d726844a7a293dcUL, NetIds.FromText("0002a1b2c3d4e5f60718293a4b5c6d7e"));
            AssertId(0x2d726844a7a293dcUL, NetIds.FromText("0002A1B2C3D4E5F60718293A4B5C6D7E"));
        }

        [Test]
        public void NoTextIsTheIdOfNobodyAndNoTwoTextsShareAnId()
        {
            Assert.AreEqual(0UL, NetIds.FromText(null));
            Assert.AreEqual(0UL, NetIds.FromText(string.Empty));

            HashSet<ulong> seen = new HashSet<ulong>();
            for (int i = 0; i < 5000; i++)
            {
                ulong id = NetIds.FromText("player-" + i);
                Assert.IsTrue(id != 0, "the id of player-" + i + " is the id of nobody");
                Assert.IsTrue(seen.Add(id), "the id of player-" + i + " was taken");
            }
        }

        [Test]
        public void TextIsClippedToAByteBudgetWithoutCuttingACharacter()
        {
            Assert.AreEqual(string.Empty, ServiceText.ClipUtf8(null, 10));
            Assert.AreEqual(string.Empty, ServiceText.ClipUtf8("abc", 0));
            Assert.AreEqual("abc", ServiceText.ClipUtf8("abc", 3));
            Assert.AreEqual("ab", ServiceText.ClipUtf8("abc", 2));

            // Korean letters take three bytes each, accented ones two.
            Assert.AreEqual("\uAC00\uB098", ServiceText.ClipUtf8("\uAC00\uB098\uB2E4", 6));
            Assert.AreEqual("\uAC00\uB098", ServiceText.ClipUtf8("\uAC00\uB098\uB2E4", 8));
            Assert.AreEqual(string.Empty, ServiceText.ClipUtf8("\uAC00", 2));
            Assert.AreEqual("a\u00E9", ServiceText.ClipUtf8("a\u00E9b", 3));
            Assert.AreEqual("a", ServiceText.ClipUtf8("a\u00E9b", 2));

            // A character outside the first plane (four bytes, two chars of the string) is not cut apart.
            string face = "\uD83D\uDE00";
            Assert.AreEqual("a" + face, ServiceText.ClipUtf8("a" + face + "b", 5));
            Assert.AreEqual("a", ServiceText.ClipUtf8("a" + face + "b", 4));
            for (int budget = 0; budget < 12; budget++)
            {
                string clipped = ServiceText.ClipUtf8("a" + face + "\uAC00" + face, budget);
                Assert.LessOrEqual(System.Text.Encoding.UTF8.GetByteCount(clipped), budget);
                Assert.IsFalse(clipped.Length > 0 && char.IsHighSurrogate(clipped[clipped.Length - 1]), "cut in the middle of a character at " + budget);
            }
        }

        // ---- cutting messages into packets ---------------------------------------------------------------------------

        [Test]
        public void AMessageThatFitsTravelsInOnePacket()
        {
            List<byte[]> packets = Packets(Bytes(15, 1), 16);
            Assert.AreEqual(1, packets.Count);
            Assert.AreEqual(16, packets[0].Length);
            Assert.AreEqual(PacketFraming.Whole, packets[0][0]);
            Assert.AreEqual(1, PacketFraming.PacketCount(15, 16));

            // One byte more and it takes two: the first piece with the length of the whole, then the rest.
            packets = Packets(Bytes(16, 2), 16);
            Assert.AreEqual(2, packets.Count);
            Assert.AreEqual(2, PacketFraming.PacketCount(16, 16));
            Assert.AreEqual(PacketFraming.First, packets[0][0]);
            Assert.AreEqual(16, packets[0].Length);
            Assert.AreEqual(16, packets[0][1]);
            Assert.AreEqual(0, packets[0][2]);
            Assert.AreEqual(PacketFraming.Next, packets[1][0]);
            Assert.AreEqual(6, packets[1].Length);

            // The length is written least significant byte first.
            packets = Packets(Bytes(70000, 3), 1170);
            Assert.AreEqual(70000 & 0xFF, packets[0][1]);
            Assert.AreEqual((70000 >> 8) & 0xFF, packets[0][2]);
            Assert.AreEqual((70000 >> 16) & 0xFF, packets[0][3]);
            Assert.AreEqual(0, packets[0][4]);
        }

        [Test]
        public void MessagesAreCutIntoPacketsAndPutTogetherAgain()
        {
            foreach (int max in new[] { 6, 7, 16, 100, 1170 })
            {
                foreach (int length in new[] { 1, 2, max - 2, max - 1, max, max + 1, 2 * max, 3 * max + 7, 5000, 70001 })
                {
                    byte[] message = Bytes(length, max + length);
                    List<byte[]> packets = Packets(message, max);
                    Assert.AreEqual(PacketFraming.PacketCount(length, max), packets.Count, length + " bytes in packets of " + max);

                    PacketAssembler assembler = new PacketAssembler(length);
                    byte[] arrived = null;
                    for (int i = 0; i < packets.Count; i++)
                    {
                        Assert.LessOrEqual(packets[i].Length, max);
                        arrived = Add(assembler, packets[i]);
                        if (i < packets.Count - 1)
                        {
                            Assert.IsNull(arrived);
                            Assert.IsTrue(assembler.InProgress);
                        }
                    }
                    AssertSame(message, arrived);
                    Assert.IsFalse(assembler.InProgress);
                    Assert.AreEqual(0, assembler.Rejected);
                }
            }
        }

        [Test]
        public void TheWriterRefusesWhatCannotBeCut()
        {
            byte[] packet = new byte[16];
            int offset = 0;
            Assert.Throws<ArgumentException>(() => PacketFraming.Write(null, ref offset, packet, 16));
            Assert.Throws<ArgumentException>(() => PacketFraming.Write(new byte[0], ref offset, packet, 16));
            Assert.Throws<ArgumentException>(() => PacketFraming.Write(new byte[4], ref offset, packet, PacketFraming.MinPacketBytes - 1));
            Assert.Throws<ArgumentException>(() => PacketFraming.Write(new byte[4], ref offset, new byte[8], 16));
            offset = 4;
            Assert.Throws<ArgumentOutOfRangeException>(() => PacketFraming.Write(new byte[4], ref offset, packet, 16));
            Assert.Throws<ArgumentOutOfRangeException>(() => PacketFraming.PacketCount(0, 16));
            Assert.Throws<ArgumentOutOfRangeException>(() => PacketFraming.PacketCount(4, PacketFraming.MinPacketBytes - 1));
        }

        [Test]
        public void MessagesLongerThanAllowedAreRefused()
        {
            PacketAssembler assembler = new PacketAssembler(100);
            Assert.IsNull(Add(assembler, WholePacket(101)));
            Assert.AreEqual(1, assembler.Rejected);
            Assert.IsNull(Add(assembler, FirstPacket(101, 20)));
            Assert.AreEqual(2, assembler.Rejected);
            Assert.IsFalse(assembler.InProgress);

            // As long as allowed is fine, whole or in pieces.
            Assert.AreEqual(100, Add(assembler, WholePacket(100)).Length);
            Assert.IsNull(Add(assembler, FirstPacket(100, 60)));
            Assert.AreEqual(100, Add(assembler, NextPacket(40)).Length);
            Assert.AreEqual(2, assembler.Rejected);
        }

        [Test]
        public void PacketsThatMakeNoSenseAreThrownAway()
        {
            PacketAssembler assembler = new PacketAssembler(1000);
            byte[][] bad =
            {
                new byte[] { PacketFraming.Whole },     // no message in it
                new byte[] { 3, 1, 2, 3 },              // of a kind that is not known
                new byte[] { 255 },
                FirstPacket(10, 0),                     // nothing of the message itself
                FirstPacket(0, 4),                      // a message of no length
                FirstPacket(3, 4),                      // more in the piece than in the whole
                FirstPacket(1001, 4),                   // longer than allowed
                FirstPacket(uint.MaxValue, 4),          // far longer
                NextPacket(3),                          // the piece of a message that never began
                WholePacket(1001)                       // longer than allowed
            };
            for (int i = 0; i < bad.Length; i++)
            {
                Assert.IsNull(Add(assembler, bad[i]), "packet " + i);
                Assert.AreEqual(i + 1, assembler.Rejected, "packet " + i);
                Assert.IsFalse(assembler.InProgress, "packet " + i);
            }

            // No bytes at all, more bytes than the packet has, and no packet.
            Assert.IsNull(assembler.Add(new byte[4], 0));
            Assert.IsNull(assembler.Add(new byte[4], 5));
            Assert.IsNull(assembler.Add(null, 1));
            Assert.AreEqual(bad.Length + 3, assembler.Rejected);

            // What comes after is not harmed.
            Assert.AreEqual(5, Add(assembler, WholePacket(5)).Length);
            Assert.AreEqual(bad.Length + 3, assembler.Rejected);
        }

        [Test]
        public void AMessageIsFinishedByItsOwnPiecesOnly()
        {
            PacketAssembler assembler = new PacketAssembler(1000);

            // A new message ends the one that was on its way.
            Assert.IsNull(Add(assembler, FirstPacket(20, 10)));
            Assert.IsTrue(assembler.InProgress);
            Assert.AreEqual(3, Add(assembler, WholePacket(3)).Length);
            Assert.IsFalse(assembler.InProgress);
            Assert.IsNull(Add(assembler, NextPacket(10)));
            Assert.AreEqual(1, assembler.Rejected);

            // The latest first piece wins.
            Assert.IsNull(Add(assembler, FirstPacket(20, 10)));
            Assert.IsNull(Add(assembler, FirstPacket(12, 5)));
            Assert.AreEqual(12, Add(assembler, NextPacket(7)).Length);
            Assert.AreEqual(1, assembler.Rejected);

            // A piece that does not fit ends the message, and the pieces that follow are no use.
            Assert.IsNull(Add(assembler, FirstPacket(20, 10)));
            Assert.IsNull(Add(assembler, NextPacket(11)));
            Assert.IsFalse(assembler.InProgress);
            Assert.IsNull(Add(assembler, NextPacket(10)));
            Assert.AreEqual(3, assembler.Rejected);

            // A piece without bytes in it is no piece.
            Assert.IsNull(Add(assembler, FirstPacket(20, 10)));
            Assert.IsNull(Add(assembler, NextPacket(0)));
            Assert.IsFalse(assembler.InProgress);
            Assert.AreEqual(4, assembler.Rejected);

            // A first piece that holds the whole message needs no more.
            Assert.AreEqual(5, Add(assembler, FirstPacket(5, 5)).Length);
            Assert.IsFalse(assembler.InProgress);
            Assert.AreEqual(4, assembler.Rejected);
        }

        // ---- the room over packets -----------------------------------------------------------------------------------

        private struct Arrival
        {
            public ulong From;
            public byte[] Packet;
        }

        // Links inside one process, on which a packet arrives whole and in order, as it does in a service of the net.
        private sealed class PacketHub
        {
            private readonly Dictionary<ulong, HubLink> _links = new Dictionary<ulong, HubLink>();

            public PacketHub(int maxPacketBytes)
            {
                MaxPacketBytes = maxPacketBytes;
            }

            public int MaxPacketBytes { get; }
            public int PacketsSent { get; private set; }
            public int FirstPieces { get; private set; }
            public int LargestPacket { get; private set; }
            public int Disposed { get; private set; }

            // How many packets still go through before the hub refuses the rest; negative when it never does.
            public int RefuseAfter { get; set; } = -1;

            public HubLink Connect(ulong id)
            {
                HubLink link = new HubLink(this, id);
                _links[id] = link;
                return link;
            }

            public int Waiting(ulong id)
            {
                return _links[id].Waiting;
            }

            public bool Deliver(ulong from, ulong to, byte[] packet, int length)
            {
                if (length < 1 || length > MaxPacketBytes || length > packet.Length || !_links.TryGetValue(to, out HubLink target) || target.IsDisposed)
                    return false;
                if (RefuseAfter == 0)
                    return false;
                if (RefuseAfter > 0)
                    RefuseAfter--;
                PacketsSent++;
                if (packet[0] == PacketFraming.First)
                    FirstPieces++;
                LargestPacket = Math.Max(LargestPacket, length);
                byte[] copy = new byte[length];
                Buffer.BlockCopy(packet, 0, copy, 0, length);
                target.Inject(from, copy);
                return true;
            }

            public void LinkDisposed()
            {
                Disposed++;
            }
        }

        private sealed class HubLink : IPacketLink
        {
            private readonly PacketHub _hub;
            private readonly ulong _id;
            private readonly Queue<Arrival> _arrived = new Queue<Arrival>();

            public HubLink(PacketHub hub, ulong id)
            {
                _hub = hub;
                _id = id;
            }

            public int MaxPacketBytes => _hub.MaxPacketBytes;
            public bool IsDisposed { get; private set; }
            public int Waiting => _arrived.Count;

            public void Inject(ulong from, byte[] packet)
            {
                _arrived.Enqueue(new Arrival { From = from, Packet = packet });
            }

            public bool SendPacket(ulong peer, byte[] packet, int length)
            {
                return !IsDisposed && _hub.Deliver(_id, peer, packet, length);
            }

            public bool ReceivePacket(byte[] buffer, out ulong peer, out int length)
            {
                if (buffer.Length < MaxPacketBytes)
                    throw new InvalidOperationException("the buffer does not hold a packet");
                if (IsDisposed || _arrived.Count == 0)
                {
                    peer = 0;
                    length = 0;
                    return false;
                }
                Arrival next = _arrived.Dequeue();
                Buffer.BlockCopy(next.Packet, 0, buffer, 0, next.Packet.Length);
                peer = next.From;
                length = next.Packet.Length;
                return true;
            }

            public void Dispose()
            {
                if (IsDisposed)
                    return;
                IsDisposed = true;
                _hub.LinkDisposed();
            }
        }

        // A host, Ann (1), and a guest, Bob (2).
        private sealed class Pair : IDisposable
        {
            public readonly PacketHub Hub;
            public readonly HubLink HostLink;
            public readonly HubLink GuestLink;
            public readonly PacketTransport Host;
            public readonly PacketTransport Guest;

            public Pair(int maxPacketBytes)
            {
                Hub = new PacketHub(maxPacketBytes);
                HostLink = Hub.Connect(1);
                GuestLink = Hub.Connect(2);
                Host = new PacketTransport(HostLink, 1, 1, "Ann");
                Guest = new PacketTransport(GuestLink, 2, 1, "Bob");
            }

            // Bob comes into the room: each of them gets to know the other.
            public void Join()
            {
                Host.AddMember(2, "Bob");
                Guest.AddMember(1, "Ann");
            }

            public void Dispose()
            {
                Host.Dispose();
                Guest.Dispose();
            }
        }

        [Test]
        public void ATransportKnowsItsPlaceInTheRoom()
        {
            using (Pair pair = new Pair(64))
            {
                Assert.IsTrue(pair.Host.IsHost);
                Assert.IsFalse(pair.Guest.IsHost);
                Assert.AreEqual(1UL, pair.Guest.HostId);
                Assert.AreEqual(2UL, pair.Guest.LocalId);
                Assert.AreEqual("Bob", pair.Guest.LocalName);
            }
            Assert.Throws<ArgumentNullException>(() => new PacketTransport(null, 1, 1, "x"));
            Assert.Throws<ArgumentException>(() => new PacketTransport(new PacketHub(64).Connect(1), 0, 1, "x"));
            Assert.Throws<ArgumentException>(() => new PacketTransport(new PacketHub(64).Connect(1), 1, 0, "x"));
            Assert.Throws<ArgumentException>(() => new PacketTransport(new PacketHub(PacketFraming.MinPacketBytes - 1).Connect(1), 1, 1, "x"));
            Assert.AreEqual(string.Empty, new PacketTransport(new PacketHub(64).Connect(1), 1, 1, null).LocalName);
        }

        [Test]
        public void MembersComeAndGoAsEvents()
        {
            using (Pair pair = new Pair(64))
            {
                PacketTransport host = pair.Host;
                Assert.IsTrue(host.AddMember(2, "Bob"));
                Assert.IsFalse(host.AddMember(2, "Bob"));
                Assert.IsFalse(host.AddMember(1, "Ann"));
                Assert.IsFalse(host.AddMember(0, "Nobody"));
                Assert.IsTrue(host.IsMember(2));
                Assert.IsFalse(host.IsMember(3));
                Assert.IsTrue(host.AddMember(3, null));
                host.RemoveMember(9);

                List<NetEvent> events = Drain(host);
                Assert.AreEqual(2, events.Count);
                Assert.AreEqual(NetEventKind.PeerJoined, events[0].Kind);
                Assert.AreEqual(2UL, events[0].Peer);
                Assert.AreEqual("Bob", events[0].Name);
                Assert.AreEqual(NetEventKind.PeerJoined, events[1].Kind);
                Assert.AreEqual(3UL, events[1].Peer);
                Assert.AreEqual(string.Empty, events[1].Name);

                host.RemoveMember(3);
                Assert.IsFalse(host.IsMember(3));
                events = Drain(host);
                Assert.AreEqual(1, events.Count);
                Assert.AreEqual(NetEventKind.PeerLeft, events[0].Kind);
                Assert.AreEqual(3UL, events[0].Peer);
            }
        }

        [Test]
        public void TheRoomIsOverWhenTheHostLeavesAndSaidOnce()
        {
            using (Pair pair = new Pair(64))
            {
                pair.Join();
                Drain(pair.Guest);

                pair.Guest.RemoveMember(1);
                List<NetEvent> events = Drain(pair.Guest);
                Assert.AreEqual(2, events.Count);
                Assert.AreEqual(NetEventKind.PeerLeft, events[0].Kind);
                Assert.AreEqual(1UL, events[0].Peer);
                Assert.AreEqual(NetEventKind.Closed, events[1].Kind);
                StringAssert.Contains("host", events[1].Reason);

                pair.Guest.Close("again");
                Assert.AreEqual(0, Drain(pair.Guest).Count);
                Assert.IsFalse(pair.Guest.Send(1, Bytes(3, 1)));
            }
        }

        [Test]
        public void ARoomCanBeClosedForAnyReason()
        {
            using (Pair pair = new Pair(64))
            {
                pair.Join();
                Drain(pair.Guest);
                pair.Guest.Close("The service said goodbye.");
                pair.Guest.Close("A second time.");
                List<NetEvent> events = Drain(pair.Guest);
                Assert.AreEqual(1, events.Count);
                Assert.AreEqual(NetEventKind.Closed, events[0].Kind);
                Assert.AreEqual("The service said goodbye.", events[0].Reason);

                pair.Host.Close(string.Empty);
                events = Drain(pair.Host);
                Assert.AreEqual(2, events.Count);
                Assert.AreEqual(NetEventKind.Closed, events[1].Kind);
                Assert.IsFalse(string.IsNullOrEmpty(events[1].Reason));
            }
        }

        [Test]
        public void MessagesCrossTheRoomWholeAndInPieces()
        {
            using (Pair pair = new Pair(64))
            {
                pair.Join();
                Drain(pair.Host);
                Drain(pair.Guest);

                byte[] small = Bytes(10, 1);
                byte[] big = Bytes(5000, 2);
                byte[] last = Bytes(1, 3);
                Assert.IsTrue(pair.Guest.Send(1, small));
                Assert.IsTrue(pair.Guest.Send(1, big));
                Assert.IsTrue(pair.Guest.Send(1, last));
                small[0] ^= 0xFF;

                List<NetEvent> events = Drain(pair.Host);
                Assert.AreEqual(3, events.Count);
                foreach (NetEvent e in events)
                {
                    Assert.AreEqual(NetEventKind.Data, e.Kind);
                    Assert.AreEqual(2UL, e.Peer);
                }
                AssertSame(Bytes(10, 1), events[0].Data);
                AssertSame(big, events[1].Data);
                AssertSame(last, events[2].Data);
                Assert.AreEqual(PacketFraming.PacketCount(10, 64) + PacketFraming.PacketCount(5000, 64) + PacketFraming.PacketCount(1, 64), pair.Hub.PacketsSent);
                Assert.AreEqual(1, pair.Hub.FirstPieces);
                Assert.LessOrEqual(pair.Hub.LargestPacket, 64);

                // And the other way.
                Assert.IsTrue(pair.Host.Send(2, big));
                events = Drain(pair.Guest);
                Assert.AreEqual(1, events.Count);
                Assert.AreEqual(1UL, events[0].Peer);
                AssertSame(big, events[0].Data);
            }
        }

        [Test]
        public void OnlyMembersCanBeReachedAndHeard()
        {
            using (Pair pair = new Pair(64))
            {
                // Nobody to talk to before the room has its members.
                Assert.IsFalse(pair.Guest.Send(1, Bytes(3, 1)));
                pair.Join();
                Drain(pair.Host);
                Drain(pair.Guest);
                Assert.IsFalse(pair.Guest.Send(7, Bytes(3, 1)));
                Assert.IsFalse(pair.Guest.Send(1, null));
                Assert.IsFalse(pair.Guest.Send(1, new byte[0]));
                Assert.AreEqual(0, pair.Hub.PacketsSent);

                // A packet from somebody who is not in the room is thrown away without a word.
                pair.HostLink.Inject(99, WholePacket(5));
                Assert.AreEqual(0, Drain(pair.Host).Count);
                Assert.AreEqual(0, pair.Hub.Waiting(1));
            }
        }

        [Test]
        public void TheHostMaySendMoreThanTheGuestsCan()
        {
            using (Pair pair = new Pair(64))
            {
                pair.Join();
                Drain(pair.Host);
                Drain(pair.Guest);

                Assert.IsFalse(pair.Guest.Send(1, new byte[NetRoom.MaxClientMessageBytes + 1]));
                Assert.IsFalse(pair.Host.Send(2, new byte[NetClientRoom.MaxHostMessageBytes + 1]));
                Assert.AreEqual(0, pair.Hub.PacketsSent);

                // A guest can send what it is allowed to...
                byte[] most = Bytes(NetRoom.MaxClientMessageBytes, 4);
                Assert.IsTrue(pair.Guest.Send(1, most));
                List<NetEvent> events = Drain(pair.Host);
                Assert.AreEqual(1, events.Count);
                AssertSame(most, events[0].Data);

                // ...and when it says that a longer one is on its way, nobody hears of it.
                pair.HostLink.Inject(2, FirstPacket((uint)NetRoom.MaxClientMessageBytes + 1, 20));
                Assert.AreEqual(0, Drain(pair.Host).Count);

                // The host, whose messages show the game, may send more than that.
                byte[] more = Bytes(NetRoom.MaxClientMessageBytes + 5000, 5);
                Assert.IsTrue(pair.Host.Send(2, more));
                events = Drain(pair.Guest);
                Assert.AreEqual(1, events.Count);
                AssertSame(more, events[0].Data);
            }
        }

        [Test]
        public void TheLongestMessageOfTheHostArrivesInSomeDrains()
        {
            using (Pair pair = new Pair(1170))
            {
                pair.Join();
                Drain(pair.Host);
                Drain(pair.Guest);

                byte[] message = Bytes(NetClientRoom.MaxHostMessageBytes, 6);
                Assert.IsTrue(pair.Host.Send(2, message));
                int packets = PacketFraming.PacketCount(message.Length, 1170);
                Assert.AreEqual(packets, pair.Hub.PacketsSent);

                // A drain takes in a limited number of packets, so a message this long needs a few frames.
                List<NetEvent> events = new List<NetEvent>();
                int drains = 0;
                while (events.Count == 0 && drains < 20)
                {
                    events.AddRange(Drain(pair.Guest));
                    drains++;
                }
                Assert.AreEqual(1, events.Count);
                AssertSame(message, events[0].Data);
                Assert.AreEqual((packets + PacketTransport.PacketsPerDrain - 1) / PacketTransport.PacketsPerDrain, drains);
            }
        }

        [Test]
        public void TheEndsOfTwoMessagesAreNotMixedUp()
        {
            PacketHub hub = new PacketHub(32);
            HubLink hostLink = hub.Connect(1);
            using (PacketTransport host = new PacketTransport(hostLink, 1, 1, "Ann"))
            {
                host.AddMember(2, "Bob");
                host.AddMember(3, "Cy");
                Drain(host);

                // The pieces of what Bob and Cy say reach the host mixed up, each in its own order.
                byte[] fromBob = Bytes(300, 4);
                byte[] fromCy = Bytes(200, 5);
                List<byte[]> bob = Packets(fromBob, 32);
                List<byte[]> cy = Packets(fromCy, 32);
                for (int i = 0; i < Math.Max(bob.Count, cy.Count); i++)
                {
                    if (i < bob.Count)
                        hostLink.Inject(2, bob[i]);
                    if (i < cy.Count)
                        hostLink.Inject(3, cy[i]);
                }

                List<NetEvent> events = Drain(host);
                Assert.AreEqual(2, events.Count);
                Assert.AreEqual(3UL, events[0].Peer);
                AssertSame(fromCy, events[0].Data);
                Assert.AreEqual(2UL, events[1].Peer);
                AssertSame(fromBob, events[1].Data);
            }
        }

        [Test]
        public void AFloodOfPacketsIsTakenInAFewAtATime()
        {
            using (Pair pair = new Pair(64))
            {
                pair.Join();
                Drain(pair.Host);
                Drain(pair.Guest);

                int count = PacketTransport.PacketsPerDrain + 300;
                for (int i = 0; i < count; i++)
                    Assert.IsTrue(pair.Guest.Send(1, new[] { (byte)i, (byte)(i >> 8) }));
                Assert.AreEqual(count, pair.Hub.Waiting(1));

                // The frame is not held up by all of them; the rest is for the next one.
                List<NetEvent> first = Drain(pair.Host);
                Assert.AreEqual(PacketTransport.PacketsPerDrain, first.Count);
                Assert.AreEqual(300, pair.Hub.Waiting(1));
                List<NetEvent> second = Drain(pair.Host);
                Assert.AreEqual(300, second.Count);
                Assert.AreEqual(0, pair.Hub.Waiting(1));

                for (int i = 0; i < count; i++)
                {
                    NetEvent e = i < first.Count ? first[i] : second[i - first.Count];
                    Assert.AreEqual((byte)i, e.Data[0]);
                    Assert.AreEqual((byte)(i >> 8), e.Data[1]);
                }
            }
        }

        [Test]
        public void ALinkThatRefusesAPacketFailsTheSendAndTheHalfMessageIsForgotten()
        {
            using (Pair pair = new Pair(64))
            {
                pair.Join();
                Drain(pair.Host);
                Drain(pair.Guest);

                pair.Hub.RefuseAfter = 3;
                Assert.IsFalse(pair.Guest.Send(1, Bytes(1000, 7)));
                Assert.AreEqual(3, pair.Hub.PacketsSent);

                pair.Hub.RefuseAfter = -1;
                Assert.IsTrue(pair.Guest.Send(1, Bytes(10, 8)));
                List<NetEvent> events = Drain(pair.Host);
                Assert.AreEqual(1, events.Count);
                AssertSame(Bytes(10, 8), events[0].Data);
            }
        }

        [Test]
        public void ADisposedTransportGoesQuiet()
        {
            using (Pair pair = new Pair(64))
            {
                pair.Join();
                Drain(pair.Host);
                pair.Host.Dispose();
                pair.Host.Dispose();
                Assert.AreEqual(1, pair.Hub.Disposed);
                Assert.IsFalse(pair.Host.Send(2, Bytes(3, 1)));
                Assert.IsFalse(pair.Host.Poll(out _));
                Assert.IsFalse(pair.Host.AddMember(5, "Cy"));
                pair.Host.RemoveMember(2);
                pair.Host.Close("late");
                Assert.IsFalse(pair.Host.Poll(out _));
            }
        }

        // ---- a game over packets -------------------------------------------------------------------------------------

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

        [Test]
        public void AGameIsPlayedAcrossTheRoomOverSmallPackets()
        {
            using (GameHost host = new GameHost(new ModCatalog(new[] { TestSupport.ModsRoot })))
            using (Pair pair = new Pair(100))
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                NetHostRoom room = new NetHostRoom(host, pair.Host);
                host.SetOption("size", JsonValue.Of(9));
                Assert.IsTrue(host.StartNewGame());
                room.Submit(Place(5, 5));

                pair.Join();
                NetClientRoom guest = new NetClientRoom(host.Mod, pair.Guest);
                Pump(room, guest);
                Assert.IsTrue(guest.Joined);
                Assert.IsTrue(guest.Ready);
                Assert.AreEqual(2, guest.Players.Count);
                Assert.AreEqual("Ann", guest.Players[0].Name);
                Assert.AreEqual(2, guest.LocalPlayer.Seat);
                ViewModel shown = guest.LocalBoard.Queue.Display;
                Assert.AreEqual(1, shown.ObjectCount);
                Assert.AreEqual(2, shown.Turn);
                Assert.AreEqual(9, shown.GetCustom("board").Raw["columns"].AsInt());

                // The guest answers, and the host plays again.
                Assert.IsTrue(guest.CanAct);
                guest.Submit(Place(4, 4));
                Pump(room, guest);
                Assert.AreEqual(2, host.Session.View.ObjectCount);
                Assert.AreEqual(2, shown.ObjectCount);
                Assert.IsTrue(room.CanAct);
                room.Submit(Place(6, 6));
                Pump(room, guest);
                Assert.AreEqual(3, shown.ObjectCount);

                // The state of the game did not fit in one packet.
                Assert.Greater(pair.Hub.FirstPieces, 0);
                Assert.LessOrEqual(pair.Hub.LargestPacket, 100);

                // When the service says the host left, the guest's room is over.
                room.Dispose();
                pair.Guest.RemoveMember(1);
                Pump(guest);
                Assert.IsTrue(guest.IsClosed);
                StringAssert.Contains("host", guest.ClosedReason);
            }
        }
    }
}
