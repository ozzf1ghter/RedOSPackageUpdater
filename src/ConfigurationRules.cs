using System;
using System.Collections.Generic;

namespace RedOSPackageUpdater
{
    internal static class ConfigurationRules
    {
        public static void Normalize(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (config.Settings == null) config.Settings = new AppSettings();
            var defaults = new AppSettings();
            config.Settings.MaxParallel = NormalizeRange(config.Settings.MaxParallel, 1, 100, defaults.MaxParallel);
            config.Settings.ConnectTimeoutSec = NormalizeRange(config.Settings.ConnectTimeoutSec, 1, 300, defaults.ConnectTimeoutSec);
            config.Settings.InitialRebootDelaySec = NormalizeRange(config.Settings.InitialRebootDelaySec, 0, 3600, defaults.InitialRebootDelaySec);
            config.Settings.DownWaitSec = NormalizeRange(config.Settings.DownWaitSec, 0, 86400, defaults.DownWaitSec);
            config.Settings.UpTimeoutSec = NormalizeRange(config.Settings.UpTimeoutSec, 30, 86400, defaults.UpTimeoutSec);
            config.Settings.StopServiceTimeoutSec = NormalizeRange(config.Settings.StopServiceTimeoutSec, 5, 3600, defaults.StopServiceTimeoutSec);
            config.Settings.AuthRetryDelayMs = NormalizeRange(config.Settings.AuthRetryDelayMs, 0, 60000, defaults.AuthRetryDelayMs);
            config.Settings.MaxAuthAttempts = NormalizeRange(config.Settings.MaxAuthAttempts, 0, 1000, 0);
            config.Settings.BackupKeep = NormalizeRange(config.Settings.BackupKeep, 0, 100, defaults.BackupKeep);
            config.Settings.UpdateTimeoutSec = NormalizeRange(config.Settings.UpdateTimeoutSec, 60, 86400, defaults.UpdateTimeoutSec);

            if (config.Credentials == null) config.Credentials = new List<Credential>();
            if (config.Systems == null) config.Systems = new List<SubSystem>();
            if (config.ExcludePackages == null) config.ExcludePackages = AppConfig.DefaultExcludePackages();
            if (string.IsNullOrWhiteSpace(config.RepoHost)) config.RepoHost = AppConfig.DefaultRepoHost;
            else config.RepoHost = config.RepoHost.Trim();
            if (config.RepoScripts == null || config.RepoScripts.Count == 0) config.RepoScripts = AppConfig.DefaultRepoScripts();

            config.Credentials.RemoveAll(c => c == null);
            config.Systems.RemoveAll(s => s == null);
            foreach (SubSystem system in config.Systems)
            {
                system.Name = (system.Name ?? "").Trim();
                if (system.Services == null) system.Services = new List<string>();
                if (system.Nodes == null) system.Nodes = new List<Node>();
                system.Nodes.RemoveAll(n => n == null);
                foreach (Node node in system.Nodes)
                {
                    node.Name = (node.Name ?? "").Trim();
                    node.Host = (node.Host ?? "").Trim();
                    node.Role = node.Role ?? "";
                    if (node.Port < 1 || node.Port > 65535) node.Port = 22;
                }
            }
        }

        private static int NormalizeRange(int value, int min, int max, int fallback)
        {
            if (value < min) return fallback;
            return value > max ? max : value;
        }
    }
}
