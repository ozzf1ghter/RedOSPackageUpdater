using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RedOSPackageUpdater
{
    internal sealed class PkgOpParseResult
    {
        public string Result;
        public string RebootRecommended;
        public int Changed;
        public int VulnerabilityTotal;
        public int VulnerabilityBdu;
        public int VulnerabilityCritical;
        public int VulnerabilityHigh;
        public string Errors;
    }

    internal static class PkgOpOutputParser
    {
        public static PkgOpParseResult Parse(string output, HostResult host)
        {
            if (host == null) throw new ArgumentNullException("host");
            var parsed = new PkgOpParseResult
            {
                Result = Marker(output, "PKGOP_RESULT"),
                RebootRecommended = Marker(output, "REBOOT_RECOMMENDED")
            };
            var errors = new List<string>();
            foreach (string raw in (output ?? "").Split('\n'))
            {
                string line = raw.TrimStart();
                if (line.StartsWith("CHANGED|", StringComparison.Ordinal)) parsed.Changed++;
                else if (line.StartsWith("VULN|", StringComparison.Ordinal)) AddFinding(line, host, parsed);
                else if (line.StartsWith("VULN_SUMMARY|", StringComparison.Ordinal)) ReadSummary(line, parsed);
                else if (line.StartsWith("VULN_DATE|", StringComparison.Ordinal)) AddDates(line, host);
                else if (line.StartsWith("VULN_URL|", StringComparison.Ordinal) || line.StartsWith("VULN_ALIAS|", StringComparison.Ordinal) || line.StartsWith("VULN_REF|", StringComparison.Ordinal)) AddMetadata(line, host);
                else if (line.StartsWith("PKGOP_ERR|", StringComparison.Ordinal))
                {
                    string[] parts = line.Split(new[] { '|' }, 2);
                    if (parts.Length == 2) AddUnique(errors, parts[1].Trim());
                }
            }
            parsed.Errors = errors.Count == 0 ? null : string.Join(", ", errors.ToArray());
            return parsed;
        }

        private static void AddFinding(string line, HostResult host, PkgOpParseResult parsed)
        {
            parsed.Changed++;
            string[] fields = line.Split(new[] { '|' }, 7);
            if (fields.Length < 6) return;
            host.Vulnerabilities.Add(new VulnerabilityFinding
            {
                Id = fields[1].Trim(), Package = fields[2].Trim(), InstalledVersion = fields[3].Trim(),
                FixedVersion = fields[4].Trim(), Severity = fields[5].Trim(),
                Title = fields.Length > 6 ? fields[6].Trim() : ""
            });
        }

        private static void ReadSummary(string line, PkgOpParseResult parsed)
        {
            string[] fields = line.Split('|');
            if (fields.Length <= 4) return;
            int.TryParse(fields[1], out parsed.VulnerabilityTotal);
            int.TryParse(fields[2], out parsed.VulnerabilityBdu);
            int.TryParse(fields[3], out parsed.VulnerabilityCritical);
            int.TryParse(fields[4], out parsed.VulnerabilityHigh);
        }

        private static void AddMetadata(string line, HostResult host)
        {
            string[] fields = line.Split(new[] { '|' }, 4);
            if (fields.Length != 4) return;
            VulnerabilityFinding finding = null;
            for (int i = host.Vulnerabilities.Count - 1; i >= 0; i--)
                if (string.Equals(host.Vulnerabilities[i].Id, fields[1].Trim(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(host.Vulnerabilities[i].Package, fields[2].Trim(), StringComparison.OrdinalIgnoreCase))
                { finding = host.Vulnerabilities[i]; break; }
            string value = fields[3].Trim();
            if (finding == null || value.Length == 0) return;
            if (line.StartsWith("VULN_URL|", StringComparison.Ordinal)) finding.PrimaryUrl = value;
            else if (line.StartsWith("VULN_ALIAS|", StringComparison.Ordinal)) AddUnique(finding.Aliases, value);
            else AddUnique(finding.References, value);
        }

        private static void AddDates(string line, HostResult host)
        {
            string[] fields = line.Split(new[] { '|' }, 5);
            if (fields.Length < 4) return;
            VulnerabilityFinding finding = FindFinding(host, fields[1], fields[2]);
            if (finding == null) return;
            finding.PublishedDate = CleanDate(fields[3]);
            if (fields.Length > 4) finding.LastModifiedDate = CleanDate(fields[4]);
        }

        private static VulnerabilityFinding FindFinding(HostResult host, string id, string package)
        {
            for (int i = host.Vulnerabilities.Count - 1; i >= 0; i--)
                if (string.Equals(host.Vulnerabilities[i].Id, id.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(host.Vulnerabilities[i].Package, package.Trim(), StringComparison.OrdinalIgnoreCase))
                    return host.Vulnerabilities[i];
            return null;
        }

        private static string CleanDate(string value)
        {
            value = (value ?? "").Trim();
            if (value == "<nil>" || value.StartsWith("0001-01-01", StringComparison.Ordinal)) return "";
            return value.Length >= 10 && value[4] == '-' && value[7] == '-' ? value.Substring(0, 10) : value;
        }

        private static void AddUnique(List<string> values, string value)
        {
            foreach (string existing in values)
                if (string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)) return;
            values.Add(value);
        }

        private static string Marker(string output, string name)
        {
            if (string.IsNullOrEmpty(output)) return null;
            Match match = Regex.Match(output, "^" + Regex.Escape(name) + ":\\s*(.+?)\\s*$", RegexOptions.Multiline);
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
