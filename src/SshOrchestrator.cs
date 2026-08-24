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

    public class SshOrchestrator
    {
        private readonly List<Credential> _pool;
        private readonly Dictionary<string, CachedCred> _cache;
        private readonly object _cacheLock = new object();
        // host:port -> SHA256-отпечаток SSH host-ключа, увиденного при первом подключении (TOFU).
        private readonly Dictionary<string, string> _knownHosts;
        private readonly object _knownHostsLock = new object();

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
        {
            _pool = pool ?? new List<Credential>();
            _cache = cache ?? new Dictionary<string, CachedCred>(StringComparer.OrdinalIgnoreCase);
            _knownHosts = Store.LoadKnownHosts();
        }

        // Убираем одинарные кавычки перед вставкой значения в bash-литерал вида VAR='...'. Простая,
        // но обязательная защита от разрыва контекста строки и потенциальной инъекции команд в скрипт,
        // который выполняется от привилегированной SSH-учётки на проде. Применяется единообразно ко
        // ВСЕМ значениям, попадающим в такие литералы - не только к тем, что визуально выглядят
        // "пользовательским вводом" (раньше часть значений вроде ACTION/PROFILE эту очистку пропускала).
        private static string Sh(string s) { return (s ?? "").Replace("'", ""); }

        // Параллельный обход items с ограничением maxPar и отменой ct.
        // onError (опционально) вызывается, если body(item) выбросил исключение, которое сама body
        // не поймала - без него узел просто бесследно исчезал бы из итогового списка результатов
        // (body уже оборачивает свою основную логику в try/catch и обычно ничего не бросает, но если
        // всё же бросит - например, из делегата OnHostStart/OnLog, вызванного подписчиком UI, -
        // не хотим тихо терять узел из отчёта о батче).
        private static void RunParallel<T>(IEnumerable<T> items, int maxPar, CancellationToken ct, Action<T> body, Action<T, Exception> onError = null)
        {
            maxPar = Math.Max(1, maxPar);
            using (var sem = new SemaphoreSlim(maxPar))
            {
                var tasks = new List<Task>();
                foreach (var it in items)
                {
                    if (ct.IsCancellationRequested) break;
                    var item = it;
                    try { sem.Wait(ct); }   // слот берём ДО создания задачи; при отмене выходим сразу
                    catch (OperationCanceledException) { break; }
                    // LongRunning: SSH-операция блокирующая и долгая (до часов), не занимаем поток пула
                    tasks.Add(Task.Factory.StartNew(() =>
                    {
                        try { if (!ct.IsCancellationRequested) body(item); }
                        catch (Exception ex)
                        {
                            if (onError != null) { try { onError(item, ex); } catch { } }
                        }
                        finally { sem.Release(); }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
                }
                Task.WaitAll(tasks.ToArray());
            }
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
                Status = HostStatus.Fail, Note = "внутренняя ошибка обработки узла: " + ex.Message
            };
            if (OnLog != null) OnLog(t.Node.Host, "ИСКЛЮЧЕНИЕ вне обработки узла: " + ex);
            if (OnHostDone != null) OnHostDone(r);
            return r;
        }

        // ============ Предпроверка (dry-run: что будет обновлено) ============
        public List<HostPreview> RunPreview(List<RunTarget> targets, string previewScript, string excludeMasks,
            string profileKey, AppSettings settings, string logDir, CancellationToken ct)
        {
            var results = new List<HostPreview>();
            var resLock = new object();
            RunParallel(targets, settings.MaxParallel, ct, t =>
            {
                var hp = PreviewHost(t, previewScript, excludeMasks, profileKey, settings, logDir, ct);
                lock (resLock) results.Add(hp);
            }, (t, ex) =>
            {
                var hp = new HostPreview { System = t.System != null ? t.System.Name : "", Name = t.Node.Name, Host = t.Node.Host, Error = "внутренняя ошибка обработки узла: " + ex.Message };
                if (OnLog != null) OnLog(t.Node.Host, "ИСКЛЮЧЕНИЕ вне обработки узла: " + ex);
                if (OnPreviewDone != null) OnPreviewDone(hp);
                lock (resLock) results.Add(hp);
            });
            return results;
        }

        // Имя лог-файла из имени/хоста узла, безопасное для файловой системы (без слэшей/двоеточий и т.п.).
        private static string SafeFileName(Node node, string fallback)
        {
            // Имя узла не обязано быть уникальным между подсистемами. Добавляем адрес, иначе два
            // параллельных узла с одинаковым Name писали в один файл и перемешивали строки логов.
            string name = !string.IsNullOrWhiteSpace(node.Name) ? node.Name.Trim() : fallback;
            string host = !string.IsNullOrWhiteSpace(node.Host) ? node.Host.Trim() : fallback;
            return Regex.Replace(name + "_" + host, "[^\\w.-]", "_");
        }

        // Логгер конкретного узла: пишет в его лог-файл на диске и одновременно шлёт строку в
        // живой лог UI через OnLog (см. контракт многопоточности у объявления OnLog выше).
        private Action<string> MakeLogger(string logFile, string host)
        {
            return line =>
            {
                try { File.AppendAllText(logFile, DateTime.Now.ToString("HH:mm:ss") + " " + line + "\r\n", new UTF8Encoding(false)); } catch { }
                if (OnLog != null) OnLog(host, line);
            };
        }

        private HostPreview PreviewHost(RunTarget target, string previewScript, string excludeMasks,
            string profileKey, AppSettings settings, string logDir, CancellationToken ct)
        {
            var node = target.Node;
            var hp = new HostPreview { System = target.System != null ? target.System.Name : "", Name = node.Name, Host = node.Host };
            string logFile = Path.Combine(logDir, SafeFileName(node, "host") + ".preview.log");
            Action<string> log = MakeLogger(logFile, node.Host);
            if (OnPreviewStart != null) OnPreviewStart(node.Host);
            log("=== PREVIEW " + node.Host + " (" + node.Name + ") ===");

            SshClient client = null; Credential used = null;
            var dummy = new HostResult();
            try { client = ResolveAndConnect(node, new RunOptions { Settings = settings }, log, dummy, ct, out used); }
            catch (Exception ex) { log("Ошибка подключения: " + ex.Message); }
            if (client == null)
            {
                hp.Error = string.IsNullOrEmpty(dummy.Note) ? "не удалось подключиться" : dummy.Note;
                if (OnPreviewDone != null) OnPreviewDone(hp);
                return hp;
            }
            hp.UsedUser = used != null ? used.User : "";

            try
            {
                // EXCLUDE шлём всегда (даже пустой) - GUI источник истины, чтобы снятые исключения реально снимались
                string prefix = "PROFILE='" + Sh(profileKey ?? "kernel_security") + "'\n"
                              + "EXCLUDE='" + Sh(excludeMasks) + "'\n";
                string outp = RunScript(client, previewScript, prefix, PreviewTimeoutSec, log, ct);
                hp.OsInfo = OsInfoFromOutput(outp);
                bool sawDone = false;
                foreach (var raw in (outp ?? "").Split('\n'))
                {
                    string ln = raw.Trim();
                    if (ln.StartsWith("PKG|"))
                    {
                        var p = ln.Split('|');   // PKG|sec/dep/kern/excl|name|old|new|repo|
                        if (p.Length >= 7)
                        {
                            string kind = p[1];
                            hp.Packages.Add(new PkgUpdate { Name = p[2], Old = p[3], New = p[4], Repo = p[5], Reason = p[6], Kind = kind, Security = (kind == "sec"), Excluded = (kind == "excl") });
                        }
                    }
                    else if (ln.StartsWith("PREVIEW_DONE|"))
                    {
                        var p = ln.Split('|');   // PREVIEW_DONE|total|sec|dep|excluded
                        if (p.Length >= 5)
                        {
                            // hp.Total/Sec/Dep/Excluded - свойства (после рефакторинга Models.cs), а не поля,
                            // поэтому напрямую в out-параметр TryParse их передать нельзя - через локальные переменные.
                            int total, sec, dep, excluded;
                            int.TryParse(p[1], out total); int.TryParse(p[2], out sec);
                            int.TryParse(p[3], out dep); int.TryParse(p[4], out excluded);
                            hp.Total = total; hp.Sec = sec; hp.Dep = dep; hp.Excluded = excluded;
                            sawDone = true;
                        }
                    }
                    else if (ln.StartsWith("PREVIEW_ERR|"))
                    {
                        var p = ln.Split(new[] { '|' }, 2);   // dnf не смог достучаться до репозитория - это ошибка, а не "0"
                        if (p.Length == 2) hp.Error = p[1];
                    }
                }
                // Скрипт не дошёл до финального маркера (обрыв/таймаут dnf, напр. makecache завис) - это НЕ "0", а недостоверный результат.
                if (string.IsNullOrEmpty(hp.Error) && !sawDone)
                {
                    bool timedOut = (outp ?? "").Contains("[TIMEOUT_OR_ERROR]");
                    hp.Error = timedOut
                        ? "предпроверка не завершилась (таймаут dnf - вероятно, недоступно/тормозит зеркало)"
                        : "предпроверка оборвалась (нет финального маркера) - результат недостоверен";
                }
            }
            catch (Exception ex) { hp.Error = ex.Message; log("ИСКЛЮЧЕНИЕ: " + ex); }
            finally { SafeDisconnect(client); }

            // Явная строка завершения (начинается с === - проходит фильтр живого лога, видно что узел отработал)
            if (string.IsNullOrEmpty(hp.Error))
                log("=== PREVIEW готово " + node.Host + ": в транзакции " + hp.Total + " (advisory " + hp.Sec + ", завис " + hp.Dep + ", исключено " + hp.Excluded + ") ===");
            else
                log("=== PREVIEW ошибка " + node.Host + ": " + hp.Error + " ===");

            if (OnPreviewDone != null) OnPreviewDone(hp);
            return hp;
        }

        // ============ Установка/обновление произвольных пакетов ============
        public List<HostResult> RunPkgOp(List<RunTarget> targets, string action, string pkgs, bool dryRun, string script,
            AppSettings settings, string logDir, CancellationToken ct, string localDbArchive = null)
        {
            var results = new List<HostResult>();
            var resLock = new object();
            // Архив базы большой, поэтому считаем его digest один раз на весь пакетный запуск,
            // а не заново в каждом из параллельных потоков для каждого узла.
            string localDbDigest = null;
            if (action == "vuln")
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(localDbArchive) || !File.Exists(localDbArchive))
                    throw new FileNotFoundException("Локальная база ФСТЭК не загружена", localDbArchive);
                localDbDigest = VulnerabilityDb.Sha256(localDbArchive);
            }
            RunParallel(targets, settings.MaxParallel, ct, t =>
            {
                var r = PkgOpHost(t, action, pkgs, dryRun, script, settings, logDir, ct, localDbArchive, localDbDigest);
                lock (resLock) results.Add(r);
            }, (t, ex) => { lock (resLock) results.Add(SyntheticFailResult(t, ex)); });
            return results;
        }

        private HostResult PkgOpHost(RunTarget target, string action, string pkgs, bool dryRun, string script,
            AppSettings settings, string logDir, CancellationToken ct, string localDbArchive, string localDbDigest)
        {
            var node = target.Node;
            var res = new HostResult
            {
                System = target.System != null ? target.System.Name : "",
                Name = node.Name, Host = node.Host, Status = HostStatus.Running
            };
            res.LogFile = Path.Combine(logDir, SafeFileName(node, "host") + ".pkgop.log");
            Action<string> log = MakeLogger(res.LogFile, node.Host);
            if (OnHostStart != null) OnHostStart(res);
            log("=== PKGOP " + node.Host + " (" + action + ": " + pkgs + ") ===");

            SshClient client = null; Credential used = null;
            try { client = ResolveAndConnect(node, new RunOptions { Settings = settings }, log, res, ct, out used); }
            catch (Exception ex) { log("Ошибка подключения: " + ex.Message); }
            if (client == null)
            {
                res.Status = HostStatus.Fail;
                if (string.IsNullOrEmpty(res.Note)) res.Note = "не удалось подключиться";
                Finish(res, log);
                return res;
            }
            res.UsedUser = used != null ? used.User : "";

            try
            {
                string dbPrefix = "";
                if (action == "vuln") dbPrefix = PrepareVulnerabilityDb(client, node, used, localDbArchive, localDbDigest, settings, log, ct);
                Phase(node.Host, action == "vuln" ? "scan" : (dryRun ? "preview" : "update"));
                string prefix = "ACTION='" + Sh(action) + "'\nPKGS='" + Sh(pkgs) + "'\n";
                prefix += dbPrefix;
                string vulnerabilityRunId = action == "vuln" ? Guid.NewGuid().ToString("N") : null;
                if (vulnerabilityRunId != null) prefix += "RPU_SCAN_ID='" + vulnerabilityRunId + "'\n";
                if (dryRun) prefix += "DRYRUN='1'\n";
                int timeout = settings.UpdateTimeoutSec > 0 ? settings.UpdateTimeoutSec : PkgOpTimeoutSec;
                string outp;
                try { outp = RunScript(client, script, prefix, timeout, log, ct); }
                finally { if (vulnerabilityRunId != null) CleanupRemoteVulnerabilityScan(client, vulnerabilityRunId, log); }
                res.OsInfo = OsInfoFromOutput(outp);

                string result = Marker(outp, "PKGOP_RESULT");
                string reb = Marker(outp, "REBOOT_RECOMMENDED");
                string trivyInstalled = Marker(outp, "TRIVY_INSTALLED");
                int changed = 0;
                int vulnTotal = 0, vulnBdu = 0, vulnCritical = 0, vulnHigh = 0;
                // Список ВСЕХ ненайденных пакетов, а не только последнего - раньше при нескольких
                // "PKGOP_ERR|" строках предыдущие терялись (переменная перезаписывалась).
                var nomatchList = new List<string>();
                foreach (var raw in (outp ?? "").Split('\n'))
                {
                    string ln = raw.TrimStart();
                    if (ln.StartsWith("CHANGED|")) changed++;
                    else if (ln.StartsWith("VULN|"))
                    {
                        changed++;
                        var vf = ln.Split(new[] { '|' }, 7);
                        if (vf.Length >= 6)
                            res.Vulnerabilities.Add(new VulnerabilityFinding
                            {
                                Id = vf[1].Trim(), Package = vf[2].Trim(), InstalledVersion = vf[3].Trim(),
                                FixedVersion = vf[4].Trim(), Severity = vf[5].Trim(),
                                Title = vf.Length > 6 ? vf[6].Trim() : ""
                            });
                    }
                    else if (ln.StartsWith("VULN_SUMMARY|"))
                    {
                        var vp = ln.Split('|');
                        if (vp.Length > 4) { int.TryParse(vp[1], out vulnTotal); int.TryParse(vp[2], out vulnBdu); int.TryParse(vp[3], out vulnCritical); int.TryParse(vp[4], out vulnHigh); }
                    }
                    else if (ln.StartsWith("VULN_URL|") || ln.StartsWith("VULN_ALIAS|") || ln.StartsWith("VULN_REF|"))
                    {
                        var mp = ln.Split(new[] { '|' }, 4);
                        if (mp.Length == 4)
                        {
                            VulnerabilityFinding finding = null;
                            for (int vi = res.Vulnerabilities.Count - 1; vi >= 0; vi--)
                                if (res.Vulnerabilities[vi].Id == mp[1].Trim() && res.Vulnerabilities[vi].Package == mp[2].Trim())
                                { finding = res.Vulnerabilities[vi]; break; }
                            string value = mp[3].Trim();
                            if (finding != null && value.Length > 0)
                            {
                                if (ln.StartsWith("VULN_URL|")) finding.PrimaryUrl = value;
                                else if (ln.StartsWith("VULN_ALIAS|") && !finding.Aliases.Contains(value)) finding.Aliases.Add(value);
                                else if (ln.StartsWith("VULN_REF|") && !finding.References.Contains(value)) finding.References.Add(value);
                            }
                        }
                    }
                    else if (ln.StartsWith("PKGOP_ERR|")) { var pp = ln.Split(new[] { '|' }, 2); if (pp.Length == 2) nomatchList.Add(pp[1].Trim()); }
                }
                string nomatch = nomatchList.Count > 0 ? string.Join(", ", nomatchList.ToArray()) : null;

                res.UpdateResult = result ?? "NO_MARKER";
                res.RebootRequired = reb ?? "?";
                res.RebootAction = (reb == "yes") ? "нужен" : "-";

                if (action == "vuln")
                {
                    if (result == "OK")
                    {
                        int bduFixable = 0;
                        foreach (var v in res.Vulnerabilities)
                            if (v.Id.StartsWith("BDU:", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v.FixedVersion)) bduFixable++;
                        res.Status = vulnTotal > 0 ? HostStatus.Warn : HostStatus.Ok;
                        res.UpdateResult = "всего: " + vulnTotal;
                        res.Note = vulnTotal > 0 ? ("БДУ ФСТЭК: " + vulnBdu + " (с исправлением: " + bduFixable + "), критических: " + vulnCritical + ", высоких: " + vulnHigh) : "уязвимостей не найдено";
                        if (trivyInstalled == "yes") res.Note += "; Trivy установлен автоматически";
                    }
                    else
                    {
                        res.Status = HostStatus.Fail;
                        res.Note = !string.IsNullOrEmpty(nomatch) ? nomatch : "проверка Trivy завершилась ошибкой";
                    }
                }
                else if (result == "OK" && !string.IsNullOrEmpty(nomatch))
                {
                    // Частичный успех: часть пакетов обновлена (changed>0), часть не найдена в репозитории.
                    // Раньше это условие срабатывало только при changed==0, и при частичном успехе
                    // информация о ненайденных пакетах терялась целиком - оператор видел "Ok" без единого
                    // намёка, что часть запрошенного не выполнена.
                    res.Status = HostStatus.Warn;
                    res.Note = "изменено пакетов: " + changed + ", не найдено: " + nomatch + (reb == "yes" ? ", рекомендуется reboot" : "");
                }
                else if (!string.IsNullOrEmpty(nomatch))
                {
                    // пакет(ы) не найдены в репозитории и ничего не изменилось - предупреждаем, а не молчим "нечего делать"
                    res.Status = HostStatus.Warn;
                    res.Note = "пакет не найден: " + nomatch;
                }
                else if (result == "OK") { res.Status = HostStatus.Ok; res.Note = (dryRun ? "к изменению пакетов: " : "изменено пакетов: ") + changed + (reb == "yes" ? ", рекомендуется reboot" : ""); }
                else if (result == "NOTHING") { res.Status = HostStatus.Ok; res.Note = dryRun ? "изменений не будет (уже актуально)" : "изменений нет (уже актуально)"; }
                else { res.Status = HostStatus.Fail; res.Note = "ошибка операции (PKGOP_RESULT=" + res.UpdateResult + ")"; }
            }
            catch (Exception ex) { res.Status = HostStatus.Fail; res.Note = "ошибка: " + ex.Message; log("ИСКЛЮЧЕНИЕ: " + ex); }
            finally { SafeDisconnect(client); }

            MarkIfCancelled(res, ct);
            Finish(res, log);
            return res;
        }

        // ============ Обновление репозитория (reposync) ============
        public HostResult RunRepo(RunTarget target, List<string> scripts, AppSettings settings, string logDir, CancellationToken ct)
        {
            var node = target.Node;
            var res = new HostResult
            {
                System = target.System != null ? target.System.Name : "",
                Name = node.Name, Host = node.Host, Status = HostStatus.Running
            };
            res.LogFile = Path.Combine(logDir, SafeFileName(node, "repo") + ".repo.log");
            Action<string> writeFile = line =>
            { try { File.AppendAllText(res.LogFile, DateTime.Now.ToString("HH:mm:ss") + " " + line + "\r\n", new UTF8Encoding(false)); } catch { } };
            Action<string> log = MakeLogger(res.LogFile, node.Host);
            if (OnHostStart != null) OnHostStart(res);
            log("=== REPO " + node.Host + " ===");

            SshClient client = null; Credential used = null;
            try { client = ResolveAndConnect(node, new RunOptions { Settings = settings }, log, res, ct, out used); }
            catch (Exception ex) { log("Ошибка подключения: " + ex.Message); }
            if (client == null)
            {
                res.Status = HostStatus.Fail;
                if (string.IsNullOrEmpty(res.Note)) res.Note = "не удалось подключиться";
                Finish(res, log);
                return res;
            }
            res.UsedUser = used != null ? used.User : "";

            // Приёмник вывода скрипта: строки-проценты схлопываем (заменяемая строка на экране),
            // из строк вида "(n/m)" вытаскиваем счётчик пакетов. Скрипт reposync не меняем - только парсим вывод.
            DateTime lastProg = DateTime.MinValue;
            Action<string> sink = line =>
            {
                string s = line ?? "";
                var mc = RepoCountRe.Match(s);
                bool hasCount = mc.Success;
                if (hasCount && OnRepoCount != null)
                {
                    int d, t;
                    if (int.TryParse(mc.Groups[1].Value, out d) && int.TryParse(mc.Groups[2].Value, out t) && t > 0)
                        OnRepoCount(node.Host, d, t);
                }
                // Строка со счётчиком (n/m) = отдельный пакет, её оставляем. Чистые проценты внутри пакета - схлопываем.
                if (!hasCount && RepoProgressRe.IsMatch(s))
                {
                    var now = DateTime.Now;
                    if ((now - lastProg).TotalMilliseconds < RepoProgressThrottleMs) return;   // не спамим каждым процентом
                    lastProg = now;
                    writeFile(s);
                    if (OnRepoProgress != null) OnRepoProgress(node.Host, s);
                }
                else
                {
                    writeFile(s);
                    if (OnLog != null) OnLog(node.Host, s);
                }
            };

            int okCount = 0, failCount = 0;
            try
            {
                Phase(node.Host, "repo");
                foreach (var raw in scripts)
                {
                    if (ct.IsCancellationRequested) break;
                    string path = (raw ?? "").Trim();
                    if (path.Length == 0) continue;
                    string p = Sh(path);   // путь пользователя, чистим кавычки
                    log("----- REPOSYNC: " + p + " -----");
                    string content =
                        "SCR='" + p + "'\n" +
                        "if [ ! -f \"$SCR\" ]; then echo \"Скрипт $SCR не найден\"; echo 'REPO_EXIT: 127'; exit 0; fi\n" +
                        "cd \"$(dirname \"$SCR\")\" || { echo 'нет доступа к каталогу скрипта'; echo 'REPO_EXIT: 1'; exit 0; }\n" +
                        "echo \"=== Запуск $SCR ===\"\n" +
                        "bash \"./$(basename \"$SCR\")\" 2>&1\n" +
                        "echo \"REPO_EXIT: $?\"\n";
                    string outp = RunScript(client, content, null, RepoTimeoutSec, sink, ct);
                    log("----- /REPOSYNC: " + p + " -----");
                    string exit = Marker(outp, "REPO_EXIT");
                    if ((outp ?? "").Contains("[TIMEOUT_OR_ERROR]")) { failCount++; log("Скрипт прерван по таймауту"); }
                    else if (exit == "0") okCount++;
                    else { failCount++; log("Скрипт завершился с кодом " + (exit ?? "?")); }
                }
            }
            catch (Exception ex) { failCount++; res.Note = "ошибка: " + ex.Message; log("ИСКЛЮЧЕНИЕ: " + ex); }
            finally { SafeDisconnect(client); }

            if (failCount == 0 && okCount > 0) { res.Status = HostStatus.Ok; res.Note = "reposync завершён (" + okCount + " скр.)"; }
            else if (okCount > 0) { res.Status = HostStatus.Warn; res.Note = "часть скриптов с ошибкой (OK " + okCount + ", ошибок " + failCount + ")"; }
            else { res.Status = HostStatus.Fail; res.Note = "reposync не выполнен"; }
            MarkIfCancelled(res, ct);
            Finish(res, log);
            return res;
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
            log("=== START " + node.Host + " (" + node.Name + ") ===");

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
            log("=== DONE " + res.Host + " status=" + res.Status + " | " + res.Note + " ===");
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

        // ---- Подбор учётки: кеш -> пул, с кешированием результата ----
        private SshClient ResolveAndConnect(Node node, RunOptions opt, Action<string> log,
            HostResult res, CancellationToken ct, out Credential used)
        {
            used = null;
            string key = Store.CacheKey(node.Host, node.Port <= 0 ? 22 : node.Port);
            var candidates = new List<Credential>();
            CachedCred cached = null;
            lock (_cacheLock) { _cache.TryGetValue(key, out cached); }
            if (cached != null)
                candidates.Add(new Credential { User = cached.User, Password = cached.Password });
            foreach (var c in _pool)
            {
                if (cached != null && c.User == cached.User && c.Password == cached.Password) continue;
                candidates.Add(new Credential { User = c.User, Password = c.Password });
            }
            if (candidates.Count == 0) { res.Note = "пул учёток пуст"; return null; }

            int attempts = 0;
            bool anyAuthFail = false;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (ct.IsCancellationRequested) return null;
                if (opt.Settings.MaxAuthAttempts > 0 && attempts >= opt.Settings.MaxAuthAttempts)
                { log("Достигнут лимит попыток учёток (" + opt.Settings.MaxAuthAttempts + ")"); break; }

                var cand = candidates[i];
                attempts++;
                bool wasCached = (cached != null && i == 0);
                try
                {
                    var client = ConnectWith(node, cand, opt.Settings.ConnectTimeoutSec);
                    used = cand;
                    if (!wasCached)
                    {
                        lock (_cacheLock)
                        {
                            _cache[key] = new CachedCred { Key = key, User = cand.User, Password = cand.Password };
                            Store.SaveCache(_cache);
                        }
                        log("Подобрана рабочая учётка (" + cand.User + "), закеширована");
                    }
                    else log("Учётка из кеша подошла (" + cand.User + ")");
                    return client;
                }
                catch (SshAuthenticationException)
                {
                    anyAuthFail = true;
                    if (wasCached)
                    {
                        log("Кешированная учётка не подошла - перебор пула");
                        lock (_cacheLock) { _cache.Remove(key); Store.SaveCache(_cache); }
                    }
                    else log("Учётка (" + cand.User + ") не подошла");
                    if (opt.Settings.AuthRetryDelayMs > 0) ct.WaitHandle.WaitOne(opt.Settings.AuthRetryDelayMs);
                }
                catch (Exception ex)
                {
                    // сетевая/таймаут - дальше перебирать бессмысленно
                    res.Note = "нет связи: " + ex.Message;
                    log("Сетевая ошибка: " + ex.Message);
                    return null;
                }
            }
            res.Note = anyAuthFail ? "не подошла ни одна учётка из пула" : "не удалось подключиться";
            return null;
        }

        private SshClient ConnectWith(Node node, Credential cred, int timeoutSec)
        {
            var ci = new PasswordConnectionInfo(node.Host, node.Port <= 0 ? 22 : node.Port, cred.User, cred.Password);
            ci.Timeout = TimeSpan.FromSeconds(timeoutSec);
            var client = new SshClient(ci);
            bool mismatch = false;
            try
            {
                // TOFU (trust-on-first-use) pinning host-ключа, как ~/.ssh/known_hosts у обычного ssh.
                // Раньше здесь было e.CanTrust = true безусловно для ЛЮБОГО ключа - инструмент шлёт
                // пароли и выполняет привилегированные команды на проде, а безусловное доверие
                // означает, что MITM между машиной инженера и сервером остаётся никак не обнаружимым.
                // Первое подключение к узлу - ключ запоминается. Дальше - должен совпадать; если нет,
                // соединение обрывается, а не тихо продолжается с новым (возможно подменённым) ключом.
                client.HostKeyReceived += (s, e) =>
                {
                    string key = Store.CacheKey(node.Host, node.Port <= 0 ? 22 : node.Port);
                    string fp = e.FingerPrintSHA256;
                    lock (_knownHostsLock)
                    {
                        string known;
                        if (_knownHosts.TryGetValue(key, out known))
                        {
                            e.CanTrust = string.Equals(known, fp, StringComparison.Ordinal);
                            if (!e.CanTrust) mismatch = true;
                        }
                        else
                        {
                            bool accepted = OnUnknownHostKey != null &&
                                OnUnknownHostKey(node.Host, node.Port <= 0 ? 22 : node.Port, fp);
                            e.CanTrust = accepted;
                            if (accepted)
                            {
                                _knownHosts[key] = fp;
                                Store.SaveKnownHosts(_knownHosts);
                            }
                        }
                    }
                };
                client.Connect();
                return client;
            }
            catch
            {
                // Connect() бросил (аутентификация/таймаут/сеть/несовпадение ключа) - освобождаем
                // клиент, иначе висит сокет.
                try { client.Dispose(); } catch { }
                if (mismatch)
                    throw new InvalidOperationException(
                        "Host-ключ узла " + node.Host + " не совпадает с ранее сохранённым - "
                      + "возможна подмена сервера (MITM) либо сервер был переустановлен. "
                      + "Если переустановка ожидаема, удалите запись для этого узла в known_hosts.json "
                      + "в папке данных приложения и подключитесь заново.");
                throw;
            }
        }

        private string PrepareVulnerabilityDb(SshClient ssh, Node node, Credential cred, string archive, string digest,
            AppSettings settings, Action<string> log, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(archive) || !File.Exists(archive))
                throw new FileNotFoundException("Локальная база ФСТЭК не загружена", archive);
            if (string.IsNullOrEmpty(digest)) digest = VulnerabilityDb.Sha256(archive);
            string current = "";
            try { current = (ssh.RunCommand("test -s /root/.cache/trivy/db/trivy.db && cat /root/.cache/trivy/rpu-db.digest 2>/dev/null || true").Result ?? "").Trim(); } catch { }
            if (string.Equals(current, digest, StringComparison.OrdinalIgnoreCase))
            {
                log("База ФСТЭК на узле актуальна: " + digest.Substring(0, 12));
                return "VULN_DB_DIGEST='" + digest + "'\n";
            }

            string remote = "/tmp/rpu-trivy-db-" + digest.Substring(0, 12) + ".tar.gz";
            string remotePart = remote + ".part";
            log("Передача базы ФСТЭК на узел (" + (new FileInfo(archive).Length / (1024 * 1024)) + " МБ)...");
            var ci = new PasswordConnectionInfo(node.Host, node.Port <= 0 ? 22 : node.Port, cred.User, cred.Password);
            ci.Timeout = TimeSpan.FromSeconds(settings.ConnectTimeoutSec);
            using (var sftp = new SftpClient(ci))
            {
                sftp.HostKeyReceived += (s, e) =>
                {
                    string key = Store.CacheKey(node.Host, node.Port <= 0 ? 22 : node.Port);
                    lock (_knownHostsLock)
                    {
                        string known;
                        e.CanTrust = _knownHosts.TryGetValue(key, out known) && string.Equals(known, e.FingerPrintSHA256, StringComparison.Ordinal);
                    }
                };
                sftp.Connect();
                ulong lastMb = 0;
                try
                {
                    if (sftp.Exists(remotePart)) sftp.DeleteFile(remotePart);
                    using (var fs = File.OpenRead(archive))
                        sftp.UploadFile(fs, remotePart, true, sent =>
                        {
                            if (ct.IsCancellationRequested) throw new OperationCanceledException();
                            ulong mb = sent / (1024 * 1024);
                            if (mb >= lastMb + 20) { lastMb = mb; log("Передано базы: " + mb + " МБ"); }
                        });
                    ct.ThrowIfCancellationRequested();
                    if (sftp.Exists(remote)) sftp.DeleteFile(remote);
                    sftp.RenameFile(remotePart, remote);
                }
                catch
                {
                    try { if (sftp.Exists(remotePart)) sftp.DeleteFile(remotePart); } catch { }
                    throw;
                }
                sftp.Disconnect();
            }
            return "VULN_DB_ARCHIVE='" + remote + "'\nVULN_DB_DIGEST='" + digest + "'\n";
        }

        // RunScript отменяет SSH-команду, но закрытие канала само по себе не гарантирует, что
        // запущенный внутри bash дочерний Trivy получил HUP. Каждый скан пишет свой PID в /run;
        // после любого исхода (успех, ошибка, Stop, таймаут) адресно дочищаем только этот процесс.
        private static void CleanupRemoteVulnerabilityScan(SshClient client, string runId, Action<string> log)
        {
            if (client == null || !client.IsConnected || string.IsNullOrEmpty(runId)) return;
            string file = "/run/rpu-trivy-" + runId + ".pid";
            string cmd = "f='" + file + "'; if [ -r \"$f\" ]; then p=$(cat \"$f\"); " +
                "case \"$p\" in *[!0-9]*|'') ;; *) kill -TERM \"$p\" 2>/dev/null || true; " +
                "sleep 1; kill -KILL \"$p\" 2>/dev/null || true ;; esac; rm -f \"$f\"; fi";
            try
            {
                using (var c = client.CreateCommand(cmd))
                {
                    c.CommandTimeout = TimeSpan.FromSeconds(5);
                    c.Execute();
                }
            }
            catch (Exception ex) { if (log != null) log("Не удалось проверить завершение удалённого Trivy: " + ex.Message); }
        }

        // Выполнить bash-скрипт на хосте потоково: строки идут в lineLog по мере вывода.
        // Есть таймаут (timeoutSec): если команда зависла (yum lock, недоступный репозиторий) - прервётся.
        private string RunScript(SshClient client, string scriptContent, string envPrefix, int timeoutSec, Action<string> lineLog, CancellationToken ct)
        {
            string content = scriptContent ?? "";
            if (!string.IsNullOrEmpty(envPrefix)) content = envPrefix + content;
            content = content.Replace("\r\n", "\n").Replace("\r", "\n");
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
            string cmd = "printf %s '" + b64 + "' | base64 -d | bash 2>&1";
            var sb = new StringBuilder();
            if (timeoutSec <= 0) timeoutSec = 1800;
            using (var command = client.CreateCommand(cmd))
            {
                // CommandTimeout выставлен для порядка/на случай будущих версий SSH.NET, но фактическую
                // защиту от зависания даёт watchdog-таймер ниже: при потоковом BeginExecute+ReadLine
                // библиотека не обрывает "немое" зависание (канал жив, вывода просто нет) по этому таймауту.
                command.CommandTimeout = TimeSpan.FromSeconds(timeoutSec);
                IAsyncResult ar = command.BeginExecute();
                bool timedOut = false;
                bool cancelled = false;
                bool finishedReading = false;   // защита от гонки: не считать таймаутом, если чтение уже дошло до конца
                // Watchdog: при "немом" зависании (TCP жив, вывода нет) ReadLine блокируется и CommandTimeout
                // не срабатывает - принудительно обрываем команду по таймауту, чтобы не держать поток вечно.
                using (var watchdog = new Timer(delegate {
                    try { if (!finishedReading && !ar.IsCompleted) { timedOut = true; command.CancelAsync(); } } catch { }
                }, null, timeoutSec * 1000, System.Threading.Timeout.Infinite))
                // Отмена пользователем ("Стоп") реально прерывает выполняющуюся команду, а не ждёт таймаут.
                using (ct.Register(() => { try { if (!ar.IsCompleted) { cancelled = true; command.CancelAsync(); } } catch { } }))
                using (var reader = new StreamReader(command.OutputStream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.IndexOf('\r') >= 0) line = line.Substring(line.LastIndexOf('\r') + 1);
                        sb.Append(line).Append('\n');
                        if (lineLog != null) lineLog(line);
                    }
                    finishedReading = true;
                    // EndExecute зовём ДО закрытия reader/OutputStream: иначе на уже освобождённом
                    // канале он может бросить и мы ложно пометим удачный прогон как [TIMEOUT_OR_ERROR].
                    try { command.EndExecute(ar); }
                    catch (Exception ex)
                    {
                        string msg = cancelled ? "отменено пользователем" : (timedOut ? "превышен таймаут " + timeoutSec + " c" : ex.Message);
                        if (lineLog != null) lineLog("[команда прервана: " + msg + "]");
                        sb.Append("\n[TIMEOUT_OR_ERROR]");
                    }
                }
            }
            return sb.ToString();
        }

        // Возвращает false, если команду reboot не удалось отправить (обрыв канала/таймаут) - раньше
        // это глушилось молча, и по факту "хост не вернулся после reboot" было невозможно понять,
        // отправлялась ли команда вообще или сервер просто долго поднимается.
        private bool IssueReboot(SshClient client, Action<string> log)
        {
            try
            {
                using (var c = client.CreateCommand("nohup bash -c 'sleep 2; systemctl reboot || reboot' >/dev/null 2>&1 & echo SCHEDULED"))
                { c.Execute(); }
                return true;
            }
            catch (Exception ex)
            {
                log("Не удалось отправить команду reboot: " + ex.Message);
                return false;
            }
        }

        // ICMP-пинг (быстрый признак доступности; в закрытых сетях ICMP может быть выключен).
        private static bool PingHost(string host, int timeoutMs)
        {
            try
            {
                using (var p = new System.Net.NetworkInformation.Ping())
                {
                    var r = p.Send(host, timeoutMs);
                    return r != null && r.Status == System.Net.NetworkInformation.IPStatus.Success;
                }
            }
            catch { return false; }
        }

        // Ждём завершения загрузки ОС после reboot: systemctl is-system-running != starting/initializing.
        // running/degraded = загрузка полностью завершена (degraded - часть юнитов не поднялась, но старт закончен).
        private void WaitSystemReady(SshClient client, Action<string> log, CancellationToken ct)
        {
            int limitSec = SystemReadyTimeoutSec;
            DateTime t0 = DateTime.Now;
            string st = "";
            while ((DateTime.Now - t0).TotalSeconds < limitSec)
            {
                if (ct.IsCancellationRequested) return;
                try { using (var c = client.CreateCommand("systemctl is-system-running 2>/dev/null")) st = (c.Execute() ?? "").Trim(); }
                catch { st = ""; }
                if (st == "running" || st == "degraded") { log("Загрузка ОС завершена (is-system-running=" + st + ")"); return; }
                if (string.IsNullOrEmpty(st)) { log("systemctl is-system-running недоступен - пропускаю ожидание готовности"); return; }
                log("Система ещё загружается (is-system-running=" + st + "), жду...");
                ct.WaitHandle.WaitOne(SystemReadyPollIntervalMs);
            }
            log("Загрузка не завершилась за " + limitSec + " c (is-system-running=" + st + "), продолжаю");
        }

        // boot_id меняется при каждой загрузке ОС - надёжный признак реальной перезагрузки.
        private string ReadBootId(SshClient client)
        {
            try { using (var c = client.CreateCommand("cat /proc/sys/kernel/random/boot_id 2>/dev/null")) { var o = c.Execute(); return (o ?? "").Trim(); } }
            catch { return ""; }
        }

        private string ProbeBootId(Node node, Credential cred, int timeoutSec)
        {
            SshClient c = null;
            try { c = ConnectWith(node, cred, timeoutSec); using (var cmd = c.CreateCommand("cat /proc/sys/kernel/random/boot_id 2>/dev/null")) { var o = cmd.Execute(); return (o ?? "").Trim(); } }
            catch { return null; }
            finally { SafeDisconnect(c); }
        }

        // Ждём возврата после reboot: если ICMP доступен - ждём пинг, потом подтверждаем сменой boot_id по SSH.
        // Если ICMP закрыт - опрашиваем boot_id по SSH напрямую. Ограничение - UpTimeoutSec (для железа ставьте 600 = 10 мин).
        private bool WaitReboot(Node node, Credential cred, RunOptions opt, Action<string> log, CancellationToken ct, string oldBoot, bool icmpOk)
        {
            log(icmpOk ? "Жду возврата хоста (ping + boot_id)..." : "Жду возврата хоста (ICMP закрыт, по SSH boot_id)...");
            if (string.IsNullOrEmpty(oldBoot))
                log("boot_id до ребута не прочитан - возврат подтверждаю только после " + WaitRebootConsecutiveDownToConfirm + " подряд неудачных проб доступности");
            // Нижний предел - чтобы проба не была совсем мгновенной; верхнего предела больше нет:
            // раньше ConnectTimeoutSec урезался до 15с даже если оператор явно поставил больше
            // (медленная сеть/VPN) - на живом, но медленном хосте это давало ложные неудачные пробы,
            // которые (в связке с "одна неудача = хост пропал") могли пометить неперезагруженный
            // хост как "вернулся после reboot". Порог consecutive-down ниже - вторая линия защиты от того же.
            int connT = Math.Max(WaitRebootProbeMinSec, opt.Settings.ConnectTimeoutSec);
            DateTime t0 = DateTime.Now;
            DateTime lastSsh = DateTime.MinValue;
            int consecutiveDown = 0;   // подряд неудачных проб доступности
            bool sawDown = false;      // подтверждено ли реальное исчезновение хоста (для случая пустого oldBoot)
            while ((DateTime.Now - t0).TotalSeconds < opt.Settings.UpTimeoutSec)
            {
                if (ct.IsCancellationRequested) return false;
                bool pinged = icmpOk ? PingHost(node.Host, PingTimeoutMs) : true;
                // SSH-проверку пробуем всегда: если пингуется - сразу; если нет - не реже раза в 20с
                // (чтобы не застрять, когда ICMP нестабилен, а хост уже поднялся). Если ping не прошёл,
                // но SSH-пробу в этом цикле не делаем (ещё не время) - трактуем это как down-сигнал по ping;
                // если SSH-пробу делаем - её результат приоритетнее ping (сильнее подтверждён), поэтому
                // не смешиваем оба сигнала в одном счётчике за один и тот же цикл (иначе успешный ping
                // может обнулить счётчик прямо перед тем, как в этом же цикле SSH-неудача его увеличит -
                // счётчик застревал бы на 1 и порог никогда бы не достигался).
                bool trySsh = pinged || (DateTime.Now - lastSsh).TotalSeconds >= ForceSshProbeIntervalSec;
                string nb = null;
                if (trySsh)
                {
                    lastSsh = DateTime.Now;
                    nb = ProbeBootId(node, cred, connT);
                    if (nb != null) consecutiveDown = 0; else consecutiveDown++;
                }
                else
                {
                    consecutiveDown++;   // ping не прошёл, SSH в этом цикле не пробовали
                }
                if (consecutiveDown >= WaitRebootConsecutiveDownToConfirm) sawDown = true;

                if (nb != null && !string.IsNullOrEmpty(oldBoot))
                {
                    // надёжный путь: ждём смену boot_id
                    if (nb != oldBoot) { log("Хост вернулся после перезагрузки (boot_id сменился)"); return true; }
                }
                else if (nb != null && sawDown)
                {
                    // boot_id неизвестен: принимаем возврат только если хост до этого ПОДТВЕРЖДЁННО пропадал
                    // (несколько проб подряд, не единичный сетевой блип)
                    log("Хост снова доступен после подтверждённой недоступности (boot_id не сверить)");
                    return true;
                }
                ct.WaitHandle.WaitOne(pinged ? RebootPollIntervalWhenPingedMs : RebootPollIntervalWhenNotPingedMs);
            }
            return false;
        }

        private static void SafeDisconnect(SshClient c)
        {
            if (c == null) return;
            try { if (c.IsConnected) c.Disconnect(); } catch { }
            try { c.Dispose(); } catch { }
        }

        private static string Marker(string output, string name)
        {
            if (string.IsNullOrEmpty(output)) return null;
            var m = Regex.Match(output, "^" + Regex.Escape(name) + ":\\s*(.+?)\\s*$", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : null;
        }

        // Разбор маркера "OS_INFO|PRETTY_NAME|ядро|dnf-версия" в компактную строку для грида/отчёта.
        // Печатается всеми профилями узла - парк RED OS может быть смешанным (7.3 / 8 / др.), и это
        // единственный способ увидеть версию ОС узла без ручной разметки в GUI.
        private static string OsInfoFromOutput(string output)
        {
            if (string.IsNullOrEmpty(output)) return "";
            var m = Regex.Match(output, "^OS_INFO\\|([^|]*)\\|([^|]*)\\|", RegexOptions.Multiline);
            if (!m.Success) return "";
            string name = m.Groups[1].Value.Trim();
            string kernel = m.Groups[2].Value.Trim();
            return string.IsNullOrEmpty(kernel) ? name : name + " (" + kernel + ")";
        }
    }
}
