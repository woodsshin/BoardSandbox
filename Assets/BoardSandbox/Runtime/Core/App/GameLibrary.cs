using System;
using System.Collections.Generic;
using BoardSandbox.Core.Mods;

namespace BoardSandbox.Core.App
{
    // What a screen that lists the games of a catalog needs to know: a search, an origin filter and the selected game.
    // Everything changes through the methods below, so a front end can call them between two frames.
    public sealed class GameLibrary
    {
        private readonly GameHost _host;
        private readonly List<ModEntry> _visible = new List<ModEntry>();
        private string _selectedId;

        public GameLibrary(GameHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            Reset();
        }

        public string Query { get; private set; } = string.Empty;

        // Null shows every origin.
        public ModOrigin? Origin { get; private set; }

        // The games that match the search and the filter, in display order.
        public IReadOnlyList<ModEntry> Visible => _visible;

        // The origins the filter can pick from.
        public IReadOnlyList<ModOrigin> Origins => _host.Catalog.Origins;

        // The position of the selected game in Visible, or -1 when nothing matches.
        public int SelectedIndex { get; private set; } = -1;

        public ModEntry Selected => SelectedIndex >= 0 ? _visible[SelectedIndex] : null;
        public string SelectedId => _selectedId;

        // Clears the search and the filter and selects the game on the table, or the first one.
        public void Reset()
        {
            Query = string.Empty;
            Origin = null;
            _selectedId = _host.Mod?.Manifest.Id;
            Refilter();
        }

        public void SetQuery(string query)
        {
            Query = query ?? string.Empty;
            Refilter();
        }

        public void SetOrigin(ModOrigin? origin)
        {
            Origin = origin;
            Refilter();
        }

        // Selects a game that is listed; any other id is ignored.
        public void Select(string id)
        {
            int index = IndexOf(id);
            if (index < 0)
                return;
            SelectedIndex = index;
            _selectedId = id;
        }

        // Moves the selection by whole entries and stops at both ends.
        public void Move(int steps)
        {
            if (_visible.Count == 0 || steps == 0)
                return;
            int index = Math.Max(0, Math.Min(_visible.Count - 1, Math.Max(0, SelectedIndex) + steps));
            SelectedIndex = index;
            _selectedId = _visible[index].Id;
        }

        // Looks at the sources again; the selection stays on the same game while it is still listed.
        public void Rescan()
        {
            _host.Catalog.Refresh();
            Refilter();
        }

        public bool IsRunning(ModEntry entry)
        {
            return entry != null && _host.IsRunning(entry.Mod);
        }

        // Starts a new game of the selected one.
        public bool Play()
        {
            ModEntry entry = Selected;
            return entry != null && _host.StartGame(entry.Mod);
        }

        private void Refilter()
        {
            if (Origin != null && !OriginIsListed(Origin.Value))
                Origin = null;
            _visible.Clear();
            _visible.AddRange(_host.Catalog.Search(Query, Origin));
            int index = IndexOf(_selectedId);
            if (index < 0 && _visible.Count > 0)
                index = 0;
            SelectedIndex = index;
            _selectedId = index >= 0 ? _visible[index].Id : null;
        }

        private bool OriginIsListed(ModOrigin origin)
        {
            foreach (ModOrigin listed in _host.Catalog.Origins)
            {
                if (listed == origin)
                    return true;
            }
            return false;
        }

        private int IndexOf(string id)
        {
            if (id == null)
                return -1;
            for (int i = 0; i < _visible.Count; i++)
            {
                if (string.Equals(_visible[i].Id, id, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }
    }
}
