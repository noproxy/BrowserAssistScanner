using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using BrowserAssistScanner;

public static class HideTests
{
    private static int assertions;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); assertions++; }
    private static string Sample(string folder, string uid, params string[] entries) {
        Directory.CreateDirectory(folder); string path = Path.Combine(folder, uid + ".var");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) foreach (string entry in entries) zip.CreateEntry(entry);
        return path;
    }
    private static DeleteRequest Request(string folder, params DeleteTarget[] targets) { return new DeleteRequest { AllowedRoots = new[] { folder }, Targets = targets.ToList() }; }
    private static string Pref(string vam, string uid, string resource) { return Path.Combine(vam, "AddonPackagesFilePrefs", uid, resource.Replace('/', '\\') + ".hide"); }
    [STAThread]
    public static int Main(string[] args) {
        string root = Path.Combine(args[0], "Hide-" + Guid.NewGuid().ToString("N"));
        string vam = Path.Combine(root, "VAM"), packages = Path.Combine(root, "packages"); Directory.CreateDirectory(vam); Directory.CreateDirectory(packages);
        try {
            string model = Sample(Path.Combine(packages, "model"), "Hide.Model.1", "Saves/scene/main.json", "Saves/scene/中文场景.json", "Custom/Atom/Person/Appearance/Preset_look.vap", "Custom/Textures/image.png", "meta.json");
            string otherVersion = Sample(Path.GetDirectoryName(model), "Hide.Model.2", "Saves/scene/main.json");
            string dependency = Sample(Path.Combine(Path.GetDirectoryName(model), "DP"), "Hide.Dependency.1", "Saves/scene/main.json");
            string notes = Path.Combine(Path.GetDirectoryName(model), "说明.txt"); File.WriteAllText(notes, "notes");
            string existing = Pref(vam, "Hide.Model.1", "Saves/scene/main.json"); Directory.CreateDirectory(Path.GetDirectoryName(existing)); File.WriteAllBytes(existing, new byte[] { 9 });
            string favourite = existing.Substring(0, existing.Length - 5) + ".fav"; File.WriteAllBytes(favourite, new byte[] { 7 });
            byte[] before = File.ReadAllBytes(model);
            var request = Request(packages, new DeleteTarget { Path = model }, new DeleteTarget { Path = model }, new DeleteTarget { Path = notes }, new DeleteTarget { Path = Path.GetDirectoryName(dependency), IsDirectory = true });
            var report = HideEngine.Hide(request, vam, CancellationToken.None, delegate { });
            Check(report.Completed.SequenceEqual(new[] { model }) && report.Issues.Count == 0, "duplicate views hide selected VAR only once");
            Check(report.CreatedMarkers == 2 && report.ExistingMarkers == 1 && report.Ignored == 2, "resource marker and ignored item counts");
            Check(File.Exists(Pref(vam, "Hide.Model.1", "Saves/scene/中文场景.json")) && File.Exists(Pref(vam, "Hide.Model.1", "Custom/Atom/Person/Appearance/Preset_look.vap")), "native markers include scenes and presets with original paths");
            Check(File.ReadAllBytes(existing).SequenceEqual(new byte[] { 9 }) && File.ReadAllBytes(favourite).SequenceEqual(new byte[] { 7 }), "existing hidden and favourite markers preserved");
            Check(File.ReadAllBytes(model).SequenceEqual(before) && File.Exists(dependency) && File.Exists(notes), "archives and non-VAR selections never modified or deleted");
            Check(!Directory.Exists(Path.Combine(vam, "AddonPackagesFilePrefs", "Hide.Model.2")) && !Directory.Exists(Path.Combine(vam, "AddonPackagesFilePrefs", "Hide.Dependency.1")), "unselected version and directory contents stay unhidden");
            Check(!File.Exists(Pref(vam, "Hide.Model.1", "meta.json")) && !File.Exists(Pref(vam, "Hide.Model.1", "Custom/Textures/image.png")), "metadata and thumbnails do not receive fake resource markers");
            report = HideEngine.Hide(request, vam, CancellationToken.None, delegate { });
            Check(report.Completed.Count == 1 && report.CreatedMarkers == 0 && report.ExistingMarkers == 3, "repeated operation is idempotent");
            request.ParentCleanups.Add(new ParentCleanup { Boundary = packages, Directories = new[] { Path.GetDirectoryName(model) } });
            report = HideEngine.Hide(request, vam, CancellationToken.None, delegate { });
            Check(report.Ignored == 3 && Directory.Exists(Path.GetDirectoryName(model)), "redundant parent selections are counted as ignored and never removed");
            var cancelled = new CancellationTokenSource(); cancelled.Cancel(); report = HideEngine.Hide(Request(packages, new DeleteTarget { Path = otherVersion }), vam, cancelled.Token, delegate { });
            Check(report.Cancelled && report.Completed.Count == 0 && !Directory.Exists(Path.Combine(vam, "AddonPackagesFilePrefs", "Hide.Model.2")), "cancel before work creates no markers");
            string malicious = Sample(Path.GetDirectoryName(model), "Hide.Unsafe.1", "Saves/scene/good.json", "Saves/scene/../../outside.json");
            report = HideEngine.Hide(Request(packages, new DeleteTarget { Path = malicious }), vam, CancellationToken.None, delegate { });
            Check(report.Issues.Count == 1 && report.Completed.Count == 0 && !Directory.Exists(Path.Combine(vam, "AddonPackagesFilePrefs", "Hide.Unsafe.1")), "reject unsafe archive paths before writing any marker");
            string broken = Path.Combine(Path.GetDirectoryName(model), "Hide.Broken.1.var"); File.WriteAllText(broken, "broken");
            string empty = Sample(Path.GetDirectoryName(model), "Hide.Empty.1", "meta.json");
            report = HideEngine.Hide(Request(packages, new DeleteTarget { Path = broken }, new DeleteTarget { Path = empty }, new DeleteTarget { Path = otherVersion }), vam, CancellationToken.None, delegate { });
            Check(report.Issues.Count == 2 && report.Completed.SequenceEqual(new[] { otherVersion }), "failure of one package does not hide its status or stop other selections");
            RunGui(root, vam, packages);
            Console.WriteLine("PASS: " + assertions + " hide assertions. Fixture: " + root); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void RunGui(string root, string vam, string packages) {
        string folder = Path.Combine(packages, "gui");
        string aPath = Sample(folder, "Hide.A.1", "Saves/scene/main.json", "Saves/scene/second.json");
        string bPath = Sample(folder, "Hide.B.1", "Saves/scene/main.json");
        string childPath = Sample(Path.Combine(folder, "DP"), "Hide.Child.1", "Saves/scene/main.json");
        string notes = Path.Combine(folder, "说明.txt"); File.WriteAllText(notes, "notes");
        string data = Path.Combine(vam, @"Saves\PluginData\JayJayWon\BrowserAssist\VARResourcesUserData"); Directory.CreateDirectory(data);
        string dataFile = Path.Combine(data, "fixture.userData");
        File.WriteAllText(dataFile, Scanner.Json().Serialize(new { resources = new[] { "A", "B" }.Select(n => new { creatorName = "Hide", packageName = n, resourceFullFileName = @"Saves\scene\main.json", baFavourite = n == "A" ? "Active" : "Inactive", baHidden = "Inactive", Tags = new[] { new { tagName = "del", tagCategory = "User" } } }).ToArray() }), Encoding.UTF8);
        byte[] originalData = File.ReadAllBytes(dataFile);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        int scans = 0, phase = 0; Exception failure = null;
        using (var form = new ScannerForm(true)) {
            form.SetScanOptions(new ScanOptions { VamPath = vam, PackagesPath = packages });
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-3000, -3000); form.ShowInTaskbar = false;
            Action<Exception> fail = e => { failure = e; form.Close(); };
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { fail(e.Exception); };
            form.ApplicationsRunning = () => { throw new Exception("hide must not run deletion process check"); };
            form.ScanFinished += delegate {
                scans++; if (phase != 0) return;
                try {
                    Check(form.CurrentResult.FileCount == 2 && form.VisiblePackageCount == 2, "GUI fixture scan");
                    var a = form.CurrentResult.Rows.Single(r => r.FullPath == aPath);
                    var b = form.CurrentResult.Rows.Single(r => r.FullPath == bPath);
                    form.CheckParent(a, true); form.CheckParent(b, true); form.CheckSibling(a, "说明.txt", true); form.CheckSibling(a, "DP\\", true);
                    Check(form.HideButton.Text == "标记为隐藏" && form.HideButton.Visible && form.HideButton.Enabled, "hide action visible beside delete after selection");
                    var controls = form.HideButton.Parent.Controls.Cast<Control>().ToArray(); int index = Array.IndexOf(controls, form.HideButton);
                    Check(index > 0 && controls[index - 1].Text == "删除选中文件", "hide button immediately follows delete");
                    form.CheckParent(a, false); form.CheckParent(b, false); form.CheckParent(a, true); form.CheckSibling(a, "说明.txt", true); form.CheckSibling(a, "DP\\", true);
                    Check(form.CheckedCount == 3, "hide chooses only A despite selected folder and notes");
                    string preview = PreviewArtifacts.PathFor("hide-action.png");
                    Check(Path.GetDirectoryName(preview).EndsWith(@"test-artifacts\ui-previews"), "all UI previews use dedicated folder");
                    using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(preview); }
                    phase = 1; form.ClickHide();
                } catch (Exception e) { fail(e); }
            };
            form.HideFinished += report => {
                try {
                    Check(report.Completed.SequenceEqual(new[] { aPath }) && report.Ignored == 2 && report.Issues.Count == 0, "GUI marks only explicitly selected VAR");
                    Check(scans == 2 && form.CurrentResult.Rows.Single(r => r.FullPath == aPath).Hidden == "是" && form.VisiblePackageCount == 1, "automatic rescan updates native hidden state and default filtering");
                    Check(form.CurrentResult.Rows.Single(r => r.FullPath == aPath).Favourite == "是" && File.ReadAllBytes(dataFile).SequenceEqual(originalData), "favourites tags and BA records preserved");
                    Check(form.CheckedCount == 0 && !form.SelectionActionsVisible && !form.HideButton.Enabled, "successful hide clears selection and attached companions");
                    Check(File.Exists(aPath) && File.Exists(bPath) && File.Exists(childPath) && File.Exists(notes) && !Directory.Exists(Path.Combine(vam, "AddonPackagesFilePrefs", "Hide.Child.1")), "GUI hiding leaves selected directories and unselected files intact");
                    form.SetResultFilters(true, false, false); Check(form.VisiblePackageCount == 2, "show hidden exposes newly hidden package");
                    var a = form.CurrentResult.Rows.Single(r => r.FullPath == aPath); form.CheckParent(a, true);
                    var mirrors = form.ResultsGrid.Rows.Cast<DataGridViewRow>().Where(r => !(r.Tag is PackageRow) && Convert.ToString(r.Cells["FullPath"].Value) == aPath).ToArray();
                    Check(mirrors.Length == 2 && mirrors.All(r => Convert.ToString(r.Cells["Hidden"].Value) == "是"), "all sibling mirrors show hidden state after refresh"); form.Close();
                } catch (Exception e) { fail(e); }
            };
            using (var timeout = new System.Windows.Forms.Timer { Interval = 45000 }) { timeout.Tick += delegate { fail(new Exception("hide GUI test timed out")); }; timeout.Start(); Application.Run(form); }
        }
        if (failure != null) throw failure;
    }
}
