#if UNITY_ANDROID
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace BoardSandbox.Client.Editor
{
    // Adapts the library module of the EOS Plugin for Unity to the Gradle version of Unity, see EosAndroidLibraryFix and
    // EosAndroidProjectFix.
    //
    // Unity calls OnPostGenerateGradleAndroidProject after it has written the Gradle project and before anything builds it, for a
    // build and for "Export Project" alike (when it exports, the path is the unityLibrary module in the folder of the export), so a
    // project that is exported and built outside of Unity gets the same files. The project is only edited where the plugin's
    // library is found, and only if its files are still in the form of the plugin 6.2.0 and 6.3.0. A project without the library
    // is not changed.
    //
    // OnPostprocessBuild looks at an exported project once more when the export is done. It finds the files already adapted and
    // says so, and it adapts them if something has written them again after the first step. Each step writes a line to the
    // Console, which is how to tell whether a step ran at all.
    public sealed class EosAndroidLibraryBuildStep : IPostGenerateGradleAndroidProject, IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        // path is the unityLibrary module of the generated project.
        public void OnPostGenerateGradleAndroidProject(string path)
        {
            Run(path, "after Unity generated the Gradle project");
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android)
                return;
            // The output of a build that is not an export is the .apk or .aab file.
            string output = report.summary.outputPath;
            if (!string.IsNullOrEmpty(output) && Directory.Exists(output))
                Run(output, "after the export");
        }

        private static void Run(string path, string when)
        {
            try
            {
                EosAndroidProjectFix.Result result = EosAndroidProjectFix.Adapt(path);
                if (result.Found)
                    Debug.Log("[EOS Android] " + when + ": " + result.Describe());
                else
                {
#if BSE_EOS
                    // The code for EOS is part of this build, so the library of the plugin should be in the project.
                    Debug.LogWarning("[EOS Android] " + when + ": " + result.Describe());
#endif
                }
            }
            catch (Exception e)
            {
                // The build goes on. A library that was not adapted fails in Gradle, which says what it does not accept.
                Debug.LogWarning("[EOS Android] The library of the EOS plugin could not be adapted to the Gradle version of Unity ("
                    + when + "): " + e.Message);
            }
        }
    }
}
#endif
