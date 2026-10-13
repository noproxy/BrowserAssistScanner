using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BrowserAssistScanner;

public static class HoverTests
{
    private static int assertions;
    private static void Check(bool condition, string text) { if (!condition) throw new Exception(text); assertions++; }
    private static ThumbnailResult ImageResult() { return new ThumbnailResult { Image = new Bitmap(64, 64) }; }
    private static DataGridViewRow Child(ScannerForm form, string name) { return form.ResultsGrid.Rows.Cast<DataGridViewRow>().Single(r => Convert.ToString(r.Cells["Package"].Value) == name); }
    [STAThread] public static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetFullPath(args[0]), "Hover-" + Guid.NewGuid().ToString("N"));
        if (!DeleteScope.Descendant(root, Path.GetFullPath(args[0]))) throw new Exception("invalid fixture scope");
        string vam = Path.Combine(root, "VAM"), packages = Path.Combine(root, "packages"), folder = Path.Combine(packages, "samples");
        string data = Path.Combine(vam, @"Saves\PluginData\JayJayWon\BrowserAssist\VARResourcesUserData");
        Directory.CreateDirectory(data); Directory.CreateDirectory(Path.Combine(folder, "目录.var"));
        string main = Path.Combine(folder, "Fixture.Hover.1.var"), peer = Path.Combine(folder, "Another.Package.1.VAR");
        using (var zip = ZipFile.Open(main, ZipArchiveMode.Create)) zip.CreateEntry("Saves/scene/sample.json");
        using (var zip = ZipFile.Open(peer, ZipArchiveMode.Create)) zip.CreateEntry("Saves/scene/peer.json");
        string prefs = Path.Combine(vam, "AddonPackagesFilePrefs", "Another.Package.1"); Directory.CreateDirectory(prefs);
        File.WriteAllText(Path.Combine(prefs, "peer.json.fav"), ""); File.WriteAllText(Path.Combine(prefs, "peer.json.hide"), "");
        File.WriteAllText(Path.Combine(folder, "Broken.Peer.1.var"), "invalid ZIP");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "fixture");
        File.WriteAllText(Path.Combine(data, "fixture.userData"), Scanner.Json().Serialize(new { resources = new[] { new {
            creatorName = "Fixture", packageName = "Hover", resourceFullFileName = @"Saves\scene\sample.json",
            Tags = new[] { new { tagName = "del", tagCategory = "User" } }
        } } }));
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        int code = 0; bool tested = false;
        using (var form = new ScannerForm(true)) {
            form.SetScanOptions(new ScanOptions { VamPath = vam, PackagesPath = packages, Tag = "del" });
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-3000, -3000); form.ShowInTaskbar = false;
            form.ScanFinished += async delegate {
                if (tested) return; tested = true;
                try {
                    var owner = form.CurrentResult.Rows.Single(); form.CheckParent(owner, true);
                    Check(owner.SiblingNames.Contains("目录.var\\") && !owner.SiblingNames.Any(n => n.EndsWith("/")), "all displayed directories use Windows suffix");
                    var peerView = Child(form, "Another.Package.1.VAR");
                    Check(Convert.ToString(peerView.Cells["Favourite"].Value) == "是" && Convert.ToString(peerView.Cells["Hidden"].Value) == "是" && owner.Marks == "", "sibling columns show native favourite/hidden without top-row favourite warning");
                    Check(Convert.ToString(Child(form, "notes.txt").Cells["Favourite"].Value) == "—" && Convert.ToString(Child(form, "目录.var\\").Cells["Hidden"].Value) == "—", "notes and folders show not-applicable states");
                    Check(Convert.ToString(Child(form, "Broken.Peer.1.var").Cells["Favourite"].Value) == "未知" && Convert.ToString(Child(form, "Broken.Peer.1.var").Cells["Hidden"].Value) == "未知", "unreadable sibling markers remain unknown");
                    form.ResultsGrid.CurrentCell = Child(form, "notes.txt").Cells["Package"];
                    var focused = form.ResultsGrid.CurrentCell;
                    var requests = new Dictionary<string, TaskCompletionSource<ThumbnailResult>>(StringComparer.OrdinalIgnoreCase);
                    requests[main] = new TaskCompletionSource<ThumbnailResult>(); requests[peer] = new TaskCompletionSource<ThumbnailResult>();
                    form.ThumbnailLoader = path => requests[path].Task;
                    form.HoverListCell(0, "Package"); var first = form.PreviewHoverNowAsync();
                    Check(form.PreviewVisible && form.PreviewPath == main, "hover main package shows loading popup: visible=" + form.PreviewVisible + ", path=" + form.PreviewPath + ", active=" + (Form.ActiveForm == null ? "none" : Form.ActiveForm.GetType().Name));
                    form.HoverListCell(Child(form, "Another.Package.1.VAR").Index, "Package"); var second = form.PreviewHoverNowAsync();
                    requests[peer].SetResult(ImageResult()); await second;
                    Check(form.PreviewVisible && form.PreviewHasImage && form.PreviewPath == peer, "sibling uppercase VAR gets its own thumbnail");
                    Check(form.ResultsGrid.CurrentCell == focused && form.CheckedCount == 1, "popup leaves exact focus and checkbox states unchanged");
                    var stale = ImageResult(); requests[main].SetResult(stale); await first;
                    Check(form.PreviewPath == peer && stale.Image == null, "late result cannot overwrite newer hover, discarded image disposed");
                    form.HoverListCell(Child(form, "notes.txt").Index, "Package"); await form.PreviewHoverNowAsync();
                    Check(!form.PreviewVisible && form.PreviewPath == null, "non-VAR sibling hides thumbnail: visible=" + form.PreviewVisible + ", path=" + form.PreviewPath);
                    form.HoverListCell(Child(form, "目录.var\\").Index, "Package"); await form.PreviewHoverNowAsync();
                    Check(!form.PreviewVisible, "directory ending in .var never treated as VAR file");
                    form.HoverListCell(0, "Favourite"); await form.PreviewHoverNowAsync();
                    Check(!form.PreviewVisible, "hover applies to name column only");
                    requests[peer] = new TaskCompletionSource<ThumbnailResult>();
                    form.HoverListCell(Child(form, "Another.Package.1.VAR").Index, "Package"); var pending = form.PreviewHoverNowAsync();
                    form.ToggleSiblings(0); var discarded = ImageResult(); requests[peer].SetResult(discarded); await pending;
                    Check(!form.PreviewVisible && discarded.Image == null, "collapsing while loading hides popup and discards stale image");
                    form.ThumbnailLoader = path => Task.FromResult(new ThumbnailResult());
                    form.HoverListCell(0, "Package"); await form.PreviewHoverNowAsync();
                    Check(form.PreviewVisible && !form.PreviewHasImage, "unavailable thumbnail handled without substituting file icon");
                    form.StopPreview(); Check(!form.PreviewVisible, "mouse leave hides preview");

                    int reads = 0; bool sta = false;
                    using (var service = new ThumbnailService(path => { reads++; sta = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA; return new Bitmap(8, 8); })) {
                        using (var image = await service.GetAsync(peer)) Check(image.Image != null && !image.CacheHit && sta, "thumbnail extraction runs on background STA worker");
                        using (var image = await service.GetAsync(peer)) Check(image.Image != null && image.CacheHit && reads == 1, "repeated hover uses memory cache");
                        File.AppendAllText(peer, "changed");
                        using (var image = await service.GetAsync(peer)) Check(!image.CacheHit && reads == 2, "changed file invalidates thumbnail cache");
                        using (var image = await service.GetAsync(Path.Combine(folder, "missing.var"))) Check(image.Image == null, "missing VAR has no thumbnail");
                    }
                    string actual = Environment.GetEnvironmentVariable("BROWSERASSIST_TEST_VAR");
                    if (!String.IsNullOrEmpty(actual) && File.Exists(actual)) using (var service = new ThumbnailService()) {
                        using (var image = await service.GetAsync(actual)) Check(image.Image != null && image.Image.Width > 32 && image.Image.Height > 32, "real Windows VAR thumbnail retrieved by production service");
                        using (var image = await service.GetAsync(actual)) Check(image.Image != null && image.CacheHit, "real thumbnail retained in memory cache");
                    }
                    Console.WriteLine("PASS: " + assertions + " hover assertions. Fixture: " + root);
                } catch (Exception error) { code = 1; Console.Error.WriteLine(error); }
                form.Close();
            };
            Application.Run(form);
        }
        return code;
    }
}
