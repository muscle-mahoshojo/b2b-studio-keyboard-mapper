using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace B2BKeys
{
    internal sealed class StatusOverlay : Form
    {
        private const int MouseActivate = 0x0021;
        private const int DoNotActivate = 3;
        private const int NoActivateStyle = 0x08000000;
        private const int ToolWindowStyle = 0x00000080;
        private const int TopMostStyle = 0x00000008;

        private readonly string positionPath;
        private readonly Func<Rectangle> defaultArea;
        private readonly Label mappingStatus = StatusLabel();
        private readonly Label leftStatus = StatusLabel();
        private readonly Label rightStatus = StatusLabel();
        private readonly ToolTip tips = new ToolTip();
        private readonly Timer dragTimer = new Timer { Interval = 15 };
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4096 };
        private Point dragStart;
        private Size dragOffset;

        internal StatusOverlay(string positionPath, Func<Rectangle> defaultArea)
        {
            this.positionPath = positionPath;
            this.defaultArea = defaultArea;
            SetStyle(ControlStyles.Selectable, false);
            Text = "B2B 映射状态";
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(300, 92);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(24, 28, 36);
            ForeColor = Color.FromArgb(237, 240, 246);
            Font = new Font("Microsoft YaHei UI", 9F);
            Cursor = Cursors.SizeAll;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12, 8, 12, 8),
                ColumnCount = 1,
                RowCount = 3,
                Cursor = Cursors.SizeAll
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            mappingStatus.Font = new Font(Font, FontStyle.Bold);
            layout.Controls.Add(mappingStatus, 0, 0);
            layout.Controls.Add(leftStatus, 0, 1);
            layout.Controls.Add(rightStatus, 0, 2);
            Controls.Add(layout);

            foreach (var control in new Control[] { this, layout, mappingStatus, leftStatus, rightStatus })
                control.MouseDown += BeginDrag;
            dragTimer.Tick += MoveWhileDragging;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                // Form.TopMost makes WinForms try to focus the form when showing it.
                parameters.ExStyle |= NoActivateStyle | ToolWindowStyle | TopMostStyle;
                return parameters;
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == MouseActivate)
            {
                message.Result = new IntPtr(DoNotActivate);
                return;
            }
            base.WndProc(ref message);
        }

        private static Label StatusLabel()
        {
            return new Label
            {
                Dock = DockStyle.Fill, Margin = Padding.Empty, AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.SizeAll
            };
        }

        internal void SetState(bool enabled, string left, string right)
        {
            mappingStatus.Text = enabled ? "映射已启用" : "映射未启用";
            mappingStatus.ForeColor = enabled ? Color.FromArgb(108, 221, 193) : Color.FromArgb(179, 191, 208);
            leftStatus.Text = "左组  " + left;
            rightStatus.Text = "右组  " + right;
            tips.SetToolTip(leftStatus, leftStatus.Text);
            tips.SetToolTip(rightStatus, rightStatus.Text);
        }

        protected override void OnLoad(EventArgs args)
        {
            base.OnLoad(args);
            PlaceOnScreen(ReadPosition());
            SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        }

        protected override void OnVisibleChanged(EventArgs args)
        {
            if (!Visible) EndDrag();
            base.OnVisibleChanged(args);
        }

        private void DisplaySettingsChanged(object sender, EventArgs args)
        {
            try { BeginInvoke(new MethodInvoker(() => PlaceOnScreen(Location))); }
            catch (InvalidOperationException) { }
        }

        private void PlaceOnScreen(Point? preferred)
        {
            if (preferred.HasValue)
            {
                var area = Screen.AllScreens.Select(screen => screen.WorkingArea)
                    .FirstOrDefault(bounds => bounds.Contains(preferred.Value));
                if (!area.IsEmpty)
                {
                    Location = Constrain(preferred.Value, area);
                    return;
                }
            }

            var fallback = defaultArea();
            Location = Constrain(new Point(fallback.Right - Width - 16, fallback.Top + 16), fallback);
        }

        private Point Constrain(Point point, Rectangle area)
        {
            return new Point(
                Math.Max(area.Left, Math.Min(point.X, area.Right - Width)),
                Math.Max(area.Top, Math.Min(point.Y, area.Bottom - Height)));
        }

        private void BeginDrag(object sender, MouseEventArgs args)
        {
            if (args.Button != MouseButtons.Left || dragTimer.Enabled) return;
            dragStart = Location;
            var cursor = Cursor.Position;
            dragOffset = new Size(cursor.X - Left, cursor.Y - Top);
            // Poll only during a drag so this non-activating window does not need mouse capture.
            dragTimer.Start();
        }

        private void MoveWhileDragging(object sender, EventArgs args)
        {
            var cursor = Cursor.Position;
            Location = Constrain(cursor - dragOffset, Screen.FromPoint(cursor).WorkingArea);
            if ((MouseButtons & MouseButtons.Left) == 0) EndDrag();
        }

        private void EndDrag()
        {
            if (!dragTimer.Enabled) return;
            dragTimer.Stop();
            if (Location == dragStart) return;
            try { Profile.WriteJson(positionPath, json.Serialize(new { X = Left, Y = Top })); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private Point? ReadPosition()
        {
            try
            {
                var info = new FileInfo(positionPath);
                if (!info.Exists || info.Length > json.MaxJsonLength) return null;
                var position = json.Deserialize<Dictionary<string, int>>(File.ReadAllText(positionPath));
                int x, y;
                if (position != null && position.TryGetValue("X", out x) && position.TryGetValue("Y", out y))
                    return new Point(x, y);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            return null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
                EndDrag();
                dragTimer.Dispose();
                tips.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
