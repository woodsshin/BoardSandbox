using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace BoardSandbox.Client.Editor
{
    // An Android build without the native Luau library is made without a complaint, installs and starts, and then
    // cannot load a single game ("Unable to load DLL 'bse_luau'"). This stops such a build before it is made.
    public sealed class NativePluginBuildCheck : IPreprocessBuildWithReport
    {
        // PlayerSettings.Android.targetArchitectures is a set of flags.
        private const long ArmV7Flag = 1;
        private const long Arm64Flag = 2;
        private const long X86_64Flag = 8;

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android)
                return;

            HashSet<string> present = new HashSet<string>(StringComparer.Ordinal);
            foreach (PluginImporter importer in PluginImporter.GetImporters(BuildTarget.Android))
            {
                if (importer.assetPath.EndsWith("/" + NativePluginSetup.AndroidFileName, StringComparison.Ordinal))
                    present.Add(importer.GetPlatformData(BuildTarget.Android, "CPU"));
            }

            long architectures = Convert.ToInt64(PlayerSettings.Android.targetArchitectures);
            List<string> missing = new List<string>();
            Require(architectures, ArmV7Flag, "ARMv7", "armeabi-v7a", present, missing);
            Require(architectures, Arm64Flag, "ARM64", "arm64-v8a", present, missing);
            Require(architectures, X86_64Flag, "X86_64", "x86_64", present, missing);
            if (missing.Count == 0)
                return;

            throw new BuildFailedException("The native Luau library is missing for " + string.Join(", ", missing) + ". "
                + "A build without it starts, but cannot load any game ('Unable to load DLL bse_luau'). "
                + "Put " + NativePluginSetup.AndroidFileName + " into Assets/BoardSandbox/Plugins/Android/<ABI>/ "
                + "(python Native/scripts/build_native.py android, or the android-native workflow on GitHub), "
                + "or leave the CPU out in Player Settings > Other Settings > Target Architectures.");
        }

        private static void Require(long architectures, long flag, string cpu, string abi, HashSet<string> present, List<string> missing)
        {
            if ((architectures & flag) != 0 && !present.Contains(cpu))
                missing.Add(cpu + " (" + abi + ")");
        }
    }
}
