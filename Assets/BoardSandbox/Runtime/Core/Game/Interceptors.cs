using System;
using System.Collections.Generic;

namespace BoardSandbox.Core.Game
{
    public interface ICommandInterceptor
    {
        // Return null to let the packet through, or Vetoed(...) to stop it before it reaches the rules.
        SubmitResult BeforeSubmit(GameSession session, InputPacket packet);

        void AfterSubmit(GameSession session, InputPacket packet, SubmitResult result);
    }

    // Stops a packet whose seat is not the seat the rules said is on turn, without running any script.
    public sealed class TurnGuard : ICommandInterceptor
    {
        public SubmitResult BeforeSubmit(GameSession session, InputPacket packet)
        {
            int turn = session.View.Turn;
            if (turn > 0 && !session.View.IsOver && packet.Seat != turn)
                return SubmitResult.Vetoed("NOT_YOUR_TURN", "seat " + packet.Seat + " is not on turn (seat " + turn + " is)");
            return null;
        }

        public void AfterSubmit(GameSession session, InputPacket packet, SubmitResult result)
        {
        }
    }

    public sealed class RejectionRecord
    {
        public RejectionRecord(long sequence, int seat, string source, string command, SubmitStatus status, string code, string message)
        {
            Sequence = sequence;
            Seat = seat;
            Source = source;
            Command = command;
            Status = status;
            Code = code;
            Message = message;
        }

        public long Sequence { get; }
        public int Seat { get; }
        public string Source { get; }
        public string Command { get; }
        public SubmitStatus Status { get; }
        public string Code { get; }
        public string Message { get; }
    }

    // Remembers every refused or failed packet so an agent or a UI can see what went wrong and how often.
    public sealed class RejectionLog : ICommandInterceptor
    {
        private readonly int _capacity;
        private readonly Queue<RejectionRecord> _records = new Queue<RejectionRecord>();
        private readonly Dictionary<string, int> _counts = new Dictionary<string, int>(StringComparer.Ordinal);

        public RejectionLog(int capacity = 200)
        {
            _capacity = Math.Max(1, capacity);
        }

        public int TotalCount { get; private set; }
        public IReadOnlyCollection<RejectionRecord> Records => _records;
        public IReadOnlyDictionary<string, int> CountsByCode => _counts;

        public SubmitResult BeforeSubmit(GameSession session, InputPacket packet)
        {
            return null;
        }

        public void AfterSubmit(GameSession session, InputPacket packet, SubmitResult result)
        {
            if (result.IsAccepted)
                return;
            TotalCount++;
            _counts.TryGetValue(result.Code, out int count);
            _counts[result.Code] = count + 1;
            _records.Enqueue(new RejectionRecord(packet.Sequence, packet.Seat, packet.Source, packet.Command == null ? "null" : packet.Command.ToJson(), result.Status,
                result.Code, result.Message));
            while (_records.Count > _capacity)
                _records.Dequeue();
        }

        public void Clear()
        {
            _records.Clear();
            _counts.Clear();
            TotalCount = 0;
        }
    }
}
