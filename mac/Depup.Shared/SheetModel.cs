using System.Globalization;
using System.Text.RegularExpressions;

namespace DepupOfficeDesktop.Core
{
    public sealed class SheetCell
    {
        public string Value { get; set; }
        public bool Bold { get; set; }
        public string? Background { get; set; }
        /// <summary>Nilai hasil terakhir dari Excel/Numbers untuk sel ber-rumus (dipakai bila rumus tidak didukung).</summary>
        public string? Cached { get; set; }

        public SheetCell(string value = "", bool bold = false, string? background = null)
        {
            Value = value;
            Bold = bold;
            Background = background;
        }
    }

    /// <summary>
    /// Model spreadsheet lintas platform + evaluator rumus (port dari SheetEditorForm / SheetEditorActivity.kt).
    /// Mendukung =SUM, =AVERAGE/AVG, =MIN, =MAX, =COUNT, =ABS, =ROUND, =INT, aritmetika + - * / ^, rentang A1:D5.
    /// </summary>
    public sealed class SheetModel
    {
        public const string TemplateFinance = "finance";
        public const string TemplateInvoice = "invoice";
        public const string TemplatePresensi = "presensi";
        public const string TemplateBlank = "blank";

        public const string HeaderBg = "#F1F5F9";
        public const string TotalBg = "#E0F2FE";
        public const int MaxCols = 26;
        public const int MaxRows = 500;

        public List<List<SheetCell>> Rows { get; } = new();
        public int ColumnCount { get; private set; } = 4;
        public string Title { get; set; } = "Spreadsheet_Baru";
        public string Currency { get; set; } = "Rp";

        private readonly HashSet<string> _visiting = new();
        private static readonly CultureInfo IdCulture = new("id-ID");

        // ---------- Pembuatan ----------
        public static SheetModel FromFile(string path)
        {
            var model = new SheetModel();
            string ext = Path.GetExtension(path).ToLowerInvariant();
            var sheet = ext is ".csv" or ".txt" ? OfficeReader.ReadCsv(path) : OfficeReader.ReadXlsx(path);
            model.Title = AppStorage.BaseName(Path.GetFileName(path));
            if (sheet != null && sheet.Rows.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(sheet.Name) && ext == ".xlsx") model.Title = sheet.Name;
                model.Fill(sheet.Rows, sheet.BoldRows);
                foreach (var ((fr, fc), f) in sheet.Formulas)
                {
                    if (fr >= MaxRows || fc >= MaxCols) continue;
                    if (fc >= model.ColumnCount) model.EnsureColumns(fc + 1);
                    var cell = model.Cell(fr, fc);
                    cell.Cached = cell.Value;
                    cell.Value = "=" + f;
                }
            }
            else
            {
                model.LoadTemplate(TemplateBlank);
                model.Title = AppStorage.BaseName(Path.GetFileName(path));
            }
            return model;
        }

        public static SheetModel FromTemplate(string key)
        {
            var m = new SheetModel();
            m.LoadTemplate(key);
            return m;
        }

        public void Fill(List<List<string>> rows, HashSet<int> boldRows)
        {
            Rows.Clear();
            int maxCols = rows.Count > 0 ? rows.Max(r => r.Count) : 4;
            ColumnCount = Math.Clamp(maxCols, 2, MaxCols);
            for (int r = 0; r < Math.Min(rows.Count, MaxRows); r++)
            {
                bool bold = boldRows.Contains(r);
                var list = new List<SheetCell>(ColumnCount);
                for (int c = 0; c < ColumnCount; c++)
                    list.Add(new SheetCell(c < rows[r].Count ? rows[r][c] : "", bold, bold ? HeaderBg : null));
                Rows.Add(list);
            }
            if (Rows.Count == 0) AddRow();
        }

        public void LoadTemplate(string key)
        {
            Rows.Clear();
            ColumnCount = 4;
            string[][] data;
            switch (key)
            {
                case TemplateInvoice:
                    Title = "Invoice_Kuitansi";
                    data = new[]
                    {
                        new[] { "No Invoice", "Klien", "Jatuh Tempo", "Total Tagihan" },
                        new[] { "INV-001", "PT Depup Tech", "28 Okt 2026", "15000000" },
                        new[] { "INV-002", "CV Jaya Abadi", "30 Okt 2026", "8500000" },
                        new[] { "TOTAL", "", "", "=SUM(D2:D3)" },
                    };
                    break;
                case TemplatePresensi:
                    Title = "Presensi_Daftar_Hadir";
                    data = new[]
                    {
                        new[] { "Nama Karyawan", "Hadir", "Izin", "Status" },
                        new[] { "Budi Pratama", "22", "0", "Lengkap" },
                        new[] { "Siti Aminah", "21", "1", "Izin Sakit" },
                        new[] { "Andi Wijaya", "22", "0", "Lengkap" },
                    };
                    break;
                case TemplateBlank:
                    Title = "Spreadsheet_Baru";
                    data = Enumerable.Range(0, 10).Select(_ => new[] { "", "", "", "" }).ToArray();
                    break;
                default:
                    Title = "Laporan_Keuangan";
                    data = new[]
                    {
                        new[] { "Produk/Item", "Qty", "Harga Satuan", "Total (Rp)" },
                        new[] { "Depup Cloud Pro", "12", "120000", "=B2*C2" },
                        new[] { "Lisensi PDF OCR", "8", "250000", "=B3*C3" },
                        new[] { "Add-on e-Meterai", "15", "12500", "=B4*C4" },
                        new[] { "Server Vault Storage", "4", "450000", "=B5*C5" },
                        new[] { "TOTAL PENDAPATAN", "=SUM(B2:B5)", "-", "=SUM(D2:D5)" },
                    };
                    break;
            }
            for (int r = 0; r < data.Length; r++)
            {
                bool header = key != TemplateBlank && r == 0;
                bool total = key != TemplateBlank && key != TemplatePresensi && r == data.Length - 1;
                string? bg = header ? HeaderBg : total ? TotalBg : null;
                Rows.Add(data[r].Select(v => new SheetCell(v, header || total, bg)).ToList());
            }
        }

        // ---------- Edit struktur ----------
        public void AddRow()
        {
            if (Rows.Count >= MaxRows) return;
            Rows.Add(Enumerable.Range(0, ColumnCount).Select(_ => new SheetCell()).ToList());
        }

        public void RemoveLastRow()
        {
            if (Rows.Count > 1) Rows.RemoveAt(Rows.Count - 1);
        }

        public void EnsureColumns(int count)
        {
            while (ColumnCount < Math.Min(count, MaxCols)) AddColumn();
        }

        public void AddColumn()
        {
            if (ColumnCount >= MaxCols) return;
            ColumnCount++;
            foreach (var row in Rows) row.Add(new SheetCell());
        }

        public void RemoveLastColumn()
        {
            if (ColumnCount <= 1) return;
            ColumnCount--;
            foreach (var row in Rows)
                if (row.Count > ColumnCount) row.RemoveAt(row.Count - 1);
        }

        public SheetCell Cell(int r, int c)
        {
            while (Rows.Count <= r) AddRow();
            while (Rows[r].Count <= c) Rows[r].Add(new SheetCell());
            return Rows[r][c];
        }

        public string AutoSumFormula(int col)
        {
            int end = Math.Max(2, Rows.Count - 1);
            string name = XlsxWriter.ColumnName(col);
            return $"=SUM({name}2:{name}{end})";
        }

        // ---------- Nilai ----------
        public string Display(int r, int c)
        {
            if (r < 0 || r >= Rows.Count || c < 0 || c >= Rows[r].Count) return "";
            string raw = Rows[r][c].Value;
            if (!raw.TrimStart().StartsWith('=')) return raw;
            double? v = CellNumber(r, c);
            if (!v.HasValue) return Rows[r][c].Cached ?? "#ERROR!";
            double val = v.Value;
            return Math.Abs(val - Math.Round(val)) < 1e-9
                ? val.ToString("N0", IdCulture)
                : val.ToString("N2", IdCulture);
        }

        public List<List<string>> ToDisplayRows()
            => Rows.Select((row, r) => Enumerable.Range(0, ColumnCount).Select(c => Display(r, c)).ToList()).ToList();

        public List<List<string>> ToExportRows()
        {
            // Hasil rumus diekspor sebagai angka murni agar terbaca sebagai angka di Excel/Numbers.
            var list = new List<List<string>>();
            for (int r = 0; r < Rows.Count; r++)
            {
                var row = new List<string>();
                for (int c = 0; c < ColumnCount; c++)
                {
                    string raw = c < Rows[r].Count ? Rows[r][c].Value : "";
                    if (raw.TrimStart().StartsWith('='))
                    {
                        double? v = CellNumber(r, c);
                        row.Add(v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : Rows[r][c].Cached ?? "#ERROR!");
                    }
                    else row.Add(raw);
                }
                list.Add(row);
            }
            return list;
        }

        /// <summary>Isi mentah (termasuk rumus "=...") untuk disimpan sebagai rumus Excel.</summary>
        public List<List<string>> ToRawRows()
            => Rows.Select(row => Enumerable.Range(0, ColumnCount).Select(c => c < row.Count ? row[c].Value : "").ToList()).ToList();

        public void SaveXlsx(string path)
            => XlsxWriter.Write(path, Title, ToExportRows(), BoldRows(), ColumnCount, ToRawRows());

        public HashSet<int> BoldRows()
            => Rows.Select((row, i) => (row, i)).Where(t => t.row.Any(c => c.Bold)).Select(t => t.i).ToHashSet();

        public string ToCsv(string separator = ",")
        {
            static string Q(string s, string sep)
                => s.Contains(sep) || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
            return string.Join("\n", ToExportRows().Select(r => string.Join(separator, r.Select(v => Q(v, separator)))));
        }

        public double? CellNumber(int r, int c)
        {
            if (r < 0 || r >= Rows.Count || c < 0 || c >= Rows[r].Count) return null;
            string raw = Rows[r][c].Value.Trim();
            if (string.IsNullOrEmpty(raw)) return null;
            if (!raw.StartsWith('=')) return ParseNumber(raw);

            string key = $"{r}:{c}";
            if (!_visiting.Add(key)) return 0.0; // referensi melingkar
            try
            {
                double val = new FormulaParser(this, raw.Substring(1)).Parse();
                return double.IsNaN(val) || double.IsInfinity(val) ? null : val;
            }
            catch { return null; }
            finally { _visiting.Remove(key); }
        }

        public double? ParseNumber(string raw)
        {
            string s = raw.Trim();
            if (!string.IsNullOrEmpty(Currency)) s = s.Replace(Currency, "", StringComparison.OrdinalIgnoreCase);
            s = s.Replace("Rp", "", StringComparison.OrdinalIgnoreCase).Replace("$", "").Replace("€", "").Replace(" ", "");
            if (string.IsNullOrEmpty(s) || !s.Any(char.IsDigit)) return null;
            if (!Regex.IsMatch(s, @"^[+\-]?[0-9.,]+$")) return null;
            string digits = s.TrimStart('+', '-');
            if (digits.Length > 1 && digits.StartsWith('0') && !digits.StartsWith("0.") && !digits.StartsWith("0,")) return null;

            bool hasDot = s.Contains('.'), hasComma = s.Contains(',');
            if (hasDot && hasComma)
                s = s.LastIndexOf(',') > s.LastIndexOf('.') ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
            else if (hasComma)
            {
                int dec = s.Length - s.LastIndexOf(',') - 1;
                s = dec is >= 1 and <= 2 && s.Count(ch => ch == ',') == 1 ? s.Replace(',', '.') : s.Replace(",", "");
            }
            else if (hasDot)
            {
                int dec = s.Length - s.LastIndexOf('.') - 1;
                s = s.Count(ch => ch == '.') == 1 && dec != 3 ? s : s.Replace(".", "");
            }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
        }

        private sealed class FormulaParser
        {
            private readonly SheetModel _m;
            private readonly string _src;
            private int _pos;

            public FormulaParser(SheetModel m, string src) { _m = m; _src = src; }

            public double Parse()
            {
                object v = Expr();
                SkipSpace();
                if (_pos < _src.Length) throw new ArgumentException("Ekspresi tidak valid");
                return Scalar(v);
            }

            private object Expr()
            {
                object val = Term();
                while (true)
                {
                    SkipSpace();
                    if (_pos >= _src.Length) break;
                    char ch = _src[_pos];
                    if (ch == '+') { _pos++; val = Scalar(val) + Scalar(Term()); }
                    else if (ch == '-') { _pos++; val = Scalar(val) - Scalar(Term()); }
                    else break;
                }
                return val;
            }

            private object Term()
            {
                object val = Power();
                while (true)
                {
                    SkipSpace();
                    if (_pos >= _src.Length) break;
                    char ch = _src[_pos];
                    if (ch == '*') { _pos++; val = Scalar(val) * Scalar(Power()); }
                    else if (ch == '/')
                    {
                        _pos++;
                        double d = Scalar(Power());
                        val = d == 0.0 ? double.NaN : Scalar(val) / d;
                    }
                    else break;
                }
                return val;
            }

            private object Power()
            {
                object val = Unary();
                SkipSpace();
                if (_pos < _src.Length && _src[_pos] == '^')
                {
                    _pos++;
                    val = Math.Pow(Scalar(val), Scalar(Power()));
                }
                return val;
            }

            private object Unary()
            {
                SkipSpace();
                if (_pos < _src.Length)
                {
                    if (_src[_pos] == '-') { _pos++; return -Scalar(Unary()); }
                    if (_src[_pos] == '+') { _pos++; return Unary(); }
                }
                return Primary();
            }

            private object Primary()
            {
                SkipSpace();
                if (_pos >= _src.Length) throw new ArgumentException("Kosong");
                char ch = _src[_pos];
                if (ch == '(')
                {
                    _pos++;
                    object v = Expr();
                    SkipSpace();
                    if (_pos < _src.Length && _src[_pos] == ')') _pos++;
                    return v;
                }
                if (char.IsDigit(ch) || ch == '.') return ReadNumber();
                if (ch == '$') { _pos++; return Primary(); }
                if (char.IsLetter(ch)) return ReadNameOrCell();
                throw new ArgumentException($"Karakter tak dikenal: {ch}");
            }

            private double ReadNumber()
            {
                int start = _pos;
                while (_pos < _src.Length && (char.IsDigit(_src[_pos]) || _src[_pos] == '.')) _pos++;
                return double.Parse(_src.AsSpan(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            private object ReadNameOrCell()
            {
                int start = _pos;
                while (_pos < _src.Length && char.IsLetter(_src[_pos])) _pos++;
                string name = _src.Substring(start, _pos - start).ToUpperInvariant();
                if (_pos < _src.Length && _src[_pos] == '$') _pos++;

                int digitsStart = _pos;
                while (_pos < _src.Length && char.IsDigit(_src[_pos])) _pos++;
                if (_pos > digitsStart)
                {
                    int row = int.Parse(_src.AsSpan(digitsStart, _pos - digitsStart), CultureInfo.InvariantCulture) - 1;
                    int col = ColumnIndex(name);
                    SkipSpace();
                    if (_pos < _src.Length && _src[_pos] == ':')
                    {
                        _pos++;
                        var end = ReadCellReference();
                        if (end == null) return 0.0;
                        return RangeValues(row, col, end.Value.Row, end.Value.Col);
                    }
                    return _m.CellNumber(row, col) ?? 0.0;
                }

                SkipSpace();
                if (_pos >= _src.Length || _src[_pos] != '(') throw new ArgumentException($"Nama tak dikenal: {name}");
                _pos++;
                var args = new List<object>();
                SkipSpace();
                if (_pos < _src.Length && _src[_pos] == ')') _pos++;
                else
                {
                    while (true)
                    {
                        args.Add(Expr());
                        SkipSpace();
                        if (_pos < _src.Length && (_src[_pos] == ',' || _src[_pos] == ';')) { _pos++; continue; }
                        if (_pos < _src.Length && _src[_pos] == ')') { _pos++; break; }
                        throw new ArgumentException($"Fungsi {name} tidak valid");
                    }
                }
                return ApplyFunction(name, args);
            }

            private (int Row, int Col)? ReadCellReference()
            {
                SkipSpace();
                if (_pos < _src.Length && _src[_pos] == '$') _pos++;
                int start = _pos;
                while (_pos < _src.Length && char.IsLetter(_src[_pos])) _pos++;
                string letters = _src.Substring(start, _pos - start);
                if (_pos < _src.Length && _src[_pos] == '$') _pos++;
                int digitsStart = _pos;
                while (_pos < _src.Length && char.IsDigit(_src[_pos])) _pos++;
                if (letters.Length == 0 || _pos == digitsStart) return null;
                int row = int.Parse(_src.AsSpan(digitsStart, _pos - digitsStart), CultureInfo.InvariantCulture) - 1;
                return (row, ColumnIndex(letters));
            }

            private static int ColumnIndex(string letters)
            {
                int n = 0;
                foreach (char ch in letters) n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
                return Math.Max(0, n - 1);
            }

            private List<double> RangeValues(int r1, int c1, int r2, int c2)
            {
                var list = new List<double>();
                for (int r = Math.Min(r1, r2); r <= Math.Max(r1, r2); r++)
                    for (int c = Math.Min(c1, c2); c <= Math.Max(c1, c2); c++)
                        if (_m.CellNumber(r, c) is double v) list.Add(v);
                return list;
            }

            private static double ApplyFunction(string name, List<object> args)
            {
                var values = new List<double>();
                foreach (var a in args)
                {
                    if (a is List<double> list) values.AddRange(list);
                    else if (a is double d) values.Add(d);
                }
                return name switch
                {
                    "SUM" => values.Sum(),
                    "AVERAGE" or "AVG" => values.Count == 0 ? 0.0 : values.Average(),
                    "MIN" => values.Count == 0 ? 0.0 : values.Min(),
                    "MAX" => values.Count == 0 ? 0.0 : values.Max(),
                    "COUNT" => values.Count,
                    "ABS" => Math.Abs(values.FirstOrDefault()),
                    "ROUND" => Math.Round(values.ElementAtOrDefault(0), Math.Clamp((int)values.ElementAtOrDefault(1), 0, 15), MidpointRounding.AwayFromZero),
                    "INT" => Math.Floor(values.FirstOrDefault()),
                    _ => throw new ArgumentException($"Fungsi tidak dikenal: {name}")
                };
            }

            private static double Scalar(object v) => v switch
            {
                double d => d,
                List<double> list => list.FirstOrDefault(),
                _ => 0.0
            };

            private void SkipSpace()
            {
                while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos])) _pos++;
            }
        }
    }
}
