using System;
using System.Collections.Generic;
using System.IO;

namespace BoardSandbox.Core.Agents
{
    // Finds the program of a command-line agent the way a shell would, and in the places where the installers of
    // such tools put them when the app was not started from a shell.
    public static class AgentLocator
    {
        public static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        // The full path of the program, or null when there is none.
        public static string Find(string command)
        {
            return Find(command, Environment.GetEnvironmentVariable("PATH"), HomeDirectory());
        }

        public static string Find(string command, string searchPath, string home)
        {
            if (string.IsNullOrWhiteSpace(command))
                return null;
            command = command.Trim();
            if (command.IndexOf('/') >= 0 || command.IndexOf('\\') >= 0)
                return Resolve(command);

            foreach (string folder in Folders(searchPath, home))
            {
                string found = Resolve(Path.Combine(folder, command));
                if (found != null)
                    return found;
            }
            return null;
        }

        // Whether the program is a batch file, which Windows only runs through its command interpreter.
        public static bool NeedsCommandInterpreter(string path)
        {
            if (!IsWindows || path == null)
                return false;
            string extension = Path.GetExtension(path);
            return string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase);
        }

        private static string Resolve(string candidate)
        {
            try
            {
                if (IsWindows)
                {
                    if (Path.GetExtension(candidate).Length > 0)
                        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
                    foreach (string extension in new[] { ".exe", ".cmd", ".bat" })
                    {
                        if (File.Exists(candidate + extension))
                            return Path.GetFullPath(candidate + extension);
                    }
                    return null;
                }
                return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
            }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is NotSupportedException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static IEnumerable<string> Folders(string searchPath, string home)
        {
            HashSet<string> seen = new HashSet<string>(IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(searchPath))
            {
                foreach (string part in searchPath.Split(Path.PathSeparator))
                {
                    string folder = part.Trim().Trim('"');
                    if (folder.Length > 0 && seen.Add(folder))
                        yield return folder;
                }
            }

            List<string> extra = new List<string>();
            if (!string.IsNullOrEmpty(home))
            {
                extra.Add(Path.Combine(home, ".local", "bin"));
                extra.Add(Path.Combine(home, ".npm-global", "bin"));
                extra.Add(Path.Combine(home, ".bun", "bin"));
                extra.Add(Path.Combine(home, ".grok", "bin"));
            }
            if (IsWindows)
            {
                string roaming = Environment.GetEnvironmentVariable("APPDATA");
                if (!string.IsNullOrEmpty(roaming))
                    extra.Add(Path.Combine(roaming, "npm"));
                string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (!string.IsNullOrEmpty(local))
                    extra.Add(Path.Combine(local, "Programs"));
            }
            else
            {
                extra.Add("/usr/local/bin");
                extra.Add("/opt/homebrew/bin");
            }
            foreach (string folder in extra)
            {
                if (seen.Add(folder))
                    yield return folder;
            }
        }

        private static string HomeDirectory()
        {
            string home = Environment.GetEnvironmentVariable(IsWindows ? "USERPROFILE" : "HOME");
            if (!string.IsNullOrEmpty(home))
                return home;
            try
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
