using System;
using System.IO;

namespace BrowserAssistScanner
{
    internal static class PreviewArtifacts
    {
        internal static string PathFor(string name) {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            for (string parent = root; parent != null; parent = Path.GetDirectoryName(parent.TrimEnd('\\', '/'))) {
                if (Directory.Exists(Path.Combine(parent, "src")) && File.Exists(Path.Combine(parent, "Test.ps1"))) { root = parent; break; }
                if (Path.GetPathRoot(parent) == parent) break;
            }
            string folder = Path.Combine(root, "test-artifacts", "ui-previews");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, Path.GetFileName(name));
        }
    }
}
