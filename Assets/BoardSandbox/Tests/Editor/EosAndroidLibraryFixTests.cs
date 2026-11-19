using System;
using System.Collections.Generic;
using System.IO;
using BoardSandbox.Client.Editor;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    // The adaptation of the library of the EOS Plugin for Unity (6.2.0, and 6.3.0 has the same files) to Gradle 9.1 and Android Gradle
    // plugin 9.0, the build tools of Unity 6000.3.17 and later: the old file stops Gradle at jcenter(), and then at the missing
    // namespace. The first tests are on the text of the two files, the last ones on a folder that holds them.
    [TestFixture]
    public class EosAndroidLibraryFixTests
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
                catch (UnauthorizedAccessException)
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

        // The files of the plugin have Windows line endings.
        private static string Crlf(string text)
        {
            return text.Replace("\n", "\r\n");
        }

        // eos_dependencies.androidlib/build.gradle of the plugin, after the plugin has put the SDK and the build tools of Unity in.
        private static readonly string PluginGradle = Lines(
            "buildscript {",
            "    repositories {",
            "        google()",
            "        jcenter()",
            "        flatDir(dirs: 'libs')",
            "    }",
            "    ",
            "    dependencies {",
            "        classpath \"com.android.tools.build:gradle:3.6.0\"",
            "    }",
            "}",
            "",
            "apply plugin: 'com.android.library'",
            "",
            "android {",
            "    sourceSets {",
            "        main {",
            "            manifest.srcFile 'AndroidManifest.xml'",
            "            java.srcDirs = ['src']",
            "            res.srcDirs = ['res']",
            "            assets.srcDirs = ['assets']",
            "            jniLibs.srcDirs = ['libs']",
            "        }",
            "    }",
            "",
            "    compileSdkVersion 36",
            "    buildToolsVersion '36.0.0'",
            "    defaultConfig {",
            "        targetSdkVersion 36",
            "    }",
            "}",
            "dependencies {",
            "        implementation 'androidx.appcompat:appcompat:1.5.1'",
            "        implementation 'androidx.constraintlayout:constraintlayout:2.1.4'",
            "        implementation 'androidx.security:security-crypto:1.0.0'",
            "        implementation 'androidx.browser:browser:1.4.0'",
            "    //api fileTree(dir: 'libs', include: ['*.aar'])",
            "}");

        private static readonly string FixedGradle = Lines(
            "",
            "apply plugin: 'com.android.library'",
            "",
            "android {",
            "    namespace 'com.pew.eos_dependencies'",
            "    compileSdk 36",
            "    sourceSets {",
            "        main {",
            "            manifest.srcFile 'AndroidManifest.xml'",
            "            java.srcDirs = ['src']",
            "            res.srcDirs = ['res']",
            "            assets.srcDirs = ['assets']",
            "            jniLibs.srcDirs = ['libs']",
            "        }",
            "    }",
            "",
            "}",
            "dependencies {",
            "        implementation 'androidx.appcompat:appcompat:1.5.1'",
            "        implementation 'androidx.constraintlayout:constraintlayout:2.1.4'",
            "        implementation 'androidx.security:security-crypto:1.0.0'",
            "        implementation 'androidx.browser:browser:1.4.0'",
            "    //api fileTree(dir: 'libs', include: ['*.aar'])",
            "}");

        private static readonly string PluginManifest = Lines(
            "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\"",
            "    package=\"com.pew.eos_dependencies\">",
            "    <application",
            "        android:theme=\"@style/Theme.AppCompat.Light.NoActionBar.FullScreen\">",
            "    </application>",
            "    <uses-permission android:name=\"android.permission.INTERNET\" />",
            "    <uses-permission android:name=\"android.permission.RECORD_AUDIO\" />",
            "",
            "</manifest>");

        // ---- build.gradle --------------------------------------------------------------------------------------------------

        [Test]
        public void TheFileOfThePluginIsFixed()
        {
            Assert.IsTrue(EosAndroidLibraryFix.BuildGradleNeedsFix(PluginGradle), "the file of the plugin is the old form");

            string result = EosAndroidLibraryFix.FixBuildGradle(PluginGradle, "com.pew.eos_dependencies", null);

            Assert.AreEqual(FixedGradle, result);
            StringAssert.DoesNotContain("jcenter", result);
            StringAssert.DoesNotContain("buildscript", result);
            StringAssert.DoesNotContain("compileSdkVersion", result);
            StringAssert.DoesNotContain("targetSdkVersion", result);
        }

        [Test]
        public void AFixedFileNeedsNoFixAndIsLeftAlone()
        {
            Assert.IsFalse(EosAndroidLibraryFix.BuildGradleNeedsFix(FixedGradle));
            Assert.AreEqual(FixedGradle, EosAndroidLibraryFix.FixBuildGradle(FixedGradle, "com.pew.eos_dependencies", null),
                "fixing a fixed file changes nothing");
        }

        [Test]
        public void ACommentedOutJcenterIsNotCode()
        {
            string text = Lines("// jcenter() was here", "apply plugin: 'com.android.library'", "android {", "    namespace 'a.b'", "    compileSdk 35", "}");

            Assert.IsFalse(EosAndroidLibraryFix.BuildGradleNeedsFix(text));
        }

        [Test]
        public void ALibraryWithoutANamespaceNeedsAFix()
        {
            string text = Lines("apply plugin: 'com.android.library'", "android {", "    compileSdk 35", "}");

            Assert.IsTrue(EosAndroidLibraryFix.BuildGradleNeedsFix(text));
            Assert.AreEqual(Lines("apply plugin: 'com.android.library'", "android {", "    namespace 'x.y'", "    compileSdk 35", "}"),
                EosAndroidLibraryFix.FixBuildGradle(text, "x.y", null));
        }

        [Test]
        public void TheWholeBuildscriptBlockGoesWithItsNestedBraces()
        {
            // The form of the plugin documentation for Unity 6: braces inside braces, and a namespace already.
            string text = Lines(
                "buildscript {",
                "    repositories {",
                "        google()",
                "        mavenCentral()",
                "        flatDir {",
                "            dirs 'libs'",
                "        }",
                "    }",
                "    dependencies {",
                "        classpath \"com.android.tools.build:gradle:7.4.2\"",
                "    }",
                "}",
                "apply plugin: 'com.android.library'",
                "android {",
                "    namespace 'com.pew.eos_dependencies'",
                "    compileSdkVersion 34",
                "}");

            string result = EosAndroidLibraryFix.FixBuildGradle(text, null, null);

            Assert.AreEqual(Lines(
                "apply plugin: 'com.android.library'",
                "android {",
                "    namespace 'com.pew.eos_dependencies'",
                "    compileSdk 34",
                "}"), result);
        }

        [Test]
        public void TheNamespaceThatTheFileHasIsKept()
        {
            string text = Lines("apply plugin: 'com.android.library'", "android {", "    namespace \"own.name.space\"", "    compileSdkVersion 34", "}");

            string result = EosAndroidLibraryFix.FixBuildGradle(text, "from.the.manifest", null);

            StringAssert.Contains("namespace 'own.name.space'", result);
            StringAssert.DoesNotContain("from.the.manifest", result);
        }

        [Test]
        public void WithoutANamespaceTheOneOfTheManifestIsTakenAndThenTheDefault()
        {
            string text = Lines("apply plugin: 'com.android.library'", "android {", "    compileSdkVersion 34", "}");

            StringAssert.Contains("namespace 'from.the.manifest'", EosAndroidLibraryFix.FixBuildGradle(text, "from.the.manifest", null));
            StringAssert.Contains("namespace '" + EosAndroidLibraryFix.DefaultNamespace + "'", EosAndroidLibraryFix.FixBuildGradle(text, null, null));
            Assert.AreEqual("com.pew.eos_dependencies", EosAndroidLibraryFix.DefaultNamespace);
        }

        [Test]
        public void ACompileSdkThatTheFileDoesNotHaveComesFromTheProject()
        {
            // The plugin leaves **APIVERSION** in the file when it could not tell the version.
            string text = Lines("apply plugin: 'com.android.library'", "android {", "    compileSdkVersion **APIVERSION**", "}");

            string result = EosAndroidLibraryFix.FixBuildGradle(text, null, "36");

            StringAssert.Contains("    compileSdk 36" + "\n", result);
            StringAssert.DoesNotContain("APIVERSION", result);
        }

        [Test]
        public void TheCompileSdkOfAGradleFileIsFound()
        {
            Assert.AreEqual("36", EosAndroidLibraryFix.FindCompileSdk("android {\n    compileSdk 36\n}"));
            Assert.AreEqual("35", EosAndroidLibraryFix.FindCompileSdk("android {\n    compileSdk = 35\n}"));
            Assert.AreEqual("34", EosAndroidLibraryFix.FindCompileSdk("android {\n    compileSdkVersion 34\n}"));
            Assert.IsNull(EosAndroidLibraryFix.FindCompileSdk("android {\n    // compileSdk 36\n}"));
            Assert.IsNull(EosAndroidLibraryFix.FindCompileSdk(null));
        }

        [Test]
        public void WindowsLineEndingsStayWindowsLineEndings()
        {
            string result = EosAndroidLibraryFix.FixBuildGradle(PluginGradle.Replace("\n", "\r\n"), "com.pew.eos_dependencies", null);

            Assert.AreEqual(FixedGradle.Replace("\n", "\r\n"), result);
        }

        [Test]
        public void TextWithoutAnAndroidBlockIsNotChanged()
        {
            string text = Lines("buildscript {", "    repositories {", "        jcenter()", "    }", "}");

            Assert.AreEqual(text, EosAndroidLibraryFix.FixBuildGradle(text, null, null));
            Assert.AreEqual(string.Empty, EosAndroidLibraryFix.FixBuildGradle(string.Empty, null, null));
            Assert.IsNull(EosAndroidLibraryFix.FixBuildGradle(null, null, null));
        }

        [Test]
        public void ABuildscriptBlockThatIsNotClosedIsLeftInPlace()
        {
            string text = Lines("buildscript {", "    repositories {", "        jcenter()", "    }", "apply plugin: 'com.android.library'", "android {", "}");

            StringAssert.Contains("buildscript {", EosAndroidLibraryFix.FixBuildGradle(text, null, null));
        }

        // ---- AndroidManifest.xml -------------------------------------------------------------------------------------------

        [Test]
        public void TheManifestLosesThePackageAndNothingElse()
        {
            Assert.IsTrue(EosAndroidLibraryFix.ManifestNeedsFix(PluginManifest));

            string result = EosAndroidLibraryFix.FixManifest(PluginManifest, out string package);

            Assert.AreEqual("com.pew.eos_dependencies", package);
            Assert.AreEqual(PluginManifest.Replace("\"\n    package=\"com.pew.eos_dependencies\">", "\">"), result);
            StringAssert.StartsWith("<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\">", result);
            StringAssert.Contains("android.permission.RECORD_AUDIO", result);
            Assert.IsFalse(EosAndroidLibraryFix.ManifestNeedsFix(result));
        }

        [Test]
        public void ThePackageIsFoundWithSingleQuotesAndOnTheFirstLine()
        {
            string text = "<manifest package='a.b.c' xmlns:android=\"http://schemas.android.com/apk/res/android\">\n    <application />\n</manifest>";

            string result = EosAndroidLibraryFix.FixManifest(text, out string package);

            Assert.AreEqual("a.b.c", package);
            Assert.AreEqual("<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\">\n    <application />\n</manifest>", result);
        }

        [Test]
        public void AManifestWithoutAPackageIsNotChanged()
        {
            string text = "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\">\n    <application />\n</manifest>";

            Assert.IsFalse(EosAndroidLibraryFix.ManifestNeedsFix(text));
            Assert.AreEqual(text, EosAndroidLibraryFix.FixManifest(text, out string package));
            Assert.IsNull(package);
            Assert.IsFalse(EosAndroidLibraryFix.ManifestNeedsFix(null));
            Assert.IsFalse(EosAndroidLibraryFix.ManifestNeedsFix("not a manifest"));
        }

        // ---- a folder with the library in it -------------------------------------------------------------------------------

        private static readonly string FixedManifest = PluginManifest.Replace("\"\n    package=\"com.pew.eos_dependencies\">", "\">");

        // The unityLibrary module of a Gradle project, with the SDK that Unity compiles it against; returns the module.
        private string NewModule(string compileSdk = "36")
        {
            string module = Path.Combine(NewDirectory(), "unityLibrary");
            TestSupport.WriteFile(module, "build.gradle", "apply plugin: 'com.android.library'\nandroid {\n    compileSdk " + compileSdk + "\n}\n");
            return module;
        }

        private static string Library(string parent)
        {
            return Path.Combine(parent, "eos_dependencies.androidlib");
        }

        // The two files as the plugin writes them, with the line endings of the plugin.
        private static void WritePluginFiles(string library)
        {
            TestSupport.WriteFile(library, "build.gradle", Crlf(PluginGradle));
            TestSupport.WriteFile(library, "AndroidManifest.xml", Crlf(PluginManifest));
        }

        private static string Read(string library, string file)
        {
            return File.ReadAllText(Path.Combine(library, file));
        }

        private static void AssertAdapted(string library)
        {
            Assert.AreEqual(Crlf(FixedGradle), Read(library, "build.gradle"));
            Assert.AreEqual(Crlf(FixedManifest), Read(library, "AndroidManifest.xml"));
        }

        [Test]
        public void ALibraryInTheUnityLibraryModuleIsAdapted()
        {
            string module = NewModule();
            string library = Library(module);
            WritePluginFiles(library);

            EosAndroidProjectFix.Result result = EosAndroidProjectFix.Adapt(module);

            Assert.IsTrue(result.Found);
            Assert.AreEqual(library, result.Library);
            Assert.IsTrue(result.GradleChanged);
            Assert.IsTrue(result.ManifestChanged);
            Assert.IsFalse(result.GradleStillOld);
            AssertAdapted(library);
            StringAssert.Contains("Adapted the library of the EOS plugin", result.Describe());
            StringAssert.Contains("build.gradle, AndroidManifest.xml", result.Describe());
        }

        [Test]
        public void TheExportedProjectOrTheLibraryItselfCanBeGivenInsteadOfTheModule()
        {
            string module = NewModule();
            string library = Library(module);
            WritePluginFiles(library);
            string exported = Path.GetDirectoryName(module);

            Assert.AreEqual(library, EosAndroidProjectFix.FindLibrary(exported));
            Assert.AreEqual(library, EosAndroidProjectFix.FindLibrary(module));
            Assert.AreEqual(library, EosAndroidProjectFix.FindLibrary(library));
            Assert.AreEqual(library, EosAndroidProjectFix.FindLibrary(library + Path.DirectorySeparatorChar));

            EosAndroidProjectFix.Result result = EosAndroidProjectFix.Adapt(exported);

            Assert.IsTrue(result.Changed);
            AssertAdapted(library);
        }

        [Test]
        public void ALibraryBesideTheModuleOrDeeperInItIsFound()
        {
            string module = NewModule();
            string beside = Library(Path.GetDirectoryName(module));
            WritePluginFiles(beside);
            Assert.AreEqual(beside, EosAndroidProjectFix.FindLibrary(module));

            string other = NewModule();
            string deeper = Library(Path.Combine(Path.Combine(other, "plugins"), "android"));
            WritePluginFiles(deeper);
            Assert.AreEqual(deeper, EosAndroidProjectFix.FindLibrary(other));

            Assert.IsTrue(EosAndroidProjectFix.Adapt(other).Changed);
            AssertAdapted(deeper);
        }

        [Test]
        public void TheLibraryInTheModuleIsPreferredToOneThatLiesElsewhere()
        {
            string module = NewModule();
            WritePluginFiles(Library(module));
            WritePluginFiles(Library(Path.Combine(module, "build")));

            Assert.AreEqual(Library(module), EosAndroidProjectFix.FindLibrary(module));
        }

        [Test]
        public void AProjectWithoutTheLibraryIsLeftAlone()
        {
            string module = NewModule();
            string gradle = Read(module, "build.gradle");

            EosAndroidProjectFix.Result result = EosAndroidProjectFix.Adapt(module);

            Assert.IsFalse(result.Found);
            Assert.IsFalse(result.Changed);
            Assert.IsNull(result.Library);
            StringAssert.Contains("There is no eos_dependencies.androidlib in " + module, result.Describe());
            Assert.AreEqual(gradle, Read(module, "build.gradle"));

            Assert.IsFalse(EosAndroidProjectFix.Adapt(Path.Combine(module, "no-such-folder")).Found);
            Assert.IsFalse(EosAndroidProjectFix.Adapt(string.Empty).Found);
            Assert.IsFalse(EosAndroidProjectFix.Adapt(null).Found);
        }

        [Test]
        public void AdaptingAgainChangesNothing()
        {
            string module = NewModule();
            string library = Library(module);
            WritePluginFiles(library);
            EosAndroidProjectFix.Adapt(module);

            EosAndroidProjectFix.Result again = EosAndroidProjectFix.Adapt(module);

            Assert.IsTrue(again.Found);
            Assert.IsFalse(again.Changed);
            Assert.IsFalse(again.GradleStillOld);
            StringAssert.Contains("already in the form", again.Describe());
            AssertAdapted(library);
        }

        [Test]
        public void TheSdkOfTheUnityModuleIsUsedWhenTheLibraryDoesNotSayWhichOne()
        {
            string module = NewModule("35");
            string library = Library(module);
            // The plugin leaves the placeholder in the file when it cannot tell the SDK.
            TestSupport.WriteFile(library, "build.gradle", PluginGradle.Replace("compileSdkVersion 36", "compileSdkVersion **APIVERSION**"));
            TestSupport.WriteFile(library, "AndroidManifest.xml", PluginManifest);

            EosAndroidProjectFix.Adapt(module);

            StringAssert.Contains("    compileSdk 35\n", Read(library, "build.gradle"));
            StringAssert.DoesNotContain("APIVERSION", Read(library, "build.gradle"));
        }

        [Test]
        public void ThePackageOfTheManifestIsTheNamespaceOfTheLibrary()
        {
            string module = NewModule();
            string library = Library(module);
            TestSupport.WriteFile(library, "build.gradle", PluginGradle);
            TestSupport.WriteFile(library, "AndroidManifest.xml", PluginManifest.Replace("com.pew.eos_dependencies", "my.own.package"));

            EosAndroidProjectFix.Adapt(module);

            StringAssert.Contains("namespace 'my.own.package'", Read(library, "build.gradle"));
            StringAssert.DoesNotContain("package=", Read(library, "AndroidManifest.xml"));
        }

        [Test]
        public void AManifestThatWasAdaptedBeforeStillGivesTheLibraryItsNamespace()
        {
            // A first run that stopped after the manifest: the package is gone from it, build.gradle is still the old file.
            string module = NewModule();
            string library = Library(module);
            TestSupport.WriteFile(library, "build.gradle", PluginGradle);
            TestSupport.WriteFile(library, "AndroidManifest.xml", FixedManifest);

            EosAndroidProjectFix.Result result = EosAndroidProjectFix.Adapt(module);

            Assert.IsTrue(result.GradleChanged);
            Assert.IsFalse(result.ManifestChanged);
            Assert.AreEqual(FixedGradle, Read(library, "build.gradle"));
            StringAssert.DoesNotContain("package=", Read(library, "AndroidManifest.xml"));
            StringAssert.DoesNotContain(", AndroidManifest.xml", result.Describe());
        }

        [Test]
        public void ABuildGradleThatCannotBeAdaptedIsReportedAndLeftAlone()
        {
            string module = NewModule();
            string library = Library(module);
            string old = Lines("buildscript {", "    repositories {", "        jcenter()", "    }", "}");
            TestSupport.WriteFile(library, "build.gradle", old);
            TestSupport.WriteFile(library, "AndroidManifest.xml", FixedManifest);

            EosAndroidProjectFix.Result result = EosAndroidProjectFix.Adapt(module);

            Assert.IsTrue(result.Found);
            Assert.IsFalse(result.GradleChanged);
            Assert.IsTrue(result.GradleStillOld);
            Assert.AreEqual(old, Read(library, "build.gradle"));
            StringAssert.Contains("left as it is", result.Describe());
        }

        [Test]
        public void ReadOnlyFilesAreAdaptedToo()
        {
            // A file that Unity copied from a read-only one is read-only. (Where the tests run as a user that may write anyway, such
            // as root on Linux, this only shows that the attribute does not get in the way.)
            string module = NewModule();
            string library = Library(module);
            WritePluginFiles(library);
            string gradlePath = Path.Combine(library, "build.gradle");
            string manifestPath = Path.Combine(library, "AndroidManifest.xml");
            File.SetAttributes(gradlePath, FileAttributes.ReadOnly);
            File.SetAttributes(manifestPath, FileAttributes.ReadOnly);
            try
            {
                EosAndroidProjectFix.Result result = EosAndroidProjectFix.Adapt(module);

                Assert.IsTrue(result.GradleChanged);
                Assert.IsTrue(result.ManifestChanged);
                AssertAdapted(library);
            }
            finally
            {
                File.SetAttributes(gradlePath, FileAttributes.Normal);
                File.SetAttributes(manifestPath, FileAttributes.Normal);
            }
        }
    }
}
