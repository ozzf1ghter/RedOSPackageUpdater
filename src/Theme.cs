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
        public static readonly Color Bg          = Color.FromArgb(233, 235, 239);
        public static readonly Color SidebarBg   = Color.FromArgb(238, 240, 243);
        public static readonly Color Surface     = Color.White;
        public static readonly Color Text        = Color.FromArgb(30, 33, 39);
        public static readonly Color Muted       = Color.FromArgb(104, 110, 120);
        public static readonly Color Disabled    = Color.FromArgb(190, 193, 199);
        public static readonly Color Border      = Color.FromArgb(188, 193, 202);
        public static readonly Color Accent      = Color.FromArgb(37, 99, 235);
        public static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
        public static readonly Color AccentDown  = Color.FromArgb(30, 64, 175);
        public static readonly Color AccentTint  = Color.FromArgb(235, 242, 255);   // светлая заливка для "+ Система/Узел"
        public static readonly Color Danger      = Color.FromArgb(201, 58, 58);
        public static readonly Color DangerHover = Color.FromArgb(176, 44, 44);
        public static readonly Color DangerTint  = Color.FromArgb(253, 237, 237);
        public static readonly Color Good        = Color.FromArgb(34, 139, 84);
        public static readonly Color Warn        = Color.FromArgb(181, 125, 11);
        public static readonly Color GridLine    = Color.FromArgb(224, 227, 233);
        public static readonly Color HeaderBg    = Color.FromArgb(240, 241, 245);
        public static readonly Color RowAlt      = Color.FromArgb(246, 247, 249);
        public static readonly Color Sel         = Color.FromArgb(222, 235, 255);

        public static readonly Font UiFont      = new Font("Segoe UI", 9F, FontStyle.Regular);
        public static readonly Font UiFontBold  = new Font("Segoe UI", 9F, FontStyle.Bold);
        public static readonly Font UiFontSmall = new Font("Segoe UI", 8F, FontStyle.Bold);
        public static readonly Font Mono        = new Font("Consolas", 9F, FontStyle.Regular);

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
            FlatBase(b);
            b.FlatAppearance.BorderSize = 0;
            WireDisabledState(b, Accent, Color.White, AccentHover, AccentDown);
        }

        // Второстепенная кнопка: белый фон, тонкая рамка - Предпроверка и большинство остальных
        public static void Secondary(Button b)
        {
            FlatBase(b);
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.BorderSize = 1;
            WireDisabledState(b, Surface, Text, Color.FromArgb(238, 240, 244), Color.FromArgb(228, 231, 236));
        }

        // Компактная кнопка панели: остаётся полноценной целью для клика и клавиатуры,
        // но не конкурирует визуально с основным действием экрана.
        public static Button ToolbarButton(string text, int width)
        {
            var b = new Button { Text = text, Width = width, Height = 26, Margin = new Padding(4, 0, 0, 0) };
            Secondary(b);
            b.Font = UiFont;
            return b;
        }

        // Опасное действие (жирная заливка): Стоп
        public static void Danger_(Button b)
        {
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
            t.DrawMode = TreeViewDrawMode.OwnerDrawText;
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

            var bounds = e.Bounds;
            bounds.Width = Math.Max(0, tree.ClientSize.Width - bounds.Left - 2);

            // Фон/текст выделенной строки красим сами через Theme.Sel/Theme.Text, а не через
            // SystemColors.Highlight/HighlightText - обнаружено на живой проверке: под mono/Linux
            // (и потенциально в кастомных темах Windows) HighlightText может фактически совпасть
            // с фоном, из-за чего текст выбранного узла становится невидимым. Своя палитра даёт
            // гарантированный контраст независимо от системной темы - тот же принцип, по которому
            // весь остальной UI этого приложения уже не полагается на системные цвета.
            if (selected)
                using (var b = new SolidBrush(Sel)) e.Graphics.FillRectangle(b, e.Bounds);
            var fg = node.ForeColor != Color.Empty ? node.ForeColor : Text;

            TextRenderer.DrawText(e.Graphics, node.Text, font, bounds, fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Left);
        }

        // ---- MenuStrip ----
        public static void Menu(MenuStrip m)
        {
            m.BackColor = Surface;
            m.ForeColor = Text;
            m.Font = UiFont;
            m.Renderer = new ToolStripProfessionalRenderer(new MenuColors());
            m.GripStyle = ToolStripGripStyle.Hidden;
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
            f.BackColor = Surface;
            f.ForeColor = Text;
            f.ShowInTaskbar = false;
            ApplyDialogControls(f);
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
                }
                if (c.HasChildren) ApplyDialogControls(c);
            }
        }

        // светлая палитра для меню
        private class MenuColors : ProfessionalColorTable
        {
            public MenuColors() { this.UseSystemColors = false; }
            public override Color MenuItemSelected { get { return Sel; } }
            public override Color MenuItemSelectedGradientBegin { get { return Sel; } }
            public override Color MenuItemSelectedGradientEnd { get { return Sel; } }
            public override Color MenuItemBorder { get { return Accent; } }
            public override Color MenuBorder { get { return Border; } }
            public override Color MenuItemPressedGradientBegin { get { return Surface; } }
            public override Color MenuItemPressedGradientEnd { get { return Surface; } }
            public override Color ToolStripDropDownBackground { get { return Surface; } }
            public override Color ImageMarginGradientBegin { get { return Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Surface; } }
            public override Color ImageMarginGradientEnd { get { return Surface; } }
            public override Color SeparatorDark { get { return Border; } }
            public override Color MenuStripGradientBegin { get { return Surface; } }
            public override Color MenuStripGradientEnd { get { return Surface; } }
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
                case Kind.Busy: _bg = Color.FromArgb(224, 234, 255); _fg = Theme.AccentDown; break;
                case Kind.Good: _bg = Color.FromArgb(223, 242, 227); _fg = Theme.Good; break;
                case Kind.Warn: _bg = Color.FromArgb(252, 238, 210); _fg = Theme.Warn; break;
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
