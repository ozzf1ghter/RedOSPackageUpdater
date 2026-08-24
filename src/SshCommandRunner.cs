using System;
using System.IO;
using System.Text;
using System.Threading;
using Renci.SshNet;

namespace RedOSPackageUpdater
{
    internal static class SshCommandRunner
    {
        public static string Run(SshClient client, string scriptContent, string environmentPrefix,
            int timeoutSec, Action<string> lineLog, CancellationToken cancellation)
        {
            if (client == null) throw new ArgumentNullException("client");
            string content = scriptContent ?? "";
            if (!string.IsNullOrEmpty(environmentPrefix)) content = environmentPrefix + content;
            content = content.Replace("\r\n", "\n").Replace("\r", "\n");
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
            string shellCommand = "printf %s '" + encoded + "' | base64 -d | bash 2>&1";
            if (timeoutSec <= 0) timeoutSec = 1800;

            var output = new StringBuilder();
            using (var command = client.CreateCommand(shellCommand))
            {
                command.CommandTimeout = TimeSpan.FromSeconds(timeoutSec);
                IAsyncResult execution = command.BeginExecute();
                int timedOut = 0, cancelled = 0, finishedReading = 0;
                using (var watchdog = new Timer(delegate
                {
                    try
                    {
                        if (Volatile.Read(ref finishedReading) == 0 && !execution.IsCompleted)
                        {
                            Interlocked.Exchange(ref timedOut, 1);
                            command.CancelAsync();
                        }
                    }
                    catch { }
                }, null, checked(timeoutSec * 1000), Timeout.Infinite))
                using (cancellation.Register(() =>
                {
                    try
                    {
                        if (!execution.IsCompleted)
                        {
                            Interlocked.Exchange(ref cancelled, 1);
                            command.CancelAsync();
                        }
                    }
                    catch { }
                }))
                using (var reader = new StreamReader(command.OutputStream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.IndexOf('\r') >= 0) line = line.Substring(line.LastIndexOf('\r') + 1);
                        output.Append(line).Append('\n');
                        if (lineLog != null) lineLog(line);
                    }
                    Volatile.Write(ref finishedReading, 1);
                    try { command.EndExecute(execution); }
                    catch (Exception ex)
                    {
                        string reason = Volatile.Read(ref cancelled) != 0 ? "отменено пользователем" :
                            (Volatile.Read(ref timedOut) != 0 ? "превышен таймаут " + timeoutSec + " c" : ex.Message);
                        if (lineLog != null) lineLog("[команда прервана: " + reason + "]");
                        output.Append("\n[TIMEOUT_OR_ERROR]");
                    }
                }
            }
            return output.ToString();
        }
    }
}
