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
                var mark = new Label { Left = 24, Top = 25, Width = 48, Height = 48, Text = "R", TextAlign = ContentAlignment.MiddleCenter, BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.UiFontBold };
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
            _title = new Label { Left = 22, Top = 18, Width = 416, Height = 24, Text = "Скачивание новой версии...", Font = Theme.UiFontBold };
            _bar = new ModernProgressBar { Left = 22, Top = 56, Width = 416, Height = 9, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 24 };
            _details = new Label { Left = 22, Top = 82, Width = 416, Height = 26, Text = "Подключение к GitHub...", ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft };
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

    // Диалог одного узла.
    internal class NodeForm : Form
    {
        private TextBox _name, _host, _role;
        private NumericUpDown _port;
        private CheckBox _enabled;
        public Node Result;

        public NodeForm(Node existing)
        {
            Text = existing == null ? "Новый узел" : "Изменить узел";
            FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false; ClientSize = new Size(360, 210);

            AddLbl("Имя:", 12);
            _name = new ModernTextBox { Left = 110, Top = 10, Width = 230, Text = existing != null ? existing.Name : "", Placeholder = "Например, redos-app01" };
            AddLbl("Host/IP:", 42);
            _host = new ModernTextBox { Left = 110, Top = 40, Width = 230, Text = existing != null ? existing.Host : "", Placeholder = "IP-адрес или DNS-имя" };
            AddLbl("Порт:", 72);
            _port = new ModernNumericUpDown { Left = 110, Top = 70, Width = 80, Minimum = 1, Maximum = 65535 };
            // Value зажимаем в [1..65535]: битый порт из импорта/ручной правки конфига иначе роняет диалог (ArgumentOutOfRange).
            _port.Value = (existing != null && existing.Port >= 1 && existing.Port <= 65535) ? existing.Port : 22;
            AddLbl("Роль:", 102);
            _role = new ModernTextBox { Left = 110, Top = 100, Width = 230, Text = existing != null ? existing.Role : "", Placeholder = "Назначение узла" };
            _enabled = new ModernCheckBox { Left = 110, Top = 132, Width = 230, Text = "Включён", Checked = existing == null || existing.Enabled, BackColor = Theme.Surface };
            Theme.Check(_enabled);

            var ok = new ModernButton { Text = "Сохранить", Width = 100, Height = 30, Top = 166, Left = 140, DialogResult = DialogResult.OK };
            var cancel = new ModernButton { Text = "Отмена", Width = 100, Height = 30, Top = 166, Left = 250, DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) =>
            {
                if (string.IsNullOrEmpty(_host.Text.Trim())) { AppDialog.Info(this, "Проверьте данные", "Укажите Host/IP."); DialogResult = DialogResult.None; return; }
                // Раньше проверялся только Host - узел с пустым именем проходил беспрепятственно и
                // в дереве выглядел неотличимо от других строк (пустая строка вместо имени).
                if (string.IsNullOrEmpty(_name.Text.Trim())) { AppDialog.Info(this, "Проверьте данные", "Укажите имя узла."); DialogResult = DialogResult.None; return; }
                Result = new Node { Name = _name.Text.Trim(), Host = _host.Text.Trim(), Port = (int)_port.Value, Role = _role.Text.Trim(), Enabled = _enabled.Checked };
            };
            Controls.AddRange(new Control[] { _name, _host, _port, _role, _enabled, ok, cancel });
            Theme.Dialog(this);
            AcceptButton = ok; CancelButton = cancel;
        }
        private void AddLbl(string t, int top) { Controls.Add(new Label { Text = t, Left = 12, Top = top + 2, Width = 95 }); }
    }

    // Массовый ввод узлов (копипаст: "имя ip" / "имя<tab>ip" / просто ip, по строке на узел).
    internal class BulkNodesForm : Form
    {
        private TextBox _tb;
        public List<Node> Result;

        public BulkNodesForm()
        {
            Text = "Массовый ввод узлов";
            StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(460, 380);
            var lbl = new Label { Text = "Вставьте строки. Форматы: 'имя IP', 'имя<tab>IP' или просто 'IP' (по строке на узел).", Left = 10, Top = 8, Width = 440, Height = 34 };
            // Theme.Mono - общий на всё приложение шрифт Consolas 9, а не новый Font на каждое открытие
            // диалога: Control.Dispose() не освобождает шрифт, назначенный через свойство Font (WinForms
            // не считает себя его владельцем) - при повторных открытиях это была утечка GDI-хендлов.
            _tb = new TextBox { Left = 10, Top = 44, Width = 440, Height = 280, Multiline = true, ScrollBars = ScrollBars.Both, AcceptsReturn = true, WordWrap = false, Font = Theme.Mono };
            var ok = new ModernButton { Text = "Добавить", Width = 100, Height = 30, Top = 332, Left = 250, DialogResult = DialogResult.OK };
            var cancel = new ModernButton { Text = "Отмена", Width = 100, Height = 30, Top = 332, Left = 360, DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) => { Result = Parse(_tb.Text); };
            Controls.AddRange(new Control[] { lbl, _tb, ok, cancel });
            Theme.Dialog(this);
            AcceptButton = ok; CancelButton = cancel;
        }

        // Формат "\d{1,3}" сам по себе не проверяет диапазон октета (0-255) - "999.999.999.999" проходил
        // бы как валидный IP-токен. Ошибка в итоге всё равно проявится при попытке SSH-подключения, но
        // раз уж код распознаёт IP-паттерн, лучше сразу отличать реальный IP от похожей на него мусорной строки.
        private static bool IsIPv4(string s)
        {
            var parts = s.Split('.');
            if (parts.Length != 4) return false;
            foreach (var p in parts)
            {
                int v;
                if (!int.TryParse(p, out v) || v < 0 || v > 255) return false;
            }
            return true;
        }

        public static List<Node> Parse(string text)
        {
            var list = new List<Node>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var parts = Regex.Split(line, @"[\t,; ]+");
                string name = "", host = "";
                if (parts.Length == 1) { host = parts[0]; name = parts[0]; }
                else
                {
                    // ищем токен-IP, остальное - имя. Раньше выбор был асимметричным: при нескольких
                    // IP-подобных токенах побеждал ПОСЛЕДНИЙ (ip перезаписывался), а для имени - ПЕРВЫЙ
                    // не-IP токен (other присваивался один раз). Теперь оба - "первый подходящий".
                    string ip = null, other = null;
                    foreach (var p in parts)
                    {
                        if (ip == null && IsIPv4(p)) ip = p;
                        else if (other == null) other = p;
                    }
                    if (ip != null) { host = ip; name = other ?? ip; }
                    else { name = parts[0]; host = parts[1]; }
                }
                if (string.IsNullOrEmpty(host)) continue;   // строка без адреса - пропускаем, не плодим пустой узел
                list.Add(new Node { Name = name, Host = host, Port = 22, Enabled = true });
            }
            return list;
        }
    }

    // Пул учёток + массовый ввод паролей.
    internal class CredentialsForm : Form
    {
        private DataGridView _grid;
        private readonly HashSet<string> _seen = new HashSet<string>();  // ключи логин\0пароль - против дублей
        public List<Credential> Result;

        public CredentialsForm(List<Credential> pool)
        {
            Text = "Учётные записи SSH";
            StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(480, 400);

            var info = new Label { Text = "Программа последовательно проверяет учётные записи и запоминает подходящую для каждого узла. Пароли всегда скрыты.", Left = 10, Top = 6, Width = 460, Height = 34 };
            _grid = new ModernDataGridView { Left = 10, Top = 40, Width = 460, Height = 280, AllowUserToAddRows = true, AllowUserToDeleteRows = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, EmptyTitle = "Учётных записей пока нет", EmptyHint = "Добавьте логин и пароль в первую строку" };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Логин", Name = "user", FillWeight = 30 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Пароль", Name = "pass", FillWeight = 70 });

            // Пароль всегда скрыт: в интерфейсе не показываем ни значение, ни длину.
            _grid.CellFormatting += (s, e) =>
            {
                if (e.ColumnIndex == _grid.Columns["pass"].Index && e.Value != null && e.Value.ToString().Length > 0)
                {
                    e.Value = "••••••••";
                    e.FormattingApplied = true;
                }
            };
            _grid.EditingControlShowing += (s, e) =>
            {
                var tb = e.Control as TextBox;
                if (tb != null)
                    tb.UseSystemPasswordChar = (_grid.CurrentCell != null && _grid.CurrentCell.ColumnIndex == _grid.Columns["pass"].Index);
            };

            // Загрузка существующего пула с отсевом дублей.
            if (pool != null)
                foreach (var c in pool) AddCred(c.User, c.Password);

            var bulk = new ModernButton { Text = "Добавить несколько", Left = 10, Top = 332, Width = 160, Height = 30 };
            bulk.Click += (s, e) =>
            {
                string txt = Prompt.Show("Пароли", "Каждая строка - пароль, либо 'логин:пароль' (по умолчанию логин root):", "", true, new Size(420, 320));
                if (txt == null) return;
                foreach (var raw in txt.Replace("\r", "").Split('\n'))
                {
                    var line = raw.Trim(); if (line.Length == 0) continue;
                    string user = "root", pass = line;
                    int idx = line.IndexOf(':');
                    if (idx > 0) { user = line.Substring(0, idx); pass = line.Substring(idx + 1); }
                    AddCred(user, pass);   // дубль просто не добавится
                }
            };
            var ok = new ModernButton { Text = "Сохранить", Width = 100, Height = 30, Top = 360, Left = 270, DialogResult = DialogResult.OK };
            var cancel = new ModernButton { Text = "Отмена", Width = 90, Height = 30, Top = 360, Left = 380, DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) =>
            {
                Result = new List<Credential>();
                var outSeen = new HashSet<string>();
                foreach (DataGridViewRow r in _grid.Rows)
                {
                    if (r.IsNewRow) continue;
                    string u = Convert.ToString(r.Cells["user"].Value);
                    string p = Convert.ToString(r.Cells["pass"].Value);
                    if (string.IsNullOrEmpty(p)) continue;
                    u = string.IsNullOrEmpty(u) ? "root" : u.Trim();
                    if (!outSeen.Add(u + "\0" + p)) continue;   // финальный отсев дублей
                    Result.Add(new Credential { User = u, Password = p });
                }
            };
            Controls.AddRange(new Control[] { info, _grid, bulk, ok, cancel });
            Theme.Dialog(this);
            AcceptButton = null; CancelButton = cancel;
        }

        // Добавить учётку, если такой (логин+пароль) ещё нет. Дубль - молча игнорируем.
        private void AddCred(string user, string pass)
        {
            user = string.IsNullOrEmpty(user) ? "root" : user.Trim();
            if (string.IsNullOrEmpty(pass)) return;
            if (!_seen.Add(user + "\0" + pass)) return;
            _grid.Rows.Add(user, pass);
        }
    }

    // Обновление репозитория: хост + список reposync-скриптов.
    internal class RepoDialog : Form
    {
        private TextBox _host, _scripts;
        public string Host;
        public List<string> Scripts;

        public RepoDialog(string host, List<string> scripts)
        {
            Text = "Обновить репозиторий";
            FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false; ClientSize = new Size(470, 320);

            Controls.Add(new Label { Text = "Хост репозитория:", Left = 10, Top = 10, Width = 450 });
            _host = new ModernTextBox { Left = 10, Top = 30, Width = 450, Text = host ?? "", Placeholder = "IP-адрес или DNS-имя" };
            Controls.Add(_host);
            Controls.Add(new Label { Text = "Скрипты (полный путь, по одному на строку) - запускаются по очереди:", Left = 10, Top = 60, Width = 450 });
            _scripts = new TextBox
            {
                Left = 10, Top = 82, Width = 450, Height = 160, Multiline = true, ScrollBars = ScrollBars.Both,
                AcceptsReturn = true, WordWrap = false, Font = Theme.Mono,   // см. комментарий у BulkNodesForm - тот же Font-leak фикс
                Text = scripts != null ? string.Join(Environment.NewLine, scripts.ToArray()) : ""
            };
            Controls.Add(_scripts);

            var ok = new ModernButton { Text = "Запустить", Width = 110, Height = 30, Top = 256, Left = 240, DialogResult = DialogResult.OK };
            var cancel = new ModernButton { Text = "Отмена", Width = 90, Height = 30, Top = 256, Left = 360, DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) =>
            {
                Host = _host.Text.Trim();
                Scripts = new List<string>();
                foreach (var raw in _scripts.Text.Replace("\r", "").Split('\n'))
                { var t = raw.Trim(); if (t.Length > 0) Scripts.Add(t); }
                if (string.IsNullOrEmpty(Host)) { AppDialog.Info(this, "Проверьте данные", "Укажите хост репозитория."); DialogResult = DialogResult.None; return; }
                if (Scripts.Count == 0) { AppDialog.Info(this, "Проверьте данные", "Укажите хотя бы один скрипт."); DialogResult = DialogResult.None; return; }
            };
            Controls.AddRange(new Control[] { ok, cancel });
            Theme.Dialog(this);
            AcceptButton = null; CancelButton = cancel;
        }
    }

    // Настройки запуска.
    internal class SettingsForm : Form
    {
        private NumericUpDown _par, _conn, _initDelay, _up, _stopto, _authDelay, _maxAuth, _backupKeep, _updto;
        public AppSettings Result;
        private readonly AppSettings _src;

        public SettingsForm(AppSettings s)
        {
            _src = s;
            Text = "Настройки";
            FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ClientSize = new Size(460, 438);
            int y = 10;
            Section("Выполнение", ref y);
            _par = Row("Одновременных узлов", s.MaxParallel, 1, 100, ref y);
            _conn = Row("Подключение по SSH", s.ConnectTimeoutSec, 1, 120, ref y, "сек.");
            _updto = Row("Операция yum/dnf", s.UpdateTimeoutSec > 0 ? s.UpdateTimeoutSec : 1800, 60, 14400, ref y, "сек.");

            Section("Перезагрузка и сервисы", ref y);
            _initDelay = Row("Пауза после команды reboot", s.InitialRebootDelaySec, 0, 120, ref y, "сек.");
            _up = Row("Ожидание возврата узла", s.UpTimeoutSec, 30, 3600, ref y, "сек.");
            _stopto = Row("Остановка сервиса", s.StopServiceTimeoutSec, 5, 600, ref y, "сек.");

            Section("Доступ и хранение", ref y);
            _authDelay = Row("Пауза между паролями", s.AuthRetryDelayMs, 0, 10000, ref y, "мс");
            _maxAuth = Row("Попыток авторизации (0 — все)", s.MaxAuthAttempts, 0, 50, ref y);
            _backupKeep = Row("Бэкапов на узле", s.BackupKeep, 1, 50, ref y, "шт.");

            var ok = new ModernButton { Text = "Сохранить", Width = 104, Height = 30, Top = y + 10, Left = 234, DialogResult = DialogResult.OK };
            var cancel = new ModernButton { Text = "Отмена", Width = 96, Height = 30, Top = y + 10, Left = 348, DialogResult = DialogResult.Cancel };
            ok.Click += (s2, e) => Result = new AppSettings
            {
                MaxParallel = (int)_par.Value, ConnectTimeoutSec = (int)_conn.Value, InitialRebootDelaySec = (int)_initDelay.Value,
                DownWaitSec = _src.DownWaitSec, UpTimeoutSec = (int)_up.Value, StopServiceTimeoutSec = (int)_stopto.Value,
                AuthRetryDelayMs = (int)_authDelay.Value, MaxAuthAttempts = (int)_maxAuth.Value, BackupKeep = (int)_backupKeep.Value,
                UpdateTimeoutSec = (int)_updto.Value
            };
            Controls.AddRange(new Control[] { ok, cancel });
            Theme.Dialog(this);
            AcceptButton = ok; CancelButton = cancel;
        }
        private void Section(string title, ref int y)
        {
            var label = Theme.SectionLabel(title);
            label.Left = 14; label.Top = y; label.Width = 420; label.Height = 24;
            Controls.Add(label); y += 26;
        }

        private NumericUpDown Row(string label, int val, int min, int max, ref int y)
        {
            return Row(label, val, min, max, ref y, null);
        }

        private NumericUpDown Row(string label, int val, int min, int max, ref int y, string unit)
        {
            Controls.Add(new Label { Text = label, Left = 22, Top = y + 4, Width = 245 });
            var n = new ModernNumericUpDown { Left = 280, Top = y, Width = 112, Minimum = min, Maximum = max, Value = Math.Min(Math.Max(val, min), max) };
            Controls.Add(n);
            if (!string.IsNullOrEmpty(unit)) Controls.Add(new Label { Text = unit, Left = 400, Top = y + 4, Width = 42, ForeColor = Theme.Muted });
            y += 30; return n;
        }
    }
}
