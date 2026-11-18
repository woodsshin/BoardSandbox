using System.IO;

namespace BoardSandbox.Client.Editor
{
    // The file work around EosAndroidLibraryFix: finds the library module of the EOS plugin in a Gradle project that Unity has
    // generated or exported, and rewrites its two files. It uses no Unity types, so that the tests can run it on a folder.
    //
    // It is used by the build step (EosAndroidLibraryBuildStep) and by the menu that repairs a project that was exported before
    // the build step existed (EosAndroidProjectMenu).
    public static class EosAndroidProjectFix
    {
        public sealed class Result
        {
            // What the caller asked about: the folder that was searched.
            public string Searched { get; internal set; }

            // The folder of the library module, or null when the project has none.
            public string Library { get; internal set; }

            public bool GradleChanged { get; internal set; }

            public bool ManifestChanged { get; internal set; }

            // True when build.gradle was not in the new form and could not be brought into it (no android block, say).
            public bool GradleStillOld { get; internal set; }

            public bool Found
            {
                get { return Library != null; }
            }

            public bool Changed
            {
                get { return GradleChanged || ManifestChanged; }
            }

            public string Describe()
            {
                if (!Found)
                    return "There is no " + EosAndroidLibraryFix.LibraryFolder + " in " + Searched + ".";
                if (GradleStillOld)
                    return "The build.gradle of the library of the EOS plugin (" + Library + ") is not in a form that can be adapted to the "
                        + "Gradle version of Unity, so it was left as it is.";
                if (!Changed)
                    return "The library of the EOS plugin is already in the form that the Gradle version of Unity takes (" + Library + ").";
                return "Adapted the library of the EOS plugin to the Gradle version of Unity (" + Library + "): "
                    + (GradleChanged ? "build.gradle" : string.Empty)
                    + (GradleChanged && ManifestChanged ? ", " : string.Empty)
                    + (ManifestChanged ? "AndroidManifest.xml" : string.Empty) + ".";
            }
        }

        // path is where to look: the exported project, its unityLibrary module (what Unity hands to the build step), or the library
        // module itself. A project without the library is not an error; the result says that nothing was found.
        public static Result Adapt(string path)
        {
            Result result = new Result { Searched = path };
            string library = FindLibrary(path);
            if (library == null)
                return result;
            result.Library = library;

            string manifestPath = Path.Combine(library, "AndroidManifest.xml");
            string gradlePath = Path.Combine(library, "build.gradle");

            // The manifest first: its package becomes the namespace in build.gradle.
            string package = null;
            if (File.Exists(manifestPath))
            {
                string manifest = File.ReadAllText(manifestPath);
                if (EosAndroidLibraryFix.ManifestNeedsFix(manifest))
                {
                    WriteText(manifestPath, EosAndroidLibraryFix.FixManifest(manifest, out package));
                    result.ManifestChanged = true;
                }
            }

            if (File.Exists(gradlePath))
            {
                string gradle = File.ReadAllText(gradlePath);
                if (EosAndroidLibraryFix.BuildGradleNeedsFix(gradle))
                {
                    string adapted = EosAndroidLibraryFix.FixBuildGradle(gradle, package, FindUnityCompileSdk(library));
                    if (adapted != gradle)
                    {
                        WriteText(gradlePath, adapted);
                        result.GradleChanged = true;
                    }
                    result.GradleStillOld = EosAndroidLibraryFix.BuildGradleNeedsFix(adapted);
                }
            }
            return result;
        }

        // Looks in the places where Unity puts a library plug-in, nearest first, and then anywhere below path.
        public static string FindLibrary(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return null;

            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 0)
                trimmed = path; // the root of a file system
            if (Path.GetFileName(trimmed) == EosAndroidLibraryFix.LibraryFolder)
                return trimmed;

            // path is the unityLibrary module (what Unity hands to the build step): the library is a folder in it.
            string inside = Path.Combine(path, EosAndroidLibraryFix.LibraryFolder);
            if (Directory.Exists(inside))
                return inside;

            // path is the exported project.
            string inModule = Path.Combine(Path.Combine(path, "unityLibrary"), EosAndroidLibraryFix.LibraryFolder);
            if (Directory.Exists(inModule))
                return inModule;

            // The library lies beside the unityLibrary module.
            string parent = Path.GetDirectoryName(trimmed);
            if (!string.IsNullOrEmpty(parent))
            {
                string beside = Path.Combine(parent, EosAndroidLibraryFix.LibraryFolder);
                if (Directory.Exists(beside))
                    return beside;
            }

            string[] found = Directory.GetDirectories(path, EosAndroidLibraryFix.LibraryFolder, SearchOption.AllDirectories);
            return found.Length > 0 ? found[0] : null;
        }

        // The SDK that the Unity module is compiled against, for a library whose own build.gradle does not say.
        private static string FindUnityCompileSdk(string library)
        {
            string parent = Path.GetDirectoryName(library.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(parent))
                return null;
            string[] candidates =
            {
                Path.Combine(parent, "build.gradle"),
                Path.Combine(Path.Combine(parent, "unityLibrary"), "build.gradle")
            };
            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate))
                    continue;
                string compileSdk = EosAndroidLibraryFix.FindCompileSdk(File.ReadAllText(candidate));
                if (compileSdk != null)
                    return compileSdk;
            }
            return null;
        }

        // A file that was copied from a read-only one is read-only itself.
        private static void WriteText(string path, string text)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.WriteAllText(path, text);
        }
    }
}
