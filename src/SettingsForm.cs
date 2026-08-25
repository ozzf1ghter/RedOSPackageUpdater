using System;
using System.Drawing;
using System.Windows.Forms;

namespace RedOSPackageUpdater
{
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
            _updto = Row("Операция DNF", s.UpdateTimeoutSec > 0 ? s.UpdateTimeoutSec : 1800, 60, 14400, ref y, "сек.");

            Section("Перезагрузка и сервисы", ref y);
            _initDelay = Row("Пауза после перезагрузки", s.InitialRebootDelaySec, 0, 120, ref y, "сек.");
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
