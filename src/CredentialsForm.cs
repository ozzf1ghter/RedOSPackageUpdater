using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RedOSPackageUpdater
{
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
                if (e.ColumnIndex != _grid.Columns["pass"].Index) return;
                if (_grid.Rows[e.RowIndex].Tag is Credential && string.IsNullOrEmpty(Convert.ToString(e.Value)))
                {
                    e.Value = "Недоступен — введите новый";
                    e.CellStyle.ForeColor = Theme.Warn;
                    e.FormattingApplied = true;
                }
                else if (e.Value != null && e.Value.ToString().Length > 0)
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

            // Загрузка существующего пула с отсевом дублей. Если DPAPI-пароль не удалось
            // расшифровать (например, конфиг перенесли другому пользователю Windows),
            // сохраняем исходный EncPassword: простое открытие диалога не должно терять учётку.
            if (pool != null)
                foreach (var c in pool) AddCred(c);

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
                    u = string.IsNullOrEmpty(u) ? "root" : u.Trim();
                    var original = r.Tag as Credential;
                    if (string.IsNullOrEmpty(p))
                    {
                        if (original == null || string.IsNullOrEmpty(original.EncPassword)) continue;
                        string encryptedKey = u + "\0enc\0" + original.EncPassword;
                        if (!outSeen.Add(encryptedKey)) continue;
                        Result.Add(new Credential { User = u, Password = null, EncPassword = original.EncPassword });
                        continue;
                    }
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

        private void AddCred(Credential credential)
        {
            if (credential == null) return;
            if (credential.Password != null) { AddCred(credential.User, credential.Password); return; }
            if (string.IsNullOrEmpty(credential.EncPassword)) return;
            string user = string.IsNullOrEmpty(credential.User) ? "root" : credential.User.Trim();
            string key = user + "\0enc\0" + credential.EncPassword;
            if (!_seen.Add(key)) return;
            int index = _grid.Rows.Add(user, "");
            _grid.Rows[index].Tag = credential;
            _grid.Rows[index].Cells["pass"].ToolTipText = "Пароль сохранён, но недоступен текущему пользователю Windows. Введите новый пароль для замены.";
        }
    }

    // Обновление репозитория: хост + список reposync-скриптов.

}

