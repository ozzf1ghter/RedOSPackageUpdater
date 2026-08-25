using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace RedOSPackageUpdater
{
    public partial class MainForm
    {
        // ---------- лог по узлам ----------
        private void BufferLog(string host, string line) { BufferLog(host, line, false); }

        // progress=true - строка прогресса reposync: заменяет предыдущую такую же строку, а не удлиняет лог.
        private void BufferLog(string host, string line, bool progress)
        {
            string stamped = DateTime.Now.ToString("HH:mm:ss") + "  " + line;
            bool replaced;
            lock (_logLock)
            {
                StringBuilder sb;
                if (!_hostLogs.TryGetValue(host, out sb)) { sb = new StringBuilder(); _hostLogs[host] = sb; }
                replaced = progress && _lastLineProgress;
                if (replaced) TrimLastLine(sb);   // затираем прошлую прогресс-строку
                sb.Append(stamped).Append("\r\n");
                if (sb.Length > 1500000) sb.Remove(0, 700000);   // не растём бесконечно на долгом reposync
                _lastLineProgress = progress;   // под тем же локом, что и решение replaced
            }
            bool shown = (_selectedHost == null) || (_selectedHost == host);
            if (!shown) return;
            string screenLine = (_selectedHost == null) ? HostLabel(host) + "  " + stamped : stamped;
            // replaced бывает только для reposync-прогресса (один хост, без чередования) - безопасно править последнюю строку
            if (replaced) Ui(() => ReplaceLastLogLine(screenLine));
            else Ui(() => AppendLog(screenLine));
        }

        // Удалить из буфера последнюю строку (вместе с завершающим \r\n).
        private static void TrimLastLine(StringBuilder sb)
        {
            int len = sb.Length;
            if (len >= 2 && sb[len - 1] == '\n' && sb[len - 2] == '\r') len -= 2;   // отбросить финальный \r\n
            int nl = -1;
            for (int i = len - 1; i >= 0; i--) if (sb[i] == '\n') { nl = i; break; }
            sb.Length = nl + 1;   // оставить всё до предыдущей строки включительно (или 0)
        }

        // Заменить последнюю строку в _log на новую (для прогресс-строки на месте, тем же форматом что и append).
        private void ReplaceLastLogLine(string line)
        {
            string t = _log.Text;
            int end = t.Length;
            if (end >= 2 && t[end - 1] == '\n') end -= 2;   // отбросить финальный \r\n
            int nl = end > 0 ? t.LastIndexOf('\n', end - 1) : -1;
            string keep = nl >= 0 ? t.Substring(0, nl + 1) : "";
            _log.Text = keep + line + "\r\n";
            _log.SelectionStart = _log.TextLength; _log.ScrollToCaret();
        }

        // Счётчик пакетов reposync в статус-строке (закачано/всего).
        private void SetRepoCount(string host, int done, int total)
        {
            DataGridViewRow row;
            if (_rowByHost.TryGetValue(host ?? "", out row))
                row.Cells[Col.Upd].Value = "пакеты " + done + "/" + total;
            int pct = total > 0 ? (int)(100.0 * done / total) : 0;
            SetStatus("reposync: пакеты " + done + "/" + total + " (" + pct + "%)");
        }
        private void ShowHostLog(string host)
        {
            _selectedHost = host;
            if (_logHint != null) _logHint.Text = "Журнал сервера: " + HostLabel(host) + "   («Общий журнал» показывает все серверы)";
            string text;
            lock (_logLock) { StringBuilder sb; text = _hostLogs.TryGetValue(host, out sb) ? sb.ToString() : ""; }
            _log.Text = text;
            _log.SelectionStart = _log.TextLength; _log.ScrollToCaret();
        }
        private void ShowAllLogs()
        {
            _selectedHost = null;
            if (_logHint != null) _logHint.Text = "Общий журнал всех серверов. Выберите строку результата для журнала одного сервера.";
            var sb = new StringBuilder();
            lock (_logLock)
                foreach (var kv in _hostLogs)
                { sb.Append("===== ").Append(HostLabel(kv.Key)).Append(" =====\r\n").Append(kv.Value).Append("\r\n"); }
            _log.Text = sb.ToString();
            _log.SelectionStart = _log.TextLength; _log.ScrollToCaret();
        }

        private string HostLabel(string host)
        {
            foreach (SubSystem system in _cfg.Systems ?? new List<SubSystem>())
                foreach (Node node in system.Nodes ?? new List<Node>())
                    if (string.Equals(node.Host, host, StringComparison.OrdinalIgnoreCase))
                        return HostIdentity.Label(node.Name, node.Host);
            return host ?? "";
        }

        // ---------- утилиты UI ----------
        private void Ui(Action a)
        {
            if (IsDisposed) return;
            // Раньше try/catch ловил исключения только ПОСТАНОВКИ делегата в очередь (BeginInvoke),
            // а не его выполнения - оно происходит асинхронно позже, в цикле сообщений UI-потока.
            // Если внутри a() (например, UpdateRow/UpdatePreviewRow при неожиданном состоянии данных)
            // вылетало исключение, оно уходило как необработанное прямо посреди SSH-операции -
            // потенциальный краш приложения. Оборачиваем сам делегат, не только его постановку.
            Action wrapped = () =>
            {
                try { a(); }
                catch (Exception ex) { try { AppendLog("ОШИБКА ОБНОВЛЕНИЯ UI: " + ex.Message); } catch { } }
            };
            try { if (InvokeRequired) BeginInvoke(wrapped); else wrapped(); }
            catch (ObjectDisposedException) { }      // окно закрыли во время прогона - гасим гонку
            catch (InvalidOperationException) { }
        }
        private void AppendLog(string line)
        {
            if (_log.TextLength > 400000) _log.Text = _log.Text.Substring(_log.TextLength - 200000);
            _log.AppendText(line + "\r\n");
        }
        private void SetStatus(string s)
        {
            _status.SetStatus(s ?? "", ClassifyStatus(s));
            if (_tips != null) _tips.SetToolTip(_status, s ?? "");
            string value = (s ?? "").ToLowerInvariant();
            if (!_running && (value.Contains("сохран") || value.Contains("обновлен") || value.Contains("очищен") ||
                value.Contains("импорт выполнен") || value.Contains("экспортирован") || value.Contains("добавлено")))
                ModernToast.Show(this, s, ToastKind.Success);
        }

        // Определяем цвет статус-чипа по тексту сообщения (сами сообщения не переписываем - их формируют
        // десятки мест в коде). Не находит категорию - остаётся нейтральным (Idle).
        private static StatusChip.Kind ClassifyStatus(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "Готово") return StatusChip.Kind.Idle;
            // Все статусы, которые StartOperation ставит в начале операции, заканчиваются на "..." -
            // проверяем это первым, иначе часть из них (например "Обновление репозитория на host...")
            // по ключевым словам ниже ошибочно попадала бы в Good ("обновлен...") ещё ДО завершения операции.
            if (s.EndsWith("...", StringComparison.Ordinal)
                || s.IndexOf("идёт", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("Выполня", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("Останавл", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("reposync:", StringComparison.OrdinalIgnoreCase) >= 0)
                return StatusChip.Kind.Busy;
            int fail = CountAfter(s, "FAIL: ");
            if (fail > 0) return StatusChip.Kind.Bad;
            // ": FAIL"/": WARN"/": OK" - формат "Репозиторий: FAIL | ..." (StatusText отдаёт голое
            // "FAIL"/"WARN"/"OK" без счётчика, так что CountAfter с маркером "FAIL: " его не ловит).
            if (s.IndexOf("ошибка", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("не удалось", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf(": FAIL", StringComparison.OrdinalIgnoreCase) >= 0)
                return StatusChip.Kind.Bad;
            int warn = CountAfter(s, "WARN: ");
            if (warn > 0 || s.IndexOf(": WARN", StringComparison.OrdinalIgnoreCase) >= 0)
                return StatusChip.Kind.Warn;
            if (fail == 0 && warn == 0
                || s.IndexOf(": OK", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("готов", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("выполнен", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("сохран", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("обновлен", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("Экспортировано", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("Добавлено", StringComparison.OrdinalIgnoreCase) >= 0)
                return StatusChip.Kind.Good;
            return StatusChip.Kind.Idle;
        }

        // Число сразу после маркера ("FAIL: 3" -> 3); маркера нет или после него не число -> -1 (не найдено).
        private static int CountAfter(string s, string marker)
        {
            int i = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return -1;
            i += marker.Length;
            int j = i;
            while (j < s.Length && char.IsDigit(s[j])) j++;
            int val;
            return (j > i && int.TryParse(s.Substring(i, j - i), out val)) ? val : -1;
        }
        private void SetRunningUi(bool running)
        {
            _btnRun.Enabled = !running; _btnStop.Enabled = running;
            _btnPreview.Enabled = !running;
            _profile.Enabled = !running; _noReboot.Enabled = !running;
            if (_pkgBox != null) _pkgBox.Enabled = !running;
            // на время прогона блокируем правку конфига и дерева
            if (_leftPanel != null) _leftPanel.Enabled = !running;
            _configurationControls.RemoveAll(control => control == null || control.IsDisposed);
            foreach (Control control in _configurationControls)
                if (control != null && !control.IsDisposed) control.Enabled = !running;
            // Исключения фиксируются в RunOptions при старте, поэтому во время операции их не меняем.
            if (_excluded != null) _excluded.Enabled = !running;
            if (!running)
            {
                // Общая разблокировка не должна повторно активировать действия,
                // которым всё ещё не хватает выбранной цели или объекта.
                RefreshSelectionSummary();
                RefreshServerDetails();
            }
        }

        // Компоненты без визуального родителя (ToolTip не кладётся в Controls, CancellationTokenSource
        // не привязан к WinForms Component-дереву вовсе) не освобождаются автоматически при закрытии
        // формы - раньше здесь не было override Dispose, и оба этих объекта просто утекали.
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_tips != null) _tips.Dispose();
                if (_nodeActionsMenu != null) _nodeActionsMenu.Dispose();
                if (_cts != null) _cts.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_running)
            {
                if (!_closeAfterOperation && !AppDialog.Confirm(this, "Завершение работы", "Сейчас выполняется операция. Остановить её и закрыть приложение?", "Остановить и выйти"))
                { e.Cancel = true; return; }
                _closeAfterOperation = true;
                if (_cts != null) _cts.Cancel();
                SetStatus("Останавливаю перед выходом...");
                e.Cancel = true;
                return;
            }
            try { Store.SaveConfig(_cfg); }
            catch (Exception ex)
            {
                e.Cancel = true;
                AppDialog.Error(this, "Конфигурация не сохранена",
                    "Приложение не будет закрыто, чтобы не потерять изменения.\n\n" + ex.Message);
                return;
            }
            base.OnFormClosing(e);
        }

    }
}
