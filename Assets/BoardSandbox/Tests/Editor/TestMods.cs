using System;
using System.Collections.Generic;
using BoardSandbox.Core.Mods;

namespace BoardSandbox.Tests
{
    // Small mods and mod sources that exist only in memory or in a temporary folder.
    internal static class TestMods
    {
        public const string MinimalScript = "local rules = {} function rules.new_game() bse.turn(1) end function rules.command() end return rules";

        public static string Manifest(string id, string name, string description = "", string extra = "")
        {
            return "{\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"description\":\"" + description + "\",\"api\":1,\"entry\":\"main.luau\"" + extra + "}";
        }

        public static ModPackage Mod(string id, string name, string description = "", string extra = "", string script = MinimalScript)
        {
            return new ModPackage(Manifest(id, name, description, extra), new Dictionary<string, string> { { "main.luau", script } });
        }

        public static void WriteMod(string root, string id, string name, string script = MinimalScript)
        {
            TestSupport.WriteFile(root, id + "/config.json", Manifest(id, name));
            TestSupport.WriteFile(root, id + "/main.luau", script);
        }
    }

    internal sealed class ListSource : IModSource
    {
        private readonly ModPackage[] _mods;

        public ListSource(ModOrigin origin, string description, params ModPackage[] mods)
        {
            Origin = origin;
            Description = description;
            _mods = mods;
        }

        public ModOrigin Origin { get; }
        public string Description { get; }

        public ModDiscovery Discover()
        {
            ModDiscovery found = new ModDiscovery();
            found.Mods.AddRange(_mods);
            return found;
        }
    }

    internal sealed class FailingSource : IModSource
    {
        public ModOrigin Origin => ModOrigin.Workshop;
        public string Description => "the subscribed items";

        public ModDiscovery Discover()
        {
            throw new InvalidOperationException("the service is not running");
        }
    }
}
