using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RedOSPackageUpdater
{
    // Единая светлая тема: скруглённые плоские элементы, Segoe UI, аккуратный грид,
    // явное неактивное состояние кнопок, цветной статус-чип.
    // Всё в C# 5 (совместимо со старым csc.exe .NET Framework), без интерполяции строк
    // и expression-bodied свойств.
    internal static class Theme
    {
        public static Color Bg, SidebarBg, Surface, Text, Muted, Disabled, Border, Accent, AccentHover,
            AccentDown, AccentTint, Danger, DangerHover, DangerTint, Good, Warn, GridLine, HeaderBg,
            RowAlt, Sel, NavigationBg, NavigationHover, NavigationActive, NavigationText;
        public static bool IsDark { get; private set; }

        public static readonly Font UiFont      = new Font("Segoe UI", 9.25F, FontStyle.Regular);
        public static readonly Font UiFontBold  = new Font("Segoe UI Semibold", 9.25F, FontStyle.Regular);
        public static readonly Font UiFontSmall = new Font("Segoe UI", 8F, FontStyle.Bold);
        public static readonly Font UiFontBodyLarge = new Font("Segoe UI", 9.5F, FontStyle.Regular);
        public static readonly Font UiFontHeading = new Font("Segoe UI Semibold", 10.5F, FontStyle.Regular);
        public static readonly Font UiFontHeadingLarge = new Font("Segoe UI Semibold", 11F, FontStyle.Regular);
        public static readonly Font UiFontPageTitle = new Font("Segoe UI Semibold", 16F, FontStyle.Regular);
        public static readonly Font UiFontBrand = new Font("Segoe UI Semibold", 10F, FontStyle.Regular);
        public static readonly Font UiFontBrandMark = new Font("Segoe UI Semibold", 15F, FontStyle.Regular);
        public static readonly Font UiFontBrandSmall = new Font("Segoe UI", 8.5F, FontStyle.Regular);
        public static readonly Font Mono        = new Font("Consolas", 9F, FontStyle.Regular);

        static Theme() { Configure(false); }

        public static void Configure(bool dark)
        {
            IsDark = dark;
            Accent = dark ? Color.FromArgb(99, 145, 255) : Color.FromArgb(38, 91, 207);
            AccentHover = dark ? Color.FromArgb(121, 161, 255) : Color.FromArgb(30, 75, 177);
            AccentDown = dark ? Color.FromArgb(76, 122, 231) : Color.FromArgb(24, 61, 148);
            Danger = dark ? Color.FromArgb(235, 98, 98) : Color.FromArgb(201, 58, 58);
            DangerHover = dark ? Color.FromArgb(244, 121, 121) : Color.FromArgb(176, 44, 44);
            Good = dark ? Color.FromArgb(75, 196, 132) : Color.FromArgb(34, 139, 84);
            Warn = dark ? Color.FromArgb(235, 178, 73) : Color.FromArgb(181, 125, 11);
            if (dark)
            {
                Bg = Color.FromArgb(14, 18, 27); SidebarBg = Color.FromArgb(20, 25, 36); Surface = Color.FromArgb(24, 30, 42);
                Text = Color.FromArgb(235, 239, 247); Muted = Color.FromArgb(151, 162, 181); Disabled = Color.FromArgb(91, 101, 117);
                Border = Color.FromArgb(51, 61, 78); HeaderBg = Color.FromArgb(31, 38, 52); RowAlt = Color.FromArgb(27, 34, 47);
                GridLine = Color.FromArgb(45, 54, 70); Sel = Color.FromArgb(39, 60, 96); AccentTint = Color.FromArgb(30, 49, 82);
                DangerTint = Color.FromArgb(67, 35, 42); NavigationBg = Color.FromArgb(11, 15, 24);
                NavigationHover = Color.FromArgb(27, 35, 50); NavigationActive = Color.FromArgb(35, 65, 119); NavigationText = Color.FromArgb(188, 199, 217);
            }
            else
            {
                Bg = Color.FromArgb(244, 246, 249); SidebarBg = Color.FromArgb(248, 249, 251); Surface = Color.White;
                Text = Color.FromArgb(27, 36, 51); Muted = Color.FromArgb(101, 112, 130); Disabled = Color.FromArgb(190, 193, 199);
                Border = Color.FromArgb(218, 223, 231); HeaderBg = Color.FromArgb(247, 248, 250); RowAlt = Color.FromArgb(250, 251, 252);
                GridLine = Color.FromArgb(231, 234, 239); Sel = Color.FromArgb(230, 238, 253); AccentTint = Color.FromArgb(235, 242, 255);
                DangerTint = Color.FromArgb(253, 237, 237); NavigationBg = Color.FromArgb(18, 28, 46);
                NavigationHover = Color.FromArgb(30, 44, 67); NavigationActive = Color.FromArgb(35, 77, 154); NavigationText = Color.FromArgb(205, 214, 228);
            }
        }

        // ---- Кнопки ----
        // Скругление углов через Control.Region пробовали и убрали: WinForms всё равно рисует
        // рамку кнопки (FlatAppearance.BorderColor) прямоугольником по полной ClientRectangle,
        // а Region её обрезает только у части кнопок (сплошные без рамки - незаметно, кнопки
        // с рамкой - обрыв в углу) - в итоге одни кнопки выглядели скруглёнными, другие нет.
        // Проще и надёжнее - все кнопки прямоугольные, единый стиль без исключений.
        private static void FlatBase(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.Font = UiFont;
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
            if (b.Height < 28) b.Height = 28;
            b.Padding = new Padding(2, 0, 2, 0);
        }

        // Явно гасим цвет при Enabled=false - иначе Primary/Danger остаются такими же яркими,
        // как активные, и пользователь не видит, что кнопка сейчас недоступна.
        private static void WireDisabledState(Button b, Color enabledBack, Color enabledFore, Color hoverBack, Color downBack)
        {
            EventHandler apply = delegate
            {
                if (b.Enabled)
                {
                    b.BackColor = enabledBack; b.ForeColor = enabledFore;
                    b.FlatAppearance.MouseOverBackColor = hoverBack;
                    b.FlatAppearance.MouseDownBackColor = downBack;
                }
                else
                {
                    b.BackColor = Color.FromArgb(238, 239, 241); b.ForeColor = Disabled;
                    b.FlatAppearance.MouseOverBackColor = b.BackColor;
                    b.FlatAppearance.MouseDownBackColor = b.BackColor;
                }
            };
            b.EnabledChanged += apply;
            apply(b, EventArgs.Empty);
        }

        // Основная кнопка (акцент, сплошная заливка): Запустить
        public static void Primary(Button b)
        {
            var modern = b as ModernButton;
            if (modern != null) { modern.Kind = ModernButtonKind.Primary; modern.Font = UiFontBold; return; }
            FlatBase(b);
            b.FlatAppearance.BorderSize = 0;
            WireDisabledState(b, Accent, Color.White, AccentHover, AccentDown);
        }

        // Второстепенная кнопка: белый фон, тонкая рамка - Предпроверка и большинство остальных
        public static void Secondary(Button b)
        {
            var modern = b as ModernButton;
            if (modern != null) { modern.Kind = ModernButtonKind.Secondary; return; }
            FlatBase(b);
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.BorderSize = 1;
            WireDisabledState(b, Surface, Text, Color.FromArgb(238, 240, 244), Color.FromArgb(228, 231, 236));
        }

        // Компактная кнопка панели: остаётся полноценной целью для клика и клавиатуры,
        // но не конкурирует визуально с основным действием экрана.
        public static Button ToolbarButton(string text, int width)
        {
            var b = new ModernButton { Text = text, Width = width, Height = 28, Margin = new Padding(4, 0, 0, 0) };
            Secondary(b);
            b.Font = UiFont;
            return b;
        }

        // Опасное действие (жирная заливка): Стоп
        public static void Danger_(Button b)
        {
            var modern = b as ModernButton;
            if (modern != null) { modern.Kind = ModernButtonKind.Danger; modern.Font = UiFontBold; return; }
            FlatBase(b);
            b.FlatAppearance.BorderSize = 0;
            WireDisabledState(b, Danger, Color.White, DangerHover, DangerHover);
        }

        // ---- ComboBox ----
        public static void Combo(ComboBox c)
        {
            c.FlatStyle = FlatStyle.Flat;
            c.Font = UiFont;
            c.BackColor = Surface;
            c.ForeColor = Text;
        }

        // ComboBox не поддерживает FlatAppearance.BorderColor как кнопки - её "плоская" рамка
        // рисуется системным цветом и на светлом фоне почти не видна. Оборачиваем в панель
        // и рисуем свою рамку тем же Border, что у кнопок - чтобы читалось как единый стиль.
        // ВАЖНО: Paint подписывается через += (анонимный делегат, без возможности снять подписку) -
        // вызывать РОВНО ОДИН раз на экземпляр контрола. Повторный вызов на том же контроле
        // (например, при будущем рефакторинге, который пересоздаёт UI без пересоздания контролов)
        // добавит второй обработчик, и рамка будет рисоваться дважды за каждый repaint - не крашит,
        // но лишняя нагрузка и потенциально видимый на глаз двойной штрих. Сейчас в MainForm.BuildUi()
        // каждый контрол оформляется один раз - инвариант соблюдён, но его стоит держать в уме.
        public static void Box(Control p)
        {
            if (p is ModernCard) return;
            p.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Border))
                {
                    var r = p.ClientRectangle;
                    e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
                }
            };
        }

        // ---- CheckBox ----
        // По умолчанию CheckBox.AutoSize = true и высота подгоняется под текст -
        // из-за этого квадратик галочки визуально "плавает" не по центру относительно
        // своей же подписи. Фиксируем Height/AutoSize явно, чтобы глиф встал по центру.
        public static void Check(CheckBox c)
        {
            c.AutoSize = false;
            c.Font = UiFont;
            c.ForeColor = Text;
            c.UseVisualStyleBackColor = true;
            c.CheckAlign = ContentAlignment.MiddleLeft;
            c.TextAlign = ContentAlignment.MiddleLeft;
            if (c.Height < 22) c.Height = 22;
        }

        // ---- DataGridView ----
        public static void Grid(DataGridView g)
        {
            g.EnableHeadersVisualStyles = false;
            g.BackgroundColor = Surface;
            g.BorderStyle = BorderStyle.None;
            g.GridColor = GridLine;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            g.AllowUserToResizeRows = false;
            g.Font = UiFont;

            g.ColumnHeadersDefaultCellStyle.BackColor = HeaderBg;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
            g.ColumnHeadersDefaultCellStyle.Font = UiFontBold;
            g.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 4, 0);
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            g.ColumnHeadersHeight = 34;

            g.DefaultCellStyle.BackColor = Surface;
            g.DefaultCellStyle.ForeColor = Text;
            g.DefaultCellStyle.SelectionBackColor = Sel;
            g.DefaultCellStyle.SelectionForeColor = Text;
            g.DefaultCellStyle.Padding = new Padding(6, 0, 4, 0);
            g.AlternatingRowsDefaultCellStyle.BackColor = RowAlt;
            g.RowTemplate.Height = 28;
        }

        // ---- TreeView ----
        public static void Tree(TreeView t)
        {
            t.BorderStyle = BorderStyle.None;
            t.BackColor = Surface;
            t.ForeColor = Text;
            t.Font = UiFont;
            t.ItemHeight = 26;
            t.ShowLines = false;
            t.ShowPlusMinus = true;
            t.HideSelection = false;
            t.FullRowSelect = true;
            t.ShowNodeToolTips = true;
            t.DrawMode = TreeViewDrawMode.OwnerDrawAll;
            t.DrawNode += TreeDrawNode;
        }

        // Рисуем текст узла сами: реальное измерение реальным шрифтом в момент
        // отрисовки + многоточие, если не помещается. Никаких угаданных ширин -
        // выглядит правильно при любой длине имени и на любой машине.
        private static void TreeDrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            var tree = (TreeView)sender;
            var node = e.Node;
            var font = node.NodeFont ?? tree.Font;
            bool selected = (e.State & TreeNodeStates.Selected) != 0;

            // OwnerDrawText оставлял системную синюю заливку под кнопкой раскрытия и чекбоксом,
            // а нашу светлую — только под текстом. Рисуем всю строку и служебные глифы сами,
            // поэтому выделение теперь выглядит как одна спокойная полоса без цветных обрывов.
            var row = new Rectangle(0, e.Bounds.Top, tree.ClientSize.Width, tree.ItemHeight);
            using (var bg = new SolidBrush(selected ? Sel : tree.BackColor)) e.Graphics.FillRectangle(bg, row);

            int centerY = row.Top + row.Height / 2;
            int levelX = 8 + node.Level * tree.Indent;
            int glyphX = levelX;
            int checkX = levelX + 18;
            int textX = levelX + 38;

            if (node.Nodes.Count > 0)
            {
                var glyph = new Rectangle(glyphX, centerY - 5, 10, 10);
                using (var pen = new Pen(Border)) e.Graphics.DrawRectangle(pen, glyph);
                using (var pen = new Pen(Muted))
                {
                    e.Graphics.DrawLine(pen, glyph.Left + 2, centerY, glyph.Right - 2, centerY);
                    if (!node.IsExpanded) e.Graphics.DrawLine(pen, glyph.Left + 5, glyph.Top + 2, glyph.Left + 5, glyph.Bottom - 2);
                }
            }

            if (tree.CheckBoxes)
            {
                var check = new Rectangle(checkX, centerY - 8, 16, 16);
                using (GraphicsPath path = ModernButton.Rounded(check, 4))
                using (var brush = new SolidBrush(node.Checked ? Accent : Surface)) e.Graphics.FillPath(brush, path);
                using (GraphicsPath path = ModernButton.Rounded(check, 4))
                using (var pen = new Pen(node.Checked ? Accent : Border)) e.Graphics.DrawPath(pen, path);
                if (node.Checked)
                    using (var pen = new Pen(Color.White, 2F))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        e.Graphics.DrawLines(pen, new[] { new Point(check.Left + 4, centerY), new Point(check.Left + 7, centerY + 3), new Point(check.Left + 13, centerY - 3) });
                    }
            }

            var bounds = new Rectangle(textX, row.Top, Math.Max(0, tree.ClientSize.Width - textX - 4), row.Height);
            var fg = node.ForeColor != Color.Empty ? node.ForeColor : Text;
            TextRenderer.DrawText(e.Graphics, node.Text, font, bounds, fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Left);
        }

        public static void ContextMenu(ContextMenuStrip menu)
        {
            menu.BackColor = Surface; menu.ForeColor = Text; menu.Font = UiFont;
            menu.ShowImageMargin = false; menu.Padding = new Padding(5);
            menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors());
        }

        // ---- Подзаголовок секции (маленькая серая надпись капсом - "СЕРВЕРЫ", "ОЧЕРЕДЬ") ----
        public static Label SectionLabel(string text)
        {
            return new Label
            {
                Text = text.ToUpperInvariant(),
                Font = UiFontSmall,
                ForeColor = Muted,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        // ---- Панель с тонкой линией по краю (эмуляция border) ----
        public static void EdgeLine(Control p, DockStyle side)
        {
            p.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Border))
                {
                    var r = p.ClientRectangle;
                    if (side == DockStyle.Bottom) e.Graphics.DrawLine(pen, 0, r.Height - 1, r.Width, r.Height - 1);
                    else if (side == DockStyle.Right) e.Graphics.DrawLine(pen, r.Width - 1, 0, r.Width - 1, r.Height);
                    else if (side == DockStyle.Top) e.Graphics.DrawLine(pen, 0, 0, r.Width, 0);
                    else if (side == DockStyle.Left) e.Graphics.DrawLine(pen, 0, 0, 0, r.Height);
                }
            };
        }

        // Общая нормализация старых диалогов. Она не меняет их логику и размеры,
        // но убирает смесь системных и фирменных контролов по разным окнам.
        public static void Dialog(Form f)
        {
            f.Font = UiFont;
            f.AutoScaleMode = AutoScaleMode.Dpi;
            f.BackColor = Surface;
            f.ForeColor = Text;
            f.ShowInTaskbar = false;
            ApplyDialogControls(f);
            AnimateDialog(f);
        }

        public static void AnimateDialog(Form form)
        {
            if (form == null || !SystemInformation.IsMenuAnimationEnabled) return;
            try { form.Opacity = 0D; }
            catch { return; }
            form.Shown += delegate
            {
                var timer = new Timer { Interval = 16 };
                timer.Tick += delegate
                {
                    if (form.IsDisposed) { timer.Stop(); timer.Dispose(); return; }
                    try
                    {
                        form.Opacity = Math.Min(1D, form.Opacity + 0.16D);
                        if (form.Opacity >= 1D) { timer.Stop(); timer.Dispose(); }
                    }
                    catch { timer.Stop(); timer.Dispose(); }
                };
                timer.Start();
            };
        }

        private static void ApplyDialogControls(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                var b = c as Button;
                if (b != null)
                {
                    if (b.DialogResult == DialogResult.OK) Primary(b); else Secondary(b);
                }
                else
                {
                    var tb = c as TextBox;
                    if (tb != null) { tb.BackColor = Surface; tb.ForeColor = Text; tb.BorderStyle = BorderStyle.FixedSingle; }
                    var cb = c as ComboBox;
                    if (cb != null) Combo(cb);
                    var check = c as CheckBox;
                    if (check != null) Check(check);
                    var grid = c as DataGridView;
                    if (grid != null) Grid(grid);
                    var panel = c as Panel;
                    if (panel != null && !(panel is ModernCard) && panel.BackColor != Color.Transparent) panel.BackColor = Surface;
                }
                if (c.HasChildren) ApplyDialogControls(c);
            }
        }

        private sealed class MenuColors : ProfessionalColorTable
        {
            public MenuColors() { UseSystemColors = false; }
            public override Color MenuItemSelected { get { return Sel; } }
            public override Color MenuItemSelectedGradientBegin { get { return Sel; } }
            public override Color MenuItemSelectedGradientEnd { get { return Sel; } }
            public override Color MenuItemBorder { get { return Accent; } }
            public override Color MenuBorder { get { return Border; } }
            public override Color ToolStripDropDownBackground { get { return Surface; } }
            public override Color ImageMarginGradientBegin { get { return Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Surface; } }
            public override Color ImageMarginGradientEnd { get { return Surface; } }
            public override Color SeparatorDark { get { return Border; } }
        }

    }

    // Статус-чип: скруглённая цветная плашка вместо голого текста ("идёт...", "Готово", "OK 5 / FAIL 1").
    // Цвет задаётся отдельно от текста через SetStatus(text, kind), чтобы состояние читалось с одного взгляда.
    internal class StatusChip : Label
    {
        public enum Kind { Idle, Busy, Good, Warn, Bad }

        private Color _bg = Theme.HeaderBg;
        private Color _fg = Theme.Muted;
        private Kind _kind = Kind.Idle;
        private bool _kindSet;

        public StatusChip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Font = Theme.UiFontBold;
            TextAlign = ContentAlignment.MiddleCenter;
        }

        public void SetStatus(string text, Kind kind)
        {
            // reposync-прогресс дёргает SetStatus на каждый пакет - не перерисовываем чип впустую,
            // если текст и категория не изменились с прошлого вызова.
            if (_kindSet && kind == _kind && text == Text) return;
            _kindSet = true; _kind = kind;
            Text = text;
            switch (kind)
            {
                case Kind.Busy: _bg = Theme.IsDark ? Color.FromArgb(31, 52, 88) : Color.FromArgb(224, 234, 255); _fg = Theme.IsDark ? Theme.Accent : Theme.AccentDown; break;
                case Kind.Good: _bg = Theme.IsDark ? Color.FromArgb(29, 63, 49) : Color.FromArgb(223, 242, 227); _fg = Theme.Good; break;
                case Kind.Warn: _bg = Theme.IsDark ? Color.FromArgb(67, 52, 26) : Color.FromArgb(252, 238, 210); _fg = Theme.Warn; break;
                case Kind.Bad:  _bg = Theme.DangerTint; _fg = Theme.DangerHover; break;
                default:        _bg = Theme.HeaderBg; _fg = Theme.Muted; break;
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = RoundedRectPath(rect, Math.Min(Height / 2, 10)))
            using (var brush = new SolidBrush(_bg))
                e.Graphics.FillPath(brush, path);
            TextRenderer.DrawText(e.Graphics, Text, Font, rect, _fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private static GraphicsPath RoundedRectPath(Rectangle b, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            if (d <= 0 || d >= b.Width || d >= b.Height) { p.AddRectangle(b); return p; }
            p.AddArc(b.X, b.Y, d, d, 180, 90);
            p.AddArc(b.Right - d, b.Y, d, d, 270, 90);
            p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
            p.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
