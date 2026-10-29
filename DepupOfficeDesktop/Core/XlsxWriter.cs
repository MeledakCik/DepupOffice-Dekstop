using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Porting dari XlsxWriter.kt:
    /// Penulis .xlsx mini tanpa library pihak ketiga: satu sheet, teks + angka, header tebal.
    /// </summary>
    public static class XlsxWriter
    {
        private const string XML = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

        public static void Write(
            string outPath,
            string sheetName,
            IReadOnlyList<IReadOnlyList<string>> rows,
            ISet<int>? boldRows = null,
            int columnCount = 0)
        {
            boldRows ??= new HashSet<int> { 0 };
            string safeName = string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : sheetName.Trim();
            if (safeName.Length > 31) safeName = safeName.Substring(0, 31);
            safeName = Regex.Replace(safeName, @"[\/*?\[\]:]", "_");

            int maxCols = rows.Count > 0 ? rows.Max(r => r.Count) : 0;
            int cols = Math.Max(1, columnCount > 0 ? columnCount : maxCols);

            using var fs = new FileStream(outPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);

            void Put(string name, string text)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = entry.Open();
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                s.Write(bytes, 0, bytes.Length);
            }

            Put("[Content_Types].xml", ContentTypes());
            Put("_rels/.rels", RootRels());
            Put("xl/workbook.xml", Workbook(safeName));
            Put("xl/_rels/workbook.xml.rels", WorkbookRels());
            Put("xl/styles.xml", Styles());
            Put("xl/worksheets/sheet1.xml", Worksheet(rows, boldRows, cols));
        }

        private static string ContentTypes() => XML +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "</Types>";

        private static string RootRels() => XML +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>";

        private static string Workbook(string sheetName) => XML +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            $"<sheets><sheet name=\"{Esc(sheetName)}\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
            "</workbook>";

        private static string WorkbookRels() => XML +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "</Relationships>";

        private static string Styles() => XML +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"2\">" +
            "<font><sz val=\"11\"/><color theme=\"1\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
            "<font><b/><sz val=\"11\"/><color theme=\"1\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
            "</fonts>" +
            "<fills count=\"2\">" +
            "<fill><patternFill patternType=\"none\"/></fill>" +
            "<fill><patternFill patternType=\"gray125\"/></fill>" +
            "</fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"2\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
            "</cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
            "</styleSheet>";

        private static string Worksheet(IReadOnlyList<IReadOnlyList<string>> rows, ISet<int> boldRows, int cols)
        {
            var sb = new StringBuilder();
            sb.Append(XML).Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            if (rows.Count > 0)
                sb.Append($"<dimension ref=\"A1:{ColumnName(cols - 1)}{rows.Count}\"/>");

            sb.Append("<cols>");
            for (int c = 0; c < cols; c++)
            {
                int maxLen = 14;
                if (rows.Count > 0)
                {
                    maxLen = rows.Max(r => c < r.Count && r[c] != null ? r[c].Length : 0);
                    maxLen = Math.Clamp(maxLen, 10, 40);
                }
                sb.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{maxLen}\" customWidth=\"1\"/>");
            }
            sb.Append("</cols><sheetData>");

            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                sb.Append($"<row r=\"{r + 1}\">");
                bool bold = boldRows.Contains(r);
                for (int c = 0; c < cols; c++)
                {
                    string raw = (c < row.Count && row[c] != null) ? row[c].Trim() : "";
                    if (string.IsNullOrEmpty(raw)) continue;
                    string cellRef = $"{ColumnName(c)}{r + 1}";
                    string style = bold ? " s=\"1\"" : "";
                    double? numeric = ParseNumber(raw);
                    if (numeric.HasValue)
                        sb.Append($"<c r=\"{cellRef}\"{style}><v>{TrimNumber(numeric.Value)}</v></c>");
                    else
                        sb.Append($"<c r=\"{cellRef}\"{style} t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(raw)}</t></is></c>");
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        private static double? ParseNumber(string raw)
        {
            string s = raw.Trim();
            if (string.IsNullOrEmpty(s)) return null;
            s = Regex.Replace(s, @"(?i)Rp|IDR", "").Replace("$", "").Replace("€", "").Trim();
            if (string.IsNullOrEmpty(s) || !s.Any(char.IsDigit)) return null;
            if (!Regex.IsMatch(s, @"^[+\-0-9.,\s]+$")) return null;
            string compact = Regex.Replace(s, @"\s+", "");
            if (compact.Length > 1 && compact.StartsWith("0") && !compact.StartsWith("0.") && !compact.StartsWith("0,"))
                return null;

            string normalized;
            if (compact.Contains('.') && compact.Contains(','))
                normalized = compact.Replace(".", "").Replace(',', '.');
            else if (compact.Count(ch => ch == ',') == 1 && compact.Substring(compact.IndexOf(',') + 1).Length is >= 1 and <= 2)
                normalized = compact.Replace(',', '.');
            else if (compact.Count(ch => ch == '.') == 1 && compact.Substring(compact.IndexOf('.') + 1).Length is >= 1 and <= 2)
                normalized = compact;
            else
                normalized = compact.Replace(".", "").Replace(",", "");

            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
        }

        private static string TrimNumber(double v)
            => Math.Abs(v - Math.Round(v)) < 0.0000001
                ? ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture)
                : v.ToString(CultureInfo.InvariantCulture);

        public static string ColumnName(int index)
        {
            int n = index + 1;
            var sb = new StringBuilder();
            while (n > 0)
            {
                int rem = (n - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                n = (n - 1) / 26;
            }
            return sb.ToString();
        }

        private static string Esc(string s)
        {
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                if (c == '&') sb.Append("&amp;");
                else if (c == '<') sb.Append("&lt;");
                else if (c == '>') sb.Append("&gt;");
                else if (c == '"') sb.Append("&quot;");
                else if (c < 32 && c != '\t' && c != '\n' && c != '\r') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
