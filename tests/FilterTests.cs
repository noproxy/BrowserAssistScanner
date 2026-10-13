using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using BrowserAssistScanner;

public static class FilterTests
{
    private static int assertions;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); assertions++; }
    private static object Record(string name, bool hidden) {
        return new { creatorName = "Filter", packageName = name, resourceFullFileName = @"Saves\scene\main.json",
            baHidden = hidden ? "Active" : "Inactive", Tags = new[] { new { tagName = "del", tagCategory = "User" } } };
    }
    private static string Sample(string folder, string name, bool broken) {
        Directory.CreateDirectory(folder); string path = Path.Combine(folder, "Filter." + name + ".1.var");
        if (broken) File.WriteAllText(path, "invalid ZIP");
        else using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) zip.CreateEntry("Saves/scene/main.json");
        return path;
    }
    private static string[] Visible(ScannerForm form) {
        return form.ResultsGrid.Rows.Cast<DataGridViewRow>().Select(r => r.Tag as PackageRow).Where(r => r != null).Select(r => r.Name).OrderBy(n => n).ToArray();
    }
    [STAThread]
    public static int Main(string[] args) {
        string root = Path.Combine(Path.GetFullPath(args[0]), "Filters-" + Guid.NewGuid().ToString("N"));
        if (!DeleteScope.Descendant(root, Path.GetFullPath(args[0]))) throw new Exception("invalid fixture scope");
        string vam = Path.Combine(root, "VAM"), packages = Path.Combine(root, "packages");
        string data = Path.Combine(vam, @"Saves\PluginData\JayJayWon\BrowserAssist\VARResourcesUserData"); Directory.CreateDirectory(data);
        Sample(packages, "Visible", false); Sample(packages, "HiddenBA", false); Sample(packages, "HiddenNative", false);
        Sample(Path.Combine(packages, "DP"), "Dependency", false); Sample(Path.Combine(packages, "DP"), "HiddenDependency", false);
        Sample(packages, "Unknown", true);
        string marker = Path.Combine(vam, "AddonPackagesFilePrefs", "Filter.HiddenNative.1", "main.json.hide");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)); File.WriteAllText(marker, "");
        File.WriteAllText(Path.Combine(data, "filters.userData"), Scanner.Json().Serialize(new { resources = new[] {
            Record("Visible", false), Record("HiddenBA", true), Record("HiddenNative", false), Record("Dependency", false),
            Record("HiddenDependency", true), Record("Unknown", false), Record("Missing", false), Record("HiddenMissing", true)
        } }), Encoding.UTF8);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); int scans = 0, code = 0;
        using (var form = new ScannerForm(true)) {
            // Old scan setting must no longer exclude missing records or change frontend defaults.
            form.SetScanOptions(new ScanOptions { VamPath = vam, PackagesPath = packages, IncludeMissing = false });
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-3000, -3000); form.ShowInTaskbar = false;
            Action<Exception> fail = e => { Console.Error.WriteLine(e); code = 1; form.Close(); };
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) { fail(e.Exception); };
            form.ScanFinished += delegate {
                scans++;
                try {
                    Check(scans == 1, "filter change must never start a scan");
                    var snapshot = form.CurrentResult;
                    Check(snapshot.Rows.Count == 8 && snapshot.Rows.Count(r => r.Missing) == 2, "complete scan retains missing rows despite old IncludeMissing=false");
                    Check(form.ResultFilters.All(value => !value), "all three frontend filters default unchecked");
                    Check(Visible(form).SequenceEqual(new[] { "Filter.Unknown.1.var", "Filter.Visible.1.var" }), "default view excludes hidden missing and dependencies, retains unknown state");
                    var expectedCounts = new[] { 2, 4, 3, 6, 3, 6, 4, 8 };
                    for (int combination = 0; combination < 8; combination++) {
                        form.SetResultFilters((combination & 1) != 0, (combination & 2) != 0, (combination & 4) != 0);
                        Check(form.VisiblePackageCount == expectedCounts[combination], "independent overlapping filters combination " + combination);
                    }
                    var dep = snapshot.Rows.Single(r => r.Name == "Filter.HiddenDependency.1.var");
                    form.CheckParent(dep, true); form.CheckSibling(dep, "Filter.Dependency.1.var", true);
                    string radio = form.SelectedRedundant(dep); int count = form.CheckedCount;
                    form.SetResultFilters(false, false, false);
                    Check(form.CheckedCount == count && form.IsSiblingChecked(dep, "Filter.Dependency.1.var") && !Visible(form).Contains(dep.Name), "filtering selected row preserves owner and child checks");
                    form.SetResultFilters(true, false, true);
                    Check(form.ResultsGrid.Rows.Cast<DataGridViewRow>().Count() > form.VisiblePackageCount && form.CheckedCount == count && form.SelectedRedundant(dep) == radio, "restoring selected row preserves expansion and radio state");
                    form.ToggleSiblings(form.ResultsGrid.Rows.Cast<DataGridViewRow>().Single(r => Object.ReferenceEquals(r.Tag, dep)).Index);
                    form.SetResultFilters(false, false, false); form.SetResultFilters(true, false, true);
                    Check(form.ResultsGrid.Rows.Count == form.VisiblePackageCount && form.CheckedCount == count, "filtering also preserves manual collapse");
                    form.SetResultSearch("HiddenDependency");
                    Check(form.VisiblePackageCount == 1, "text search combines with checked status filters");
                    form.SetResultFilters(false, true, true);
                    Check(form.VisiblePackageCount == 0, "hidden status exclusion still applies to text search");
                    form.SetResultSearch(""); form.SetResultFilters(true, true, true); form.ClearChecks();
                    form.SortResults("Package", true);
                    Check(form.ResultsGrid.Rows.Cast<DataGridViewRow>().Select(r => ((PackageRow)r.Tag).Name).SequenceEqual(Visible(form).OrderByDescending(n => n)), "sorting works after filter changes");
                    Check(Object.ReferenceEquals(snapshot, form.CurrentResult) && scans == 1, "all toggles reuse exact completed scan without replacing result");
                    form.SetResultFilters(false, false, false); form.Refresh();
                    using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, form.ClientRectangle); bitmap.Save(PreviewArtifacts.PathFor("filters-default.png")); }
                    Console.WriteLine("PASS: " + assertions + " frontend filter assertions. Fixture: " + root); form.Close();
                } catch (Exception e) { fail(e); }
            };
            using (var timeout = new Timer { Interval = 45000 }) { timeout.Tick += delegate { fail(new Exception("frontend filter test timed out")); }; timeout.Start(); Application.Run(form); }
        }
        return code;
    }
}
