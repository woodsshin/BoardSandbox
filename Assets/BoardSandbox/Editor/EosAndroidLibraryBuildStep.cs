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
    // EosAndroidProjectFix, and turns on core library desugaring in the application module (launcher/build.gradle), which the AAR
    // of the EOS SDK asks for, see EosAndroidDesugaringFix. It does so at three points of a build. Each of them writes a line to the
    // Console ("[EOS Android] ..."), which is how to tell which of them ran, and what is in the new form already is left alone, so
    // the later points find the work of the earlier ones done.
    //
    // 1. OnPreprocessBuild adapts the copy of the library in Assets/Plugins/Android/EOS, which Unity copies into the Gradle project.
    //    The EOS plugin writes that copy again for every build, in its own OnPreprocessBuild (the orders 0 and 1), so this step has
    //    a later order.
    // 2. OnPostGenerateGradleAndroidProject adapts the library in the Gradle project that Unity has just written, before anything
    //    builds it, for a build and for "Export Project" alike (when it exports, the path is the unityLibrary module in the folder
    //    of the export). A project that is exported and built outside of Unity gets the same files.
    // 3. OnPostprocessBuild looks at an exported project once more when the export is done, and adapts the files if something has
    //    written them again after the second step.
    //
    // The desugaring is set up at the second and the third point. The first one has no launcher module: there is no Gradle project
    // yet, only the copy of the library.
    //
    // The project is only edited where the plugin's library is found, and only if its files are still in the form of the plugin
    // 6.2.0 and 6.3.0 (the library), or launcher/build.gradle does not desugar yet. A project without the library is not changed.
    public sealed class EosAndroidLibraryBuildStep : IPreprocessBuildWithReport, IPostGenerateGradleAndroidProject, IPostprocessBuildWithReport
    {
        // After the build steps of the EOS plugin, which have the orders 0 (PlatformSpecificBuilder) and 1 (BuildRunner). The second
        // one copies the library into the project.
        public int callbackOrder => 100;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android)
                return;
            Run(Path.Combine(Path.Combine(Path.Combine(Application.dataPath, "Plugins"), "Android"), "EOS"),
                "before Unity generated the Gradle project");
        }

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
