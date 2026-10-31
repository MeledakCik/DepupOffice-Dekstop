# Catatan v2.3.0: Editor PDF + scanner dihapus

## Dihapus (scanner / kamera)
- `Forms/ScanEditForm.cs`, `Controls/CropCanvasControl.cs`, `Core/ScanAi.cs` (Smart Scan, Auto-Crop, Deskew, KTP 2-sisi).
- Tombol/kartu "Smart Scan", hero scanner, tile "Scan Struk & Nota" diganti ke Editor PDF.
- Kode pipeline scan di `PdfEngine.cs` (ScanPage, RenderScan, ExportPdf/Docx/Jpg, RenderKtpA4) masih ada sebagai kode mati; aman dihapus nanti setelah build lolos.

## Ditambah: `Forms/PdfEditorForm.cs`
- Buka PDF (termasuk ber-password) atau gambar; thumbnail halaman, zoom, Ctrl+roda mouse.
- Edit Teks Asli: klik baris teks -> OCR offline Windows -> teks lama ditutup, teks baru bisa diketik langsung.
- Teks Baru (font, ukuran, B/I/U, warna, rata), Gambar, Tanda Tangan, Sorot, Tutup/Hapus (warna latar diambil otomatis), Kotak.
- Pilih/Geser/ubah ukuran objek, Hapus, Urungkan/Ulangi (Ctrl+Z / Ctrl+Y), Simpan (Ctrl+S).
- Entry point: tombol sidebar "Edit PDF", hero Baca & Buat, tile Tools, tombol "Edit PDF" di Reader.

## Batasan teknis
Halaman dirender jadi gambar lalu disusun ulang jadi PDF baru (berkas asli tidak diubah). Teks asli menjadi gambar;
opsi OCR saat simpan menambahkan lapisan teks tak terlihat agar hasil bisa dicari/disalin.
