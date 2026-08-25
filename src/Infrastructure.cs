using System;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace RedOSPackageUpdater
{
    internal static class ShellText
    {
        // Значение помещается между одинарными кавычками bash. Последовательность
        // '"'"' закрывает литерал, добавляет одинарную кавычку и открывает его снова.
        public static string InSingleQuotes(string value)
        {
            return (value ?? "").Replace("'", "'\"'\"'");
        }
    }

    internal static class HostIdentity
    {
        public static string Label(string name, string host)
        {
            name = (name ?? "").Trim(); host = (host ?? "").Trim();
            if (name.Length == 0) return host;
            if (host.Length == 0 || string.Equals(name, host, StringComparison.OrdinalIgnoreCase)) return name;
            return name + " (" + host + ")";
        }

        public static string CacheKey(string host, int port)
        {
            return (host ?? "").Trim() + ":" + (port <= 0 ? 22 : port);
        }
    }

    internal static class ParallelBatch
    {
        public static void Run<T>(IEnumerable<T> source, int maxParallel, CancellationToken cancellation,
            Action<T> body, Action<T, Exception> onError)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (body == null) throw new ArgumentNullException("body");
            var tasks = new List<Task>();
            using (var slots = new SemaphoreSlim(Math.Max(1, maxParallel)))
            {
                foreach (T item in new List<T>(source))
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        NotifyError(onError, item, new OperationCanceledException(cancellation));
                        continue;
                    }
                    try { slots.Wait(cancellation); }
                    catch (OperationCanceledException ex) { NotifyError(onError, item, ex); continue; }
                    T captured = item;
                    tasks.Add(Task.Factory.StartNew(() =>
                    {
                        try
                        {
                            if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
                            body(captured);
                        }
                        catch (Exception ex) { NotifyError(onError, captured, ex); }
                        finally { slots.Release(); }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
                }
                Task.WaitAll(tasks.ToArray());
            }
        }

        private static void NotifyError<T>(Action<T, Exception> onError, T item, Exception error)
        {
            if (onError == null) return;
            // Ошибка обработчика означает нарушение контракта батча (например,
            // результат узла не удалось добавить в итоговую коллекцию). Не маскируем
            // её: иначе UI покажет завершение операции с пропавшей целью.
            onError(item, error);
        }
    }

    internal static class CredentialCandidates
    {
        public static List<Credential> Build(IEnumerable<Credential> pool, CachedCred cached)
        {
            var result = new List<Credential>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Add(result, seen, cached != null ? cached.User : null, cached != null ? cached.Password : null);
            foreach (Credential credential in pool ?? new Credential[0])
                if (credential != null) Add(result, seen, credential.User, credential.Password);
            return result;
        }

        private static void Add(List<Credential> result, HashSet<string> seen, string user, string password)
        {
            user = (user ?? "").Trim();
            if (user.Length == 0 || password == null) return;
            string key = user + "\0" + password;
            if (!seen.Add(key)) return;
            result.Add(new Credential { User = user, Password = password });
        }
    }

    internal static class FileSwap
    {
        public static void Replace(string source, string target)
        {
            if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Не задан исходный файл", "source");
            if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("Не задан целевой файл", "target");
            string backup = target + ".old";
            try
            {
                if (File.Exists(backup)) File.Delete(backup);
                if (File.Exists(target)) File.Move(target, backup);
                File.Move(source, target);
            }
            catch
            {
                if (!File.Exists(target) && File.Exists(backup)) File.Move(backup, target);
                throw;
            }
            try { if (File.Exists(backup)) File.Delete(backup); } catch { }
        }
    }

    internal static class UpdatePolicy
    {
        public static bool IsAvailable(Version remoteVersion, Version currentVersion, string publishedHash, string currentHash)
        {
            if (remoteVersion == null) throw new ArgumentNullException("remoteVersion");
            if (currentVersion == null) throw new ArgumentNullException("currentVersion");
            if (remoteVersion > currentVersion) return true;
            if (remoteVersion < currentVersion) return false;
            return !string.Equals(publishedHash ?? "", currentHash ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class WebRequests
    {
        public static HttpWebRequest Create(string url, int timeoutMs)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = BuildInfo.UserAgent;
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = Math.Max(timeoutMs, 30000);
            request.AllowAutoRedirect = true;
            request.CachePolicy = new HttpRequestCachePolicy(HttpRequestCacheLevel.NoCacheNoStore);
            request.Headers[HttpRequestHeader.Pragma] = "no-cache";
            return request;
        }

        public static T Retry<T>(Func<T> operation, int attempts)
        {
            if (operation == null) throw new ArgumentNullException("operation");
            if (attempts < 1) throw new ArgumentOutOfRangeException("attempts");
            for (int attempt = 1; ; attempt++)
            {
                try { return operation(); }
                catch (WebException ex)
                {
                    if (attempt >= attempts || !IsTransient(ex)) throw;
                    if (ex.Response != null) ex.Response.Dispose();
                    Thread.Sleep(attempt == 1 ? 500 : 1500);
                }
            }
        }

        public static string ReadUtf8(HttpWebRequest request)
        {
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        internal static bool IsTransient(WebException ex)
        {
            var response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                int code = (int)response.StatusCode;
                return code == 429 || code == 502 || code == 503 || code == 504;
            }
            return ex.Status == WebExceptionStatus.Timeout ||
                   ex.Status == WebExceptionStatus.ConnectFailure ||
                   ex.Status == WebExceptionStatus.ConnectionClosed ||
                   ex.Status == WebExceptionStatus.NameResolutionFailure ||
                   ex.Status == WebExceptionStatus.ReceiveFailure ||
                   ex.Status == WebExceptionStatus.SendFailure;
        }
    }
}
