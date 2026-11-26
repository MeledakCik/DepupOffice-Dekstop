using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Forms
{
    public sealed class SheetCell
    {
        public string ValStr { get; set; } = "";
        public bool IsBold { get; set; }
        public Color BgColor { get; set; } = Color.White;

        public SheetCell(string valStr = "", bool isBold = false, Color? bgColor = null)
        {
            ValStr = valStr;
            IsBold = isBold;
            BgColor = bgColor ?? Color.White;
        }
    }

    /// <summary>
    /// Porting dari SheetEditorActivity.kt:
    /// Editor Spreadsheet Excel (.xlsx & .csv) dengan dukungan Formula Bar (fx), evaluasi rumus rekursif
    /// (=SUM, =AVERAGE, =MIN, =MAX, =COUNT, =ABS, =ROUND, =INT, aritmetika sel), templat siap pakai,
    /// Voice Reader, Cetak Langsung, dan ekspor .xlsx + .csv + .pdf.
    /// </summary>
    public class SheetEditorForm : Form
    {
        public const string TEMPLATE_INVOICE = "invoice";
        public const string TEMPLATE_PRESENSI = "presensi";
        public const string TEMPLATE_BLANK = "blank";

        private readonly string? _sourceFilePath;
        private readonly string? _templateKey;
        private string? _sourceName;

        private TextBox _sheetTitleEt = null!;
        private TextBox _formulaEt = null!;
        private Label _activeCellPill = null!;
        private DataGridView _grid = null!;

        private int _numCols = 4;
        private const int MaxColumns = 16_384;
        private readonly List<List<SheetCell>> _matrix = new();

        private int _activeRow = 0;
        private int _activeCol = 0;
        private bool _updatingUi = false;
        private bool _dirty;

        private bool _currentGridlines;
        private string _currentCurrency;
        private readonly HashSet<string> _visitingCells = new();
        private readonly CultureInfo _idCulture = new("id-ID");

        public SheetEditorForm(string? filePath = null, string? templateKey = null)
        {
            _sourceFilePath = filePath;
            _templateKey = templateKey;

            _currentGridlines = AppStorage.Config.SheetShowGridlines;
            _currentCurrency = AppStorage.Config.SheetCurrencySymbol;

            string initialTitle = LoadInitialMatrix();

            Text = "Editor Spreadsheet Excel — Depup Office Desktop";
            Size = new Size(1020, 720);
            MinimumSize = new Size(780, 520);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.BG;

            BuildUi(initialTitle);
            SyncFormulaBar();
            RenderGrid();
        }

        private string LoadInitialMatrix()
        {
            if (!string.IsNullOrWhiteSpace(_sourceFilePath) && File.Exists(_sourceFilePath))
            {
                _sourceName = AppStorage.BaseName(Path.GetFileName(_sourceFilePath));
                string ext = Path.GetExtension(_sourceFilePath).ToLowerInvariant();
                var sheet = (ext == ".csv" || ext == ".txt")
                    ? OfficeReader.ReadCsv(_sourceFilePath)
                    : OfficeReader.ReadXlsx(_sourceFilePath, preserveFormulas: true);

                if (sheet == null) throw new InvalidDataException("Spreadsheet tidak dapat dibaca.");
                FillMatrix(sheet.Rows, sheet.BoldRows);
                return string.IsNullOrWhiteSpace(sheet.Name) ? (_sourceName ?? "Spreadsheet") : sheet.Name;
            }

            switch (_templateKey)
            {
                case TEMPLATE_INVOICE:
                    LoadInvoiceTemplate();
                    return "Invoice_Kuintansi";
                case TEMPLATE_PRESENSI:
                    LoadPresensiTemplate();
                    return "Presensi_Daftar_Hadir";
                case TEMPLATE_BLANK:
                    _matrix.Clear();
                    _numCols = 4;
                    for (int r = 0; r < 8; r++)
                        _matrix.Add(Enumerable.Range(0, _numCols).Select(_ => new SheetCell("")).ToList());
                    return "Spreadsheet_Baru";
            }

            SeedDefaultMatrix();
            return "Laporan_Keuangan_Depup";
        }

        private void FillMatrix(List<List<string>> rows, HashSet<int> boldRows)
        {
            _matrix.Clear();
            int maxCols = rows.Count > 0 ? rows.Max(r => r.Count) : 4;
            if (maxCols > MaxColumns || rows.Count > 1_048_576 || Math.Max(1, rows.Count) * (long)maxCols > 2_000_000)
                throw new InvalidDataException("Rentang spreadsheet terlalu besar untuk dibuka di editor.");
            _numCols = Math.Max(2, maxCols);
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                bool isBold = boldRows.Contains(r);
                var list = new List<SheetCell>(_numCols);
                for (int c = 0; c < _numCols; c++)
                {
                    string val = c < row.Count ? row[c] : "";
                    list.Add(new SheetCell(val, isBold, isBold ? Theme.SHEET_HEAD : Color.White));
                }
                _matrix.Add(list);
            }
            if (_matrix.Count == 0)
                _matrix.Add(Enumerable.Range(0, _numCols).Select(_ => new SheetCell("")).ToList());
        }

        private void SeedDefaultMatrix()
        {
            _matrix.Clear();
            _numCols = 4;
            string[][] defaultRows =
            {
                new[] { "Produk/Item", "Qty", "Harga Satuan", "Total (Rp)" },
                new[] { "Depup Cloud Pro", "12", "120000", "=B2*C2" },
                new[] { "Lisensi PDF OCR", "8", "250000", "=B3*C3" },
                new[] { "Add-on e-Meterai", "15", "12500", "=B4*C4" },
                new[] { "Server Vault Storage", "4", "450000", "=B5*C5" },
                new[] { "TOTAL PENDAPATAN", "=SUM(B2:B5)", "-", "=SUM(D2:D5)" }
            };

            for (int r = 0; r < defaultRows.Length; r++)
            {
                var rList = new List<SheetCell>();
                Color bg = r == 0 ? Theme.SHEET_HEAD : (r == 5 ? Theme.SHEET_TOTAL : Color.White);
                bool bold = r == 0 || r == 5;
                foreach (string v in defaultRows[r])
                    rList.Add(new SheetCell(v, bold, bg));
                _matrix.Add(rList);
            }
        }

        private void LoadInvoiceTemplate()
        {
            _matrix.Clear();
            _numCols = 4;
            _matrix.Add(new List<SheetCell> { new("No Invoice", true, Theme.SHEET_HEAD), new("Klien", true, Theme.SHEET_HEAD), new("Jatuh Tempo", true, Theme.SHEET_HEAD), new("Total Tagihan", true, Theme.SHEET_HEAD) });
            _matrix.Add(new List<SheetCell> { new("INV-001"), new("PT Depup Tech"), new("28 Okt 2026"), new("15000000") });
            _matrix.Add(new List<SheetCell> { new("INV-002"), new("CV Jaya Abadi"), new("30 Okt 2026"), new("8500000") });
            _matrix.Add(new List<SheetCell> { new("TOTAL", true, Theme.SHEET_TOTAL), new("", false, Theme.SHEET_TOTAL), new("", false, Theme.SHEET_TOTAL), new("=SUM(D2:D3)", true, Theme.SHEET_TOTAL) });
        }

        private void LoadPresensiTemplate()
        {
            _matrix.Clear();
            _numCols = 4;
            _matrix.Add(new List<SheetCell> { new("Nama Karyawan", true, Theme.SHEET_HEAD), new("Hadir", true, Theme.SHEET_HEAD), new("Izin", true, Theme.SHEET_HEAD), new("Status", true, Theme.SHEET_HEAD) });
            _matrix.Add(new List<SheetCell> { new("Budi Pratama"), new("22"), new("0"), new("Lengkap") });
            _matrix.Add(new List<SheetCell> { new("Siti Aminah"), new("21"), new("1"), new("Izin Sakit") });
            _matrix.Add(new List<SheetCell> { new("Andi Wijaya"), new("22"), new("0"), new("Lengkap") });
        }

        private void BuildUi(string initialTitle)
        {
            // Top Bar
            var topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = Theme.S(58),
                BackColor = Theme.SURFACE,
                Padding = new Padding(Theme.S(14), Theme.S(10), Theme.S(14), Theme.S(10))
            };

            var btnBack = Theme.CreateButton("← Kembali", false, Theme.TEXT, Theme.S(36), Theme.S(102));
            btnBack.Location = new Point(Theme.S(14), Theme.S(11));
            btnBack.Click += (s, e) => Close();
            topBar.Controls.Add(btnBack);

            _sheetTitleEt = new TextBox
            {
                Text = initialTitle,
                Font = Theme.FontHead(13f),
                ForeColor = Theme.TEXT,
                BackColor = Theme.BG,
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(Theme.S(130), Theme.S(12)),
                Width = Theme.S(340),
                AccessibleName = "Nama lembar kerja"
            };
            _sheetTitleEt.TextChanged += (s, e) => _dirty = true;
            topBar.Controls.Add(_sheetTitleEt);
            topBar.Resize += (s, e) => _sheetTitleEt.Width =
                Math.Max(1, Math.Min(Theme.S(420), topBar.ClientSize.Width - _sheetTitleEt.Left - Theme.S(14)));

            var rightActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                BackColor = Theme.SURFACE,
                Padding = new Padding(Theme.S(12), 0, Theme.S(12), Theme.S(6))
            };

            var btnTemplates = Theme.CreateButton("📊 Templat", false, Theme.TEAL, 36);
            btnTemplates.Click += (s, e) => ShowTemplateMenu(btnTemplates);

            var btnSettings = Theme.CreateButton("⚙️ Pengaturan", false, Theme.TEAL, 36);
            btnSettings.Click += (s, e) => ShowExcelSettingsDialog();

            var btnVoice = Theme.CreateButton("🔊 Bacakan Tabel", false, Theme.INDIGO, 36);
            btnVoice.Click += (s, e) =>
            {
                string t = string.IsNullOrWhiteSpace(_sheetTitleEt.Text) ? "Spreadsheet Excel" : _sheetTitleEt.Text.Trim();
                VoiceReaderManager.ShowVoiceReaderDialog(this, t, GetReadableGridText());
            };

            var btnPrint = Theme.CreateButton("🖨️ Cetak", false, Theme.ACCENT, 36);
            btnPrint.Click += (s, e) =>
            {
                string t = string.IsNullOrWhiteSpace(_sheetTitleEt.Text) ? "Spreadsheet Excel" : _sheetTitleEt.Text.Trim();
                PrintHelper.PrintSpreadsheet(this, t, GetExportRows(), _matrix.Select((r, idx) => (r, idx)).Where(x => x.r.Any(c => c.IsBold)).Select(x => x.idx).ToHashSet());
            };

            var btnExport = Theme.CreateButton("Ekspor lembar kerja", true, Theme.TEAL, Theme.S(36));
            btnExport.Click += (s, e) => SaveSpreadsheet();

            rightActions.Controls.AddRange(new Control[] { btnTemplates, btnSettings, btnVoice, btnPrint, btnExport });

            // Formula Bar
            var formulaBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 44,
                BackColor = Theme.SURFACE,
                Padding = new Padding(14, 6, 14, 6)
            };

            _activeCellPill = new Label
            {
                Text = "A1",
                Font = Theme.FontLabel(10f),
                ForeColor = Theme.TEAL,
                BackColor = Theme.TEAL_BG,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(48, 28),
                Location = new Point(14, 8)
            };
            formulaBar.Controls.Add(_activeCellPill);

            var lblFx = new Label
            {
                Text = "fx",
                Font = Theme.FontLabel(10.5f, FontStyle.Bold | FontStyle.Italic),
                ForeColor = Theme.MUTED,
                AutoSize = true,
                Location = new Point(70, 12)
            };
            formulaBar.Controls.Add(lblFx);

            _formulaEt = new TextBox
            {
                Font = Theme.FontMono(10.5f),
                ForeColor = Theme.TEXT,
                BackColor = Theme.SURFACE2,
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(100, 9),
                Width = 880,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                PlaceholderText = "Masukkan teks, angka, atau rumus (=SUM(D2:D5), =AVERAGE(B2:B5), =B2*C2)"
            };
            _formulaEt.TextChanged += (s, e) =>
            {
                if (_updatingUi) return;
                EnsureCell(_activeRow, _activeCol);
                _matrix[_activeRow][_activeCol].ValStr = _formulaEt.Text;
                _dirty = true;
                _grid.Invalidate();
            };
            formulaBar.Controls.Add(_formulaEt);

            // Grid Toolbar
            var gridToolBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                BackColor = Theme.SURFACE,
                Padding = new Padding(14, 5, 14, 5),
                FlowDirection = FlowDirection.LeftToRight
            };

            Button ToolBtn(string label, Action onClick)
            {
                var b = Theme.CreateButton(label, false, Theme.TEXT, 32);
                b.Click += (s, e) => { _dirty = true; onClick(); };
                return b;
            }

            var autoSum = ToolBtn("Jumlah Otomatis", () =>
            {
                string colName = XlsxWriter.ColumnName(_activeCol);
                int endRow = _activeRow;
                if (endRow < 2) { Theme.Toast(this, "Pilih sel di bawah baris angka untuk menjumlahkan."); return; }
                EnsureCell(_activeRow, _activeCol);
                _matrix[_activeRow][_activeCol].ValStr = $"=SUM({colName}2:{colName}{endRow})";
                _matrix[_activeRow][_activeCol].IsBold = true;
                RenderGrid();
            });
            autoSum.Visible = AppStorage.Config.SheetAutoSum;

            gridToolBar.Controls.AddRange(new Control[]
            {
                ToolBtn("+ Baris", () =>
                {
                    _matrix.Add(Enumerable.Range(0, _numCols).Select(_ => new SheetCell("")).ToList());
                    _activeRow = _matrix.Count - 1;
                    RenderGrid();
                }),
                ToolBtn("- Baris", () =>
                {
                    if (_matrix.Count > 1)
                    {
                        _matrix.RemoveAt(_matrix.Count - 1);
                        _activeRow = Math.Clamp(_activeRow, 0, _matrix.Count - 1);
                        RenderGrid();
                    }
                }),
                ToolBtn("+ Kolom", () =>
                {
                    if (_numCols < MaxColumns)
                    {
                        _numCols++;
                        for (int r = 0; r < _matrix.Count; r++)
                            while (_matrix[r].Count < _numCols) _matrix[r].Add(new SheetCell("", r == 0));
                        RenderGrid();
                    }
                }),
                ToolBtn("- Kolom", () =>
                {
                    if (_numCols > 2)
                    {
                        _numCols--;
                        foreach (var row in _matrix)
                            if (row.Count > _numCols) row.RemoveRange(_numCols, row.Count - _numCols);
                        _activeCol = Math.Clamp(_activeCol, 0, _numCols - 1);
                        RenderGrid();
                    }
                }),
                autoSum,
                ToolBtn("Bold Sel", () =>
                {
                    EnsureCell(_activeRow, _activeCol);
                    _matrix[_activeRow][_activeCol].IsBold = !_matrix[_activeRow][_activeCol].IsBold;
                    RenderGrid();
                }),
                ToolBtn("Warna Biru", () =>
                {
                    EnsureCell(_activeRow, _activeCol);
                    _matrix[_activeRow][_activeCol].BgColor = Theme.SHEET_TOTAL;
                    RenderGrid();
                }),
                ToolBtn("Warna Putih", () =>
                {
                    EnsureCell(_activeRow, _activeCol);
                    _matrix[_activeRow][_activeCol].BgColor = Color.White;
                    RenderGrid();
                })
            });

            // DataGridView
            var gridContainer = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                BackColor = Theme.CANVAS
            };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToOrderColumns = false,
                VirtualMode = true,
                RowHeadersWidth = 54,
                ColumnHeadersHeight = 36,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                MultiSelect = false,
                Font = Theme.FontBody(10f)
            };
            _grid.RowTemplate.Height = Theme.S(32);
            _grid.RowPostPaint += (s, e) => TextRenderer.DrawText(e.Graphics, (e.RowIndex + 1).ToString(),
                _grid.RowHeadersDefaultCellStyle.Font ?? _grid.Font,
                new Rectangle(e.RowBounds.X, e.RowBounds.Y, _grid.RowHeadersWidth - Theme.S(10), e.RowBounds.Height),
                _grid.RowHeadersDefaultCellStyle.ForeColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            _grid.CellValueNeeded += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex >= 0) e.Value = DisplayValue(e.RowIndex, e.ColumnIndex);
            };
            _grid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex >= _matrix.Count || e.ColumnIndex < 0 || e.ColumnIndex >= _matrix[e.RowIndex].Count) return;
                var cell = _matrix[e.RowIndex][e.ColumnIndex];
                e.CellStyle!.BackColor = cell.BgColor;
                e.CellStyle.ForeColor = Theme.PAPER_INK;
                e.CellStyle.Font = Theme.FontBody(10f, cell.IsBold ? FontStyle.Bold : FontStyle.Regular);
            };
            _grid.CellEnter += (s, e) =>
            {
                if (_updatingUi || e.RowIndex < 0 || e.ColumnIndex < 0) return;
                _activeRow = e.RowIndex;
                _activeCol = e.ColumnIndex;
                SyncFormulaBar();
            };
            _grid.CellValuePushed += (s, e) =>
            {
                if (_updatingUi) return;
                EnsureCell(e.RowIndex, e.ColumnIndex);
                _matrix[e.RowIndex][e.ColumnIndex].ValStr = e.Value?.ToString() ?? "";
                _dirty = true;
            };
            _grid.CellEndEdit += (s, e) => { SyncFormulaBar(); _grid.Invalidate(); };
            _grid.EditingControlShowing += (s, e) =>
            {
                if (e.Control is TextBox editor && _grid.CurrentCell is { } cell)
                {
                    editor.Text = _matrix[cell.RowIndex][cell.ColumnIndex].ValStr;
                    editor.SelectAll();
                }
            };

            gridContainer.Controls.Add(_grid);

            Controls.Add(gridContainer);
            Controls.Add(gridToolBar);
            Controls.Add(formulaBar);
            Controls.Add(rightActions);
            Controls.Add(topBar);
        }

        private void EnsureCell(int r, int c)
        {
            while (_matrix.Count <= r)
                _matrix.Add(Enumerable.Range(0, _numCols).Select(_ => new SheetCell("")).ToList());
            while (_matrix[r].Count <= c)
                _matrix[r].Add(new SheetCell(""));
        }

        private void SyncFormulaBar()
        {
            EnsureCell(_activeRow, _activeCol);
            _updatingUi = true;
            _formulaEt.Text = _matrix[_activeRow][_activeCol].ValStr;
            _activeCellPill.Text = $"{XlsxWriter.ColumnName(_activeCol)}{_activeRow + 1}";
            _updatingUi = false;
        }

        private void RenderGrid()
        {
            _updatingUi = true;
            _grid.SuspendLayout();
            _grid.CellBorderStyle = _currentGridlines ? DataGridViewCellBorderStyle.Single : DataGridViewCellBorderStyle.None;
            while (_grid.Columns.Count > _numCols)
                _grid.Columns.RemoveAt(_grid.Columns.Count - 1);
            for (int c = _grid.Columns.Count; c < _numCols; c++)
            {
                var col = new DataGridViewTextBoxColumn
                {
                    HeaderText = XlsxWriter.ColumnName(c),
                    Width = Theme.S(136),
                    MinimumWidth = Theme.S(68),
                    SortMode = DataGridViewColumnSortMode.NotSortable
                };
                _grid.Columns.Add(col);
            }
            _grid.AutoSizeColumnsMode = _numCols <= 6 ? DataGridViewAutoSizeColumnsMode.Fill : DataGridViewAutoSizeColumnsMode.None;
            _grid.RowCount = _matrix.Count;
            if (_activeRow < _grid.Rows.Count && _activeCol < _grid.Columns.Count)
                _grid.CurrentCell = _grid.Rows[_activeRow].Cells[_activeCol];
            _grid.ResumeLayout(true);
            _updatingUi = false;
            SyncFormulaBar();
            _grid.Invalidate();
        }

        // ================= EVALUATOR RUMUS (PORTING FormulaParser SheetEditorActivity.kt) =================

        private double? CellNumber(int r, int c)
        {
            if (r < 0 || r >= _matrix.Count || c < 0 || c >= _matrix[r].Count) return null;
            string raw = _matrix[r][c].ValStr.Trim();
            if (string.IsNullOrEmpty(raw)) return null;
            if (!raw.StartsWith("=")) return ParseNumber(raw);

            string key = $"{r}:{c}";
            if (_visitingCells.Count >= 256 || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack() ||
                !_visitingCells.Add(key)) return null;
            try
            {
                var parser = new FormulaParser(this, raw.Substring(1));
                double val = parser.Parse();
                return (double.IsNaN(val) || double.IsInfinity(val)) ? null : val;
            }
            catch
            {
                return null;
            }
            finally
            {
                _visitingCells.Remove(key);
            }
        }

        private double? ReferencedValue(int row, int column)
        {
            double? value = CellNumber(row, column);
            if (!value.HasValue && row >= 0 && row < _matrix.Count && column >= 0 && column < _matrix[row].Count &&
                _matrix[row][column].ValStr.TrimStart().StartsWith("="))
                throw new ArgumentException("Referensi mengandung rumus yang tidak valid.");
            return value;
        }

        private double? ParseNumber(string raw)
        {
            string s = string.IsNullOrWhiteSpace(_currentCurrency) ? raw.Trim() :
                raw.Trim().Replace(_currentCurrency, "", StringComparison.OrdinalIgnoreCase);
            s = s
                .Replace("Rp", "", StringComparison.OrdinalIgnoreCase)
                .Replace("$", "")
                .Replace("€", "")
                .Replace(" ", "");
            if (string.IsNullOrEmpty(s) || !s.Any(char.IsDigit)) return null;
            if (!Regex.IsMatch(s, @"^[+\-0-9.,]+$")) return null;
            if (s.Length > 1 && s.StartsWith("0") && !s.StartsWith("0.") && !s.StartsWith("0,")) return null;

            bool hasDot = s.Contains('.');
            bool hasComma = s.Contains(',');
            if (hasDot && hasComma)
            {
                s = s.LastIndexOf(',') > s.LastIndexOf('.')
                    ? s.Replace(".", "").Replace(',', '.')
                    : s.Replace(",", "");
            }
            else if (hasComma)
            {
                int dec = s.Substring(s.LastIndexOf(',') + 1).Length;
                s = (dec is >= 1 and <= 2 && s.Count(ch => ch == ',') == 1) ? s.Replace(',', '.') : s.Replace(",", "");
            }
            else if (hasDot)
            {
                int dec = s.Substring(s.LastIndexOf('.') + 1).Length;
                int groups = s.Count(ch => ch == '.');
                s = (groups == 1 && dec != 3) ? s : s.Replace(".", "");
            }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
        }

        private string DisplayValue(int r, int c)
        {
            if (r < 0 || r >= _matrix.Count || c < 0 || c >= _matrix[r].Count) return "";
            string raw = _matrix[r][c].ValStr;
            if (!raw.TrimStart().StartsWith("=")) return raw;
            double? val = CellNumber(r, c);
            return val.HasValue ? val.Value.ToString("#,0.##########", _idCulture) : "#ERROR!";
        }

        private sealed class FormulaParser
        {
            private readonly SheetEditorForm _owner;
            private readonly string _src;
            private int _pos = 0;
            private int _depth;

            public FormulaParser(SheetEditorForm owner, string src)
            {
                _owner = owner;
                _src = src;
            }

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
                if (_depth >= 64) throw new ArgumentException("Rumus bertingkat terlalu dalam.");
                _depth++;
                try
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
                finally { _depth--; }
            }

            private object Unary()
            {
                bool negative = false;
                SkipSpace();
                while (_pos < _src.Length && _src[_pos] is '-' or '+')
                {
                    if (_src[_pos++] == '-') negative = !negative;
                    SkipSpace();
                }
                object value = Primary();
                return negative ? -Scalar(value) : value;
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
                    if (_pos >= _src.Length || _src[_pos] != ')') throw new ArgumentException("Kurung penutup diperlukan.");
                    _pos++;
                    return v;
                }
                if (char.IsDigit(ch) || ch == '.') return ReadNumber();
                if (ch == '$')
                {
                    _pos++;
                    if (_pos >= _src.Length || !char.IsAsciiLetter(_src[_pos])) throw new ArgumentException("Referensi sel tidak valid.");
                    return ReadNameOrCell();
                }
                if (char.IsLetter(ch)) return ReadNameOrCell();
                throw new ArgumentException($"Karakter tak dikenal: {ch}");
            }

            private double ReadNumber()
            {
                int start = _pos;
                while (_pos < _src.Length && (char.IsDigit(_src[_pos]) || _src[_pos] == '.')) _pos++;
                return double.Parse(_src.Substring(start, _pos - start), CultureInfo.InvariantCulture);
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
                    if (!int.TryParse(_src.AsSpan(digitsStart, _pos - digitsStart), out int r) || r is < 1 or > 1_048_576)
                        throw new ArgumentException("Baris sel tidak valid.");
                    int rowIdx = r - 1;
                    int colIdx = ColumnIndex(name);
                    SkipSpace();
                    if (_pos < _src.Length && _src[_pos] == ':')
                    {
                        _pos++;
                        var end = ReadCellReference();
                        if (end == null) throw new ArgumentException("Akhir rentang tidak valid.");
                        return RangeValues(rowIdx, colIdx, end.Value.Row, end.Value.Col);
                    }
                    return _owner.ReferencedValue(rowIdx, colIdx) ?? 0.0;
                }

                SkipSpace();
                if (_pos >= _src.Length || _src[_pos] != '(') throw new ArgumentException("Nama sel tidak valid.");
                _pos++;
                var args = new List<object>();
                SkipSpace();
                if (_pos < _src.Length && _src[_pos] == ')')
                {
                    _pos++;
                }
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
                if (string.IsNullOrEmpty(letters) || _pos == digitsStart) return null;
                if (!int.TryParse(_src.AsSpan(digitsStart, _pos - digitsStart), out int r) || r is < 1 or > 1_048_576)
                    return null;
                int row = r - 1;
                return (row, ColumnIndex(letters));
            }

            private static int ColumnIndex(string letters)
            {
                int n = 0;
                foreach (char ch in letters)
                {
                    if (!char.IsAsciiLetter(ch)) throw new ArgumentException("Kolom sel tidak valid.");
                    n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
                    if (n > MaxColumns) throw new ArgumentException("Kolom sel di luar rentang Excel.");
                }
                return n - 1;
            }

            private List<double> RangeValues(int r1, int c1, int r2, int c2)
            {
                int top = Math.Min(r1, r2), bottom = Math.Min(_owner._matrix.Count - 1, Math.Max(r1, r2));
                int left = Math.Min(c1, c2), right = Math.Min(_owner._numCols - 1, Math.Max(c1, c2));
                var list = new List<double>();
                for (int r = top; r <= bottom; r++)
                {
                    for (int c = left; c <= right; c++)
                    {
                        double? v = _owner.ReferencedValue(r, c);
                        if (v.HasValue) list.Add(v.Value);
                    }
                }
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
                    "ROUND" => Math.Round(values.ElementAtOrDefault(0), (int)values.ElementAtOrDefault(1), MidpointRounding.AwayFromZero),
                    "INT" => Math.Floor(values.FirstOrDefault()),
                    _ => throw new NotSupportedException($"Fungsi {name} belum didukung.")
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

        private void ShowTemplateMenu(Control anchor)
        {
            var menu = new ContextMenuStrip();
            void ReplaceTemplate(Action initialize)
            {
                _grid.EndEdit();
                if (_dirty && MessageBox.Show(this, "Ganti lembar kerja dan buang perubahan yang belum disimpan?",
                    "Templat Spreadsheet", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                initialize();
                _activeRow = _activeCol = 0;
                _dirty = true;
                RenderGrid();
            }
            menu.Items.Add("Laporan Keuangan & Sales", null, (s, e) => ReplaceTemplate(SeedDefaultMatrix));
            menu.Items.Add("Invoice / Tagihan", null, (s, e) => ReplaceTemplate(LoadInvoiceTemplate));
            menu.Items.Add("Presensi & Daftar Hadir", null, (s, e) => ReplaceTemplate(LoadPresensiTemplate));
            menu.Items.Add("Kosongkan Tabel", null, (s, e) => ReplaceTemplate(() =>
            {
                _matrix.Clear();
                _numCols = 4;
                for (int r = 0; r < 8; r++) _matrix.Add(Enumerable.Range(0, _numCols).Select(_ => new SheetCell("")).ToList());
            }));
            menu.Closed += (s, e) => menu.Dispose();
            menu.Show(anchor, new Point(0, anchor.Height));
        }

        private void ShowExcelSettingsDialog()
        {
            using var dlg = new Form
            {
                Text = "Pengaturan Excel & CSV",
                Size = new Size(400, 250),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Theme.SURFACE
            };

            var lblCurr = new Label { Text = "Simbol Mata Uang:", Font = Theme.FontLabel(10f), Location = new Point(20, 24), AutoSize = true };
            var cmbCurr = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(175, 22), Width = 185, Font = Theme.FontBody(10f) };
            cmbCurr.Items.AddRange(new object[] { "Rp", "$", "€" });
            cmbCurr.SelectedItem = _currentCurrency;

            var chkGrid = new CheckBox
            {
                Text = "Tampilkan Garis Kisi Tabel (Gridlines)",
                Checked = _currentGridlines,
                Font = Theme.FontLabel(10f),
                Location = new Point(20, 74),
                AutoSize = true
            };

            var btnSave = Theme.CreateButton("Terapkan", true, Theme.TEAL, 38, 340);
            btnSave.Location = new Point(20, 136);
            btnSave.Click += (s, e) =>
            {
                _currentCurrency = cmbCurr.SelectedItem?.ToString() ?? "Rp";
                _currentGridlines = chkGrid.Checked;
                AppStorage.Config.SheetCurrencySymbol = _currentCurrency;
                AppStorage.Config.SheetShowGridlines = _currentGridlines;
                AppStorage.SaveConfig();
                RenderGrid();
                dlg.Close();
            };

            dlg.Controls.AddRange(new Control[] { lblCurr, cmbCurr, chkGrid, btnSave });
            Theme.ScaleRawDialog(dlg);
            dlg.ShowDialog(this);
        }

        private List<List<string>> GetExportRows()
        {
            var rows = new List<List<string>>();
            for (int r = 0; r < _matrix.Count; r++)
            {
                var row = new List<string>(_numCols);
                for (int c = 0; c < _numCols; c++)
                    row.Add(DisplayValue(r, c));
                rows.Add(row);
            }
            return rows;
        }

        private string GetReadableGridText()
        {
            var sb = new StringBuilder();
            for (int r = 0; r < _matrix.Count; r++)
            {
                var parts = new List<string>();
                for (int c = 0; c < Math.Min(_numCols, _matrix[r].Count); c++)
                {
                    string val = DisplayValue(r, c);
                    if (!string.IsNullOrWhiteSpace(val))
                        parts.Add($"Kolom {XlsxWriter.ColumnName(c)}: {val}");
                }
                if (parts.Count > 0)
                    sb.Append($"Baris {r + 1}: ").Append(string.Join(", ", parts)).Append('\n');
            }
            return sb.ToString().Trim();
        }

        private Dictionary<(int Row, int Column), (string Expression, double Value)> GetExportFormulas()
        {
            var formulas = new Dictionary<(int, int), (string, double)>();
            for (int row = 0; row < _matrix.Count; row++)
            {
                for (int column = 0; column < _numCols; column++)
                {
                    string raw = _matrix[row][column].ValStr.Trim();
                    if (!raw.StartsWith("=") || CellNumber(row, column) is not double value) continue;
                    string expression = Regex.Replace(raw[1..].Replace(';', ','), @"\bAVG(?=\s*\()", "AVERAGE", RegexOptions.IgnoreCase);
                    formulas.Add((row, column), (expression, value));
                }
            }
            return formulas;
        }

        private void SaveSpreadsheet()
        {
            if (!_grid.EndEdit()) return;
            string title = string.IsNullOrWhiteSpace(_sheetTitleEt.Text) ? "Spreadsheet_Excel" : _sheetTitleEt.Text.Trim();
            string defaultName = _sourceName == null ? AppStorage.BaseName(title) : _sourceName + "_edit";

            using var sfd = new SaveFileDialog
            {
                Title = "Ekspor Spreadsheet (.xlsx / .csv)",
                InitialDirectory = AppStorage.OutDir,
                FileName = $"{defaultName}.xlsx",
                Filter = "Excel Workbook (*.xlsx)|*.xlsx|CSV Comma Delimited (*.csv)|*.csv"
            };
            if (sfd.ShowDialog(this) != DialogResult.OK) return;

            string outPath = sfd.FileName;
            string pdfPath = Path.ChangeExtension(outPath, ".pdf");
            if (File.Exists(pdfPath))
                pdfPath = Path.Combine(Path.GetDirectoryName(outPath)!,
                    $"{Path.GetFileNameWithoutExtension(outPath)}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.pdf");

            try
            {
                var exportRows = GetExportRows();
                var boldRows = _matrix.Select((r, idx) => (r, idx)).Where(x => x.r.Any(c => c.IsBold)).Select(x => x.idx).ToHashSet();

                bool csv = outPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
                string separator = AppStorage.Config.SheetCsvSeparator is ";" or "\t" ? AppStorage.Config.SheetCsvSeparator : ",";
                var formulas = csv ? null : GetExportFormulas();
                int columns = _numCols;
                if (!Ui.Busy(this, "Menyimpan lembar kerja…", _ =>
                {
                    string temp = outPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        if (csv)
                        {
                            var csvLines = exportRows.Select(row => string.Join(separator, row.Select(cell => $"\"{cell.Replace("\"", "\"\"")}\"")));
                            File.WriteAllLines(temp, csvLines, Encoding.UTF8);
                        }
                        else XlsxWriter.Write(temp, title, exportRows, boldRows, columns, formulas);
                        File.Move(temp, outPath, overwrite: true);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                })) return;

                _dirty = false;
                AppStorage.AddRecent(outPath);
                AppStorage.SetLastJob("Spreadsheet Diekspor", outPath);

                string pdfText = $"LAPORAN SPREADSHEET EXCEL\nJudul: {title}\n\n" +
                                 string.Join("\n", exportRows.Select(r => string.Join("  |  ", r)));
                if (!Ui.Busy(this, "Membuat salinan PDF…", _ => PdfEngine.TextToPdf(pdfText, pdfPath))) return;
                AppStorage.AddRecent(pdfPath);

                MessageBox.Show(
                    this,
                    $"Spreadsheet berhasil diekspor:\n• {outPath}\n\nSalinan PDF juga dibuat:\n• {pdfPath}",
                    "Spreadsheet Berhasil Diekspor",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Gagal mengekspor spreadsheet: {ex.Message}", "Gagal", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _grid.EndEdit();
            if (_dirty && e.CloseReason == CloseReason.UserClosing &&
                MessageBox.Show(this, "Tutup spreadsheet tanpa menyimpan perubahan?", "Editor Spreadsheet",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                e.Cancel = true;
            base.OnFormClosing(e);
        }
    }
}
