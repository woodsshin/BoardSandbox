using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BoardSandbox.Core.Json;

namespace BoardSandbox.Core.Agents
{
    // The agents a player can choose from and what the player told the app about them: which one to use, keys and
    // model names. It is kept in one JSON file that can also be edited by hand to add or correct an agent.
    public sealed class AgentSettings
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private readonly List<AgentProfile> _profiles = new List<AgentProfile>();
        private readonly Dictionary<string, string> _keys = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _models = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<JsonValue> _custom = new List<JsonValue>();
        private readonly Func<string, string> _environment;

        // A null path keeps everything in memory. The environment is where keys are looked up first.
        public AgentSettings(string path = null, Func<string, string> environment = null)
        {
            Path = path;
            _environment = environment ?? Environment.GetEnvironmentVariable;
            _profiles.AddRange(AgentProfile.Defaults());
            SelectedId = _profiles[0].Id;
        }

        public string Path { get; }
        public IReadOnlyList<AgentProfile> Profiles => _profiles;
        public string SelectedId { get; private set; }

        // Why the file could not be read or written the last time; empty when it went well.
        public string Problem { get; private set; } = string.Empty;

        public AgentProfile Selected => Find(SelectedId) ?? _profiles[0];

        public AgentProfile Find(string id)
        {
            foreach (AgentProfile profile in _profiles)
            {
                if (string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase))
                    return profile;
            }
            return null;
        }

        public bool Select(string id)
        {
            AgentProfile profile = Find(id);
            if (profile == null)
                return false;
            SelectedId = profile.Id;
            return true;
        }

        // The key for an agent: the environment variable of its profile wins over a stored key. Empty for none.
        public string KeyFor(AgentProfile profile)
        {
            if (profile == null)
                return string.Empty;
            if (profile.KeyVariable.Length > 0)
            {
                string fromEnvironment = _environment(profile.KeyVariable);
                if (!string.IsNullOrWhiteSpace(fromEnvironment))
                    return fromEnvironment.Trim();
            }
            return _keys.TryGetValue(profile.Id, out string stored) ? stored : string.Empty;
        }

        public bool HasStoredKey(AgentProfile profile)
        {
            return profile != null && _keys.ContainsKey(profile.Id);
        }

        // An empty key forgets the stored one.
        public void SetKey(AgentProfile profile, string key)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            key = key == null ? string.Empty : key.Trim();
            if (key.Length == 0)
                _keys.Remove(profile.Id);
            else
                _keys[profile.Id] = key;
        }

        public string ModelFor(AgentProfile profile)
        {
            if (profile == null)
                return string.Empty;
            return _models.TryGetValue(profile.Id, out string chosen) ? chosen : profile.Model;
        }

        // An empty name goes back to the model of the profile.
        public void SetModel(AgentProfile profile, string model)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            model = model == null ? string.Empty : model.Trim();
            if (model.Length == 0)
                _models.Remove(profile.Id);
            else
                _models[profile.Id] = model;
        }

        // Reads the file when there is one. A file that cannot be read leaves the defaults in place.
        public bool Load()
        {
            Problem = string.Empty;
            if (Path == null || !File.Exists(Path))
                return false;
            try
            {
                Apply(JsonValue.Parse(File.ReadAllText(Path, Utf8)));
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonParseException)
            {
                Problem = "The agent settings could not be read: " + e.Message;
                return false;
            }
        }

        public bool Save()
        {
            Problem = string.Empty;
            if (Path == null)
                return true;
            try
            {
                string folder = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(folder))
                    Directory.CreateDirectory(folder);
                File.WriteAllText(Path, ToJson().ToJson(true), Utf8);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Problem = "The agent settings could not be saved: " + e.Message;
                return false;
            }
        }

        public JsonValue ToJson()
        {
            JsonValue keys = JsonValue.NewObject();
            foreach (KeyValuePair<string, string> pair in _keys)
                keys.Set(pair.Key, pair.Value);
            JsonValue models = JsonValue.NewObject();
            foreach (KeyValuePair<string, string> pair in _models)
                models.Set(pair.Key, pair.Value);
            JsonValue custom = JsonValue.NewArray();
            foreach (JsonValue profile in _custom)
                custom.Add(profile.DeepClone());
            return JsonValue.NewObject().Set("selected", SelectedId).Set("keys", keys).Set("models", models).Set("profiles", custom);
        }

        public void Apply(JsonValue json)
        {
            if (json == null || !json.IsObject)
                return;
            _keys.Clear();
            _models.Clear();
            _custom.Clear();
            _profiles.Clear();
            _profiles.AddRange(AgentProfile.Defaults());

            foreach (JsonValue item in json["profiles"].Items)
            {
                AgentProfile profile = AgentProfile.FromJson(item);
                if (profile == null)
                    continue;
                _custom.Add(item.DeepClone());
                int existing = _profiles.FindIndex(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0)
                    _profiles[existing] = profile;
                else
                    _profiles.Add(profile);
            }
            foreach (KeyValuePair<string, JsonValue> pair in json["keys"].Members)
            {
                if (pair.Value.IsString && pair.Value.AsString().Trim().Length > 0)
                    _keys[pair.Key] = pair.Value.AsString().Trim();
            }
            foreach (KeyValuePair<string, JsonValue> pair in json["models"].Members)
            {
                if (pair.Value.IsString && pair.Value.AsString().Trim().Length > 0)
                    _models[pair.Key] = pair.Value.AsString().Trim();
            }
            SelectedId = _profiles[0].Id;
            Select(json["selected"].AsString());
        }
    }
}
