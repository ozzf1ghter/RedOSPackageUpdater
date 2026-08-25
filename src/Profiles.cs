using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace RedOSPackageUpdater
{
    // Доступ к вшитым в exe bash-профилям (embedded resources). Имена ресурсов = имена файлов.
    internal static class Profiles
    {
        public const string KernelSecurity = "redos_kernel_security.sh";
        public const string SecurityOnly = "redos_security_only.sh";
        public const string KernelOnly = "redos_kernel_only.sh";
        public const string Preview = "redos_preview.sh";
        public const string PreStop = "redos_prestop.sh";
        public const string PostCheck = "redos_postcheck.sh";
        public const string PkgOp = "redos_pkgop.sh";
        public const string AdvisoryScan = "redos_advisory_scan.sh";

        // Скрипты неизменны в течение жизни процесса (это embedded resources внутри самого exe),
        // а Read() дёргается на каждый узел при массовом обновлении - кешируем, чтобы не гонять
        // Stream->StreamReader->string и два Replace на одни и те же байты сотни раз за прогон.
        private static readonly Dictionary<string, string> _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly object _cacheLock = new object();

        public static string Read(string resourceName)
        {
            lock (_cacheLock)
            {
                string cached;
                if (_cache.TryGetValue(resourceName, out cached)) return cached;
                var asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream(resourceName))
                {
                    if (s == null) throw new InvalidOperationException("Не найден встроенный ресурс: " + resourceName);
                    using (var r = new StreamReader(s, Encoding.UTF8))
                    {
                        string text = r.ReadToEnd().Replace("\r\n", "\n").Replace("\r", "\n");
                        _cache[resourceName] = text;
                        return text;
                    }
                }
            }
        }

        // Необязательные ресурсы (например, seed персональной сборки) отсутствуют
        // в обычном публичном EXE. Их отсутствие — штатный случай, а не повреждение.
        public static string TryRead(string resourceName)
        {
            if (string.IsNullOrWhiteSpace(resourceName)) return null;
            lock (_cacheLock)
            {
                string cached;
                if (_cache.TryGetValue(resourceName, out cached)) return cached;
                var asm = Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return null;
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string text = reader.ReadToEnd().Replace("\r\n", "\n").Replace("\r", "\n");
                        _cache[resourceName] = text;
                        return text;
                    }
                }
            }
        }
    }
}
