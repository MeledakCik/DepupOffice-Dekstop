# Catatan Migrasi Android v4.1.0 → Desktop v4.1.0

Sumber: `C:\Project-App\DepupOffice` (Android, Kotlin, v4.1.0). Target: `DepupOfficeDesktop.sln` (WinForms .NET 8).
Tetap **tanpa NuGet baru** dan 100% offline. Scanner/kamera tetap tidak dibawa (sesuai keputusan v2.3.0).

## Fondasi baru (Core)
| Berkas | Port dari Android | Isi |
| :--- | :--- | :--- |
| `Core/PdfObjects.cs` | `PdfParse.kt` | Parser PDF: xref klasik & stream, object stream, Flate/LZW/A85/AHx/RL, perbaikan xref rusak, dekripsi RC4/AES-128/AES-256 |
| `Core/PdfSave.cs` | `PdfSave.kt` | Tulis ulang PDF **tanpa render ulang**, kunci AES-256 (R6) + izin, `PdfComposer` (gabung/pisah/susun lossless) |
| `Core/PdfCanvas.cs` | `android.graphics.pdf.PdfDocument` | Kanvas PDF vektor: teks Helvetica asli (bisa dicari), bentuk, juring, gambar JPEG/PNG transparan |
| `Core/ProtectedDoc.cs` | `ProtectedDoc.kt`, `Sx.kt`, `Integrity.kt`, `DocxProtect.kt` | Dokumen Terlindungi (format identik Android/iOS), honeypot, deteksi debugger/hook, pembersih prompt-injection, proteksi Word |
| `Core/PdfToolsX.cs` | `PdfTools.kt`, `PdfPageNumberingManager`, `PdfProtectionManager`, `ProtectedDocumentManager`, `UniversalMergeManager` | Nomor halaman, watermark, anti-kopi/cetak, password AES-256, gabung campuran, PDF→Word |
| `Core/BizGenerators.cs` | `InvoiceMaker`, `CatalogMaker`, `SchedulePlanner`, `BarcodePriceTag`, `IdPhoto`, `ExcelChartGenerator` | Nota, katalog, jadwal, barcode Code128/EAN-13 (pengganti ZXing), pasfoto ukuran asli, grafik |
| `Core/DocTools.cs` | `DocFilter`, `BusinessCard`, `TableToExcel`, `BatchWatermark`, `DocumentSummarizer`, `GlobalSearch` | Penjernih bayangan, kartu nama→VCF, tabel foto→Excel, watermark foto, ringkasan, cari isi berkas |
| `Core/PptxRenderer.cs` | `PptxHtml.kt` | Perender PPTX ke gambar (GDI+): latar, master/layout, placeholder, teks bertingkat, gambar, grup, tabel |
| `Core/SecureDisplay.cs` | `SecureGuard.kt` (FLAG_SECURE) | `SetWindowDisplayAffinity`: jendela tidak ikut tangkapan/rekam/berbagi layar |

## UI baru
- `Forms/MainForm.Features.cs` — semua alur fitur baru; `Forms/FeatureUi.cs` — dialog formulir + proses latar berprogres.
- `Forms/SlideshowForm.cs` — slideshow layar penuh PDF/PPTX (transisi, laser pointer, timer, auto-play, layar hitam).
- `Forms/ViewerForm.cs` — kini membuka **.pptx**, tombol **▶ Slideshow**, dan **mode aman** Dokumen Terlindungi.
- Tile baru: **Tools → Alat PDF Lanjutan** (8), **Buat → Generator Bisnis & UMKM** (10), **Baca → Produktivitas** (3).

## Perubahan perilaku
- Gabung / Pisah / Susun PDF kini **lossless** (teks & vektor asli tetap); metode render lama dipakai otomatis bila struktur PDF tidak terbaca.
- Nomor halaman & watermark PDF ditimpa di atas halaman asli (Android merender ulang jadi gambar).
- Pasfoto: ganti latar memakai flood-fill dari tepi (baju/wajah yang mirip warna latar tidak ikut berubah) dan lembar A4 dicetak dalam **ukuran cm asli**.
- Salinan terdekripsi Dokumen Terlindungi ada di `%LocalAppData%\DepupOfficeDesktop\secure_tmp`, ditimpa & dihapus saat Reader ditutup dan saat aplikasi dibuka.

## Batasan yang diketahui
- PDF→Word memakai OCR offline Windows per halaman (jalur `toWordOcr` Android). Konverter tata-letak penuh Android (`PdfToDocx.kt` + `PdfLayout.kt`, ±1.700 baris) belum diport.
- Dokumen Terlindungi skema lama **v1** (kunci terikat model HP Android) tidak bisa dibuka di PC; skema v2 (sekarang) bisa.
- PPTX: grafik/SmartArt tampil sebagai kotak penanda; animasi & transisi PowerPoint tidak diputar.
- Deteksi tamper desktop terbatas pada debugger & pustaka hook yang dimuat (tidak ada padanan verifikasi tanda tangan APK).

## Verifikasi yang sudah dilakukan
- Vektor uji `SecurityTest.kt` Android cocok (rahasia, prefiks `DepUpReadKey:v2:`, sanitasi "Halodunia").
- Dokumen Terlindungi: protect → open identik byte, password guru benar/salah, dokumen yang diubah 1 bit ditolak.
- PDF hasil (kunci AES-256, gabung, nomor halaman, watermark, label) lolos `qpdf --check`; izin terbaca benar oleh qpdf; teks tetap bisa diekstrak `pdftotext`.
- Barcode Code 128 & EAN-13 identik dengan pustaka referensi dan terbaca pemindai `zbar`.
- Perender PPTX diuji visual (judul, bullet bertingkat, bentuk, gambar, tabel).
- Unit test baru: `DepupOfficeDesktop.Tests/MigrationTests.cs` (jalankan `dotnet test`).
- **Belum**: uji klik UI di Windows.

## QC di Windows (18 Nov 2026)
Build penuh 0 warning/0 error; 11/11 unit test; smoke test 67/67 pada PDF nyata (12–1200 halaman), DOCX & PPTX.
- **Diperbaiki — AES-256 R6 tidak didukung mesin PDF Windows** (`Windows.Data.Pdf` menolak R6 walau password benar; R5/AES-128/RC4 didukung). Akibatnya hasil Password PDF, Anti-Kopi & Dokumen Terlindungi (termasuk dari Android) tak bisa dibuka di Reader. `PdfRenderer` kini mendekripsi di memori lewat `PdfFile` lalu memberikan salinan polos ke Windows (tidak ditulis ke disk; `PdfRenderer.Forget` dipanggil saat Dokumen Terlindungi ditutup). Uji: `WindowsRendererOpensAes256R6Output`.
- **Diperbaiki — skala DPI**: `Windows.Data.Pdf` menafsirkan ukuran tujuan dalam DIP, jadi di layar 140% semua render 1,4× lebih besar (≈2× memori/waktu). Faktor kini dipelajari & dikompensasi.
- **Diperbaiki — memori Reader**: ganti zoom/tema membocorkan salinan bitmap tiap halaman; Reader & Slideshow kini memakai `PdfEngine.BudgetDim` (PDF ratusan halaman diturunkan resolusinya, ≤160 halaman tidak berubah). Manual 1200 halaman: ±16 GB → 1,9 GB.
- **Dioptimasi**: PDF→Word & OCR Cari/Ringkas memproses per halaman (`PdfEngine.ForEachPage`), `Thumbnail` merender 1 halaman, cetak PPTX langsung dari slide (tanpa PDF sementara yang tertinggal).
- Saran berikutnya: lazy-loading halaman di Reader (render hanya yang terlihat) untuk PDF sangat besar.
