using System;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;

namespace BrowserAssistScanner
{
    public sealed class ResultsTable : DataGridView
    {
        public Func<Keys, bool> ListKey;
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (ListKey != null && ListKey(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }
        // Tests use the same route as actual keyboard messages.
        internal bool DispatchKey(Keys key) { Message msg = new Message(); return ProcessCmdKey(ref msg, key); }
        internal void DispatchClick(int row, string column) {
            CurrentCell = Rows[row].Cells[column];
            OnCellClick(new DataGridViewCellEventArgs(Columns[column].Index, row));
        }
        internal void DispatchDoubleClick(int row, string column) {
            CurrentCell = Rows[row].Cells[column];
            OnCellDoubleClick(new DataGridViewCellEventArgs(Columns[column].Index, row));
        }
    }

    public sealed class RadioCell : DataGridViewTextBoxCell
    {
        public bool Active;
        protected override void Paint(Graphics graphics, Rectangle clipBounds, Rectangle cellBounds, int rowIndex,
            DataGridViewElementStates state, object value, object formattedValue, string errorText,
            DataGridViewCellStyle style, DataGridViewAdvancedBorderStyle border, DataGridViewPaintParts parts)
        {
            base.Paint(graphics, clipBounds, cellBounds, rowIndex, state, value, "", errorText, style, border, parts & ~DataGridViewPaintParts.ContentForeground);
            bool selected = Object.Equals(value, true);
            RadioButtonState button = Active ? (selected ? RadioButtonState.CheckedNormal : RadioButtonState.UncheckedNormal) : RadioButtonState.UncheckedDisabled;
            Size size = RadioButtonRenderer.GetGlyphSize(graphics, button);
            RadioButtonRenderer.DrawRadioButton(graphics, new Point(cellBounds.Left + (cellBounds.Width - size.Width) / 2, cellBounds.Top + (cellBounds.Height - size.Height) / 2), button);
        }
    }
}
