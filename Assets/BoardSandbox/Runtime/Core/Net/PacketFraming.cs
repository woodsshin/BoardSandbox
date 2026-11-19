using System;

namespace BoardSandbox.Core.Net
{
    // A message of a room can be as long as 4 MiB, while a packet of some services holds little more than a thousand bytes
    // (Epic Online Services: 1170). A message that does not fit in a packet is cut into pieces that the packets carry one
    // after the other. The packets between two people arrive whole and in the order they were sent, so the receiver puts the
    // pieces together again as they come (PacketAssembler).
    //
    // A packet starts with a byte that says what it is: a whole message, the first piece of a longer one (followed by the
    // length of the whole message, 4 bytes, least significant first) or another piece of it.
    public static class PacketFraming
    {
        public const byte Whole = 0;
        public const byte First = 1;
        public const byte Next = 2;

        public const int WholeHeaderBytes = 1;
        public const int FirstHeaderBytes = 5;
        public const int NextHeaderBytes = 1;

        // A packet that holds less cannot carry a message of any length.
        public const int MinPacketBytes = FirstHeaderBytes + 1;

        // How many packets a message of the given length takes.
        public static int PacketCount(int messageBytes, int maxPacketBytes)
        {
            if (messageBytes < 1 || maxPacketBytes < MinPacketBytes)
                throw new ArgumentOutOfRangeException(nameof(messageBytes));
            if (messageBytes + WholeHeaderBytes <= maxPacketBytes)
                return 1;
            int rest = messageBytes - (maxPacketBytes - FirstHeaderBytes);
            int perPiece = maxPacketBytes - NextHeaderBytes;
            return 1 + (rest + perPiece - 1) / perPiece;
        }

        // Writes the packet that follows in the message into the buffer, which holds maxPacketBytes at least, and tells how long
        // it is. The offset is how much of the message has gone into packets already (0 for the first one) and moves on; the
        // message is done when it reaches the end.
        public static int Write(byte[] message, ref int offset, byte[] packet, int maxPacketBytes)
        {
            if (message == null || message.Length == 0)
                throw new ArgumentException("a message has at least one byte", nameof(message));
            if (maxPacketBytes < MinPacketBytes || packet == null || packet.Length < maxPacketBytes)
                throw new ArgumentException("the packet does not hold what the service allows", nameof(packet));
            if (offset < 0 || offset >= message.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            int left = message.Length - offset;
            if (offset == 0 && left + WholeHeaderBytes <= maxPacketBytes)
            {
                packet[0] = Whole;
                Buffer.BlockCopy(message, 0, packet, WholeHeaderBytes, left);
                offset = message.Length;
                return WholeHeaderBytes + left;
            }
            if (offset == 0)
            {
                int piece = Math.Min(left, maxPacketBytes - FirstHeaderBytes);
                packet[0] = First;
                packet[1] = (byte)message.Length;
                packet[2] = (byte)(message.Length >> 8);
                packet[3] = (byte)(message.Length >> 16);
                packet[4] = (byte)(message.Length >> 24);
                Buffer.BlockCopy(message, 0, packet, FirstHeaderBytes, piece);
                offset = piece;
                return FirstHeaderBytes + piece;
            }
            int next = Math.Min(left, maxPacketBytes - NextHeaderBytes);
            packet[0] = Next;
            Buffer.BlockCopy(message, offset, packet, NextHeaderBytes, next);
            offset += next;
            return NextHeaderBytes + next;
        }
    }

    // Puts the messages back together from the packets of one person. A packet that makes no sense is thrown away, and so is
    // the message it belonged to; the next message starts afresh, because whoever sends one puts all its pieces in a row.
    public sealed class PacketAssembler
    {
        // The first piece of a message says how long the whole will be, but it is not trusted: room is made only for the pieces
        // that really come, so that a packet which claims megabytes does not make us hold them.
        private const int FirstBufferBytes = 4096;

        private readonly int _maxMessageBytes;
        private byte[] _message;
        private int _expected;
        private int _filled;

        public PacketAssembler(int maxMessageBytes)
        {
            if (maxMessageBytes < 1)
                throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
            _maxMessageBytes = maxMessageBytes;
        }

        // How many packets were thrown away because they made no sense, each with the message that was on its way.
        public int Rejected { get; private set; }

        // True while the pieces of a longer message are still coming.
        public bool InProgress => _message != null;

        // Takes the first length bytes of a packet. Returns the message when this packet finished one; null while more pieces are
        // to come and for a packet that was thrown away.
        public byte[] Add(byte[] packet, int length)
        {
            if (packet == null || length < 1 || length > packet.Length)
                return Reject();
            switch (packet[0])
            {
                case PacketFraming.Whole:
                    return AddWhole(packet, length);
                case PacketFraming.First:
                    return AddFirst(packet, length);
                case PacketFraming.Next:
                    return AddNext(packet, length);
                default:
                    return Reject();
            }
        }

        private byte[] AddWhole(byte[] packet, int length)
        {
            Abandon();
            int size = length - PacketFraming.WholeHeaderBytes;
            if (size < 1 || size > _maxMessageBytes)
                return Reject();
            byte[] message = new byte[size];
            Buffer.BlockCopy(packet, PacketFraming.WholeHeaderBytes, message, 0, size);
            return message;
        }

        private byte[] AddFirst(byte[] packet, int length)
        {
            Abandon();
            if (length <= PacketFraming.FirstHeaderBytes)
                return Reject();
            uint total = packet[1] | ((uint)packet[2] << 8) | ((uint)packet[3] << 16) | ((uint)packet[4] << 24);
            int piece = length - PacketFraming.FirstHeaderBytes;
            if (total < 1 || total > (uint)_maxMessageBytes || piece > total)
                return Reject();
            int expected = (int)total;
            byte[] message = new byte[Math.Min(expected, Math.Max(FirstBufferBytes, piece))];
            Buffer.BlockCopy(packet, PacketFraming.FirstHeaderBytes, message, 0, piece);
            if (piece == expected)
                return message;
            _message = message;
            _expected = expected;
            _filled = piece;
            return null;
        }

        private byte[] AddNext(byte[] packet, int length)
        {
            int piece = length - PacketFraming.NextHeaderBytes;
            if (_message == null || piece < 1 || piece > _expected - _filled)
                return Reject();
            int needed = _filled + piece;
            if (needed > _message.Length)
            {
                // Twice as much each time, never more than the whole message, so that the buffer is exactly as long as the
                // message when the last piece arrives.
                int size = (int)Math.Min((long)_expected, Math.Max((long)_message.Length * 2, needed));
                Array.Resize(ref _message, size);
            }
            Buffer.BlockCopy(packet, PacketFraming.NextHeaderBytes, _message, _filled, piece);
            _filled = needed;
            if (_filled < _expected)
                return null;
            byte[] message = _message;
            Abandon();
            return message;
        }

        // A message that was on its way when a new one started, or when a packet made no sense, will not be finished any more.
        private void Abandon()
        {
            _message = null;
            _expected = 0;
            _filled = 0;
        }

        private byte[] Reject()
        {
            Abandon();
            Rejected++;
            return null;
        }
    }
}
