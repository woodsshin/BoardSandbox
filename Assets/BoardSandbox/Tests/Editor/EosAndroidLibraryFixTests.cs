using BoardSandbox.Client.Editor;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    // The adaptation of the library of the EOS Plugin for Unity 6.2.0 to Gradle 9.1 and Android Gradle plugin 9.0, the build tools of
    // Unity 6000.3.17 and later: the old file stops Gradle at jcenter(), and then at the missing namespace.
    [TestFixture]
    public class EosAndroidLibraryFixTests
    {
        private static string Lines(params string[] lines)
        {
            return string.Join("\n", lines);
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
    }
}
