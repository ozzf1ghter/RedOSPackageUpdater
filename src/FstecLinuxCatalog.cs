using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
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
        public List<string> Cves { get; set; }
        public LinuxBduRecord() { Versions = new List<string>(); Cves = new List<string>(); }
    }

    // Консервативная проверка карточек БДУ, где уязвимое ПО указано как общий продукт Linux.
    // Каталог строится на управляющем ПК из официального vulxml.zip; узлам нужен только uname -r.
    internal static class FstecLinuxCatalog
    {
        private const string Url = "https://bdu.fstec.ru/files/documents/vulxml.zip";
        public static string CatalogPath { get { return Path.Combine(VulnerabilityDb.Dir, "linux-bdu.json"); } }
        public static bool Exists { get { return File.Exists(CatalogPath) && new FileInfo(CatalogPath).Length > 0; } }

        public static void UpdateOnline(Action<long, long> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(VulnerabilityDb.Dir);
            string zip = Path.Combine(VulnerabilityDb.Dir, "vulxml.zip.download");
            try
            {
                Download(zip, progress, ct);
                Build(zip, CatalogPath + ".tmp", ct);
                Replace(CatalogPath + ".tmp", CatalogPath);
            }
            finally { TryDelete(zip); TryDelete(CatalogPath + ".tmp"); }
        }

        public static void Import(string zipPath, CancellationToken ct)
        {
            Directory.CreateDirectory(VulnerabilityDb.Dir);
            Build(zipPath, CatalogPath + ".tmp", ct);
            Replace(CatalogPath + ".tmp", CatalogPath);
        }

        public static int Enrich(List<HostResult> results)
        {
            if (!Exists || results == null) return 0;
            List<LinuxBduRecord> catalog;
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                catalog = ser.Deserialize<List<LinuxBduRecord>>(File.ReadAllText(CatalogPath, Encoding.UTF8));
            }
            catch { return 0; }
            int added = 0;
            foreach (HostResult host in results)
            {
                string kernel = KernelVersion(host.OsInfo);
                if (kernel.Length == 0) continue;
                var known = new HashSet<string>((host.Vulnerabilities ?? new List<VulnerabilityFinding>()).Select(v => v.Id), StringComparer.OrdinalIgnoreCase);
                foreach (LinuxBduRecord record in catalog)
                {
                    string range;
                    if (known.Contains(record.Id) || !Applies(kernel, record.Versions, out range)) continue;
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
                        var linux = vul.Descendants("soft").Where(s => string.Equals((string)s.Element("name"), "Linux", StringComparison.OrdinalIgnoreCase)).ToList();
                        if (linux.Count == 0) continue;
                        string id = ((string)vul.Element("identifier") ?? "").Trim();
                        if (!id.StartsWith("BDU:", StringComparison.OrdinalIgnoreCase)) continue;
                        var rec = new LinuxBduRecord
                        {
                            Id = id, Title = Clean((string)vul.Element("name")),
                            Published = Date((string)vul.Element("publication_date")), Modified = Date((string)vul.Element("last_upd_date")),
                            Severity = Severity((string)vul.Element("severity"))
                        };
                        foreach (XElement soft in linux) AddUnique(rec.Versions, Clean((string)soft.Element("version")));
                        foreach (XElement alias in vul.Descendants("identifier"))
                        {
                            string value = Clean(alias.Value);
                            if (value.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase)) AddUnique(rec.Cves, value.ToUpperInvariant());
                        }
                        if (rec.Versions.Count > 0) records.Add(rec);
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
        private static bool SameBranch(string a, string b) { string[] x = a.Split('.'), y = b.Split('.'); return x.Length > 1 && y.Length > 1 && x[0] == y[0] && x[1] == y[1]; }
        private static int Compare(string a, string b)
        {
            MatchCollection x = Regex.Matches(a, "\\d+"), y = Regex.Matches(b, "\\d+");
            for (int i = 0; i < Math.Max(x.Count, y.Count); i++) { int xv = i < x.Count ? int.Parse(x[i].Value) : 0, yv = i < y.Count ? int.Parse(y[i].Value) : 0; if (xv != yv) return xv.CompareTo(yv); }
            return 0;
        }
        private static string Severity(string s) { s = (s ?? "").ToLowerInvariant(); if (s.Contains("критическ")) return "CRITICAL"; if (s.Contains("высок")) return "HIGH"; if (s.Contains("средн")) return "MEDIUM"; if (s.Contains("низк")) return "LOW"; return "UNKNOWN"; }
        private static string Date(string s) { DateTime d; return DateTime.TryParse(s, out d) ? d.ToString("yyyy-MM-dd") : ""; }
        private static string Clean(string s) { return Regex.Replace((s ?? "").Trim(), "\\s+", " "); }
        private static void AddUnique(List<string> list, string value) { if (value.Length > 0 && !list.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase))) list.Add(value); }
        private static void Replace(string source, string target) { if (File.Exists(target)) File.Delete(target); File.Move(source, target); }
        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        private static void Download(string path, Action<long, long> progress, CancellationToken ct)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(Url); request.UserAgent = BuildInfo.UserAgent; request.Timeout = 30000; request.ReadWriteTimeout = 30000;
            using (var response = (HttpWebResponse)request.GetResponse()) using (Stream input = response.GetResponseStream()) using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
            { byte[] buffer = new byte[128 * 1024]; long done = 0, total = response.ContentLength; int n; while ((n = input.Read(buffer, 0, buffer.Length)) > 0) { ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, n); done += n; if (progress != null) progress(done, total); } }
        }
    }
}
