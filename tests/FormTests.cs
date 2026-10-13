using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using BrowserAssistScanner;

public static class FormTests
{
    private static int assertions;
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); assertions++; }
    private static string Sample(string root, string package)
    {
        string folder = Path.Combine(root, package, "2026", "20261001", "秋分"); Directory.CreateDirectory(Path.Combine(folder, "同级文件夹", "nested"));
        File.WriteAllText(Path.Combine(folder, "同级文件夹", "nested", "child.txt"), "fixture");
        File.WriteAllText(Path.Combine(folder, "peer.txt"), "fixture");
        string path = Path.Combine(folder, "Fixture." + package + ".1.var");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) zip.CreateEntry("Saves/scene/sample.json");
        return path;
    }
    private static DataGridViewRow Child(ScannerForm form, PackageRow owner, string label) {
        int parent = form.ResultsGrid.Rows.Cast<DataGridViewRow>().Single(r => Object.ReferenceEquals(r.Tag, owner)).Index;
        return form.ResultsGrid.Rows.Cast<DataGridViewRow>().Skip(parent + 1).TakeWhile(r => !(r.Tag is PackageRow)).Single(r => Convert.ToString(r.Cells["Package"].Value) == label);
    }
    [STAThread]
    public static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetFullPath(args[0]), "Form-" + Guid.NewGuid().ToString("N"));
        if (!DeleteScope.Descendant(root, Path.GetFullPath(args[0]))) throw new Exception("invalid fixture scope");
        string vam = Path.Combine(root, "VAM"), packages = Path.Combine(root, "packages");
        string data = Path.Combine(vam, @"Saves\PluginData\JayJayWon\BrowserAssist\VARResourcesUserData");
        Directory.CreateDirectory(data); Directory.CreateDirectory(packages);
        string fastPath = Sample(packages, "Fast"), slowPath = Sample(packages, "Slow");
        var resources = new[] { "Fast", "Slow" }.Select(name => new {
            creatorName = "Fixture", packageName = name, resourceFullFileName = @"Saves\scene\sample.json",
            Tags = new[] { new { tagName = "del", tagCategory = "User" } }
        }).ToArray();
        File.WriteAllText(Path.Combine(data, "fixture.userData"), Scanner.Json().Serialize(new { resources = resources }), Encoding.UTF8);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        int code = 0, phase = 0, scans = 0, confirmations = 0;
        string message = null;
        using (var form = new ScannerForm(true)) {
            form.SetScanOptions(new ScanOptions { VamPath = vam, PackagesPath = packages, Tag = "del", IncludeMissing = true });
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-3000, -3000); form.ShowInTaskbar = false;
            Action<Exception> fail = error => { Console.Error.WriteLine(error); code = 1; form.Close(); };
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) { fail(e.Exception); };
            form.DeleteConfirmation = text => { message = text; confirmations++; return phase == 0 ? DialogResult.No : DialogResult.Yes; };
            form.ScanFinished += delegate {
                scans++;
                if (phase != 0) return;
                try {
                    Check(form.CurrentResult.FileCount == 2, "initial fixture scan");
                    var slow = form.CurrentResult.Rows.Single(r => r.FullPath == slowPath);
                    form.CheckParent(slow, true);
                    Check(slow.AutoSelectSiblings && form.CheckedCount == 7 && slow.SiblingNames.All(n => form.IsSiblingChecked(slow, n)) && form.SelectedRedundant(slow) == slow.RedundantParents.Last().Path, "GUI applies intelligent initial selection and final ancestor candidate");
                    form.CheckSibling(slow, "peer.txt", false);
                    Check(slow.RedundantParents.Length == 4 && slow.RedundantParents[0].Label == "秋分\\" && slow.RedundantParents[2].Label == "2026\\20261001\\秋分\\", "multi-level labels and scan boundary exclusion");
                    Check(form.ResultsGrid.Columns["Select"].Index < form.ResultsGrid.Columns["Expand"].Index && form.ResultsGrid.Columns["Expand"].Index < form.ResultsGrid.Columns["Package"].Index, "expand switch placed immediately before package name");
                    Check(!form.ResultsGrid.AllowUserToResizeRows && form.ResultsGrid.Rows.Cast<DataGridViewRow>().All(r => r.Height == 32 && r.Resizable == DataGridViewTriState.False), "fixed heights for every row");
                    string located = null;
                    form.FolderOpener = path => located = path;
                    form.DoubleClickListCell(Child(form, slow, "peer.txt").Index, "Package");
                    Check(located == Path.Combine(Path.GetDirectoryName(slowPath), "peer.txt"), "double click sibling file locates that file, not owning VAR");
                    form.DoubleClickListCell(Child(form, slow, "同级文件夹\\").Index, "Package");
                    Check(located == Path.Combine(Path.GetDirectoryName(slowPath), "同级文件夹"), "double click sibling folder resolves Windows suffix correctly");
                    var peerView = Child(form, slow, "peer.txt");
                    form.ResultsGrid.CurrentCell = peerView.Cells["Package"];
                    form.DispatchListKey(Keys.Space);
                    Check(form.IsSiblingChecked(slow, "peer.txt") && Convert.ToString(form.ResultsGrid.CurrentCell.Value) == "peer.txt", "space toggles sibling and retains its current row and column");
                    form.ResultsGrid.CurrentCell = Child(form, slow, "peer.txt").Cells[0];
                    form.DispatchListKey(Keys.Space);
                    Check(!form.IsSiblingChecked(slow, "peer.txt") && Convert.ToString(form.ResultsGrid.CurrentRow.Cells["Package"].Value) == "peer.txt" && form.ResultsGrid.CurrentCell.ColumnIndex == 0, "space directly on checkbox retains child checkbox focus");
                    form.DispatchListKey(Keys.Space);
                    form.SortResults("Package", true);
                    Check(Convert.ToString(form.ResultsGrid.CurrentRow.Cells["Package"].Value) == "peer.txt", "sorting retains exact child focus");
                    form.DispatchListKey(Keys.Left);
                    Check(Object.ReferenceEquals(form.ResultsGrid.CurrentRow.Tag, slow) && form.IsSiblingChecked(slow, "peer.txt"), "left on child collapses owner and preserves checks");
                    form.DispatchListKey(Keys.Right);
                    Check(Child(form, slow, "peer.txt").Cells[0] is DataGridViewCheckBoxCell, "right on top row expands child list");
                    form.ClickListCell(Child(form, slow, slow.RedundantParents.Last().Label).Index, "Select");
                    Check(form.CheckedCount == 7 && slow.SiblingNames.All(n => form.IsSiblingChecked(slow, n)), "choosing highest parent automatically checks all siblings and schedules every level");
                    Check(form.ResultsGrid.Rows.Cast<DataGridViewRow>().Count(r => r.Cells[0] is RadioCell && Object.Equals(r.Cells[0].Value, true)) == 1, "redundant levels are mutually exclusive radio choices");
                    form.ResultsGrid.CurrentCell = Child(form, slow, "peer.txt").Cells["Package"];
                    form.DispatchListKey(Keys.Space);
                    Check(form.SelectedRedundant(slow) == null && !form.IsSiblingChecked(slow, "peer.txt") && Convert.ToString(form.ResultsGrid.CurrentCell.Value) == "peer.txt", "unchecking any sibling clears selected parent without losing focus");
                    form.CheckRedundant(slow, slow.RedundantParents[0].Path);
                    var radio = Child(form, slow, slow.RedundantParents[0].Label);
                    form.ResultsGrid.CurrentCell = radio.Cells[0];
                    form.DispatchListKey(Keys.Space);
                    Check(form.SelectedRedundant(slow) == null && form.IsSiblingChecked(slow, "peer.txt") && form.ResultsGrid.CurrentRow.Cells[0] is RadioCell, "space can clear a radio while retaining all sibling selections and radio focus");
                    form.ClearChecks(); form.CheckParent(slow, true);
                    form.ApplicationsRunning = () => true;
                    form.ClickDelete();
                    Check(confirmations == 1 && message == "检测到VAM正在运行中， 删除速度较慢，是否确认删除？", "running branch exact confirmation text");
                    Check(File.Exists(slowPath) && form.CheckedCount == 7 && scans == 1, "declined deletion preserves file, automatic ancestor selection, and scan");
                    form.ClearChecks();
                    var fast = form.CurrentResult.Rows.Single(r => r.FullPath == fastPath);
                    form.CheckParent(fast, true); form.CheckRedundant(fast, fast.RedundantParents.Last().Path);
                    form.ApplicationsRunning = () => false; phase = 1;
                    form.ClickDelete();
                } catch (Exception error) { fail(error); }
            };
            form.DeleteFinished += report => {
                try {
                    Check(report.Issues.Count == 0, "GUI deletion errors: " + String.Join(";", report.Issues));
                    Check(form.CheckedCount == 0 && !form.SelectionActionsVisible, "successful deletion refresh removes all checks and hides action buttons");
                    if (phase == 1) {
                        Check(!report.PostOrder && report.Recycled == 7 && confirmations == 1, "fast mode plus empty parent cleanup without confirmation");
                        Check(scans == 2 && form.CurrentResult.FileCount == 1 && !File.Exists(fastPath), "fast deletion automatically refreshes results");
                        Check(!Directory.Exists(Path.Combine(packages, "Fast")) && Directory.Exists(packages), "all chosen ancestor levels recycled while scan root remains");
                        var slow = form.CurrentResult.Rows.Single(r => r.FullPath == slowPath);
                        form.CheckParent(slow, true); form.CheckRedundant(slow, slow.RedundantParents.Last().Path);
                        form.ApplicationsRunning = () => true; phase = 2;
                        form.BeginInvoke(new Action(() => form.ClickDelete()));
                    } else {
                        Check(report.PostOrder && report.Recycled == 9 && confirmations == 2, "recursive mode plus empty parent cleanup");
                        Check(scans == 3 && form.CurrentResult.FileCount == 0 && !File.Exists(slowPath), "recursive deletion automatically refreshes results");
                        Check(!Directory.Exists(Path.Combine(packages, "Slow")) && Directory.Exists(packages), "recursive cleanup stops before scan root");
                        Console.WriteLine("PASS: " + assertions + " GUI deletion assertions. Fixture: " + root);
                        form.Close();
                    }
                } catch (Exception error) { fail(error); }
            };
            using (var timeout = new Timer { Interval = 45000 }) {
                timeout.Tick += delegate { fail(new Exception("GUI deletion test timed out, phase " + phase)); }; timeout.Start();
                Application.Run(form);
            }
        }
        return code;
    }
}
