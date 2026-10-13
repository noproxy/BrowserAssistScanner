using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace BrowserAssistScanner
{
    public sealed class ScanOptions
    {
        public string VamPath = @"G:\VAM-1.22.0.8";
        public string PackagesPath = @"G:\AddonPackages";
        public string Tag = "del";
        public bool Contains;
        public bool IncludeMissing = true;
    }

    public sealed class PackageRow
    {
        public string Name { get; set; }
        public string Favourite { get; set; }
        public string Hidden { get; set; }
        public string FullPath { get; set; }
        public string Tags { get; set; }
        public string Details { get; set; }
        public bool Missing { get; set; }
        public string[] SiblingNames { get; set; }
        public string[] DirectoryNames { get { return Missing || SiblingNames == null ? null : SiblingNames.Concat(new[] { System.IO.Path.GetFileName(FullPath) }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n.EndsWith("\\") || n.EndsWith("/") ? 0 : 1).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(); } }
        public int? SiblingCount { get { return SiblingNames == null ? (int?)null : SiblingNames.Length; } }
        public string SiblingError { get; set; }
        public RedundantParent[] RedundantParents { get; set; }
        public string ScanBoundary { get; set; }
        public bool AutoSelectSiblings { get; set; }
        public string SelectionReason { get; set; }
        public bool IsDependency { get; set; }
        public string DependencyDirectory { get; set; }
        public string[] FavouriteChildren { get; set; }
        public string Marks { get; set; }
        public Dictionary<string, PackageState> SiblingStates { get; set; }
        public PackageRow() { RedundantParents = new RedundantParent[0]; FavouriteChildren = new string[0]; Marks = ""; SiblingStates = new Dictionary<string, PackageState>(StringComparer.OrdinalIgnoreCase); }
    }

    public sealed class PackageState
    {
        public string Favourite { get; set; }
        public string Hidden { get; set; }
        internal bool Complete;
    }

    public sealed class ScanResult
    {
        public List<PackageRow> Rows = new List<PackageRow>();
        public List<string> Warnings = new List<string>();
        public int Groups;
        public int MatchedResources;
        public int MissingGroups;
        public int FileCount;
        public int DirectoryCount;
    }

    internal sealed class Resource
    {
        public string Creator, Package, Path, Source;
        public bool Favourite, Hidden;
        public List<string> Tags = new List<string>();
        public string Key { get { return Creator + "\0" + Package; } }
    }

    public static class Scanner
    {
        private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
        public static JavaScriptSerializer Json()
        {
            return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 128 };
        }

        private static string Text(IDictionary<string, object> value, string key)
        {
            object found;
            return value.TryGetValue(key, out found) && found != null ? Convert.ToString(found) : "";
        }

        public static bool TagMatches(string tag, ScanOptions options)
        {
            string query = (options.Tag ?? "").Trim();
            return query.Length == 0 || (options.Contains
                ? tag.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                : String.Equals(tag, query, StringComparison.OrdinalIgnoreCase));
        }

        public static ScanResult Scan(ScanOptions options, CancellationToken cancel, Action<string> progress)
        {
            string vam = System.IO.Path.GetFullPath(options.VamPath.Trim());
            string packages = System.IO.Path.GetFullPath(options.PackagesPath.Trim());
            if (!Directory.Exists(vam)) throw new DirectoryNotFoundException("VAM 目录不存在：" + vam);
            if (!Directory.Exists(packages)) throw new DirectoryNotFoundException("VAR 目录不存在：" + packages);
            string boundary = CanonicalPath(packages);
            string plugin = System.IO.Path.Combine(vam, @"Saves\PluginData\JayJayWon\BrowserAssist");
            string userData = System.IO.Path.Combine(plugin, "VARResourcesUserData");
            if (!Directory.Exists(userData)) throw new DirectoryNotFoundException("未找到 BrowserAssist 标签目录：" + userData);
            var result = new ScanResult();
            var records = new Dictionary<string, List<Resource>>(Comparer);
            var matched = new Dictionary<string, List<Resource>>(Comparer);
            string[] dataFiles = Directory.GetFiles(userData, "*.userData");
            if (dataFiles.Length == 0) throw new InvalidDataException("标签目录中没有 .userData 文件。请检查 VAM 路径。");
            int fileIndex = 0;
            foreach (string file in dataFiles)
            {
                cancel.ThrowIfCancellationRequested();
                progress("读取标签记录 " + (++fileIndex) + " / " + dataFiles.Length);
                try
                {
                    var root = Json().DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as IDictionary<string, object>;
                    object raw;
                    if (root == null || !root.TryGetValue("resources", out raw) || !(raw is object[]))
                        throw new InvalidDataException("缺少 resources 数组");
                    foreach (object item in (object[])raw)
                    {
                        var value = item as IDictionary<string, object>;
                        if (value == null) { result.Warnings.Add(file + "：无效的资源记录"); continue; }
                        var resource = new Resource {
                            Creator = Text(value, "creatorName"), Package = Text(value, "packageName"),
                            Path = Text(value, "resourceFullFileName").Replace('/', '\\'), Source = file,
                            Favourite = Text(value, "baFavourite") == "Active", Hidden = Text(value, "baHidden") == "Active"
                        };
                        if (resource.Creator.Length == 0 || resource.Package.Length == 0)
                        { result.Warnings.Add(file + "：跳过缺少作者或包名的记录"); continue; }
                        object tags;
                        if (value.TryGetValue("Tags", out tags) && tags is object[])
                            foreach (object tag in (object[])tags)
                            {
                                var tagValue = tag as IDictionary<string, object>;
                                if (tagValue != null && Text(tagValue, "tagName").Length > 0)
                                    resource.Tags.Add(Text(tagValue, "tagName"));
                            }
                        if (!records.ContainsKey(resource.Key)) records[resource.Key] = new List<Resource>();
                        records[resource.Key].Add(resource);
                        if (resource.Tags.Any(t => TagMatches(t, options)))
                        {
                            if (!matched.ContainsKey(resource.Key)) matched[resource.Key] = new List<Resource>();
                            matched[resource.Key].Add(resource);
                            result.MatchedResources++;
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { result.Warnings.Add(file + "：" + error.Message); }
            }
            result.Groups = matched.Count;
            if (matched.Count == 0) return result;
            var paths = matched.Keys.ToDictionary(key => key, key => new HashSet<string>(Comparer), Comparer);
            var visited = new HashSet<string>(Comparer);
            var allPaths = new HashSet<string>(Comparer);
            var pending = new Stack<string>();
            // Prefer the external physical directory when VAM has links into it.
            var roots = new[] { packages, System.IO.Path.Combine(vam, "AddonPackages"), System.IO.Path.Combine(plugin, "OffloadedVARs") };
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                pending.Push(root);
                while (pending.Count > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    string folder = pending.Pop();
                    string realFolder = CanonicalPath(folder);
                    if (!visited.Add(realFolder)) continue;
                    result.DirectoryCount++;
                    progress("扫描 VAR 目录：" + result.DirectoryCount + " 个目录，找到 " + paths.Values.Sum(x => x.Count) + " 个匹配文件");
                    try
                    {
                        foreach (string file in Directory.EnumerateFiles(folder, "*.var"))
                        {
                            cancel.ThrowIfCancellationRequested();
                            allPaths.Add(CanonicalPath(file));
                            string key = PackageKey(System.IO.Path.GetFileName(file));
                            if (key != null && paths.ContainsKey(key)) paths[key].Add(CanonicalPath(file));
                        }
                        foreach (string child in Directory.EnumerateDirectories(folder)) pending.Push(child);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { result.Warnings.Add(folder + "：" + error.Message); }
                }
            }
            int checkedFiles = 0;
            foreach (string key in matched.Keys.OrderBy(k => k, Comparer))
            {
                cancel.ThrowIfCancellationRequested();
                var tagged = matched[key];
                string tags = String.Join("、", tagged.SelectMany(r => r.Tags).Distinct(Comparer).OrderBy(t => t, Comparer));
                string details = "匹配的标签资源：\r\n" + String.Join("\r\n", tagged.Select(r =>
                    "  " + (r.Path.Length == 0 ? "[包记录]" : r.Path) + "    标签：" + String.Join("、", r.Tags)).Distinct());
                if (paths[key].Count == 0)
                {
                    result.MissingGroups++;
                    if (options.IncludeMissing) result.Rows.Add(new PackageRow {
                        Name = tagged[0].Creator + "." + tagged[0].Package + "（版本未确认）",
                        Favourite = records[key].Any(r => r.Favourite) ? "是（记录）" : "未标记（记录）",
                        Hidden = records[key].Any(r => r.Hidden) ? "是（记录）" : "未标记（记录）",
                        FullPath = "未找到磁盘文件，仅保留标签记录", Missing = true, Tags = tags,
                        Details = details + "\r\n\r\n未在扫描目录中找到 VAR 文件。收藏/隐藏仅反映 BrowserAssist 已保存的记录。"
                    });
                }
                foreach (string path in paths[key].OrderBy(p => p, Comparer))
                {
                    cancel.ThrowIfCancellationRequested();
                    progress("核对收藏和隐藏：" + (++checkedFiles) + " / " + paths.Values.Sum(x => x.Count));
                    PackageState state = ReadPackageState(path, records[key], vam, cancel, result.Warnings);
                    string prefs = System.IO.Path.Combine(vam, "AddonPackagesFilePrefs", System.IO.Path.GetFileNameWithoutExtension(path));
                    bool complete = state.Complete;
                    string[] siblings = null;
                    string siblingError = null;
                    RedundantParent[] parents = new RedundantParent[0];
                    try { siblings = ReadSiblingNames(path, cancel); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) {
                        siblingError = error.Message;
                        result.Warnings.Add(path + "：无法读取同级内容，" + error.Message);
                    }
                    try { parents = RedundantDirectories.Find(path, boundary, cancel); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { result.Warnings.Add(path + "：无法读取冗余父目录，" + error.Message); }
                    result.Rows.Add(new PackageRow {
                        Name = System.IO.Path.GetFileName(path), FullPath = path, Tags = tags,
                        Favourite = state.Favourite, Hidden = state.Hidden,
                        SiblingNames = siblings, SiblingError = siblingError,
                        RedundantParents = parents, ScanBoundary = boundary,
                        Details = details + "\r\n\r\n用户标记目录：" + prefs +
                            "\r\n“是”表示包内至少一项资源有 BrowserAssist 或 VAM 的用户标记。" +
                            (complete ? "" : "\r\n此文件的状态核对未完成，请查看扫描提示。")
                    });
                    result.FileCount++;
                }
            }
            PackageAnalysis.Apply(result, allPaths, records, vam, cancel, progress);
            return result;
        }

        internal static PackageState ReadPackageState(string path, IEnumerable<Resource> resources, string vam, CancellationToken cancel, List<string> warnings)
        {
            cancel.ThrowIfCancellationRequested();
            HashSet<string> contents = null;
            bool readable = true, prefsReadable = true;
            try {
                using (var archive = ZipFile.OpenRead(path)) contents = new HashSet<string>(archive.Entries.Select(e => e.FullName.Replace('/', '\\')), Comparer);
            } catch (OperationCanceledException) { throw; }
            catch (Exception error) { readable = false; warnings.Add(path + "：无法读取 VAR 目录，" + error.Message); }
            var applicable = (resources ?? new Resource[0]).Where(r => r.Path.Length == 0 || (contents != null && contents.Contains(r.Path))).ToList();
            bool favourite = applicable.Any(r => r.Favourite), hidden = applicable.Any(r => r.Hidden);
            string prefs = System.IO.Path.Combine(vam, "AddonPackagesFilePrefs", System.IO.Path.GetFileNameWithoutExtension(path));
            if (Directory.Exists(prefs)) foreach (string file in WalkPreferenceFiles(prefs, cancel, warnings, delegate { prefsReadable = false; })) {
                if (file.EndsWith(".fav", StringComparison.OrdinalIgnoreCase)) favourite = true;
                if (file.EndsWith(".hide", StringComparison.OrdinalIgnoreCase)) hidden = true;
            }
            bool complete = readable && prefsReadable;
            return new PackageState { Favourite = favourite ? "是" : complete ? "否" : "未知", Hidden = hidden ? "是" : complete ? "否" : "未知", Complete = complete };
        }

        public static string[] ReadSiblingNames(string target, CancellationToken cancel)
        {
            if (!File.Exists(target)) throw new FileNotFoundException("目标文件已移动或删除。", target);
            string targetName = System.IO.Path.GetFileName(target);
            var names = new List<string>();
            foreach (string entry in Directory.EnumerateFileSystemEntries(System.IO.Path.GetDirectoryName(target)))
            {
                cancel.ThrowIfCancellationRequested();
                string name = System.IO.Path.GetFileName(entry);
                if (Comparer.Equals(name, targetName)) continue;
                bool folder = (File.GetAttributes(entry) & FileAttributes.Directory) != 0;
                names.Add(name + (folder ? System.IO.Path.DirectorySeparatorChar.ToString() : ""));
            }
            return names.OrderBy(n => n.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString()) ? 0 : 1).ThenBy(n => n, Comparer).ToArray();
        }

        private static IEnumerable<string> WalkPreferenceFiles(string root, CancellationToken cancel, List<string> warnings, Action failed)
        {
            var pending = new Stack<string>();
            var seen = new HashSet<string>(Comparer);
            pending.Push(root);
            while (pending.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                string folder = pending.Pop();
                if (!seen.Add(CanonicalPath(folder))) continue;
                string[] files = new string[0], dirs = new string[0];
                try { files = Directory.GetFiles(folder); dirs = Directory.GetDirectories(folder); }
                catch (Exception error) { failed(); warnings.Add(folder + "：" + error.Message); }
                foreach (string file in files) yield return file;
                foreach (string dir in dirs) pending.Push(dir);
            }
        }

        internal static string PackageKey(string name)
        {
            if (!name.EndsWith(".var", StringComparison.OrdinalIgnoreCase)) return null;
            string uid = name.Substring(0, name.Length - 4);
            int versionDot = uid.LastIndexOf('.'), version;
            if (versionDot <= 0 || !Int32.TryParse(uid.Substring(versionDot + 1), out version)) return null;
            int packageDot = uid.LastIndexOf('.', versionDot - 1);
            if (packageDot <= 0 || packageDot == versionDot - 1) return null;
            return uid.Substring(0, packageDot) + "\0" + uid.Substring(packageDot + 1, versionDot - packageDot - 1);
        }

        public static string CanonicalPath(string path)
        {
            // Resolve junctions and links to avoid duplicate paths and recursive link loops.
            using (var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                if (!handle.IsInvalid)
                {
                    var buffer = new StringBuilder(32768);
                    uint size = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                    if (size > 0 && size < buffer.Capacity)
                    {
                        string value = buffer.ToString();
                        if (value.StartsWith(@"\\?\UNC\")) return @"\\" + value.Substring(8);
                        if (value.StartsWith(@"\\?\")) return value.Substring(4);
                        return value;
                    }
                }
            }
            return System.IO.Path.GetFullPath(path);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    }
}
