using System;
using UnityEditor;

namespace BoardSandbox.Client.Editor
{
    // Keeps the import settings of the native Luau library right: it is a 64-bit Windows DLL and is used only by
    // the Windows Editor and the Windows standalone player.
    [InitializeOnLoad]
    public static class NativePluginSetup
    {
        private const string FileName = "bse_luau.dll";
        private const string Cpu = "x86_64";

        static NativePluginSetup()
        {
            EditorApplication.delayCall += Apply;
        }

        [MenuItem("Board Sandbox/Fix Native Plugin Settings")]
        public static void Apply()
        {
            foreach (string path in AssetDatabase.GetAllAssetPaths())
            {
                if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith("/" + FileName, StringComparison.Ordinal))
                    continue;
                PluginImporter importer = AssetImporter.GetAtPath(path) as PluginImporter;
                if (importer != null && Configure(importer))
                    importer.SaveAndReimport();
            }
        }

        // Returns true when something had to be changed.
        private static bool Configure(PluginImporter importer)
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
            if (importer.GetEditorData("CPU") != Cpu)
            {
                importer.SetEditorData("CPU", Cpu);
                changed = true;
            }
            if (importer.GetPlatformData(BuildTarget.StandaloneWindows64, "CPU") != Cpu)
            {
                importer.SetPlatformData(BuildTarget.StandaloneWindows64, "CPU", Cpu);
                changed = true;
            }
            return changed;
        }
    }
}
