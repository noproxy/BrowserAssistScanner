using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using BrowserAssistScanner;

public static class ScannerTests
{
    private static int assertions;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAILED: " + label);
        assertions++;
    }
    private static Dictionary<string, object> Resource(string creator, string package, string path, string tag, string favourite, string hidden)
    {
        return new Dictionary<string, object> {
            { "creatorName", creator }, { "packageName", package }, { "resourceFullFileName", path },
            { "Tags", new object[] { new Dictionary<string, object> { { "tagName", tag }, { "tagCategory", "User" } } } },
            { "baFavourite", favourite }, { "baHidden", hidden }
        };
    }
    private static void Zip(string path, params string[] resources)
    {
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var resource in resources) zip.CreateEntry(resource);
    }
    private static void Marker(string root, string uid, string resource, string ext)
    {
        string path = Path.Combine(root, "AddonPackagesFilePrefs", uid, resource + ext);
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, new byte[] { 0 });
    }
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            string testRoot = args[0];
            string vam = Path.Combine(testRoot, "VAM"), packages = Path.Combine(testRoot, "包目录 中文 空格");
            string data = Path.Combine(vam, @"Saves\PluginData\JayJayWon\BrowserAssist\VARResourcesUserData");
            Directory.CreateDirectory(data); Directory.CreateDirectory(packages);
            var records = new object[] {
                Resource("作者", "人物", @"Saves\scene\场景.json", "del", "Active", null),
                Resource("作者", "人物", @"Saves\scene\旧资源.json", "other", null, "Active"),
                Resource("Native", "Marked", @"Saves\scene\native.json", "del", null, null),
                Resource("Absent", "Gone", @"Saves\scene\missing.json", "del", null, "Active"),
                Resource("Other", "Deluxe", @"Saves\scene\other.json", "deluxe", null, null),
                Resource("Broken", "Archive", @"Saves\scene\broken.json", "del", null, null)
            };
            File.WriteAllText(Path.Combine(data, "one.userData"), Scanner.Json().Serialize(new { resources = records }), new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(data, "bad.userData"), "{invalid json");
            Zip(Path.Combine(packages, "作者.人物.1.var"), "Saves/scene/场景.json");
            Zip(Path.Combine(packages, "作者.人物.2.var"), "Saves/scene/场景.json");
            Zip(Path.Combine(packages, "Native.Marked.1.var"), "Saves/scene/native.json");
            Zip(Path.Combine(packages, "Other.Deluxe.1.var"), "Saves/scene/other.json");
            File.WriteAllText(Path.Combine(packages, "Broken.Archive.1.var"), "not a zip");
            Marker(vam, "Native.Marked.1", @"Saves\scene\native.json", ".hide");
            Marker(vam, "Native.Marked.1", @"Saves\scene\native.json", ".fav");
            var options = new ScanOptions { VamPath = vam, PackagesPath = packages, Tag = "del" };
            var result = Scanner.Scan(options, CancellationToken.None, delegate { });
            Check(result.Groups == 4 && result.FileCount == 4 && result.MissingGroups == 1, "group/version/missing counts");
            Check(result.Rows.Count == 5, "all matching versions and missing record included");
            Check(result.Rows.Count(r => r.Name.StartsWith("作者.人物") && r.Favourite == "是" && r.Hidden == "否") == 2, "BA applies only to resources present in each version");
            var native = result.Rows.Single(r => r.Name == "Native.Marked.1.var");
            Check(native.Favourite == "是" && native.Hidden == "是", "VAM native fav/hide markers");
            var missing = result.Rows.Single(r => r.Missing);
            Check(missing.Hidden == "是（记录）" && missing.FullPath.Contains("未找到"), "missing record preserves saved state");
            var broken = result.Rows.Single(r => r.Name == "Broken.Archive.1.var");
            Check(broken.Favourite == "未知" && broken.Hidden == "未知", "unreadable archives never reported as no");
            Check(!missing.SiblingCount.HasValue, "missing targets have no invented sibling count");
            Check(native.SiblingCount == 5, "count includes other files and same-level junction, excludes target");
            Check(native.SiblingNames.Contains("循环链接\\") && !native.SiblingNames.Contains("Native.Marked.1.var"), "folder slash and target exclusion");
            Check(result.Rows.Where(r => !r.Missing).All(r => r.SiblingNames.Length == r.SiblingCount.Value), "display count equals expandable list size");
            string siblingsFolder = Path.Combine(testRoot, "Sibling-check");
            Directory.CreateDirectory(Path.Combine(siblingsFolder, "子目录"));
            string target = Path.Combine(siblingsFolder, "target.var");
            File.WriteAllText(target, "target");
            File.WriteAllText(Path.Combine(siblingsFolder, "说明.txt"), "notes");
            File.WriteAllText(Path.Combine(siblingsFolder, "子目录", "nested.var"), "nested");
            var siblingNames = Scanner.ReadSiblingNames(target, CancellationToken.None);
            Check(siblingNames.SequenceEqual(new[] { "子目录\\", "说明.txt" }), "list is flat, folders first, no subdirectory traversal");
            string singleFolder = Path.Combine(testRoot, "Single-target"); Directory.CreateDirectory(singleFolder);
            string single = Path.Combine(singleFolder, "only.var"); File.WriteAllText(single, "only");
            Check(Scanner.ReadSiblingNames(single, CancellationToken.None).Length == 0, "only target yields zero siblings");
            bool missingTarget = false;
            try { Scanner.ReadSiblingNames(Path.Combine(testRoot, "not-present.var"), CancellationToken.None); }
            catch (FileNotFoundException) { missingTarget = true; }
            Check(missingTarget, "removed target yields explicit failure");
            Check(result.Warnings.Count == 2, "invalid JSON and archive diagnosed");
            Check(result.DirectoryCount == 1, "junction aliases and cyclic links do not duplicate or loop");
            Check(!result.Rows.Any(r => r.Name.Contains("Deluxe")), "exact tag excludes deluxe");
            options.Contains = true;
            Check(Scanner.Scan(options, CancellationToken.None, delegate { }).Groups == 5, "substring tag includes deluxe");
            options.Contains = false; options.IncludeMissing = false;
            Check(!Scanner.Scan(options, CancellationToken.None, delegate { }).Rows.Any(r => r.Missing), "missing checkbox hides absent files");
            options.Tag = "DEL"; Check(Scanner.TagMatches("del", options), "case insensitive tag matching");
            options.Tag = ""; Check(Scanner.Scan(options, CancellationToken.None, delegate { }).Groups == 5, "blank query lists all tagged groups");
            var cancel = new CancellationTokenSource(); cancel.Cancel();
            bool stopped = false;
            try { Scanner.Scan(options, cancel.Token, delegate { }); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "cancellation observed");
            options.Tag = "no such tag";
            Check(Scanner.Scan(options, CancellationToken.None, delegate { }).Rows.Count == 0, "empty matching result");
            options.VamPath = Path.Combine(testRoot, "does-not-exist");
            bool badPath = false;
            try { Scanner.Scan(options, CancellationToken.None, delegate { }); } catch (DirectoryNotFoundException) { badPath = true; }
            Check(badPath, "invalid path explicitly rejected");
            DeletionTests.Run(testRoot, Check);
            SmartSelectionTests.Run(testRoot, Check);
            Console.WriteLine("PASS: " + assertions + " assertions. Fixture: " + testRoot);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
