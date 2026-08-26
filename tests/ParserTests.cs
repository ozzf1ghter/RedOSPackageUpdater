using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using RedOSPackageUpdater;

internal static class ParserTests
{
    private static int _failed;

    private static void Check(bool condition, string name)
    {
        if (condition) Console.WriteLine("OK   " + name);
        else { Console.WriteLine("FAIL " + name); _failed++; }
    }

    public static int Main()
    {
        string output =
            "VULN|BDU:2026-1|kernel|1.0|1.1|HIGH|Описание|с разделителем\n" +
            "VULN_URL|BDU:2026-1|kernel|https://bdu.fstec.ru/vul/2026-1\n" +
            "VULN_DATE|BDU:2026-1|kernel|2026-05-27 10:15:00 +0000 UTC|2026-06-22 12:00:00 +0000 UTC\n" +
            "VULN_ALIAS|BDU:2026-1|kernel|CVE-2026-1\n" +
            "VULN_ALIAS|bdu:2026-1|KERNEL|cve-2026-1\n" +
            "VULN_REF|BDU:2026-1|kernel|https://example.test/CVE-2026-1\n" +
            "VULN_SUMMARY|1|1|0|1\n" +
            "PKGOP_ERR|первый пакет не найден\n" +
            "PKGOP_ERR|второй пакет не найден\n" +
            "PKGOP_RESULT: OK\nREBOOT_RECOMMENDED: no\n";
        var host = new HostResult();
        PkgOpParseResult parsed = PkgOpOutputParser.Parse(output, host);
        Check(parsed.Result == "OK", "результат операции");
        Check(parsed.RebootRecommended == "no", "маркер reboot");
        Check(parsed.VulnerabilityTotal == 1 && parsed.VulnerabilityBdu == 1 && parsed.VulnerabilityHigh == 1, "сводка уязвимостей");
        Check(host.Vulnerabilities.Count == 1, "строка уязвимости");
        Check(host.Vulnerabilities[0].Title == "Описание|с разделителем", "разделитель в описании");
        Check(host.Vulnerabilities[0].Aliases.Count == 1, "регистронезависимое удаление дублей");
        Check(host.Vulnerabilities[0].PublishedDate == "2026-05-27" && host.Vulnerabilities[0].LastModifiedDate == "2026-06-22", "даты карточки уязвимости");
        Check(parsed.Errors.Contains("первый") && parsed.Errors.Contains("второй"), "накопление всех ошибок");

        var partial = new HostResult();
        PkgOpResultPolicy.Apply(partial, new PkgOpParseResult { Result = "OK", RebootRecommended = "yes", Changed = 2, Errors = "missing" }, "install", false);
        Check(partial.Status == HostStatus.Warn && partial.Note.Contains("изменено пакетов: 2") && partial.Note.Contains("перезагруз"), "частичный пакетный успех не маскируется");
        var noneInstalled = new HostResult();
        PkgOpResultPolicy.Apply(noneInstalled, new PkgOpParseResult { Result = "FAIL", Changed = 0, Errors = "missing" }, "install", false);
        Check(noneInstalled.Status == HostStatus.Fail, "полностью невыполненная пакетная операция не считается предупреждением");
        var noMarker = new HostResult();
        PkgOpResultPolicy.Apply(noMarker, new PkgOpParseResult(), "install", false);
        Check(noMarker.Status == HostStatus.Fail && noMarker.UpdateResult == "NO_MARKER", "отсутствие контрактного маркера считается ошибкой");
        var vulnOk = new HostResult();
        PkgOpResultPolicy.Apply(vulnOk, new PkgOpParseResult { Result = "OK", VulnerabilityTotal = 3, VulnerabilityHigh = 2 }, "vuln", true);
        Check(vulnOk.Status == HostStatus.Warn && vulnOk.UpdateResult == "кандидатов: 3", "найденные advisory дают предупреждение, а не сбой");

        var missing = new VulnerabilityFinding { Id = "BDU:2026-07378", Severity = "UNKNOWN" };
        Check(BduFindingEnricher.Enrich(missing), "дозаполнение неполной карточки БДУ");
        Check(missing.Severity == "HIGH" && !string.IsNullOrWhiteSpace(missing.Title), "критичность и описание БДУ");
        Check(missing.PrimaryUrl == "https://bdu.fstec.ru/vul/2026-07378" && missing.References.Count >= 2, "ссылки карточки БДУ");

        var complete = new VulnerabilityFinding { Id = "BDU:2026-06252", Severity = "MEDIUM", Title = "Более свежие данные" };
        BduFindingEnricher.Enrich(complete);
        Check(complete.Severity == "MEDIUM" && complete.Title == "Более свежие данные", "существующие данные не перезаписываются");

        Check(ShellText.InSingleQuotes("a'b") == "a'\"'\"'b", "экранирование bash без потери апострофа");
        string packageList, packageError;
        Check(OperationDomain.TryNormalizePackageList("kernel-lt nginx-1:1.26.0-2.el8 @server postgresql*", out packageList, out packageError) &&
            packageList.Contains("kernel-lt") && packageError == null, "допустимые имена и NEVRA пакетов принимаются");
        Check(!OperationDomain.TryNormalizePackageList("--setopt=pluginpath=/tmp evil/package", out packageList, out packageError) &&
            packageError.Contains("Недопустимое"), "параметры DNF и пути нельзя передать через поле пакетов");
        List<string> serviceMasks;
        string serviceError;
        Check(OperationDomain.TryNormalizeServiceMasks(new[] { "postgresql*", "patroni.service", "PATRONI.service" }, out serviceMasks, out serviceError) &&
            serviceMasks.Count == 2, "маски systemd-служб очищаются и дедуплицируются");
        Check(!OperationDomain.TryNormalizeServiceMasks(new[] { "--state=failed" }, out serviceMasks, out serviceError),
            "параметры systemctl нельзя сохранить как маску службы");
        Check(WebRequests.IsTransient(new WebException("timeout", WebExceptionStatus.Timeout)), "повтор запроса после таймаута");
        Check(!WebRequests.IsTransient(new WebException("tls", WebExceptionStatus.TrustFailure)), "TLS-ошибка не маскируется повторами");

        string matched;
        Check(FstecLinuxCatalog.Applies("6.1.175", new[] { "до 6.1.180" }, out matched), "диапазон общего продукта Linux");
        Check(!FstecLinuxCatalog.Applies("5.15.10", new[] { "до 6.1.180" }, out matched), "ветки ядра не смешиваются");
        Check(FstecLinuxCatalog.RedOsVersion("RED OS MUROM (7.3) (6.1.175-1.el7.3.x86_64)") == "7.3", "версия RED OS извлекается из сведений узла");
        Check(FstecLinuxCatalog.RedOsVersion("RED OS 8.0 (6.12.92-1.red80.x86_64)") == "8.0", "версия RED OS 8 извлекается из сведений узла");
        Check(FstecLinuxCatalog.AppliesToRedOs("7.3", new[] { "7.3", "8.0" }), "карточка БДУ применима к RED OS 7.3");
        Check(FstecLinuxCatalog.AppliesToRedOs("7.3", new[] { "7.3 МУРОМ" }), "вариант названия RED OS 7.3 распознаётся");
        Check(!FstecLinuxCatalog.AppliesToRedOs("7.3", new[] { "7.1 МУРОМ", "7.2 Муром" }), "старые выпуски RED OS не смешиваются с 7.3");
        Check(!FstecLinuxCatalog.AppliesToRedOs("7.3", new[] { "8.0" }), "карточка только для RED OS 8.0 не применяется к 7.3");
        Check(!FstecLinuxCatalog.AppliesToRedOs("7.3", new string[0]), "отсутствие RED OS в карточке не считается применимостью");
        Check(!FstecLinuxCatalog.ShouldIncludeRecord(new LinuxBduRecord { Id = "BDU:2026-1" }) &&
            FstecLinuxCatalog.ShouldIncludeRecord(new LinuxBduRecord { Id = "BDU:2026-2", Versions = new List<string> { "до 6.1.1" } }) &&
            FstecLinuxCatalog.ShouldIncludeRecord(new LinuxBduRecord { Id = "BDU:2026-3", RedOsVersions = new List<string> { "8.0" } }),
            "компактный каталог содержит только карточки Linux и RED OS");
        Check(FstecLinuxCatalog.IsKernelPackage("kernel-lt") && FstecLinuxCatalog.IsKernelPackage("kernel-core") &&
            !FstecLinuxCatalog.IsKernelPackage("kernelshark"), "пакеты ядра распознаются без ложных совпадений");
        Check(FstecLinuxCatalog.IsActiveKernelVersion("6.1.175-1.el7.3", "6.1.175") &&
            !FstecLinuxCatalog.IsActiveKernelVersion("6.1.162-1.el7.3", "6.1.175"), "резервные ядра отделяются от работающего");
        Check(FstecLinuxCatalog.IsActiveKernelVersion("6.12.92-1.red80", "6.12.92") &&
            !FstecLinuxCatalog.IsActiveKernelVersion("6.12.21-1.red80", "6.12.92"), "активное ядро RED OS 8 отделяется от резервного");

        var brokenConfig = new AppConfig
        {
            Settings = new AppSettings { MaxParallel = 999, ConnectTimeoutSec = 0 },
            Systems = new List<SubSystem> { null, new SubSystem { Nodes = new List<Node> { null, new Node { Host = " host ", Port = 70000 } } } },
            Credentials = new List<Credential> { null }
        };
        brokenConfig.ExcludePackages = new List<string> { " postgresql* ", "POSTGRESQL*", "" };
        ConfigurationRules.Normalize(brokenConfig);
        Check(brokenConfig.Version == AppConfig.CurrentSchemaVersion, "старая схема конфигурации мигрирует на текущую");
        Check(brokenConfig.Settings.MaxParallel == 100 && brokenConfig.Settings.ConnectTimeoutSec == 15, "границы настроек конфигурации");
        Check(brokenConfig.Systems.Count == 1 && brokenConfig.Systems[0].Nodes.Count == 1 && brokenConfig.Systems[0].Nodes[0].Host == "host" && brokenConfig.Systems[0].Nodes[0].Port == 22, "нормализация узлов конфигурации");
        Check(brokenConfig.ExcludePackages.Count == 1 && brokenConfig.ExcludePackages[0] == "postgresql*", "строковые списки конфигурации очищаются от пустых значений и дублей");
        Check(brokenConfig.UiTheme == "light", "неизвестная тема заменяется светлой");
        brokenConfig.UiTheme = "DARK";
        ConfigurationRules.Normalize(brokenConfig);
        Check(brokenConfig.UiTheme == "DARK", "тёмная тема принимается без учёта регистра");
        bool futureConfigRejected = false;
        try { ConfigurationRules.Normalize(new AppConfig { Version = AppConfig.CurrentSchemaVersion + 1 }); }
        catch (InvalidOperationException) { futureConfigRejected = true; }
        Check(futureConfigRejected, "конфигурация будущей схемы не перезаписывается старой программой");

        var candidates = CredentialCandidates.Build(new[] {
            new Credential { User = "root", Password = null },
            new Credential { User = " root ", Password = "p" },
            new Credential { User = "root", Password = "p" }
        }, null);
        Check(candidates.Count == 1 && candidates[0].User == "root", "невалидные и повторные учётки пропускаются");

        var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        int bodies = 0, cancelledResults = 0;
        ParallelBatch.Run(new[] { 1, 2, 3 }, 2, cancelled.Token, x => bodies++, (x, error) => { if (error is OperationCanceledException) cancelledResults++; });
        Check(bodies == 0 && cancelledResults == 3, "отмена возвращает результат для каждой цели");
        bool callbackFailureSurfaced = false;
        try { ParallelBatch.Run(new[] { 1 }, 1, CancellationToken.None, x => { throw new InvalidOperationException("body"); }, (x, error) => { throw new InvalidOperationException("callback"); }); }
        catch (AggregateException) { callbackFailureSurfaced = true; }
        Check(callbackFailureSurfaced, "сбой обработчика результата батча не маскируется");

        var reportFinding = new VulnerabilityFinding { Id = "BDU:2026-1", PrimaryUrl = "" };
        reportFinding.Aliases.Add("cve-2026-5");
        reportFinding.References.Add("https://example/CVE-2026-5/CVE-2026-6");
        Check(VulnerabilityReportService.RelatedCves(reportFinding).Count == 2, "CVE отчёта объединяются без дублей");
        reportFinding.DetectionKind = "REDOS_UNVERIFIED";
        Check(!VulnerabilityReportService.IsConfirmedBdu(reportFinding), "непроверенное совпадение БДУ не считается подтверждённым");
        reportFinding.DetectionKind = "REDOS_CONFIRMED";
        Check(VulnerabilityReportService.IsConfirmedBdu(reportFinding), "подтверждённая для RED OS БДУ попадает в отчёт ФСТЭК");
        reportFinding.DetectionKind = "INACTIVE_KERNEL";
        Check(!VulnerabilityReportService.IsConfirmedBdu(reportFinding), "уязвимость резервного ядра не считается активной");
        reportFinding.DetectionKind = "REDOS_ADVISORY";
        Check(VulnerabilityReportService.IsConfirmedBdu(reportFinding), "security advisory RED OS подтверждает связанную БДУ");
        var advisoryHost = new HostResult();
        var advisoryCve = new VulnerabilityFinding { Id = "CVE-2026-39316", Package = "cups", InstalledVersion = "1:2.4.7-3.red80", FixedVersion = "1:2.4.7-6.red80", Severity = "HIGH" };
        advisoryCve.Aliases.Add("ROS-20260825-01");
        advisoryHost.Vulnerabilities.Add(advisoryCve);
        var advisoryRecord = new LinuxBduRecord { Id = "BDU:2026-05932", Title = "Уязвимость CUPS", Severity = "HIGH", Published = "2026-04-05" };
        advisoryRecord.Cves.Add("CVE-2026-39316");
        Check(FstecLinuxCatalog.ExpandRedOsAdvisoriesForTest(advisoryHost, new[] { advisoryRecord }) == 1 &&
            advisoryHost.Vulnerabilities.Count == 2 && advisoryHost.Vulnerabilities[1].DetectionKind == "REDOS_ADVISORY" &&
            advisoryHost.Vulnerabilities[1].FixedVersion == "1:2.4.7-6.red80", "CVE из RED OS advisory связывается с БДУ и сохраняет исправление");
        Check(VulnerabilityReportService.Csv("a;\"b\"") == "\"a;\"\"b\"\"\"", "CSV корректно экранирует разделители и кавычки");

        string secret = Crypto.EncryptPortable("данные", "достаточно-длинный-пароль");
        Check(Crypto.DecryptPortable(secret, "достаточно-длинный-пароль") == "данные", "переносимое шифрование: круговой тест");
        char replacement = secret[secret.Length - 2] == 'A' ? 'B' : 'A';
        string tampered = secret.Substring(0, secret.Length - 2) + replacement + secret.Substring(secret.Length - 1);
        bool tamperRejected = false;
        try { Crypto.DecryptPortable(tampered, "достаточно-длинный-пароль"); } catch { tamperRejected = true; }
        Check(tamperRejected, "изменённый экспорт отклоняется");
        bool incompleteExportRejected = false;
        try
        {
            Store.ExportPortable(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rpu-test-" + Guid.NewGuid().ToString("N") + ".rpu"), "достаточно-длинный-пароль",
                new AppConfig { Credentials = new List<Credential> { new Credential { User = "root", Password = null, EncPassword = "foreign-dpapi" } } });
        }
        catch (InvalidOperationException) { incompleteExportRejected = true; }
        Check(incompleteExportRejected, "экспорт не создаёт резервную копию без недоступного DPAPI-пароля");
        Check(UpdatePolicy.IsAvailable(new Version("1.5.0"), new Version("1.5.0"), "bb", "aa"), "обновлённая сборка той же версии обнаруживается по SHA-256");
        Check(!UpdatePolicy.IsAvailable(new Version("1.5.0"), new Version("1.5.0"), "AA", "aa"), "та же сборка повторно не скачивается");
        Check(AppUpdater.IsSha256(new string('a', 64)) && !AppUpdater.IsSha256(new string('z', 64)), "манифест принимает только шестнадцатеричный SHA-256");
        Check(AppUpdater.BuildRawUrl("main", "update.json", "abc").EndsWith("/main/update.json?r=abc"), "URL манифеста обходит HTTP-кеш");
        Check(AppUpdater.BuildRawUrl(new string('a', 40), "RedOSPackageUpdater.exe", "def").Contains("/" + new string('a', 40) + "/RedOSPackageUpdater.exe?r=def"), "EXE скачивается из закреплённого коммита");
        Check(AppUpdater.BatchLiteral(@"C:\100%\app.exe") == @"C:\100%%\app.exe", "путь обновления безопасен для batch-переменных");
        foreach (int width in new[] { 766, 850, 929, 959, 960, 1100, 1180 })
        {
            CommandBarLayout command = UiLayoutRules.CommandBar(width, 236);
            Check(command.PreviewLeft >= 372 && command.RunLeft >= command.PreviewLeft + command.PreviewWidth + 8 &&
                  command.StopLeft >= command.RunLeft + command.RunWidth + 8,
                "панель действий не перекрывает выбор сценария при ширине " + width);
        }
        foreach (int width in new[] { 500, 766, 900, 1200 })
        {
            ServerWorkspaceLayout workspace = UiLayoutRules.ServerWorkspace(width, 8);
            Check(workspace.SplitterDistance >= workspace.LeftMinimum &&
                  width - workspace.SplitterDistance - 8 >= workspace.RightMinimum,
                "панели серверов допустимы при ширине " + width);
        }
        return _failed == 0 ? 0 : 1;
    }
}
