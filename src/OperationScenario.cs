using System.Collections.Generic;

namespace RedOSPackageUpdater
{
    internal enum OperationScenarioKind { KernelAndSecurity, SecurityOnly, KernelOnly, PackageInstall, PackageUpdate, PackageRemove, VersionLock, VersionUnlock, VersionLockList }

    internal sealed class OperationScenario
    {
        public OperationScenarioKind Kind { get; private set; }
        public string Title { get; private set; }
        public string PackageAction { get; private set; }
        public string ProfileResource { get; private set; }
        public string ProfileKey { get; private set; }
        public bool IsPackageOperation { get { return PackageAction != null; } }
        public bool PackageFilterOptional { get { return Kind == OperationScenarioKind.VersionLockList; } }

        private OperationScenario(OperationScenarioKind kind, string title, string packageAction, string profileResource, string profileKey)
        { Kind = kind; Title = title; PackageAction = packageAction; ProfileResource = profileResource; ProfileKey = profileKey; }

        public override string ToString() { return Title; }

        public static readonly IList<OperationScenario> All = new List<OperationScenario>
        {
            new OperationScenario(OperationScenarioKind.KernelAndSecurity, "Ядро kernel-lt и обновления безопасности", null, Profiles.KernelSecurity, "kernel_security"),
            new OperationScenario(OperationScenarioKind.SecurityOnly, "Только обновления безопасности", null, Profiles.SecurityOnly, "security_only"),
            new OperationScenario(OperationScenarioKind.KernelOnly, "Только ядро kernel-lt", null, Profiles.KernelOnly, "kernel_only"),
            new OperationScenario(OperationScenarioKind.PackageInstall, "Установить пакеты", "install", null, null),
            new OperationScenario(OperationScenarioKind.PackageUpdate, "Обновить пакеты", "update", null, null),
            new OperationScenario(OperationScenarioKind.PackageRemove, "Удалить пакеты", "remove", null, null),
            new OperationScenario(OperationScenarioKind.VersionLock, "Закрепить версии пакетов", "lock", null, null),
            new OperationScenario(OperationScenarioKind.VersionUnlock, "Снять закрепление версий", "unlock", null, null),
            new OperationScenario(OperationScenarioKind.VersionLockList, "Показать закреплённые версии", "locklist", null, null)
        }.AsReadOnly();
    }
}
