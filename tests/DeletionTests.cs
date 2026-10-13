using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using BrowserAssistScanner;

public static class DeletionTests
{
    private sealed class FakeRecycler : IRecycler
    {
        public readonly List<string> Calls = new List<string>();
        public readonly List<int> BatchSizes = new List<int>();
        public bool Perform;
        public string FailPath, AddFileAfter;
        public void Recycle(IList<DeleteTarget> targets, DeleteScope scope, bool emptyOnly, CancellationToken cancel, Action<string> progress, DeleteReport report)
        {
            BatchSizes.Add(targets.Count);
            foreach (var item in targets) {
                scope.Validate(item.Path, item.IsDirectory);
                Calls.Add(item.Path);
                if (item.Path == FailPath) { report.Issues.Add("simulated locked item: " + item.Path); continue; }
                if (Perform) {
                    // The caller has confined these fixtures to its unique test directory.
                    if (item.IsDirectory) Directory.Delete(item.Path, false); else File.Delete(item.Path);
                    report.Recycled++;
                }
                if (item.Path == AddFileAfter) File.WriteAllText(Path.Combine(Path.GetDirectoryName(item.Path), "new.txt"), "created during operation");
            }
        }
    }
    private static DeleteRequest Request(string root, params DeleteTarget[] items) { return new DeleteRequest { AllowedRoots = new[] { root }, Targets = items.ToList() }; }
    private static DeleteTarget Target(string path) { return new DeleteTarget { Path = path, IsDirectory = Directory.Exists(path) }; }
    private static string FileAt(string folder, string name) { Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); File.WriteAllText(path, "temporary test sample"); return path; }
    private static bool InRecycleBin(string original)
    {
        string folder = Path.Combine(Path.GetPathRoot(original), "$Recycle.Bin", WindowsIdentity.GetCurrent().User.Value);
        foreach (string metadata in Directory.GetFiles(folder, "$I*")) {
            try {
                byte[] bytes = File.ReadAllBytes(metadata);
                if (bytes.Length < 28) continue;
                int offset = BitConverter.ToInt64(bytes, 0) == 2 ? 28 : 24;
                string path = Encoding.Unicode.GetString(bytes, offset, bytes.Length - offset).TrimEnd('\0');
                if (String.Equals(path, original, StringComparison.OrdinalIgnoreCase)) return true;
            } catch (IOException) { }
        }
        return false;
    }
    public static void Run(string fixture, Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetFullPath(fixture), "Deletion-" + Guid.NewGuid().ToString("N"));
        if (!DeleteScope.Descendant(root, Path.GetFullPath(fixture))) throw new Exception("invalid fixture scope");
        Directory.CreateDirectory(root);
        string main = FileAt(root, "sample.var"), peer = FileAt(root, "peer.txt");
        string folder = Path.Combine(root, "folder"); Directory.CreateDirectory(folder);
        var row = new PackageRow { Name = "sample.var", FullPath = main, SiblingNames = new[] { "peer.txt", "folder\\" } };
        var selection = new SelectionState();
        selection.SetSibling(row, "peer.txt", true);
        check(selection.Count == 0, "siblings cannot be checked without parent");
        selection.SetParent(row, true);
        check(selection.Count == 1 && !selection.IsSiblingChecked(row, "peer.txt"), "parent starts with no siblings selected");
        selection.SetSibling(row, "peer.txt", true); selection.SetSibling(row, "folder\\", true);
        check(selection.Count == 3 && selection.Snapshot().Targets.Single(t => t.Path == folder).IsDirectory, "file and folder checkboxes become deletion targets");
        var second = new PackageRow { Name = "peer.txt", FullPath = peer, SiblingNames = new[] { "sample.var", "folder\\" } };
        selection.SetParent(second, true); selection.SetSibling(second, "folder\\", true);
        check(selection.Count == 3, "same physical target selected via multiple rows deduplicated");
        selection.SetParent(row, false); selection.SetParent(row, true);
        check(selection.IsSiblingChecked(row, "folder\\"), "cancelling one of several same-directory owners preserves shared companions");
        selection.Reconcile(new[] { new PackageRow { FullPath = main, SiblingNames = new[] { "folder\\" } } });
        check(selection.Count == 2, "refresh reconciles shared directory state and removes vanished names");
        selection.Clear(); check(selection.Count == 0, "clear all empties every selection");
        selection.SetParent(row, true); selection.SetSibling(row, "folder\\", true);
        var changedType = new PackageRow { FullPath = main, SiblingNames = new[] { "folder", "peer.txt" } };
        selection.Reconcile(new[] { changedType });
        check(selection.Count == 1 && !selection.IsSiblingChecked(changedType, "folder"), "refresh clears shared selection when an item changes between folder and file");
        selection.Clear();
        check(RunningApplications.IsTargetName(@"X:\another\VAM.EXE") && RunningApplications.IsTargetName("vambox.exe") && !RunningApplications.IsTargetName("VaMUpdater.exe"), "process matching uses executable names only");
        var scope = new DeleteScope(new[] { root });
        var expected = Target(main);
        check(ShellRecycler.CheckDelete(0, main, expected, scope, true) < 0, "permanent fallback vetoed");
        check(ShellRecycler.CheckDelete(ShellRecycler.RecycleFlag, peer, expected, scope, true) < 0, "unexpected shell target vetoed");
        check(ShellRecycler.CheckDelete(ShellRecycler.RecycleFlag, main, expected, scope, true) == 0, "recycle-only expected file accepted");
        bool rejected = false;
        try { scope.Validate(root, true); } catch { rejected = true; }
        check(rejected, "selection base itself is outside scope");
        rejected = false; try { DeleteScope.Normalize("D:relative.var"); } catch { rejected = true; }
        check(rejected, "drive-relative delete target rejected");
        string nested = FileAt(Path.Combine(folder, "nested"), "child.txt");
        var fake = new FakeRecycler();
        var fast = DeleteEngine.Delete(Request(root, Target(folder), Target(nested), Target(main)), false, fake, CancellationToken.None, delegate { });
        check(fake.BatchSizes.SequenceEqual(new[] { 2 }) && !fake.Calls.Contains(nested), "fast mode sends only top-level selections to system without traversing");
        fake = new FakeRecycler { Perform = true };
        var recursive = DeleteEngine.Delete(Request(root, Target(folder)), true, fake, CancellationToken.None, delegate { });
        check(recursive.Issues.Count == 0 && fake.Calls.SequenceEqual(new[] { nested, Path.GetDirectoryName(nested), folder }), "recursive mode postorder: file, empty child folder, empty parent");
        string blockedRoot = Path.Combine(root, "blocked"), blocked = FileAt(blockedRoot, "locked.txt"), good = FileAt(blockedRoot, "good.txt");
        fake = new FakeRecycler { Perform = true, FailPath = blocked };
        recursive = DeleteEngine.Delete(Request(root, Target(blockedRoot)), true, fake, CancellationToken.None, delegate { });
        check(File.Exists(blocked) && !File.Exists(good) && Directory.Exists(blockedRoot) && recursive.Issues.Count == 2, "failed child retained, peers processed, nonempty parent retained");
        string changing = Path.Combine(root, "changing"), initial = FileAt(changing, "original.txt");
        fake = new FakeRecycler { Perform = true, AddFileAfter = initial };
        recursive = DeleteEngine.Delete(Request(root, Target(changing)), true, fake, CancellationToken.None, delegate { });
        check(Directory.Exists(changing) && File.Exists(Path.Combine(changing, "new.txt")) && recursive.Issues.Count == 1, "newly created child retained rather than swept recursively");
        var cts = new CancellationTokenSource(); cts.Cancel(); fake = new FakeRecycler();
        recursive = DeleteEngine.Delete(Request(root, Target(main)), true, fake, cts.Token, delegate { });
        check(recursive.Cancelled && fake.Calls.Count == 0, "cancellation stops before first deletion");

        string scanRoot = Path.Combine(root, "parent-fixture"), leaf = Path.Combine(scanRoot, "2026", "20261001", "秋分");
        string parentVar = FileAt(leaf, "sample.var"), parentNote = FileAt(leaf, "更新说明.txt");
        var candidates = RedundantDirectories.Find(parentVar, scanRoot, CancellationToken.None);
        check(candidates.Length == 3 && candidates[0].Label == "秋分\\" && candidates[2].Label == "2026\\20261001\\秋分\\", "redundant chain labels from leaf to highest eligible parent");
        check(candidates.All(c => c.Path != scanRoot), "scan boundary never a redundant candidate");
        string neighbor = FileAt(Path.GetDirectoryName(leaf), "邻居.txt");
        check(RedundantDirectories.Find(parentVar, scanRoot, CancellationToken.None).Length == 1, "neighbor file blocks higher redundant levels");
        check(RedundantDirectories.Find(main, scanRoot, CancellationToken.None).Length == 0, "outside scan boundary has no cleanup candidates");
        check(RedundantDirectories.Find(FileAt(scanRoot, "direct.var"), scanRoot, CancellationToken.None).Length == 0, "file directly under scan root cannot select root");
        var parentRow = new PackageRow { FullPath = parentVar, ScanBoundary = scanRoot, SiblingNames = new[] { "更新说明.txt" }, RedundantParents = candidates };
        selection.Clear(); selection.SetParent(parentRow, true); selection.SetRedundant(parentRow, candidates[2].Path);
        check(selection.IsSiblingChecked(parentRow, "更新说明.txt") && selection.Count == 5, "radio automatically includes all siblings and intermediate ancestors");
        selection.SetSibling(parentRow, "更新说明.txt", false);
        check(selection.SelectedRedundant(parentRow) == null && selection.Count == 1, "unchecking any peer clears ancestor radio and cleanup schedule");
        selection.SetRedundant(parentRow, candidates[0].Path);
        check(selection.Snapshot().ParentCleanups[0].Directories.SequenceEqual(new[] { leaf }), "lower radio excludes upper ancestors");
        selection.SetParent(parentRow, false); selection.SetParent(parentRow, true);
        check(selection.SelectedRedundant(parentRow) == null && selection.Count == 1, "unchecking VAR clears parent radio and all sibling checks");
        selection.SetRedundant(parentRow, candidates[2].Path);
        selection.Reconcile(new[] { new PackageRow { FullPath = parentVar, ScanBoundary = scanRoot, SiblingNames = new[] { "更新说明.txt", "new.txt" }, RedundantParents = candidates } });
        check(selection.SelectedRedundant(parentRow) == null && !selection.IsSiblingChecked(parentRow, "new.txt"), "refresh with new unselected peer cancels ancestor choice");

        // Revalidate each ancestor at deletion time; the previously discovered higher chain now contains a neighbor.
        selection.Clear(); selection.SetParent(parentRow, true); selection.SetRedundant(parentRow, candidates[2].Path);
        recursive = DeleteEngine.Delete(selection.Snapshot(), false, new ShellRecycler(), CancellationToken.None, delegate { });
        check(recursive.Recycled == 3 && !Directory.Exists(leaf) && File.Exists(neighbor) && Directory.Exists(Path.Combine(scanRoot, "2026")), "recycle leaf only; newly added neighbor keeps higher ancestors");
        check(InRecycleBin(parentVar) && InRecycleBin(parentNote) && InRecycleBin(leaf), "empty-parent cleanup verified in actual recycle bin");

        string failedLeaf = Path.Combine(scanRoot, "failed-parent"), failedVar = FileAt(failedLeaf, "sample.var"), failedPeer = FileAt(failedLeaf, "locked.txt");
        var failedRow = new PackageRow { FullPath = failedVar, ScanBoundary = scanRoot, SiblingNames = new[] { "locked.txt" }, RedundantParents = RedundantDirectories.Find(failedVar, scanRoot, CancellationToken.None) };
        selection.Clear(); selection.SetParent(failedRow, true); selection.SetRedundant(failedRow, failedLeaf);
        using (var held = new FileStream(failedPeer, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            recursive = DeleteEngine.Delete(selection.Snapshot(), true, new ShellRecycler(), CancellationToken.None, delegate { });
            check(!File.Exists(failedVar) && File.Exists(failedPeer) && Directory.Exists(failedLeaf) && recursive.Issues.Count > 0, "locked sibling prevents redundant parent recycling");
        }
        var boundaryAttempt = Request(scanRoot);
        boundaryAttempt.ParentCleanups.Add(new ParentCleanup { Boundary = scanRoot, Directories = new[] { scanRoot } });
        rejected = false;
        try { DeleteEngine.Delete(boundaryAttempt, false, new FakeRecycler(), CancellationToken.None, delegate { }); } catch (InvalidDataException) { rejected = true; }
        check(rejected && Directory.Exists(scanRoot), "engine rejects forged inclusive scan boundary before deletion");

        // Real Windows recycle verification, confined to uniquely named fixtures.
        string fastFolder = Path.Combine(root, "真实顶层 文件夹"), fastChild = FileAt(fastFolder, "child.txt"), fastFile = FileAt(root, "真实顶层.txt");
        fast = DeleteEngine.Delete(Request(root, Target(fastFolder), Target(fastFile)), false, new ShellRecycler(), CancellationToken.None, delegate { });
        check(fast.Issues.Count == 0 && fast.Recycled == 2 && !Directory.Exists(fastFolder) && !File.Exists(fastFile), "native top-level recycle succeeds: " + String.Join(";", fast.Issues));
        check(InRecycleBin(fastFolder) && InRecycleBin(fastFile), "native files and intact folder verified in actual recycle bin");
        string slowFolder = Path.Combine(root, "真实逐项 文件夹"), slowChild = FileAt(Path.Combine(slowFolder, "nested"), "child.txt");
        recursive = DeleteEngine.Delete(Request(root, Target(slowFolder)), true, new ShellRecycler(), CancellationToken.None, delegate { });
        check(recursive.Issues.Count == 0 && recursive.Recycled == 3 && !Directory.Exists(slowFolder), "native recursive recycling succeeds: " + String.Join(";", recursive.Issues));
        check(InRecycleBin(slowChild) && InRecycleBin(Path.GetDirectoryName(slowChild)) && InRecycleBin(slowFolder), "postorder children and empty directories each verified in recycle bin");
        string lockFolder = Path.Combine(root, "真实占用"), lockFile = FileAt(lockFolder, "locked.txt");
        using (var held = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            recursive = DeleteEngine.Delete(Request(root, Target(lockFolder)), true, new ShellRecycler(), CancellationToken.None, delegate { });
            check(recursive.Recycled == 0 && recursive.Issues.Count > 0 && File.Exists(lockFile) && Directory.Exists(lockFolder), "locked file and its parent remain, no permanent fallback");
        }
    }
}
