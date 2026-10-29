using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Porting dari OfficeReader.kt:
    /// Pembaca ringan untuk dokumen Office (.docx / .xlsx) dan CSV tanpa library pihak ketiga.
    /// </summary>
    public static class OfficeReader
    {
        public record Paragraph(string Text, bool Bold = false, bool Italic = false, int Align = 0);
        public record Sheet(string Name, List<List<string>> Rows, HashSet<int> BoldRows);

        // ================= DOCX =================
        public static List<Paragraph> ReadDocx(string filePath)
        {
            var entries = ReadZip(filePath, name => name == "word/document.xml");
            if (!entries.TryGetValue("word/document.xml", out byte[]? xml))
                return new List<Paragraph>();
            return ParseDocx(xml);
        }

        public static string ReadDocxText(string filePath)
            => string.Join("\n\n", ReadDocx(filePath).Select(p => p.Text));

        private static List<Paragraph> ParseDocx(byte[] data)
        {
            var outList = new List<Paragraph>();
            try
            {
                using var ms = new MemoryStream(data);
                using var r = XmlReader.Create(ms, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });

                bool inParagraph = false;
                bool inText = false;
                bool bold = false;
                bool italic = false;
                int align = 0;
                var text = new StringBuilder();

                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element)
                    {
                        string local = r.LocalName.ToLowerInvariant();
                        bool empty = r.IsEmptyElement;
                        switch (local)
                        {
                            case "p":
                                inParagraph = true;
                                bold = false;
                                italic = false;
                                align = 0;
                                text.Clear();
                                if (empty) inParagraph = false;
                                break;
                            case "b":
                                if (inParagraph) bold = !IsOff(r.GetAttribute("w:val") ?? r.GetAttribute("val"));
                                break;
                            case "i":
                                if (inParagraph) italic = !IsOff(r.GetAttribute("w:val") ?? r.GetAttribute("val"));
                                break;
                            case "jc":
                                if (inParagraph)
                                {
                                    string val = (r.GetAttribute("w:val") ?? r.GetAttribute("val") ?? "").ToLowerInvariant();
                                    align = val switch
                                    {
                                        "center" => 1,
                                        "right" or "end" => 2,
                                        "both" or "justify" => 3,
                                        _ => 0
                                    };
                                }
                                break;
                            case "t":
                                if (!empty) inText = true;
                                break;
                            case "br" or "cr":
                                if (inParagraph) text.Append('\n');
                                break;
                            case "tab":
                                if (inParagraph) text.Append('\t');
                                break;
                        }
                    }
                    else if (r.NodeType == XmlNodeType.Text || r.NodeType == XmlNodeType.SignificantWhitespace || r.NodeType == XmlNodeType.Whitespace)
                    {
                        if (inParagraph && inText)
                            text.Append(r.Value);
                    }
                    else if (r.NodeType == XmlNodeType.EndElement)
                    {
                        string local = r.LocalName.ToLowerInvariant();
                        if (local == "t") inText = false;
                        else if (local == "p")
                        {
                            if (inParagraph)
                            {
                                string val = text.ToString().Trim();
                                if (!string.IsNullOrEmpty(val))
                                    outList.Add(new Paragraph(val, bold, italic, align));
                                if (outList.Count >= 800) return outList;
                            }
                            inParagraph = false;
                            inText = false;
                        }
                    }
                }
            }
            catch { }
            return outList;
        }

        private static bool IsOff(string? val)
            => val == "0" || string.Equals(val, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(val, "off", StringComparison.OrdinalIgnoreCase);

        // ================= XLSX =================
        public static Sheet? ReadXlsx(string filePath)
        {
            var entries = ReadZip(filePath, name =>
                name == "xl/workbook.xml" ||
                name == "xl/sharedStrings.xml" ||
                (name.StartsWith("xl/worksheets/sheet") && name.EndsWith(".xml")));

            string? sheetEntry = entries.Keys
                .Where(k => k.StartsWith("xl/worksheets/sheet") && k.EndsWith(".xml"))
                .OrderBy(k => k)
                .FirstOrDefault();

            if (sheetEntry == null) return null;

            var shared = entries.TryGetValue("xl/sharedStrings.xml", out byte[]? ss) ? ParseSharedStrings(ss) : new List<string>();
            string sheetName = entries.TryGetValue("xl/workbook.xml", out byte[]? wb) ? (ParseSheetName(wb) ?? "Sheet1") : "Sheet1";
            var rows = ParseWorksheet(entries[sheetEntry], shared);
            return new Sheet(sheetName, rows, new HashSet<int> { 0 });
        }

        private static List<string> ParseSharedStrings(byte[] data)
        {
            var outList = new List<string>();
            try
            {
                using var ms = new MemoryStream(data);
                using var r = XmlReader.Create(ms, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
                bool inSi = false, inText = false;
                var sb = new StringBuilder();
                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element)
                    {
                        string local = r.LocalName.ToLowerInvariant();
                        if (local == "si") { inSi = true; sb.Clear(); if (r.IsEmptyElement) { inSi = false; outList.Add(""); } }
                        else if (local == "t" && inSi && !r.IsEmptyElement) inText = true;
                    }
                    else if ((r.NodeType == XmlNodeType.Text || r.NodeType == XmlNodeType.Whitespace || r.NodeType == XmlNodeType.SignificantWhitespace) && inSi && inText)
                    {
                        sb.Append(r.Value);
                    }
                    else if (r.NodeType == XmlNodeType.EndElement)
                    {
                        string local = r.LocalName.ToLowerInvariant();
                        if (local == "t") inText = false;
                        else if (local == "si") { inSi = false; outList.Add(sb.ToString()); }
                    }
                }
            }
            catch { }
            return outList;
        }

        private static string? ParseSheetName(byte[] data)
        {
            try
            {
                using var ms = new MemoryStream(data);
                using var r = XmlReader.Create(ms, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element && r.LocalName.Equals("sheet", StringComparison.OrdinalIgnoreCase))
                    {
                        string? name = r.GetAttribute("name");
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                    }
                }
            }
            catch { }
            return null;
        }

        private static List<List<string>> ParseWorksheet(byte[] data, List<string> shared)
        {
            var cells = new Dictionary<int, Dictionary<int, string>>();
            int maxRow = -1, maxCol = -1;
            try
            {
                using var ms = new MemoryStream(data);
                using var r = XmlReader.Create(ms, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
                int row = -1, col = -1;
                string cellType = "";
                bool inValue = false, inInline = false;
                var val = new StringBuilder();

                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element)
                    {
                        string local = r.LocalName.ToLowerInvariant();
                        bool empty = r.IsEmptyElement;
                        switch (local)
                        {
                            case "row":
                                row = int.TryParse(r.GetAttribute("r"), out int rr) ? rr - 1 : row + 1;
                                col = -1;
                                break;
                            case "c":
                                cellType = r.GetAttribute("t") ?? "";
                                string? cellRef = r.GetAttribute("r");
                                col = cellRef != null ? ColumnIndex(cellRef) : col + 1;
                                val.Clear();
                                break;
                            case "v":
                                if (!empty) inValue = true;
                                break;
                            case "is":
                                if (!empty) inInline = true;
                                break;
                            case "t":
                                if (inInline && !empty) inValue = true;
                                break;
                        }
                    }
                    else if ((r.NodeType == XmlNodeType.Text || r.NodeType == XmlNodeType.Whitespace || r.NodeType == XmlNodeType.SignificantWhitespace) && inValue)
                    {
                        val.Append(r.Value);
                    }
                    else if (r.NodeType == XmlNodeType.EndElement)
                    {
                        string local = r.LocalName.ToLowerInvariant();
                        switch (local)
                        {
                            case "v":
                                inValue = false;
                                break;
                            case "t":
                                if (inInline) inValue = false;
                                break;
                            case "is":
                                inInline = false;
                                break;
                            case "c":
                                if (row >= 0 && col >= 0)
                                {
                                    string text = val.ToString();
                                    if (cellType == "s" && int.TryParse(text.Trim(), out int sIdx) && sIdx >= 0 && sIdx < shared.Count)
                                        text = shared[sIdx];
                                    if (!string.IsNullOrEmpty(text))
                                    {
                                        if (!cells.TryGetValue(row, out var rowMap))
                                        {
                                            rowMap = new Dictionary<int, string>();
                                            cells[row] = rowMap;
                                        }
                                        rowMap[col] = text;
                                        if (row > maxRow) maxRow = row;
                                        if (col > maxCol) maxCol = col;
                                    }
                                }
                                break;
                        }
                    }
                }
            }
            catch { }

            if (maxRow < 0 || maxCol < 0) return new List<List<string>>();
            var outRows = new List<List<string>>(maxRow + 1);
            for (int r = 0; r <= maxRow; r++)
            {
                cells.TryGetValue(r, out var src);
                var rowList = new List<string>(maxCol + 1);
                for (int c = 0; c <= maxCol; c++)
                    rowList.Add(src != null && src.TryGetValue(c, out string? v) ? v : "");
                outRows.Add(rowList);
            }
            return outRows;
        }

        private static int ColumnIndex(string cellRef)
        {
            int n = 0;
            foreach (char ch in cellRef)
            {
                if (ch is >= 'A' and <= 'Z') n = n * 26 + (ch - 'A' + 1);
                else if (ch is >= 'a' and <= 'z') n = n * 26 + (ch - 'a' + 1);
                else break;
            }
            return Math.Max(0, n - 1);
        }

        // ================= CSV =================
        public static Sheet ReadCsv(string filePath)
        {
            string text = "";
            try { text = File.ReadAllText(filePath); } catch { }
            var firstLines = text.Split('\n').Take(3).ToList();
            char sep = ',';
            if (firstLines.Any(l => l.Count(c => c == ';') > l.Count(c => c == ','))) sep = ';';
            else if (firstLines.Any(l => l.Contains('\t'))) sep = '\t';

            var rows = ParseCsv(text, sep)
                .Where(row => row.Any(cell => !string.IsNullOrWhiteSpace(cell)))
                .ToList();
            return new Sheet("Sheet1", rows, new HashSet<int> { 0 });
        }

        private static List<List<string>> ParseCsv(string text, char sep)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (quoted && c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = !quoted;
                }
                else if (!quoted && c == sep)
                {
                    row.Add(cell.ToString());
                    cell.Clear();
                }
                else if (!quoted && (c == '\n' || c == '\r'))
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(new List<string>(row));
                    row.Clear();
                }
                else
                {
                    cell.Append(c);
                }
                i++;
            }
            if (cell.Length > 0 || row.Count > 0)
            {
                row.Add(cell.ToString());
                rows.Add(new List<string>(row));
            }
            return rows.Count > 500 ? rows.Take(500).ToList() : rows;
        }

        private static Dictionary<string, byte[]> ReadZip(string filePath, Func<string, bool> wanted)
        {
            var outMap = new Dictionary<string, byte[]>();
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                long total = 0;
                foreach (var entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    if (wanted(name) && entry.Length < 12L * 1024 * 1024)
                    {
                        using var s = entry.Open();
                        using var ms = new MemoryStream();
                        s.CopyTo(ms);
                        byte[] data = ms.ToArray();
                        total += data.Length;
                        if (total <= 24L * 1024 * 1024)
                            outMap[name] = data;
                    }
                }
            }
            catch { }
            return outMap;
        }
    }
}
