using System.Collections.Generic;
using System.IO;
using BoardSandbox.Client.Editor;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    // The AAR of the EOS SDK is built with core library desugaring, and the Android Gradle plugin stops the build of the application
    // module (launcher) with "Dependency ':eos-sdk:' requires core library desugaring to be enabled for :launcher" if the module does
    // not desugar as well. The first tests are on the text of launcher/build.gradle, the last ones on a Gradle project in a folder.
    [TestFixture]
    public class EosAndroidDesugaringFixTests
    {
        private readonly List<string> _temporary = new List<string>();

        [TearDown]
        public void RemoveTemporaryDirectories()
        {
            foreach (string path in _temporary)
            {
                try
                {
                    Directory.Delete(path, true);
                }
                catch (IOException)
                {
                }
                catch (System.UnauthorizedAccessException)
                {
                }
            }
            _temporary.Clear();
        }

        private string NewDirectory()
        {
            string path = TestSupport.TempDirectory();
            _temporary.Add(path);
            return path;
        }

        private static string Lines(params string[] lines)
        {
            return string.Join("\n", lines);
        }

        private static string Crlf(string text)
        {
            return text.Replace("\n", "\r\n");
        }

        // launcher/build.gradle as Unity writes it, shortened (and without the newline at its end, which the tests add where they
        // want one).
        private static readonly string LauncherGradle = Lines(
            "apply plugin: 'com.android.application'",
            "",
            "dependencies {",
            "    implementation project(':unityLibrary')",
            "}",
            "",
            "android {",
            "    namespace \"com.unity3d.player\"",
            "    compileSdk 36",
            "    buildToolsVersion = \"36.0.0\"",
            "",
            "    compileOptions {",
            "        sourceCompatibility JavaVersion.VERSION_17",
            "        targetCompatibility JavaVersion.VERSION_17",
            "    }",
            "",
            "    defaultConfig {",
            "        minSdk 23",
            "        targetSdk 36",
            "        applicationId 'com.example.boardsandbox'",
            "    }",
            "",
            "    buildTypes {",
            "        release {",
            "            minifyEnabled false",
            "        }",
            "    }",
            "}");

        private const string Note =
            "// The AAR of the EOS SDK asks the application for core library desugaring (Board Sandbox, EosAndroidDesugaringFix).";

        private static readonly string FlagBlock = Lines(
            "android {",
            "    compileOptions {",
            "        coreLibraryDesugaringEnabled true",
            "    }",
            "}");

        private static readonly string LibraryBlock = Lines(
            "dependencies {",
            "    coreLibraryDesugaring '" + EosAndroidDesugaringFix.DesugarLibrary + "'",
            "}");

        // The flag in the compileOptions block of LauncherGradle.
        private static readonly string LauncherWithFlag = LauncherGradle.Replace(
            "        targetCompatibility JavaVersion.VERSION_17",
            "        targetCompatibility JavaVersion.VERSION_17\n        coreLibraryDesugaringEnabled true");

        // ---- the text of launcher/build.gradle -----------------------------------------------------------------------------

        [Test]
        public void AModuleThatDoesNotDesugarGetsTheFlagAndTheLibraryAtItsEnd()
        {
            string text = LauncherGradle + "\n";

            Assert.IsTrue(EosAndroidDesugaringFix.NeedsFix(text));

            string result = EosAndroidDesugaringFix.Fix(text);

            Assert.AreEqual(text + "\n" + Note + "\n" + FlagBlock + "\n" + LibraryBlock + "\n", result);
            Assert.IsFalse(EosAndroidDesugaringFix.NeedsFix(result), "the result needs nothing more");
        }

        [Test]
        public void TheLibraryIsOneOfGoogleMavenWithAVersion()
        {
            StringAssert.StartsWith("com.android.tools:desugar_jdk_libs:", EosAndroidDesugaringFix.DesugarLibrary);
            Assert.IsTrue(char.IsDigit(EosAndroidDesugaringFix.DesugarLibrary[EosAndroidDesugaringFix.DesugarLibrary.Length - 1]),
                "the library ends with its version");
        }

        [Test]
        public void AFileWithoutANewlineAtItsEndIsEndedFirst()
        {
            string result = EosAndroidDesugaringFix.Fix(LauncherGradle);

            Assert.AreEqual(LauncherGradle + "\n\n" + Note + "\n" + FlagBlock + "\n" + LibraryBlock + "\n", result);
        }

        [Test]
        public void WindowsLineEndingsStayWindowsLineEndings()
        {
            string text = Crlf(LauncherGradle) + "\r\n";

            string result = EosAndroidDesugaringFix.Fix(text);

            Assert.AreEqual(Crlf(LauncherGradle + "\n" + "\n" + Note + "\n" + FlagBlock + "\n" + LibraryBlock + "\n"), result);
            Assert.IsFalse(result.Replace("\r\n", string.Empty).Contains("\n"), "no line ends in a bare newline");

            // Also where the last line of the file has no ending, the one that is added is a Windows one.
            string noEnding = EosAndroidDesugaringFix.Fix(Crlf(LauncherGradle));
            Assert.IsFalse(noEnding.Replace("\r\n", string.Empty).Contains("\n"), "no line ends in a bare newline");
            StringAssert.Contains("}\r\n\r\n" + Note, noEnding);
        }

        [Test]
        public void OnlyTheLibraryIsAddedWhereTheFlagIsOnAlready()
        {
            string text = LauncherWithFlag + "\n";

            Assert.IsTrue(EosAndroidDesugaringFix.NeedsFix(text), "the flag alone does not make a library to desugar with");

            Assert.AreEqual(text + "\n" + Note + "\n" + LibraryBlock + "\n", EosAndroidDesugaringFix.Fix(text));
        }

        [Test]
        public void OnlyTheFlagIsAddedWhereTheLibraryIsThereAlready()
        {
            string text = LauncherGradle + "\n" + LibraryBlock + "\n";

            Assert.IsTrue(EosAndroidDesugaringFix.NeedsFix(text), "the library alone does not turn the desugaring on");

            Assert.AreEqual(text + "\n" + Note + "\n" + FlagBlock + "\n", EosAndroidDesugaringFix.Fix(text));
        }

        [Test]
        public void AModuleThatDesugarsIsLeftAsItIs()
        {
            string text = LauncherWithFlag + "\n" + LibraryBlock + "\n";

            Assert.IsFalse(EosAndroidDesugaringFix.NeedsFix(text));
            Assert.AreSame(text, EosAndroidDesugaringFix.Fix(text), "the text is returned as it is");
        }

        [Test]
        public void TheOtherSpellingsOfTheSettingsAreRecognized()
        {
            // The documentation of the EOS plugin, and the Kotlin form of the property.
            string[] flags =
            {
                "        coreLibraryDesugaringEnabled true",
                "        coreLibraryDesugaringEnabled = true",
                "        coreLibraryDesugaringEnabled=true",
                "        isCoreLibraryDesugaringEnabled = true",
                "\tcoreLibraryDesugaringEnabled true // for the AAR of the EOS SDK",
            };
            string[] libraries =
            {
                "    coreLibraryDesugaring 'com.android.tools:desugar_jdk_libs:2.0.4'",
                "    coreLibraryDesugaring \"com.android.tools:desugar_jdk_libs:2.0.4\"",
                "    coreLibraryDesugaring(\"com.android.tools:desugar_jdk_libs:2.1.3\")",
                "    coreLibraryDesugaring libs.desugar",
            };
            foreach (string flag in flags)
            {
                foreach (string library in libraries)
                {
                    string text = Lines(
                        "apply plugin: 'com.android.application'",
                        "android {",
                        "    compileOptions {",
                        flag,
                        "    }",
                        "}",
                        "dependencies {",
                        library,
                        "}");

                    Assert.IsFalse(EosAndroidDesugaringFix.NeedsFix(text), flag + " / " + library);
                }
            }
        }

        [Test]
        public void CommentedOutSettingsAreNoSettings()
        {
            string text = Lines(
                "apply plugin: 'com.android.application'",
                "android {",
                "    compileOptions {",
                "        // coreLibraryDesugaringEnabled true",
                "    }",
                "}",
                "dependencies {",
                "    // coreLibraryDesugaring 'com.android.tools:desugar_jdk_libs:2.0.4'",
                "}",
                "");

            Assert.IsTrue(EosAndroidDesugaringFix.NeedsFix(text));
            Assert.AreEqual(text + "\n" + Note + "\n" + FlagBlock + "\n" + LibraryBlock + "\n", EosAndroidDesugaringFix.Fix(text));
        }

        [Test]
        public void AFlagThatIsOffIsTurnedOnByTheBlockThatComesLast()
        {
            string text = LauncherGradle.Replace(
                "        targetCompatibility JavaVersion.VERSION_17",
                "        targetCompatibility JavaVersion.VERSION_17\n        coreLibraryDesugaringEnabled false") + "\n" + LibraryBlock + "\n";

            Assert.IsTrue(EosAndroidDesugaringFix.NeedsFix(text));

            string result = EosAndroidDesugaringFix.Fix(text);

            Assert.AreEqual(text + "\n" + Note + "\n" + FlagBlock + "\n", result);
            Assert.Greater(result.LastIndexOf("coreLibraryDesugaringEnabled true"), result.LastIndexOf("coreLibraryDesugaringEnabled false"),
                "Gradle takes the value that is set last");
        }

        [Test]
        public void TextWithoutAnAndroidBlockIsLeftAlone()
        {
            // The build file of something that is no module with an android block: there is nothing that the setting could go into.
            string text = Lines("apply plugin: 'java'", "dependencies {", "    implementation 'a:b:1'", "}", "");

            Assert.IsFalse(EosAndroidDesugaringFix.NeedsFix(text));
            Assert.AreSame(text, EosAndroidDesugaringFix.Fix(text));

            Assert.IsFalse(EosAndroidDesugaringFix.NeedsFix(string.Empty));
            Assert.IsFalse(EosAndroidDesugaringFix.NeedsFix(null));
            Assert.AreEqual(string.Empty, EosAndroidDesugaringFix.Fix(string.Empty));
            Assert.IsNull(EosAndroidDesugaringFix.Fix(null));
            Assert.IsFalse(EosAndroidDesugaringFix.NeedsFix("// android {\n"), "a commented out block is none");
        }

        [Test]
        public void FixingAFixedFileChangesNothing()
        {
            foreach (string original in new[] { LauncherGradle, LauncherGradle + "\n", LauncherWithFlag, Crlf(LauncherGradle) })
            {
                string once = EosAndroidDesugaringFix.Fix(original);
                string twice = EosAndroidDesugaringFix.Fix(once);

                Assert.AreNotEqual(original, once);
                Assert.AreEqual(once, twice);
            }
        }

        // ---- a Gradle project in a folder ----------------------------------------------------------------------------------

        // The unityLibrary module, which the tests must not change.
        private static readonly string ModuleGradle = "apply plugin: 'com.android.library'\nandroid {\n    namespace 'com.unity3d.player'\n    compileSdk 36\n}\n";

        private const string AdaptedLibraryGradle = "apply plugin: 'com.android.library'\nandroid {\n    namespace 'com.pew.eos_dependencies'\n    compileSdk 36\n}\n";

        private const string AdaptedManifest = "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\">\n    <application />\n</manifest>";

        // The form that the plugin writes: jcenter() in a buildscript block, a package in the manifest.
        private const string OldLibraryGradle = "buildscript {\n    repositories {\n        jcenter()\n    }\n}\napply plugin: 'com.android.library'\nandroid {\n    compileSdkVersion 36\n}\n";

        private const string OldManifest = "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\"\n    package=\"com.pew.eos_dependencies\">\n    <application />\n</manifest>";

        // A project as Unity exports it, with the library of the EOS plugin in the form that Gradle takes already, so that what the
        // tests see is the work on launcher. Returns the root of the project.
        private string NewProject(string launcherGradle)
        {
            string root = NewDirectory();
            if (launcherGradle != null)
                TestSupport.WriteFile(Path.Combine(root, "launcher"), "build.gradle", launcherGradle);
            string module = Path.Combine(root, "unityLibrary");
            TestSupport.WriteFile(module, "build.gradle", ModuleGradle);
            WriteLibrary(Path.Combine(module, "eos_dependencies.androidlib"), AdaptedLibraryGradle, AdaptedManifest);
            return root;
        }

        private static void WriteLibrary(string library, string gradle, string manifest)
        {
            TestSupport.WriteFile(library, "build.gradle", gradle);
            TestSupport.WriteFile(library, "AndroidManifest.xml", manifest);
        }

        private static string LauncherPath(string root)
        {
            return Path.Combine(Path.Combine(root, "launcher"), "build.gradle");
        }

        private static string ReadLauncher(string root)
        {
            return File.ReadAllText(LauncherPath(root));
        }

        [Test]
        public void TheLauncherIsSetUpWhereTheLibraryOfThePluginIsInTheProject()
        {
            string root = NewProject(LauncherGradle + "\n");
            string module = Path.Combine(root, "unityLibrary");

            EosAndroidProjectFix.Result result = EosAndroidProjectFix.Adapt(module);

            Assert.IsTrue(result.Found);
            Assert.IsFalse(result.GradleChanged);
            Assert.IsFalse(result.ManifestChanged);
            Assert.IsTrue(result.DesugaringChanged);
            Assert.IsTrue(result.Changed);
            Assert.AreEqual(EosAndroidDesugaringFix.Fix(LauncherGradle + "\n"), ReadLauncher(root));
            Assert.AreEqual(ModuleGradle, File.ReadAllText(Path.Combine(module, "build.gradle")), "the unityLibrary module is not changed");
            StringAssert.Contains("core library desugaring", result.Describe());
            StringAssert.Contains("launcher/build.gradle", result.Describe());
        }

        [Test]
        public void TheExportedProjectCanBeGivenInsteadOfTheModule()
        {
            string root = NewProject(LauncherGradle + "\n");

            EosAndroidProjectFix.Result result = EosAndroidProjectFix.Adapt(root);

            Assert.IsTrue(result.DesugaringChanged);
            Assert.AreEqual(EosAndroidDesugaringFix.Fix(LauncherGradle + "\n"), ReadLauncher(root));
        }

        [Test]
        public void TheLibraryAndTheLauncherAreAdaptedInOneRun()
        {
            string root = NewProject(Crlf(LauncherGradle) + "\r\n");
            string library = Path.Combine(Path.Combine(root, "unityLibrary"), "eos_dependencies.androidlib");
            WriteLibrary(library, OldLibraryGradle, OldManifest);

            EosAndroidProjectFix.Result result = EosAndroidProjectFix.Adapt(Path.Combine(root, "unityLibrary"));

            Assert.IsTrue(result.GradleChanged);
            Assert.IsTrue(result.ManifestChanged);
            Assert.IsTrue(result.DesugaringChanged);
            StringAssert.DoesNotContain("jcenter", File.ReadAllText(Path.Combine(library, "build.gradle")));
            string launcher = ReadLauncher(root);
            StringAssert.Contains("coreLibraryDesugaringEnabled true", launcher);
            Assert.IsFalse(launcher.Replace("\r\n", string.Empty).Contains("\n"), "launcher keeps its Windows line endings");
            string text = result.Describe();
            StringAssert.Contains("Adapted the library of the EOS plugin", text);
            StringAssert.Contains("build.gradle, AndroidManifest.xml", text);
            StringAssert.Contains("core library desugaring", text);
        }

        [Test]
        public void ALibraryThatWasAdaptedBeforeDoesNotKeepTheLauncherFromBeingSetUp()
        {
            // The export that was repaired by the first version of the fix, which knew nothing about desugaring.
            string root = NewProject(LauncherGradle + "\n");

            EosAndroidProjectFix.Result result = EosAndroidProjectFix.Adapt(root);

            Assert.IsTrue(result.Found);
            Assert.IsFalse(result.GradleStillOld);
            Assert.IsTrue(result.Changed);
            StringAssert.Contains("already in the form", result.Describe());
            StringAssert.Contains("core library desugaring", result.Describe());
        }

        [Test]
        public void AdaptingAgainChangesNothing()
        {
            string root = NewProject(LauncherGradle + "\n");
            EosAndroidProjectFix.Adapt(root);
            string launcher = ReadLauncher(root);

            EosAndroidProjectFix.Result again = EosAndroidProjectFix.Adapt(root);

            Assert.IsTrue(again.Found);
            Assert.IsFalse(again.Changed);
            Assert.IsFalse(again.DesugaringChanged);
            Assert.AreEqual(launcher, ReadLauncher(root));
            StringAssert.Contains("already in the form", again.Describe());
            StringAssert.DoesNotContain("desugaring", again.Describe());
        }

        [Test]
        public void ALauncherThatDesugarsAlreadyIsNotTouched()
        {
            string own = LauncherWithFlag + "\n" + LibraryBlock + "\n";
            string root = NewProject(own);

            EosAndroidProjectFix.Result result = EosAndroidProjectFix.Adapt(root);

            Assert.IsFalse(result.DesugaringChanged);
            Assert.IsFalse(result.Changed);
            Assert.AreEqual(own, ReadLauncher(root));
        }

        [Test]
        public void WithoutTheLibraryOfThePluginTheLauncherIsNotTouched()
        {
            string root = NewProject(LauncherGradle + "\n");
            Directory.Delete(Path.Combine(Path.Combine(root, "unityLibrary"), "eos_dependencies.androidlib"), true);

            EosAndroidProjectFix.Result result = EosAndroidProjectFix.Adapt(root);

            Assert.IsFalse(result.Found);
            Assert.IsFalse(result.Changed);
            Assert.AreEqual(LauncherGradle + "\n", ReadLauncher(root), "a game without EOS needs no desugaring");
        }

        [Test]
        public void ALibraryWithoutALauncherNextToItIsAdaptedAndThatIsAll()
        {
            // The copy in Assets/Plugins/Android, which the build step adapts before Unity has generated any Gradle project.
            string root = NewDirectory();
            string library = Path.Combine(Path.Combine(Path.Combine(Path.Combine(root, "Assets"), "Plugins"), "Android"), "EOS");
            library = Path.Combine(library, "eos_dependencies.androidlib");
            WriteLibrary(library, OldLibraryGradle, OldManifest);

            EosAndroidProjectFix.Result result = EosAndroidProjectFix.Adapt(Path.GetDirectoryName(library));

            Assert.IsTrue(result.GradleChanged);
            Assert.IsFalse(result.DesugaringChanged);
            StringAssert.DoesNotContain("desugaring", result.Describe());
        }

        [Test]
        public void ALibraryInTheRootOfTheProjectFindsTheLauncherBesideIt()
        {
            string root = NewProject(LauncherGradle + "\n");
            string inModule = Path.Combine(Path.Combine(root, "unityLibrary"), "eos_dependencies.androidlib");
            string beside = Path.Combine(root, "eos_dependencies.androidlib");
            Directory.Move(inModule, beside);

            EosAndroidProjectFix.Result result = EosAndroidProjectFix.Adapt(Path.Combine(root, "unityLibrary"));

            Assert.AreEqual(beside, result.Library);
            Assert.IsTrue(result.DesugaringChanged);
            StringAssert.Contains("coreLibraryDesugaringEnabled true", ReadLauncher(root));
        }

        [Test]
        public void AFileThatIsNoBuildFileOfAnApplicationIsNotChanged()
        {
            string odd = "// nothing in here\n";
            string root = NewProject(odd);

            EosAndroidProjectFix.Result result = EosAndroidProjectFix.Adapt(root);

            Assert.IsTrue(result.Found);
            Assert.IsFalse(result.DesugaringChanged);
            Assert.AreEqual(odd, ReadLauncher(root));
        }

        [Test]
        public void AProjectWithoutAnyLauncherIsFine()
        {
            string root = NewProject(null);

            EosAndroidProjectFix.Result result = EosAndroidProjectFix.Adapt(root);

            Assert.IsTrue(result.Found);
            Assert.IsFalse(result.Changed);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "launcher")), "nothing is created");
        }

        [Test]
        public void AReadOnlyLauncherIsSetUpToo()
        {
            // A file that Unity copied from a read-only one is read-only. (Where the tests run as a user that may write anyway, such
            // as root on Linux, this only shows that the attribute does not get in the way.)
            string root = NewProject(LauncherGradle + "\n");
            string path = LauncherPath(root);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                EosAndroidProjectFix.Result result = EosAndroidProjectFix.Adapt(root);

                Assert.IsTrue(result.DesugaringChanged);
                StringAssert.Contains("coreLibraryDesugaringEnabled true", ReadLauncher(root));
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }
    }
}
