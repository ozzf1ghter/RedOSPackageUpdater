using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace RedOSPackageUpdater
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // Загрузка вшитых в exe сборок (Renci.SshNet и её зависимости) из ресурсов.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;

            // Application.ThreadException ловит необработанные исключения ТОЛЬКО из UI-потока
            // (обработчики кнопок/меню/событий). Исключения из фоновых потоков SSH-операций
            // (SshOrchestrator работает через Task.Factory.StartNew) сюда не попадают -
            // они обязаны быть перехвачены внутри самой SSH-логики.
            Application.ThreadException += (s, e) =>
                MessageBox.Show("Ошибка: " + e.Exception.Message, "RED OS Package Updater",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            // AppDomain.UnhandledException - это уведомление постфактум, а не защита: CLR всё равно
            // завершит процесс сразу после этого обработчика, если exception прилетел не из UI-потока.
            // Единственная его польза здесь - показать пользователю причину падения перед закрытием.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                MessageBox.Show("Необработанная ошибка: " + (ex != null ? ex.Message : "неизвестно"),
                    "RED OS Package Updater", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show("Критическая ошибка: " + ex, "RED OS Package Updater",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Кеш уже загруженных embedded-сборок по имени. Без него повторный запрос одной и той же
        // сборки (бывает при повторном probing после сбойной попытки резолва) грузит её ЕЩЁ раз через
        // Assembly.Load - получаем два разных Assembly/Type с одинаковым именем и непонятные
        // InvalidCastException/MissingMethodException в духе "нельзя привести X к X".
        private static readonly Dictionary<string, Assembly> _resolvedCache = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        private static Assembly ResolveEmbedded(object sender, ResolveEventArgs args)
        {
            string wanted = new AssemblyName(args.Name).Name + ".dll";

            Assembly cached;
            if (_resolvedCache.TryGetValue(wanted, out cached)) return cached;

            var asm = Assembly.GetExecutingAssembly();
            foreach (string res in asm.GetManifestResourceNames())
            {
                if (res.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                    res.EndsWith("." + wanted, StringComparison.OrdinalIgnoreCase))
                {
                    using (Stream s = asm.GetManifestResourceStream(res))
                    {
                        if (s == null) return null;
                        byte[] buf = new byte[s.Length];
                        int off = 0, n;
                        while (off < buf.Length && (n = s.Read(buf, off, buf.Length - off)) > 0) off += n;
                        Assembly loaded = Assembly.Load(buf);
                        _resolvedCache[wanted] = loaded;
                        return loaded;
                    }
                }
            }
            return null;
        }
    }
}
