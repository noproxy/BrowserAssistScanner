using System;
using System.Drawing;
using System.Windows.Forms;

namespace BrowserAssistScanner
{
    public sealed class ThumbnailPopup : ToolStripDropDown
    {
        private readonly PictureBox picture;
        private readonly Label caption, message;
        private ThumbnailResult thumbnail;
        private readonly bool offscreen;
        public ThumbnailPopup(bool offscreen) {
            this.offscreen = offscreen;
            AutoSize = false; AutoClose = false; Size = new Size(280, 306);
            BackColor = Color.White; Padding = new Padding(0); Font = new Font("Microsoft YaHei UI", 9);
            picture = new PictureBox { Location = new Point(12, 12), Size = new Size(256, 256), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(245, 247, 250) };
            message = new Label { Bounds = picture.Bounds, TextAlign = ContentAlignment.MiddleCenter, BackColor = picture.BackColor };
            caption = new Label { Bounds = new Rectangle(10, 273, 260, 26), AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            var content = new Panel { Size = new Size(280, 306), BackColor = Color.White };
            content.Controls.Add(picture); content.Controls.Add(message); content.Controls.Add(caption);
            Items.Add(new ToolStripControlHost(content) { AutoSize = false, Size = content.Size, Margin = new Padding(0), Padding = new Padding(0) });
        }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); e.Graphics.DrawRectangle(Pens.LightGray, 0, 0, Width - 1, Height - 1); }
        public void Loading(Form owner, string path, Point pointer) {
            ClearImage(); caption.Text = System.IO.Path.GetFileName(path); message.Text = "正在读取缩略图…"; message.Visible = true;
            Rectangle area = Screen.FromPoint(pointer).WorkingArea;
            int x = pointer.X + 18, y = pointer.Y + 20;
            if (x + Width > area.Right) x = pointer.X - Width - 18;
            if (y + Height > area.Bottom) y = pointer.Y - Height - 20;
            Location = new Point(Math.Max(area.Left, Math.Min(x, area.Right - Width)), Math.Max(area.Top, Math.Min(y, area.Bottom - Height)));
            if (offscreen) Location = new Point(-3000, -3000);
            if (!Visible) Show(Location);
        }
        public void SetThumbnail(ThumbnailResult value) {
            ClearImage(); thumbnail = value; picture.Image = value.Image;
            message.Text = "无可用缩略图"; message.Visible = value.Image == null;
        }
        public bool HasImage { get { return picture.Image != null; } }
        public void HidePreview() { Close(ToolStripDropDownCloseReason.CloseCalled); ClearImage(); }
        private void ClearImage() { picture.Image = null; if (thumbnail != null) { thumbnail.Dispose(); thumbnail = null; } }
        protected override void Dispose(bool disposing) { if (disposing) ClearImage(); base.Dispose(disposing); }
    }
}
