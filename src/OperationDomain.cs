using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace RedOSPackageUpdater
{
    /// <summary>
    /// Правила результатов и журналов, не зависящие от WinForms.
    /// </summary>
    internal static class OperationDomain
    {
        private const string LogDirTimeFormat = "yyyy-MM-dd_HHmmss_fff";

        public static string ActionTitle(string action)
        {
            switch (action)
            {
                case "install": return "Установка пакетов";
                case "remove": return "Удаление пакетов";
                case "lock": return "Закрепление версий";
                case "unlock": return "Снятие закрепления версий";
                case "locklist": return "Просмотр закреплённых версий";
                default: return "Обновление пакетов";
            }
        }

        private static readonly Regex SafePackageToken = new Regex(@"^@?[A-Za-z0-9_+.*?:~]+(?:-[A-Za-z0-9_+.*?:~]+)*$", RegexOptions.Compiled);

        public static bool TryNormalizePackageList(string value, out string normalized, out string error)
        {
            var result = new List<string>();
            foreach (string token in (value ?? "").Replace("\r", " ").Replace("\n", " ").Split(' '))
            {
                string item = token.Trim();
                if (item.Length == 0) continue;
                // Значение передаётся DNF как набор аргументов. Не разрешаем ключи командной строки,
                // пути и shell-подобный мусор: оператор должен вводить только имя/NEVRA/маску пакета.
                if (item.StartsWith("-", StringComparison.Ordinal) || !SafePackageToken.IsMatch(item))
                {
                    normalized = null;
                    error = "Недопустимое имя пакета: «" + item + "». Используйте имя, NEVRA, маску '*' или группу с префиксом @; параметры DNF здесь запрещены.";
                    return false;
                }
                result.Add(item);
            }
            normalized = result.Count == 0 ? null : string.Join(" ", result.ToArray());
            error = null;
            return true;
        }

        public static bool TryNormalizeServiceMasks(IEnumerable<string> values, out List<string> normalized, out string error)
        {
            normalized = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var safe = new Regex(@"^[A-Za-z0-9_.@*?-]+$", RegexOptions.Compiled);
            foreach (string raw in values ?? new string[0])
            {
                string value = (raw ?? "").Trim();
                if (value.Length == 0) continue;
                if (value.StartsWith("-", StringComparison.Ordinal) || !safe.IsMatch(value))
                {
                    error = "Недопустимое имя или маска службы: «" + value + "». Используйте имя systemd unit и при необходимости символ '*'.";
                    normalized = null;
                    return false;
                }
                if (seen.Add(value)) normalized.Add(value);
            }
            error = null;
            return true;
        }

        public static string NewLogDirectory(string root, string prefix, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Не задан каталог журналов", "root");
            if (string.IsNullOrWhiteSpace(prefix)) throw new ArgumentException("Не задан префикс операции", "prefix");
            return Path.Combine(root, prefix + now.ToString(LogDirTimeFormat));
        }

        public static List<T> OrderLikeTargets<T>(IEnumerable<T> results, IList<RunTarget> targets, Func<T, string> hostOf)
        {
            if (hostOf == null) throw new ArgumentNullException("hostOf");
            var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (targets != null)
                for (int i = 0; i < targets.Count; i++)
                {
                    string host = targets[i] != null && targets[i].Node != null ? targets[i].Node.Host ?? "" : "";
                    if (!positions.ContainsKey(host)) positions[host] = i;
                }

            var ordered = results == null ? new List<T>() : new List<T>(results);
            ordered.Sort((left, right) =>
            {
                int leftIndex;
                if (!positions.TryGetValue(hostOf(left) ?? "", out leftIndex)) leftIndex = int.MaxValue;
                int rightIndex;
                if (!positions.TryGetValue(hostOf(right) ?? "", out rightIndex)) rightIndex = int.MaxValue;
                return leftIndex.CompareTo(rightIndex);
            });
            return ordered;
        }

        public static BatchCounts CountResults(IEnumerable<HostResult> results)
        {
            var counts = new BatchCounts();
            if (results == null) return counts;
            foreach (HostResult result in results)
            {
                if (result != null && result.Status == HostStatus.Ok) counts.Ok++;
                else if (result != null && result.Status == HostStatus.Warn) counts.Warn++;
                else counts.Fail++;
            }
            return counts;
        }
    }

    internal sealed class BatchCounts
    {
        public int Ok;
        public int Warn;
        public int Fail;

        public string StatusText
        {
            get { return string.Format("Готово. OK: {0}  WARN: {1}  FAIL: {2}", Ok, Warn, Fail); }
        }
    }
}
