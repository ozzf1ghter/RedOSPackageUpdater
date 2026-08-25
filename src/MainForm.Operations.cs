using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RedOSPackageUpdater
{
    public partial class MainForm
    {
        private void RunVulnerabilityScan()
        {
            if (_running) { AppDialog.Info(this, "Операция выполняется", "Дождитесь завершения текущей операции или остановите её."); return; }
            var targets = DedupeByHost(CollectChecked());
            if (targets.Count == 0) { AppDialog.Info(this, "Нет выбранных серверов", "Отметьте серверы для проверки."); return; }
            if (!Preflight(targets)) return;
            try { FstecLinuxCatalog.EnsureReady(); }
            catch (Exception ex)
            {
                AppDialog.Error(this, "Каталог БДУ недоступен", ex.Message + "\n\nОбновите базу из интернета или импортируйте официальную ZIP-выгрузку.");
                return;
            }
            if (!AppDialog.Confirm(this, "Проверка уязвимостей ФСТЭК",
                "Проверить " + targets.Count + " узлов по бюллетеням безопасности RED OS и связать CVE с БДУ ФСТЭК?\n\n" +
                "Trivy и дополнительные пакеты на узлах не устанавливаются. Серверы не перезагружаются.", "Проверить")) return;

            string logDir = NewLogDir("vuln_");
            Directory.CreateDirectory(logDir);
            ResetSummary(targets, "Уязвимости ФСТЭК. Полный список — в логе каждого узла.");
            var orch = NewOrchestrator(true);
            WireHostCallbacks(orch);
            string script = Profiles.Read(Profiles.AdvisoryScan);

            StartOperation("Проверка ФСТЭК на " + targets.Count + " узлах...", token =>
            {
                var res = orch.RunPkgOp(targets, "vuln", "", true, script, _cfg.Settings, logDir, token);
                res = OrderLikeTargets(res, targets, r => r.Host);
                Ui(() => { ReportBatchStatus(res); WriteSummaryFile(logDir, res); WriteVulnerabilityReport(logDir, res); });
            });
        }

        private void WriteVulnerabilityReport(string logDir, List<HostResult> results)
        {
            try
            {
                VulnerabilityReportOutput output = VulnerabilityReportService.WriteCsv(logDir, results);
                string htmlPath = Path.Combine(logDir, "fstec_report.html");
                File.WriteAllText(htmlPath, VulnerabilityHtmlReport.Build(results), new UTF8Encoding(false));
                _lastReportDir = logDir;
                AppendLog("Отчёт ФСТЭК: " + output.FstecCsvPath);
                AppendLog("Подтверждено для версии ОС: " + output.ConfirmedBduCount + "; исключено неподтверждённых/неприменимых совпадений: " + output.RejectedBduCount);
                AppendLog("Расширенный отчёт: " + output.AllCsvPath);
                AppendLog("HTML-отчёт: " + htmlPath);
                if (output.LinuxFindingsAdded > 0) AppendLog("Сопоставление advisory/общего Linux с БДУ: добавлено " + output.LinuxFindingsAdded + " подтверждённых записей");
            }
            catch (Exception ex) { AppendLog("Не удалось сформировать отчёт ФСТЭК: " + ex.Message); }
        }


        private void UpdateVulnerabilityDb()
        {
            if (_running) { AppDialog.Info(this, "Операция выполняется", "Дождитесь завершения текущей операции или остановите её."); return; }
            ShowVulnerabilityDbProgress();
            StartOperation("Загрузка базы ФСТЭК...", token =>
            {
                try
                {
                    VulnerabilityDatabaseService.UpdateAll(progress =>
                    {
                        Ui(() => UpdateVulnerabilityDbProgress(progress.Percent,
                            progress.Done / (1024 * 1024), progress.Total > 0 ? progress.Total / (1024 * 1024) : 0,
                            "Каталог БДУ ФСТЭК"));
                    }, token);
                    Ui(() => { RefreshVulnerabilityDbStatus(); AppDialog.Info(this, "ФСТЭК", "Каталог БДУ ФСТЭК успешно обновлён."); });
                }
                catch (OperationCanceledException) { Ui(() => SetStatus("Загрузка базы ФСТЭК отменена")); }
                catch (Exception ex) { Ui(() => { SetStatus("Ошибка загрузки базы ФСТЭК"); AppDialog.Error(this, "ФСТЭК", "Не удалось обновить базу:\n" + ex.Message); }); }
                finally { Ui(HideVulnerabilityDbProgress); }
            });
        }

        private void ShowVulnerabilityDbProgress()
        {
            _fstecProgress.Style = ProgressBarStyle.Marquee;
            _fstecProgress.Value = 0;
            _fstecProgress.Visible = true;
            _fstecProgressLabel.Text = "Подключение к RED SOFT...";
            _fstecProgressLabel.Visible = true;
        }

        private void UpdateVulnerabilityDbProgress(int percent, long doneMb, long totalMb, string stage)
        {
            if (percent >= 0)
            {
                _fstecProgress.Style = ProgressBarStyle.Continuous;
                _fstecProgress.Value = Math.Max(0, Math.Min(100, percent));
                _fstecProgressLabel.Text = stage + ": " + doneMb + " / " + totalMb + " МБ  (" + percent + "%)";
                SetStatus(stage + " " + percent + "%");
            }
            else
            {
                _fstecProgress.Style = ProgressBarStyle.Marquee;
                _fstecProgressLabel.Text = stage + ": " + doneMb + " МБ загружено";
            }
        }

        private void HideVulnerabilityDbProgress()
        {
            _fstecProgress.Visible = false;
            _fstecProgressLabel.Visible = false;
        }

        private void ImportVulnerabilityDb()
        {
            if (_running) { AppDialog.Info(this, "Операция выполняется", "Дождитесь завершения текущей операции или остановите её."); return; }
            using (var d = new OpenFileDialog { Title = "Официальная XML-выгрузка или компактный каталог ФСТЭК", Filter = "Каталог ФСТЭК (*.zip)|*.zip" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    VulnerabilityDatabaseService.Import(d.FileName, CancellationToken.None);
                    RefreshVulnerabilityDbStatus(); AppDialog.Info(this, "ФСТЭК", "База уязвимостей успешно импортирована.");
                }
                catch (Exception ex) { AppDialog.Error(this, "ФСТЭК", "Ошибка импорта:\n" + ex.Message); }
            }
        }

        private void RefreshVulnerabilityDbStatus()
        {
            string status = VulnerabilityDb.StatusText();
            if (_fstecDbState != null)
            {
                _fstecDbState.Text = status;
                _fstecDbState.ForeColor = FstecLinuxCatalog.Exists ? Theme.Good : Theme.Warn;
            }
            SetStatus(status);
        }

        // ---------- Установка/обновление произвольных пакетов (режим "пакеты" в списке профиля) ----------
        // dryRun=true - предпроверка (ничего не ставим), false - боевой прогон.
        private void RunPkgOpTargets(List<RunTarget> targets, bool dryRun)
        {
            if (!Preflight(targets)) return;
            targets = DedupeByHost(targets);
            string action = PkgAction();
            bool listOnly = action == "locklist";   // просмотр закреплённых версий - только чтение
            if (listOnly) dryRun = true;             // ничего не меняем, оба режима = показать список
            string packages = PkgListFromBox();
            if (packages == null)
            {
                if (!listOnly) { AppDialog.Info(this, "Не указаны пакеты", "Введите пакеты в поле сценария — через пробел или по одному на строку."); return; }
                packages = "";   // для просмотра список необязателен (пусто = все закреплённые)
            }
            string actRu = ActionRu(action);

            if (!dryRun)
            {
                string warn;
                if (action == "remove")
                    warn = "\n\nВНИМАНИЕ: удаление может потянуть за собой зависимые пакеты - сверьтесь с предпроверкой.";
                else if (action == "lock")
                    warn = "\n\nВерсии будут закреплены: dnf update перестанет обновлять эти пакеты.";
                else if (action == "unlock")
                    warn = "\n\nЗакрепление версий будет снято: пакеты снова начнут обновляться.";
                else
                    warn = "\n\nReboot не выполняется (только сообщение, если нужен).";
                if (!AppDialog.Confirm(this, "Подтверждение операции", actRu + " на " + targets.Count + " узлах:\n" + packages + warn + "\n\nВыполнить?",
                    action == "remove" ? "Удалить" : "Выполнить")) return;
            }

            string prefix = dryRun ? "pkgpreview_" : "pkgop_";
            string logDir = NewLogDir(prefix);
            Directory.CreateDirectory(logDir);
            string script = Profiles.Read(Profiles.PkgOp);

            string title = listOnly ? actRu : (dryRun ? "Предпроверка: " + actRu.ToLower() : actRu);
            ResetSummary(targets, title + ". Клик по строке — лог узла.");
            var orch = NewOrchestrator(true);
            WireHostCallbacks(orch);

            StartOperation(title + " на " + targets.Count + " узлах...", token =>
            {
                var res = orch.RunPkgOp(targets, action, packages, dryRun, script, _cfg.Settings, logDir, token);
                res = OrderLikeTargets(res, targets, r => r.Host);   // порядок как в дереве
                Ui(() =>
                {
                    ReportBatchStatus(res);
                    WriteSummaryFile(logDir, res);
                });
            });
        }

        // ---------- Предпроверка (dry-run) ----------
        private void RunPreviewTargets(List<RunTarget> targets)
        {
            if (IsPkgMode()) { RunPkgOpTargets(targets, true); return; }   // режим "пакеты" - dry-run
            if (!Preflight(targets)) return;
            targets = DedupeByHost(targets);

            string logDir = NewLogDir("preview_");
            Directory.CreateDirectory(logDir);
            string excl = ExcludeMasks();
            string previewScript = Profiles.Read(Profiles.Preview);
            string profileKey = SelectedProfileKey();

            ResetSummary(targets, "Проверка изменений: реальная транзакция DNF без установки. Выберите строку для журнала сервера.");
            var orch = NewOrchestrator(true);
            orch.OnPreviewStart = host => Ui(() => SetRowPhase(host, "preview"));
            orch.OnPreviewDone = hp => Ui(() => UpdatePreviewRow(hp));

            StartOperation("Проверка изменений на " + targets.Count + " серверах...", token =>
            {
                var res = orch.RunPreview(targets, previewScript, excl, profileKey, _cfg.Settings, logDir, token);
                res = OrderLikeTargets(res, targets, h => h.Host);   // порядок как в дереве, не по завершению
                Ui(() =>
                {
                    int totW = 0, totS = 0, totD = 0; foreach (var h in res) { totW += h.Total; totS += h.Sec; totD += h.Dep; }
                    SetStatus(string.Format("Проверка завершена: пакетов в транзакции {0} (бюллетени {1}, зависимости {2})", totW, totS, totD));
                    if (res.Count > 0)
                    {
                        string html = null, xls = null;
                        try { html = PreviewReport.Build(res, logDir); AppendLog("Отчёт (HTML): " + html); }
                        catch (Exception ex) { AppendLog("Не удалось сформировать HTML: " + ex.Message); }
                        try { xls = PreviewReport.BuildXlsx(res, logDir); AppendLog("Отчёт (Excel): " + xls); }
                        catch (Exception ex) { AppendLog("Не удалось сформировать XLS: " + ex.Message); }
                        _lastReportDir = logDir;
                        if (Visible) { try { Activate(); } catch { } }
                        OfferOpenReport(html, xls);   // диалог с выбором: HTML / Excel / оба / папка
                    }
                });
            });
        }

        // Упорядочить результаты как в дереве (по порядку целей запуска), а не по порядку завершения.
        private static List<T> OrderLikeTargets<T>(List<T> res, List<RunTarget> targets, Func<T, string> hostOf)
        {
            return OperationDomain.OrderLikeTargets(res, targets, hostOf);
        }

        // Убрать дубли учёток по (логин+пароль).
        private static List<Credential> DedupCreds(List<Credential> list)
        {
            var seen = new HashSet<string>();
            var outp = new List<Credential>();
            foreach (var c in list)
                if (c != null && seen.Add((c.User ?? "") + "\0" + (c.Password ?? ""))) outp.Add(c);
            return outp;
        }

        // Открыть файл во внешней программе (с логированием, если не открылось).
        private void OpenPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { Process.Start(path); }
            catch (Exception ex) { AppendLog("Не открылось (файл сохранён): " + path + " - " + ex.Message); }
        }

        // Диалог по завершении предпроверки: что открыть - HTML / Excel / оба / папку.
        private void OfferOpenReport(string html, string xls)
        {
            using (var f = new Form
            {
                Text = "Отчёт предпроверки готов", FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false,
                ShowInTaskbar = false, ClientSize = new Size(372, 116)
            })
            {
                try { f.Icon = this.Icon; } catch { }
                f.Controls.Add(new Label { Text = "Что открыть?", Left = 12, Top = 14, Width = 348 });
                var bHtml = new ModernButton { Text = "HTML", Left = 12, Top = 42, Width = 84, Height = 30, Enabled = html != null };
                var bXls = new ModernButton { Text = "Excel", Left = 102, Top = 42, Width = 84, Height = 30, Enabled = xls != null };
                var bBoth = new ModernButton { Text = "Оба", Left = 192, Top = 42, Width = 76, Height = 30, Enabled = html != null && xls != null };
                var bDir = new ModernButton { Text = "Папка", Left = 274, Top = 42, Width = 86, Height = 30 };
                var bCancel = new ModernButton { Text = "Закрыть", Left = 274, Top = 78, Width = 86, Height = 30, DialogResult = DialogResult.Cancel };
                bHtml.Click += (s, e) => { OpenPath(html); f.Close(); };
                bXls.Click += (s, e) => { OpenPath(xls); f.Close(); };
                bBoth.Click += (s, e) => { OpenPath(html); OpenPath(xls); f.Close(); };
                bDir.Click += (s, e) => { OpenReportsFolder(); f.Close(); };
                f.Controls.AddRange(new Control[] { bHtml, bXls, bBoth, bDir, bCancel });
                Theme.Dialog(f);
                f.CancelButton = bCancel;
                f.ShowDialog(this);
            }
        }

        // Открыть папку с отчётами: последний отчёт, если был, иначе общий каталог логов.
        private void OpenReportsFolder()
        {
            string dir = (!string.IsNullOrEmpty(_lastReportDir) && Directory.Exists(_lastReportDir)) ? _lastReportDir : Store.LogsDir;
            try { Directory.CreateDirectory(dir); Process.Start(dir); }
            catch (Exception ex) { AppDialog.Error(this, "Не удалось открыть папку", ex.Message); }
        }

        private void OpenLatestReport()
        {
            try
            {
                Store.EnsureDirs();
                string[] patterns = { "*.html", "*.htm", "*.xlsx", "*.csv", "*.log" };
                var root = new DirectoryInfo(Store.LogsDir);
                FileInfo latest = patterns.SelectMany(pattern => root.GetFiles(pattern, SearchOption.AllDirectories))
                    .OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
                if (latest == null)
                {
                    AppDialog.Info(this, "Отчётов пока нет", "Сначала выполните проверку изменений, операцию или проверку уязвимостей.");
                    return;
                }
                OpenPath(latest.FullName);
            }
            catch (Exception ex) { AppDialog.Error(this, "Не удалось открыть последний отчёт", ex.Message); }
        }

        private void UpdatePreviewRow(HostPreview hp)
        {
            DataGridViewRow row;
            if (!_rowByHost.TryGetValue(hp.Host ?? "", out row)) return;
            if (!string.IsNullOrEmpty(hp.OsInfo)) row.Cells[Col.Os].Value = hp.OsInfo;
            if (!string.IsNullOrEmpty(hp.Error))
            {
                row.Cells[Col.St].Value = "ошибка"; row.Cells[Col.Note].Value = hp.Error;
                row.DefaultCellStyle.BackColor = Theme.IsDark ? Color.FromArgb(72, 38, 45) : Color.FromArgb(253, 232, 234);
            }
            else
            {
                row.Cells[Col.St].Value = hp.Total + " в транз.";
                row.Cells[Col.Upd].Value = "adv " + hp.Sec + " / завис " + hp.Dep;
                row.Cells[Col.Note].Value = hp.Total > 0 ? ("исключено маской: " + hp.Excluded) : "обновлять нечего (исключено: " + hp.Excluded + ")";
                // зелёный - есть что ставить; голубой - проверено, апдейтов нет (не путать с "не отработало")
                row.DefaultCellStyle.BackColor = hp.Total > 0
                    ? (Theme.IsDark ? Color.FromArgb(28, 66, 49) : Color.FromArgb(229, 247, 237))
                    : Theme.AccentTint;
            }
        }

        private static bool Important(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            string tr = line.Trim();
            // сплошные разделители yum (=====, -----) без текста - не показываем
            if (tr.Length >= 8 && (tr.Trim('=').Length == 0 || tr.Trim('-').Length == 0)) return false;
            if (line.StartsWith("===") || line.StartsWith("-----")) return true;
            string[] keys = { "RESULT:", "REBOOT_REQUIRED:", "PRESTOP_RESULT:", "RUNNING_KERNEL:", "EXPECTED_KERNEL:", "VULN|", "VULN_SUMMARY|", "TRIVY_LOG|", "TRIVY_ERR|",
                "Подобрана", "кеш", "Ошибка", "ОШИБКА", "ИСКЛЮЧЕНИЕ", "ВНИМАНИЕ", "Останавли", "reboot", "Reboot", "вернул", "down",
                "Отсутствует", "не подошла", "нет связи", "агрузк", "is-system", "готовности" };
            foreach (var k in keys) if (line.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void UpdateRow(HostResult r, bool starting)
        {
            DataGridViewRow row;
            if (!_rowByHost.TryGetValue(r.Host ?? "", out row)) return;
            row.Cells[Col.St].Value = starting ? "идёт..." : StatusText(r.Status);
            if (!string.IsNullOrEmpty(r.OsInfo)) row.Cells[Col.Os].Value = r.OsInfo;
            if (!starting)
            {
                row.Cells[Col.Upd].Value = r.UpdateResult;
                row.Cells[Col.Reb].Value = r.RebootAction;
                row.Cells[Col.Pre].Value = r.PreStop;
                row.Cells[Col.Post].Value = r.PostCheck;
                row.Cells[Col.Ker].Value = r.RunningKernel;
                row.Cells[Col.Note].Value = r.Note;
                row.Tag = r.LogFile;
                Color bg = r.Status == HostStatus.Ok ? (Theme.IsDark ? Color.FromArgb(28, 66, 49) : Color.FromArgb(229, 247, 237))
                        : r.Status == HostStatus.Warn ? (Theme.IsDark ? Color.FromArgb(70, 56, 29) : Color.FromArgb(255, 247, 220))
                        : (Theme.IsDark ? Color.FromArgb(72, 38, 45) : Color.FromArgb(253, 232, 234));
                row.DefaultCellStyle.BackColor = bg;
            }
            else row.DefaultCellStyle.BackColor = Theme.AccentTint;
        }

        private void SetRowPhase(string host, string phase)
        {
            DataGridViewRow row;
            if (!_rowByHost.TryGetValue(host ?? "", out row)) return;
            string txt; Color bg;
            switch (phase)
            {
                case "update": txt = "обновление..."; bg = Theme.AccentTint; break;
                case "preview": txt = "предпроверка..."; bg = Theme.AccentTint; break;
                case "prestop": txt = "стоп служб..."; bg = Theme.IsDark ? Color.FromArgb(52, 42, 78) : Color.FromArgb(239, 234, 255); break;
                case "reboot": txt = "перезагрузка..."; bg = Theme.IsDark ? Color.FromArgb(74, 55, 27) : Color.FromArgb(255, 238, 202); break;
                case "postcheck": txt = "проверка..."; bg = Theme.IsDark ? Color.FromArgb(28, 61, 75) : Color.FromArgb(226, 245, 252); break;
                case "scan": txt = "сканирование..."; bg = Theme.AccentTint; break;
                case "repo": txt = "reposync..."; bg = Theme.IsDark ? Color.FromArgb(27, 65, 57) : Color.FromArgb(225, 247, 241); break;
                default: txt = "идёт..."; bg = Theme.AccentTint; break;
            }
            row.Cells[Col.St].Value = txt;
            row.DefaultCellStyle.BackColor = bg;
        }

        // Экранирование поля CSV: переводы строк убираем, при наличии ; " - оборачиваем в кавычки с удвоением.
        private static string Csv(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            if (s.IndexOf('"') >= 0 || s.IndexOf(';') >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static string StatusText(HostStatus s)
        {
            switch (s) { case HostStatus.Ok: return "OK"; case HostStatus.Warn: return "WARN"; case HostStatus.Fail: return "FAIL"; default: return "-"; }
        }

        private void WriteSummaryFile(string dir, List<HostResult> res)
        {
            if (res == null) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Система;Узел;IP или имя;Статус;Обновление;Перезагрузка;До обновления;После обновления;Ядро;Примечание");
                foreach (var r in res)
                    sb.AppendLine(string.Join(";", new[] { Csv(r.System), Csv(r.Name), Csv(r.Host), Csv(StatusText(r.Status)),
                        Csv(r.UpdateResult), Csv(r.RebootAction), Csv(r.PreStop), Csv(r.PostCheck), Csv(r.RunningKernel), Csv(r.Note) }));
                File.WriteAllText(Path.Combine(dir, "summary.csv"), sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // Соседний код отчётов предпроверки (PreviewReport.Build/BuildXlsx) логирует свои ошибки
                // через AppendLog - раньше summary.csv тут был единственным исключением, проглатывающим
                // ошибку молча. Вызывается изнутри Ui(), так что AppendLog здесь безопасен (UI-поток).
                AppendLog("Не удалось записать summary.csv: " + ex.Message);
            }
        }

    }
}
