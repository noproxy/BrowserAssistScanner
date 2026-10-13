using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using BrowserAssistScanner;

public static class SharedSelectionTests
{
    private static int assertions;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); assertions++; }
    private static string Sample(string folder, string uid) {
        Directory.CreateDirectory(folder); string path = Path.Combine(folder, uid + ".var");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) zip.CreateEntry("Saves/scene/main.json");
        return path;
    }
    private static DataGridViewRow Parent(ScannerForm form, PackageRow owner) { return form.ResultsGrid.Rows.Cast<DataGridViewRow>().Single(r => Object.ReferenceEquals(r.Tag, owner)); }
    private static DataGridViewRow[] Children(ScannerForm form, PackageRow owner) {
        return form.ResultsGrid.Rows.Cast<DataGridViewRow>().Skip(Parent(form, owner).Index + 1).TakeWhile(r => !(r.Tag is PackageRow)).ToArray();
    }
    private static DataGridViewRow Child(ScannerForm form, PackageRow owner, string name) { return Children(form, owner).Single(r => Convert.ToString(r.Cells["Package"].Value) == name); }
    private static void Consistent(ScannerForm form, PackageRow a, PackageRow b) {
        var left = Children(form, a); var right = Children(form, b);
        Check(left.Length == right.Length && left.Zip(right, (x, y) => Convert.ToString(x.Cells["Package"].Value) == Convert.ToString(y.Cells["Package"].Value)
            && Object.Equals(x.Cells["Select"].Value, y.Cells["Select"].Value)
            && x.Cells["Select"].GetType() == y.Cells["Select"].GetType()
            && Convert.ToString(x.Cells["Favourite"].Value) == Convert.ToString(y.Cells["Favourite"].Value)
            && Convert.ToString(x.Cells["Hidden"].Value) == Convert.ToString(y.Cells["Hidden"].Value)
            && Convert.ToString(x.Cells["FullPath"].Value) == Convert.ToString(y.Cells["FullPath"].Value)).All(v => v), "same directory lists have identical items, states, checkboxes and paths");
    }
    [STAThread]
    public static int Main(string[] args) {
        string root = Path.Combine(Path.GetFullPath(args[0]), "Shared-" + Guid.NewGuid().ToString("N"));
        if (!DeleteScope.Descendant(root, Path.GetFullPath(args[0]))) throw new Exception("invalid fixture scope");
        string vam = Path.Combine(root, "VAM"), packages = Path.Combine(root, "packages"), folder = Path.Combine(packages, "shared");
        string data = Path.Combine(vam, @"Saves\PluginData\JayJayWon\BrowserAssist\VARResourcesUserData"); Directory.CreateDirectory(data);
        string aPath = Sample(folder, "Sync.A.1"), bPath = Sample(folder, "Sync.B.1"), cPath = Sample(folder, "Sync.C.1");
        string otherPath = Sample(Path.Combine(packages, "other"), "Sync.A.1");
        Directory.CreateDirectory(Path.Combine(folder, "DP")); File.WriteAllText(Path.Combine(folder, "DP", "note.txt"), "fixture");
        File.WriteAllText(Path.Combine(folder, "说明.txt"), "fixture");
        var resources = new[] { "A", "B", "C" }.Select(n => new {
            creatorName = "Sync", packageName = n, resourceFullFileName = @"Saves\scene\main.json",
            baFavourite = n == "A" ? "Inactive" : "Active", baHidden = n == "C" ? "Active" : "Inactive",
            Tags = new[] { new { tagName = n == "C" ? "best" : "del", tagCategory = "User" } }
        }).ToArray();
        File.WriteAllText(Path.Combine(data, "shared.userData"), Scanner.Json().Serialize(new { resources = resources }), Encoding.UTF8);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); int code = 0, scans = 0, phase = 0;
        using (var form = new ScannerForm(true)) {
            form.SetScanOptions(new ScanOptions { VamPath = vam, PackagesPath = packages });
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-3000, -3000); form.ShowInTaskbar = false;
            Action<Exception> fail = e => { Console.Error.WriteLine(e); code = 1; form.Close(); };
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) { fail(e.Exception); };
            form.ScanFinished += delegate {
                scans++;
                if (phase == 2) return;
                try {
                    var a = form.CurrentResult.Rows.Single(r => r.FullPath == aPath);
                    var b = form.CurrentResult.Rows.Single(r => r.FullPath == bPath);
                    var other = form.CurrentResult.Rows.Single(r => r.FullPath == otherPath);
                    if (phase == 0) {
                        Check(a.DirectoryNames.SequenceEqual(b.DirectoryNames) && a.DirectoryNames.Length == a.SiblingCount + 1, "directory list includes own VAR and agrees across owners");
                        form.ToggleSiblings(Parent(form, a).Index); form.ToggleSiblings(Parent(form, b).Index); Consistent(form, a, b);
                        Check(Child(form, a, a.Name).Cells["Select"] is DataGridViewCheckBoxCell && Child(form, a, b.Name).Cells["Select"] is DataGridViewCheckBoxCell, "any mirrored top-level VAR can initiate checking in an inactive directory");
                        Child(form, a, a.Name).Cells["Select"].Value = true; Application.DoEvents();
                        Check(Object.Equals(Parent(form, a).Cells["Select"].Value, true) && Object.Equals(Child(form, b, a.Name).Cells["Select"].Value, true) && form.CheckedCount == 1, "mouse check of self mirror synchronizes top row and peer copy, counts once");
                        Consistent(form, a, b);
                        form.ResultsGrid.CurrentCell = Child(form, a, b.Name).Cells["Package"]; form.DispatchListKey(Keys.Space);
                        Check(Object.Equals(Parent(form, b).Cells["Select"].Value, true) && Object.Equals(Child(form, b, b.Name).Cells["Select"].Value, true) && form.CheckedCount == 2 && Convert.ToString(form.ResultsGrid.CurrentCell.Value) == b.Name, "space checks another top-level VAR across copies and retains exact focus");
                        Check(!Object.Equals(Parent(form, other).Cells["Select"].Value, true), "same filename in another directory is independent");
                        form.CheckSibling(a, "说明.txt", true); form.CheckSibling(b, "DP\\", true);
                        Check(form.CheckedCount == 4 && form.IsSiblingChecked(b, "说明.txt") && form.IsSiblingChecked(a, "DP\\"), "shared note and folder checks deduplicate across owners");
                        Check(Convert.ToString(Child(form, a, b.Name).Cells["Favourite"].Value) == b.Favourite && Convert.ToString(Child(form, a, b.Name).Cells["FullPath"].Value) == b.FullPath, "self and peer copies preserve top-level info");
                        Check(Convert.ToString(Child(form, b, b.Name).Cells["Siblings"].Value) == b.SiblingCount.Value.ToString(), "own mirror includes top-level sibling-count information");
                        Check(Convert.ToString(Child(form, a, "Sync.C.1.var").Cells["Favourite"].Value) == "是" && Convert.ToString(Child(form, b, "Sync.C.1.var").Cells["Hidden"].Value) == "是", "non-del sibling states agree across lists");
                        form.ResultsGrid.CurrentCell = Child(form, b, a.Name).Cells["Select"]; form.DispatchListKey(Keys.Space);
                        Check(form.CheckedCount == 3 && Object.Equals(Parent(form, b).Cells["Select"].Value, true) && !form.IsSiblingChecked(b, a.Name) && form.IsSiblingChecked(b, "说明.txt"), "cancelling one VAR preserves another selected VAR and shared companions");
                        Check(Convert.ToString(form.ResultsGrid.CurrentRow.Cells["Package"].Value) == a.Name && form.ResultsGrid.CurrentCell.ColumnIndex == 0, "space unchecking a peer mirror keeps the original child checkbox focus");
                        Consistent(form, a, b);
                        form.CheckParent(a, true); form.CheckRedundant(a, a.RedundantParents.Last().Path);
                        Check(form.SelectedRedundant(b) == form.SelectedRedundant(a) && form.CheckedCount == 6 && form.IsSiblingChecked(b, "Sync.C.1.var"), "radio includes every file and shares a single cleanup level");
                        form.CheckSibling(b, "说明.txt", false);
                        Check(form.SelectedRedundant(a) == null && form.SelectedRedundant(b) == null && form.CheckedCount == 4, "unchecking a companion clears shared radio everywhere");
                        form.SetResultSearch("Sync.B.1.var"); form.CheckSibling(b, a.Name, false); form.SetResultSearch("");
                        Check(!Object.Equals(Parent(form, a).Cells["Select"].Value, true) && form.CheckedCount == 3, "filtered-away top row still synchronizes from peer list");
                        form.CheckSibling(b, a.Name, true); form.CheckSibling(a, "说明.txt", true);
                        form.ToggleSiblings(Parent(form, a).Index); form.SortResults("Package", true); phase = 1; form.ClickRefresh();
                    } else {
                        Check(form.CheckedCount == 5 && Children(form, a).Length == 0 && Children(form, b).Length > 0 && form.IsSiblingChecked(b, "说明.txt"), "refresh preserves shared checks and independent collapse");
                        form.ToggleSiblings(Parent(form, a).Index); Consistent(form, a, b);
                        form.CheckParent(a, false); form.CheckParent(b, false);
                        Check(form.CheckedCount == 0 && !form.IsSiblingChecked(a, "DP\\") && !form.IsSiblingChecked(b, "Sync.C.1.var"), "cancelling final main VAR clears shared supplemental selections");
                        form.CheckSibling(a, b.Name, true);
                        Check(form.CheckedCount == 1 && Object.Equals(Parent(form, b).Cells["Select"].Value, true), "peer mirror can start selection after all states cleared");
                        form.CheckRedundant(a, a.RedundantParents.Last().Path); Consistent(form, a, b);
                        using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(PreviewArtifacts.PathFor("shared-selection.png")); }
                        form.ApplicationsRunning = () => false; phase = 2; form.ClickDelete();
                    }
                } catch (Exception e) { fail(e); }
            };
            form.DeleteFinished += report => {
                try {
                    Check(report.Issues.Count == 0 && report.Recycled == 6 && !Directory.Exists(folder), "shared targets and empty directory recycled exactly once");
                    Check(File.Exists(otherPath) && Directory.Exists(packages) && form.CurrentResult.FileCount == 1 && form.CheckedCount == 0 && scans == 3, "deletion refresh clears disappeared shared state and preserves same-name file elsewhere and scan root");
                    Console.WriteLine("PASS: " + assertions + " shared selection assertions. Fixture: " + root); form.Close();
                } catch (Exception e) { fail(e); }
            };
            using (var timeout = new Timer { Interval = 60000 }) { timeout.Tick += delegate { fail(new Exception("shared-selection test timed out")); }; timeout.Start(); Application.Run(form); }
        }
        return code;
    }
}
