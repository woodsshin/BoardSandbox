using System;
using System.Collections.Generic;
using BoardSandbox.Core.Json;

namespace BoardSandbox.Core.Agents
{
    public enum AgentKind
    {
        // A coding agent that is installed on this computer and runs as a child process in the folder of the game.
        // It signs in with the player's own account and edits the files itself.
        Cli,

        // A chat endpoint that speaks the OpenAI chat completions protocol. The answer carries the files as text.
        Api
    }

    public enum PromptChannel
    {
        // The request is written to the standard input of the agent.
        Stdin,

        // The request is written to a file in the folder of the game and the command line only points at it.
        File
    }

    // How to reach one AI agent. Nothing a player types ever becomes part of a command line: the request travels
    // through standard input, a file or the body of a web request.
    public sealed class AgentProfile
    {
        // Stands in an argument for the sentence that tells a file-driven agent where its request is.
        public const string PromptPlaceholder = "{prompt}";

        private readonly List<string> _arguments = new List<string>();

        public AgentProfile(string id, string name, AgentKind kind)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("an agent needs an id", nameof(id));
            Id = id.Trim();
            Name = string.IsNullOrWhiteSpace(name) ? Id : name.Trim();
            Kind = kind;
        }

        public string Id { get; }
        public string Name { get; }
        public AgentKind Kind { get; }

        // Cli: the program to run, looked up on the PATH unless it is a path.
        public string Command { get; set; } = string.Empty;
        public IReadOnlyList<string> Arguments => _arguments;
        public PromptChannel Channel { get; set; } = PromptChannel.Stdin;

        // Api: the address of the chat completions endpoint and the model to ask for.
        public string Endpoint { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;

        // The environment variable that holds the key. An Api agent sends it as a bearer token; a Cli agent gets a
        // stored key through this variable. Empty when no key is involved.
        public string KeyVariable { get; set; } = string.Empty;

        // Whether an Api agent works without a key, as a model server on this computer does.
        public bool KeyOptional { get; set; }

        // How to get it going, shown when the agent is chosen: where it comes from and how to sign in.
        public string Hint { get; set; } = string.Empty;

        public AgentProfile WithArguments(params string[] arguments)
        {
            _arguments.Clear();
            if (arguments != null)
            {
                foreach (string argument in arguments)
                {
                    if (argument != null)
                        _arguments.Add(argument);
                }
            }
            return this;
        }

        public JsonValue ToJson()
        {
            JsonValue json = JsonValue.NewObject().Set("id", Id).Set("name", Name).Set("kind", Kind == AgentKind.Cli ? "cli" : "api");
            if (Kind == AgentKind.Cli)
            {
                JsonValue arguments = JsonValue.NewArray();
                foreach (string argument in _arguments)
                    arguments.Add(argument);
                json.Set("command", Command).Set("args", arguments).Set("prompt", Channel == PromptChannel.Stdin ? "stdin" : "file");
            }
            else
            {
                json.Set("endpoint", Endpoint).Set("model", Model).Set("key_optional", KeyOptional);
            }
            json.Set("key_env", KeyVariable).Set("hint", Hint);
            return json;
        }

        // Reads a profile as ToJson writes it; null when the value does not describe one.
        public static AgentProfile FromJson(JsonValue json)
        {
            if (json == null || !json.IsObject)
                return null;
            string id = json["id"].AsString().Trim();
            string kindName = json["kind"].AsString("cli").Trim().ToLowerInvariant();
            if (id.Length == 0 || id.Length > 40 || (kindName != "cli" && kindName != "api"))
                return null;

            AgentProfile profile = new AgentProfile(id, json["name"].AsString(id), kindName == "cli" ? AgentKind.Cli : AgentKind.Api)
            {
                Command = json["command"].AsString().Trim(),
                Channel = json["prompt"].AsString("stdin").Trim().ToLowerInvariant() == "file" ? PromptChannel.File : PromptChannel.Stdin,
                Endpoint = json["endpoint"].AsString().Trim(),
                Model = json["model"].AsString().Trim(),
                KeyVariable = json["key_env"].AsString().Trim(),
                KeyOptional = json["key_optional"].AsBool(),
                Hint = json["hint"].AsString()
            };
            foreach (JsonValue argument in json["args"].Items)
            {
                if (argument.IsString)
                    profile._arguments.Add(argument.AsString());
            }
            if (profile.Kind == AgentKind.Cli && profile.Command.Length == 0)
                return null;
            if (profile.Kind == AgentKind.Api && profile.Endpoint.Length == 0)
                return null;
            return profile;
        }

        // The agents the app knows without any configuration. Command lines and model names of these tools change
        // often; a profile with the same id in the settings file replaces the one here.
        public static List<AgentProfile> Defaults()
        {
            return new List<AgentProfile>
            {
                new AgentProfile("claude", "Claude Code", AgentKind.Cli)
                {
                    Command = "claude",
                    Hint = "Install: npm install -g @anthropic-ai/claude-code, then run 'claude' once in a terminal to sign in. Uses your Claude plan or API credit."
                }.WithArguments("-p", "--permission-mode", "acceptEdits"),

                new AgentProfile("codex", "OpenAI Codex", AgentKind.Cli)
                {
                    Command = "codex",
                    Hint = "Install: npm install -g @openai/codex, then run 'codex' once in a terminal to sign in with ChatGPT (the Free plan has a small allowance)."
                }.WithArguments("exec", "--sandbox", "workspace-write", "--skip-git-repo-check", "-"),

                new AgentProfile("gemini", "Gemini CLI", AgentKind.Cli)
                {
                    Command = "gemini",
                    KeyVariable = "GEMINI_API_KEY",
                    Hint = "Install: npm install -g @google/gemini-cli. Works with a Gemini API key: get a free one at aistudio.google.com/apikey and enter it with /key."
                }.WithArguments("--approval-mode", "auto_edit"),

                new AgentProfile("antigravity", "Antigravity CLI", AgentKind.Cli)
                {
                    Command = "agy",
                    Channel = PromptChannel.File,
                    Hint = "Google's successor to the Gemini CLI. Install it from antigravity.google and run 'agy' once in a terminal to sign in with a Google account (free quota)."
                }.WithArguments("--mode", "accept-edits", "-p", PromptPlaceholder),

                new AgentProfile("grok", "Grok Build", AgentKind.Cli)
                {
                    Command = "grok",
                    Channel = PromptChannel.File,
                    KeyVariable = "XAI_API_KEY",
                    Hint = "Install it from x.ai/cli and run 'grok' once in a terminal to sign in. It runs with --always-approve, so it may run any tool without asking."
                }.WithArguments("--no-auto-update", "--always-approve", "-p", PromptPlaceholder),

                new AgentProfile("gemini-api", "Gemini API (free key)", AgentKind.Api)
                {
                    Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
                    Model = "gemini-3.8-flash",
                    KeyVariable = "GEMINI_API_KEY",
                    Hint = "Nothing to install. Get a free key at aistudio.google.com/apikey and enter it with /key."
                },

                new AgentProfile("openrouter", "OpenRouter (free models)", AgentKind.Api)
                {
                    Endpoint = "https://openrouter.ai/api/v1/chat/completions",
                    Model = "openrouter/free",
                    KeyVariable = "OPENROUTER_API_KEY",
                    Hint = "Nothing to install. Create a key at openrouter.ai/keys and enter it with /key. Pick any model whose name ends in ':free' with /model."
                },

                new AgentProfile("ollama", "Ollama (this computer)", AgentKind.Api)
                {
                    Endpoint = "http://localhost:11434/v1/chat/completions",
                    Model = "qwen2.5-coder",
                    KeyOptional = true,
                    Hint = "Runs a model on this computer, free and offline. Install Ollama, then 'ollama pull qwen2.5-coder'. Choose another model with /model."
                },

                new AgentProfile("openai-api", "OpenAI API", AgentKind.Api)
                {
                    Endpoint = "https://api.openai.com/v1/chat/completions",
                    Model = "gpt-5.5",
                    KeyVariable = "OPENAI_API_KEY",
                    Hint = "Needs an API key from platform.openai.com (paid). Enter it with /key."
                },

                new AgentProfile("grok-api", "xAI Grok API", AgentKind.Api)
                {
                    Endpoint = "https://api.x.ai/v1/chat/completions",
                    Model = "grok-4",
                    KeyVariable = "XAI_API_KEY",
                    Hint = "Needs an API key from console.x.ai. Enter it with /key."
                },

                new AgentProfile("claude-api", "Claude API", AgentKind.Api)
                {
                    Endpoint = "https://api.anthropic.com/v1/chat/completions",
                    Model = "claude-sonnet-5-5",
                    KeyVariable = "ANTHROPIC_API_KEY",
                    Hint = "Needs an API key from console.anthropic.com (paid). Enter it with /key."
                }
            };
        }
    }
}
