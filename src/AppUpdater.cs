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
        public string Notes;
        public bool IsNewer;
    }

    internal static class AppUpdater
    {
        // Semantic Versioning: major.minor.patch. При выпуске менять вместе с update.json.
        public const string CurrentVersion = "1.1.1";
        private const string Owner = "ozzf1ghter";
        private const string Repo = "RedOSPackageUpdater";
        public static UpdateInfo Check()
        {
            var content = ApiObject("/repos/" + Owner + "/" + Repo + "/contents/update.json?ref=main");
            string manifest = DecodeContent(content);
            var m = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(manifest);
            string versionText = m != null && m.ContainsKey("version") ? Convert.ToString(m["version"]) : "";
            string sha = m != null && m.ContainsKey("sha256") ? Convert.ToString(m["sha256"]) : "";
            string notes = m != null && m.ContainsKey("notes") ? Convert.ToString(m["notes"]) : "";
            Version remote;
            if (!Version.TryParse(versionText, out remote)) throw new InvalidDataException("В update.json указана некорректная версия");
            if (string.IsNullOrEmpty(sha) || sha.Length != 64) throw new InvalidDataException("В update.json отсутствует SHA-256");
            return new UpdateInfo { Version = remote, VersionText = versionText, Sha256 = sha.ToLowerInvariant(), Notes = notes, IsNewer = remote > new Version(CurrentVersion) };
        }

        public static string Download(UpdateInfo info, Action<long, long> progress)
        {
            if (info == null) throw new ArgumentNullException("info");
            var fileInfo = ApiObject("/repos/" + Owner + "/" + Repo + "/contents/RedOSPackageUpdater.exe?ref=main");
            string blobSha = fileInfo.ContainsKey("sha") ? Convert.ToString(fileInfo["sha"]) : "";
            if (string.IsNullOrEmpty(blobSha)) throw new InvalidDataException("GitHub не вернул идентификатор EXE");
            var blob = ApiObject("/repos/" + Owner + "/" + Repo + "/git/blobs/" + blobSha, 32 * 1024 * 1024);
            byte[] bytes = Convert.FromBase64String((Convert.ToString(blob["content"]) ?? "").Replace("\n", ""));
            if (progress != null) progress(bytes.Length, bytes.Length);
            string actual;
            using (var hash = SHA256.Create()) actual = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 загруженного обновления не совпадает с update.json");
            string current = Process.GetCurrentProcess().MainModule.FileName;
            string next = current + ".update";
            File.WriteAllBytes(next, bytes);
            return next;
        }

        public static void InstallAndRestart(string downloadedPath)
        {
            string current = Process.GetCurrentProcess().MainModule.FileName;
            if (!File.Exists(downloadedPath)) throw new FileNotFoundException("Файл обновления не найден", downloadedPath);
            string script = Path.Combine(Path.GetDirectoryName(current), "rpu_apply_update.cmd");
            string body = "@echo off\r\nsetlocal\r\n" +
                "set \"OLD=" + current + "\"\r\nset \"NEW=" + downloadedPath + "\"\r\n" +
                ":wait\r\ntimeout /t 1 /nobreak >nul\r\n" +
                "tasklist /fi \"PID eq " + Process.GetCurrentProcess().Id + "\" | find \"" + Process.GetCurrentProcess().Id + "\" >nul && goto wait\r\n" +
                "move /y \"%NEW%\" \"%OLD%\" >nul\r\nstart \"\" \"%OLD%\"\r\ndel \"%~f0\"\r\n";
            // Encoding.Default сохраняет кириллицу в локальных путях в кодировке cmd.exe.
            File.WriteAllText(script, body, Encoding.Default);
            Process.Start(new ProcessStartInfo { FileName = script, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
        }

        private static Dictionary<string, object> ApiObject(string path, int maxJson = 1024 * 1024)
        {
            var req = (HttpWebRequest)WebRequest.Create("https://api.github.com" + path);
            req.UserAgent = "RedOSPackageUpdater/" + CurrentVersion;
            req.Accept = "application/vnd.github+json";
            req.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            req.Timeout = 30000; req.ReadWriteTimeout = 60000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = maxJson };
                return ser.Deserialize<Dictionary<string, object>>(sr.ReadToEnd());
            }
        }

        private static string DecodeContent(Dictionary<string, object> obj)
        {
            string enc = obj != null && obj.ContainsKey("encoding") ? Convert.ToString(obj["encoding"]) : "";
            string content = obj != null && obj.ContainsKey("content") ? Convert.ToString(obj["content"]) : "";
            if (enc != "base64" || string.IsNullOrEmpty(content)) throw new InvalidDataException("GitHub не вернул содержимое файла");
            return Encoding.UTF8.GetString(Convert.FromBase64String(content.Replace("\n", "")));
        }
    }
}
