using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Windows.Forms;
using DepupOfficeDesktop.Forms;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Semua jalur cetak lewat lembar Cetak DepUp (<see cref="PrintSheetForm"/>, port PrintSheet.kt):
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
                var (_, document) = Task.Run(() => PagedDocument.Open(filePath)).GetAwaiter().GetResult();
                if (document == null) throw new InvalidDataException("PDF tidak dapat dibaca.");
                PrintSheetForm.Show(owner, new PagedPrintSource(document, own: true), documentName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, $"Gagal mencetak PDF: {ex.Message}", "Cetak Dokumen", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>Cetak halaman yang sudah dirender (mis. slide PPTX, hasil pindai). Bitmap TIDAK dibuang di sini.</summary>
        public static void PrintPages(Form owner, IReadOnlyList<Bitmap> pages, string documentName)
            => PrintSheetForm.Show(owner, new BitmapPrintSource(pages), documentName);

        /// <summary>Lembar Cetak DepUp untuk dokumen yang sedang dibuka di Reader (dokumen tidak dibuang).</summary>
        internal static void PrintDocumentPages(Form owner, PagedDocument document, string documentName)
            => PrintSheetForm.Show(owner, new PagedPrintSource(document, own: false), documentName);

        public static void PrintBitmap(Form owner, Bitmap bitmap, string jobName)
        {
            try { PrintSheetForm.Show(owner, new BitmapPrintSource(new[] { bitmap }), jobName); }
            catch (Exception ex)
            {
                MessageBox.Show(owner, $"Gagal mencetak gambar: {ex.Message}", "Cetak Gambar", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public static void PrintText(Form owner, string title, string text)
        {
            try { PrintSheetForm.Show(owner, new TextPrintSource(title, text), title); }
            catch (Exception ex)
            {
                MessageBox.Show(owner, $"Gagal mencetak dokumen: {ex.Message}", "Cetak Dokumen", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Cetak lembar kerja UTUH (port SheetPrint.kt Android 4.3): semua baris &amp; kolom berisi, nilai rumus, lebar kolom
        /// mengikuti isi, dipecah per halaman dengan judul kolom diulang. <paramref name="boldRows"/> = baris tebal (judul kolom).
        /// </summary>
        public static void PrintSpreadsheet(Form owner, string title, IReadOnlyList<IReadOnlyList<string>> grid, ISet<int>? boldRows = null)
        {
            try
            {
                var doc = new SheetLayout.Doc(title, new[] { SheetLayout.FromRows(title, grid, boldRows) });
                PrintSheetForm.Show(owner, new SheetPrintSource(doc), title);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, $"Gagal mencetak spreadsheet: {ex.Message}", "Cetak Spreadsheet", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
