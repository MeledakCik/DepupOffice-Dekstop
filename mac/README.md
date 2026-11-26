# Depup Office for Mac (v4.4.3)

Versi macOS dari Depup Office: **FREE, 100% offline, tanpa unggahan ke cloud.**
Dibangun dengan **C# .NET 8 + Avalonia UI**. Logika inti (Word, Excel, PDF, QR, Brankas) dipakai bersama
dengan versi Windows di folder `Depup.Shared`.

## Cara menjalankan (sekali pasang, lalu tinggal klik)

1. **Pasang .NET 8 SDK** (pilih salah satu):
   - Homebrew: `brew install --cask dotnet-sdk`
   - Atau unduh installer macOS dari <https://dotnet.microsoft.com/download/dotnet/8.0>
     (pilih **Arm64** untuk Mac M1/M2/M3/M4, **x64** untuk Mac Intel).
2. Buka **Terminal**, masuk ke folder ini, lalu jalankan:

   ```bash
   cd DepupOffice-Dekstop/mac
   ./run.sh                 # langsung jalan dari kode sumber
   ```

3. Buat aplikasi `.app` yang bisa diklik dua kali dari Finder:

   ```bash
   ./build-mac-app.sh       # hasil: mac/build/Depup Office.app
   ```

   Seret **Depup Office.app** ke folder **Applications**. Saat pertama kali dibuka, bila macOS menolak,
   klik kanan → **Open** → **Open**.

> Build pertama butuh internet untuk mengunduh paket NuGet (Avalonia, SkiaSharp, Docnet/PDFium).
> Setelah itu aplikasi berjalan sepenuhnya offline.

**Opsional — OCR & pemindai QR** memakai Apple Vision dan perlu *Xcode Command Line Tools* (gratis):
`xcode-select --install`. Helper Swift kecil dikompilasi otomatis saat fitur ini pertama dipakai.

## Fitur

| Menu | Isi |
| --- | --- |
| **Baca** | PDF (termasuk ber-password), Word `.docx`, Excel `.xlsx`/`.csv`, teks, gambar (JPG/PNG/HEIC). 4 tema kertas Eye-Care (Siang, Malam, Sepia, Hijau), zoom 50–250%, bookmark ★, seret & lepas berkas, pencarian ⌘K. |
| **Tools PDF** | Gabung, Pisah, Kompres (termasuk target ≤ 1 MB / 500 KB / 200 KB untuk SSCASN/CPNS), Gambar → PDF, PDF → JPG, Kunci (AES-128), Buka Kunci, Putar, Susun/Hapus Halaman, OCR, Buat QR, Pindai QR. |
| **Buat** | Templat Word (Kosong, Surat Lamaran, CV) → simpan `.docx` + `.pdf`. Spreadsheet (Kosong, Laporan Keuangan, Invoice, Presensi) dengan rumus `=SUM`, `=AVERAGE`, `=MIN`, `=MAX`, `=COUNT`, `=ABS`, `=ROUND`, `=INT`, `A1:D5` → `.xlsx` (rumus ikut tersimpan), CSV, PDF. |
| **Simpan** | Kelola hasil di `~/Documents/DepupOffice`: buka, tampilkan di Finder, ganti nama, pindah ke Brankas, pindah ke Trash. |
| **Brankas** | Enkripsi AES-256-GCM dengan PIN 4–6 digit. Kunci diikat ke Hardware UUID Mac ini. |
| **Lainnya** | Baca Lantang dengan suara macOS (kecepatan 0,75×–2×, simpan audio), Cetak lewat Preview (⌘P). |

Pintasan: **⌘O** buka berkas, **⌘K** cari, **⌘1–⌘5** pindah menu, **←/→** pindah halaman PDF,
**⌘+ / ⌘−** zoom, **⌘S** simpan di editor, **⌘P** cetak.

## Peta migrasi Windows → macOS

| Windows (`DepupOfficeDesktop`) | macOS (`mac/`) |
| --- | --- |
| Windows Forms + GDI+ | Avalonia UI + SkiaSharp |
| `Windows.Data.Pdf` (render PDF) | PDFium via Docnet.Core (`Services/PdfService.cs`) |
| `Windows.Media.Ocr` | Apple Vision (`Services/VisionService.cs`) |
| Windows SAPI 5 (suara) | Perintah `say` macOS (`Services/MacSystem.cs`) |
| `PrintDialog` | Preview + ⌘P (`Services/DocText.cs` → `PrintService`) |
| `WebBrowser` (privacy policy) | Browser bawaan / berkas offline |
| `Documents\DepupOffice` | `~/Documents/DepupOffice` |
| `%LocalAppData%\DepupOfficeDesktop` | `~/Library/Application Support/DepupOffice` |
| QrManager (tidak sesuai standar untuk teks panjang) | `QrEncoder` ISO/IEC 18004 versi 1–40 (`Depup.Shared/QrEncoder.cs`) |

Kode yang sama dipakai di kedua platform (tanpa API Windows): `OfficeReader`, `DocxWriter`, `XlsxWriter`,
`PdfWriter`, `VaultManager`, `AppStorage`, `PrivacyManager`, plus yang baru: `SheetModel` (rumus),
`DocModel` (dokumen), `PageRange`, `QrEncoder`, `MacPaths`.

## Catatan & batasan

- Seperti versi Windows, alat PDF menyusun ulang halaman sebagai gambar (150 dpi). Bedanya, versi Mac
  **mempertahankan lapisan teks asli**, sehingga hasilnya tetap bisa dicari dan disalin (kecuali saat diputar).
- Editor PDF bergaya Word (`PdfEditorForm` di Windows) dan tanda tangan di PDF **belum** dibawa ke versi Mac.
- Berkas `.doc` / `.xls` lama belum didukung. Simpan ulang sebagai `.docx` / `.xlsx`.
- Kode yang di-compile untuk Windows tetap ada di `DepupOfficeDesktop/` dan tidak diubah.

## Struktur

```
mac/
├── DepupOfficeMac.sln
├── run.sh                    # jalankan dari kode sumber
├── build-mac-app.sh          # buat "Depup Office.app"
├── Depup.Shared/             # inti lintas platform (net8.0, tanpa paket NuGet)
└── DepupOffice.Mac/          # aplikasi Avalonia
    ├── App.cs, Program.cs
    ├── Ui/                   # sistem desain + dialog
    ├── Services/             # PDF, gambar, OCR, suara, cetak, integrasi macOS
    └── Windows/              # Jendela utama, Pembaca, Editor Dokumen, Spreadsheet, QR, dll.
```
