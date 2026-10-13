using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BrowserAssistScanner
{
    public sealed class DeleteTarget
    {
        public string Path;
        public bool IsDirectory;
    }

    public sealed class DeleteRequest
    {
        public List<DeleteTarget> Targets = new List<DeleteTarget>();
        public string[] AllowedRoots;
        public List<ParentCleanup> ParentCleanups = new List<ParentCleanup>();
    }

    public sealed class SelectionState
    {
        private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
        private sealed class Group
        {
            public PackageRow Row;
            public readonly Dictionary<string, DeleteTarget> Targets = new Dictionary<string, DeleteTarget>(Comparer);
            public string Redundant;
        }
        // Each directory has one selection, shared by every view of its files.
        private readonly Dictionary<string, Group> groups = new Dictionary<string, Group>(Comparer);
        private readonly Dictionary<string, PackageRow> rows = new Dictionary<string, PackageRow>(Comparer);
        private static string Identity(string path) { return Path.GetFullPath(path); }
        private static string Folder(PackageRow row) { return Path.GetDirectoryName(Identity(row.FullPath)); }
        private static string Bare(string name) { return name.TrimEnd('\\', '/'); }
        private Group Existing(PackageRow row) { Group group; return !row.Missing && groups.TryGetValue(Folder(row), out group) ? group : null; }
        private Group Ensure(PackageRow row) {
            Group group; string folder = Folder(row);
            if (!groups.TryGetValue(folder, out group)) { group = new Group { Row = row }; groups.Add(folder, group); }
            rows[Identity(row.FullPath)] = row;
            return group;
        }
        private static void Add(Group group, string folder, string name) {
            string bare = Bare(name);
            if (String.IsNullOrEmpty(bare) || bare == "." || bare == ".." || bare != Path.GetFileName(bare) || bare.Contains("\\") || bare.Contains("/")) throw new InvalidDataException("无效的同级名称：" + name);
            group.Targets[bare] = new DeleteTarget { Path = Path.Combine(folder, bare), IsDirectory = name.EndsWith("\\") || name.EndsWith("/") };
        }
        public bool IsParentChecked(PackageRow row) { var group = Existing(row); return group != null && group.Targets.ContainsKey(Path.GetFileName(row.FullPath)); }
        public bool HasSelections(PackageRow row) { var group = Existing(row); return group != null && group.Targets.Count > 0; }
        public bool IsSiblingChecked(PackageRow row, string name) { var group = Existing(row); return group != null && group.Targets.ContainsKey(Bare(name)); }
        public void SetParent(PackageRow row, bool check) {
            if (row.Missing) return;
            string folder = Folder(row), name = Path.GetFileName(row.FullPath);
            rows[Identity(row.FullPath)] = row;
            if (!check) {
                var existing = Existing(row); if (existing == null) return;
                existing.Targets.Remove(name); existing.Redundant = null;
                // Preserve other selected top-level VARs and their shared companions.
                // Cancelling the last top-level VAR clears its supplemental selections.
                if (!rows.Values.Any(r => !r.Missing && Comparer.Equals(Folder(r), folder) && IsParentChecked(r))) groups.Remove(folder);
                return;
            }
            var group = Ensure(row);
            if (group.Targets.ContainsKey(name)) return;
            Add(group, folder, name);
            if (row.AutoSelectSiblings && row.DirectoryNames != null) {
                foreach (string item in row.DirectoryNames) Add(group, folder, item);
                if (row.RedundantParents.Length > 0) group.Redundant = row.RedundantParents.Last().Path;
                group.Row = row;
            }
        }
        public void SetSibling(PackageRow row, string name, bool check) {
            if (row.Missing) return;
            if (Comparer.Equals(Bare(name), Path.GetFileName(row.FullPath))) { SetParent(row, check); return; }
            if (row.DirectoryNames == null || !row.DirectoryNames.Contains(name, Comparer)) return;
            string path = Identity(Path.Combine(Folder(row), Bare(name)));
            PackageRow peer;
            if (rows.TryGetValue(path, out peer)) { SetParent(peer, check); return; }
            var group = Existing(row); if (group == null || group.Targets.Count == 0) return;
            if (check) Add(group, Folder(row), name);
            else { group.Targets.Remove(Bare(name)); group.Redundant = null; }
        }
        public void Clear() { groups.Clear(); }
        public void RemoveCompletedFiles(IEnumerable<string> paths) {
            var completed = new HashSet<string>(paths.Select(Identity), Comparer);
            foreach (string folder in groups.Keys.ToArray()) {
                var group = groups[folder];
                bool removed = false;
                foreach (string name in group.Targets.Keys.ToArray()) {
                    if (completed.Contains(Identity(group.Targets[name].Path))) { group.Targets.Remove(name); removed = true; }
                }
                if (!removed) continue;
                group.Redundant = null;
                if (!rows.Values.Any(r => Comparer.Equals(Folder(r), folder) && IsParentChecked(r))) groups.Remove(folder);
            }
        }
        public string SelectedRedundant(PackageRow row) { var group = Existing(row); return group == null ? null : group.Redundant; }
        public void SetRedundant(PackageRow row, string path) {
            var group = Existing(row); if (group == null || group.Targets.Count == 0) return;
            if (path != null && !(row.RedundantParents ?? new RedundantParent[0]).Any(p => Comparer.Equals(p.Path, path))) return;
            if (path != null) {
                if (row.DirectoryNames == null) return;
                foreach (string name in row.DirectoryNames) Add(group, Folder(row), name);
            }
            group.Row = row; group.Redundant = path;
        }
        public int Count { get { var request = Snapshot(); return request.Targets.Select(t => t.Path).Concat(request.ParentCleanups.SelectMany(c => c.Directories)).Distinct(Comparer).Count(); } }
        public DeleteRequest Snapshot() {
            var targets = new Dictionary<string, DeleteTarget>(Comparer);
            var roots = new HashSet<string>(Comparer);
            var cleanup = new List<ParentCleanup>();
            foreach (var pair in groups) {
                var group = pair.Value;
                if (group.Targets.Count == 0) continue;
                roots.Add(pair.Key);
                foreach (var target in group.Targets.Values) targets[target.Path] = target;
                if (group.Redundant != null) {
                    string[] chain = group.Row.RedundantParents.TakeWhile(p => !Comparer.Equals(p.Path, group.Redundant)).Select(p => p.Path).Concat(new[] { group.Redundant }).ToArray();
                    cleanup.Add(new ParentCleanup { Boundary = group.Row.ScanBoundary, Directories = chain });
                    roots.Add(group.Row.ScanBoundary);
                }
            }
            return new DeleteRequest { Targets = targets.Values.OrderBy(t => t.Path, Comparer).ToList(), AllowedRoots = roots.ToArray(), ParentCleanups = cleanup };
        }
        public void Reconcile(IEnumerable<PackageRow> currentRows) {
            rows.Clear();
            foreach (var row in currentRows.Where(r => !r.Missing)) rows[Identity(row.FullPath)] = row;
            foreach (string folder in groups.Keys.ToArray()) {
                var owners = rows.Values.Where(r => Comparer.Equals(Folder(r), folder)).ToArray();
                if (owners.Length == 0) { groups.Remove(folder); continue; }
                var group = groups[folder];
                group.Row = owners.FirstOrDefault(r => Comparer.Equals(r.FullPath, group.Row.FullPath)) ?? owners[0];
                var readable = owners.FirstOrDefault(r => r.DirectoryNames != null);
                if (readable != null) {
                    var names = readable.DirectoryNames.ToDictionary(Bare, n => n.EndsWith("\\") || n.EndsWith("/"), Comparer);
                    foreach (string name in group.Targets.Keys.ToArray()) {
                        bool directory;
                        if (!names.TryGetValue(name, out directory) || group.Targets[name].IsDirectory != directory) group.Targets.Remove(name);
                    }
                }
                if (group.Targets.Count == 0) { groups.Remove(folder); continue; }
                if (group.Redundant != null && (!group.Row.RedundantParents.Any(p => Comparer.Equals(p.Path, group.Redundant))
                    || group.Row.DirectoryNames == null || group.Row.DirectoryNames.Any(n => !group.Targets.ContainsKey(Bare(n))))) group.Redundant = null;
            }
        }
    }
}
