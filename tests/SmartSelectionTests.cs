using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using BrowserAssistScanner;

public static class SmartSelectionTests
{
    private static string Var(string folder, string uid, object dependencies) {
        Directory.CreateDirectory(folder); string path = Path.Combine(folder, uid + ".var");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) {
            zip.CreateEntry("Saves/scene/main.json");
            using (var writer = new StreamWriter(zip.CreateEntry("meta.json").Open()))
                writer.Write(Scanner.Json().Serialize(new { dependencies = dependencies ?? new Dictionary<string, object>() }));
        }
        return path;
    }
    private static object Resource(string uid, string tag, bool fav, string resource, bool hidden = false) {
        string[] parts = uid.Split('.');
        return new { creatorName = parts[0], packageName = parts[1], resourceFullFileName = resource,
            baFavourite = fav ? "Active" : "Inactive", baHidden = hidden ? "Active" : "Inactive", Tags = new[] { new { tagName = tag, tagCategory = "User" } } };
    }
    public static void Run(string fixtureRoot, Action<bool, string> check) {
        string root = Path.Combine(fixtureRoot, "Smart"), packages = Path.Combine(root, "packages"), vam = Path.Combine(root, "VAM");
        string data = Path.Combine(vam, @"Saves\PluginData\JayJayWon\BrowserAssist\VARResourcesUserData"); Directory.CreateDirectory(data);
        string autumn = Var(Path.Combine(packages, "2026", "20261001", "秋分"), "Fixture.Autumn.2", null);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(autumn), "更新说明.txt"), "notes");
        string butterfly = Var(Path.Combine(packages, "2026", "20261001", "作者", "蝴蝶忍"), "Fixture.Butterfly.1", null);
        string depFolder = Path.Combine(Path.GetDirectoryName(butterfly), "dependencies");
        string favourite = Var(Path.Combine(depFolder, "nested"), "Fixture.Favourite.1", null);
        string stale = Var(depFolder, "Fixture.Stale.1", null);
        string native = Var(depFolder, "Fixture.Native.1", null);
        string mark = Path.Combine(vam, "AddonPackagesFilePrefs", "Fixture.Native.1", "Saves", "scene", "main.json.fav");
        Directory.CreateDirectory(Path.GetDirectoryName(mark)); File.WriteAllText(mark, "");
        string mimi = Var(Path.Combine(packages, "MimiBera"), "Fixture.Mimi.1", null);
        Var(Path.GetDirectoryName(mimi), "Fixture.Bera.1", null); Var(Path.Combine(Path.GetDirectoryName(mimi), "DP"), "Fixture.Shared.1", null);
        string unrelated = Var(Path.GetDirectoryName(mimi), "Fixture.UnrelatedFavourite.1", null);
        string thorn = Var(Path.Combine(packages, "thorn"), "Fixture.Thorn.1", new Dictionary<string, object> { { "Fixture.Hair.latest", new { dependencies = new Dictionary<string, object>() } } });
        string hair = Var(Path.GetDirectoryName(thorn), "Fixture.Hair.2", null);
        Var(Path.GetDirectoryName(thorn), "Fixture.Hair.1", null);
        string ordinary = Var(Path.Combine(packages, "Unknown"), "Fixture.Unknown.1", null);
        Var(Path.Combine(Path.GetDirectoryName(ordinary), "另一个人物"), "Fixture.OtherCharacter.1", null);
        string noHigherFav = Var(Path.Combine(packages, "OldFavourite"), "Fixture.Latest.1", new Dictionary<string, object> { { "Fixture.Newer.latest", new { dependencies = new Dictionary<string, object>() } } });
        string oldFav = Var(Path.Combine(packages, "pool"), "Fixture.Newer.1", null);
        Var(Path.GetDirectoryName(oldFav), "Fixture.Newer.2", null);
        string nestedReference = Var(Path.Combine(packages, "NestedReference"), "Fixture.Nested.1", new Dictionary<string, object> {
            { "Fixture.NotInstalled.1", new { dependencies = new Dictionary<string, object> { { "Fixture.Newer.1", new { dependencies = new Dictionary<string, object>() } } } } }
        });
        Var(Path.Combine(packages, "NestedReference-other"), "Fixture.NeighbourFavourite.1", null);
        string ownFavourite = Var(Path.Combine(packages, "OwnFavourite"), "Fixture.OwnFavourite.1", null);
        string oldMark = Path.Combine(vam, "AddonPackagesFilePrefs", "Fixture.Newer.1", "main.json.fav");
        Directory.CreateDirectory(Path.GetDirectoryName(oldMark)); File.WriteAllText(oldMark, "");
        var resources = new[] {
            Resource("Fixture.Autumn.2", "del", false, @"Saves\scene\main.json"),
            Resource("Fixture.Butterfly.1", "del", false, @"Saves\scene\main.json"),
            Resource("Fixture.Favourite.1", "best", true, @"Saves\scene\main.json"),
            Resource("Fixture.Stale.1", "best", true, @"Saves\scene\not-present.json"),
            Resource("Fixture.Native.1", "del", false, @"Saves\scene\main.json"),
            Resource("Fixture.Mimi.1", "del", false, @"Saves\scene\main.json"),
            Resource("Fixture.UnrelatedFavourite.1", "best", true, @"Saves\scene\main.json", true),
            Resource("Fixture.Thorn.1", "del", false, @"Saves\scene\main.json"),
            Resource("Fixture.Hair.2", "best", true, @"Saves\scene\main.json"),
            Resource("Fixture.Unknown.1", "del", false, @"Saves\scene\main.json"),
            Resource("Fixture.Latest.1", "del", false, @"Saves\scene\main.json"),
            Resource("Fixture.Nested.1", "del", false, @"Saves\scene\main.json"),
            Resource("Fixture.NeighbourFavourite.1", "best", true, @"Saves\scene\main.json"),
            Resource("Fixture.OwnFavourite.1", "del", true, @"Saves\scene\main.json"),
            Resource("Fixture.Newer.1", "best", false, @"Saves\scene\main.json")
        };
        File.WriteAllText(Path.Combine(data, "smart.userData"), Scanner.Json().Serialize(new { resources = resources }), Encoding.UTF8);
        var options = new ScanOptions { VamPath = vam, PackagesPath = packages };
        var result = Scanner.Scan(options, CancellationToken.None, delegate {});
        var state = new SelectionState();
        var a = result.Rows.Single(r => r.FullPath == autumn);
        state.SetParent(a, true);
        check(a.AutoSelectSiblings && state.IsSiblingChecked(a, "更新说明.txt") && state.SelectedRedundant(a) == Path.GetDirectoryName(autumn) && state.Count == 3, "single main VAR auto checks note and only leaf directory");
        state.SetSibling(a, "更新说明.txt", false); state.SetParent(a, true);
        check(state.Count == 1 && state.SelectedRedundant(a) == null, "repeated true does not overwrite manual choice");
        state.Reconcile(result.Rows);
        check(state.Count == 1, "refresh never reapplies smart selection");
        state.SetParent(a, false); check(state.Count == 0, "uncheck removes automatic selection"); state.SetParent(a, true);
        check(state.Count == 3, "new check reapplies current smart rule");
        state.Clear(); var b = result.Rows.Single(r => r.FullPath == butterfly); state.SetParent(b, true);
        check(b.AutoSelectSiblings && state.IsSiblingChecked(b, "dependencies\\") && b.RedundantParents.Length == 2 && state.SelectedRedundant(b) == b.RedundantParents.Last().Path && state.Count == 4, "single main VAR selects private dependency tree and final ancestor candidate including intermediate directory");
        check(b.Marks == "有收藏的子文件" && b.FavouriteChildren.Contains(favourite) && b.FavouriteChildren.Contains(native) && !b.FavouriteChildren.Contains(stale), "favourite warning includes non-del BA and native flags, excludes stale BA resource");
        check(b.Details.Contains(favourite) && b.Details.Contains("人工移出"), "warning explains action and shows full favourite paths");
        var d = result.Rows.Single(r => r.FullPath == native); state.Clear(); state.SetParent(d, true);
        check(d.IsDependency && d.Marks.Contains("依赖") && state.IsParentChecked(d), "dependency ancestor label is informational and does not block checking");
        check(d.Marks == "依赖；有收藏的子文件", "both warning labels coexist on a dependency with favourite descendants");
        check(!d.FavouriteChildren.Contains(native), "current favourite VAR itself is excluded from child warning");
        var m = result.Rows.Single(r => r.FullPath == mimi); state.Clear(); state.SetParent(m, true);
        check(!m.AutoSelectSiblings && state.Count == 1 && m.SiblingNames.All(n => !state.IsSiblingChecked(m, n)), "Mimi with untagged Bera and shared DP remains manual");
        check(m.Marks == "" && m.FavouriteChildren.Length == 0, "flat favourite sibling is excluded from favourite-child warning");
        check(m.SiblingStates["Fixture.UnrelatedFavourite.1.var"].Favourite == "是" && m.SiblingStates["Fixture.UnrelatedFavourite.1.var"].Hidden == "是", "untagged-for-del sibling exposes BA favourite and hidden states");
        check(m.SiblingStates["Fixture.Bera.1.var"].Favourite == "否" && m.SiblingStates["Fixture.Bera.1.var"].Hidden == "否", "readable unmarked sibling exposes explicit no states");
        check(!m.SiblingStates.ContainsKey("DP\\"), "folder has no invented aggregate VAM marker states");
        var t = result.Rows.Single(r => r.FullPath == thorn); state.Clear(); state.SetParent(t, true);
        check(!t.AutoSelectSiblings && state.Count == 1 && t.FavouriteChildren.Length == 0 && t.Marks == "" && t.SiblingStates[Path.GetFileName(hair)].Favourite == "是", "flat hair favourite shown in sibling column without top-row warning");
        var u = result.Rows.Single(r => r.FullPath == ordinary); state.Clear(); state.SetParent(u, true);
        check(!u.AutoSelectSiblings && state.Count == 1, "unknown child directory with VAR is not automatically deleted");
        var latestRow = result.Rows.Single(r => r.FullPath == noHigherFav);
        check(latestRow.FavouriteChildren.Length == 0, "external latest references do not count as physical children");
        var nestedRow = result.Rows.Single(r => r.FullPath == nestedReference);
        check(nestedRow.FavouriteChildren.Length == 0 && nestedRow.Marks == "" && !nestedRow.Details.Contains(oldFav), "external fixed references and similarly prefixed neighbouring directory are excluded");
        var ownRow = result.Rows.Single(r => r.FullPath == ownFavourite);
        check(ownRow.Favourite == "是" && ownRow.FavouriteChildren.Length == 0 && ownRow.Marks == "", "only self favourite does not produce favourite-child warning");
        check(new[] { "依赖", "DP", "dependencies", "Dependency", "Depend", "Dependent", "deps", "Tina_Dependencies", "KITTY依赖", "Dependencies New" }.All(PackageAnalysis.IsDependencyName), "observed dependency names recognized case insensitively");
        check(!PackageAnalysis.IsDependencyName("independent") && !PackageAnalysis.IsDependencyName("DPgirl"), "ordinary folder names avoid English substring false positives");
        var old = state.Snapshot(); state.Clear(); check(state.Count == 0 && state.Snapshot().Targets.Count == 0, "clear all removes smart state");
        check(result.Warnings.Count == 0, "valid smart fixture has no scan errors");
        options.PackagesPath = depFolder;
        check(Scanner.Scan(options, CancellationToken.None, delegate {}).Rows.Single(r => r.FullPath == native).IsDependency, "dependency label survives scanning dependency directory as root");
    }
}
