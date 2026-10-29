using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Windows.Forms;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Porting dari PrintHelper.kt:
    /// Cetak langsung PDF, gambar hasil scan/KTP 2 sisi, dokumen Word, dan tabel Spreadsheet Excel
    /// ke printer WiFi, USB, Bluetooth, atau Microsoft Print to PDF menggunakan PrintDocument asli Windows.
    /// </summary>
    public static class PrintHelper
    {
        public static void PrintPdf(Form owner, string filePath, string documentName)
        {
            try
            {
                if (!PdfEngine.EnsureUnlocked(owner, filePath)) return;
                var pages = PdfEngine.RenderAllPages(filePath, 1600);
                if (pages.Count == 0)
                {
                    Theme.Toast(owner, "Tidak ada halaman yang dapat dicetak");
                    return;
                }
                int pageIdx = 0;
                using var pd = new PrintDocument { DocumentName = $"DepupOffice_{documentName}" };
                pd.PrintPage += (s, e) =>
                {
                    if (e.Graphics != null && pageIdx < pages.Count)
                    {
                        var bmp = pages[pageIdx];
                        Rectangle bounds = e.MarginBounds;
                        float scale = Math.Min((float)bounds.Width / bmp.Width, (float)bounds.Height / bmp.Height);
                        int dw = (int)(bmp.Width * scale);
                        int dh = (int)(bmp.Height * scale);
                        int dx = bounds.Left + (bounds.Width - dw) / 2;
                        int dy = bounds.Top + (bounds.Height - dh) / 2;
                        e.Graphics.DrawImage(bmp, new Rectangle(dx, dy, dw, dh));
                    }
                    pageIdx++;
                    e.HasMorePages = pageIdx < pages.Count;
                };

                using var dlg = new PrintDialog { Document = pd, UseEXDialog = true };
                if (dlg.ShowDialog(owner) == DialogResult.OK)
                    pd.Print();

                foreach (var b in pages) b.Dispose();
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, $"Gagal mencetak PDF: {ex.Message}", "Cetak Dokumen", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public static void PrintBitmap(Form owner, Bitmap bitmap, string jobName)
        {
            try
            {
                using var pd = new PrintDocument { DocumentName = jobName };
                pd.PrintPage += (s, e) =>
                {
                    if (e.Graphics != null)
                    {
                        Rectangle bounds = e.MarginBounds;
                        float scale = Math.Min((float)bounds.Width / bitmap.Width, (float)bounds.Height / bitmap.Height);
                        int dw = (int)(bitmap.Width * scale);
                        int dh = (int)(bitmap.Height * scale);
                        int dx = bounds.Left + (bounds.Width - dw) / 2;
                        int dy = bounds.Top + (bounds.Height - dh) / 2;
                        e.Graphics.DrawImage(bitmap, new Rectangle(dx, dy, dw, dh));
                    }
                    e.HasMorePages = false;
                };

                using var dlg = new PrintDialog { Document = pd, UseEXDialog = true };
                if (dlg.ShowDialog(owner) == DialogResult.OK)
                    pd.Print();
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, $"Gagal mencetak gambar: {ex.Message}", "Cetak Gambar", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public static void PrintText(Form owner, string title, string text)
        {
            try
            {
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                int lineIdx = 0;
                using var pd = new PrintDocument { DocumentName = $"DepupOffice_{title}" };
                pd.PrintPage += (s, e) =>
                {
                    if (e.Graphics == null) { e.HasMorePages = false; return; }
                    using var titleFont = new Font("Segoe UI", 16f, FontStyle.Bold);
                    using var bodyFont = new Font("Segoe UI", 11f, FontStyle.Regular);
                    float y = e.MarginBounds.Top;
                    float left = e.MarginBounds.Left;
                    float maxW = e.MarginBounds.Width;

                    if (lineIdx == 0)
                    {
                        e.Graphics.DrawString(title, titleFont, Brushes.Black, left, y);
                        y += 40f;
                    }

                    while (lineIdx < lines.Length && y + 24f <= e.MarginBounds.Bottom)
                    {
                        var rect = new RectangleF(left, y, maxW, e.MarginBounds.Bottom - y);
                        SizeF sz = e.Graphics.MeasureString(lines[lineIdx], bodyFont, (int)maxW);
                        e.Graphics.DrawString(lines[lineIdx], bodyFont, Brushes.DarkSlateGray, rect);
                        y += Math.Max(20f, sz.Height + 4f);
                        lineIdx++;
                    }
                    e.HasMorePages = lineIdx < lines.Length;
                };

                using var dlg = new PrintDialog { Document = pd, UseEXDialog = true };
                if (dlg.ShowDialog(owner) == DialogResult.OK)
                    pd.Print();
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, $"Gagal mencetak dokumen: {ex.Message}", "Cetak Dokumen", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public static void PrintSpreadsheet(Form owner, string title, IReadOnlyList<IReadOnlyList<string>> grid)
        {
            try
            {
                using var pd = new PrintDocument { DocumentName = $"DepupOffice_{title}" };
                pd.PrintPage += (s, e) =>
                {
                    if (e.Graphics == null) return;
                    using var titleFont = new Font("Segoe UI", 15f, FontStyle.Bold);
                    using var cellFont = new Font("Segoe UI", 9.5f, FontStyle.Regular);
                    using var boldFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
                    using var linePen = new Pen(Color.LightGray, 1f);

                    float left = e.MarginBounds.Left;
                    float startY = e.MarginBounds.Top;
                    e.Graphics.DrawString(title, titleFont, Brushes.Black, left, startY);
                    startY += 36f;

                    int maxCols = grid.Count > 0 ? Math.Min(8, grid[0].Count) : 4;
                    float cellW = Math.Max(80f, (float)e.MarginBounds.Width / Math.Max(1, maxCols));
                    float cellH = 26f;

                    int maxRows = Math.Min(grid.Count, 32);
                    for (int r = 0; r < maxRows; r++)
                    {
                        var row = grid[r];
                        for (int c = 0; c < Math.Min(row.Count, maxCols); c++)
                        {
                            float cx = left + c * cellW;
                            float cy = startY + r * cellH;
                            if (r == 0)
                            {
                                using var hb = new SolidBrush(Theme.SURFACE2);
                                e.Graphics.FillRectangle(hb, cx, cy, cellW, cellH);
                            }
                            e.Graphics.DrawRectangle(linePen, cx, cy, cellW, cellH);
                            string val = row[c] ?? "";
                            if (val.Length > 18) val = val.Substring(0, 16) + "..";
                            e.Graphics.DrawString(val, r == 0 ? boldFont : cellFont, Brushes.Black, cx + 6f, cy + 5f);
                        }
                    }
                    e.HasMorePages = false;
                };

                using var dlg = new PrintDialog { Document = pd, UseEXDialog = true };
                if (dlg.ShowDialog(owner) == DialogResult.OK)
                    pd.Print();
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, $"Gagal mencetak spreadsheet: {ex.Message}", "Cetak Spreadsheet", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
