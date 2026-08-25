using System;
using System.Collections.Generic;

namespace RedOSPackageUpdater
{
    /// <summary>
    /// Граница постоянного состояния SSH. Оркестратор не должен знать, в каких файлах
    /// приложение хранит кэш учёток и отпечатки серверов.
    /// </summary>
    internal interface ISshStateStore
    {
        Dictionary<string, string> LoadKnownHosts();
        void SaveKnownHosts(Dictionary<string, string> knownHosts);
        void SaveCredentialCache(Dictionary<string, CachedCred> cache);
    }

    internal sealed class FileSshStateStore : ISshStateStore
    {
        public Dictionary<string, string> LoadKnownHosts()
        {
            return Store.LoadKnownHosts();
        }

        public void SaveKnownHosts(Dictionary<string, string> knownHosts)
        {
            if (knownHosts == null) throw new ArgumentNullException("knownHosts");
            Store.SaveKnownHosts(knownHosts);
        }

        public void SaveCredentialCache(Dictionary<string, CachedCred> cache)
        {
            if (cache == null) throw new ArgumentNullException("cache");
            Store.SaveCache(cache);
        }
    }
}
