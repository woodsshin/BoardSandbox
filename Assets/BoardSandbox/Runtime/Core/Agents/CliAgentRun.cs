using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace BoardSandbox.Core.Agents
{
    // A command-line agent at work: a child process in the folder of the game whose output is collected line by line.
    public sealed class CliAgentRun : AgentRun
    {
        // Where a file-driven agent finds its request, relative to the folder of the game. The folder starts with a
        // dot, so the mod loader does not look into it.
        public const string RequestFile = ".bse/request.md";

        public const string RequestPointer = "Read the file " + RequestFile + " in the current folder and do exactly what it asks.";

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private readonly object _gate = new object();
        private Process _process;
        private Timer _timeout;
        private bool _timedOut;
        private int _streamsOpen;

        public CliAgentRun(AgentProfile profile, string key, AgentRequest request)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            try
            {
                Launch(profile, key, request);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException
                                      || e is System.ComponentModel.Win32Exception || e is PlatformNotSupportedException)
            {
                Complete(false, "'" + profile.Command + "' could not be started: " + e.Message);
            }
        }

        private void Launch(AgentProfile profile, string key, AgentRequest request)
        {
            string program = AgentLocator.Find(profile.Command);
            if (program == null)
            {
                Complete(false, "'" + profile.Command + "' was not found on this computer.");
                return;
            }

            if (profile.Channel == PromptChannel.File)
            {
                string file = Path.Combine(request.Directory, RequestFile.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, request.Prompt, Utf8);
            }

            List<string> arguments = new List<string>();
            foreach (string argument in profile.Arguments)
                arguments.Add(argument.Replace(AgentProfile.PromptPlaceholder, RequestPointer));

            ProcessStartInfo info = new ProcessStartInfo
            {
                WorkingDirectory = request.Directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8
            };
            if (AgentLocator.NeedsCommandInterpreter(program))
            {
                // cmd strips the outermost pair of quotes with /s, which keeps the quotes around each word intact.
                info.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
                info.Arguments = "/d /s /c \"" + CommandLine(program, arguments) + "\"";
            }
            else
            {
                info.FileName = program;
                info.Arguments = CommandLine(null, arguments);
            }
            info.EnvironmentVariables["NO_COLOR"] = "1";
            info.EnvironmentVariables["TERM"] = "dumb";
            if (profile.KeyVariable.Length > 0 && !string.IsNullOrEmpty(key))
                info.EnvironmentVariables[profile.KeyVariable] = key;

            Process process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += (sender, e) => Received(AgentLineKind.Output, e.Data);
            process.ErrorDataReceived += (sender, e) => Received(AgentLineKind.Notice, e.Data);
            process.Exited += (sender, e) => Finish();
            _streamsOpen = 2;
            lock (_gate)
                _process = process;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Bytes go to the stream as they are, whatever code page the console of the process would use.
            try
            {
                Stream input = process.StandardInput.BaseStream;
                if (profile.Channel == PromptChannel.Stdin)
                {
                    byte[] bytes = Utf8.GetBytes(request.Prompt);
                    input.Write(bytes, 0, bytes.Length);
                    input.Flush();
                }
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The process ended or closed its input before reading everything; its exit tells the rest.
            }

            if (request.TimeoutSeconds > 0)
                _timeout = new Timer(_ => TimeOut(), null, TimeSpan.FromSeconds(request.TimeoutSeconds), Timeout.InfiniteTimeSpan);
        }

        // A null line marks the end of a stream.
        private void Received(AgentLineKind kind, string line)
        {
            if (line != null)
            {
                Post(kind, line);
                return;
            }
            if (Interlocked.Decrement(ref _streamsOpen) == 0)
                Finish();
        }

        // The result is only final once the process has exited and both streams have been read to the end, so that
        // no line of output arrives after the run reports that it is done.
        private void Finish()
        {
            Process process;
            lock (_gate)
                process = _process;
            if (process == null)
                return;
            int code;
            try
            {
                if (!process.HasExited || Volatile.Read(ref _streamsOpen) > 0)
                    return;
                code = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (Cancelled)
                Complete(false, "Cancelled.");
            else if (_timedOut)
                Complete(false, "The agent did not finish in time and was stopped.");
            else if (code != 0)
                Complete(false, "The agent ended with exit code " + code + ".");
            else
                Complete(true, string.Empty);
        }

        private void TimeOut()
        {
            if (!IsRunning)
                return;
            _timedOut = true;
            Stop();
        }

        protected override void Stop()
        {
            Process process;
            lock (_gate)
                process = _process;
            if (process == null)
                return;
            try
            {
                if (process.HasExited)
                    return;
                if (AgentLocator.IsWindows)
                    KillTree(process.Id);
                if (!process.HasExited)
                    process.Kill();
            }
            catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception || e is NotSupportedException)
            {
                // It ended on its own in the meantime.
            }
            // A process that the agent started may keep the output open; the run is over all the same.
            Complete(false, _timedOut ? "The agent did not finish in time and was stopped." : "Cancelled.");
        }

        // Tools installed through npm are batch files that start node; ending only the interpreter would leave the
        // agent running.
        private static void KillTree(int id)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = "/PID " + id + " /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process killer = Process.Start(info))
                    killer?.WaitForExit(3000);
            }
            catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception || e is IOException)
            {
            }
        }

        protected override void Release()
        {
            _timeout?.Dispose();
            _timeout = null;
            Process process;
            lock (_gate)
            {
                process = _process;
                _process = null;
            }
            process?.Dispose();
        }

        private static string CommandLine(string program, List<string> arguments)
        {
            StringBuilder builder = new StringBuilder();
            if (program != null)
                builder.Append(Quote(program));
            foreach (string argument in arguments)
            {
                if (builder.Length > 0)
                    builder.Append(' ');
                builder.Append(Quote(argument));
            }
            return builder.ToString();
        }

        // Quotes one word the way the C runtime of Windows splits a command line, which other platforms accept too.
        public static string Quote(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"', '\n' }) < 0)
                return argument;
            StringBuilder builder = new StringBuilder(argument.Length + 2);
            builder.Append('"');
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                    builder.Append('\\', backslashes * 2 + 1);
                else
                    builder.Append('\\', backslashes);
                backslashes = 0;
                builder.Append(c);
            }
            builder.Append('\\', backslashes * 2);
            builder.Append('"');
            return builder.ToString();
        }
    }
}
