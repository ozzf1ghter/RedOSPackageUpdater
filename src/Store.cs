using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace RedOSPackageUpdater
{
    // Хранилище: конфиг + кеш учёток в %LOCALAPPDATA%\RedOSPackageUpdater.
    internal static class Store
    {
        public static readonly string AppDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedOSPackageUpdater");
        public static string ConfigPath { get { return Path.Combine(AppDir, "config.json"); } }
        public static string CachePath { get { return Path.Combine(AppDir, "creds_cache.dat"); } }
        public static string LogsDir { get { return Path.Combine(AppDir, "logs"); } }
        public static string KnownHostsPath { get { return Path.Combine(AppDir, "known_hosts.json"); } }

        private static JavaScriptSerializer NewSer()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = 64 * 1024 * 1024;
            return s;
        }

        public static void EnsureDirs()
        {
            Directory.CreateDirectory(AppDir);
            Directory.CreateDirectory(LogsDir);
        }

        // ---- Конфиг ----
        public static AppConfig LoadConfig(string seedPathIfMissing)
        {
            EnsureDirs();
            AppConfig cfg = null;
            if (File.Exists(ConfigPath))
            {
                string raw = File.ReadAllText(ConfigPath, Encoding.UTF8);
                cfg = DeserializeConfig(raw);
                // Конфиг есть, но не распарсился (битый/обрезанный) - НЕ теряем молча: сохраняем копию для восстановления.
                if (cfg == null && raw.Trim().Length > 0)
                    try { File.Copy(ConfigPath, ConfigPath + ".corrupt", true); } catch { }
            }
            else if (!string.IsNullOrEmpty(seedPathIfMissing) && File.Exists(seedPathIfMissing))
                cfg = DeserializeConfig(File.ReadAllText(seedPathIfMissing, Encoding.UTF8));

            if (cfg == null) cfg = new AppConfig();
            Normalize(cfg);
            // расшифровать пароли пула из DPAPI. Password==null => расшифровать не удалось (чужой DPAPI):
            // тогда при сохранении НЕ перешифровываем, а сохраняем исходный EncPassword (иначе потеряем пароль).
            foreach (var c in cfg.Credentials)
            {
                if (c == null) continue;
                if (c.User == null) c.User = "root";
                c.Password = Crypto.UnprotectLocal(c.EncPassword);
            }
            return cfg;
        }

        // Разбор конфига из JSON (например вшитый seed). Пароли пула тут plain (seed без паролей).
        public static AppConfig FromJson(string json)
        {
            var cfg = DeserializeConfig(json);
            if (cfg == null) return null;
            Normalize(cfg);
            return cfg;
        }

        private static void Normalize(AppConfig cfg)
        {
            if (cfg.Settings == null) cfg.Settings = new AppSettings();
            // JSON иногда правят вручную. Нулевые/отрицательные таймауты и параллелизм не должны
            // превращать запуск в мгновенный отказ или бесконтрольное число потоков.
            var d = new AppSettings();
            if (cfg.Settings.MaxParallel < 1) cfg.Settings.MaxParallel = d.MaxParallel;
            if (cfg.Settings.MaxParallel > 100) cfg.Settings.MaxParallel = 100;
            if (cfg.Settings.ConnectTimeoutSec < 1) cfg.Settings.ConnectTimeoutSec = d.ConnectTimeoutSec;
            if (cfg.Settings.ConnectTimeoutSec > 300) cfg.Settings.ConnectTimeoutSec = 300;
            if (cfg.Settings.InitialRebootDelaySec < 0) cfg.Settings.InitialRebootDelaySec = d.InitialRebootDelaySec;
            if (cfg.Settings.InitialRebootDelaySec > 3600) cfg.Settings.InitialRebootDelaySec = 3600;
            if (cfg.Settings.DownWaitSec < 0) cfg.Settings.DownWaitSec = d.DownWaitSec;
            if (cfg.Settings.DownWaitSec > 86400) cfg.Settings.DownWaitSec = 86400;
            if (cfg.Settings.UpTimeoutSec < 30) cfg.Settings.UpTimeoutSec = d.UpTimeoutSec;
            if (cfg.Settings.UpTimeoutSec > 86400) cfg.Settings.UpTimeoutSec = 86400;
            if (cfg.Settings.StopServiceTimeoutSec < 5) cfg.Settings.StopServiceTimeoutSec = d.StopServiceTimeoutSec;
            if (cfg.Settings.StopServiceTimeoutSec > 3600) cfg.Settings.StopServiceTimeoutSec = 3600;
            if (cfg.Settings.AuthRetryDelayMs < 0) cfg.Settings.AuthRetryDelayMs = d.AuthRetryDelayMs;
            if (cfg.Settings.AuthRetryDelayMs > 60000) cfg.Settings.AuthRetryDelayMs = 60000;
            if (cfg.Settings.MaxAuthAttempts < 0) cfg.Settings.MaxAuthAttempts = 0;
            if (cfg.Settings.MaxAuthAttempts > 1000) cfg.Settings.MaxAuthAttempts = 1000;
            if (cfg.Settings.BackupKeep < 0) cfg.Settings.BackupKeep = d.BackupKeep;
            if (cfg.Settings.BackupKeep > 100) cfg.Settings.BackupKeep = 100;
            if (cfg.Settings.UpdateTimeoutSec < 60) cfg.Settings.UpdateTimeoutSec = d.UpdateTimeoutSec;
            if (cfg.Settings.UpdateTimeoutSec > 86400) cfg.Settings.UpdateTimeoutSec = 86400;
            if (cfg.Credentials == null) cfg.Credentials = new List<Credential>();
            if (cfg.Systems == null) cfg.Systems = new List<SubSystem>();
            if (cfg.ExcludePackages == null) cfg.ExcludePackages = AppConfig.DefaultExcludePackages();
            if (string.IsNullOrEmpty(cfg.RepoHost)) cfg.RepoHost = AppConfig.DefaultRepoHost;
            if (cfg.RepoScripts == null || cfg.RepoScripts.Count == 0) cfg.RepoScripts = AppConfig.DefaultRepoScripts();
            // null-элементы в массивах (битый/сторонний json: "Systems":[null], "Nodes":[null]) отсеиваем,
            // иначе дальше по коду ловим NRE мимо защищённой ветки "битого конфига".
            cfg.Credentials.RemoveAll(c => c == null);
            cfg.Systems.RemoveAll(s => s == null);
            foreach (var s in cfg.Systems)
            {
                if (s.Services == null) s.Services = new List<string>();
                if (s.Nodes == null) s.Nodes = new List<Node>();
                s.Nodes.RemoveAll(n => n == null);
                foreach (var n in s.Nodes) if (n.Port <= 0) n.Port = 22;
            }
        }

        private static AppConfig DeserializeConfig(string json)
        {
            try { return NewSer().Deserialize<AppConfig>(json); }
            catch { return null; }
        }

        public static void SaveConfig(AppConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException("cfg");
            Normalize(cfg);
            EnsureDirs();
            // зашифровать пароли пула в DPAPI, plain не пишем.
            // Если Password==null (не расшифровали при загрузке) - сохраняем исходный EncPassword, не затираем.
            // Защита от null-элемента: SaveConfig - публичный метод, его можно вызвать на AppConfig,
            // не прошедшем через Normalize (например, после ручной сборки объекта в другом месте кода).
            foreach (var c in cfg.Credentials)
            {
                if (c == null) continue;
                if (c.Password != null) c.EncPassword = Crypto.ProtectLocal(c.Password);
            }
            string json = PrettyJson(NewSer().Serialize(cfg));
            // Атомарная запись: пишем во временный файл и подменяем. Иначе обрыв (питание/kill/нет места)
            // на середине записи оставит битый config.json и весь пул учёток потеряется.
            string tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            ReplaceWithRetry(tmp, ConfigPath, ConfigPath + ".bak");
        }

        // File.Replace/Move иногда на ровном месте кидают IOException на Windows - антивирус или
        // индексатор на миг держат хендл только что записанного .tmp файла. Несколько попыток с
        // паузой закрывают почти все такие случаи; если конфиг реально нельзя сохранить (диск
        // недоступен и т.п.) - исключение всё равно уйдёт наверх после последней попытки.
        private static void ReplaceWithRetry(string tmp, string target, string backup)
        {
            const int attempts = 3;
            for (int i = 1; i <= attempts; i++)
            {
                try
                {
                    if (File.Exists(target)) File.Replace(tmp, target, backup);
                    else File.Move(tmp, target);
                    return;
                }
                catch (IOException)
                {
                    if (i >= attempts) throw;
                    System.Threading.Thread.Sleep(150);
                }
                catch (PlatformNotSupportedException)
                {
                    // File.Replace недоступна на файловой системе (например, сетевой диск без
                    // транзакционной замены) - откатываемся на простое удаление+перемещение.
                    try { if (File.Exists(target)) File.Delete(target); } catch { }
                    File.Move(tmp, target);
                    return;
                }
            }
        }

        // ---- Кеш подобранных учёток ----
        // Кеш читается/пишется из UI-потока (после завершения батча обновлений), поэтому конкурентный
        // доступ с фоновыми SSH-потоками здесь не ожидается - лок нужен только чтобы сериализовать
        // повторные SaveCache/LoadCache, если они всё же будут вызваны параллельно в будущем.
        private static readonly object _cacheLock = new object();

        public static Dictionary<string, CachedCred> LoadCache()
        {
            var dict = new Dictionary<string, CachedCred>(StringComparer.OrdinalIgnoreCase);
            lock (_cacheLock)
            {
                try
                {
                    if (!File.Exists(CachePath)) return dict;
                    string enc = File.ReadAllText(CachePath, Encoding.UTF8);
                    string json = Crypto.UnprotectLocal(enc);
                    if (string.IsNullOrEmpty(json)) return dict;
                    var list = NewSer().Deserialize<List<CachedCred>>(json);
                    if (list != null)
                        foreach (var c in list) if (c != null && c.Key != null) dict[c.Key] = c;
                }
                catch (Exception ex) { LogStoreError("LoadCache", ex); }
            }
            return dict;
        }

        public static void SaveCache(Dictionary<string, CachedCred> dict)
        {
            lock (_cacheLock)
            {
                try
                {
                    EnsureDirs();
                    var list = new List<CachedCred>(dict.Values);
                    string json = NewSer().Serialize(list);
                    string enc = Crypto.ProtectLocal(json);
                    string tmp = CachePath + ".tmp";
                    File.WriteAllText(tmp, enc, new UTF8Encoding(false));
                    ReplaceWithRetry(tmp, CachePath, CachePath + ".bak");
                }
                catch (Exception ex) { LogStoreError("SaveCache", ex); }
            }
        }

        public static string CacheKey(string host, int port) { return (host ?? "").Trim() + ":" + (port <= 0 ? 22 : port); }

        // ---- Known hosts (TOFU pinning SSH host-ключей) ----
        // Отпечаток host-ключа не секретен (это открытая часть ключа сервера), поэтому файл
        // не шифруется - по формату и духу это прямой аналог ~/.ssh/known_hosts, только в JSON
        // и per-host:port, а не per-host. Хранится отдельно от creds_cache.dat (тот - пароли).
        private static readonly object _knownHostsLock = new object();

        public static Dictionary<string, string> LoadKnownHosts()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            lock (_knownHostsLock) try
            {
                if (!File.Exists(KnownHostsPath)) return dict;
                string json = File.ReadAllText(KnownHostsPath, Encoding.UTF8);
                var loaded = NewSer().Deserialize<Dictionary<string, string>>(json);
                if (loaded != null)
                    foreach (var kv in loaded) if (!string.IsNullOrEmpty(kv.Key)) dict[kv.Key] = kv.Value;
            }
            catch (Exception ex) { LogStoreError("LoadKnownHosts", ex); }
            return dict;
        }

        public static void SaveKnownHosts(Dictionary<string, string> dict)
        {
            lock (_knownHostsLock) try
            {
                EnsureDirs();
                string json = NewSer().Serialize(dict);
                string tmp = KnownHostsPath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                ReplaceWithRetry(tmp, KnownHostsPath, KnownHostsPath + ".bak");
            }
            catch (Exception ex) { LogStoreError("SaveKnownHosts", ex); }
        }

        // Кеш учёток некритичен (переподбор пароля при следующем запуске просто чуть медленнее),
        // поэтому ошибку не показываем модалкой поверх основной работы - но и не проглатываем молча:
        // пишем в отдельный лог-файл, чтобы при жалобе "почему кеш не сохраняется" было что посмотреть.
        private static void LogStoreError(string where, Exception ex)
        {
            try
            {
                EnsureDirs();
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + where + "] " + ex + Environment.NewLine;
                File.AppendAllText(Path.Combine(AppDir, "store_errors.log"), line, new UTF8Encoding(false));
            }
            catch { /* логирование само по себе не должно ронять приложение */ }
        }

        // ---- Переносимый экспорт/импорт (мастер-пароль) ----
        // Отдельные DTO: в Credential пароль помечен [ScriptIgnore] и не сериализуется,
        // поэтому для экспорта пароли кладём в обычное поле CredExport.Password.
        private class CredExport { public string User { get; set; } public string Password { get; set; } }
        private class ExportBundle
        {
            public int Version { get; set; }
            public AppSettings Settings { get; set; }
            public List<string> ExcludePackages { get; set; }
            public string RepoHost { get; set; }
            public List<string> RepoScripts { get; set; }
            public List<SubSystem> Systems { get; set; }
            public List<CredExport> Credentials { get; set; }
        }

        public static void ExportPortable(string path, string master, AppConfig cfg)
        {
            // Мастер-пароль защищает весь пул паролей узлов в файле экспорта - короткий/пустой
            // пароль тривиально брутфорсится офлайн. Проверяем на уровне хранилища, а не только
            // (не факт, что вообще) на уровне UI-диалога.
            if (string.IsNullOrEmpty(master) || master.Length < 8)
                throw new ArgumentException("Мастер-пароль экспорта должен быть не короче 8 символов");
            var b = new ExportBundle
            {
                Version = cfg.Version,
                Settings = cfg.Settings,
                ExcludePackages = cfg.ExcludePackages,
                RepoHost = cfg.RepoHost,
                RepoScripts = cfg.RepoScripts,
                Systems = cfg.Systems,
                Credentials = new List<CredExport>()
            };
            foreach (var c in cfg.Credentials)
                b.Credentials.Add(new CredExport { User = c.User, Password = c.Password });
            string json = NewSer().Serialize(b);
            string blob = Crypto.EncryptPortable(json, master);
            File.WriteAllText(path, blob, new UTF8Encoding(false));
        }

        public static AppConfig ImportPortable(string path, string master)
        {
            string blob = File.ReadAllText(path, Encoding.UTF8);
            string json = Crypto.DecryptPortable(blob, master);
            var b = NewSer().Deserialize<ExportBundle>(json);
            if (b == null) throw new InvalidDataException("Не удалось разобрать импортируемые данные");
            var cfg = new AppConfig
            {
                Version = b.Version == 0 ? 1 : b.Version,
                Settings = b.Settings,
                Systems = b.Systems,
                ExcludePackages = b.ExcludePackages,
                RepoHost = b.RepoHost,
                RepoScripts = b.RepoScripts,
                Credentials = new List<Credential>()
            };
            if (b.Credentials != null)
                foreach (var ce in b.Credentials)
                    cfg.Credentials.Add(new Credential { User = string.IsNullOrEmpty(ce.User) ? "root" : ce.User, Password = ce.Password });
            Normalize(cfg);
            return cfg;
        }

        // Простенькое форматирование JSON (JavaScriptSerializer выдаёт одну строку).
        private static string PrettyJson(string json)
        {
            var sb = new StringBuilder();
            int indent = 0; bool inStr = false; bool esc = false;
            foreach (char c in json)
            {
                if (inStr)
                {
                    // корректно учитываем экранирование: \" и \\ внутри строки не закрывают её
                    sb.Append(c);
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; esc = false; sb.Append(c); continue; }
                if (c == '{' || c == '[') { sb.Append(c); sb.Append('\n'); indent++; sb.Append(new string(' ', indent * 2)); continue; }
                if (c == '}' || c == ']')
                {
                    // indent не должен уйти в минус даже на гипотетически несбалансированном вводе -
                    // это единственное место, где сбой форматирования способен сорвать сохранение
                    // всего конфига (ArgumentOutOfRangeException из new string(' ', -N)).
                    if (indent > 0) indent--;
                    sb.Append('\n'); sb.Append(new string(' ', indent * 2)); sb.Append(c);
                    continue;
                }
                if (c == ',') { sb.Append(c); sb.Append('\n'); sb.Append(new string(' ', indent * 2)); continue; }
                if (c == ':') { sb.Append(": "); continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
