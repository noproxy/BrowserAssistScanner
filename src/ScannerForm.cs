using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BrowserAssistScanner
{
    public sealed class ScannerForm : Form
    {
        private TextBox vamPath, packagesPath, tag, search, details;
        private Button refresh, cancel, export, warnings, deleteSelected, markHidden, clearSelected;
        private FlowLayoutPanel selectionBar;
        private TableLayoutPanel layout;
        private Label selectionCount;
        private CheckBox contains, showHidden, showMissing, showDependencies;
        private Label summary;
        private ToolStripStatusLabel status;
        private ToolStripProgressBar progress;
        private ResultsTable grid;
        private CancellationTokenSource cancellation;
        private ScanResult result;
        private Dictionary<string, PackageRow> rowLookup = new Dictionary<string, PackageRow>(StringComparer.OrdinalIgnoreCase);
        private bool scanning, deleting, hiding, rendering, closingAfterDelete;
        private readonly SelectionState selection = new SelectionState();
        private readonly bool smokeTest;
        private readonly HashSet<string> expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string sortColumn = "Package";
        private bool sortDescending;
        private int visiblePackages;
        private ThumbnailService thumbnailService;
        private ThumbnailPopup thumbnailPopup;
        private System.Windows.Forms.Timer hoverTimer;
        private string hoverPath, previewPath;
        private Point hoverPoint;
        private int hoverVersion;
        internal Func<string, Task<ThumbnailResult>> ThumbnailLoader;
        private sealed class SiblingDisplayRow
        {
            public PackageRow Owner;
            public string Name;
            public string Kind = "Sibling";
            public string Path;
        }
        private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrowserAssistScanner", "settings.json");
        public event EventHandler ScanFinished;
        internal event Action<DeleteReport> DeleteFinished;
        internal event Action<HideReport> HideFinished;
        internal Func<bool> ApplicationsRunning = RunningApplications.AnyRunning;
        internal Func<string, DialogResult> DeleteConfirmation;
        public Action<string> FolderOpener;
        public DataGridView ResultsGrid { get { return grid; } }
        public ScanResult CurrentResult { get { return result; } }
        public void SetResultSearch(string value) { search.Text = value; }
        internal void SetResultFilters(bool hidden, bool missing, bool dependencies) {
            showHidden.Checked = hidden; showMissing.Checked = missing; showDependencies.Checked = dependencies;
        }
        internal bool[] ResultFilters { get { return new[] { showHidden.Checked, showMissing.Checked, showDependencies.Checked }; } }
        public void ClickRefresh() { refresh.PerformClick(); }
        public void SortResults(string column, bool descending) { sortColumn = column; sortDescending = descending; ShowRows(); }
        public int VisiblePackageCount { get { return visiblePackages; } }
        public int CheckedCount { get { return selection.Count; } }
        public bool SelectionActionsVisible { get { return selectionBar.Visible; } }
        public bool IsSiblingChecked(PackageRow row, string name) { return selection.IsSiblingChecked(row, name); }
        public void CheckParent(PackageRow row, bool value) {
            selection.SetParent(row, value);
            if (value && !row.Missing) expanded.Add(row.FullPath);
            ShowRows();
        }
        public void CheckSibling(PackageRow row, string name, bool value) { selection.SetSibling(row, name, value); ShowRows(); }
        public void CheckRedundant(PackageRow row, string path) { selection.SetRedundant(row, path); ShowRows(); }
        public string SelectedRedundant(PackageRow row) { return selection.SelectedRedundant(row); }
        internal bool DispatchListKey(Keys key) { return grid.DispatchKey(key); }
        internal void ClickListCell(int row, string column) { grid.DispatchClick(row, column); }
        internal void DoubleClickListCell(int row, string column) { grid.DispatchDoubleClick(row, column); }
        internal bool PreviewVisible { get { return thumbnailPopup.Visible; } }
        internal bool PreviewHasImage { get { return thumbnailPopup.HasImage; } }
        internal string PreviewPath { get { return previewPath; } }
        internal void HoverListCell(int row, string column) { RequestPreview(row, grid.Columns[column].Index, new Point(400, 400)); }
        internal Task PreviewHoverNowAsync() { hoverTimer.Stop(); return ShowPreviewAsync(); }
        internal void StopPreview() { HidePreview(); }
        internal void SavePreviewImage(string path) { using (var image = new Bitmap(thumbnailPopup.Width, thumbnailPopup.Height)) { thumbnailPopup.DrawToBitmap(image, thumbnailPopup.ClientRectangle); image.Save(path); } }
        public void ClearChecks() { selection.Clear(); ShowRows(); }
        internal void ClickDelete() { deleteSelected.PerformClick(); }
        internal void ClickHide() { markHidden.PerformClick(); }
        internal Button HideButton { get { return markHidden; } }
        internal void SetScanOptions(ScanOptions options) {
            vamPath.Text = options.VamPath; packagesPath.Text = options.PackagesPath; tag.Text = options.Tag;
            contains.Checked = options.Contains;
        }

        public ScannerForm(bool smokeTest)
        {
            this.smokeTest = smokeTest;
            Text = "BrowserAssist 标签扫描器";
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(245, 247, 250);
            ForeColor = Color.FromArgb(32, 45, 61);
            Size = new Size(1440, 870);
            MinimumSize = new Size(1180, 650);
            StartPosition = FormStartPosition.CenterScreen;
            BuildUi();
            thumbnailService = new ThumbnailService(); ThumbnailLoader = thumbnailService.GetAsync;
            thumbnailPopup = new ThumbnailPopup(smokeTest);
            hoverTimer = new System.Windows.Forms.Timer { Interval = 400 };
            hoverTimer.Tick += async delegate { hoverTimer.Stop(); await ShowPreviewAsync(); };
            Deactivate += delegate { HidePreview(); };
            if (!smokeTest) LoadSettings();
            FolderOpener = OpenExplorer;
            DeleteConfirmation = message => MessageBox.Show(this, message, "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            Shown += delegate { StartScan(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (cancellation != null) cancellation.Cancel();
                if (deleting) { closingAfterDelete = true; e.Cancel = true; status.Text = hiding ? "正在停止隐藏操作，请稍候…" : "正在停止回收操作，请稍候…"; }
            };
        }

        private static Button Button(string text)
        {
            return new Button { Text = text, AutoSize = false, Width = 98, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Color.White, Margin = new Padding(4, 2, 4, 2) };
        }

        private void BuildUi()
        {
            layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 8), ColumnCount = 1, RowCount = 8 };
            foreach (int height in new[] { 50, 92, 46, 74 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 115));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            Controls.Add(layout);
            var heading = new Label { Text = "BrowserAssist 标签扫描器", Font = new Font(Font.FontFamily, 18, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            layout.Controls.Add(heading, 0, 0);
            var paths = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
            paths.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); paths.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            vamPath = new TextBox { Text = @"G:\VAM-1.22.0.8", Dock = DockStyle.Fill, Margin = new Padding(0, 8, 5, 8) };
            packagesPath = new TextBox { Text = @"G:\AddonPackages", Dock = DockStyle.Fill, Margin = new Padding(0, 8, 5, 8) };
            paths.Controls.Add(new Label { Text = "VAM 根目录", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            paths.Controls.Add(new Label { Text = "VAR 扫描目录", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
            paths.Controls.Add(vamPath, 1, 0); paths.Controls.Add(packagesPath, 1, 1);
            var chooseVam = Button("选择目录…"); chooseVam.Click += delegate { PickFolder(vamPath); };
            var choosePackages = Button("选择目录…"); choosePackages.Click += delegate { PickFolder(packagesPath); };
            paths.Controls.Add(chooseVam, 2, 0); paths.Controls.Add(choosePackages, 2, 1);
            layout.Controls.Add(paths, 0, 1);
            var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
            controls.Controls.Add(new Label { Text = "标签", Width = 42, Height = 32, TextAlign = ContentAlignment.MiddleLeft });
            tag = new TextBox { Text = "del", Width = 160, Margin = new Padding(0, 5, 12, 0) };
            tag.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; StartScan(); } };
            contains = new CheckBox { Text = "包含匹配", AutoSize = true, Height = 32, Margin = new Padding(0, 7, 14, 0) };
            refresh = Button("刷新扫描"); refresh.BackColor = Color.FromArgb(36, 71, 106); refresh.ForeColor = Color.White; refresh.Click += delegate { StartScan(); };
            cancel = Button("停止"); cancel.Enabled = false; cancel.Click += delegate { if (cancellation != null) cancellation.Cancel(); };
            export = Button("导出 CSV"); export.Enabled = false; export.Click += delegate { ExportRows(); };
            warnings = Button("扫描提示"); warnings.Enabled = false; warnings.Click += delegate { ShowWarnings(); };
            controls.Controls.AddRange(new Control[] { tag, contains, refresh, cancel, export, warnings });
            layout.Controls.Add(controls, 0, 2);
            var filterBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
            filterBar.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            filterBar.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            filterBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); filterBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70)); filterBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            summary = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Text = "标签留空可扫描所有带标签的包。" };
            search = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 7, 0, 7) }; search.TextChanged += delegate { ShowRows(); };
            filterBar.Controls.Add(summary, 0, 0); filterBar.Controls.Add(new Label { Text = "结果搜索", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 1, 0); filterBar.Controls.Add(search, 2, 0);
            var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            showHidden = new CheckBox { Text = "显示隐藏", AutoSize = true, Margin = new Padding(0, 7, 18, 0) };
            showMissing = new CheckBox { Text = "显示未找到的包", AutoSize = true, Margin = new Padding(0, 7, 18, 0) };
            showDependencies = new CheckBox { Text = "显示标记为依赖的包", AutoSize = true, Margin = new Padding(0, 7, 18, 0) };
            foreach (var filter in new[] { showHidden, showMissing, showDependencies }) {
                filter.CheckedChanged += delegate { ShowRows(); };
                filters.Controls.Add(filter);
            }
            filterBar.Controls.Add(filters, 0, 1); filterBar.SetColumnSpan(filters, 3);
            layout.Controls.Add(filterBar, 0, 3);
            grid = new ResultsTable {
                Dock = DockStyle.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = false,
                AutoGenerateColumns = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false, RowHeadersVisible = false, EnableHeadersVisualStyles = false,
                AllowUserToResizeRows = false, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None, RowTemplate = { Height = 32, Resizable = DataGridViewTriState.False }
            };
            grid.ListKey = HandleListKey;
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(36, 71, 106), ForeColor = Color.White, Font = new Font(Font, FontStyle.Bold), Alignment = DataGridViewContentAlignment.MiddleLeft };
            grid.ColumnHeadersHeight = 36;
            grid.DefaultCellStyle = new DataGridViewCellStyle { Padding = new Padding(5, 0, 5, 0), SelectionBackColor = Color.FromArgb(218, 233, 246), SelectionForeColor = Color.FromArgb(20, 35, 55) };
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(245, 248, 251);
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.GridColor = Color.FromArgb(231, 235, 240);
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Select", HeaderText = "选", Width = 42, Frozen = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Expand", HeaderText = "", Width = 30, Frozen = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Package", HeaderText = "包名", Width = 275, Frozen = true, SortMode = DataGridViewColumnSortMode.Automatic });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Marks", HeaderText = "提示标记", Width = 215 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Favourite", HeaderText = "是否收藏", Width = 95, SortMode = DataGridViewColumnSortMode.Automatic });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Hidden", HeaderText = "是否隐藏", Width = 95, SortMode = DataGridViewColumnSortMode.Automatic });
            grid.Columns.Add(new DataGridViewLinkColumn { Name = "FullPath", HeaderText = "完整路径（点击打开文件夹）", MinimumWidth = 300, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, LinkColor = Color.FromArgb(30, 99, 161), ActiveLinkColor = Color.FromArgb(15, 70, 125), VisitedLinkColor = Color.FromArgb(30, 99, 161), TrackVisitedState = false, SortMode = DataGridViewColumnSortMode.Automatic });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Siblings", HeaderText = "同级项数", Width = 95 });
            foreach (DataGridViewColumn column in grid.Columns) {
                column.SortMode = column.Name == "Select" || column.Name == "Expand" ? DataGridViewColumnSortMode.NotSortable : DataGridViewColumnSortMode.Programmatic;
                column.ReadOnly = column.Name != "Select";
            }
            grid.CurrentCellDirtyStateChanged += delegate { if (grid.IsCurrentCellDirty && grid.CurrentCell.ColumnIndex == 0) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += OnCheckChanged;
            grid.ColumnHeaderMouseClick += delegate(object sender, DataGridViewCellMouseEventArgs e) {
                string column = grid.Columns[e.ColumnIndex].Name;
                if (column == "Select" || column == "Expand") return;
                SortResults(column, sortColumn == column && !sortDescending);
            };
            grid.CellContentClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0 && e.ColumnIndex == grid.Columns["FullPath"].Index) OpenRow(e.RowIndex); };
            grid.CellClick += delegate(object sender, DataGridViewCellEventArgs e) {
                if (e.RowIndex < 0) return;
                if (e.ColumnIndex == 1) ToggleSiblings(e.RowIndex);
                var child = grid.Rows[e.RowIndex].Tag as SiblingDisplayRow;
                if (e.ColumnIndex == 0 && child != null && child.Kind == "ParentChoice") ToggleRedundant(child);
            };
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && new[] { "Package", "Favourite", "Hidden" }.Contains(grid.Columns[e.ColumnIndex].Name)) OpenRow(e.RowIndex); };
            grid.CellMouseEnter += delegate(object sender, DataGridViewCellEventArgs e) {
                var row = e.RowIndex >= 0 ? grid.Rows[e.RowIndex].Tag as PackageRow : null;
                grid.Cursor = e.ColumnIndex == 1 && row != null && !row.Missing ? Cursors.Hand : Cursors.Default;
            };
            grid.MouseMove += delegate(object sender, MouseEventArgs e) { var hit = grid.HitTest(e.X, e.Y); RequestPreview(hit.RowIndex, hit.ColumnIndex, grid.PointToScreen(e.Location)); };
            grid.MouseLeave += delegate { HidePreview(); };
            grid.Scroll += delegate { HidePreview(); };
            grid.SelectionChanged += delegate { UpdateDetails(); };
            grid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e) {
                var row = grid.Rows[e.RowIndex].Tag as PackageRow;
                if (row == null) return;
                if (row.Missing) {
                    e.CellStyle.ForeColor = Color.FromArgb(145, 105, 42);
                    if (e.ColumnIndex == grid.Columns["FullPath"].Index) {
                        e.Value = row.FullPath; e.FormattingApplied = true;
                        e.CellStyle.ForeColor = Color.FromArgb(145, 105, 42);
                    }
                }
            };
            var context = new ContextMenuStrip();
            context.Items.Add("打开所在文件夹", null, delegate { if (grid.CurrentRow != null) OpenRow(grid.CurrentRow.Index); });
            context.Items.Add("复制完整路径", null, delegate { var path = grid.CurrentRow == null ? null : DiskPath(grid.CurrentRow.Index); if (path != null) Clipboard.SetText(path); });
            grid.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Right) { var hit = grid.HitTest(e.X, e.Y); if (hit.RowIndex >= 0) grid.CurrentCell = grid.Rows[hit.RowIndex].Cells[0]; } };
            grid.ContextMenuStrip = context;
            layout.Controls.Add(grid, 0, 4);
            details = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 12, 0, 4), Text = "选择一行查看标签和包内资源。" };
            layout.Controls.Add(details, 0, 5);
            selectionBar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Visible = false };
            selectionCount = new Label { AutoSize = true, Margin = new Padding(0, 8, 16, 0) };
            deleteSelected = Button("删除选中文件"); deleteSelected.Width = 140; deleteSelected.ForeColor = Color.FromArgb(175, 45, 45);
            markHidden = Button("标记为隐藏"); markHidden.Width = 140;
            clearSelected = Button("全部取消勾选"); clearSelected.Width = 140;
            deleteSelected.Click += delegate { DeleteChecked(); };
            markHidden.Click += delegate { HideChecked(); };
            clearSelected.Click += delegate { ClearChecks(); };
            selectionBar.Controls.AddRange(new Control[] { selectionCount, deleteSelected, markHidden, clearSelected });
            layout.Controls.Add(selectionBar, 0, 6);
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(101, 115, 132), Text = "← 收起所属列表，→ 展开顶层行，空格切换勾选。选择冗余父目录会勾选全部同级项；取消任意同级项会取消父目录选择。" }, 0, 7);
            var statusBar = new StatusStrip { SizingGrip = false };
            status = new ToolStripStatusLabel { Text = "准备扫描", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            progress = new ToolStripProgressBar { Width = 180, Style = ProgressBarStyle.Marquee, Visible = false };
            statusBar.Items.Add(status); statusBar.Items.Add(progress); Controls.Add(statusBar);
            statusBar.Dock = DockStyle.Bottom;
            AcceptButton = refresh;
        }

        private PackageRow SelectedRow() {
            if (grid.CurrentRow == null) return null;
            var sibling = grid.CurrentRow.Tag as SiblingDisplayRow;
            return sibling != null ? sibling.Owner : grid.CurrentRow.Tag as PackageRow;
        }
        private static string DisplayKey(object tag) {
            var row = tag as PackageRow;
            if (row != null) return "Package\0" + row.FullPath;
            var child = tag as SiblingDisplayRow;
            return child == null ? null : child.Kind + "\0" + child.Owner.FullPath + "\0" + child.Name;
        }
        private void ToggleRedundant(SiblingDisplayRow child) {
            if (scanning || deleting || !selection.HasSelections(child.Owner)) return;
            selection.SetRedundant(child.Owner, String.Equals(selection.SelectedRedundant(child.Owner), child.Path, StringComparison.OrdinalIgnoreCase) ? null : child.Path);
            ShowRows();
        }
        private bool HandleListKey(Keys key) {
            if (key != Keys.Left && key != Keys.Right && key != Keys.Space) return false;
            if (scanning || deleting || grid.CurrentRow == null) return true;
            var row = grid.CurrentRow.Tag as PackageRow;
            var child = grid.CurrentRow.Tag as SiblingDisplayRow;
            var owner = row ?? (child == null ? null : child.Owner);
            if (owner == null || owner.Missing) return true;
            if (key == Keys.Left) { expanded.Remove(owner.FullPath); ShowRows(); return true; }
            if (key == Keys.Right) { if (row != null) { expanded.Add(row.FullPath); ShowRows(); } return true; }
            if (row != null) {
                bool value = !selection.IsParentChecked(row);
                selection.SetParent(row, value); if (value) expanded.Add(row.FullPath);
            } else if (child.Kind == "Sibling") selection.SetSibling(owner, child.Name, !selection.IsSiblingChecked(owner, child.Name));
            else if (child.Kind == "ParentChoice") { ToggleRedundant(child); return true; }
            ShowRows(); return true;
        }
        private void UpdateDetails() {
            var row = SelectedRow();
            string path = grid.CurrentRow == null ? null : DiskPath(grid.CurrentRow.Index);
            PackageRow actual;
            if (path != null && rowLookup.TryGetValue(path, out actual)) row = actual;
            details.Text = row == null ? "选择一行查看标签和包内资源。" : row.Details;
        }
        private void PickFolder(TextBox input)
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择目录", SelectedPath = Directory.Exists(input.Text) ? input.Text : "", ShowNewFolderButton = false })
                if (dialog.ShowDialog(this) == DialogResult.OK) input.Text = dialog.SelectedPath;
        }

        public async void StartScan() { await ScanAsync(); }
        private async Task<bool> ScanAsync()
        {
            if (scanning || deleting) return false;
            bool completed = false;
            var options = new ScanOptions { VamPath = vamPath.Text, PackagesPath = packagesPath.Text, Tag = tag.Text, Contains = contains.Checked, IncludeMissing = true };
            scanning = true; SetBusy(true); cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            status.Text = "开始扫描…";
            var timer = Stopwatch.StartNew();
            long lastReport = -200;
            var reporter = new Progress<string>(delegate(string text) { if (!IsDisposed && scanning) status.Text = text; });
            try
            {
                var scanned = await Task.Run(() => Scanner.Scan(options, token, delegate(string text) {
                    long now = timer.ElapsedMilliseconds;
                    if (now - lastReport >= 150) { lastReport = now; ((IProgress<string>)reporter).Report(text); }
                }), token);
                if (IsDisposed) return false;
                result = scanned;
                selection.Reconcile(result.Rows);
                expanded.IntersectWith(result.Rows.Where(r => !r.Missing).Select(r => r.FullPath));
                ShowRows();
                if (!smokeTest) SaveSettings(options);
                status.Text = "扫描完成，用时 " + timer.Elapsed.TotalSeconds.ToString("0.0") + " 秒" + (result.Warnings.Count > 0 ? "；有 " + result.Warnings.Count + " 条扫描提示，结果可能不完整。" : "。");
                warnings.Text = "扫描提示 " + result.Warnings.Count;
                completed = true;
            }
            catch (OperationCanceledException) { if (!IsDisposed) status.Text = "扫描已停止，保留上次完成的结果。"; }
            catch (Exception error) { if (!IsDisposed) { status.Text = "扫描失败：" + error.Message; if (smokeTest) throw; MessageBox.Show(this, error.Message, "无法扫描", MessageBoxButtons.OK, MessageBoxIcon.Warning); } }
            finally
            {
                scanning = false;
                cancellation.Dispose(); cancellation = null;
                if (!IsDisposed) { SetBusy(false); if (ScanFinished != null) ScanFinished(this, EventArgs.Empty); }
            }
            return completed;
        }

        private void SetBusy(bool busy)
        {
            if (busy) HidePreview();
            refresh.Enabled = !busy; cancel.Enabled = busy; progress.Visible = busy;
            export.Enabled = !busy && result != null; warnings.Enabled = !busy && result != null && result.Warnings.Count > 0;
            vamPath.Enabled = packagesPath.Enabled = tag.Enabled = contains.Enabled = !busy;
            showHidden.Enabled = showMissing.Enabled = showDependencies.Enabled = !deleting;
            grid.Enabled = search.Enabled = deleteSelected.Enabled = clearSelected.Enabled = !busy;
            markHidden.Enabled = !busy && HideEngine.SelectedVars(selection.Snapshot()).Length > 0;
            foreach (Control panel in layout.Controls) if (panel is TableLayoutPanel && panel != layout) {
                foreach (Control control in panel.Controls) if (control is Button) control.Enabled = !busy;
            }
        }

        private void OnCheckChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (rendering || scanning || deleting || e.ColumnIndex != 0 || e.RowIndex < 0) return;
            var view = grid.Rows[e.RowIndex];
            bool value = Object.Equals(view.Cells[0].Value, true);
            var row = view.Tag as PackageRow;
            var sibling = view.Tag as SiblingDisplayRow;
            if (row != null) {
                selection.SetParent(row, value);
                if (value && !row.Missing) expanded.Add(row.FullPath);
            }
            else if (sibling != null && sibling.Kind == "Sibling") selection.SetSibling(sibling.Owner, sibling.Name, value);
            // Rebuilding rows inside the edit callback is prohibited by DataGridView.
            BeginInvoke(new Action(delegate {
                if (IsDisposed || scanning || deleting) return;
                int first = grid.FirstDisplayedScrollingRowIndex;
                ShowRows();
                if (first >= 0 && first < grid.Rows.Count) grid.FirstDisplayedScrollingRowIndex = first;
            }));
        }

        private async void HideChecked()
        {
            if (scanning || deleting) return;
            var request = selection.Snapshot();
            if (HideEngine.SelectedVars(request).Length == 0) return;
            string vam = vamPath.Text;
            deleting = hiding = true; cancellation = new CancellationTokenSource(); SetBusy(true);
            status.Text = "正在标记选中的 VAR 为隐藏…";
            var reporter = new Progress<string>(text => { if (!IsDisposed && hiding) status.Text = text; });
            HideReport report;
            try { report = await HideEngine.HideAsync(request, vam, cancellation.Token, text => ((IProgress<string>)reporter).Report(text)); }
            catch (Exception error) { report = new HideReport(); report.Issues.Add(error.Message); }
            finally { deleting = hiding = false; cancellation.Dispose(); cancellation = null; SetBusy(false); }
            selection.RemoveCompletedFiles(report.Completed);
            if (closingAfterDelete) { Close(); return; }
            bool refreshed = await ScanAsync();
            if (IsDisposed) return;
            status.Text = "隐藏完成：" + report.Completed.Count + " 个 VAR，忽略 " + report.Ignored + " 个文件 / 文件夹，" + report.Issues.Count + " 条未完成提示" + (report.Cancelled ? "（已停止）" : "") + (refreshed ? "；列表已重新扫描。" : "；列表刷新未完成，请重新刷新。");
            if (report.Issues.Count > 0 && !smokeTest) {
                using (var dialog = new Form { Text = "隐藏结果：未完成的项目", Size = new Size(950, 500), StartPosition = FormStartPosition.CenterParent, Font = Font }) {
                    dialog.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = String.Join("\r\n\r\n", report.Issues) });
                    dialog.ShowDialog(this);
                }
            }
            if (HideFinished != null) HideFinished(report);
        }

        private async void DeleteChecked()
        {
            if (scanning || deleting || selection.Count == 0) return;
            DeleteRequest request = selection.Snapshot();
            bool postOrder;
            try { postOrder = ApplicationsRunning(); }
            catch (Exception error) {
                MessageBox.Show(this, "无法检查运行程序：" + error.Message, "删除未开始", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (postOrder && DeleteConfirmation("检测到VAM正在运行中， 删除速度较慢，是否确认删除？") != DialogResult.Yes) return;
            deleting = true; cancellation = new CancellationTokenSource(); SetBusy(true);
            status.Text = postOrder ? "正在逐项递归移入回收站…" : "正在通过 Windows 回收选中的顶层项目…";
            var reporter = new Progress<string>(delegate(string text) { if (!IsDisposed && deleting) status.Text = text; });
            DeleteReport report = null;
            try { report = await DeleteEngine.DeleteAsync(request, postOrder, cancellation.Token, text => ((IProgress<string>)reporter).Report(text)); }
            catch (Exception error) { report = new DeleteReport(); report.Issues.Add(error.Message); }
            finally { deleting = false; cancellation.Dispose(); cancellation = null; SetBusy(false); }
            if (closingAfterDelete) { Close(); return; }
            bool refreshed = await ScanAsync();
            if (IsDisposed) return;
            status.Text = "回收完成：" + report.Recycled + " 项已进入回收站，" + report.Missing + " 项已不存在，" + report.Issues.Count + " 条未完成提示" + (report.Cancelled ? "（已停止）" : "") + (refreshed ? "；列表已重新扫描。" : "；列表刷新未完成，请重新刷新。");
            if (report.Issues.Count > 0 && !smokeTest) {
                using (var dialog = new Form { Text = "回收结果：未完成的项目", Size = new Size(950, 500), StartPosition = FormStartPosition.CenterParent, Font = Font }) {
                    dialog.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = String.Join("\r\n\r\n", report.Issues) });
                    dialog.ShowDialog(this);
                }
            }
            if (DeleteFinished != null) DeleteFinished(report);
        }

        private void ShowRows()
        {
            if (result == null) return;
            rowLookup = result.Rows.Where(r => !r.Missing).ToDictionary(r => r.FullPath, StringComparer.OrdinalIgnoreCase);
            HidePreview();
            var selected = SelectedRow();
            string selectedKey = grid.CurrentRow == null ? null : DisplayKey(grid.CurrentRow.Tag);
            string selectedColumn = grid.CurrentCell == null ? "Select" : grid.CurrentCell.OwningColumn.Name;
            int first = grid.FirstDisplayedScrollingRowIndex;
            string query = search.Text.Trim();
            var rows = result.Rows.Where(r => (showHidden.Checked || !(r.Hidden ?? "").StartsWith("是", StringComparison.Ordinal))
                && (showMissing.Checked || !r.Missing) && (showDependencies.Checked || !r.IsDependency)
                && (query.Length == 0 || (r.Name + " " + r.FullPath + " " + r.Tags + " " + r.Marks).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            rows.Sort(delegate(PackageRow a, PackageRow b) {
                int comparison;
                if (sortColumn == "Siblings") comparison = Nullable.Compare(a.SiblingCount, b.SiblingCount);
                else {
                    string left = sortColumn == "Marks" ? a.Marks : sortColumn == "Favourite" ? a.Favourite : sortColumn == "Hidden" ? a.Hidden : sortColumn == "FullPath" ? a.FullPath : a.Name;
                    string right = sortColumn == "Marks" ? b.Marks : sortColumn == "Favourite" ? b.Favourite : sortColumn == "Hidden" ? b.Hidden : sortColumn == "FullPath" ? b.FullPath : b.Name;
                    comparison = StringComparer.OrdinalIgnoreCase.Compare(left, right);
                }
                if (comparison == 0) comparison = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
                return sortDescending ? -comparison : comparison;
            });
            visiblePackages = rows.Count;
            rendering = true;
            grid.SuspendLayout(); grid.Rows.Clear();
            int parentIndex = -1;
            foreach (var row in rows)
            {
                string count = row.Missing ? "—" : !row.SiblingCount.HasValue ? "未知" : row.SiblingCount.Value.ToString();
                int i = grid.Rows.Add(selection.IsParentChecked(row), row.Missing ? "" : expanded.Contains(row.FullPath) ? "▼" : "▶", row.Name, row.Marks ?? "", row.Favourite, row.Hidden, row.FullPath, count);
                var view = grid.Rows[i]; view.Tag = row;
                if (selected != null && String.Equals(row.FullPath, selected.FullPath, StringComparison.OrdinalIgnoreCase)) parentIndex = i;
                if (row.Missing) { view.Cells[0] = new DataGridViewTextBoxCell { Value = "" }; view.Cells[0].ReadOnly = true; view.Cells["FullPath"] = new DataGridViewTextBoxCell { Value = row.FullPath }; }
                view.Cells["FullPath"].ToolTipText = row.Missing ? "未找到文件，无法打开" : row.FullPath + "\r\n点击打开文件所在文件夹并选中文件";
                view.Cells["Expand"].ToolTipText = "展开 / 收起同级项（← 收起，→ 展开）";
                view.Cells["Expand"].Style.Padding = new Padding(0);
                view.Cells["Expand"].Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
                view.Cells["Siblings"].ToolTipText = row.Missing ? "未找到文件，无法统计同级项" : row.SiblingError ?? "数量不含当前 VAR；展开列表包含当前 VAR，以便同目录各列表保持一致。";
                view.Cells["Select"].ToolTipText = row.SelectionReason ?? "未找到文件";
                view.Cells["Marks"].ToolTipText = row.Details;
                if (!String.IsNullOrEmpty(row.Marks)) {
                    view.Cells["Marks"].Style.BackColor = Color.FromArgb(255, 235, 198);
                    view.Cells["Marks"].Style.ForeColor = Color.FromArgb(161, 51, 15);
                    view.Cells["Marks"].Style.SelectionForeColor = Color.FromArgb(161, 51, 15);
                    view.Cells["Marks"].Style.Font = grid.ColumnHeadersDefaultCellStyle.Font;
                }
                if (!row.Missing && expanded.Contains(row.FullPath)) {
                    RedundantParent[] candidates = row.RedundantParents ?? new RedundantParent[0];
                    AddChild(new SiblingDisplayRow { Owner = row, Kind = "ParentHeading", Name = "冗余父目录" }, "冗余父目录：" + (candidates.Length == 0 ? "无" : ""), false);
                    foreach (var candidate in candidates) {
                        var child = AddChild(new SiblingDisplayRow { Owner = row, Kind = "ParentChoice", Name = candidate.Path, Path = candidate.Path }, candidate.Label, false);
                        child.Cells[0] = new RadioCell { Active = selection.HasSelections(row) && row.SiblingNames != null, Value = String.Equals(selection.SelectedRedundant(row), candidate.Path, StringComparison.OrdinalIgnoreCase) };
                        child.Cells[0].ReadOnly = true;
                        child.Cells[0].ToolTipText = "单选回收层级；再次点击取消。自动勾选全部同级项，删除后逐层回收空目录。";
                        child.Cells["Package"].ToolTipText = candidate.Path;
                    }
                    foreach (string name in row.DirectoryNames ?? new[] { Path.GetFileName(row.FullPath) })
                        AddChild(new SiblingDisplayRow { Owner = row, Name = name }, name, selection.HasSelections(row) || rowLookup.ContainsKey(Path.Combine(Path.GetDirectoryName(row.FullPath), name.TrimEnd('\\', '/'))));
                }
            }
            foreach (DataGridViewColumn column in grid.Columns)
                column.HeaderCell.SortGlyphDirection = column.Name == sortColumn ? (sortDescending ? SortOrder.Descending : SortOrder.Ascending) : SortOrder.None;
            var sameRow = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => selectedKey != null && DisplayKey(r.Tag) == selectedKey);
            if (sameRow != null) grid.CurrentCell = sameRow.Cells[selectedColumn];
            else if (parentIndex >= 0) grid.CurrentCell = grid.Rows[parentIndex].Cells[selectedColumn];
            if (first >= 0 && grid.Rows.Count > 0) grid.FirstDisplayedScrollingRowIndex = Math.Min(first, grid.Rows.Count - 1);
            grid.ResumeLayout();
            rendering = false;
            int checkedCount = selection.Count;
            selectionCount.Text = "已勾选 " + checkedCount + " 个文件 / 文件夹";
            selectionBar.Visible = checkedCount > 0;
            markHidden.Enabled = !scanning && !deleting && HideEngine.SelectedVars(selection.Snapshot()).Length > 0;
            layout.RowStyles[6].Height = checkedCount > 0 ? 42 : 0;
            summary.Text = String.Format("{0} 个包系列 · {1} 个 VAR 文件 · {2} 个未找到 · 当前显示 {3} 个包", result.Groups, result.FileCount, result.MissingGroups, rows.Count);
            UpdateDetails();
        }

        private DataGridViewRow AddChild(SiblingDisplayRow child, string label, bool showCheck)
        {
            PackageState state = null;
            if (child.Kind == "Sibling") child.Owner.SiblingStates.TryGetValue(child.Name, out state);
            string path = child.Kind == "Sibling" ? Path.Combine(Path.GetDirectoryName(child.Owner.FullPath), child.Name.TrimEnd('\\', '/')) : "";
            PackageRow actual = null;
            if (path.Length > 0 && rowLookup.TryGetValue(path, out actual) && state == null) state = new PackageState { Favourite = actual.Favourite, Hidden = actual.Hidden };
            string favourite = child.Kind != "Sibling" ? "" : state == null ? "—" : state.Favourite;
            string hidden = child.Kind != "Sibling" ? "" : state == null ? "—" : state.Hidden;
            string count = actual == null ? "" : actual.SiblingCount.HasValue ? actual.SiblingCount.Value.ToString() : "未知";
            int index = grid.Rows.Add(selection.IsSiblingChecked(child.Owner, child.Name), "", label, actual == null ? "" : actual.Marks, favourite, hidden, path, count);
            var view = grid.Rows[index]; view.Tag = child;
            if (!showCheck) { view.Cells[0] = new DataGridViewTextBoxCell { Value = "" }; view.Cells[0].ReadOnly = true; }
            if (child.Kind != "Sibling") view.Cells["FullPath"] = new DataGridViewTextBoxCell { Value = "" };
            else view.Cells["FullPath"].ToolTipText = path + "\r\n点击打开所在文件夹并选中该项";
            view.DefaultCellStyle.BackColor = Color.FromArgb(238, 243, 248);
            view.DefaultCellStyle.ForeColor = Color.FromArgb(89, 105, 123);
            view.Cells["Package"].Style.WrapMode = DataGridViewTriState.False;
            view.Cells["Package"].ToolTipText = label;
            if (actual != null) {
                view.Cells["Marks"].ToolTipText = actual.Details;
                if (!String.IsNullOrEmpty(actual.Marks)) {
                    view.Cells["Marks"].Style.BackColor = Color.FromArgb(255, 235, 198);
                    view.Cells["Marks"].Style.ForeColor = Color.FromArgb(161, 51, 15);
                    view.Cells["Marks"].Style.SelectionForeColor = Color.FromArgb(161, 51, 15);
                    view.Cells["Marks"].Style.Font = grid.ColumnHeadersDefaultCellStyle.Font;
                }
            }
            if (child.Kind == "Sibling" && state == null) {
                view.Cells["Favourite"].ToolTipText = view.Cells["Hidden"].ToolTipText = "文件夹和非 VAR 文件不适用 VAM 收藏 / 隐藏标记。";
            }
            view.Cells["Package"].Style.Padding = new Padding(child.Kind == "ParentHeading" ? 5 : 18, 0, 5, 0);
            if (child.Kind == "ParentHeading") view.Cells["Package"].Style.ForeColor = Color.FromArgb(36, 71, 106);
            view.Height = 32; view.Resizable = DataGridViewTriState.False;
            return view;
        }

        public void ToggleSiblings(int index)
        {
            var row = grid.Rows[index].Tag as PackageRow;
            if (row == null) return;
            if (row.Missing) { status.Text = "未找到文件，无法统计同级内容。"; return; }
            int first = grid.FirstDisplayedScrollingRowIndex;
            if (!expanded.Add(row.FullPath)) expanded.Remove(row.FullPath);
            grid.CurrentCell = grid.Rows[index].Cells[0];
            ShowRows();
            if (first >= 0 && first < grid.Rows.Count) grid.FirstDisplayedScrollingRowIndex = first;
        }

        public void OpenRow(int index)
        {
            string path = DiskPath(index);
            if (path == null) return;
            if (!File.Exists(path) && !Directory.Exists(path)) { status.Text = "文件或目录已移动或删除，请刷新扫描：" + path; return; }
            HidePreview();
            try { FolderOpener(path); }
            catch (Exception error) { MessageBox.Show(this, error.Message, "无法打开文件夹", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private string DiskPath(int index) {
            if (index < 0 || index >= grid.Rows.Count) return null;
            var row = grid.Rows[index].Tag as PackageRow;
            if (row != null) return row.Missing ? null : row.FullPath;
            var child = grid.Rows[index].Tag as SiblingDisplayRow;
            if (child == null || child.Kind != "Sibling") return null;
            return Path.Combine(Path.GetDirectoryName(child.Owner.FullPath), child.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        private void RequestPreview(int row, int column, Point pointer) {
            string path = column == grid.Columns["Package"].Index ? DiskPath(row) : null;
            if (path != null && !ThumbnailService.IsVar(path)) path = null;
            var child = row >= 0 && row < grid.Rows.Count ? grid.Rows[row].Tag as SiblingDisplayRow : null;
            if (child != null && (child.Kind != "Sibling" || child.Name.EndsWith(Path.DirectorySeparatorChar.ToString()))) path = null;
            if (scanning || deleting) path = null;
            if (String.Equals(path, hoverPath, StringComparison.OrdinalIgnoreCase)) return;
            HidePreview();
            if (path == null) return;
            hoverPath = path; hoverPoint = pointer; grid.ShowCellToolTips = false; hoverTimer.Start();
        }
        private async Task ShowPreviewAsync() {
            string path = hoverPath; int version = hoverVersion;
            if (path == null || IsDisposed || Disposing || scanning || deleting) return;
            previewPath = path; thumbnailPopup.Loading(this, path, hoverPoint);
            ThumbnailResult image = null;
            try { image = await ThumbnailLoader(path); }
            catch (OperationCanceledException) { }
            catch { image = new ThumbnailResult(); }
            if (image == null) return;
            if (IsDisposed || Disposing || thumbnailPopup.IsDisposed || version != hoverVersion || hoverPath != path) { image.Dispose(); return; }
            thumbnailPopup.SetThumbnail(image);
        }
        private void HidePreview() {
            hoverVersion++; hoverPath = previewPath = null;
            if (hoverTimer != null) hoverTimer.Stop();
            if (thumbnailPopup != null && !thumbnailPopup.IsDisposed) thumbnailPopup.HidePreview();
            if (grid != null && !grid.IsDisposed) grid.ShowCellToolTips = true;
        }
        protected override void Dispose(bool disposing) {
            if (disposing) {
                HidePreview();
                if (hoverTimer != null) hoverTimer.Dispose();
                if (thumbnailService != null) thumbnailService.Dispose();
                if (thumbnailPopup != null) thumbnailPopup.Dispose();
            }
            base.Dispose(disposing);
        }

        private static void OpenExplorer(string path)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
        }

        private void ExportRows()
        {
            using (var dialog = new SaveFileDialog { Filter = "CSV 表格 (*.csv)|*.csv", FileName = "BrowserAssist-标签包清单.csv", OverwritePrompt = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true)))
                    {
                        writer.WriteLine("包名,提示标记,是否收藏,是否隐藏,完整路径,同级项数");
                        foreach (DataGridViewRow viewRow in grid.Rows)
                        {
                            var row = viewRow.Tag as PackageRow;
                            if (row == null) continue;
                            string count = row.Missing ? "—" : row.SiblingCount.HasValue ? row.SiblingCount.Value.ToString() : "未知";
                            writer.WriteLine(String.Join(",", new[] { row.Name, row.Marks ?? "", row.Favourite, row.Hidden, row.FullPath, count }.Select(CsvCell)));
                        }
                    }
                    status.Text = "已导出当前显示的 " + visiblePackages + " 个包：" + dialog.FileName;
                }
                catch (Exception error) { MessageBox.Show(this, error.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        private static string CsvCell(string value)
        {
            if (value.StartsWith("=") || value.StartsWith("+") || value.StartsWith("-") || value.StartsWith("@")) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private void ShowWarnings()
        {
            var dialog = new Form { Text = "扫描提示", Size = new Size(900, 500), StartPosition = FormStartPosition.CenterParent, Font = Font };
            dialog.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = String.Join("\r\n\r\n", result.Warnings) });
            using (dialog) dialog.ShowDialog(this);
        }

        private void LoadSettings()
        {
            if (!File.Exists(settingsPath)) return;
            try
            {
                var options = Scanner.Json().Deserialize<ScanOptions>(File.ReadAllText(settingsPath, Encoding.UTF8));
                vamPath.Text = options.VamPath; packagesPath.Text = options.PackagesPath; tag.Text = options.Tag;
                contains.Checked = options.Contains;
            }
            catch { status.Text = "配置读取失败，已使用默认路径。"; }
        }

        private void SaveSettings(ScanOptions options)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)); File.WriteAllText(settingsPath, Scanner.Json().Serialize(options), Encoding.UTF8); }
            catch { status.Text = "扫描完成，路径设置未能保存。"; }
        }
    }
}
