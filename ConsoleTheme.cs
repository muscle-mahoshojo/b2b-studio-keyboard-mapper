using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace B2BKeys
{
    internal static class ConsoleTheme
    {
        private const uint GetClientAreaAnimation = 0x1042;
        internal static readonly Color Canvas = Color.FromArgb(16, 19, 22);
        internal static readonly Color Surface = Color.FromArgb(24, 29, 33);
        internal static readonly Color Raised = Color.FromArgb(33, 40, 45);
        internal static readonly Color Field = Color.FromArgb(15, 20, 23);
        internal static readonly Color Line = Color.FromArgb(52, 64, 70);
        internal static readonly Color Ink = Color.FromArgb(237, 244, 242);
        internal static readonly Color Muted = Color.FromArgb(172, 186, 187);
        internal static readonly Color Dim = Color.FromArgb(126, 146, 148);
        internal static readonly Color Accent = Color.FromArgb(111, 231, 181);
        internal static readonly Color AccentInk = Color.FromArgb(10, 32, 25);
        internal static readonly Color AccentSurface = Color.FromArgb(31, 65, 54);
        internal static readonly Color Alert = Color.FromArgb(255, 194, 131);
        internal static readonly Font Body = new Font("Microsoft YaHei UI", 9F);
        internal static readonly Font Strong = new Font(Body, FontStyle.Bold);
        internal static readonly Font Title = new Font("Segoe UI", 23F, FontStyle.Bold);
        internal static readonly Font DeckTitle = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold);
        internal static readonly Font Key = new Font("Consolas", 10F, FontStyle.Bold);

        internal static void StyleWindow(Form form)
        {
            form.AutoScaleDimensions = new SizeF(96F, 96F);
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.Font = Body;
            form.BackColor = Canvas;
            form.ForeColor = Ink;
            form.HandleCreated += delegate
            {
                int enabled = 1;
                if (DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(form.Handle, 19, ref enabled, sizeof(int));
            };
        }

        internal static Label Label(string text, Color color, Font font = null)
        {
            return new Label
            {
                Text = text, ForeColor = color, Font = font ?? Body, Margin = Padding.Empty,
                AutoSize = true, TextAlign = ContentAlignment.MiddleLeft
            };
        }

        internal static void StyleInput(Control control)
        {
            control.BackColor = Field;
            control.ForeColor = Ink;
            var text = control as TextBox;
            if (text != null) text.BorderStyle = BorderStyle.FixedSingle;
            var number = control as NumericUpDown;
            if (number != null) number.BorderStyle = BorderStyle.FixedSingle;
            var combo = control as ComboBox;
            if (combo == null) return;
            combo.FlatStyle = FlatStyle.Flat;
            combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.DrawItem += DrawChoice;
        }

        private static void DrawChoice(object sender, DrawItemEventArgs args)
        {
            var combo = (ComboBox)sender;
            bool field = (args.State & DrawItemState.ComboBoxEdit) != 0;
            bool selected = !field && (args.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(selected ? AccentSurface : Field))
                args.Graphics.FillRectangle(brush, args.Bounds);
            if (args.Index >= 0 && args.Index < combo.Items.Count)
            {
                var bounds = args.Bounds;
                bounds.Inflate(-3, 0);
                TextRenderer.DrawText(args.Graphics, combo.GetItemText(combo.Items[args.Index]), args.Font, bounds,
                    combo.Enabled ? Ink : Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
            if (combo.Focused) args.DrawFocusRectangle();
        }

        internal static void PaintKey(Graphics graphics, Rectangle bounds, string text, Font font, Color fill, Color edge, Color ink)
        {
            if (bounds.Width < 4 || bounds.Height < 4) return;
            int radius = Math.Min(8, Math.Min(bounds.Width, bounds.Height) - 2);
            int right = bounds.Right - 1;
            int bottom = bounds.Bottom - 1;
            var previous = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = new GraphicsPath())
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(edge))
            {
                path.AddArc(bounds.Left, bounds.Top, radius, radius, 180, 90);
                path.AddArc(right - radius, bounds.Top, radius, radius, 270, 90);
                path.AddArc(right - radius, bottom - radius, radius, radius, 0, 90);
                path.AddArc(bounds.Left, bottom - radius, radius, radius, 90, 90);
                path.CloseFigure();
                graphics.FillPath(brush, path);
                graphics.DrawPath(pen, path);
            }
            graphics.SmoothingMode = previous;
            bounds.Inflate(-4, 0);
            if (bounds.Width > 0)
                TextRenderer.DrawText(graphics, text, font, bounds, ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        internal static bool AnimationsEnabled()
        {
            bool enabled;
            return SystemParametersInfo(GetClientAreaAnimation, 0, out enabled, 0) && enabled;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint update);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    }

    internal sealed class ColorTransition
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly HashSet<ColorTransition> Running = new HashSet<ColorTransition>();
        private static readonly Timer Frame = new Timer { Interval = 15 };
        private readonly Control owner;
        private readonly Action<Color> apply;
        private Color current, from, to;
        private long started;
        private int duration;

        static ColorTransition() { Frame.Tick += Advance; }

        internal ColorTransition(Control owner, Color initial, Action<Color> apply)
        {
            this.owner = owner;
            this.apply = apply;
            current = initial;
        }

        internal void Play(Color target, int milliseconds, Color? start = null)
        {
            if (!owner.Visible || !owner.IsHandleCreated || !ConsoleTheme.AnimationsEnabled() || milliseconds == 0)
            {
                SetColor(target);
                Stop();
                return;
            }
            if (Running.Contains(this) && to == target && !start.HasValue) return;
            from = start ?? current;
            to = target;
            if (from == to) { SetColor(to); Stop(); return; }
            SetColor(from);
            started = Clock.ElapsedMilliseconds;
            duration = milliseconds;
            Running.Add(this);
            Frame.Start();
        }

        private static void Advance(object sender, EventArgs args)
        {
            long now = Clock.ElapsedMilliseconds;
            bool animate = ConsoleTheme.AnimationsEnabled();
            var pending = new ColorTransition[Running.Count];
            Running.CopyTo(pending);
            foreach (var transition in pending)
            {
                double progress = (double)(now - transition.started) / transition.duration;
                if (!animate || progress >= 1 || !transition.owner.Visible)
                {
                    transition.SetColor(transition.to);
                    transition.Stop();
                    continue;
                }
                double weight = 1 - (1 - progress) * (1 - progress);
                var a = transition.from;
                var b = transition.to;
                transition.SetColor(Color.FromArgb((int)Math.Round(a.R + (b.R - a.R) * weight),
                    (int)Math.Round(a.G + (b.G - a.G) * weight), (int)Math.Round(a.B + (b.B - a.B) * weight)));
            }
        }

        private void SetColor(Color color)
        {
            if (color == current) return;
            current = color;
            apply(color);
        }

        internal void Stop()
        {
            Running.Remove(this);
            if (Running.Count == 0) Frame.Stop();
        }
    }

    internal sealed class ConsoleButton : Button
    {
        private readonly bool primary;
        private readonly ColorTransition transition;
        private bool over, pressed, space;

        internal ConsoleButton(string text, bool primary = false)
        {
            this.primary = primary;
            Text = text;
            Font = primary ? ConsoleTheme.Strong : ConsoleTheme.Body;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            Padding = new Padding(primary ? 13 : 7, 0, primary ? 13 : 7, 0);
            MinimumSize = new Size(primary ? 106 : 0, primary ? 37 : 30);
            Margin = new Padding(0, 2, 6, 2);
            Cursor = Cursors.Hand;
            var rest = primary ? ConsoleTheme.Accent : ConsoleTheme.Raised;
            transition = new ColorTransition(this, rest, PaintBackground);
            PaintBackground(rest);
            RefreshColors(false);
        }

        private void PaintBackground(Color color)
        {
            BackColor = color;
            FlatAppearance.MouseOverBackColor = color;
            FlatAppearance.MouseDownBackColor = color;
        }

        private void RefreshColors(bool animate = true)
        {
            if (transition == null) return;
            ForeColor = !Enabled ? ConsoleTheme.Dim : primary ? ConsoleTheme.AccentInk : ConsoleTheme.Ink;
            FlatAppearance.BorderColor = primary && Enabled ? ConsoleTheme.Accent : ConsoleTheme.Line;
            Color target = primary ? ConsoleTheme.Accent : ConsoleTheme.Raised;
            if (!Enabled) target = ConsoleTheme.Surface;
            else if ((over && pressed) || space) target = primary ? Color.FromArgb(86, 193, 152) : ConsoleTheme.Field;
            else if (over) target = primary ? Color.FromArgb(158, 246, 206) : Color.FromArgb(49, 63, 67);
            transition.Play(target, animate ? 120 : 0);
        }

        protected override void OnMouseEnter(EventArgs args)
        {
            base.OnMouseEnter(args);
            over = true;
            RefreshColors();
        }

        protected override void OnMouseLeave(EventArgs args)
        {
            base.OnMouseLeave(args);
            over = false;
            RefreshColors();
        }

        protected override void OnMouseDown(MouseEventArgs args)
        {
            base.OnMouseDown(args);
            if (args.Button != MouseButtons.Left) return;
            pressed = true;
            RefreshColors();
        }

        protected override void OnMouseUp(MouseEventArgs args)
        {
            base.OnMouseUp(args);
            pressed = false;
            RefreshColors();
        }

        protected override void OnMouseCaptureChanged(EventArgs args)
        {
            base.OnMouseCaptureChanged(args);
            if (Capture) return;
            pressed = false;
            RefreshColors();
        }

        protected override void OnKeyDown(KeyEventArgs args)
        {
            base.OnKeyDown(args);
            if (args.KeyCode != Keys.Space) return;
            space = true;
            RefreshColors();
        }

        protected override void OnKeyUp(KeyEventArgs args)
        {
            base.OnKeyUp(args);
            if (args.KeyCode != Keys.Space) return;
            space = false;
            RefreshColors();
        }

        protected override void OnLostFocus(EventArgs args)
        {
            base.OnLostFocus(args);
            space = false;
            RefreshColors();
        }

        protected override void OnEnabledChanged(EventArgs args)
        {
            base.OnEnabledChanged(args);
            pressed = over = space = false;
            RefreshColors(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && transition != null) transition.Stop();
            base.Dispose(disposing);
        }
    }

    internal class ConsolePanel : Panel
    {
        internal ConsolePanel()
        {
            BackColor = ConsoleTheme.Surface;
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width > 0 && Height > 0)
                using (var pen = new Pen(ConsoleTheme.Line))
                    e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    internal sealed class MappingIndicator : Label
    {
        private readonly ColorTransition transition;
        private Color dot = ConsoleTheme.Dim;
        private bool enabled;

        internal MappingIndicator()
        {
            Text = "映射未启用";
            ForeColor = ConsoleTheme.Muted;
            Font = ConsoleTheme.Strong;
            TextAlign = ContentAlignment.MiddleLeft;
            Padding = new Padding(17, 0, 0, 0);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            transition = new ColorTransition(this, dot, color => { dot = color; Invalidate(); });
        }

        internal void SetMappingActive(bool value)
        {
            if (enabled == value) return;
            enabled = value;
            Text = value ? "映射已启用" : "映射未启用";
            ForeColor = value ? ConsoleTheme.Accent : ConsoleTheme.Muted;
            transition.Play(value ? ConsoleTheme.Accent : ConsoleTheme.Dim, 180);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int size = Math.Max(6, Padding.Left / 2);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(dot))
                e.Graphics.FillEllipse(brush, 1, (Height - size) / 2, size, size);
        }

        protected override void Dispose(bool disposing) { if (disposing) transition.Stop(); base.Dispose(disposing); }
    }

    internal sealed class DeckCard : ConsolePanel
    {
        private readonly Label name;
        private readonly Label shortcut;
        private readonly ColorTransition transition;
        private readonly ToolTip tips;
        private string deck;

        internal DeckCard(string group, ToolTip tips, bool compact = false)
        {
            this.tips = tips;
            Dock = DockStyle.Fill;
            Padding = compact ? new Padding(9, 5, 6, 5) : new Padding(14, 7, 12, 8);
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 2 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Controls.Add(ConsoleTheme.Label(group, ConsoleTheme.Muted), 0, 0);
            shortcut = ConsoleTheme.Label("", ConsoleTheme.Accent, ConsoleTheme.Key);
            grid.Controls.Add(shortcut, 1, 0);
            name = ConsoleTheme.Label("", ConsoleTheme.Ink, compact ? ConsoleTheme.Strong : ConsoleTheme.DeckTitle);
            name.AutoSize = false;
            name.AutoEllipsis = true;
            name.Dock = DockStyle.Fill;
            grid.Controls.Add(name, 0, 1);
            grid.SetColumnSpan(name, 2);
            Controls.Add(grid);

            transition = new ColorTransition(this, BackColor, color => BackColor = color);
            if (compact)
                foreach (Control control in new Control[] { grid, grid.GetControlFromPosition(0, 0), shortcut, name })
                {
                    control.Cursor = Cursors.SizeAll;
                    control.MouseDown += delegate(object sender, MouseEventArgs e) { OnMouseDown(e); };
                }
        }

        internal void SetDeck(string value, string key = "")
        {
            bool changed = deck != null && !String.Equals(deck, value, StringComparison.Ordinal);
            if (deck != value)
            {
                deck = value;
                name.Text = value;
                tips.SetToolTip(name, value);
            }
            if (shortcut.Text != key)
            {
                shortcut.Text = key;
                tips.SetToolTip(shortcut, key.Length > 0 ? "切换键 " + key : "");
            }
            if (changed) transition.Play(ConsoleTheme.Surface, 180, ConsoleTheme.AccentSurface);
        }

        protected override void Dispose(bool disposing) { if (disposing) transition.Stop(); base.Dispose(disposing); }
    }
}
