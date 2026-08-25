using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RedOSPackageUpdater
{
    public class RunOptions
    {
        public AppSettings Settings;
        public bool NoReboot;
        public string UpdateScript;   // содержимое выбранного профиля обновления (LF)
        public string PostScript;     // redos_postcheck.sh
        public string PreStopScript;  // redos_prestop.sh
        public string RunLogDir;      // папка логов текущего запуска
        public string ExcludeMasks;   // маски пакетов для --exclude (через пробел)
    }

    // Одна цель запуска = узел + его подсистема (для списка сервисов prestop).
    public class RunTarget
    {
        public SubSystem System;
        public Node Node;
        public RunTarget(SubSystem s, Node n) { System = s; Node = n; }
    }

    public partial class SshOrchestrator
    {
        private readonly List<Credential> _pool;
        private readonly Dictionary<string, CachedCred> _cache;
        private readonly object _cacheLock = new object();
        private int _cacheDirty;
        // host:port -> SHA256-отпечаток SSH host-ключа, увиденного при первом подключении (TOFU).
        private readonly Dictionary<string, string> _knownHosts;
        private readonly object _knownHostsLock = new object();
        private readonly ISshStateStore _stateStore;

        // Контракт многопоточности: все колбэки ниже вызываются из фоновых SSH-потоков
        // (Task.Factory.StartNew(..., LongRunning) в RunParallel), КОНКУРЕНТНО, по одному на узел -
        // никогда из UI-потока напрямую. Подписчик (MainForm) обязан сам маршалить в UI через
        // Control.Invoke/BeginInvoke - у самого SshOrchestrator никакого понятия о UI-потоке нет.
        public Action<HostResult> OnHostStart;
        public Action<HostResult> OnHostDone;
        public Action<string, string> OnLog;     // (host, line) для живого лога
        public Action<string, string> OnHostPhase; // (host, phase) для смены цвета/статуса строки
        // Вызывается при первом подключении к неизвестному host:port. Обработчик должен явно
        // подтвердить показанный SHA-256 fingerprint; без обработчика новый ключ не принимается.
        public Func<string, int, string, bool> OnUnknownHostKey;
        private void Phase(string host, string ph) { if (OnHostPhase != null) OnHostPhase(host, ph); }
        public Action<string> OnPreviewStart;
        public Action<HostPreview> OnPreviewDone;
        public Action<string, string> OnRepoProgress;  // (host, line) - строка прогресса reposync (заменяет предыдущую на экране)
        public Action<string, int, int> OnRepoCount;   // (host, done, total) - счётчик пакетов из вывода reposync

        // Строка прогресса загрузки (NN%) - её схлопываем, чтобы reposync-лог не рос вертикально.
        private static readonly Regex RepoProgressRe = new Regex("\\d{1,3}\\s*%", RegexOptions.Compiled);
        // Счётчик пакетов вида "(45/234)" - показываем закачано/всего, скрипт править не нужно.
        private static readonly Regex RepoCountRe = new Regex("\\((\\d+)\\s*/\\s*(\\d+)\\)", RegexOptions.Compiled);

        // Таймауты фаз (сек). Обновление берёт таймаут из настроек, остальное - здесь.
        private const int PreStopTimeoutSec = 900;
        private const int PostCheckTimeoutSec = 300;
        private const int PreviewTimeoutSec = 300;
        private const int SystemReadyTimeoutSec = 180;
        private const int ForceSshProbeIntervalSec = 20;   // как часто пробовать SSH, если пинг молчит
        private const int RepoTimeoutSec = 21600;          // reposync может идти долго (до 6 ч)
        private const int PkgOpTimeoutSec = 3600;          // установка/обновление отдельных пакетов

        // Интервалы опроса/пинга (мс) - раньше были магическими числами прямо в теле методов.
        private const int PingTimeoutMs = 1000;
        private const int SystemReadyPollIntervalMs = 4000;
        private const int RebootPollIntervalWhenPingedMs = 2000;
        private const int RebootPollIntervalWhenNotPingedMs = 3000;
        private const int RepoProgressThrottleMs = 700;
        private const int WaitRebootProbeMinSec = 5;
        // Сколько ПОДРЯД неудачных проб нужно, чтобы засчитать хост как реально "пропавший" (sawDown)
        // при ожидании возврата после reboot. Одна неудачная проба - обычный сетевой блип, а не факт
        // недоступности; без этого порога короткая заминка сети могла ложно засчитаться как "хост ушёл
        // в перезагрузку и вернулся", хотя реального ребута не было.
        private const int WaitRebootConsecutiveDownToConfirm = 2;

        public SshOrchestrator(List<Credential> pool, Dictionary<string, CachedCred> cache)
            : this(pool, cache, new FileSshStateStore())
        {
        }

        internal SshOrchestrator(List<Credential> pool, Dictionary<string, CachedCred> cache, ISshStateStore stateStore)
        {
            _pool = pool ?? new List<Credential>();
            _cache = cache ?? new Dictionary<string, CachedCred>(StringComparer.OrdinalIgnoreCase);
            if (stateStore == null) throw new ArgumentNullException("stateStore");
            _stateStore = stateStore;
            _knownHosts = _stateStore.LoadKnownHosts() ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // Убираем одинарные кавычки перед вставкой значения в bash-литерал вида VAR='...'. Простая,
        // но обязательная защита от разрыва контекста строки и потенциальной инъекции команд в скрипт,
        // который выполняется от привилегированной SSH-учётки на проде. Применяется единообразно ко
        // ВСЕМ значениям, попадающим в такие литералы - не только к тем, что визуально выглядят
        // "пользовательским вводом" (раньше часть значений вроде ACTION/PROFILE эту очистку пропускала).
        private static string Sh(string s) { return ShellText.InSingleQuotes(s); }

        // Параллельный обход items с ограничением maxPar и отменой ct.
        // onError (опционально) вызывается, если body(item) выбросил исключение, которое сама body
        // не поймала - без него узел просто бесследно исчезал бы из итогового списка результатов
        // (body уже оборачивает свою основную логику в try/catch и обычно ничего не бросает, но если
        // всё же бросит - например, из делегата OnHostStart/OnLog, вызванного подписчиком UI, -
        // не хотим тихо терять узел из отчёта о батче).
        private static void RunParallel<T>(IEnumerable<T> items, int maxPar, CancellationToken ct, Action<T> body, Action<T, Exception> onError = null)
        {
            ParallelBatch.Run(items, maxPar, ct, body, onError);
        }

        // ---- Точка входа: параллельный прогон обновления ----
        public List<HostResult> RunBatch(List<RunTarget> targets, RunOptions opt, CancellationToken ct)
        {
            var results = new List<HostResult>();
            var resLock = new object();
            RunParallel(targets, opt.Settings.MaxParallel, ct, t =>
            {
                var r = RunHost(t, opt, ct);
                lock (resLock) results.Add(r);
            }, (t, ex) => { lock (resLock) results.Add(SyntheticFailResult(t, ex)); });
            SaveCredentialCacheIfDirty();
            return results;
        }

        // Результат-заглушка для узла, на котором RunParallel поймал исключение вне обработки самого
        // body (см. комментарий у RunParallel) - чтобы узел не пропал из отчёта молча.
        private HostResult SyntheticFailResult(RunTarget t, Exception ex)
        {
            var r = new HostResult
            {
                System = t.System != null ? t.System.Name : "",
                Name = t.Node.Name, Host = t.Node.Host,
                Status = ex is OperationCanceledException ? HostStatus.Warn : HostStatus.Fail,
                Note = ex is OperationCanceledException ? "отменено пользователем" : "внутренняя ошибка обработки узла: " + ex.Message
            };
            if (OnLog != null) OnLog(t.Node.Host, "ИСКЛЮЧЕНИЕ вне обработки узла: " + ex);
            if (OnHostDone != null) OnHostDone(r);
            return r;
        }


        // ---- Обработка одного узла ----
        private HostResult RunHost(RunTarget target, RunOptions opt, CancellationToken ct)
        {
            var node = target.Node;
            var res = new HostResult
            {
                System = target.System != null ? target.System.Name : "",
                Name = node.Name,
                Host = node.Host,
                Status = HostStatus.Running
            };
            res.LogFile = Path.Combine(opt.RunLogDir, SafeFileName(node, "host") + ".log");
            Action<string> log = MakeLogger(res.LogFile, node.Host);

            if (OnHostStart != null) OnHostStart(res);
            log("=== START " + NodeLabel(node) + " ===");

            // 1. Подбор учётки (кеш -> пул) + подключение
            SshClient client = null;
            Credential used = null;
            try
            {
                client = ResolveAndConnect(node, opt, log, res, ct, out used);
            }
            catch (Exception ex)
            {
                log("Ошибка подключения: " + ex.Message);
            }

            if (client == null)
            {
                if (string.IsNullOrEmpty(res.Note)) res.Note = "не удалось подключиться";
                res.Status = HostStatus.Fail;
                Finish(res, log);
                return res;
            }
            res.UsedUser = used != null ? used.User : "";

            try
            {
                // 2. Обновление (потоково, с таймаутом)
                Phase(node.Host, "update");
                // EXCLUDE шлём всегда (даже пустой) - GUI источник истины
                string updPrefix = "BACKUP_KEEP=" + opt.Settings.BackupKeep + "\n"
                                 + "EXCLUDE='" + Sh(opt.ExcludeMasks) + "'\n";
                int updTimeout = opt.Settings.UpdateTimeoutSec > 0 ? opt.Settings.UpdateTimeoutSec : 1800;
                log("----- UPDATE (профиль обновления) -----");
                string updOut = RunScript(client, opt.UpdateScript, updPrefix, updTimeout, log, ct);
                log("----- /UPDATE -----");
                res.OsInfo = OsInfoFromOutput(updOut);

                string result = Marker(updOut, "RESULT");
                string rebReq = Marker(updOut, "REBOOT_REQUIRED");
                string expected = Marker(updOut, "EXPECTED_KERNEL");
                res.UpdateResult = result ?? "NO_MARKER";
                res.RebootRequired = rebReq ?? "unknown";
                res.ExpectedKernel = expected ?? "";

                if (result != "READY_FOR_REBOOT")
                {
                    res.Note = "профиль не подтвердил готовность (RESULT=" + res.UpdateResult + ")";
                    res.Status = HostStatus.Fail;
                    SafeDisconnect(client);
                    Finish(res, log);
                    return res;
                }

                bool doReboot = (rebReq == "yes") && !opt.NoReboot;
                if (rebReq != "yes") res.RebootAction = "NOT_NEEDED";
                else if (opt.NoReboot) res.RebootAction = "SKIPPED_NOREBOOT";

                if (doReboot)
                {
                    // 3. Prestop сервисов
                    var services = (target.System != null && target.System.Services != null)
                        ? target.System.Services : new List<string>();
                    string svcLine = string.Join(" ", services.ToArray());
                    string prefix = "SERVICES='" + Sh(svcLine) + "'\n"
                                  + "STOP_TIMEOUT=" + opt.Settings.StopServiceTimeoutSec + "\n";
                    Phase(node.Host, "prestop");
                    log("Останавливаю сервисы (если запущены): [" + svcLine + "]");
                    log("----- PRESTOP -----");
                    string psOut = RunScript(client, opt.PreStopScript, prefix, PreStopTimeoutSec, log, ct);
                    log("----- /PRESTOP -----");
                    string ps = Marker(psOut, "PRESTOP_RESULT");
                    res.PreStop = ps ?? "NO_MARKER";
                    if (ps != "OK")
                    {
                        res.Note = "сервисы не остановлены корректно (PRESTOP_RESULT=" + res.PreStop + "), reboot отменён";
                        res.Status = HostStatus.Fail;
                        SafeDisconnect(client);
                        Finish(res, log);
                        return res;
                    }

                    // 4. Reboot + ожидание возврата (доступность - ping, факт перезагрузки - boot_id)
                    Phase(node.Host, "reboot");
                    log("Отправляю reboot...");
                    bool icmpOk = PingHost(node.Host, PingTimeoutMs);   // пингуется ли живой хост (ICMP не закрыт)
                    string oldBoot = ReadBootId(client);
                    if (!IssueReboot(client, log))
                    {
                        res.RebootAction = "FAILED";
                        res.Note = "не удалось отправить команду reboot";
                        res.Status = HostStatus.Fail;
                        SafeDisconnect(client); client = null;
                        Finish(res, log);
                        return res;
                    }
                    SafeDisconnect(client);
                    client = null;
                    if (opt.Settings.InitialRebootDelaySec > 0)
                        ct.WaitHandle.WaitOne(opt.Settings.InitialRebootDelaySec * 1000);   // отменяемая пауза

                    if (!WaitReboot(node, used, opt, log, ct, oldBoot, icmpOk))
                    {
                        res.RebootAction = "FAILED";
                        res.Note = "хост не вернулся после reboot за " + opt.Settings.UpTimeoutSec + " c";
                        res.Status = HostStatus.Fail;
                        Finish(res, log);
                        return res;
                    }
                    res.RebootAction = "DONE";
                    client = ConnectWith(node, used, opt.Settings.ConnectTimeoutSec);
                    WaitSystemReady(client, log, ct);
                }

                // 5. Пост-проверка
                if (client == null) client = ConnectWith(node, used, opt.Settings.ConnectTimeoutSec);
                Phase(node.Host, "postcheck");
                log("----- POSTCHECK -----");
                var postServices = (target.System != null && target.System.Services != null)
                    ? target.System.Services : new List<string>();
                string postPrefix = "SERVICES='" + Sh(string.Join(" ", postServices.ToArray())) + "'\n";
                string pcOut = RunScript(client, opt.PostScript, postPrefix, PostCheckTimeoutSec, log, ct);
                log("----- /POSTCHECK -----");
                string running = Marker(pcOut, "RUNNING_KERNEL");
                string postResult = Marker(pcOut, "POSTCHECK_RESULT");
                res.RunningKernel = running ?? "";

                if (string.IsNullOrEmpty(postResult))
                {
                    res.PostCheck = "FAILED";
                    res.Note = "пост-проверка без итогового маркера POSTCHECK_RESULT";
                    res.Status = HostStatus.Fail;
                }
                else if (postResult == "FAILED")
                {
                    res.PostCheck = "FAILED";
                    res.Note = "после обновления не поднялись критические сервисы";
                    res.Status = HostStatus.Fail;
                }
                else if (string.IsNullOrEmpty(running))
                {
                    res.PostCheck = "FAILED";
                    res.Note = "пост-проверка без маркера RUNNING_KERNEL";
                    res.Status = HostStatus.Fail;
                }
                else if (res.RebootAction == "DONE")
                {
                    if (string.IsNullOrEmpty(expected))
                    { res.PostCheck = postResult == "WARN" ? "WARN" : "OK"; res.Status = postResult == "WARN" ? HostStatus.Warn : HostStatus.Ok; res.Note = "перезагружен, ядро " + running + " (ожидаемое не определено)"; }
                    else if (running.Trim() == expected.Trim())
                    { res.PostCheck = postResult == "WARN" ? "WARN" : "OK"; res.Status = postResult == "WARN" ? HostStatus.Warn : HostStatus.Ok; res.Note = postResult == "WARN" ? "ядро загружено, но systemd ещё не в состоянии running" : "загружено ожидаемое ядро"; }
                    else
                    { res.PostCheck = "MISMATCH"; res.Status = HostStatus.Warn; res.Note = "после reboot ядро " + running + ", ожидалось " + expected; }
                }
                else
                {
                    res.PostCheck = postResult == "WARN" ? "WARN" : "OK";
                    if (res.RebootAction == "SKIPPED_NOREBOOT")
                    { res.Status = HostStatus.Warn; res.Note = "нужен reboot, но выключен (NoReboot): хост не перезагружен"; }
                    else if (postResult == "WARN")
                    { res.Status = HostStatus.Warn; res.Note = "reboot не требовался, но systemd не в состоянии running"; }
                    else
                    { res.Status = HostStatus.Ok; res.Note = "reboot не требовался"; }
                }
            }
            catch (Exception ex)
            {
                res.Status = HostStatus.Fail;
                res.Note = "ошибка: " + ex.Message;
                log("ИСКЛЮЧЕНИЕ: " + ex);
            }
            finally
            {
                SafeDisconnect(client);
            }

            MarkIfCancelled(res, ct);
            Finish(res, log);
            return res;
        }

        private void Finish(HostResult res, Action<string> log)
        {
            log("=== DONE " + NodeLabel(res.Name, res.Host) + " status=" + res.Status + " | " + res.Note + " ===");
            if (OnHostDone != null) OnHostDone(res);
        }

        // Если пользователь нажал "Стоп" посреди работы над узлом, финальный статус до этого момента
        // обычно получается Fail/Warn от недоделанной операции (таймаут, оборванный скрипт) - оператор
        // видит это неотличимо от настоящей ошибки. Помечаем явно, что узел не доделан из-за отмены,
        // а не упал сам по себе - это меняет, что оператору нужно делать дальше (доделать вручную vs
        // разбираться, что сломалось).
        private static void MarkIfCancelled(HostResult res, CancellationToken ct)
        {
            if (!ct.IsCancellationRequested) return;
            if (res.Status == HostStatus.Ok) return;
            res.Status = HostStatus.Warn;
            res.Note = "отменено пользователем" + (string.IsNullOrEmpty(res.Note) ? "" : " (было: " + res.Note + ")");
        }


    }
}
