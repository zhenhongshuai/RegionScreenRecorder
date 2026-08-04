using System;
using System.Drawing;
using System.Windows.Forms;

namespace RegionScreenRecorder
{
    public partial class RegionSelectForm : Form
    {
        private Point _startPos;
        private Rectangle _selectRect;
        private bool _isDragging;
        public Rectangle SelectedRegion { get; private set; }

        public RegionSelectForm()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.WindowState = FormWindowState.Maximized;
            this.Opacity = 0.4;
            this.BackColor = Color.Black;
            this.Cursor = Cursors.Cross;
            this.DoubleBuffered = true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _isDragging = true;
                _startPos = e.Location;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_isDragging) return;
            int x = Math.Min(_startPos.X, e.X);
            int y = Math.Min(_startPos.Y, e.Y);
            int w = Math.Abs(e.X - _startPos.X);
            int h = Math.Abs(e.Y - _startPos.Y);
            _selectRect = new Rectangle(x, y, w, h);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && _isDragging)
            {
                _isDragging = false;
                if (_selectRect.Width > 10 && _selectRect.Height > 10)
                {
                    SelectedRegion = _selectRect;
                    DialogResult = DialogResult.OK;
                }
                else
                {
                    DialogResult = DialogResult.Cancel;
                }
                Close();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_isDragging && _selectRect.Width > 0)
            {
                using (var pen = new Pen(Color.Lime, 2))
                {
                    e.Graphics.DrawRectangle(pen, _selectRect);
                }
                // 选区内部半透明遮罩
                var mask = new Rectangle(0, 0, Width, Height);
                using (var brush = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                {
                    var region = new Region(mask);
                    region.Exclude(_selectRect);
                    e.Graphics.FillRegion(brush, region);
                }
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // ESC取消选区
            if (keyData == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}