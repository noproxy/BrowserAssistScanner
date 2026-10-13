using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace BrowserAssistScanner
{
    public sealed class RedundantParent
    {
        public string Path { get; set; }
        public string Label { get; set; }
    }
    public sealed class ParentCleanup
    {
        public string Boundary;
        // Leaf first, ending at the selected ancestor. The scan boundary is excluded.
        public string[] Directories;
    }
    public static class RedundantDirectories
    {
        public static RedundantParent[] Find(string target, string boundary, CancellationToken cancel)
        {
            target = DeleteScope.Normalize(target); boundary = DeleteScope.Normalize(boundary);
            string leaf = System.IO.Path.GetDirectoryName(target), current = leaf;
            var found = new List<RedundantParent>();
            while (DeleteScope.Descendant(current, boundary)) {
                cancel.ThrowIfCancellationRequested();
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) break;
                string baseFolder = System.IO.Path.GetDirectoryName(current);
                string label = leaf.Substring(baseFolder.TrimEnd('\\').Length + 1) + "\\";
                found.Add(new RedundantParent { Path = current, Label = label });
                if (!DeleteScope.Descendant(baseFolder, boundary)) break;
                // The next ancestor is eligible only if its sole child is this directory.
                string[] entries = Directory.GetFileSystemEntries(baseFolder);
                if (entries.Length != 1 || !String.Equals(DeleteScope.Normalize(entries[0]), current, StringComparison.OrdinalIgnoreCase)) break;
                current = baseFolder;
            }
            return found.ToArray();
        }
    }
}
