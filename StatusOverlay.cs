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
        private readonly MappingIndicator mappingStatus = new MappingIndicator();
        private readonly DeckCard leftStatus, rightStatus;
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
            ClientSize = new Size(278, 92);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ConsoleTheme.StyleWindow(this);
            Cursor = Cursors.SizeAll;

            var surface = new ConsolePanel { Dock = DockStyle.Fill, Padding = new Padding(8, 5, 8, 8), Cursor = Cursors.SizeAll };
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Cursor = Cursors.SizeAll
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            mappingStatus.Dock = DockStyle.Fill;
            mappingStatus.Margin = new Padding(3, 0, 0, 0);
            mappingStatus.Cursor = Cursors.SizeAll;
            layout.Controls.Add(mappingStatus, 0, 0);
            layout.SetColumnSpan(mappingStatus, 2);
            leftStatus = new DeckCard("左组", tips, true) { Margin = new Padding(0, 2, 3, 0), Cursor = Cursors.SizeAll };
            rightStatus = new DeckCard("右组", tips, true) { Margin = new Padding(3, 2, 0, 0), Cursor = Cursors.SizeAll };
            layout.Controls.Add(leftStatus, 0, 1);
            layout.Controls.Add(rightStatus, 1, 1);
            surface.Controls.Add(layout);
            Controls.Add(surface);

            foreach (var control in new Control[] { this, surface, layout, mappingStatus, leftStatus, rightStatus })
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

        internal void SetState(bool enabled, string left, string right)
        {
            mappingStatus.SetMappingActive(enabled);
            leftStatus.SetDeck(left);
            rightStatus.SetDeck(right);
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
