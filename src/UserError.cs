using System;
using System.Net;

namespace RedOSPackageUpdater
{
    internal static class UserError
    {
        public static string Message(Exception error)
        {
            var web = error as WebException;
            if (web == null) return error != null ? error.Message : "Неизвестная ошибка";
            switch (web.Status)
            {
                case WebExceptionStatus.NameResolutionFailure: return "Не удалось определить адрес сервера. Проверьте DNS и подключение к сети.";
                case WebExceptionStatus.ConnectFailure: return "Не удалось подключиться к серверу. Проверьте сеть, прокси и межсетевой экран.";
                case WebExceptionStatus.Timeout: return "Сервер не ответил за отведённое время.";
                case WebExceptionStatus.TrustFailure: return "Windows не доверяет TLS-сертификату сервера. Проверьте дату, корневые сертификаты и HTTPS-прокси.";
                case WebExceptionStatus.SecureChannelFailure: return "Не удалось установить защищённое TLS-соединение. Требуются TLS 1.2 и актуальные корневые сертификаты Windows.";
                case WebExceptionStatus.ProtocolError:
                    var response = web.Response as HttpWebResponse;
                    if (response != null)
                    {
                        string result = "Сервер вернул ошибку HTTP " + (int)response.StatusCode + " (" + response.StatusDescription + ").";
                        response.Dispose();
                        return result;
                    }
                    break;
            }
            return web.Message;
        }
    }
}
