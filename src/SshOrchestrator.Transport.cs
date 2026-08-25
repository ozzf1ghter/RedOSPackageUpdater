using System;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using Renci.SshNet;

namespace RedOSPackageUpdater
{
    public partial class SshOrchestrator
    {
        // Выполнить bash-скрипт на хосте потоково: строки идут в lineLog по мере вывода.
        // Есть таймаут (timeoutSec): если команда зависла (yum lock, недоступный репозиторий) - прервётся.
        private string RunScript(SshClient client, string scriptContent, string envPrefix, int timeoutSec, Action<string> lineLog, CancellationToken ct)
        {
            return SshCommandRunner.Run(client, scriptContent, envPrefix, timeoutSec, lineLog, ct);
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

