using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedOSPackageUpdater
{
    /// <summary>Жизненный цикл и точки входа изменяющих операций MainForm.</summary>
    public partial class MainForm
    {
        private void WireHostCallbacks(SshOrchestrator orchestrator)
        {
            orchestrator.OnHostStart = result => Ui(() => UpdateRow(result, true));
            orchestrator.OnHostDone = result => Ui(() => UpdateRow(result, false));
            orchestrator.OnHostPhase = (host, phase) => Ui(() => SetRowPhase(host, phase));
        }

        private void StartOperation(string status, Action<CancellationToken> body)
        {
            if (_cts != null) throw new InvalidOperationException("Предыдущая операция ещё не завершена");
            var source = new CancellationTokenSource();
            _cts = source;
            _trustUnknownHostKeysForOperation = false;
            _running = true;
            SetRunningUi(true);
            SetStatus(status);
            CancellationToken token = source.Token;
            Task.Factory.StartNew(() =>
            {
                try { body(token); }
                catch (OperationCanceledException) { Ui(() => { AppendLog("Операция отменена пользователем"); SetStatus("Остановлено"); }); }
                catch (Exception ex) { Ui(() => { AppendLog("ОБЩАЯ ОШИБКА: " + ex); SetStatus("Операция завершилась ошибкой"); }); }
                finally
                {
                    Ui(() =>
                    {
                        _running = false;
                        SetRunningUi(false);
                        if (ReferenceEquals(_cts, source)) _cts = null;
                        source.Dispose();
                        if (_closeAfterOperation)
                        {
                            _closeAfterOperation = false;
                            Close();
                        }
                    });
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void RunTargets(List<RunTarget> targets)
        {
            if (IsPkgMode()) { RunPkgOpTargets(targets, false); return; }
            if (!Preflight(targets)) return;
            targets = DedupeByHost(targets);

            var options = new RunOptions
            {
                Settings = _cfg.Settings,
                NoReboot = _noReboot.Checked,
                UpdateScript = Profiles.Read(SelectedProfileResource()),
                PostScript = Profiles.Read(Profiles.PostCheck),
                PreStopScript = Profiles.Read(Profiles.PreStop),
                RunLogDir = NewLogDir("run_"),
                ExcludeMasks = ExcludeMasks()
            };

            string exclusions = string.IsNullOrEmpty(options.ExcludeMasks) ? "(нет)" : options.ExcludeMasks;
            string confirmation = "Серверов: " + targets.Count + "\nСценарий: " + _profile.Text +
                "\nИсключения пакетов: " + exclusions +
                (_noReboot.Checked ? "\nПерезагрузка: не выполнять" : "\nПерезагрузка: выполнить при необходимости");
            if (!AppDialog.Confirm(this, "Подтверждение операции", confirmation, "Запустить")) return;

            Directory.CreateDirectory(options.RunLogDir);
            ResetSummary(targets, "Общий журнал всех серверов. Выберите строку результата для журнала одного сервера.");
            var orchestrator = NewOrchestrator(true);
            WireHostCallbacks(orchestrator);

            StartOperation("Операция выполняется на " + targets.Count + " серверах...", token =>
            {
                List<HostResult> results = orchestrator.RunBatch(targets, options, token);
                results = OrderLikeTargets(results, targets, result => result.Host);
                Ui(() =>
                {
                    ReportBatchStatus(results);
                    WriteSummaryFile(options.RunLogDir, results);
                });
            });
        }

        private void OpenRepo()
        {
            if (_running) { AppDialog.Info(this, "Операция выполняется", "Дождитесь завершения текущей операции или остановите её."); return; }
            string repoHost;
            List<string> repoScripts;
            using (var dialog = new RepoDialog(_cfg.RepoHost, _cfg.RepoScripts))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                repoHost = dialog.Host;
                repoScripts = dialog.Scripts;
            }
            _cfg.RepoHost = repoHost;
            _cfg.RepoScripts = repoScripts;
            Store.SaveConfig(_cfg);
            RunRepoTargets(repoHost, repoScripts);
        }

        private void RunRepoTargets(string host, List<string> scripts)
        {
            if (!HasUsableCredentials()) { AppDialog.Info(this, "Нет доступных учётных записей", "Добавьте или повторно введите учётную запись в разделе «Доступ и SSH»."); return; }
            var node = new Node { Name = "repo (" + host + ")", Host = host, Port = 22, Enabled = true };
            var target = new RunTarget(new SubSystem { Name = "Репозиторий" }, node);
            var targets = new List<RunTarget> { target };
            string logDir = NewLogDir("repo_");

            if (!AppDialog.Confirm(this, "Обновление репозитория", "Сервер: " + host + "\nСкриптов к запуску: " + scripts.Count, "Запустить")) return;
            Directory.CreateDirectory(logDir);
            ResetSummary(targets, "Обновление репозитория. Ниже отображается полный вывод скрипта.");
            var orchestrator = NewOrchestrator(false);
            WireHostCallbacks(orchestrator);
            orchestrator.OnRepoProgress = (repoHost, line) => BufferLog(repoHost, line, true);
            orchestrator.OnRepoCount = (repoHost, done, total) => Ui(() => SetRepoCount(repoHost, done, total));

            StartOperation("Обновление репозитория на " + host + "...", token =>
            {
                HostResult result = orchestrator.RunRepo(target, scripts, _cfg.Settings, logDir, token);
                Ui(() => SetStatus("Репозиторий: " + StatusText(result.Status) + " | " + result.Note));
            });
        }
    }
}
