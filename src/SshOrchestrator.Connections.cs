using System;
using System.Collections.Generic;
using System.Threading;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RedOSPackageUpdater
{
    public partial class SshOrchestrator
    {
        // ---- Подбор учётки: кеш -> пул, с кешированием результата ----
        private SshClient ResolveAndConnect(Node node, RunOptions opt, Action<string> log,
            HostResult res, CancellationToken ct, out Credential used)
        {
            used = null;
            string key = HostIdentity.CacheKey(node.Host, node.Port);
            CachedCred cached = null;
            lock (_cacheLock) { _cache.TryGetValue(key, out cached); }
            var candidates = CredentialCandidates.Build(_pool, cached);
            if (candidates.Count == 0) { res.Note = "нет доступных учёток (пул пуст либо пароли не удалось расшифровать)"; return null; }

            int attempts = 0;
            bool anyAuthFail = false;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (ct.IsCancellationRequested) return null;
                if (opt.Settings.MaxAuthAttempts > 0 && attempts >= opt.Settings.MaxAuthAttempts)
                { log("Достигнут лимит попыток учёток (" + opt.Settings.MaxAuthAttempts + ")"); break; }

                var cand = candidates[i];
                attempts++;
                bool wasCached = cached != null && string.Equals(cand.User, (cached.User ?? "").Trim(), StringComparison.Ordinal) && cand.Password == cached.Password;
                try
                {
                    var client = ConnectWith(node, cand, opt.Settings.ConnectTimeoutSec);
                    used = cand;
                    if (!wasCached)
                    {
                        lock (_cacheLock)
                        {
                            _cache[key] = new CachedCred { Key = key, User = cand.User, Password = cand.Password };
                            Interlocked.Exchange(ref _cacheDirty, 1);
                        }
                        log("Подобрана рабочая учётка (" + cand.User + "), закеширована");
                    }
                    else log("Учётка из кеша подошла (" + cand.User + ")");
                    return client;
                }
                catch (SshAuthenticationException)
                {
                    anyAuthFail = true;
                    if (wasCached)
                    {
                        log("Кешированная учётка не подошла - перебор пула");
                        lock (_cacheLock) { _cache.Remove(key); Interlocked.Exchange(ref _cacheDirty, 1); }
                    }
                    else log("Учётка (" + cand.User + ") не подошла");
                    if (opt.Settings.AuthRetryDelayMs > 0) ct.WaitHandle.WaitOne(opt.Settings.AuthRetryDelayMs);
                }
                catch (Exception ex)
                {
                    // сетевая/таймаут - дальше перебирать бессмысленно
                    res.Note = "нет связи: " + ex.Message;
                    log("Сетевая ошибка: " + ex.Message);
                    return null;
                }
            }
            res.Note = anyAuthFail ? "не подошла ни одна учётка из пула" : "не удалось подключиться";
            return null;
        }

        private void SaveCredentialCacheIfDirty()
        {
            if (Interlocked.Exchange(ref _cacheDirty, 0) == 0) return;
            lock (_cacheLock) _stateStore.SaveCredentialCache(_cache);
        }

        private SshClient ConnectWith(Node node, Credential cred, int timeoutSec)
        {
            var ci = new PasswordConnectionInfo(node.Host, node.Port <= 0 ? 22 : node.Port, cred.User, cred.Password);
            ci.Timeout = TimeSpan.FromSeconds(timeoutSec);
            var client = new SshClient(ci);
            bool mismatch = false;
            try
            {
                // TOFU (trust-on-first-use) pinning host-ключа, как ~/.ssh/known_hosts у обычного ssh.
                // Раньше здесь было e.CanTrust = true безусловно для ЛЮБОГО ключа - инструмент шлёт
                // пароли и выполняет привилегированные команды на проде, а безусловное доверие
                // означает, что MITM между машиной инженера и сервером остаётся никак не обнаружимым.
                // Первое подключение к узлу - ключ запоминается. Дальше - должен совпадать; если нет,
                // соединение обрывается, а не тихо продолжается с новым (возможно подменённым) ключом.
                client.HostKeyReceived += (s, e) =>
                {
                    string key = HostIdentity.CacheKey(node.Host, node.Port);
                    string fp = e.FingerPrintSHA256;
                    lock (_knownHostsLock)
                    {
                        string known;
                        if (_knownHosts.TryGetValue(key, out known))
                        {
                            e.CanTrust = string.Equals(known, fp, StringComparison.Ordinal);
                            if (!e.CanTrust) mismatch = true;
                        }
                        else
                        {
                            bool accepted = OnUnknownHostKey != null &&
                                OnUnknownHostKey(node.Host, node.Port <= 0 ? 22 : node.Port, fp);
                            e.CanTrust = accepted;
                            if (accepted)
                            {
                                _knownHosts[key] = fp;
                                _stateStore.SaveKnownHosts(_knownHosts);
                            }
                        }
                    }
                };
                client.Connect();
                return client;
            }
            catch
            {
                // Connect() бросил (аутентификация/таймаут/сеть/несовпадение ключа) - освобождаем
                // клиент, иначе висит сокет.
                try { client.Dispose(); } catch { }
                if (mismatch)
                    throw new InvalidOperationException(
                        "Host-ключ узла " + node.Host + " не совпадает с ранее сохранённым - "
                      + "возможна подмена сервера (MITM) либо сервер был переустановлен. "
                      + "Если переустановка ожидаема, удалите запись для этого узла в known_hosts.json "
                      + "в папке данных приложения и подключитесь заново.");
                throw;
            }
        }

    }
}
