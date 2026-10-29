# Catatan Perbaikan (v2.2.0 → fixed_2)

1. **PDF ber-password bisa dibuka.** Pembaca PDF diganti dengan mesin PDF bawaan Windows (`Windows.Data.Pdf`,
   file `Core/PdfRenderer.cs`). Saat membuka PDF terenkripsi, muncul dialog password; password yang benar diingat
   selama aplikasi berjalan sehingga Gabung/Pisah/Kompres/Susun/TTD/PDF→JPG/Cetak tidak meminta ulang.
   PDF biasa (teks, vektor, hasil scan) kini dirender asli, bukan hanya mengambil gambar JPEG di dalamnya.
2. **OCR sungguhan.** `PdfEngine.RunOcr` sebelumnya mengembalikan teks contoh; kini memakai OCR offline bawaan Windows
   (`Windows.Media.Ocr`, file `Core/WinOcrRunner.cs`). Dipakai oleh Voice Reader, OCR hasil scan, ekspor Word dari scan,
   dan lapisan teks PDF hasil scan. Butuh paket bahasa OCR Windows (otomatis ada bila bahasa Windows terpasang).
3. **Penyimpanan hanya di Documents.** Semua hasil ke `Documents\DepupOffice`; gambar → `...\Gambar`, salinan/ekspor → `...\Salinan`.
   Tidak ada lagi penulisan ke Downloads maupun Pictures.
4. `TargetFramework` diubah ke `net8.0-windows10.0.19041.0` (perlu untuk API Windows di atas). Restore pertama membutuhkan internet.
