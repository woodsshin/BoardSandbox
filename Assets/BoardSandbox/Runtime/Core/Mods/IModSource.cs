using System;

namespace BoardSandbox.Core.Mods
{
    // Somewhere mods can be found. A catalog asks each of its sources for their mods whenever it refreshes.
    public interface IModSource
    {
        ModOrigin Origin { get; }

        // Where the source looks, for messages such as "No games were found in ...".
        string Description { get; }

        // Loads every mod the source provides. A mod that cannot be loaded goes into Errors and leaves the others alone.
        ModDiscovery Discover();
    }

    // The mods in the immediate subfolders of a folder; a subfolder is a mod when it has a config.json.
    public sealed class FolderModSource : IModSource
    {
        public FolderModSource(string folder, ModOrigin origin)
        {
            if (string.IsNullOrWhiteSpace(folder))
                throw new ArgumentException("a folder is required", nameof(folder));
            Folder = folder;
            Origin = origin;
        }

        public string Folder { get; }
        public ModOrigin Origin { get; }
        public string Description => Folder;

        public ModDiscovery Discover()
        {
            return ModLoader.Discover(Folder);
        }
    }
}
