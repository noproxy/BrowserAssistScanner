using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BrowserAssistScanner
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--scan-json")
            {
                try
                {
                    var result = Scanner.Scan(new ScanOptions(), CancellationToken.None, delegate { });
                    File.WriteAllText(args[1], Scanner.Json().Serialize(result), Encoding.UTF8);
                }
                catch (Exception error) { File.WriteAllText(args[1], Scanner.Json().Serialize(new { Error = error.ToString() }), Encoding.UTF8); Environment.ExitCode = 1; }
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool smoke = args.Length == 2 && args[0] == "--ui-smoke";
            if (smoke) args[1] = PreviewArtifacts.PathFor(args[1]);
            if (smoke) Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) {
                File.WriteAllText(args[1] + ".json", Scanner.Json().Serialize(new { Error = e.Exception.ToString() }), Encoding.UTF8);
                Environment.ExitCode = 1; Application.Exit();
            };
            using (var form = new ScannerForm(smoke))
            {
                if (smoke)
                {
                    form.SetResultFilters(true, true, true);
                    int phase = 0;
                    string checkedPath = null, checkedSibling = null;
                    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-3000, -3000); form.ShowInTaskbar = false;
                    form.ScanFinished += async delegate {
                        try
                        {
                            if (form.CurrentResult == null || form.ResultsGrid.Rows.Count == 0) throw new Exception("UI scan returned no results");
                            if (phase == 0)
                            {
                                int count = form.ResultsGrid.Rows.Count;
                                form.SetResultSearch(form.CurrentResult.Rows.First(r => !r.Missing).Name);
                                if (form.VisiblePackageCount == 0) throw new Exception("Result search failed");
                                form.SetResultSearch("");
                                if (form.ResultsGrid.Rows.Count != count) throw new Exception("Clearing search lost rows");
                                form.SortResults("Siblings", true);
                                var numeric = form.ResultsGrid.Rows.Cast<DataGridViewRow>().Select(r => r.Tag as PackageRow).Where(r => r.SiblingCount.HasValue).Select(r => r.SiblingCount.Value).ToArray();
                                if (!numeric.SequenceEqual(numeric.OrderByDescending(n => n))) throw new Exception("Sibling count sorting failed");
                                var checkedRow = form.CurrentResult.Rows.First(r => !r.AutoSelectSiblings && r.SiblingCount > 0 && r.SiblingCount <= 8 && r.SiblingNames.Any(n => n.EndsWith("\\")));
                                checkedPath = checkedRow.FullPath; checkedSibling = checkedRow.SiblingNames.First(n => n.EndsWith("\\"));
                                form.SetResultSearch(checkedRow.Name);
                                form.ResultsGrid.Rows[0].Cells[0].Value = true;
                                Application.DoEvents();
                                if (form.CheckedCount != 1 || form.ResultsGrid.Rows.Count != 2 + checkedRow.RedundantParents.Length + checkedRow.DirectoryNames.Length) throw new Exception("Checkbox does not auto expand with all siblings unchecked");
                                int childIndex = 2 + checkedRow.RedundantParents.Length + Array.IndexOf(checkedRow.DirectoryNames, checkedSibling);
                                form.ResultsGrid.Rows[childIndex].Cells[0].Value = true;
                                Application.DoEvents();
                                if (form.CheckedCount != 2 || !form.SelectionActionsVisible) throw new Exception("Child checkbox or bottom actions failed");
                                form.ToggleSiblings(0);
                                if (form.ResultsGrid.Rows.Count != 1 || !form.IsSiblingChecked(checkedRow, checkedSibling)) throw new Exception("Collapse discarded sibling checks");
                                form.SetResultSearch("no matching visible row");
                                if (form.CheckedCount != 2) throw new Exception("Search discarded hidden checks");
                                form.SetResultSearch(checkedRow.Name);
                                phase = 1; form.ClickRefresh(); return;
                            }
                            var restored = form.CurrentResult.Rows.Single(r => r.FullPath == checkedPath);
                            if (form.CheckedCount != 2 || !form.IsSiblingChecked(restored, checkedSibling) || form.ResultsGrid.Rows.Count != 1) throw new Exception("Refresh failed to preserve collapsed checks");
                            form.ToggleSiblings(0);
                            int siblingIndex = 2 + restored.RedundantParents.Length + Array.IndexOf(restored.DirectoryNames, checkedSibling);
                            if (!Object.Equals(form.ResultsGrid.Rows[siblingIndex].Cells[0].Value, true)) throw new Exception("Reopening did not restore checked folder");
                            form.ResultsGrid.Rows[0].Cells[0].Value = false;
                            Application.DoEvents();
                            if (form.CheckedCount != 0 || form.SelectionActionsVisible || form.ResultsGrid.Rows[siblingIndex].Cells[0] is DataGridViewCheckBoxCell) throw new Exception("Unchecking parent did not clear and hide child checkbox");
                            form.ResultsGrid.Rows[0].Cells[0].Value = true;
                            Application.DoEvents();
                            if (form.CheckedCount != 1 || form.IsSiblingChecked(restored, checkedSibling)) throw new Exception("Recheck did not reset sibling selections");
                            form.ClearChecks();
                            if (form.CheckedCount != 0 || form.SelectionActionsVisible) throw new Exception("Clear all failed");
                            form.SetResultSearch("");
                            string opened = null;
                            form.FolderOpener = delegate(string path) { opened = path; };
                            int actual = form.ResultsGrid.Rows.Cast<DataGridViewRow>().First(r => r.Tag is PackageRow && !((PackageRow)r.Tag).Missing).Index;
                            form.OpenRow(actual);
                            if (opened != ((PackageRow)form.ResultsGrid.Rows[actual].Tag).FullPath) throw new Exception("Folder action selected the wrong path");
                            var preview = form.CurrentResult.Rows.FirstOrDefault(r => r.SiblingCount > 0 && r.SiblingCount <= 8 && r.SiblingNames.Any(n => n.EndsWith("\\")))
                                ?? form.CurrentResult.Rows.First(r => r.SiblingCount > 0 && r.SiblingCount <= 8);
                            form.SetResultSearch(preview.Name);
                            int parent = form.ResultsGrid.Rows.Cast<DataGridViewRow>().First(r => Object.ReferenceEquals(r.Tag, preview)).Index;
                            if (form.ResultsGrid.Rows.Count > form.VisiblePackageCount) form.ToggleSiblings(parent);
                            int collapsed = form.ResultsGrid.Rows.Count;
                            form.ToggleSiblings(parent);
                            int specialRows = 1 + preview.RedundantParents.Length;
                            if (form.ResultsGrid.Rows.Count != collapsed + specialRows + preview.DirectoryNames.Length) throw new Exception("Sibling expansion failed");
                            for (int i = 0; i < preview.DirectoryNames.Length; i++) {
                                var siblingView = form.ResultsGrid.Rows[parent + i + 1 + specialRows];
                                if (Convert.ToString(siblingView.Cells["Package"].Value).Trim() != preview.DirectoryNames[i]) throw new Exception("Expanded sibling filename mismatch");
                                PackageState siblingState;
                                if (preview.SiblingStates.TryGetValue(preview.DirectoryNames[i], out siblingState)
                                    && (Convert.ToString(siblingView.Cells["Favourite"].Value) != siblingState.Favourite || Convert.ToString(siblingView.Cells["Hidden"].Value) != siblingState.Hidden)) throw new Exception("Real sibling favourite/hidden columns mismatch");
                            }
                            int expanded = form.ResultsGrid.Rows.Count;
                            form.ToggleSiblings(parent + 1);
                            if (form.ResultsGrid.Rows.Count != expanded) throw new Exception("Sibling items must not expand recursively");
                            form.SortResults("Package", false);
                            parent = form.ResultsGrid.Rows.Cast<DataGridViewRow>().First(r => Object.ReferenceEquals(r.Tag, preview)).Index;
                            form.ToggleSiblings(parent);
                            if (form.ResultsGrid.Rows.Count != collapsed) throw new Exception("Sibling collapse failed");
                            form.CheckParent(preview, true);
                            form.CheckSibling(preview, preview.SiblingNames.First(), true);
                            if (preview.RedundantParents.Length > 0) form.CheckRedundant(preview, preview.RedundantParents.Last().Path);
                            form.Refresh();
                            using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(args[1], ImageFormat.Png); }
                            form.HoverListCell(parent, "Package"); await form.PreviewHoverNowAsync();
                            if (!form.PreviewVisible || !form.PreviewHasImage) throw new Exception("Real VAR hover preview failed");
                            form.SavePreviewImage(args[1] + ".thumbnail.png"); form.StopPreview();
                            int varSibling = Array.FindIndex(preview.DirectoryNames, n => n.EndsWith(".var", StringComparison.OrdinalIgnoreCase) && !String.Equals(n, preview.Name, StringComparison.OrdinalIgnoreCase));
                            bool siblingImage = false;
                            if (varSibling >= 0) {
                                int childRow = parent + 1 + specialRows + varSibling;
                                string siblingPath = Path.Combine(Path.GetDirectoryName(preview.FullPath), preview.DirectoryNames[varSibling]);
                                form.HoverListCell(childRow, "Package"); await form.PreviewHoverNowAsync();
                                if (!form.PreviewVisible || form.PreviewPath != siblingPath) throw new Exception("Real sibling VAR hover selected wrong path");
                                siblingImage = form.PreviewHasImage;
                                if (siblingImage) form.SavePreviewImage(args[1] + ".sibling-thumbnail.png");
                                form.StopPreview(); form.OpenRow(childRow);
                                if (opened != siblingPath) throw new Exception("Sibling open folder selected owner VAR instead");
                            }
                            int previewRows = form.ResultsGrid.Rows.Count;
                            foreach (var smart in form.CurrentResult.Rows.Where(r => !r.Missing && (r.IsDependency || (r.AutoSelectSiblings && r.RedundantParents.Length > 0))).Take(3)) {
                                form.ClearChecks();
                                form.SetResultSearch(smart.Name); form.CheckParent(smart, true);
                                if (smart.IsDependency && !smart.Marks.Contains("依赖")) throw new Exception("Dependency mark failed");
                                if (smart.AutoSelectSiblings && (!smart.SiblingNames.All(n => form.IsSiblingChecked(smart, n)) || (smart.RedundantParents.Length > 0 && form.SelectedRedundant(smart) != smart.RedundantParents.Last().Path))) throw new Exception("Real single-VAR automatic selection failed");
                                form.Refresh();
                                using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(args[1] + "." + smart.Name + ".png", ImageFormat.Png); }
                            }
                            form.ClearChecks();
                            bool sharedDirectoryChecked = false;
                            var sharedGroup = form.CurrentResult.Rows.Where(r => !r.Missing && r.DirectoryNames != null && !r.AutoSelectSiblings).GroupBy(r => Path.GetDirectoryName(r.FullPath), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
                            var firstShared = sharedGroup == null ? null : sharedGroup.First();
                            var secondShared = sharedGroup == null ? null : sharedGroup.Skip(1).First();
                            if (firstShared != null && secondShared != null) {
                                if (!firstShared.DirectoryNames.SequenceEqual(secondShared.DirectoryNames)) throw new Exception("Real same-directory lists differ");
                                form.SetResultSearch(Path.GetDirectoryName(firstShared.FullPath)); form.CheckParent(firstShared, true); form.CheckParent(secondShared, true);
                                if (form.CheckedCount != 2 || !form.IsSiblingChecked(firstShared, secondShared.Name) || !form.IsSiblingChecked(secondShared, firstShared.Name)) throw new Exception("Real top-level and peer selection did not synchronize");
                                foreach (var owner in new[] { firstShared, secondShared }) {
                                    int ownerIndex = form.ResultsGrid.Rows.Cast<DataGridViewRow>().Single(r => Object.ReferenceEquals(r.Tag, owner)).Index;
                                    var list = form.ResultsGrid.Rows.Cast<DataGridViewRow>().Skip(ownerIndex + 1).TakeWhile(r => !(r.Tag is PackageRow)).ToArray();
                                    foreach (var file in new[] { firstShared, secondShared }) {
                                        var mirror = list.Single(r => Convert.ToString(r.Cells["Package"].Value) == file.Name);
                                        if (!Object.Equals(mirror.Cells["Select"].Value, true) || Convert.ToString(mirror.Cells["FullPath"].Value) != file.FullPath || Convert.ToString(mirror.Cells["Favourite"].Value) != file.Favourite) throw new Exception("Real same-file mirror info or check differs");
                                    }
                                    form.ResultsGrid.FirstDisplayedScrollingRowIndex = ownerIndex; form.Refresh();
                                    using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(args[1] + "." + owner.Name + ".png", ImageFormat.Png); }
                                }
                                form.CheckSibling(firstShared, secondShared.Name, false);
                                if (form.CheckedCount != 1 || form.IsSiblingChecked(secondShared, secondShared.Name)) throw new Exception("Real peer uncheck failed to synchronize top row");
                                sharedDirectoryChecked = true; form.ClearChecks();
                            }
                            form.SetResultSearch("");
                            var scannedResult = form.CurrentResult;
                            form.SetResultFilters(false, false, false);
                            if (!Object.ReferenceEquals(scannedResult, form.CurrentResult) || form.ResultFilters.Any(v => v)
                                || form.ResultsGrid.Rows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<PackageRow>().Any(r => r.Missing || r.IsDependency || r.Hidden.StartsWith("是"))) throw new Exception("Real frontend default filters failed");
                            form.Refresh();
                            using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(args[1] + ".default.png", ImageFormat.Png); }
                            File.WriteAllText(args[1] + ".json", Scanner.Json().Serialize(new { Packages = form.CurrentResult.Rows.Count, DefaultVisiblePackages = form.VisiblePackageCount, PreviewRows = previewRows, OpenFolderPath = opened, RefreshPassed = true, SearchPassed = true, SortPassed = true, CheckboxStatePassed = true, HoverThumbnailPassed = true, SiblingHoverPassed = varSibling >= 0, SiblingThumbnailAvailable = siblingImage, SiblingExpandCollapsePassed = true, SiblingStatesPassed = true, SharedDirectoryChecked = sharedDirectoryChecked, SmartSelectionPassed = true, DependencyMarksPassed = true, FrontendFiltersPassed = true, SiblingNames = preview.SiblingNames, Passed = true }), Encoding.UTF8);
                        }
                        catch (Exception error) { File.WriteAllText(args[1] + ".json", Scanner.Json().Serialize(new { Error = error.ToString() }), Encoding.UTF8); Environment.ExitCode = 1; }
                        form.Close();
                    };
                }
                Application.Run(form);
            }
        }
    }
}
