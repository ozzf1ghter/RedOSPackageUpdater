using System;
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
        Check(parsed.Errors.Contains("первый") && parsed.Errors.Contains("второй"), "накопление всех ошибок");
        return _failed == 0 ? 0 : 1;
    }
}
