using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace RedOSPackageUpdater
{
    internal sealed class BorderlessTextEditor : TextBox
    {
        private const int WsBorder = 0x00800000;
        private const int WsExClientEdge = 0x00000200;
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style &= ~WsBorder;
                cp.ExStyle &= ~WsExClientEdge;
                return cp;
            }
        }
        public BorderlessTextEditor() { BorderStyle = BorderStyle.None; }
        protected override void OnHandleCreated(EventArgs e) { BorderStyle = BorderStyle.None; base.OnHandleCreated(e); }
    }

    internal static class ModernControlShape
    {
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        public static void Apply(Control control, int radius)
        {
            if (control == null || control.Width < 2 || control.Height < 2) return;
            IntPtr handle = CreateRoundRectRgn(0, 0, control.Width + 1, control.Height + 1, radius * 2, radius * 2);
            if (handle == IntPtr.Zero) return;
            Region previous = control.Region;
            control.Region = Region.FromHrgn(handle);
            DeleteObject(handle);
            if (previous != null) previous.Dispose();
        }
    }

    internal enum ModernButtonKind { Secondary, Primary, Danger, Navigation, Ghost }

    internal sealed class ModernButton : Button
    {
        private readonly Timer _animation;
        private float _hover;
        private float _target;
        private ModernButtonKind _kind;
        public int CornerRadius { get; set; }
        public ModernButtonKind Kind { get { return _kind; } set { _kind = value; Invalidate(); } }
        public bool NavigationActive { get; set; }
        public string NavigationIcon { get; set; }
        public string IconName { get; set; }

        public ModernButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            Font = Theme.UiFont;
            CornerRadius = 7;
            _animation = new Timer { Interval = 16 };
            _animation.Tick += delegate
            {
                float step = 0.18F;
                if (Math.Abs(_target - _hover) <= step) { _hover = _target; _animation.Stop(); }
                else _hover += _target > _hover ? step : -step;
                Invalidate();
            };
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _target = 1F; _animation.Start(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _target = 0F; _animation.Start(); }
        protected override void OnMouseDown(MouseEventArgs mevent) { base.OnMouseDown(mevent); Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs mevent) { base.OnMouseUp(mevent); Invalidate(); }
        protected override void Dispose(bool disposing) { if (disposing) _animation.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // A user-painted Button does not erase the pixels outside the rounded
            // path.  On classic/remote Windows desktops those stale pixels are
            // commonly presented as black corner wedges.  Paint the complete
            // control surface first, then draw the rounded button above it.
            e.Graphics.Clear(Parent != null ? Parent.BackColor : BackColor);
            Rectangle rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            Color normal, hover, fore, border;
            GetPalette(out normal, out hover, out fore, out border);
            if (!Enabled) { normal = Theme.HeaderBg; hover = normal; fore = Theme.Disabled; border = Theme.Border; }
            Color fill = Blend(normal, hover, _hover);
            if (Capture && MouseButtons == MouseButtons.Left) fill = Blend(fill, Color.Black, 0.08F);
            using (GraphicsPath path = Rounded(rect, CornerRadius))
            using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
            if (border.A > 0)
                using (GraphicsPath path = Rounded(rect, CornerRadius)) using (var pen = new Pen(border)) e.Graphics.DrawPath(pen, path);
            if (_kind == ModernButtonKind.Navigation && NavigationActive)
                using (var brush = new SolidBrush(Color.FromArgb(112, 157, 255))) e.Graphics.FillRectangle(brush, 0, 8, 3, Math.Max(1, Height - 16));
            if (_kind == ModernButtonKind.Navigation && !string.IsNullOrEmpty(NavigationIcon))
                AppIcons.Draw(e.Graphics, NavigationIcon, fore, new Rectangle(16, Height / 2 - 9, 18, 18));
            bool hasIcon = !string.IsNullOrEmpty(IconName);
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            flags |= TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter;
            int iconOffset = hasIcon ? 22 : 0;
            Rectangle textRect;
            if (hasIcon && TextAlign != ContentAlignment.MiddleLeft)
            {
                // Centre the icon and caption as one optical group. Previously the
                // icon was pinned to x=10 while the caption was centred separately.
                // That made every command button look visibly lopsided.
                Size measured = TextRenderer.MeasureText(Text ?? "", Font, new Size(int.MaxValue, Height),
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                int groupWidth = 16 + 7 + Math.Min(measured.Width, Math.Max(0, Width - 30));
                int groupLeft = Math.Max(8, (Width - groupWidth) / 2);
                AppIcons.Draw(e.Graphics, IconName, fore, new Rectangle(groupLeft, Height / 2 - 8, 16, 16));
                textRect = new Rectangle(groupLeft + 23, -1, Math.Max(1, Width - groupLeft - 27), Height + 1);
                flags &= ~TextFormatFlags.HorizontalCenter;
                flags |= TextFormatFlags.Left;
            }
            else
            {
                if (hasIcon) AppIcons.Draw(e.Graphics, IconName, fore, new Rectangle(10, Height / 2 - 8, 16, 16));
                textRect = new Rectangle(Padding.Left + 3 + iconOffset, -1, Math.Max(1, Width - Padding.Horizontal - 6 - iconOffset), Height + 1);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, textRect, fore, flags);
            if (Focused && ShowFocusCues)
                using (GraphicsPath path = Rounded(new Rectangle(2, 2, Math.Max(1, Width - 5), Math.Max(1, Height - 5)), Math.Max(3, CornerRadius - 2)))
                using (var pen = new Pen(Color.FromArgb(150, Theme.Accent))) { pen.DashStyle = DashStyle.Dot; e.Graphics.DrawPath(pen, path); }
        }

        private void GetPalette(out Color normal, out Color hover, out Color fore, out Color border)
        {
            border = Color.Transparent;
            if (_kind == ModernButtonKind.Primary) { normal = Theme.Accent; hover = Theme.AccentHover; fore = Color.White; }
            else if (_kind == ModernButtonKind.Danger) { normal = Theme.Danger; hover = Theme.DangerHover; fore = Color.White; }
            else if (_kind == ModernButtonKind.Navigation)
            {
                normal = NavigationActive ? Theme.NavigationActive : Theme.NavigationBg;
                hover = NavigationActive ? Theme.NavigationActive : Theme.NavigationHover;
                fore = NavigationActive ? Color.White : Theme.NavigationText;
            }
            else if (_kind == ModernButtonKind.Ghost) { normal = Color.Transparent; hover = Theme.HeaderBg; fore = Theme.Text; }
            else { normal = Theme.Surface; hover = Theme.HeaderBg; fore = Theme.Text; border = Theme.Border; }
        }

        internal static GraphicsPath Rounded(Rectangle rectangle, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(2, Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height)));
            path.AddArc(rectangle.Left, rectangle.Top, d, d, 180, 90);
            path.AddArc(rectangle.Right - d, rectangle.Top, d, d, 270, 90);
            path.AddArc(rectangle.Right - d, rectangle.Bottom - d, d, d, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - d, d, d, 90, 90);
            path.CloseFigure(); return path;
        }

        private static Color Blend(Color a, Color b, float amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * amount), (int)(a.R + (b.R - a.R) * amount),
                (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
        }
    }

    internal sealed class ModernCard : Panel
    {
        public int CornerRadius { get; set; }
        public ModernCard()
        {
            CornerRadius = 9; BackColor = Theme.Surface;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Color outside = Parent == null ? Theme.Bg : Parent.BackColor;
            e.Graphics.Clear(outside);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            using (GraphicsPath path = ModernButton.Rounded(rect, CornerRadius))
            using (var brush = new SolidBrush(BackColor)) e.Graphics.FillPath(brush, path);
            using (GraphicsPath path = ModernButton.Rounded(rect, CornerRadius))
            using (var pen = new Pen(Theme.Border)) e.Graphics.DrawPath(pen, path);
            base.OnPaint(e);
        }
    }

    internal sealed class ModernProgressBar : Control
    {
        private readonly Timer _timer;
        private int _value, _minimum, _maximum = 100, _phase;
        private ProgressBarStyle _style;
        public int Minimum { get { return _minimum; } set { _minimum = value; Invalidate(); } }
        public int Maximum { get { return _maximum; } set { _maximum = Math.Max(value, _minimum + 1); Invalidate(); } }
        public int Value { get { return _value; } set { _value = Math.Max(_minimum, Math.Min(_maximum, value)); Invalidate(); } }
        public ProgressBarStyle Style
        {
            get { return _style; }
            set { _style = value; if (value == ProgressBarStyle.Marquee) _timer.Start(); else _timer.Stop(); Invalidate(); }
        }
        public int MarqueeAnimationSpeed { get { return _timer.Interval; } set { _timer.Interval = Math.Max(12, value); } }
        public ModernProgressBar()
        {
            Height = 8; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            _timer = new Timer { Interval = 24 };
            _timer.Tick += delegate { _phase = (_phase + 5) % Math.Max(1, Width + 80); Invalidate(); };
        }
        protected override void Dispose(bool disposing) { if (disposing) _timer.Dispose(); base.Dispose(disposing); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath path = ModernButton.Rounded(track, Math.Max(2, Height / 2)))
            using (var brush = new SolidBrush(Theme.HeaderBg)) e.Graphics.FillPath(brush, path);
            Rectangle fill;
            if (_style == ProgressBarStyle.Marquee)
            {
                int w = Math.Max(42, Width / 4); int x = _phase - 80;
                fill = new Rectangle(x, 0, w, Math.Max(1, Height - 1));
            }
            else
            {
                int width = (int)((Width - 1) * ((_value - _minimum) / (double)Math.Max(1, _maximum - _minimum)));
                fill = new Rectangle(0, 0, Math.Max(1, width), Math.Max(1, Height - 1));
            }
            e.Graphics.SetClip(track);
            using (GraphicsPath path = ModernButton.Rounded(fill, Math.Max(2, Height / 2)))
            using (var brush = new LinearGradientBrush(fill, Theme.Accent, Theme.AccentHover, 0F)) e.Graphics.FillPath(brush, path);
            e.Graphics.ResetClip();
        }
    }

    internal enum ToastKind { Info, Success, Warning, Error }

    internal static class ModernToast
    {
        public static void Show(Form owner, string text, ToastKind kind)
        {
            if (owner == null || owner.IsDisposed || string.IsNullOrWhiteSpace(text)) return;
            Control old = owner.Controls["modernToast"];
            if (old != null) { owner.Controls.Remove(old); old.Dispose(); }
            Color accent = kind == ToastKind.Success ? Theme.Good : kind == ToastKind.Warning ? Theme.Warn : kind == ToastKind.Error ? Theme.Danger : Theme.Accent;
            var toast = new ModernCard { Name = "modernToast", Width = 344, Height = 58, BackColor = Theme.Surface, Padding = new Padding(17, 10, 14, 9), Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
            var strip = new Panel { Dock = DockStyle.Left, Width = 4, BackColor = accent, Margin = new Padding(0, 0, 10, 0) };
            var label = new Label { Dock = DockStyle.Fill, Text = text, ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = new Padding(12, 0, 0, 0) };
            toast.Controls.Add(label); toast.Controls.Add(strip);
            int targetX = Math.Max(12, owner.ClientSize.Width - toast.Width - 20);
            toast.Left = owner.ClientSize.Width + 6; toast.Top = Math.Max(12, owner.ClientSize.Height - toast.Height - 48);
            owner.Controls.Add(toast); toast.BringToFront();
            int ticks = 0; var timer = new Timer { Interval = 16 };
            timer.Tick += delegate
            {
                if (toast.IsDisposed) { timer.Stop(); timer.Dispose(); return; }
                ticks++;
                if (ticks < 18) toast.Left += Math.Max(-38, (targetX - toast.Left) / 3);
                else if (ticks > 180) toast.Left += Math.Max(10, (owner.ClientSize.Width + 8 - toast.Left) / 3);
                if (ticks > 205) { timer.Stop(); owner.Controls.Remove(toast); toast.Dispose(); timer.Dispose(); }
            };
            timer.Start();
        }
    }

    internal sealed class ModernDataGridView : DataGridView
    {
        public string EmptyTitle { get; set; }
        public string EmptyHint { get; set; }
        public ModernDataGridView()
        {
            EmptyTitle = "Здесь появятся результаты";
            EmptyHint = "Запустите предпроверку или операцию для выбранных серверов";
            DoubleBuffered = true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Rows.Count != 0 || Width < 260 || Height < 140) return;
            int top = ColumnHeadersVisible ? ColumnHeadersHeight : 0;
            var area = new Rectangle(0, top, Width, Height - top);
            int cy = area.Top + area.Height / 2 - 24;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(Theme.AccentTint)) e.Graphics.FillEllipse(brush, area.Left + area.Width / 2 - 20, cy - 23, 40, 40);
            using (var pen = new Pen(Theme.Accent, 2))
            {
                e.Graphics.DrawLine(pen, area.Left + area.Width / 2 - 8, cy - 4, area.Left + area.Width / 2 + 8, cy - 4);
                e.Graphics.DrawLine(pen, area.Left + area.Width / 2 - 8, cy + 3, area.Left + area.Width / 2 + 4, cy + 3);
            }
            var titleRect = new Rectangle(area.Left + 20, cy + 27, area.Width - 40, 24);
            var hintRect = new Rectangle(area.Left + 20, cy + 53, area.Width - 40, 22);
            TextRenderer.DrawText(e.Graphics, EmptyTitle, Theme.UiFontHeading, titleRect, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
            TextRenderer.DrawText(e.Graphics, EmptyHint, Theme.UiFont, hintRect, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
        }
    }

    internal sealed class ModernTextBox : UserControl
    {
        private const int EmSetCueBanner = 0x1501;
        private const int EmSetMargins = 0x00D3;
        private readonly TextBox _editor;
        private string _placeholder = "";
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessagePtr(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        public string Placeholder { get { return _placeholder; } set { _placeholder = value ?? ""; UpdatePlaceholder(); } }
        public bool Multiline { get { return _editor.Multiline; } set { _editor.Multiline = value; LayoutEditor(); } }
        public ScrollBars ScrollBars { get { return _editor.ScrollBars; } set { _editor.ScrollBars = value; } }
        public bool AcceptsReturn { get { return _editor.AcceptsReturn; } set { _editor.AcceptsReturn = value; } }
        public bool ReadOnly { get { return _editor.ReadOnly; } set { _editor.ReadOnly = value; } }
        public int TextLength { get { return _editor.TextLength; } }
        public void Clear() { _editor.Clear(); }
        public override string Text { get { return _editor == null ? base.Text : _editor.Text; } set { if (_editor != null) _editor.Text = value ?? ""; else base.Text = value; } }
        public ModernTextBox()
        {
            Height = 28; BackColor = Theme.Surface; ForeColor = Theme.Text; Font = Theme.UiFont; BorderStyle = BorderStyle.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            _editor = new BorderlessTextEditor { BorderStyle = BorderStyle.None, BackColor = Theme.Surface,
                ForeColor = Theme.Text, Font = Theme.UiFont };
            _editor.TextChanged += delegate { base.Text = _editor.Text; UpdatePlaceholder(); };
            _editor.Enter += delegate { UpdatePlaceholder(); Invalidate(); };
            _editor.Leave += delegate { UpdatePlaceholder(); Invalidate(); };
            Controls.Add(_editor);
        }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style &= ~0x00800000;
                cp.ExStyle &= ~0x00000200;
                return cp;
            }
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyPlaceholder(); LayoutEditor(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutEditor(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (_editor != null) { _editor.Font = Font; LayoutEditor(); } }
        protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e); if (_editor != null) _editor.ForeColor = ForeColor; }
        protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); if (_editor != null) _editor.BackColor = BackColor; }
        protected override void OnClick(EventArgs e) { base.OnClick(e); _editor.Focus(); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var outside = new SolidBrush(Parent == null ? Theme.Bg : Parent.BackColor))
                e.Graphics.FillRectangle(outside, ClientRectangle);
            var bounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            using (GraphicsPath path = ModernButton.Rounded(bounds, 6))
            using (var fill = new SolidBrush(BackColor)) e.Graphics.FillPath(fill, path);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            using (GraphicsPath path = ModernButton.Rounded(bounds, 6))
            using (var pen = new Pen(_editor.Focused ? Theme.Accent : Theme.Border))
                e.Graphics.DrawPath(pen, path);
        }
        private void ApplyPlaceholder()
        {
            if (_editor != null && _editor.IsHandleCreated)
                SendMessage(_editor.Handle, EmSetCueBanner, new IntPtr(1), _placeholder);
        }
        private void UpdatePlaceholder()
        {
            if (_editor == null) return;
            ApplyPlaceholder();
            Invalidate();
        }
        private void LayoutEditor()
        {
            if (_editor == null) return;
            const int horizontalPadding = 8;
            if (_editor.Multiline) _editor.SetBounds(horizontalPadding, 6, Math.Max(1, Width - horizontalPadding * 2), Math.Max(1, Height - 12));
            else
            {
                int editorHeight = _editor.PreferredHeight;
                _editor.SetBounds(horizontalPadding, Math.Max(1, (Height - editorHeight) / 2 - 1),
                    Math.Max(1, Width - horizontalPadding * 2), editorHeight);
            }
            ApplyPlaceholder();
        }
    }

    internal sealed class ModernCheckBox : CheckBox
    {
        private bool _hover;
        public ModernCheckBox()
        {
            AutoSize = false; Height = 24; Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(BackColor)) e.Graphics.FillRectangle(bg, ClientRectangle);
            var box = new Rectangle(1, (Height - 17) / 2, 16, 16);
            Color fill = Checked ? Theme.Accent : (_hover ? Theme.HeaderBg : Theme.Surface);
            Color border = Checked ? Theme.Accent : (_hover ? Theme.Accent : Theme.Border);
            using (GraphicsPath path = ModernButton.Rounded(box, 4))
            using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
            using (GraphicsPath path = ModernButton.Rounded(box, 4))
            using (var pen = new Pen(border, 1F)) e.Graphics.DrawPath(pen, path);
            if (Checked)
            {
                using (var pen = new Pen(Color.White, 2F))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    e.Graphics.DrawLines(pen, new[] { new Point(5, box.Top + 8), new Point(8, box.Top + 11), new Point(14, box.Top + 5) });
                }
            }
            var textRect = new Rectangle(25, -1, Math.Max(0, Width - 25), Height + 1);
            TextRenderer.DrawText(e.Graphics, Text, Font, textRect, Enabled ? Theme.Text : Theme.Disabled,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, textRect, Theme.Text, BackColor);
        }
    }

    internal sealed class ModernComboBox : ComboBox
    {
        private const int WmPaint = 0x000F;
        private const int WmNcPaint = 0x0085;
        public ModernComboBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed; DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat; ItemHeight = 25; IntegralHeight = false; DropDownHeight = 260;
            BackColor = Theme.Surface; ForeColor = Theme.Text; Font = Theme.UiFont;
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnDropDown(EventArgs e) { base.OnDropDown(e); Invalidate(); }
        protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if ((m.Msg == WmPaint || m.Msg == WmNcPaint) && IsHandleCreated) DrawChrome();
        }
        private void DrawChrome()
        {
            using (Graphics g = Graphics.FromHwnd(Handle))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
                using (GraphicsPath path = ModernButton.Rounded(bounds, 6))
                {
                    using (var outside = new SolidBrush(Parent == null ? Theme.Bg : Parent.BackColor))
                    using (var corners = new Region(ClientRectangle))
                    {
                        corners.Exclude(path);
                        g.FillRegion(outside, corners);
                    }
                }
                int buttonWidth = Math.Max(22, Height - 2);
                var button = new Rectangle(Math.Max(1, Width - buttonWidth - 1), 1,
                    Math.Max(1, buttonWidth), Math.Max(1, Height - 2));
                GraphicsState buttonState = g.Save();
                using (GraphicsPath clipPath = ModernButton.Rounded(bounds, 6)) g.SetClip(clipPath);
                using (var fill = new SolidBrush(Enabled ? BackColor : Theme.HeaderBg)) g.FillRectangle(fill, button);
                g.Restore(buttonState);
                int cx = button.Left + button.Width / 2;
                int cy = button.Top + button.Height / 2;
                Point[] arrow = { new Point(cx - 4, cy - 2), new Point(cx + 4, cy - 2), new Point(cx, cy + 3) };
                using (var brush = new SolidBrush(Enabled ? Theme.Muted : Theme.Disabled)) g.FillPolygon(brush, arrow);
                using (GraphicsPath path = ModernButton.Rounded(bounds, 6))
                using (var border = new Pen(Focused ? Theme.Accent : Theme.Border)) g.DrawPath(border, path);
            }
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Color back = selected ? Theme.Sel : Theme.Surface;
            using (var brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, e.Bounds);
            var textRect = new Rectangle(e.Bounds.Left + 9, e.Bounds.Top - 1, Math.Max(1, e.Bounds.Width - 14), e.Bounds.Height + 1);
            TextRenderer.DrawText(e.Graphics, Convert.ToString(Items[e.Index]), Font, textRect, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if ((e.State & DrawItemState.Focus) != 0 && !selected) e.DrawFocusRectangle();
        }
    }

    internal sealed class ModernNumericUpDown : NumericUpDown
    {
        public ModernNumericUpDown()
        {
            BorderStyle = BorderStyle.FixedSingle; BackColor = Theme.Surface; ForeColor = Theme.Text;
            Font = Theme.UiFont; TextAlign = HorizontalAlignment.Right;
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ModernControlShape.Apply(this, 5); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); ModernControlShape.Apply(this, 5); }
        protected override void OnEnter(EventArgs e) { base.OnEnter(e); BackColor = Theme.IsDark ? Theme.HeaderBg : Color.White; }
        protected override void OnLeave(EventArgs e) { base.OnLeave(e); BackColor = Theme.Surface; }
    }

    internal sealed class ModernListView : ListView
    {
        private readonly ImageList _rowHeight;
        public ModernListView()
        {
            OwnerDraw = true; BorderStyle = BorderStyle.None; BackColor = Theme.Surface; ForeColor = Theme.Text;
            Font = Theme.UiFont; FullRowSelect = true; HideSelection = false;
            _rowHeight = new ImageList { ImageSize = new Size(1, 28), ColorDepth = ColorDepth.Depth8Bit };
            SmallImageList = _rowHeight;
        }
        protected override void Dispose(bool disposing) { if (disposing) _rowHeight.Dispose(); base.Dispose(disposing); }
        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            using (var brush = new SolidBrush(Theme.HeaderBg)) e.Graphics.FillRectangle(brush, e.Bounds);
            using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            var rect = new Rectangle(e.Bounds.Left + 9, e.Bounds.Top, Math.Max(1, e.Bounds.Width - 14), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, Theme.UiFontBold, rect, Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            if (View != View.Details) e.DrawDefault = true;
        }
        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            bool selected = e.Item.Selected;
            Color back = selected ? Theme.Sel : (e.ItemIndex % 2 == 0 ? Theme.Surface : Theme.RowAlt);
            using (var brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, e.Bounds);
            var rect = new Rectangle(e.Bounds.Left + 9, e.Bounds.Top, Math.Max(1, e.Bounds.Width - 14), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, rect, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using (var pen = new Pen(Theme.GridLine)) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }
    }
}
