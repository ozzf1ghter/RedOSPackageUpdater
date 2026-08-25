using System;

namespace RedOSPackageUpdater
{
    /// <summary>Преобразует машинные маркеры shell-профиля в единый операторский результат.</summary>
    internal static class PkgOpResultPolicy
    {
        public static void Apply(HostResult host, PkgOpParseResult parsed, string action, bool dryRun)
        {
            if (host == null) throw new ArgumentNullException("host");
            if (parsed == null) throw new ArgumentNullException("parsed");

            string result = parsed.Result;
            string reboot = parsed.RebootRecommended;
            string errors = parsed.Errors;
            host.UpdateResult = result ?? "NO_MARKER";
            host.RebootRequired = reboot ?? "?";
            host.RebootAction = reboot == "yes" ? "нужен" : "-";

            if (string.Equals(action, "vuln", StringComparison.Ordinal))
            {
                ApplyVulnerability(host, parsed, result, errors);
                return;
            }

            if (result == "OK" && !string.IsNullOrEmpty(errors))
            {
                host.Status = HostStatus.Warn;
                host.Note = "изменено пакетов: " + parsed.Changed + ", не найдено: " + errors + RebootSuffix(reboot);
            }
            else if (!string.IsNullOrEmpty(errors) && parsed.Changed > 0)
            {
                host.Status = HostStatus.Warn;
                host.Note = "частичное выполнение: изменено пакетов: " + parsed.Changed + ", не найдено: " + errors + RebootSuffix(reboot);
            }
            else if (!string.IsNullOrEmpty(errors))
            {
                host.Status = HostStatus.Fail;
                host.Note = "операция не выполнена: " + errors;
            }
            else if (result == "OK")
            {
                host.Status = HostStatus.Ok;
                host.Note = (dryRun ? "к изменению пакетов: " : "изменено пакетов: ") + parsed.Changed + RebootSuffix(reboot);
            }
            else if (result == "NOTHING")
            {
                host.Status = HostStatus.Ok;
                host.Note = dryRun ? "изменений не будет (уже актуально)" : "изменений нет (уже актуально)";
            }
            else
            {
                host.Status = HostStatus.Fail;
                host.Note = "ошибка операции (PKGOP_RESULT=" + host.UpdateResult + ")";
            }
        }

        private static void ApplyVulnerability(HostResult host, PkgOpParseResult parsed, string result, string errors)
        {
            if (result != "OK")
            {
                host.Status = HostStatus.Fail;
                host.Note = !string.IsNullOrEmpty(errors) ? errors : "проверка бюллетеней RED OS завершилась ошибкой";
                return;
            }

            host.Status = parsed.VulnerabilityTotal > 0 ? HostStatus.Warn : HostStatus.Ok;
            host.UpdateResult = "кандидатов: " + parsed.VulnerabilityTotal;
            host.Note = parsed.VulnerabilityTotal > 0
                ? "CVE в доступных бюллетенях RED OS: " + parsed.VulnerabilityTotal +
                  " (БДУ будут сопоставлены в отчёте), критических: " + parsed.VulnerabilityCritical +
                  ", высоких: " + parsed.VulnerabilityHigh
                : "доступных бюллетеней безопасности не найдено";
        }

        private static string RebootSuffix(string reboot)
        {
            return reboot == "yes" ? ", рекомендуется перезагрузка" : "";
        }
    }
}
