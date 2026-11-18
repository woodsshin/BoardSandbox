#if UNITY_ANDROID
using System;
using System.IO;
using UnityEditor.Android;
using UnityEngine;

namespace BoardSandbox.Client.Editor
{
    // Adapts the library module of the EOS Plugin for Unity to the Gradle version of Unity, see EosAndroidLibraryFix.
    //
    // Unity calls this after it has written the Gradle project and before anything builds it, for a build and for "Export Project"
    // alike, so a project that is exported and built outside of Unity gets the same files. The project is only edited where the
    // plugin's library is found, and only if its files are still in the form of the plugin 6.2.0. A build without the plugin is
    // not affected.
    public sealed class EosAndroidLibraryBuildStep : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 0;

        // path is the unityLibrary module of the generated project.
        public void OnPostGenerateGradleAndroidProject(string path)
        {
            try
            {
                string library = FindLibrary(path);
                if (library != null)
                    Adapt(library, path);
            }
            catch (Exception e)
            {
                // The build goes on. A library that was not adapted fails in Gradle, which says what it does not accept.
                Debug.LogWarning("The library of the EOS plugin could not be adapted to the Gradle version of Unity: " + e.Message);
            }
        }

        private static string FindLibrary(string unityLibrary)
        {
            string inside = Path.Combine(unityLibrary, EosAndroidLibraryFix.LibraryFolder);
            if (Directory.Exists(inside))
                return inside;

            string project = Path.GetDirectoryName(unityLibrary.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrEmpty(project))
            {
                string beside = Path.Combine(project, EosAndroidLibraryFix.LibraryFolder);
                if (Directory.Exists(beside))
                    return beside;
            }

            string[] found = Directory.GetDirectories(unityLibrary, EosAndroidLibraryFix.LibraryFolder, SearchOption.AllDirectories);
            return found.Length > 0 ? found[0] : null;
        }

        private static void Adapt(string library, string unityLibrary)
        {
            string manifestPath = Path.Combine(library, "AndroidManifest.xml");
            string gradlePath = Path.Combine(library, "build.gradle");
            bool manifestChanged = false;
            bool gradleChanged = false;

            // The manifest first: its package becomes the namespace in build.gradle.
            string package = null;
            if (File.Exists(manifestPath))
            {
                string manifest = File.ReadAllText(manifestPath);
                if (EosAndroidLibraryFix.ManifestNeedsFix(manifest))
                {
                    File.WriteAllText(manifestPath, EosAndroidLibraryFix.FixManifest(manifest, out package));
                    manifestChanged = true;
                }
            }

            if (File.Exists(gradlePath))
            {
                string gradle = File.ReadAllText(gradlePath);
                if (EosAndroidLibraryFix.BuildGradleNeedsFix(gradle))
                {
                    string mainGradlePath = Path.Combine(unityLibrary, "build.gradle");
                    string compileSdk = File.Exists(mainGradlePath) ? EosAndroidLibraryFix.FindCompileSdk(File.ReadAllText(mainGradlePath)) : null;
                    File.WriteAllText(gradlePath, EosAndroidLibraryFix.FixBuildGradle(gradle, package, compileSdk));
                    gradleChanged = true;
                }
            }

            if (manifestChanged || gradleChanged)
                Debug.Log("Adapted the library of the EOS plugin to the Gradle version of Unity (" + library + "): "
                    + (gradleChanged ? "build.gradle " : string.Empty) + (manifestChanged ? "AndroidManifest.xml" : string.Empty));
        }
    }
}
#endif
