using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace RedOSPackageUpdater
{
    internal sealed class UpdateInfo
    {
        public Version Version;
        public string VersionText;
        public string Sha256;
        public string CommitSha;
        public bool IsNewer;
    }

    internal static class AppUpdater
    {
        public const string CurrentVersion = BuildInfo.Version;
        private const string Owner = "ozzf1ghter";
        private const string Repo = "RedOSPackageUpdater";
        public static UpdateInfo Check()
        {
            // Один запрос к raw достаточно надёжен и заметно быстрее трёх
            // последовательных обращений к GitHub API. Возможная гонка во время
            // публикации безопасна: загруженный EXE обязательно сверяется с SHA-256
            // из манифеста и при несовпадении не устанавливается.
            UpdateInfo latest = ParseManifest(ReadTextWithRetry(
                "https://raw.githubusercontent.com/" + Owner + "/" + Repo + "/main/update.json"));
            latest.CommitSha = "main";
            return latest;
        }

        private static UpdateInfo ParseManifest(string manifest)
        {
            var m = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(manifest);
            string versionText = m != null && m.ContainsKey("version") ? Convert.ToString(m["version"]) : "";
            string sha = m != null && m.ContainsKey("sha256") ? Convert.ToString(m["sha256"]) : "";
            Version remote;
            if (!Version.TryParse(versionText, out remote)) throw new InvalidDataException("В update.json указана некорректная версия");
            if (!IsSha256(sha)) throw new InvalidDataException("В update.json отсутствует корректный SHA-256");
            sha = sha.ToLowerInvariant();
            Version current = new Version(CurrentVersion);
            string currentHash = remote == current ? FileSha256(Process.GetCurrentProcess().MainModule.FileName) : null;
            bool newer = UpdatePolicy.IsAvailable(remote, current, sha, currentHash);
            return new UpdateInfo { Version = remote, VersionText = versionText, Sha256 = sha, IsNewer = newer };
        }

        public static string Download(UpdateInfo info, Action<long, long> progress)
        {
            if (info == null) throw new ArgumentNullException("info");
            if (!IsGitSha(info.CommitSha) && !string.Equals(info.CommitSha, "main", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Не задан источник обновления");
            string current = Process.GetCurrentProcess().MainModule.FileName;
            string next = current + ".update";
            string actual;
            try
            {
                actual = WebRequests.Retry(() =>
                {
                    var req = WebRequests.Create("https://raw.githubusercontent.com/" + Owner + "/" + Repo + "/" + info.CommitSha + "/RedOSPackageUpdater.exe", 30000);
                    req.ReadWriteTimeout = 60000;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var input = resp.GetResponseStream())
                    using (var output = new FileStream(next, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var hash = SHA256.Create())
                    {
                        if (input == null) throw new IOException("Сервер вернул пустой поток обновления");
                        long total = resp.ContentLength, done = 0;
                        if (progress != null) progress(0, total);
                        byte[] buffer = new byte[128 * 1024]; int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, read);
                            hash.TransformBlock(buffer, 0, read, null, 0);
                            done += read;
                            if (progress != null) progress(done, total);
                        }
                        hash.TransformFinalBlock(new byte[0], 0, 0);
                        return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
                    }
                }, 3);
            }
            catch
            {
                try { if (File.Exists(next)) File.Delete(next); } catch { }
                throw;
            }
            if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(next); } catch { }
                throw new InvalidDataException("SHA-256 загруженного обновления не совпадает с update.json");
            }
            try { ValidateExecutable(next); }
            catch { try { File.Delete(next); } catch { } throw; }
            return next;
        }

        public static void InstallAndRestart(string downloadedPath)
        {
            string current = Process.GetCurrentProcess().MainModule.FileName;
            if (!File.Exists(downloadedPath)) throw new FileNotFoundException("Файл обновления не найден", downloadedPath);
            string script = Path.Combine(Path.GetDirectoryName(current), "rpu_apply_update.cmd");
            string body = "@echo off\r\nsetlocal\r\n" +
                "set \"OLD=" + BatchLiteral(current) + "\"\r\nset \"NEW=" + BatchLiteral(downloadedPath) + "\"\r\n" +
                ":wait\r\ntimeout /t 1 /nobreak >nul\r\n" +
                "tasklist /fi \"PID eq " + Process.GetCurrentProcess().Id + "\" | find \"" + Process.GetCurrentProcess().Id + "\" >nul && goto wait\r\n" +
                "set /a TRY=0\r\n:replace\r\nset /a TRY+=1\r\n" +
                "move /y \"%NEW%\" \"%OLD%\" >nul 2>&1\r\n" +
                "if not errorlevel 1 goto success\r\n" +
                "if %TRY% LSS 30 (timeout /t 1 /nobreak >nul & goto replace)\r\n" +
                "echo [%date% %time%] Не удалось заменить файл после 30 попыток.>\"%OLD%.update-error.log\"\r\n" +
                "start \"\" \"%OLD%\"\r\ndel \"%~f0\"\r\nexit /b 1\r\n" +
                ":success\r\ndel \"%OLD%.update-error.log\" >nul 2>&1\r\n" +
                "start \"\" \"%OLD%\"\r\ndel \"%~f0\"\r\n";
            // Encoding.Default сохраняет кириллицу в локальных путях в кодировке cmd.exe.
            File.WriteAllText(script, body, Encoding.Default);
            Process.Start(new ProcessStartInfo { FileName = script, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
        }

        private static string ReadTextWithRetry(string url)
        {
            // Ручная проверка должна быстро вернуть понятную ошибку, а не держать
            // интерфейс до 45 секунд на трёх системных таймаутах.
            return WebRequests.Retry(() => WebRequests.ReadUtf8(WebRequests.Create(url, 8000)), 1);
        }

        private static bool IsGitSha(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 40) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }

        internal static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }

        internal static string BatchLiteral(string value)
        {
            // Внутри batch-файла знак процента раскрывает переменные окружения
            // даже в кавычках. Удваиваем его, чтобы редкий, но допустимый путь с
            // '%' не ломал автоматическое обновление.
            return (value ?? "").Replace("%", "%%");
        }

        private static void ValidateExecutable(string path)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < 1024 * 1024)
                throw new InvalidDataException("Загруженный файл обновления имеет некорректный размер");
            using (var stream = File.OpenRead(path))
                if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
                    throw new InvalidDataException("Загруженный файл не является Windows-программой");
        }

        private static string FileSha256(string path)
        {
            using (var hash = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
