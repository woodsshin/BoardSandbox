using System;
using BoardSandbox.Core.Mods;

namespace BoardSandbox.Core.Net
{
    // What the host of a room decides before the room opens.
    public sealed class RoomOptions
    {
        // The computer plays every seat that nobody sits at, and the host starts out against it. People who come into the
        // room wait, watching, until the host starts the next game: that game gives them the seats of the computer. A
        // person who leaves hands their seat back to the computer, which plays the game on from where it stands.
        public bool ComputerFills;

        // How many people the game is played by, for a game that lets the host choose (ModManifest.ChoosesPlayerCount);
        // 0 leaves it to the game.
        public int Players;
    }

    // The choices a host is offered for a game before its room opens, and the options they make. It knows nothing about
    // the screen, so a front end only draws it and passes on what the player picks.
    public sealed class RoomSetup
    {
        // A game that takes more players than this has no buttons for all the numbers.
        public const int MaxPlayerChoices = 8;

        private int _players;

        public RoomSetup(ModManifest manifest)
        {
            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            CanPlayComputer = manifest.HasComputerPlayer && !manifest.SplitScreen && !manifest.Cooperative && manifest.MaxPlayers >= 2;
            ChoosesPlayers = manifest.ChoosesPlayerCount;
            PlayComputer = CanPlayComputer;
            Players = manifest.MinPlayers;
        }

        public ModManifest Manifest { get; }

        // Whether the host may open the room against the computer.
        public bool CanPlayComputer { get; }

        // Whether the host may choose how many people play.
        public bool ChoosesPlayers { get; }

        // True when there is something to choose; if not, the room has no use for a setup and opens at once.
        public bool HasChoices => CanPlayComputer || ChoosesPlayers;

        // Whether the host opens the room against the computer. A game without a computer player cannot be.
        public bool PlayComputer { get; set; }

        // The numbers of players that can be chosen, from the fewest to the most.
        public int FewestPlayers => Manifest.MinPlayers;
        public int MostPlayers => Math.Min(Manifest.MaxPlayers, Manifest.MinPlayers + MaxPlayerChoices - 1);

        // How many people play; always one of the numbers the game takes.
        public int Players
        {
            get => _players;
            set => _players = Math.Max(FewestPlayers, Math.Min(MostPlayers, value));
        }

        public RoomOptions ToOptions()
        {
            return new RoomOptions
            {
                ComputerFills = CanPlayComputer && PlayComputer,
                Players = ChoosesPlayers ? Players : 0
            };
        }
    }
}
