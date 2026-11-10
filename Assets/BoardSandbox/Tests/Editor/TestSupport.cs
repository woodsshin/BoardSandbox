using System;
using System.Collections.Generic;
using System.IO;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Luau;
using BoardSandbox.Core.Mods;

namespace BoardSandbox.Tests
{
    internal static class TestSupport
    {
        public static string ModsRoot
        {
            get
            {
#if UNITY_EDITOR
                return Path.Combine(UnityEngine.Application.dataPath, "StreamingAssets", "Mods");
#else
                string fromEnvironment = Environment.GetEnvironmentVariable("BSE_MODS_DIR");
                if (!string.IsNullOrEmpty(fromEnvironment))
                    return fromEnvironment;
                return Path.GetFullPath(Path.Combine("Assets", "StreamingAssets", "Mods"));
#endif
            }
        }

        public static ModPackage LoadGomoku()
        {
            return ModLoader.LoadDirectory(Path.Combine(ModsRoot, "gomoku"));
        }

        public static ModPackage InlineMod(string source, string optionsJson = "{}", int maxPlayers = 2)
        {
            string manifest = "{\"id\":\"inline\",\"name\":\"Inline\",\"api\":1,\"entry\":\"main.luau\",\"players\":{\"min\":1,\"max\":" + maxPlayers + "},\"options\":" +
                              optionsJson + "}";
            return new ModPackage(manifest, new Dictionary<string, string> { { "main.luau", source } });
        }

        public static GameSession StartGomoku(JsonValue options = null, VmOptions vm = null)
        {
            SessionOptions sessionOptions = new SessionOptions();
            if (vm != null)
                sessionOptions.Vm = vm;
            GameSession session = new GameSession(LoadGomoku(), sessionOptions);
            session.NewGame(options);
            return session;
        }

        public static JsonValue Options(string json)
        {
            return JsonValue.Parse(json);
        }

        public static InputPacket Place(int seat, int x, int y)
        {
            return InputPacket.Action(seat, "place_stone").With("x", x).With("y", y);
        }

        public static SubmitResult PlaceOk(GameSession session, int seat, int x, int y)
        {
            SubmitResult result = session.Submit(Place(seat, x, y));
            if (!result.IsAccepted)
                throw new InvalidOperationException("expected (" + x + "," + y + ") to be accepted but got " + result);
            return result;
        }

        public static string TempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "bse-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        public static void WriteFile(string root, string relative, string text)
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }
    }
}
