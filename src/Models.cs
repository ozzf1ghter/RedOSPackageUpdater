using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace RedOSPackageUpdater
{
    // Учётка из пула. В конфиге хранится EncPassword (DPAPI). Password - расшифрованный, только в памяти.
    public class Credential
    {
        public string User { get; set; }
        public string EncPassword { get; set; }     // DPAPI base64 (пишется в конфиг)

        [ScriptIgnore] public string Password { get; set; }   // plain, только в памяти

        public Credential() { User = "root"; }
    }

    public class Node
    {
        public string Name { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string Role { get; set; }
        public bool Enabled { get; set; }

        public Node() { Port = 22; Enabled = true; Role = ""; }

        [ScriptIgnore] public string Display { get { return string.IsNullOrEmpty(Name) ? Host : (Name + "  (" + Host + ")"); } }
    }

    public class SubSystem
    {
        public string Name { get; set; }
        public List<string> Services { get; set; }   // маски сервисов для prestop
        public List<Node> Nodes { get; set; }

        public SubSystem()
        {
            Services = new List<string>();
            Nodes = new List<Node>();
        }
    }

    public class AppSettings
    {
        public int MaxParallel { get; set; }
        public int ConnectTimeoutSec { get; set; }
        public int InitialRebootDelaySec { get; set; }
        public int DownWaitSec { get; set; }
        public int UpTimeoutSec { get; set; }
        public int StopServiceTimeoutSec { get; set; }
        public int AuthRetryDelayMs { get; set; }    // пауза между попытками паролей (против блокировок)
        public int MaxAuthAttempts { get; set; }     // 0 = пробовать все из пула
        public int BackupKeep { get; set; }          // сколько бэкапов /root/rpu-backup-* хранить на хосте
        public int UpdateTimeoutSec { get; set; }    // таймаут выполнения обновления на узле (yum)

        public AppSettings()
        {
            MaxParallel = 5;
            ConnectTimeoutSec = 15;
            InitialRebootDelaySec = 15;
            DownWaitSec = 180;
            UpTimeoutSec = 900;
            StopServiceTimeoutSec = 120;
            AuthRetryDelayMs = 800;
            MaxAuthAttempts = 0;
            BackupKeep = 5;
            UpdateTimeoutSec = 1800;
        }
    }

    public class AppConfig
    {
        public int Version { get; set; }
        public AppSettings Settings { get; set; }
        public List<Credential> Credentials { get; set; }
        public List<SubSystem> Systems { get; set; }
        public List<string> ExcludePackages { get; set; }   // маски пакетов, исключаемых из обновления
        public string RepoHost { get; set; }                 // хост зеркала репозиториев
        public List<string> RepoScripts { get; set; }        // полные пути reposync-скриптов (запускаются по очереди)

        // Дефолты вынесены сюда, а не продублированы в конструкторе и в Store.Normalize -
        // раньше это были два независимых места, и правка списка исключений в одном легко
        // забывалась во втором (конструктор используется для нового конфига, Normalize - для
        // "заполнить дыры" после загрузки/восстановления битого JSON).
        public const string DefaultRepoHost = "192.168.35.224";
        public static List<string> DefaultExcludePackages()
        {
            return new List<string> { "postgresql*", "java-11-openjdk*", "jre-11-openjdk*", "docker-ce*" };
        }
        public static List<string> DefaultRepoScripts()
        {
            return new List<string> { "/root/redos-reposync.sh" };
        }

        public AppConfig()
        {
            Version = 1;
            Settings = new AppSettings();
            Credentials = new List<Credential>();
            Systems = new List<SubSystem>();
            ExcludePackages = DefaultExcludePackages();
            RepoHost = DefaultRepoHost;
            RepoScripts = DefaultRepoScripts();
        }
    }

    // Запись кеша подобранной учётки на узел (файл creds_cache.dat, DPAPI поверх всего файла).
    public class CachedCred
    {
        public string Key { get; set; }        // host:port
        public string User { get; set; }
        public string Password { get; set; }   // plain внутри уже DPAPI-зашифрованного файла
    }

    // Строка предпроверки: пакет и переход версии.
    // Kind: sec (по advisory) | dep (зависимость) | kern (ядро) | excl (исключён маской, будет пропущен).
    public class PkgUpdate
    {
        public string Name { get; set; }
        public string Old { get; set; }
        public string New { get; set; }
        public string Repo { get; set; }
        public string Kind { get; set; }
        public string Reason { get; set; }   // для dep: кто тянет пакет
        public bool Security { get; set; }   // Kind == "sec"
        public bool Excluded { get; set; }   // Kind == "excl"
    }

    // Результат предпроверки по одному узлу.
    public class HostPreview
    {
        public string System { get; set; }
        public string Name { get; set; }
        public string Host { get; set; }
        public string UsedUser { get; set; }
        public string OsInfo { get; set; }   // "PRETTY_NAME|ядро|версия dnf" из /etc/os-release узла (маркер OS_INFO)
        public string Error { get; set; }
        public int Total { get; set; }
        public int Sec { get; set; }
        public int Dep { get; set; }
        public int Excluded { get; set; }
        public List<PkgUpdate> Packages { get; set; }

        public HostPreview() { Packages = new List<PkgUpdate>(); }
    }

    public enum HostStatus { Pending, Running, Ok, Warn, Fail }

    // Результат обработки одного узла (для сводки/грида).
    public class HostResult
    {
        public string System { get; set; }
        public string Name { get; set; }
        public string Host { get; set; }
        public HostStatus Status { get; set; }
        public string UsedUser { get; set; }
        public string OsInfo { get; set; }   // "PRETTY_NAME|ядро|версия dnf" из /etc/os-release узла
        public string UpdateResult { get; set; }
        public string RebootRequired { get; set; }
        public string RebootAction { get; set; }
        public string PreStop { get; set; }
        public string PostCheck { get; set; }
        public string ExpectedKernel { get; set; }
        public string RunningKernel { get; set; }
        public string Note { get; set; }
        public string LogFile { get; set; }
        public List<VulnerabilityFinding> Vulnerabilities { get; set; }

        public HostResult()
        {
            Status = HostStatus.Pending;
            UpdateResult = ""; RebootRequired = ""; RebootAction = "-";
            PreStop = "-"; PostCheck = "-"; ExpectedKernel = ""; RunningKernel = "";
            Note = ""; UsedUser = ""; LogFile = ""; OsInfo = "";
            Vulnerabilities = new List<VulnerabilityFinding>();
        }
    }

    // Структурированная строка отчёта Trivy. Храним её отдельно от текстового лога,
    // чтобы формировать общий отчёт по всем узлам без повторного разбора файлов.
    public class VulnerabilityFinding
    {
        public string Id { get; set; }
        public string Package { get; set; }
        public string InstalledVersion { get; set; }
        public string FixedVersion { get; set; }
        public string Severity { get; set; }
        public string Title { get; set; }
        public string PrimaryUrl { get; set; }
        public string PublishedDate { get; set; }
        public string LastModifiedDate { get; set; }
        public List<string> Aliases { get; set; }
        public List<string> References { get; set; }

        public VulnerabilityFinding()
        {
            Aliases = new List<string>();
            References = new List<string>();
        }
    }
}
