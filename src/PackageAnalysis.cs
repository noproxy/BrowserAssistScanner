using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace BrowserAssistScanner
{
    internal static class PackageAnalysis
    {
        private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
        // Names observed in the package tree. Match words, never arbitrary substrings such as "independent".
        private static readonly Regex DependencyName = new Regex(@"依赖|(^|[\s_\-])(?:dp|dep|deps|depend|depends|dependent|dependency|dependencies)(?=$|[\s_\-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        internal static bool IsDependencyName(string name) { return DependencyName.IsMatch(name); }

        internal static void Apply(ScanResult result, IEnumerable<string> paths, Dictionary<string, List<Resource>> records,
            string vam, CancellationToken cancel, Action<string> progress)
        {
            string[] allPaths = paths.ToArray();
            var favourites = new HashSet<string>(Comparer);
            var known = result.Rows.Where(r => !r.Missing).ToDictionary(r => r.FullPath, Comparer);
            var states = known.ToDictionary(pair => pair.Key, pair => new PackageState { Favourite = pair.Value.Favourite, Hidden = pair.Value.Hidden }, Comparer);
            int siblingIndex = 0;
            foreach (var row in known.Values) {
                if (row.SiblingNames == null) continue;
                foreach (string name in row.DirectoryNames.Where(n => !IsFolder(n) && n.EndsWith(".var", StringComparison.OrdinalIgnoreCase))) {
                    cancel.ThrowIfCancellationRequested();
                    string path = Scanner.CanonicalPath(Path.Combine(Path.GetDirectoryName(row.FullPath), name));
                    PackageState state;
                    if (!states.TryGetValue(path, out state)) {
                        progress("核对同级文件收藏和隐藏：" + (++siblingIndex) + " 个 VAR");
                        List<Resource> resources;
                        records.TryGetValue(Scanner.PackageKey(Path.GetFileName(path)) ?? "", out resources);
                        state = Scanner.ReadPackageState(path, resources, vam, cancel, result.Warnings);
                        states[path] = state;
                    }
                    row.SiblingStates[name] = state;
                }
            }
            string prefsRoot = Path.Combine(vam, "AddonPackagesFilePrefs");
            var prefUids = new HashSet<string>(Comparer);
            try { if (Directory.Exists(prefsRoot)) foreach (string dir in Directory.EnumerateDirectories(prefsRoot)) prefUids.Add(Path.GetFileName(dir)); }
            catch (Exception error) { result.Warnings.Add("无法读取收藏标记目录：" + error.Message); }
            int index = 0;
            foreach (string path in allPaths) {
                cancel.ThrowIfCancellationRequested();
                string uid = Path.GetFileNameWithoutExtension(path), key = Scanner.PackageKey(Path.GetFileName(path));
                List<Resource> resources;
                records.TryGetValue(key ?? "", out resources);
                bool baCandidate = resources != null && resources.Any(r => r.Favourite);
                if (!baCandidate && !prefUids.Contains(uid)) continue;
                progress("核对依赖收藏：" + (++index) + " 个候选 VAR");
                PackageState knownState;
                if (states.TryGetValue(path, out knownState)) { if (knownState.Favourite == "是") favourites.Add(path); continue; }
                try {
                    bool native = HasFavouriteMarker(Path.Combine(prefsRoot, uid), cancel);
                    if (native) { favourites.Add(path); continue; }
                    if (!baCandidate) continue;
                    using (var zip = ZipFile.OpenRead(path)) {
                        var names = new HashSet<string>(zip.Entries.Select(e => e.FullName.Replace('/', '\\')), Comparer);
                        if (resources.Any(r => r.Favourite && (r.Path.Length == 0 || names.Contains(r.Path)))) favourites.Add(path);
                    }
                } catch (OperationCanceledException) { throw; }
                catch (Exception error) { result.Warnings.Add(path + "：依赖收藏核对未完成，" + error.Message); }
            }

            foreach (var row in result.Rows.Where(r => !r.Missing)) {
                cancel.ThrowIfCancellationRequested();
                progress("分析同级项和依赖：" + row.Name);
                string folder = Path.GetDirectoryName(row.FullPath);
                // Include the scan root itself when the user scans a dependency folder directly.
                string current = folder;
                while (!String.IsNullOrEmpty(current)) {
                    if (IsDependencyName(Path.GetFileName(current))) { row.IsDependency = true; row.DependencyDirectory = current; break; }
                    if (Comparer.Equals(current.TrimEnd('\\'), row.ScanBoundary.TrimEnd('\\'))) break;
                    current = Path.GetDirectoryName(current);
                }
                AnalyseSelection(row, cancel);
                // Only files inside immediate child folders (recursively) qualify, never flat siblings.
                var childFavourites = new HashSet<string>(favourites.Where(p => DeleteScope.Descendant(p, folder)
                    && !Comparer.Equals(Path.GetDirectoryName(p), folder)), Comparer);
                row.FavouriteChildren = childFavourites.OrderBy(p => p, Comparer).ToArray();
                row.Marks = String.Join("；", new[] { row.IsDependency ? "依赖" : null, childFavourites.Count > 0 ? "有收藏的子文件" : null }.Where(s => s != null));
                string analysis = "智能勾选：" + row.SelectionReason;
                if (row.IsDependency) analysis += "\r\n依赖：位于 " + row.DependencyDirectory + "。建议只做隐藏；删除可能影响引用它的场景或人物。此提示不限制操作。";
                if (childFavourites.Count > 0) {
                    analysis += "\r\n有收藏的子文件：建议先人工移出待删除目录中的收藏 VAR，并检查引用关系，再删除。此提示不限制操作。";
                    foreach (string path in row.FavouriteChildren) analysis += "\r\n  " + path;
                }
                row.Details = analysis + "\r\n\r\n" + row.Details;
            }
        }

        private static bool HasFavouriteMarker(string root, CancellationToken cancel) {
            if (!Directory.Exists(root)) return false;
            var pending = new Stack<string>(); var seen = new HashSet<string>(Comparer); pending.Push(root);
            while (pending.Count > 0) {
                cancel.ThrowIfCancellationRequested(); string dir = pending.Pop();
                if (!seen.Add(Scanner.CanonicalPath(dir))) continue;
                if (Directory.EnumerateFiles(dir, "*.fav").Any()) return true;
                foreach (string child in Directory.EnumerateDirectories(dir)) pending.Push(child);
            }
            return false;
        }
        private static bool IsFolder(string name) { return name.EndsWith("\\") || name.EndsWith("/"); }
        private static string Bare(string name) { return name.TrimEnd('\\', '/'); }
        private static void AnalyseSelection(PackageRow row, CancellationToken cancel) {
            row.AutoSelectSiblings = false;
            if (row.SiblingNames == null) { row.SelectionReason = "手动选择：同级内容无法读取"; return; }
            if (row.SiblingNames.Any(n => !IsFolder(n) && n.EndsWith(".var", StringComparison.OrdinalIgnoreCase))) {
                row.SelectionReason = "手动选择：同级存在其他 VAR，可能共享依赖"; return;
            }
            try {
                string folder = Path.GetDirectoryName(row.FullPath);
                foreach (string name in row.SiblingNames) {
                    string path = Path.Combine(folder, Bare(name));
                    var pending = new Stack<string>(); pending.Push(path);
                    bool dependency = IsFolder(name) && IsDependencyName(Bare(name));
                    while (pending.Count > 0) {
                        cancel.ThrowIfCancellationRequested(); string item = pending.Pop();
                        FileAttributes attrs = File.GetAttributes(item);
                        if ((attrs & FileAttributes.ReparsePoint) != 0) { row.SelectionReason = "手动选择：同级内容含目录链接或文件链接"; return; }
                        if ((attrs & FileAttributes.Directory) != 0) foreach (string child in Directory.EnumerateFileSystemEntries(item)) pending.Push(child);
                        else if (!dependency && item.EndsWith(".var", StringComparison.OrdinalIgnoreCase)) { row.SelectionReason = "手动选择：普通子目录内存在 VAR，归属不明"; return; }
                    }
                }
                row.AutoSelectSiblings = true;
                row.SelectionReason = "自动勾选全部同级项" + (row.RedundantParents.Length > 0 ? "及层级最多的冗余父目录（列表最后一项）" : "；扫描根目录不勾选");
            } catch (OperationCanceledException) { throw; }
            catch (Exception error) { row.SelectionReason = "手动选择：同级内容核对失败，" + error.Message; }
        }
    }
}
