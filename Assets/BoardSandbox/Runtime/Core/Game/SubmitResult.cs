using System;
using System.Collections.Generic;
using BoardSandbox.Core.Json;

namespace BoardSandbox.Core.Game
{
    public enum SubmitStatus
    {
        // The rules took the command and changed the game.
        Accepted,

        // The rules refused the command and nothing changed.
        Rejected,

        // An interceptor stopped the command before it reached the rules.
        Vetoed,

        // The host refused the packet before it reached the rules.
        Invalid,

        // The script failed while running the command; the session rolled back when it could.
        Faulted
    }

    public sealed class SubmitResult
    {
        private static readonly ViewEvent[] NoEvents = new ViewEvent[0];

        private SubmitResult(SubmitStatus status, string code, string message)
        {
            Status = status;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            Details = JsonValue.Null;
            Events = NoEvents;
            Traceback = string.Empty;
        }

        public SubmitStatus Status { get; private set; }
        public bool IsAccepted => Status == SubmitStatus.Accepted;
        public string Code { get; private set; }
        public string Message { get; private set; }
        public JsonValue Details { get; private set; }
        public IReadOnlyList<ViewEvent> Events { get; private set; }
        public string Traceback { get; private set; }

        // Faulted only: the session rebuilt the VM and restored the last good state.
        public bool Recovered { get; private set; }

        public static SubmitResult Accepted(IReadOnlyList<ViewEvent> events)
        {
            return new SubmitResult(SubmitStatus.Accepted, "OK", string.Empty) { Events = events ?? NoEvents };
        }

        public static SubmitResult Rejected(string code, string message, JsonValue details)
        {
            return new SubmitResult(SubmitStatus.Rejected, code, message) { Details = details ?? JsonValue.Null };
        }

        public static SubmitResult Vetoed(string code, string message)
        {
            return new SubmitResult(SubmitStatus.Vetoed, code, message);
        }

        public static SubmitResult Invalid(string code, string message)
        {
            return new SubmitResult(SubmitStatus.Invalid, code, message);
        }

        internal static SubmitResult Faulted(string code, string message, string traceback, bool recovered)
        {
            return new SubmitResult(SubmitStatus.Faulted, code, message) { Traceback = traceback ?? string.Empty, Recovered = recovered };
        }

        public override string ToString()
        {
            if (IsAccepted)
                return "accepted (" + Events.Count + " events)";
            return Status.ToString().ToLowerInvariant() + " " + Code + ": " + Message;
        }
    }

    public sealed class GameSessionException : Exception
    {
        public GameSessionException(string code, string message)
            : base(code + ": " + message)
        {
            Code = code;
            Detail = message;
        }

        public GameSessionException(string code, string message, Exception inner)
            : base(code + ": " + message, inner)
        {
            Code = code;
            Detail = message;
        }

        public string Code { get; }
        public string Detail { get; }
        public string Traceback { get; internal set; } = string.Empty;
    }
}
