using System;
using System.IO;
using System.Text;

namespace RedOSPackageUpdater
{
    /// <summary>Единая запись журналов узла. Изолирует файловый ввод-вывод от SSH workflow.</summary>
    internal sealed class HostOperationLog
    {
        private readonly string _path;
        private readonly string _host;
        private readonly Action<string, string> _liveSink;
        private readonly object _sync = new object();

        public HostOperationLog(string path, string host, Action<string, string> liveSink)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Не задан файл журнала", "path");
            _path = path;
            _host = host ?? "";
            _liveSink = liveSink;
        }

        public void Write(string line)
        {
            Append(line);
            if (_liveSink != null) _liveSink(_host, line ?? "");
        }

        public void Append(string line)
        {
            string record = DateTime.Now.ToString("HH:mm:ss") + " " + (line ?? "") + "\r\n";
            lock (_sync)
            {
                // Ошибка журнала не должна прерывать привилегированную операцию на сервере.
                // Она остаётся локальной, а живой вывод продолжает работать.
                try { File.AppendAllText(_path, record, new UTF8Encoding(false)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
