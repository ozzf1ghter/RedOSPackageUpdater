using System;
using System.Collections.Generic;

namespace RedOSPackageUpdater
{
    // Небольшой встроенный слой исправлений для карточек, которые присутствуют
    // в пакетном источнике Trivy, но отсутствуют в его справочнике описаний.
    // Значения Trivy всегда имеют приоритет: заполняем только пустые поля.
    internal static class BduFindingEnricher
    {
        private sealed class Details
        {
            public string Severity;
            public string Title;
            public string[] References;
        }

        private static readonly Dictionary<string, Details> Known =
            new Dictionary<string, Details>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "BDU:2026-06252",
                    new Details
                    {
                        Severity = "HIGH",
                        Title = "Уязвимость компонента raster-interpret.c сервера печати CUPS, связанная с недостаточной проверкой вводимых данных и позволяющая удалённому нарушителю вызвать отказ в обслуживании",
                        References = new[]
                        {
                            "https://bdu.fstec.ru/vul/2026-06252",
                            "https://github.com/OpenPrinting/cups/issues/1188",
                            "https://github.com/OpenPrinting/cups/commit/7487b879ee5440e2b8313ae17d8f400d3488222e"
                        }
                    }
                },
                {
                    "BDU:2026-07378",
                    new Details
                    {
                        Severity = "HIGH",
                        Title = "Уязвимость функции skb_gro_receive() ядра Linux, связанная с использованием памяти после освобождения и позволяющая локальному нарушителю получить root-привилегии",
                        References = new[]
                        {
                            "https://bdu.fstec.ru/vul/2026-07378",
                            "https://git.kernel.org/pub/scm/linux/kernel/git/stable/linux.git/commit/?id=4db79a322db8"
                        }
                    }
                }
            };

        public static void Enrich(IEnumerable<HostResult> results)
        {
            if (results == null) return;
            foreach (HostResult host in results)
                foreach (VulnerabilityFinding finding in host.Vulnerabilities ?? new List<VulnerabilityFinding>())
                    Enrich(finding);
        }

        internal static bool Enrich(VulnerabilityFinding finding)
        {
            if (finding == null || string.IsNullOrWhiteSpace(finding.Id)) return false;
            Details details;
            if (!Known.TryGetValue(finding.Id.Trim(), out details)) return false;

            bool changed = false;
            if (string.IsNullOrWhiteSpace(finding.Title))
            {
                finding.Title = details.Title;
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(finding.Severity) ||
                string.Equals(finding.Severity, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
            {
                finding.Severity = details.Severity;
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(finding.PrimaryUrl))
            {
                finding.PrimaryUrl = details.References[0];
                changed = true;
            }
            foreach (string reference in details.References)
                if (AddUnique(finding.References, reference)) changed = true;
            return changed;
        }

        private static bool AddUnique(List<string> values, string value)
        {
            if (values == null || string.IsNullOrWhiteSpace(value)) return false;
            foreach (string existing in values)
                if (string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)) return false;
            values.Add(value);
            return true;
        }
    }
}
