using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace RedOSPackageUpdater
{
    internal static class AppDialog
    {
        public static void Info(IWin32Window owner, string title, string message)
        {
            Show(owner, title, message, false);
        }

        public static void Error(IWin32Window owner, string title, string message)
        {
            Show(owner, title, message, true);
        }

        public static bool Confirm(IWin32Window owner, string title, string message, string okText)
        {
            string actionText = string.IsNullOrEmpty(okText) ? "Продолжить" : okText;
            int actionWidth = Math.Max(100, Math.Min(180, TextRenderer.MeasureText(actionText, Theme.UiFontBold).Width + 28));
            using (var f = new Form
            {
                Text = title, FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent, MinimizeBox = false,
                MaximizeBox = false, ShowInTaskbar = false, ClientSize = new Size(540, 220),
                BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.UiFont,
                AutoScaleMode = AutoScaleMode.Dpi
            })
            {
                var stripe = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = Theme.Accent };
                var text = new Label { Left = 24, Top = 18, Width = 492, Height = 138, Text = message ?? "", AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
                var footer = new Panel { Dock = DockStyle.Bottom, Height = 54, BackColor = Theme.HeaderBg };
                Theme.EdgeLine(footer, DockStyle.Top);
                var ok = new ModernButton { Text = actionText, Width = actionWidth, Height = 32, Left = 528 - actionWidth, Top = 11, DialogResult = DialogResult.OK };
                var cancel = new ModernButton { Text = "Отмена", Width = 92, Height = 32, Left = ok.Left - 102, Top = 11, DialogResult = DialogResult.Cancel };
                Theme.Secondary(cancel); Theme.Primary(ok);
                footer.Controls.Add(cancel); footer.Controls.Add(ok);
                f.Controls.Add(stripe); f.Controls.Add(text); f.Controls.Add(footer);
                f.AcceptButton = ok; f.CancelButton = cancel;
                Theme.AnimateDialog(f);
                return f.ShowDialog(owner) == DialogResult.OK;
            }
        }

        public static bool About(IWin32Window owner, string version)
        {
            using (var f = new Form
            {
                Text = "О программе", FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false,
                ShowInTaskbar = false, ClientSize = new Size(470, 246), BackColor = Theme.Surface,
                ForeColor = Theme.Text, Font = Theme.UiFont, AutoScaleMode = AutoScaleMode.Dpi
            })
            {
                var stripe = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = Theme.Accent };
                var mark = new AppIconView { Left = 24, Top = 25, Width = 48, Height = 48, BackColor = Theme.Surface };
                var title = new Label { Left = 88, Top = 24, Width = 350, Height = 27, Text = "RED OS Package Updater", Font = Theme.UiFontBold, ForeColor = Theme.Text };
                var build = new Label { Left = 89, Top = 53, Width = 330, Height = 22, Text = "Версия " + version, ForeColor = Theme.Muted };
                var description = new Label { Left = 24, Top = 96, Width = 414, Height = 54, Text = "Профессиональное управление обновлениями и проверкой уязвимостей серверов RED OS по SSH.", ForeColor = Theme.Text };
                var footer = new Panel { Dock = DockStyle.Bottom, Height = 62, BackColor = Theme.HeaderBg };
                Theme.EdgeLine(footer, DockStyle.Top);
                var update = new ModernButton { Text = "Проверить обновления", Left = 24, Top = 15, Width = 170, Height = 32, DialogResult = DialogResult.Retry };
                var close = new ModernButton { Text = "Закрыть", Left = 354, Top = 15, Width = 92, Height = 32, DialogResult = DialogResult.Cancel };
                Theme.Secondary(update); Theme.Primary(close);
                footer.Controls.Add(update); footer.Controls.Add(close);
                f.Controls.AddRange(new Control[] { stripe, mark, title, build, description, footer });
                f.AcceptButton = close; f.CancelButton = close;
                Theme.AnimateDialog(f);
                return f.ShowDialog(owner) == DialogResult.Retry;
            }
        }

        public static DialogResult ImportChoice(IWin32Window owner)
        {
            using (var f = new Form
            {
                Text = "Импорт конфигурации", FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false,
                ShowInTaskbar = false, ClientSize = new Size(520, 210), BackColor = Theme.Surface,
                ForeColor = Theme.Text, Font = Theme.UiFont, AutoScaleMode = AutoScaleMode.Dpi
            })
            {
                var stripe = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = Theme.Accent };
                var title = new Label { Left = 24, Top = 22, Width = 472, Height = 27, Text = "Как применить импортированные данные?", Font = Theme.UiFontBold };
                var hint = new Label { Left = 24, Top = 56, Width = 472, Height = 54, Text = "Заменить — текущие системы и учётки будут заменены.\nДобавить — новые данные будут объединены с текущими без дублей.", ForeColor = Theme.Muted };
                var footer = new Panel { Dock = DockStyle.Bottom, Height = 62, BackColor = Theme.HeaderBg };
                Theme.EdgeLine(footer, DockStyle.Top);
                var cancel = new ModernButton { Text = "Отмена", Left = 24, Top = 15, Width = 92, Height = 32, DialogResult = DialogResult.Cancel };
                var merge = new ModernButton { Text = "Добавить", Left = 308, Top = 15, Width = 92, Height = 32, DialogResult = DialogResult.No };
                var replace = new ModernButton { Text = "Заменить", Left = 410, Top = 15, Width = 92, Height = 32, DialogResult = DialogResult.Yes };
                Theme.Secondary(cancel); Theme.Secondary(merge); Theme.Primary(replace);
                footer.Controls.AddRange(new Control[] { cancel, merge, replace });
                f.Controls.AddRange(new Control[] { stripe, title, hint, footer });
                f.CancelButton = cancel;
                Theme.AnimateDialog(f);
                return f.ShowDialog(owner);
            }
        }

        private static void Show(IWin32Window owner, string title, string message, bool error)
        {
            string body = message ?? "";
            const int dialogWidth = 500;
            Size measured = TextRenderer.MeasureText(body, Theme.UiFont, new Size(452, 220), TextFormatFlags.WordBreak);
            int dialogHeight = Math.Max(152, Math.Min(320, measured.Height + 104));
            using (var f = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(dialogWidth, dialogHeight),
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                Font = Theme.UiFont
            })
            {
                f.AutoScaleMode = AutoScaleMode.Dpi;
                var stripe = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = error ? Theme.Danger : Theme.Accent };
                var text = new Label
                {
                    Left = 24, Top = 18, Width = 452, Height = dialogHeight - 88,
                    Text = body, AutoEllipsis = true,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = Theme.UiFont,
                    ForeColor = Theme.Text
                };
                var footer = new Panel { Dock = DockStyle.Bottom, Height = 54, BackColor = Theme.HeaderBg };
                Theme.EdgeLine(footer, DockStyle.Top);
                var ok = new ModernButton { Text = "Понятно", Width = 92, Height = 32, Left = 384, Top = 11, DialogResult = DialogResult.OK };
                Theme.Primary(ok);
                footer.Controls.Add(ok);
                f.Controls.Add(stripe);
                f.Controls.Add(text);
                f.Controls.Add(footer);
                f.AcceptButton = ok;
                f.CancelButton = ok;
                f.Shown += delegate { ok.Focus(); };
                Theme.AnimateDialog(f);
                f.ShowDialog(owner);
            }
        }
    }

    internal sealed class UpdateProgressDialog : Form
    {
        private readonly ModernProgressBar _bar;
        private readonly Label _details;
        private readonly Label _title;

        public UpdateProgressDialog()
        {
            Text = "Обновление программы";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false; ControlBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(460, 138);
            BackColor = Theme.Surface; ForeColor = Theme.Text; Font = Theme.UiFont;
            var stripe = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = Theme.Accent };
            _title = new Label { Left = 22, Top = 18, Width = 416, Height = 24, Text = "Скачивание новой версии…", Font = Theme.UiFontBold };
            _bar = new ModernProgressBar { Left = 22, Top = 56, Width = 416, Height = 9, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 24 };
            _details = new Label { Left = 22, Top = 82, Width = 416, Height = 26, Text = "Подключение к GitHub…", ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft };
            var hint = new Label { Left = 22, Top = 111, Width = 416, Height = 18, Text = "После проверки файл будет установлен автоматически.", ForeColor = Theme.Muted };
            Controls.AddRange(new Control[] { stripe, _title, _bar, _details, hint });
            Theme.AnimateDialog(this);
        }

        public void SetStage(string title, string details)
        {
            _title.Text = title ?? "";
            _details.Text = details ?? "";
            _bar.Style = ProgressBarStyle.Marquee;
            _bar.MarqueeAnimationSpeed = 24;
        }

        public void SetProgress(long done, long total)
        {
            if (total > 0)
            {
                int percent = (int)Math.Min(100, done * 100 / total);
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = percent;
                _details.Text = string.Format("{0}%  —  {1:0.0} из {2:0.0} МБ", percent, done / 1048576d, total / 1048576d);
            }
            else
            {
                _bar.Style = ProgressBarStyle.Marquee;
                _details.Text = string.Format("Загружено {0:0.0} МБ", done / 1048576d);
            }
        }
    }

    // Универсальный ввод строки/многострочный.
    internal static class Prompt
    {
        public static string Show(string title, string label, string def, bool multiline, Size size)
        {
            using (var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ClientSize = size })
            {
                var lbl = new Label { Text = label, Left = 10, Top = 8, Width = size.Width - 20, AutoSize = false, Height = 20 };
                var tb = new ModernTextBox { Left = 10, Top = 30, Width = size.Width - 20, Text = def ?? "", Multiline = multiline, Height = multiline ? size.Height - 80 : 28, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None, AcceptsReturn = multiline };
                var ok = new ModernButton { Text = "Сохранить", DialogResult = DialogResult.OK, Width = 100, Height = 30, Top = size.Height - 42, Left = size.Width - 220 };
                var cancel = new ModernButton { Text = "Отмена", DialogResult = DialogResult.Cancel, Width = 100, Height = 30, Top = size.Height - 42, Left = size.Width - 110 };
                f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
                Theme.Dialog(f);
                f.AcceptButton = multiline ? null : ok; f.CancelButton = cancel;
                return f.ShowDialog() == DialogResult.OK ? tb.Text : null;
            }
        }

    }


}
