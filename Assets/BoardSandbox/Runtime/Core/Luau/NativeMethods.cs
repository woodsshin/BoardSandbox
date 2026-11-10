using System;
using System.Runtime.InteropServices;

namespace BoardSandbox.Core.Luau
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeOptions
    {
        public uint StructSize;
        public uint Flags;
        public uint MemoryLimitMb;
        public uint CallTimeoutMs;
        public uint BootTimeoutMs;
        public int OptimizationLevel;
        public int DebugLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeStats
    {
        public uint StructSize;
        public uint Faulted;
        public ulong MemoryBytes;
        public ulong MemoryPeakBytes;
        public ulong CallCount;
        public ulong LastCallMicros;
        public ulong DroppedLogs;
    }

    internal static class NativeMethods
    {
        public const string Library = "bse_luau";
        public const uint ExpectedAbiVersion = 1;
        public const uint FlagDev = 1;

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint bse_abi_version();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr bse_luau_version();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr bse_vm_create(ref NativeOptions options);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void bse_vm_destroy(IntPtr vm);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int bse_vm_add_module(IntPtr vm, byte[] path, int pathLength, byte[] source, int sourceLength);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int bse_vm_boot(IntPtr vm, byte[] entry, int entryLength);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int bse_vm_call(IntPtr vm, byte[] method, int methodLength, byte[] argsJson, int argsLength, uint timeoutMs);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr bse_vm_result(IntPtr vm, out int length);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr bse_vm_take_logs(IntPtr vm, out int length);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int bse_vm_get_stats(IntPtr vm, ref NativeStats stats);
    }
}
