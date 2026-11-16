using System;
using System.IO;
using UnityEditor;

namespace BoardSandbox.Client.Editor
{
    // Keeps the import settings of the native Luau library right. The library is one file per platform and CPU:
    //   bse_luau.dll      a 64-bit Windows library, used only by the Windows Editor and the Windows standalone player;
    //   libbse_luau.so    an Android library per ABI, in a folder that is named after the ABI (arm64-v8a, armeabi-v7a, x86_64),
    //                     used only by Android players.
    // With wrong settings Unity either leaves the library out of a build, which then starts but cannot load any game, or
    // packs it into builds for platforms that cannot use it.
    [InitializeOnLoad]
    public static class NativePluginSetup
    {
        public const string WindowsFileName = "bse_luau.dll";
        public const string AndroidFileName = "libbse_luau.so";
        private const string WindowsCpu = "x86_64";

        static NativePluginSetup()
        {
            EditorApplication.delayCall += Apply;
        }

        [MenuItem("Board Sandbox/Fix Native Plugin Settings")]
        public static void Apply()
        {
            foreach (string path in AssetDatabase.GetAllAssetPaths())
            {
                if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                    continue;
                bool windows = path.EndsWith("/" + WindowsFileName, StringComparison.Ordinal);
                bool android = path.EndsWith("/" + AndroidFileName, StringComparison.Ordinal);
                if (!windows && !android)
                    continue;
                PluginImporter importer = AssetImporter.GetAtPath(path) as PluginImporter;
                if (importer == null)
                    continue;
                bool changed = windows ? ConfigureWindows(importer) : ConfigureAndroid(importer, path);
                if (changed)
                    importer.SaveAndReimport();
            }
        }

        // The Android CPU of a library in an ABI folder: "ARM64", "ARMv7" or "X86_64", and null for any other folder.
        public static string AndroidCpuOf(string assetPath)
        {
            string directory = Path.GetDirectoryName(assetPath);
            string abi = string.IsNullOrEmpty(directory) ? string.Empty : Path.GetFileName(directory);
            switch (abi)
            {
                case "arm64-v8a":
                    return "ARM64";
                case "armeabi-v7a":
                    return "ARMv7";
                case "x86_64":
                    return "X86_64";
                default:
                    return null;
            }
        }

        // Each returns true when something had to be changed.
        private static bool ConfigureWindows(PluginImporter importer)
        {
            bool changed = false;
            if (importer.GetCompatibleWithAnyPlatform())
            {
                importer.SetCompatibleWithAnyPlatform(false);
                changed = true;
            }
            if (!importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64))
            {
                importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, true);
                changed = true;
            }
            if (importer.GetCompatibleWithPlatform(BuildTarget.Android))
            {
                importer.SetCompatibleWithPlatform(BuildTarget.Android, false);
                changed = true;
            }
            if (!importer.GetCompatibleWithEditor())
            {
                importer.SetCompatibleWithEditor(true);
                changed = true;
            }
            if (importer.GetEditorData("OS") != "Windows")
            {
                importer.SetEditorData("OS", "Windows");
                changed = true;
            }
            if (importer.GetEditorData("CPU") != WindowsCpu)
            {
                importer.SetEditorData("CPU", WindowsCpu);
                changed = true;
            }
            if (importer.GetPlatformData(BuildTarget.StandaloneWindows64, "CPU") != WindowsCpu)
            {
                importer.SetPlatformData(BuildTarget.StandaloneWindows64, "CPU", WindowsCpu);
                changed = true;
            }
            return changed;
        }

        private static bool ConfigureAndroid(PluginImporter importer, string path)
        {
            string cpu = AndroidCpuOf(path);
            if (cpu == null)
                return false;

            bool changed = false;
            if (importer.GetCompatibleWithAnyPlatform())
            {
                importer.SetCompatibleWithAnyPlatform(false);
                changed = true;
            }
            if (importer.GetCompatibleWithEditor())
            {
                importer.SetCompatibleWithEditor(false);
                changed = true;
            }
            if (importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64))
            {
                importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, false);
                changed = true;
            }
            if (!importer.GetCompatibleWithPlatform(BuildTarget.Android))
            {
                importer.SetCompatibleWithPlatform(BuildTarget.Android, true);
                changed = true;
            }
            if (importer.GetPlatformData(BuildTarget.Android, "CPU") != cpu)
            {
                importer.SetPlatformData(BuildTarget.Android, "CPU", cpu);
                changed = true;
            }
            return changed;
        }
    }
}
