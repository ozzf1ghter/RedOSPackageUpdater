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
            "PKGOP_RESULT: OK\nREBOOT_RECOMMENDED: no\nTRIVY_INSTALLED: yes\n";
        var host = new HostResult();
        PkgOpParseResult parsed = PkgOpOutputParser.Parse(output, host);
        Check(parsed.Result == "OK", "результат операции");
        Check(parsed.RebootRecommended == "no", "маркер reboot");
        Check(parsed.TrivyInstalled == "yes", "маркер установки Trivy");
        Check(parsed.VulnerabilityTotal == 1 && parsed.VulnerabilityBdu == 1 && parsed.VulnerabilityHigh == 1, "сводка уязвимостей");
        Check(host.Vulnerabilities.Count == 1, "строка уязвимости");
        Check(host.Vulnerabilities[0].Title == "Описание|с разделителем", "разделитель в описании");
        Check(host.Vulnerabilities[0].Aliases.Count == 1, "регистронезависимое удаление дублей");
        Check(host.Vulnerabilities[0].PublishedDate == "2026-05-27" && host.Vulnerabilities[0].LastModifiedDate == "2026-06-22", "даты карточки уязвимости");
        Check(parsed.Errors.Contains("первый") && parsed.Errors.Contains("второй"), "накопление всех ошибок");

        var missing = new VulnerabilityFinding { Id = "BDU:2026-07378", Severity = "UNKNOWN" };
        Check(BduFindingEnricher.Enrich(missing), "дозаполнение неполной карточки БДУ");
        Check(missing.Severity == "HIGH" && !string.IsNullOrWhiteSpace(missing.Title), "критичность и описание БДУ");
        Check(missing.PrimaryUrl == "https://bdu.fstec.ru/vul/2026-07378" && missing.References.Count >= 2, "ссылки карточки БДУ");

        var complete = new VulnerabilityFinding { Id = "BDU:2026-06252", Severity = "MEDIUM", Title = "Более свежие данные" };
        BduFindingEnricher.Enrich(complete);
        Check(complete.Severity == "MEDIUM" && complete.Title == "Более свежие данные", "данные Trivy не перезаписываются");

        Check(ShellText.InSingleQuotes("a'b") == "a'\"'\"'b", "экранирование bash без потери апострофа");
        Check(WebRequests.IsTransient(new WebException("timeout", WebExceptionStatus.Timeout)), "повтор запроса после таймаута");
        Check(!WebRequests.IsTransient(new WebException("tls", WebExceptionStatus.TrustFailure)), "TLS-ошибка не маскируется повторами");

        string matched;
        Check(FstecLinuxCatalog.Applies("6.1.175", new[] { "до 6.1.180" }, out matched), "диапазон общего продукта Linux");
        Check(!FstecLinuxCatalog.Applies("5.15.10", new[] { "до 6.1.180" }, out matched), "ветки ядра не смешиваются");
        Check(FstecLinuxCatalog.RedOsVersion("RED OS MUROM (7.3) (6.1.175-1.el7.3.x86_64)") == "7.3", "версия RED OS извлекается из сведений узла");
        Check(FstecLinuxCatalog.AppliesToRedOs("7.3", new[] { "7.3", "8.0" }), "карточка БДУ применима к RED OS 7.3");
        Check(FstecLinuxCatalog.AppliesToRedOs("7.3", new[] { "7.3 МУРОМ" }), "вариант названия RED OS 7.3 распознаётся");
        Check(!FstecLinuxCatalog.AppliesToRedOs("7.3", new[] { "7.1 МУРОМ", "7.2 Муром" }), "старые выпуски RED OS не смешиваются с 7.3");
        Check(!FstecLinuxCatalog.AppliesToRedOs("7.3", new[] { "8.0" }), "карточка только для RED OS 8.0 не применяется к 7.3");
        Check(!FstecLinuxCatalog.AppliesToRedOs("7.3", new string[0]), "отсутствие RED OS в карточке не считается применимостью");
        Check(FstecLinuxCatalog.IsKernelPackage("kernel-lt") && FstecLinuxCatalog.IsKernelPackage("kernel-core") &&
            !FstecLinuxCatalog.IsKernelPackage("kernelshark"), "пакеты ядра распознаются без ложных совпадений");
        Check(FstecLinuxCatalog.IsActiveKernelVersion("6.1.175-1.el7.3", "6.1.175") &&
            !FstecLinuxCatalog.IsActiveKernelVersion("6.1.162-1.el7.3", "6.1.175"), "резервные ядра отделяются от работающего");

        var brokenConfig = new AppConfig
        {
            Settings = new AppSettings { MaxParallel = 999, ConnectTimeoutSec = 0 },
            Systems = new List<SubSystem> { null, new SubSystem { Nodes = new List<Node> { null, new Node { Host = " host ", Port = 70000 } } } },
            Credentials = new List<Credential> { null }
        };
        ConfigurationRules.Normalize(brokenConfig);
        Check(brokenConfig.Settings.MaxParallel == 100 && brokenConfig.Settings.ConnectTimeoutSec == 15, "границы настроек конфигурации");
        Check(brokenConfig.Systems.Count == 1 && brokenConfig.Systems[0].Nodes.Count == 1 && brokenConfig.Systems[0].Nodes[0].Host == "host" && brokenConfig.Systems[0].Nodes[0].Port == 22, "нормализация узлов конфигурации");
        Check(brokenConfig.UiTheme == "light", "неизвестная тема заменяется светлой");
        brokenConfig.UiTheme = "DARK";
        ConfigurationRules.Normalize(brokenConfig);
        Check(brokenConfig.UiTheme == "DARK", "тёмная тема принимается без учёта регистра");

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
        Check(VulnerabilityReportService.Csv("a;\"b\"") == "\"a;\"\"b\"\"\"", "CSV корректно экранирует разделители и кавычки");

        string secret = Crypto.EncryptPortable("данные", "достаточно-длинный-пароль");
        Check(Crypto.DecryptPortable(secret, "достаточно-длинный-пароль") == "данные", "переносимое шифрование: круговой тест");
        char replacement = secret[secret.Length - 2] == 'A' ? 'B' : 'A';
        string tampered = secret.Substring(0, secret.Length - 2) + replacement + secret.Substring(secret.Length - 1);
        bool tamperRejected = false;
        try { Crypto.DecryptPortable(tampered, "достаточно-длинный-пароль"); } catch { tamperRejected = true; }
        Check(tamperRejected, "изменённый экспорт отклоняется");
        Check(UpdatePolicy.IsAvailable(new Version("1.5.0"), new Version("1.5.0"), "bb", "aa"), "обновлённая сборка той же версии обнаруживается по SHA-256");
        Check(!UpdatePolicy.IsAvailable(new Version("1.5.0"), new Version("1.5.0"), "AA", "aa"), "та же сборка повторно не скачивается");
        foreach (int width in new[] { 720, 766, 929, 930, 1100 })
        {
            CommandBarLayout command = UiLayoutRules.CommandBar(width, 158);
            Check(command.PreviewLeft >= 310 && command.RunLeft > command.PreviewLeft && command.StopLeft > command.RunLeft,
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
