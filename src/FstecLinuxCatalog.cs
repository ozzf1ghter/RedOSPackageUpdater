using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml.Linq;

namespace RedOSPackageUpdater
{
    internal sealed class LinuxBduRecord
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Severity { get; set; }
        public string Published { get; set; }
        public string Modified { get; set; }
        public List<string> Versions { get; set; }
        public List<string> RedOsVersions { get; set; }
        public List<string> Cves { get; set; }
        public LinuxBduRecord() { Versions = new List<string>(); RedOsVersions = new List<string>(); Cves = new List<string>(); }
    }

    // Консервативная проверка карточек БДУ, где уязвимое ПО указано как общий продукт Linux.
    // Каталог строится на управляющем ПК из официального vulxml.zip; узлам нужен только uname -r.
    internal static class FstecLinuxCatalog
    {
        private const string Url = "https://bdu.fstec.ru/files/documents/vulxml.zip";
        private const string FallbackUrl = "https://raw.githubusercontent.com/ozzf1ghter/RedOSPackageUpdater/main/data/linux-bdu.zip";
        public static string CatalogPath { get { return Path.Combine(VulnerabilityDb.Dir, "linux-bdu.json"); } }
        public static bool Exists { get { return File.Exists(CatalogPath) && new FileInfo(CatalogPath).Length > 0; } }

        public static void UpdateOnline(Action<long, long> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(VulnerabilityDb.Dir);
            string zip = Path.Combine(VulnerabilityDb.Dir, "vulxml.zip.download");
            try
            {
                try
                {
                    Download(Url, zip, progress, ct);
                    Build(zip, CatalogPath + ".tmp", ct);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    TryDelete(zip);
                    Download(FallbackUrl, zip, progress, ct);
                    ExtractCompactCatalog(zip, CatalogPath + ".tmp");
                }
                FileSwap.Replace(CatalogPath + ".tmp", CatalogPath);
            }
            finally { TryDelete(zip); TryDelete(CatalogPath + ".tmp"); }
        }

        private static void ExtractCompactCatalog(string zipPath, string output)
        {
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                ZipArchiveEntry entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
                if (entry == null || entry.Length < 1000) throw new InvalidDataException("В резервном архиве не найден каталог Linux");
                using (Stream input = entry.Open()) using (var target = new FileStream(output, FileMode.Create, FileAccess.Write)) input.CopyTo(target);
            }
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var records = serializer.Deserialize<List<LinuxBduRecord>>(File.ReadAllText(output, Encoding.UTF8));
            if (records == null || records.Count < 100) throw new InvalidDataException("Резервный каталог Linux пуст или повреждён");
        }

        public static void Import(string zipPath, CancellationToken ct)
        {
            Directory.CreateDirectory(VulnerabilityDb.Dir);
            Build(zipPath, CatalogPath + ".tmp", ct);
            FileSwap.Replace(CatalogPath + ".tmp", CatalogPath);
        }

        internal static void BuildForDistribution(string zipPath, string output, CancellationToken ct)
        {
            Build(zipPath, output, ct);
        }

        public static int Enrich(List<HostResult> results)
        {
            EnsureBundledCatalog();
            if (!Exists || results == null) return 0;
            List<LinuxBduRecord> catalog;
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                catalog = ser.Deserialize<List<LinuxBduRecord>>(File.ReadAllText(CatalogPath, Encoding.UTF8));
                if (!HasApplicabilitySchema(catalog) && InstallBundledCatalog())
                    catalog = ser.Deserialize<List<LinuxBduRecord>>(File.ReadAllText(CatalogPath, Encoding.UTF8));
            }
            catch { return 0; }
            var byId = catalog.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Id))
                .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (HostResult host in results)
            {
                if (host == null) continue;
                if (host.Vulnerabilities == null) host.Vulnerabilities = new List<VulnerabilityFinding>();
                ClassifyPackageFindings(host, byId);
                string kernel = KernelVersion(host.OsInfo);
                if (kernel.Length == 0) continue;
                var known = new HashSet<string>(host.Vulnerabilities.Where(v => v != null && !string.IsNullOrWhiteSpace(v.Id)).Select(v => v.Id), StringComparer.OrdinalIgnoreCase);
                foreach (LinuxBduRecord record in catalog)
                {
                    string range;
                    if (record == null || string.IsNullOrWhiteSpace(record.Id) || known.Contains(record.Id) ||
                        record.Versions == null || !Applies(kernel, record.Versions, out range)) continue;
                    var finding = new VulnerabilityFinding
                    {
                        Id = record.Id, Package = "Linux (работающее ядро)", InstalledVersion = kernel,
                        FixedVersion = "", Severity = record.Severity, Title = record.Title,
                        PrimaryUrl = "https://bdu.fstec.ru/vul/" + record.Id.Substring(4),
                        PublishedDate = record.Published, LastModifiedDate = record.Modified,
                        DetectionKind = "LINUX_GENERAL", AffectedRange = range
                    };
                    finding.Aliases.AddRange(record.Cves ?? new List<string>());
                    host.Vulnerabilities.Add(finding); known.Add(record.Id); added++;
                }
            }
            return added;
        }

        private static bool HasApplicabilitySchema(List<LinuxBduRecord> records)
        {
            LinuxBduRecord sentinel = (records ?? new List<LinuxBduRecord>()).FirstOrDefault(r =>
                r != null && string.Equals(r.Id, "BDU:2026-05932", StringComparison.OrdinalIgnoreCase));
            return sentinel != null && (sentinel.RedOsVersions ?? new List<string>()).Any(v => SameOsVersion("7.3", v));
        }

        private static void EnsureBundledCatalog()
        {
            if (!Exists) InstallBundledCatalog();
        }

        private static bool InstallBundledCatalog()
        {
            try
            {
                using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("linux-bdu.zip"))
                {
                    if (resource == null) return false;
                    Directory.CreateDirectory(VulnerabilityDb.Dir);
                    string zip = CatalogPath + ".bundled.zip";
                    using (var output = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None)) resource.CopyTo(output);
                    try { ExtractCompactCatalog(zip, CatalogPath + ".tmp"); FileSwap.Replace(CatalogPath + ".tmp", CatalogPath); }
                    finally { TryDelete(zip); TryDelete(CatalogPath + ".tmp"); }
                    return true;
                }
            }
            catch { return false; }
        }

        private static void ClassifyPackageFindings(HostResult host, IDictionary<string, LinuxBduRecord> byId)
        {
            string hostVersion = RedOsVersion(host.OsInfo);
            string activeKernel = KernelVersion(host.OsInfo);
            foreach (VulnerabilityFinding finding in host.Vulnerabilities)
            {
                if (finding == null || !(finding.Id ?? "").StartsWith("BDU:", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(finding.DetectionKind, "LINUX_GENERAL", StringComparison.OrdinalIgnoreCase)) continue;
                // RPM keeps several kernel versions intentionally. Trivy rootfs sees
                // every installed kernel RPM, but only uname -r identifies the code
                // that is actually running. Old fallback kernels must remain in the
                // full diagnostic report, not in confirmed active vulnerabilities.
                if (IsKernelPackage(finding.Package) && !IsActiveKernelVersion(finding.InstalledVersion, activeKernel))
                {
                    finding.DetectionKind = "INACTIVE_KERNEL";
                    finding.AffectedRange = string.IsNullOrEmpty(activeKernel)
                        ? "Не удалось определить работающее ядро"
                        : "Установлено как резервное; работающее ядро " + activeKernel;
                    continue;
                }
                LinuxBduRecord record;
                if (!byId.TryGetValue(finding.Id, out record))
                {
                    finding.DetectionKind = "REDOS_UNVERIFIED";
                    finding.AffectedRange = "Нет данных о платформах в локальном каталоге ФСТЭК";
                    continue;
                }
                List<string> versions = record.RedOsVersions ?? new List<string>();
                string matched = versions.FirstOrDefault(v => SameOsVersion(hostVersion, v));
                if (versions.Count == 0)
                {
                    finding.DetectionKind = "REDOS_NOT_APPLICABLE";
                    finding.AffectedRange = "RED OS не указана в карточке БДУ";
                }
                else if (string.IsNullOrEmpty(hostVersion))
                {
                    finding.DetectionKind = "REDOS_UNVERIFIED";
                    finding.AffectedRange = "Не удалось определить версию RED OS узла; в карточке указана RED OS " + string.Join(", ", versions.ToArray());
                }
                else if (matched != null)
                {
                    finding.DetectionKind = "REDOS_CONFIRMED";
                    finding.AffectedRange = "RED OS " + matched;
                }
                else
                {
                    finding.DetectionKind = "REDOS_NOT_APPLICABLE";
                    finding.AffectedRange = "В карточке БДУ указана только RED OS " + string.Join(", ", versions.ToArray());
                }
            }
        }

        private static void Build(string zipPath, string output, CancellationToken ct)
        {
            var records = new List<LinuxBduRecord>();
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                ZipArchiveEntry entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
                if (entry == null) throw new InvalidDataException("В выгрузке ФСТЭК не найден XML-файл");
                using (Stream stream = entry.Open())
                using (var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings { IgnoreComments = true, DtdProcessing = System.Xml.DtdProcessing.Prohibit }))
                {
                    while (reader.Read())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (reader.NodeType != System.Xml.XmlNodeType.Element || reader.Name != "vul") continue;
                        XElement vul;
                        using (var sub = reader.ReadSubtree()) vul = XElement.Load(sub);
                        var linux = vul.Descendants("soft").Where(s => string.Equals(Clean((string)s.Element("name")), "Linux", StringComparison.OrdinalIgnoreCase)).ToList();
                        var redOs = vul.Descendants("os").Where(s => IsRedOs(Clean((string)s.Element("name")), Clean((string)s.Element("vendor")))).ToList();
                        string id = ((string)vul.Element("identifier") ?? "").Trim();
                        if (!id.StartsWith("BDU:", StringComparison.OrdinalIgnoreCase)) continue;
                        var rec = new LinuxBduRecord
                        {
                            Id = id, Title = Clean((string)vul.Element("name")),
                            Published = Date((string)vul.Element("publication_date")), Modified = Date((string)vul.Element("last_upd_date")),
                            Severity = Severity((string)vul.Element("severity"))
                        };
                        foreach (XElement soft in linux) AddUnique(rec.Versions, Clean((string)soft.Element("version")));
                        foreach (XElement os in redOs) AddUnique(rec.RedOsVersions, Clean((string)os.Element("version")));
                        foreach (XElement alias in vul.Descendants("identifier"))
                        {
                            string value = Clean(alias.Value);
                            if (value.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase)) AddUnique(rec.Cves, value.ToUpperInvariant());
                        }
                        if (rec.Versions.Count > 0 || rec.RedOsVersions.Count > 0 || id.StartsWith("BDU:", StringComparison.OrdinalIgnoreCase)) records.Add(rec);
                    }
                }
            }
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            File.WriteAllText(output, serializer.Serialize(records), new UTF8Encoding(false));
        }

        // Принимаем только однозначные диапазоны. Свободный текст, который нельзя надёжно
        // интерпретировать, намеренно не превращаем в ложную подтверждённую находку.
        internal static bool Applies(string current, IEnumerable<string> ranges, out string matched)
        {
            matched = "";
            foreach (string raw in ranges ?? new string[0])
            {
                MatchCollection versions = Regex.Matches(raw ?? "", "\\d+(?:\\.\\d+){1,3}(?:-rc\\d+)?", RegexOptions.IgnoreCase);
                if (versions.Count == 0) continue;
                string min = "", max = "";
                if ((raw.IndexOf("от ", StringComparison.OrdinalIgnoreCase) >= 0) && versions.Count >= 2) { min = versions[0].Value; max = versions[1].Value; }
                else if (raw.IndexOf("до ", StringComparison.OrdinalIgnoreCase) >= 0 || raw.IndexOf("ниже", StringComparison.OrdinalIgnoreCase) >= 0) max = versions[versions.Count - 1].Value;
                else if (raw.IndexOf("и выше", StringComparison.OrdinalIgnoreCase) >= 0) min = versions[0].Value;
                else continue;
                string boundary = max.Length > 0 ? max : min;
                if (!SameBranch(current, boundary)) continue;
                if ((min.Length == 0 || Compare(current, min) >= 0) && (max.Length == 0 || Compare(current, max) <= 0)) { matched = raw; return true; }
            }
            return false;
        }

        private static string KernelVersion(string osInfo)
        {
            Match m = Regex.Match(osInfo ?? "", "\\((\\d+\\.\\d+\\.\\d+(?:-rc\\d+)?)(?:-[^()]*)?\\)\\s*$");
            return m.Success ? m.Groups[1].Value : "";
        }
        internal static string RedOsVersion(string osInfo)
        {
            Match m = Regex.Match(osInfo ?? "", "(?:RED\\s*OS|РЕД\\s*ОС)[^0-9]*(\\d+(?:\\.\\d+)*)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "";
        }
        private static bool SameOsVersion(string host, string card)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(card)) return false;
            Match m = Regex.Match(card, "\\d+(?:\\.\\d+)*");
            return m.Success && string.Equals(host, m.Value, StringComparison.OrdinalIgnoreCase);
        }
        internal static bool AppliesToRedOs(string hostVersion, IEnumerable<string> cardVersions)
        {
            return (cardVersions ?? new string[0]).Any(v => SameOsVersion(hostVersion, v));
        }
        internal static bool IsKernelPackage(string package)
        {
            return Regex.IsMatch(package ?? "", "^kernel(?:$|[-_])", RegexOptions.IgnoreCase);
        }
        internal static bool IsActiveKernelVersion(string installed, string active)
        {
            Match installedVersion = Regex.Match(installed ?? "", "\\d+\\.\\d+\\.\\d+(?:-rc\\d+)?", RegexOptions.IgnoreCase);
            Match activeVersion = Regex.Match(active ?? "", "\\d+\\.\\d+\\.\\d+(?:-rc\\d+)?", RegexOptions.IgnoreCase);
            return installedVersion.Success && activeVersion.Success &&
                string.Equals(installedVersion.Value, activeVersion.Value, StringComparison.OrdinalIgnoreCase);
        }
        private static bool IsRedOs(string name, string vendor)
        {
            return Regex.IsMatch(name ?? "", "^(?:RED\\s*OS|РЕД\\s*ОС)$", RegexOptions.IgnoreCase) ||
                (name ?? "").IndexOf("РЕД ОС", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (vendor ?? "").IndexOf("Ред Софт", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        private static bool SameBranch(string a, string b) { string[] x = a.Split('.'), y = b.Split('.'); return x.Length > 1 && y.Length > 1 && x[0] == y[0] && x[1] == y[1]; }
        private static int Compare(string a, string b)
        {
            MatchCollection x = Regex.Matches(a, "\\d+"), y = Regex.Matches(b, "\\d+");
            for (int i = 0; i < Math.Max(x.Count, y.Count); i++)
            {
                long xv = 0, yv = 0;
                if (i < x.Count) long.TryParse(x[i].Value, out xv);
                if (i < y.Count) long.TryParse(y[i].Value, out yv);
                if (xv != yv) return xv.CompareTo(yv);
            }
            return 0;
        }
        private static string Severity(string s) { s = (s ?? "").ToLowerInvariant(); if (s.Contains("критическ")) return "CRITICAL"; if (s.Contains("высок")) return "HIGH"; if (s.Contains("средн")) return "MEDIUM"; if (s.Contains("низк")) return "LOW"; return "UNKNOWN"; }
        private static string Date(string s) { DateTime d; return DateTime.TryParse(s, out d) ? d.ToString("yyyy-MM-dd") : ""; }
        private static string Clean(string s) { return Regex.Replace((s ?? "").Trim(), "\\s+", " "); }
        private static void AddUnique(List<string> list, string value) { if (value.Length > 0 && !list.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase))) list.Add(value); }
        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        private static void Download(string url, string path, Action<long, long> progress, CancellationToken ct)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            WebRequests.Retry(() =>
            {
                ct.ThrowIfCancellationRequested();
                var request = WebRequests.Create(url, 30000);
                using (var response = (HttpWebResponse)request.GetResponse())
                using (Stream input = response.GetResponseStream())
                using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[128 * 1024];
                    long done = 0, total = response.ContentLength;
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, n); done += n;
                        if (progress != null) progress(done, total);
                    }
                }
                return true;
            }, 3);
        }
    }
}
