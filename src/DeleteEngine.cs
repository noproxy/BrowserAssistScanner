using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BrowserAssistScanner
{
    public sealed class DeleteReport
    {
        public int Recycled;
        public int Missing;
        public bool Cancelled;
        public List<string> Issues = new List<string>();
        public bool PostOrder;
    }

    public interface IRecycler
    {
        void Recycle(IList<DeleteTarget> targets, DeleteScope scope, bool emptyDirectoriesOnly,
            CancellationToken cancel, Action<string> progress, DeleteReport report);
    }

    public sealed class DeleteScope
    {
        private readonly string[] roots;
        public DeleteScope(IEnumerable<string> allowedRoots)
        {
            roots = allowedRoots.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (roots.Length == 0) throw new InvalidOperationException("没有有效的删除范围。");
        }
        public static string Normalize(string path)
        {
            if (String.IsNullOrWhiteSpace(path) ||
                !(path.StartsWith(@"\\") || (path.Length >= 3 && Char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))) ||
                path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\"))
                throw new InvalidDataException("删除路径必须是明确的完整路径。");
            if (path.IndexOf(':', 2) >= 0) throw new InvalidDataException("不支持备用数据流路径。");
            string full = System.IO.Path.GetFullPath(path);
            string root = System.IO.Path.GetPathRoot(full);
            return String.Equals(full, root, StringComparison.OrdinalIgnoreCase) ? root : full.TrimEnd('\\', '/');
        }
        public static bool Descendant(string child, string parent)
        {
            return child.StartsWith(parent.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase);
        }
        public void Validate(string path, bool? expectedDirectory)
        {
            path = Normalize(path);
            if (!roots.Any(r => Descendant(path, r))) throw new InvalidOperationException("删除目标不在已勾选项目的所在目录范围内：" + path);
            string volume = System.IO.Path.GetPathRoot(path);
            if (String.Equals(path, volume, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("不能删除盘符根目录。");
            string part = path;
            while (!String.IsNullOrEmpty(part) && !String.Equals(part, volume, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(part);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("保留目录链接或重解析点：" + part);
                    if (String.Equals(part, path, StringComparison.OrdinalIgnoreCase) && expectedDirectory.HasValue &&
                        (((attributes & FileAttributes.Directory) != 0) != expectedDirectory.Value))
                        throw new InvalidOperationException("目标类型已改变，请刷新后重新勾选：" + path);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                part = System.IO.Path.GetDirectoryName(part);
            }
        }
    }

    public static class RunningApplications
    {
        public static bool IsTargetName(string name)
        {
            name = System.IO.Path.GetFileNameWithoutExtension(name);
            return String.Equals(name, "VaM", StringComparison.OrdinalIgnoreCase) || String.Equals(name, "vambox", StringComparison.OrdinalIgnoreCase);
        }
        public static bool AnyRunning()
        {
            foreach (string name in new[] { "VaM", "vambox" })
            {
                Process[] processes = Process.GetProcessesByName(name);
                bool found = processes.Length > 0;
                foreach (Process process in processes) process.Dispose();
                if (found) return true;
            }
            return false;
        }
    }

    public static class DeleteEngine
    {
        public static List<DeleteTarget> TopLevelTargets(IEnumerable<DeleteTarget> targets)
        {
            var unique = new List<DeleteTarget>();
            foreach (var target in targets.OrderBy(t => t.Path.Length))
            {
                string path = DeleteScope.Normalize(target.Path);
                if (unique.Any(t => String.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase) || (t.IsDirectory && DeleteScope.Descendant(path, t.Path)))) continue;
                unique.Add(new DeleteTarget { Path = path, IsDirectory = target.IsDirectory });
            }
            return unique;
        }
        public static DeleteReport Delete(DeleteRequest request, bool postOrder, IRecycler recycler, CancellationToken cancel, Action<string> progress)
        {
            var report = new DeleteReport { PostOrder = postOrder };
            var scope = new DeleteScope(request.AllowedRoots);
            var targets = TopLevelTargets(request.Targets);
            // Validate all selections before deduplication hides overlapping targets.
            foreach (var target in request.Targets) scope.Validate(target.Path, target.IsDirectory);
            foreach (var cleanup in request.ParentCleanups) {
                string boundary = DeleteScope.Normalize(cleanup.Boundary), previous = null;
                if (cleanup.Directories == null || cleanup.Directories.Length == 0) throw new InvalidDataException("冗余父目录链为空。");
                foreach (string directory in cleanup.Directories) {
                    string path = DeleteScope.Normalize(directory);
                    if (!DeleteScope.Descendant(path, boundary) || (previous != null && !String.Equals(System.IO.Path.GetDirectoryName(previous), path, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("冗余父目录链超出扫描目录或不连续：" + path);
                    scope.Validate(path, true); previous = path;
                }
            }
            if (!postOrder)
            {
                recycler.Recycle(targets, scope, false, cancel, progress, report);
                CleanupParents(request, scope, recycler, cancel, progress, report);
                return report;
            }
            var pending = new Stack<Tuple<DeleteTarget, bool>>();
            foreach (var target in targets.AsEnumerable().Reverse()) pending.Push(Tuple.Create(target, false));
            while (pending.Count > 0)
            {
                if (cancel.IsCancellationRequested) { report.Cancelled = true; break; }
                var entry = pending.Pop();
                var target = entry.Item1;
                try
                {
                    scope.Validate(target.Path, target.IsDirectory);
                    try { File.GetAttributes(target.Path); }
                    catch (FileNotFoundException) { report.Missing++; continue; }
                    catch (DirectoryNotFoundException) { report.Missing++; continue; }
                    if (target.IsDirectory && !entry.Item2)
                    {
                        // Snapshot immediate children once. Do not add newly created files later.
                        var children = Directory.GetFileSystemEntries(target.Path).Select(p => new DeleteTarget {
                            Path = p, IsDirectory = (File.GetAttributes(p) & FileAttributes.Directory) != 0
                        }).ToArray();
                        pending.Push(Tuple.Create(target, true));
                        foreach (var child in children.Reverse()) pending.Push(Tuple.Create(child, false));
                        continue;
                    }
                    if (target.IsDirectory && Directory.EnumerateFileSystemEntries(target.Path).Any())
                    {
                        report.Issues.Add(target.Path + "：目录仍非空，已保留（被占用、跳过或新增的项目）。");
                        continue;
                    }
                    recycler.Recycle(new[] { target }, scope, true, cancel, progress, report);
                    if (report.Cancelled) break;
                }
                catch (Exception error) { report.Issues.Add(target.Path + "：" + error.Message); }
            }
            CleanupParents(request, scope, recycler, cancel, progress, report);
            return report;
        }

        private static void CleanupParents(DeleteRequest request, DeleteScope scope, IRecycler recycler, CancellationToken cancel, Action<string> progress, DeleteReport report)
        {
            var directories = request.ParentCleanups.SelectMany(c => c.Directories).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(p => p.Length);
            foreach (string path in directories) {
                if (cancel.IsCancellationRequested || report.Cancelled) { report.Cancelled = true; break; }
                try {
                    scope.Validate(path, true);
                    try { File.GetAttributes(path); }
                    catch (FileNotFoundException) { continue; }
                    catch (DirectoryNotFoundException) { continue; }
                    if (Directory.EnumerateFileSystemEntries(path).Any()) {
                        report.Issues.Add(path + "：冗余父目录仍非空，已保留（删除失败或出现了新项目）。"); continue;
                    }
                    recycler.Recycle(new[] { new DeleteTarget { Path = path, IsDirectory = true } }, scope, true, cancel, progress, report);
                } catch (Exception error) { report.Issues.Add(path + "：" + error.Message); }
            }
        }

        public static Task<DeleteReport> DeleteAsync(DeleteRequest request, bool postOrder, CancellationToken cancel, Action<string> progress)
        {
            var completion = new TaskCompletionSource<DeleteReport>();
            var thread = new Thread(delegate() {
                try { completion.SetResult(Delete(request, postOrder, new ShellRecycler(), cancel, progress)); }
                catch (Exception error) { completion.SetException(error); }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task;
        }
    }
}
