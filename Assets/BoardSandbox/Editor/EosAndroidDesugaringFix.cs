using System.Text;
using System.Text.RegularExpressions;

namespace BoardSandbox.Client.Editor
{
    // The EOS SDK (eos-sdk.aar) is built with core library desugaring, and the Android Gradle plugin builds an application that has
    // it as a dependency only if the application desugars as well: "Dependency ':eos-sdk:' requires core library desugaring to be
    // enabled for :launcher". The documentation of the EOS plugin asks for the setting in the Gradle templates for Unity 6.
    //
    // These functions put it into the build.gradle of the application module of a Gradle project that Unity has written: the flag in
    // the android block, and the library that the desugaring is done with. What is missing is added as blocks at the end of the
    // file. Gradle takes a second android or dependencies block as more of the same, so the blocks that the file has, which are not
    // the same in every version of Unity, are neither parsed nor moved. A module that desugars already is not touched.
    //
    // They are only text work, with no Unity types, so that the tests can run without the Editor. EosAndroidProjectFix applies
    // them to the project, and Tools/FixEosAndroidExport.ps1 is a copy of them in PowerShell.
    public static class EosAndroidDesugaringFix
    {
        // What the Android Gradle plugin desugars with, from Google's Maven repository, which a Gradle project of Unity reads. The
        // documentation of the EOS plugin names 2.0.4 (written for the Android Gradle plugin 8). If Gradle does not take this
        // version, that one is the first to try.
        public const string DesugarLibrary = "com.android.tools:desugar_jdk_libs:2.1.5";

        private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.Multiline;

        private static readonly Regex AndroidBlock = new Regex(@"^[ \t]*android[ \t]*\{", Options);

        private static readonly Regex FlagOn = new Regex(
            @"^[ \t]*(?:isCoreLibraryDesugaringEnabled|coreLibraryDesugaringEnabled)[ \t]*=?[ \t]*true\b", Options);

        // The dependency, not the flag, whose name starts the same.
        private static readonly Regex Dependency = new Regex(@"^[ \t]*coreLibraryDesugaring(?![A-Za-z0-9_])", Options);

        // True for the build file of a module (it has an android block) that does not desugar yet: the flag is not on, or there is
        // no library to desugar with.
        public static bool NeedsFix(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            string code = WithoutLineComments(text);
            return AndroidBlock.IsMatch(code) && (!FlagOn.IsMatch(code) || !Dependency.IsMatch(code));
        }

        // The text with what is missing added at its end, in the line endings of the file. Text that does not need it is returned as
        // it is.
        public static string Fix(string text)
        {
            if (!NeedsFix(text))
                return text;

            string code = WithoutLineComments(text);
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            StringBuilder result = new StringBuilder(text);
            if (text[text.Length - 1] != '\n')
                result.Append(newline);
            result.Append(newline);
            result.Append("// The AAR of the EOS SDK asks the application for core library desugaring (Board Sandbox, EosAndroidDesugaringFix).")
                .Append(newline);
            if (!FlagOn.IsMatch(code))
            {
                result.Append("android {").Append(newline);
                result.Append("    compileOptions {").Append(newline);
                result.Append("        coreLibraryDesugaringEnabled true").Append(newline);
                result.Append("    }").Append(newline);
                result.Append("}").Append(newline);
            }
            if (!Dependency.IsMatch(code))
            {
                result.Append("dependencies {").Append(newline);
                result.Append("    coreLibraryDesugaring '").Append(DesugarLibrary).Append('\'').Append(newline);
                result.Append("}").Append(newline);
            }
            return result.ToString();
        }

        // The words after // are no code. Quotes are not looked at: nothing that is looked for sits in a string.
        private static string WithoutLineComments(string text)
        {
            return Regex.Replace(text, @"//[^\r\n]*", string.Empty, RegexOptions.CultureInvariant);
        }
    }
}
