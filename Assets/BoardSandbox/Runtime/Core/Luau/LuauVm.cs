using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using BoardSandbox.Core.Json;

namespace BoardSandbox.Core.Luau
{
    public sealed class LuauVm : IDisposable
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly LogLine[] NoLogs = new LogLine[0];
        private static readonly object ProbeGate = new object();
        private static string _luauVersion;

        private readonly object _gate = new object();
        private IntPtr _handle;

        private LuauVm(IntPtr handle, VmOptions options)
        {
            _handle = handle;
            Options = options;
        }

        ~LuauVm()
        {
            Release();
        }

        public VmOptions Options { get; }

        public static string NativeLuauVersion
        {
            get
            {
                EnsureNativeLibrary();
                return _luauVersion;
            }
        }

        public bool IsDisposed
        {
            get
            {
                lock (_gate)
                {
                    return _handle == IntPtr.Zero;
                }
            }
        }

        public static LuauVm Create(VmOptions options = null)
        {
            EnsureNativeLibrary();
            VmOptions used = (options ?? new VmOptions()).Clone();
            NativeOptions native = new NativeOptions
            {
                StructSize = (uint)Marshal.SizeOf(typeof(NativeOptions)),
                Flags = used.DevMode ? NativeMethods.FlagDev : 0u,
                MemoryLimitMb = used.MemoryLimitMb,
                CallTimeoutMs = used.CallTimeoutMs,
                BootTimeoutMs = used.BootTimeoutMs,
                OptimizationLevel = used.OptimizationLevel,
                DebugLevel = used.DebugLevel
            };
            IntPtr handle = NativeMethods.bse_vm_create(ref native);
            if (handle == IntPtr.Zero)
                throw new LuauException(VmStatus.Internal, "the native VM could not be created");
            return new LuauVm(handle, used);
        }

        private static void EnsureNativeLibrary()
        {
            if (_luauVersion != null)
                return;
            lock (ProbeGate)
            {
                if (_luauVersion != null)
                    return;
                try
                {
                    uint abi = NativeMethods.bse_abi_version();
                    if (abi != NativeMethods.ExpectedAbiVersion)
                    {
                        throw new LuauException(VmStatus.Internal,
                            "the native library reports ABI version " + abi + " but this build expects " + NativeMethods.ExpectedAbiVersion);
                    }
                    _luauVersion = Marshal.PtrToStringAnsi(NativeMethods.bse_luau_version()) ?? "unknown";
                }
                catch (DllNotFoundException e)
                {
                    throw new LuauException(VmStatus.Internal, "the native library '" + NativeMethods.Library + "' could not be loaded: " + e.Message, e);
                }
                catch (BadImageFormatException e)
                {
                    throw new LuauException(VmStatus.Internal, "the native library '" + NativeMethods.Library + "' has the wrong architecture: " + e.Message, e);
                }
                catch (EntryPointNotFoundException e)
                {
                    throw new LuauException(VmStatus.Internal, "the native library '" + NativeMethods.Library + "' is not a compatible build: " + e.Message, e);
                }
            }
        }

        public void AddModule(string path, string source)
        {
            if (path == null)
                throw new ArgumentNullException(nameof(path));
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            byte[] pathBytes = Utf8.GetBytes(path);
            byte[] sourceBytes = Utf8.GetBytes(source);
            lock (_gate)
            {
                ThrowIfDisposed();
                int status = NativeMethods.bse_vm_add_module(_handle, pathBytes, pathBytes.Length, sourceBytes, sourceBytes.Length);
                if (status != 0)
                    throw new LuauException((VmStatus)status, "cannot add module '" + path + "': " + ReadResultLocked());
            }
        }

        public void Boot(string entry)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));
            byte[] entryBytes = Utf8.GetBytes(entry);
            lock (_gate)
            {
                ThrowIfDisposed();
                int status = NativeMethods.bse_vm_boot(_handle, entryBytes, entryBytes.Length);
                if (status != 0)
                    throw new LuauException((VmStatus)status, ReadResultLocked());
            }
        }

        public VmCallResult Call(string method, string argsJson = null, uint timeoutMs = 0)
        {
            if (method == null)
                throw new ArgumentNullException(nameof(method));
            byte[] methodBytes = Utf8.GetBytes(method);
            byte[] argBytes = argsJson == null ? null : Utf8.GetBytes(argsJson);
            lock (_gate)
            {
                ThrowIfDisposed();
                int status = NativeMethods.bse_vm_call(_handle, methodBytes, methodBytes.Length, argBytes, argBytes == null ? 0 : argBytes.Length, timeoutMs);
                return new VmCallResult((VmStatus)status, ReadResultLocked());
            }
        }

        public JsonValue CallJson(string method, JsonValue args = null, uint timeoutMs = 0)
        {
            VmCallResult result = Call(method, args == null ? null : args.ToJson(), timeoutMs);
            if (!result.IsOk)
                throw new LuauException(result.Status, result.Text);
            try
            {
                return JsonValue.Parse(result.Text);
            }
            catch (JsonParseException e)
            {
                throw new LuauException(VmStatus.Json, "the VM returned malformed JSON: " + e.Message, e);
            }
        }

        public IReadOnlyList<LogLine> TakeLogs()
        {
            string json;
            lock (_gate)
            {
                ThrowIfDisposed();
                IntPtr text = NativeMethods.bse_vm_take_logs(_handle, out int length);
                if (length <= 2)
                    return NoLogs;
                json = Marshal.PtrToStringUTF8(text, length);
            }
            JsonValue parsed = JsonValue.Parse(json);
            List<LogLine> lines = new List<LogLine>(parsed.Count);
            foreach (JsonValue entry in parsed.Items)
                lines.Add(new LogLine(entry["l"].AsInt(), entry["m"].AsString()));
            return lines;
        }

        public VmStats GetStats()
        {
            NativeStats native = new NativeStats { StructSize = (uint)Marshal.SizeOf(typeof(NativeStats)) };
            lock (_gate)
            {
                ThrowIfDisposed();
                int status = NativeMethods.bse_vm_get_stats(_handle, ref native);
                if (status != 0)
                    throw new LuauException((VmStatus)status, "cannot read VM statistics");
            }
            return new VmStats(native.Faulted != 0, (long)native.MemoryBytes, (long)native.MemoryPeakBytes, (long)native.CallCount, (long)native.LastCallMicros,
                (long)native.DroppedLogs);
        }

        public void Dispose()
        {
            Release();
            GC.SuppressFinalize(this);
        }

        private void Release()
        {
            lock (_gate)
            {
                if (_handle == IntPtr.Zero)
                    return;
                NativeMethods.bse_vm_destroy(_handle);
                _handle = IntPtr.Zero;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_handle == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(LuauVm));
        }

        private string ReadResultLocked()
        {
            IntPtr text = NativeMethods.bse_vm_result(_handle, out int length);
            return length <= 0 ? string.Empty : Marshal.PtrToStringUTF8(text, length);
        }
    }
}
