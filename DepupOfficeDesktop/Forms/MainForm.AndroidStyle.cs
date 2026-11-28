using System;
using System.Collections.Generic;
using System.Drawing;
using DepupOfficeDesktop.Controls;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Forms
{
    /// <summary>
    /// Ikon &amp; warna tiap fitur SAMA dengan aplikasi Android (MainActivity.kt: tile / createRow / actionRow). Judul fitur
    /// desktop dipetakan ke pasangan ikon + warna yang dipakai Android untuk fitur yang sama.
    /// </summary>
    public partial class MainForm
    {
        private enum Tone { Pdf, Doc, Xls, Img, Primary, Success, Error, Amber }

        private static (Color fg, Color bg) ToneColors(Tone t) => t switch
        {
            Tone.Pdf => (Theme.RED, Theme.RED_BG),           // C.PDF_FG / PDF_BG
            Tone.Doc => (Theme.SKY, Theme.SKY_BG),           // C.DOC_FG / DOC_BG
            Tone.Xls => (Theme.TEAL, Theme.TEAL_BG),         // C.XLS_FG / XLS_BG
            Tone.Img => (Theme.INDIGO, Theme.INDIGO_BG),     // C.IMG_FG / IMG_BG
            Tone.Success => (Theme.SUCCESS, Theme.SUCCESS_BG),
            Tone.Error => (Theme.ERROR, Theme.ERROR_BG),
            Tone.Amber => (Theme.AMBER, Theme.AMBER_BG),     // C.AMBER / AMBER_BG
            _ => (Theme.ACCENT, Theme.BLUE_BG),              // C.PRIMARY / PRIMARY_C
        };

        private static readonly Dictionary<string, (CardIcon icon, Tone tone)> AndroidStyle = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Ambil gambar"] = (CardIcon.ImagePdf, Tone.Pdf),
            ["Anti-kopi & anti-cetak"] = (CardIcon.Lock, Tone.Primary),
            ["Baca Lantang"] = (CardIcon.Speaker, Tone.Primary),
            ["Baca lantang"] = (CardIcon.Speaker, Tone.Primary),
            ["Brankas pribadi"] = (CardIcon.Lock, Tone.Primary),
            ["CV / Resume"] = (CardIcon.Person, Tone.Doc),
            ["Cari teks dalam file"] = (CardIcon.Search, Tone.Primary),
            ["Catatan"] = (CardIcon.Note, Tone.Amber),
            ["Tulis catatan"] = (CardIcon.Note, Tone.Amber),
            ["Daftar centang"] = (CardIcon.Checklist, Tone.Amber),
            ["Cetak"] = (CardIcon.Printer, Tone.Primary),
            ["Coret-coret PDF"] = (CardIcon.Bright, Tone.Pdf),
            ["Daftar Hadir"] = (CardIcon.Excel, Tone.Xls),
            ["Dokumen Terlindungi"] = (CardIcon.Shield, Tone.Primary),
            ["Dokumen Word"] = (CardIcon.Word, Tone.Doc),
            ["Dukung developer"] = (CardIcon.Heart, Tone.Error),
            ["EXE ke PDF"] = (CardIcon.Pdf, Tone.Doc),
            ["Edit PDF"] = (CardIcon.Edit, Tone.Pdf),
            ["Gabung PDF"] = (CardIcon.Merge, Tone.Pdf),
            ["Gabung campuran"] = (CardIcon.Merge, Tone.Pdf),
            ["Gambar ke PDF"] = (CardIcon.Image, Tone.Doc),
            ["Grafik dari Excel"] = (CardIcon.Excel, Tone.Xls),
            ["Hapus halaman kosong"] = (CardIcon.Crop, Tone.Pdf),
            ["Jadwal & Agenda"] = (CardIcon.Note, Tone.Primary),
            ["Katalog Produk"] = (CardIcon.Word, Tone.Doc),
            ["Kirim masukan"] = (CardIcon.Mail, Tone.Primary),
            ["Kompres PDF Presisi"] = (CardIcon.Compress, Tone.Pdf),
            ["Kompres foto"] = (CardIcon.Image, Tone.Img),
            ["Konversi massal"] = (CardIcon.Grid, Tone.Img),
            ["Kuitansi / Invoice"] = (CardIcon.Excel, Tone.Xls),
            ["Kunci berkas"] = (CardIcon.Lock, Tone.Primary),
            ["Label Harga & Barcode"] = (CardIcon.Qr, Tone.Primary),
            ["Nomor Halaman PDF"] = (CardIcon.Crop, Tone.Pdf),
            ["Nota / Kuitansi Toko"] = (CardIcon.Excel, Tone.Xls),
            ["Office ke PDF"] = (CardIcon.Pdf, Tone.Doc),
            ["PDF bisa dicari"] = (CardIcon.Search, Tone.Doc),
            ["PDF dari teks"] = (CardIcon.Note, Tone.Primary),
            ["PDF ke JPG"] = (CardIcon.Image, Tone.Pdf),
            ["PDF ke Word"] = (CardIcon.Word, Tone.Doc),
            ["Password PDF AES-256"] = (CardIcon.Lock, Tone.Primary),
            ["Pembaca PDF"] = (CardIcon.Pdf, Tone.Pdf),
            ["Pembersih bayangan foto"] = (CardIcon.Wand, Tone.Img),
            ["Pembuat Pasfoto"] = (CardIcon.Person, Tone.Img),
            ["Pindai dokumen"] = (CardIcon.Scan, Tone.Primary),
            ["Pindai kartu nama"] = (CardIcon.Person, Tone.Img),
            ["Pindai struk"] = (CardIcon.Scan, Tone.Xls),
            ["Pindai tabel ke Excel"] = (CardIcon.Excel, Tone.Xls),
            ["Pisah PDF"] = (CardIcon.Cut, Tone.Pdf),
            ["Privasi & tentang aplikasi"] = (CardIcon.Shield, Tone.Success),
            ["Pustaka TTD & Stempel"] = (CardIcon.Signature, Tone.Success),
            ["QR Code"] = (CardIcon.Qr, Tone.Primary),
            ["Ringkas dokumen"] = (CardIcon.Note, Tone.Primary),
            ["Salin untuk dibagikan"] = (CardIcon.Share, Tone.Primary),
            ["Simpan salinan"] = (CardIcon.Download, Tone.Primary),
            ["Slideshow presentasi"] = (CardIcon.Pdf, Tone.Doc),
            ["Spreadsheet Excel"] = (CardIcon.Excel, Tone.Xls),
            ["Surat Lamaran Kerja"] = (CardIcon.Word, Tone.Doc),
            ["Susun halaman"] = (CardIcon.Crop, Tone.Pdf),
            ["TTD transparan"] = (CardIcon.Signature, Tone.Img),
            ["Tanda tangan PDF"] = (CardIcon.Signature, Tone.Success),
            ["Watermark PDF"] = (CardIcon.Wand, Tone.Pdf),
            ["Watermark foto massal"] = (CardIcon.Wand, Tone.Img),
            ["Pilih berkas dulu"] = (CardIcon.Upload, Tone.Primary),
            // presentasi (desktop; warna lencana PPTX = amber)
            ["Presentasi"] = (CardIcon.ImagePdf, Tone.Amber),
            ["Presentasi PowerPoint"] = (CardIcon.ImagePdf, Tone.Amber),
            ["Presentasi Tugas"] = (CardIcon.ImagePdf, Tone.Amber),
            ["Proposal Usaha"] = (CardIcon.ImagePdf, Tone.Amber),
            ["Edit PPTX"] = (CardIcon.Edit, Tone.Amber),
        };

        /// <summary>Ikon &amp; warna Android untuk judul fitur; null bila judul tidak dikenal (gaya pemanggil dipakai).</summary>
        private static (CardIcon icon, Color fg, Color bg)? StyleFor(string title)
        {
            if (!AndroidStyle.TryGetValue(title.Trim(), out var st)) return null;
            var (fg, bg) = ToneColors(st.tone);
            return (st.icon, fg, bg);
        }
    }
}
