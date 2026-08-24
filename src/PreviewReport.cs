using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace RedOSPackageUpdater
{
    // Формирование HTML-отчёта предпроверки (что будет обновлено по каждому узлу).
    internal static class PreviewReport
    {
        public static string Build(List<HostPreview> res, string dir)
        {
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset='utf-8'><title>Предпроверка обновлений</title><style>");
            sb.Append("body{font-family:Segoe UI,Arial,sans-serif;margin:20px;color:#222}h1{font-size:20px}");
            sb.Append("h2{font-size:16px;margin-top:22px;border-bottom:2px solid #ccc;padding-bottom:4px}");
            sb.Append("h3{font-size:14px;margin:14px 0 4px}table{border-collapse:collapse;width:100%;margin:6px 0 14px;font-size:13px}");
            sb.Append("th,td{border:1px solid #ddd;padding:4px 8px;text-align:left}th{background:#f3f3f3}");
            sb.Append("tr.sec{background:#fff6e5}.excl{color:#999;text-decoration:line-through}.err{color:#b00}.muted{color:#777}");
            sb.Append(".badge{display:inline-block;background:#e01a22;color:#fff;border-radius:3px;padding:0 5px;font-size:11px}");
            sb.Append(".dep{display:inline-block;background:#6c757d;color:#fff;border-radius:3px;padding:0 5px;font-size:11px}");
            sb.Append(".kern{display:inline-block;background:#1e6fd0;color:#fff;border-radius:3px;padding:0 5px;font-size:11px}");
            sb.Append(".skip{display:inline-block;background:#999;color:#fff;border-radius:3px;padding:0 5px;font-size:11px}");
            sb.Append("</style></head><body>");
            sb.Append("<h1>Предпроверка: что реально изменит транзакция профиля</h1>");

            int totalPkgs = 0, totalSec = 0, totalDep = 0;
            foreach (var h in res) { totalPkgs += h.Total; totalSec += h.Sec; totalDep += h.Dep; }
            sb.Append("<p class=muted>Сформировано: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " &nbsp; Узлов: " + res.Count + "</p>");
            sb.Append("<p><b>Всего в транзакции:</b> " + totalPkgs + " (по advisory: " + totalSec + ", зависимости: " + totalDep + ")</p>");
            sb.Append("<p class=muted>Категории: <span class=badge>security</span> - есть уязвимость; <span class=dep>зависимость</span> - тянется резолвером (своего advisory нет); <span class=kern>ядро</span>; <span class=skip>будет пропущен</span> - исключён маской.</p>");

            foreach (var sys in DistinctSystems(res))
            {
                sb.Append("<h2>" + Esc(sys) + "</h2>");
                foreach (var h in res)
                {
                    if (h.System != sys) continue;
                    AppendHost(sb, h);
                }
            }

            sb.Append("</body></html>");
            string path = Path.Combine(dir, "preview.html");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }

        private static void AppendHost(StringBuilder sb, HostPreview h)
        {
            string osSuffix = string.IsNullOrEmpty(h.OsInfo) ? "" : " <span class=muted>[" + Esc(h.OsInfo) + "]</span>";
            sb.Append("<h3>" + Esc(h.Name) + " — " + Esc(h.Host) + osSuffix + "</h3>");
            if (!string.IsNullOrEmpty(h.Error)) { sb.Append("<p class=err>Ошибка: " + Esc(h.Error) + "</p>"); return; }
            sb.Append("<p class=muted>В транзакции: " + h.Total + " (по advisory: " + h.Sec + ", зависимости: " + h.Dep + "), исключено маской: " + h.Excluded + "</p>");
            if (h.Packages == null || h.Packages.Count == 0) { sb.Append("<p class=muted>Транзакция пустая - обновлять нечего.</p>"); return; }

            sb.Append("<table><tr><th>Пакет</th><th>Текущая</th><th>Новая</th><th>Репозиторий</th><th>Категория</th></tr>");
            foreach (var p in h.Packages)
            {
                sb.Append("<tr class='" + RowClass(p) + "'>");
                sb.Append("<td>" + Esc(p.Name) + "</td><td>" + Esc(p.Old) + "</td><td>" + Esc(p.New) + "</td><td>" + Esc(p.Repo) + "</td>");
                sb.Append("<td>" + KindBadge(p) + "</td></tr>");
            }
            sb.Append("</table>");
        }

        // Настоящий .xlsx (OOXML) - плоская таблица. Именно xlsx, а не SpreadsheetML c расширением .xls,
        // иначе Excel ругается "формат не соответствует расширению".
        private static readonly int[] ColW = { 20, 20, 14, 26, 30, 16, 16, 15, 30 };
        private static readonly string[] Headers = { "Система", "Узел", "Host", "ОС узла", "Пакет", "Текущая", "Новая", "Репозиторий", "Категория" };

        public static string BuildXlsx(List<HostPreview> res, string dir)
        {
            string path = Path.Combine(dir, "preview.xlsx");
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                AddEntry(zip, "[Content_Types].xml", ContentTypesXml());
                AddEntry(zip, "_rels/.rels", RootRelsXml());
                AddEntry(zip, "xl/workbook.xml", WorkbookXml());
                AddEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml());
                AddEntry(zip, "xl/styles.xml", StylesXml());
                AddEntry(zip, "xl/worksheets/sheet1.xml", SheetXml(res));
            }
            return path;
        }

        private static void AddEntry(ZipArchive zip, string name, string content)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var s = e.Open())
            using (var w = new StreamWriter(s, new UTF8Encoding(false)))
                w.Write(content);
        }

        private static string ContentTypesXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
                + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>"
                + "</Types>";
        }

        private static string RootRelsXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
                + "</Relationships>";
        }

        private static string WorkbookXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                + "<sheets><sheet name=\"Предпроверка\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";
        }

        private static string WorkbookRelsXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"
                + "</Relationships>";
        }

        // Индексы cellXfs: 0 default, 1 hdr, 2 sec, 3 dep, 4 kern, 5 exc, 6 err.
        private static string StylesXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                + "<fonts count=\"4\">"
                + "<font><sz val=\"11\"/><name val=\"Calibri\"/></font>"
                + "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>"
                + "<font><color rgb=\"FF999999\"/><sz val=\"11\"/><name val=\"Calibri\"/></font>"
                + "<font><b/><color rgb=\"FFB00000\"/><sz val=\"11\"/><name val=\"Calibri\"/></font>"
                + "</fonts>"
                + "<fills count=\"6\">"
                + "<fill><patternFill patternType=\"none\"/></fill>"
                + "<fill><patternFill patternType=\"gray125\"/></fill>"
                + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFF6E5\"/><bgColor indexed=\"64\"/></patternFill></fill>"
                + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF0F0F0\"/><bgColor indexed=\"64\"/></patternFill></fill>"
                + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE4EFFB\"/><bgColor indexed=\"64\"/></patternFill></fill>"
                + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF3F3F3\"/><bgColor indexed=\"64\"/></patternFill></fill>"
                + "</fills>"
                + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
                + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
                + "<cellXfs count=\"7\">"
                + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
                + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"5\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>"
                + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFill=\"1\"/>"
                + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"3\" borderId=\"0\" xfId=\"0\" applyFill=\"1\"/>"
                + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"4\" borderId=\"0\" xfId=\"0\" applyFill=\"1\"/>"
                + "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>"
                + "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>"
                + "</cellXfs>"
                + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>"
                + "</styleSheet>";
        }

        private static string SheetXml(List<HostPreview> res)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sb.Append("<cols>");
            for (int i = 0; i < ColW.Length; i++)
                sb.Append("<col min=\"" + (i + 1) + "\" max=\"" + (i + 1) + "\" width=\"" + ColW[i] + "\" customWidth=\"1\"/>");
            sb.Append("</cols><sheetData>");

            int r = 1;
            Row(sb, r++, Headers, 1);
            foreach (var host in res)
            {
                if (!string.IsNullOrEmpty(host.Error))
                    Row(sb, r++, new[] { host.System, host.Name, host.Host, host.OsInfo, "ОШИБКА: " + host.Error, "", "", "", "" }, 6);
                else if (host.Packages == null || host.Packages.Count == 0)
                    Row(sb, r++, new[] { host.System, host.Name, host.Host, host.OsInfo, "обновлений нет", "", "", "", "" }, 0);
                else
                    foreach (var p in host.Packages)
                        Row(sb, r++, new[] { host.System, host.Name, host.Host, host.OsInfo, p.Name, p.Old, p.New, p.Repo, KindText(p) }, KindXf(p));
            }
            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        private static void Row(StringBuilder sb, int r, string[] vals, int s)
        {
            sb.Append("<row r=\"" + r + "\">");
            for (int c = 0; c < vals.Length; c++)
                sb.Append("<c r=\"" + ColLetter(c) + r + "\" s=\"" + s + "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">" + Esc(vals[c]) + "</t></is></c>");
            sb.Append("</row>");
        }

        private static string ColLetter(int i)
        {
            string s = ""; i++;
            while (i > 0) { int rem = (i - 1) % 26; s = (char)('A' + rem) + s; i = (i - 1) / 26; }
            return s;
        }

        // Индекс стиля cellXfs по категории пакета.
        private static int KindXf(PkgUpdate p)
        {
            switch (Kind(p)) { case "sec": return 2; case "kern": return 4; case "excl": return 5; default: return 3; }
        }

        private static List<string> DistinctSystems(List<HostPreview> res)
        {
            var list = new List<string>();
            foreach (var h in res) if (!list.Contains(h.System)) list.Add(h.System);
            return list;
        }

        private static string Kind(PkgUpdate p) { return string.IsNullOrEmpty(p.Kind) ? (p.Excluded ? "excl" : (p.Security ? "sec" : "dep")) : p.Kind; }

        private static string RowClass(PkgUpdate p)
        {
            switch (Kind(p)) { case "excl": return "excl"; case "sec": return "sec"; default: return ""; }
        }

        private static string KindBadge(PkgUpdate p)
        {
            switch (Kind(p))
            {
                case "sec": return "<span class=badge>security</span>";
                case "kern": return "<span class=kern>ядро</span>";
                case "excl": return "<span class=skip>исключён - будет пропущен</span>";
                default:
                    string r = string.IsNullOrEmpty(p.Reason) ? "" : " <span class=muted>(" + Esc(p.Reason) + ")</span>";
                    return "<span class=dep>зависимость</span>" + r;
            }
        }

        // Текстовая метка категории для XLS.
        private static string KindText(PkgUpdate p)
        {
            switch (Kind(p))
            {
                case "sec": return "security (advisory)";
                case "kern": return "ядро";
                case "excl": return "исключён - будет пропущен";
                default: return string.IsNullOrEmpty(p.Reason) ? "зависимость" : "зависимость — " + p.Reason;
            }
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // Вывод dnf/yum по SSH может нести ANSI-esc (0x1B) и прочие C0-управляющие символы.
            // В XML 1.0 они запрещены даже экранированные - Excel посчитает .xlsx битым. Вычищаем.
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') continue;   // разрешены только TAB/LF/CR
                if (c == '&') sb.Append("&amp;");
                else if (c == '<') sb.Append("&lt;");
                else if (c == '>') sb.Append("&gt;");
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
