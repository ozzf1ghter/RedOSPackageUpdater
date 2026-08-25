using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RedOSPackageUpdater
{
    internal class RepoDialog : Form
    {
        private ModernTextBox _host;
        private TextBox _scripts;
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

}
