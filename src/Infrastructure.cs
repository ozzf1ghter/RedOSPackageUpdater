using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

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

    internal static class WebRequests
    {
        public static HttpWebRequest Create(string url, int timeoutMs)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = BuildInfo.UserAgent;
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = Math.Max(timeoutMs, 30000);
            request.AllowAutoRedirect = true;
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
