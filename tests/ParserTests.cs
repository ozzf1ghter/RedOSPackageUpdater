using System;
using System.Net;
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
        return _failed == 0 ? 0 : 1;
    }
}
