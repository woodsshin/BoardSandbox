using System;
using System.Collections.Generic;
using System.Text;

namespace BoardSandbox.Core.Agents
{
    public sealed class FileBlock
    {
        public FileBlock(string path, string content)
        {
            Path = path;
            Content = content;
        }

        public string Path { get; }
        public string Content { get; }
    }

    // The files inside the answer of an agent that cannot write them itself. A file is written as
    //
    //     === FILE: logic.luau ===
    //     ...the whole file...
    //     === END FILE ===
    //
    // and everything outside such blocks is what the agent has to say.
    public sealed class FileBlocks
    {
        private FileBlocks(List<FileBlock> files, string commentary)
        {
            Files = files;
            Commentary = commentary;
        }

        public IReadOnlyList<FileBlock> Files { get; }
        public string Commentary { get; }

        public static string Format(string path, string content)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("=== FILE: ").Append(path).Append(" ===\n").Append(content);
            if (content.Length > 0 && content[content.Length - 1] != '\n')
                builder.Append('\n');
            builder.Append("=== END FILE ===\n");
            return builder.ToString();
        }

        public static FileBlocks Parse(string text)
        {
            List<FileBlock> files = new List<FileBlock>();
            StringBuilder commentary = new StringBuilder();
            string[] lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            string path = null;
            List<string> body = new List<string>();
            foreach (string line in lines)
            {
                if (path == null)
                {
                    string opened = HeaderPath(line);
                    if (opened != null)
                    {
                        path = opened;
                        body.Clear();
                    }
                    else
                    {
                        commentary.Append(line).Append('\n');
                    }
                    continue;
                }

                // A model that forgets to close a block before the next one still means two files.
                string next = HeaderPath(line);
                if (next != null || IsEnd(line))
                {
                    files.Add(new FileBlock(path, Join(body)));
                    path = next;
                    body.Clear();
                    continue;
                }
                body.Add(line);
            }
            if (path != null)
                files.Add(new FileBlock(path, Join(body)));
            return new FileBlocks(files, TrimCommentary(commentary.ToString()));
        }

        private static string HeaderPath(string line)
        {
            string inner = Marker(line);
            if (inner == null || !inner.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase))
                return null;
            string path = inner.Substring(5).Trim().Trim('`', '"', '\'').Trim();
            return path.Length == 0 ? null : path;
        }

        private static bool IsEnd(string line)
        {
            string inner = Marker(line);
            return inner != null && (inner.Equals("END FILE", StringComparison.OrdinalIgnoreCase) || inner.Equals("END", StringComparison.OrdinalIgnoreCase));
        }

        // The text between the rows of equals signs, or null for a line that is not a marker.
        private static string Marker(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length < 8 || !trimmed.StartsWith("===", StringComparison.Ordinal) || !trimmed.EndsWith("===", StringComparison.Ordinal))
                return null;
            string inner = trimmed.Trim('=').Trim();
            return inner.Length == 0 ? null : inner;
        }

        // Models like to put the content of a file into a code fence; the fence is not part of the file.
        private static string Join(List<string> body)
        {
            int first = 0;
            int last = body.Count - 1;
            while (first <= last && body[first].Trim().Length == 0)
                first++;
            while (last >= first && body[last].Trim().Length == 0)
                last--;
            if (last > first && body[first].TrimStart().StartsWith("```", StringComparison.Ordinal) && body[last].Trim() == "```")
            {
                first++;
                last--;
            }
            StringBuilder builder = new StringBuilder();
            for (int i = first; i <= last; i++)
                builder.Append(body[i]).Append('\n');
            return builder.ToString();
        }

        private static string TrimCommentary(string text)
        {
            List<string> kept = new List<string>();
            foreach (string line in text.Split('\n'))
            {
                // A fence that was wrapped around a block has nothing left inside it.
                if (line.Trim().StartsWith("```", StringComparison.Ordinal))
                    continue;
                if (line.Trim().Length == 0 && (kept.Count == 0 || kept[kept.Count - 1].Length == 0))
                    continue;
                kept.Add(line.TrimEnd());
            }
            while (kept.Count > 0 && kept[kept.Count - 1].Length == 0)
                kept.RemoveAt(kept.Count - 1);
            return string.Join("\n", kept);
        }
    }
}
