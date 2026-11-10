using System;
using System.Collections.Generic;

namespace BoardSandbox.Core.Luau
{
    public enum VmStatus
    {
        Ok = 0,
        Argument = 1,
        Compile = 2,
        Runtime = 3,
        Timeout = 4,
        Memory = 5,
        Json = 6,
        Faulted = 7,
        Internal = 8,
        State = 9
    }

    public sealed class VmOptions
    {
        public uint MemoryLimitMb = 64;
        public uint CallTimeoutMs = 250;
        public uint BootTimeoutMs = 2000;
        public bool DevMode;
        public int OptimizationLevel = 1;
        public int DebugLevel = 1;

        public VmOptions Clone()
        {
            return (VmOptions)MemberwiseClone();
        }
    }

    public readonly struct VmStats
    {
        public VmStats(bool faulted, long memoryBytes, long memoryPeakBytes, long callCount, long lastCallMicros, long droppedLogs)
        {
            Faulted = faulted;
            MemoryBytes = memoryBytes;
            MemoryPeakBytes = memoryPeakBytes;
            CallCount = callCount;
            LastCallMicros = lastCallMicros;
            DroppedLogs = droppedLogs;
        }

        public bool Faulted { get; }
        public long MemoryBytes { get; }
        public long MemoryPeakBytes { get; }
        public long CallCount { get; }
        public long LastCallMicros { get; }
        public long DroppedLogs { get; }
    }

    public readonly struct LogLine
    {
        public LogLine(int level, string message)
        {
            Level = level;
            Message = message;
        }

        public int Level { get; }
        public string Message { get; }

        public override string ToString()
        {
            return (Level == 2 ? "[error] " : Level == 1 ? "[warn] " : "") + Message;
        }
    }

    public readonly struct VmCallResult
    {
        public VmCallResult(VmStatus status, string text)
        {
            Status = status;
            Text = text;
        }

        public VmStatus Status { get; }

        // JSON when Status is Ok, otherwise an error message.
        public string Text { get; }

        public bool IsOk => Status == VmStatus.Ok;
    }

    public sealed class LuauException : Exception
    {
        public LuauException(VmStatus status, string message)
            : base(status + ": " + message)
        {
            Status = status;
            Detail = message;
        }

        public LuauException(VmStatus status, string message, Exception inner)
            : base(status + ": " + message, inner)
        {
            Status = status;
            Detail = message;
        }

        public VmStatus Status { get; }
        public string Detail { get; }
    }
}
