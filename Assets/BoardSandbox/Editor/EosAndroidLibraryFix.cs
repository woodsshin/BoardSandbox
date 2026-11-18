using System.Text;
using System.Text.RegularExpressions;

namespace BoardSandbox.Client.Editor
{
    // The Gradle project of an Android build holds a copy of eos_dependencies.androidlib, the library module of the EOS Plugin for
    // Unity. Version 6.2.0 of the plugin writes it for an old Android Gradle plugin: a buildscript block with jcenter() and its own
    // plugin version, the compileSdkVersion and targetSdkVersion properties, and package="..." in the manifest. Unity 6000.3.17 and
    // later build with Gradle 9.1 and Android Gradle plugin 9.0, and there Gradle stops at "Could not find method jcenter()" (the
    // method is gone), the plugin takes the namespace from build.gradle only, and the old properties are gone.
    //
    // These functions turn the two files into what such a build accepts. They are only text work, with no Unity types, so that the
    // tests can run without the Editor. A file that is already in the new form is not touched, so a plugin that has been fixed
    // is left alone. EosAndroidLibraryBuildStep applies them to the generated project.
    //
    // Tools/FixEosAndroidExport.ps1 is a copy of this class and of EosAndroidProjectFix in PowerShell, for a folder that was
    // exported without Unity running. A change to the patterns or the steps here is to be made there as well.
    public static class EosAndroidLibraryFix
    {
        public const string LibraryFolder = "eos_dependencies.androidlib";

        // The package the manifest of the library has had in every version of the plugin.
        public const string DefaultNamespace = "com.pew.eos_dependencies";

        private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.Multiline;

        private static readonly Regex Jcenter = new Regex(@"\bjcenter[ \t]*\(", Options);
        private static readonly Regex BuildscriptBlock = new Regex(@"^[ \t]*buildscript[ \t]*\{", Options);
        private static readonly Regex OldVersionProperty = new Regex(@"^[ \t]*(compileSdkVersion|targetSdkVersion)\b", Options);
        private static readonly Regex NamespaceProperty = new Regex(@"^[ \t]*namespace\b", Options);
        private static readonly Regex AndroidBlock = new Regex(@"^([ \t]*)android[ \t]*\{[ \t]*(\r\n|\n)?", Options);
        private static readonly Regex CompileSdk = new Regex(@"^[ \t]*compileSdk(?:Version)?[ \t]*=?[ \t]*(\d+)", Options);
        private static readonly Regex NamespaceValue = new Regex(@"^[ \t]*namespace[ \t]*=?[ \t]*['""]([^'""\r\n]+)['""]", Options);

        // The lines that are written again at the top of the android block, or that have no use in a library any more.
        private static readonly Regex RewrittenLine = new Regex(
            @"^[ \t]*(compileSdkVersion|compileSdk|buildToolsVersion|targetSdkVersion|namespace)\b[^\r\n]*(\r\n|\n)?", Options);

        private static readonly Regex EmptyDefaultConfig = new Regex(@"^[ \t]*defaultConfig[ \t]*\{\s*\}[ \t]*(\r\n|\n)?", Options);

        private static readonly Regex ManifestStartTag = new Regex(@"<manifest\b[^>]*>", RegexOptions.CultureInvariant);
        private static readonly Regex PackageAttribute = new Regex(@"\s+package\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.CultureInvariant);

        // ---- build.gradle --------------------------------------------------------------------------------------------------

        public static bool BuildGradleNeedsFix(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            string code = WithoutLineComments(text);
            return Jcenter.IsMatch(code) || BuildscriptBlock.IsMatch(code) || OldVersionProperty.IsMatch(code)
                || !NamespaceProperty.IsMatch(code);
        }

        // The compileSdk of a build.gradle (of the Unity library module, say), or null.
        public static string FindCompileSdk(string text)
        {
            return string.IsNullOrEmpty(text) ? null : Capture(CompileSdk, WithoutLineComments(text), 1);
        }

        // manifestNamespace is the package of the manifest, as FixManifest returned it. fallbackCompileSdk is for a file that does
        // not say which SDK it is compiled against. Text that has no android block is returned as it is.
        public static string FixBuildGradle(string text, string manifestNamespace, string fallbackCompileSdk)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            string code = WithoutLineComments(text);
            if (!AndroidBlock.IsMatch(text))
                return text;

            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            string compileSdk = Capture(CompileSdk, code, 1) ?? fallbackCompileSdk;
            string ns = Capture(NamespaceValue, code, 1) ?? (string.IsNullOrEmpty(manifestNamespace) ? DefaultNamespace : manifestNamespace);

            string result = RemoveBlock(text, "buildscript");
            result = RewrittenLine.Replace(result, string.Empty);
            result = EmptyDefaultConfig.Replace(result, string.Empty);

            // The build tools are left out on purpose. Every module of the project is built by the same Android Gradle plugin, which
            // brings its own version for a module that does not ask for one.
            Match android = AndroidBlock.Match(result);
            if (!android.Success)
                return text;
            string indent = android.Groups[1].Value + "    ";
            StringBuilder lines = new StringBuilder();
            if (android.Groups[2].Length == 0)
                lines.Append(newline);
            lines.Append(indent).Append("namespace '").Append(ns).Append('\'').Append(newline);
            if (!string.IsNullOrEmpty(compileSdk))
                lines.Append(indent).Append("compileSdk ").Append(compileSdk).Append(newline);
            return result.Insert(android.Index + android.Length, lines.ToString());
        }

        // ---- AndroidManifest.xml -------------------------------------------------------------------------------------------

        public static bool ManifestNeedsFix(string text)
        {
            return !string.IsNullOrEmpty(text) && FindPackage(text, out _, out _);
        }

        // Takes the package attribute off the manifest tag. The package is what the library is called in build.gradle from now on.
        public static string FixManifest(string text, out string package)
        {
            package = null;
            if (!FindPackage(text, out Match tag, out Match attribute))
                return text;
            package = attribute.Groups[1].Success ? attribute.Groups[1].Value : attribute.Groups[2].Value;
            string fixedTag = tag.Value.Remove(attribute.Index, attribute.Length);
            return text.Substring(0, tag.Index) + fixedTag + text.Substring(tag.Index + tag.Length);
        }

        private static bool FindPackage(string text, out Match tag, out Match attribute)
        {
            attribute = null;
            tag = string.IsNullOrEmpty(text) ? Match.Empty : ManifestStartTag.Match(text);
            if (!tag.Success)
                return false;
            attribute = PackageAttribute.Match(tag.Value);
            return attribute.Success;
        }

        // ---- text ----------------------------------------------------------------------------------------------------------

        private static string Capture(Regex pattern, string text, int group)
        {
            Match match = pattern.Match(text);
            return match.Success ? match.Groups[group].Value : null;
        }

        // The words after // are no code. Quotes are not looked at: nothing that is looked for sits in a string.
        private static string WithoutLineComments(string text)
        {
            return Regex.Replace(text, @"//[^\r\n]*", string.Empty, RegexOptions.CultureInvariant);
        }

        // Takes the first block that starts a line with the keyword out of the text, with the braces inside it.
        // Text whose braces do not match is returned as it is.
        private static string RemoveBlock(string text, string keyword)
        {
            Regex start = new Regex(@"^[ \t]*" + Regex.Escape(keyword) + @"[ \t]*\{", Options);
            Match match = start.Match(text);
            if (!match.Success)
                return text;
            int depth = 0;
            for (int i = match.Index + match.Length - 1; i < text.Length; i++)
            {
                if (text[i] == '{')
                    depth++;
                else if (text[i] == '}' && --depth == 0)
                {
                    int end = i + 1;
                    while (end < text.Length && (text[end] == ' ' || text[end] == '\t'))
                        end++;
                    if (end < text.Length && text[end] == '\r')
                        end++;
                    if (end < text.Length && text[end] == '\n')
                        end++;
                    return text.Remove(match.Index, end - match.Index);
                }
            }
            return text;
        }
    }
}
