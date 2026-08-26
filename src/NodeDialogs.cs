using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace RedOSPackageUpdater
{
    // Диалог одного узла.
    internal class NodeForm : Form
    {
        private ModernTextBox _name, _host, _role;
        private NumericUpDown _port;
        private CheckBox _enabled;
        public Node Result;

        public NodeForm(Node existing)
        {
            Text = existing == null ? "Новый сервер" : "Изменить сервер";
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
            Text = "Массовое добавление серверов";
            StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(460, 380);
            var lbl = new Label { Text = "Вставьте серверы: «имя IP», «имя<tab>IP» или только IP — по одному на строку.", Left = 10, Top = 8, Width = 440, Height = 34 };
            // Theme.Mono - общий на всё приложение шрифт Consolas 9, а не новый Font на каждое открытие
            // диалога: Control.Dispose() не освобождает шрифт, назначенный через свойство Font (WinForms
            // не считает себя его владельцем) - при повторных открытиях это была утечка GDI-хендлов.
            _tb = new TextBox { Left = 10, Top = 44, Width = 440, Height = 280, Multiline = true, ScrollBars = ScrollBars.Both, AcceptsReturn = true, WordWrap = false, Font = Theme.Mono };
            var ok = new ModernButton { Text = "Добавить", Width = 100, Height = 30, Top = 332, Left = 250, DialogResult = DialogResult.OK };
            var cancel = new ModernButton { Text = "Отмена", Width = 100, Height = 30, Top = 332, Left = 360, DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) =>
            {
                Result = Parse(_tb.Text);
                if (Result.Count == 0)
                {
                    AppDialog.Info(this, "Серверы не распознаны", "Введите хотя бы одну строку с адресом сервера.");
                    DialogResult = DialogResult.None;
                }
            };
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

}
