using System;
using System.Collections.Generic;

namespace BoardSandbox.Core.Agents
{
    public enum AgentLineKind
    {
        // What the agent says.
        Output,

        // What the agent reports beside its answer, such as progress on its error stream.
        Notice
    }

    public readonly struct AgentLine
    {
        public AgentLine(AgentLineKind kind, string text)
        {
            Kind = kind;
            Text = text ?? string.Empty;
        }

        public AgentLineKind Kind { get; }
        public string Text { get; }
    }

    // What an agent is asked to do in the folder of a game.
    public sealed class AgentRequest
    {
        public AgentRequest(string directory, string prompt, string system = "")
        {
            Directory = directory ?? throw new ArgumentNullException(nameof(directory));
            Prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
            System = system ?? string.Empty;
        }

        public string Directory { get; }

        // Everything the agent has to read. A Cli agent gets nothing else.
        public string Prompt { get; }

        // The standing instructions, for agents that take them apart from the request.
        public string System { get; }

        public double TimeoutSeconds { get; set; } = 600;
    }

    // One request on its way. The work happens on other threads; the owner polls from its own.
    public abstract class AgentRun : IDisposable
    {
        private const int MaxLineLength = 4000;
        private const int MaxPending = 4000;

        private readonly object _gate = new object();
        private readonly Queue<AgentLine> _pending = new Queue<AgentLine>();
        private bool _finished;
        private bool _succeeded;
        private string _error = string.Empty;
        private string _reply = string.Empty;

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                    return !_finished;
            }
        }

        // Meaningful once the run has ended.
        public bool Succeeded
        {
            get
            {
                lock (_gate)
                    return _finished && _succeeded;
            }
        }

        public string Error
        {
            get
            {
                lock (_gate)
                    return _error;
            }
        }

        // The whole answer of an agent that returns its work as text; empty for agents that edit files themselves.
        public string Reply
        {
            get
            {
                lock (_gate)
                    return _reply;
            }
        }

        public bool Cancelled { get; private set; }

        // Moves the lines that arrived since the last call into the list and returns how many there were.
        public int Drain(List<AgentLine> into)
        {
            if (into == null)
                throw new ArgumentNullException(nameof(into));
            lock (_gate)
            {
                int count = _pending.Count;
                while (_pending.Count > 0)
                    into.Add(_pending.Dequeue());
                return count;
            }
        }

        public void Cancel()
        {
            if (!IsRunning)
                return;
            Cancelled = true;
            Stop();
        }

        public void Dispose()
        {
            Cancel();
            Release();
        }

        protected abstract void Stop();

        protected virtual void Release()
        {
        }

        protected void Post(AgentLineKind kind, string text)
        {
            if (text == null)
                return;
            text = TerminalText.Clean(text);
            if (text.Length > MaxLineLength)
                text = text.Substring(0, MaxLineLength) + " …";
            lock (_gate)
            {
                if (_finished)
                    return;
                if (_pending.Count >= MaxPending)
                    _pending.Dequeue();
                _pending.Enqueue(new AgentLine(kind, text));
            }
        }

        protected void Complete(bool succeeded, string error, string reply = "")
        {
            lock (_gate)
            {
                if (_finished)
                    return;
                _finished = true;
                _succeeded = succeeded;
                _error = error ?? string.Empty;
                _reply = reply ?? string.Empty;
            }
        }
    }

    // Starts requests. The studio only knows this, so tests can put a pretend agent in its place.
    public interface IAgentLauncher
    {
        // Why the agent cannot be used right now, such as a program that is not installed or a missing key; empty
        // when it is ready.
        string Readiness(AgentProfile profile, AgentSettings settings);

        AgentRun Start(AgentProfile profile, AgentSettings settings, AgentRequest request);
    }

    public sealed class AgentLauncher : IAgentLauncher
    {
        public string Readiness(AgentProfile profile, AgentSettings settings)
        {
            if (profile == null)
                return "No agent is selected.";
            if (profile.Kind == AgentKind.Cli)
                return AgentLocator.Find(profile.Command) == null ? "'" + profile.Command + "' was not found on this computer." : string.Empty;
            if (!profile.KeyOptional && settings.KeyFor(profile).Length == 0)
                return "This agent needs a key. Enter it with /key <your key>.";
            return settings.ModelFor(profile).Length == 0 ? "This agent needs a model name. Set it with /model <name>." : string.Empty;
        }

        public AgentRun Start(AgentProfile profile, AgentSettings settings, AgentRequest request)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (profile.Kind == AgentKind.Cli)
                return new CliAgentRun(profile, settings.KeyFor(profile), request);
            return new ApiAgentRun(profile.Endpoint, settings.ModelFor(profile), settings.KeyFor(profile), request);
        }
    }

    // Programs written for a terminal colour their output with escape sequences, which a plain text view shows as
    // garbage.
    public static class TerminalText
    {
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            if (text.IndexOf('\u001b') < 0 && text.IndexOf('\r') < 0 && text.IndexOf('\t') < 0)
                return text;

            System.Text.StringBuilder builder = new System.Text.StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\u001b')
                {
                    i = SkipEscape(text, i);
                    continue;
                }
                if (c == '\t')
                    builder.Append("    ");
                else if (c != '\r' && (c >= ' ' || c == '\n'))
                    builder.Append(c);
                i++;
            }
            return builder.ToString();
        }

        private static int SkipEscape(string text, int start)
        {
            int i = start + 1;
            if (i >= text.Length)
                return i;
            char kind = text[i];
            if (kind == '[')
            {
                // A control sequence ends with a letter or one of a few symbols.
                i++;
                while (i < text.Length && (text[i] < '@' || text[i] > '~'))
                    i++;
                return Math.Min(text.Length, i + 1);
            }
            if (kind == ']')
            {
                // An operating system command ends with BEL or ESC \.
                i++;
                while (i < text.Length && text[i] != '\u0007' && text[i] != '\u001b')
                    i++;
                if (i < text.Length && text[i] == '\u001b')
                    i++;
                return Math.Min(text.Length, i + 1);
            }
            return i + 1;
        }
    }
}
