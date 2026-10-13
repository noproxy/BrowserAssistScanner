using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BrowserAssistScanner
{
    public sealed class HideReport
    {
        public readonly List<string> Completed = new List<string>();
        public readonly List<string> Issues = new List<string>();
        public int CreatedMarkers, ExistingMarkers, Ignored;
        public bool Cancelled;
    }

    public static class HideEngine
    {
        public static DeleteTarget[] SelectedVars(DeleteRequest request) {
            return request.Targets.Where(t => !t.IsDirectory && String.Equals(Path.GetExtension(t.Path), ".var", StringComparison.OrdinalIgnoreCase))
                .GroupBy(t => Path.GetFullPath(t.Path), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        }
        // VaM and BrowserAssist read resource-path .hide files under this exact version's preferences.
        // Do not alter BA's shared, version-independent userData records.
        private static bool Hideable(string path) {
            string p = path.Replace('/', '\\').ToLowerInvariant(), ext = Path.GetExtension(p);
            if (ext == ".ogg" || ext == ".wav" || ext == ".mp3") return true;
            if (ext == ".json") return p.StartsWith("saves\\scene\\") || p.StartsWith("custom\\subscene\\");
            if (ext == ".vap") return p.StartsWith("custom\\atom\\") || p.StartsWith("custom\\pluginpresets\\") || p.StartsWith("custom\\clothing\\") || p.StartsWith("custom\\hair\\");
            if (ext == ".vam") return p.StartsWith("custom\\clothing\\") || p.StartsWith("custom\\hair\\");
            if (ext == ".assetbundle" || ext == ".scene") return p.StartsWith("custom\\assets\\");
            return (ext == ".cs" || ext == ".cslist" || ext == ".dll") && p.StartsWith("custom\\scripts\\");
        }
        private static string MarkerPath(string root, string resource) {
            string relative = resource.Replace('/', '\\');
            if (relative.Split('\\').Any(p => p.Length == 0 || p == "." || p == ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.TrimEnd(' ', '.') != p))
                throw new InvalidDataException("包内资源路径无效：" + resource);
            string path = Path.GetFullPath(Path.Combine(root, relative + ".hide"));
            if (!DeleteScope.Descendant(path, root)) throw new InvalidDataException("隐藏标记超出包版本目录。");
            return path;
        }
        private static void ValidatePreferencePath(string vam, string path) {
            if (!DeleteScope.Descendant(path, vam)) throw new InvalidDataException("隐藏标记超出 VAM 目录。");
            for (string part = path; !String.Equals(part, vam, StringComparison.OrdinalIgnoreCase); part = Path.GetDirectoryName(part)) {
                if (File.Exists(part) || Directory.Exists(part)) {
                    if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("隐藏标记目录含链接：" + part);
                }
            }
        }
        public static HideReport Hide(DeleteRequest request, string vamPath, CancellationToken cancel, Action<string> progress) {
            string vam = DeleteScope.Normalize(Scanner.CanonicalPath(Path.GetFullPath(vamPath.Trim())));
            if (!Directory.Exists(vam)) throw new DirectoryNotFoundException("VAM 目录不存在：" + vam);
            var report = new HideReport();
            var targets = SelectedVars(request);
            report.Ignored = request.Targets.Where(t => t.IsDirectory || !String.Equals(Path.GetExtension(t.Path), ".var", StringComparison.OrdinalIgnoreCase))
                .Select(t => Path.GetFullPath(t.Path)).Concat(request.ParentCleanups.SelectMany(c => c.Directories).Select(Path.GetFullPath))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var scope = new DeleteScope(request.AllowedRoots);
            foreach (var target in targets) {
                if (cancel.IsCancellationRequested) { report.Cancelled = true; break; }
                try {
                    scope.Validate(target.Path, false);
                    if (Scanner.PackageKey(Path.GetFileName(target.Path)) == null) throw new InvalidDataException("VAR 包名不符合作者.包名.版本格式。");
                    string prefs = Path.Combine(vam, "AddonPackagesFilePrefs", Path.GetFileNameWithoutExtension(target.Path));
                    string[] markers;
                    using (var zip = ZipFile.OpenRead(target.Path)) {
                        markers = zip.Entries.Where(e => !String.IsNullOrEmpty(e.Name) && Hideable(e.FullName)).Select(e => MarkerPath(prefs, e.FullName))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    }
                    if (markers.Length == 0) throw new InvalidDataException("包内没有可隐藏的浏览器资源。");
                    foreach (string marker in markers) ValidatePreferencePath(vam, marker);
                    foreach (string marker in markers) {
                        cancel.ThrowIfCancellationRequested();
                        ValidatePreferencePath(vam, marker);
                        Directory.CreateDirectory(Path.GetDirectoryName(marker));
                        if (File.Exists(marker)) { report.ExistingMarkers++; continue; }
                        using (var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) file.WriteByte(0);
                        report.CreatedMarkers++;
                    }
                    report.Completed.Add(Path.GetFullPath(target.Path));
                    progress("已标记隐藏 " + report.Completed.Count + " / " + targets.Length + " 个 VAR");
                } catch (OperationCanceledException) { report.Cancelled = true; break; }
                catch (Exception error) { report.Issues.Add(target.Path + "：" + error.Message); }
            }
            return report;
        }
        public static Task<HideReport> HideAsync(DeleteRequest request, string vamPath, CancellationToken cancel, Action<string> progress) {
            return Task.Run(() => Hide(request, vamPath, cancel, progress));
        }
    }
}
