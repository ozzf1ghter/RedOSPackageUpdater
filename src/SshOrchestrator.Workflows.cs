using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Renci.SshNet;

namespace RedOSPackageUpdater
{
    public partial class SshOrchestrator
    {
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
            SaveCredentialCacheIfDirty();
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

        private static string NodeLabel(Node node)
        {
            return NodeLabel(node != null ? node.Name : "", node != null ? node.Host : "");
        }

        private static string NodeLabel(string name, string host)
        {
            return HostIdentity.Label(name, host);
        }

        // Логгер конкретного узла: пишет в его лог-файл на диске и одновременно шлёт строку в
        // живой лог UI через OnLog (см. контракт многопоточности у объявления OnLog выше).
        private Action<string> MakeLogger(string logFile, string host)
        {
            var writer = new HostOperationLog(logFile, host, (h, line) => { if (OnLog != null) OnLog(h, line); });
            return writer.Write;
        }

        private HostPreview PreviewHost(RunTarget target, string previewScript, string excludeMasks,
            string profileKey, AppSettings settings, string logDir, CancellationToken ct)
        {
            var node = target.Node;
            var hp = new HostPreview { System = target.System != null ? target.System.Name : "", Name = node.Name, Host = node.Host };
            string logFile = Path.Combine(logDir, SafeFileName(node, "host") + ".preview.log");
            Action<string> log = MakeLogger(logFile, node.Host);
            if (OnPreviewStart != null) OnPreviewStart(node.Host);
            log("=== PREVIEW " + NodeLabel(node) + " ===");

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
                log("=== PREVIEW готово " + NodeLabel(node) + ": в транзакции " + hp.Total + " (advisory " + hp.Sec + ", завис " + hp.Dep + ", исключено " + hp.Excluded + ") ===");
            else
                log("=== PREVIEW ошибка " + NodeLabel(node) + ": " + hp.Error + " ===");

            if (OnPreviewDone != null) OnPreviewDone(hp);
            return hp;
        }

        // ============ Установка/обновление произвольных пакетов ============
        public List<HostResult> RunPkgOp(List<RunTarget> targets, string action, string pkgs, bool dryRun, string script,
            AppSettings settings, string logDir, CancellationToken ct)
        {
            var results = new List<HostResult>();
            var resLock = new object();
            RunParallel(targets, settings.MaxParallel, ct, t =>
            {
                var r = PkgOpHost(t, action, pkgs, dryRun, script, settings, logDir, ct);
                lock (resLock) results.Add(r);
            }, (t, ex) => { lock (resLock) results.Add(SyntheticFailResult(t, ex)); });
            SaveCredentialCacheIfDirty();
            return results;
        }

        private HostResult PkgOpHost(RunTarget target, string action, string pkgs, bool dryRun, string script,
            AppSettings settings, string logDir, CancellationToken ct)
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
            log("=== PKGOP " + NodeLabel(node) + " (" + action + ": " + pkgs + ") ===");

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
                Phase(node.Host, action == "vuln" ? "scan" : (dryRun ? "preview" : "update"));
                string prefix = "ACTION='" + Sh(action) + "'\nPKGS='" + Sh(pkgs) + "'\n";
                if (dryRun) prefix += "DRYRUN='1'\n";
                int timeout = settings.UpdateTimeoutSec > 0 ? settings.UpdateTimeoutSec : PkgOpTimeoutSec;
                string outp = RunScript(client, script, prefix, timeout, log, ct);
                res.OsInfo = OsInfoFromOutput(outp);

                PkgOpParseResult parsed = PkgOpOutputParser.Parse(outp, res);
                PkgOpResultPolicy.Apply(res, parsed, action, dryRun);
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
            Action<string> log = MakeLogger(res.LogFile, node.Host);
            var fileLog = new HostOperationLog(res.LogFile, node.Host, null);
            Action<string> writeFile = fileLog.Append;
            if (OnHostStart != null) OnHostStart(res);
            log("=== REPO " + NodeLabel(node) + " ===");

            SshClient client = null; Credential used = null;
            try { client = ResolveAndConnect(node, new RunOptions { Settings = settings }, log, res, ct, out used); }
            catch (Exception ex) { log("Ошибка подключения: " + ex.Message); }
            if (client == null)
            {
                res.Status = HostStatus.Fail;
                if (string.IsNullOrEmpty(res.Note)) res.Note = "не удалось подключиться";
                Finish(res, log);
                SaveCredentialCacheIfDirty();
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
                    if (exit == "0") okCount++;
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
            SaveCredentialCacheIfDirty();
            return res;
        }

    }
}
