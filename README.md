# 🚀 Depup Office Desktop v2.2.0 (Microsoft Visual Studio Edition)

> **Depup Office Desktop v2.2.0 - FREE, 100% Offline, Zero Cloud Upload. Donasi sukarela via https://saweria.co/depupdonate (tidak membuka fitur premium, diproses di luar aplikasi)**

Hasil migrasi penuh aplikasi Android **DepupOffice v2.2.0** (`cloud.depup.office`) menjadi aplikasi desktop native Windows menggunakan **C# .NET 8.0 (Windows Forms + GDI+)** untuk **Microsoft Visual Studio 2022 / 2026**.

Seluruh fitur utama versi Android dipertahankan **100% offline tanpa dependensi library/NuGet pihak ketiga** (*Zero External NuGet Dependencies*):

* **Tanpa Internet / Zero Cloud Upload**: Semua pemrosesan dokumen, PDF, OCR, QR Code, TTS, dan enkripsi berjalan 100% lokal di PC.
* **Privacy Policy Online & Offline Fallback**: Dilengkapi `PrivacyPolicyForm` berbasis `WebBrowser` bawaan .NET yang memuat `https://depupoffice-privacy.vercel.app/` saat online dan otomatis beralih (*fallback*) ke `Documents\DepupOffice\privacy.html` saat offline.
* **Donasi Saweria & Kontak Resmi Compliant**: Seluruh tautan donasi (`https://saweria.co/depupdonate`), email (`mailto:depupdev@gmail.com`), dan repositori (`https://github.com/MeledakCik/DepupOffice`) dibuka di browser/aplikasi eksternal bawaan OS via `Process.Start`.
* **Siap Buka & Build di Visual Studio**: Cukup buka `DepupOfficeDesktop.sln` di Microsoft Visual Studio dan tekan **F5** (`Start`).

## 📂 Struktur Solusi Visual Studio & Peta Migrasi (Kotlin Android ➔ C# .NET Desktop)

| Modul Android / Fitur | Modul Visual Studio (`C# .NET 8`) | Deskripsi Fitur yang Dimigrasikan |
| ----- | ----- | ----- |
| `MainActivity.kt` | `Forms/MainForm.cs` | Navigasi 4 menu utama (`Baca`, `Tools`, `Buat`, `Simpan`) + `MenuStrip` (`Simpan` & `Help`), item menu `Privacy Policy`, `Dukung Developer ❤️` (`https://saweria.co/depupdonate`), `Kontak Developer` (`mailto:depupdev@gmail.com`), dialog `About`, pencarian langsung, Drag-and-Drop PDF/Gambar, 12 Alat PDF & Dokumen, dan Manajer Folder `Documents\DepupOffice` |
| `PrivacyPolicy` (Web & Offline) | `Forms/PrivacyPolicyForm.cs`, `Core/PrivacyManager.cs` & `privacy.html` | Jendela Privacy Policy dengan toolbar `← Back` dan judul `"Privacy Policy - Depup Office"`, memakai kontrol `WebBrowser` bawaan .NET (`Zero NuGet`), memuat `https://depupoffice-privacy.vercel.app/` dengan fallback offline otomatis ke `Documents\DepupOffice\privacy.html`, serta membuka link eksternal (`saweria.co/depupdonate`, `mailto:depupdev@gmail.com`, `github.com/MeledakCik/DepupOffice`) via `Process.Start` |
| `DocEditorActivity.kt` | `Forms/DocEditorForm.cs` | Editor dokumen Word (`.docx`) WYSIWYG per bagian (Judul Bab, Paragraf, Catatan), Bold/Italic/Align, Templat (`Surat Lamaran Kerja`, `CV Profesional`, `Kosong`), pengaturan kertas (`A4`, `Letter`, `F4`), margin, spasi, Voice Reader, Cetak, dan ekspor ganda `.docx` + `.pdf` |
| `SheetEditorActivity.kt` | `Forms/SheetEditorForm.cs` | Editor Spreadsheet Excel (`.xlsx` & `.csv`) dengan Formula Bar (`fx`), parser rumus rekursif (`=SUM`, `=AVERAGE`, `=MIN`, `=MAX`, `=COUNT`, `=ABS`, `=ROUND`, `=INT`, operasi `A1:D5`), Templat (`Laporan Keuangan`, `Invoice`, `Presensi`), dan ekspor `.xlsx` + `.csv` + `.pdf` |
| `ViewerActivity.kt` | `Forms/ViewerForm.cs` | Pembaca multi-format (`.pdf`, `.docx`, `.xlsx`, `.csv`, `.txt`, gambar) dengan **4 Tema Kertas Eye-Care** (`Siang`, `Malam #0F172A`, `Sepia #FBF0D9`, `Eye-Care Hijau #E2F0D9`), Zoom `50%–250%`, Bookmark, Tanda Tangan PDF, Voice Reader, dan Cetak |
| `ScanEditActivity.kt` & `CameraActivity.kt` | `Forms/ScanEditForm.cs` & `Controls/CropCanvasControl.cs` | Studio Smart Scan & Edit: kanvas potong 8-titik dengan kaca pembesar (*Magnifier Loupe 2.5x*), **Auto-Crop AI** (Otsu + BFS + Sobel), **Deskew** (Luruskan teks), 4 Preset Filter (`Magic Color`, `B&W Dokumen`, `Grayscale`, `Warna Asli`), Sensur Hitam NIK, Cap Watermark Diagonal, **Tata KTP 2-Sisi ke 1 Lembar A4**, OCR Offline + Terjemahan EN↔ID + Deteksi Struk ke Excel, dan Modal Simpan gaya WPS |
| `VaultActivity.kt` | `Forms/VaultForm.cs` & `Core/VaultManager.cs` | Brankas Pribadi Terenkripsi **AES-256-GCM** dengan autentikasi PIN 4–6 digit (`SHA-256` + *salt*) dan pembungkus kunci lokal `PBKDF2` |
| `DocxWriter.kt` & `XlsxWriter.kt` | `Core/DocxWriter.cs` & `Core/XlsxWriter.cs` | Generator OpenXML `.docx` dan `.xlsx` murni (`System.IO.Compression.ZipArchive`) tanpa library pihak ketiga |
| `OfficeReader.kt` | `Core/OfficeReader.cs` | Pembaca dokumen `.docx`, `.xlsx` (termasuk `sharedStrings.xml`), dan `.csv` tanpa library pihak ketiga |
| `PdfWriter.kt`, `Pdf.kt`, `Export.kt` & `Ocr.kt` | `Core/PdfWriter.cs` & `Core/PdfEngine.cs` | Penulis biner **PDF 1.6** dengan stream gambar JPEG (`DCTDecode`), lapisan teks OCR (`BT 3 Tr`), enkripsi password **AES-128 (`AESV2`)**, Gabung, Pisah, Kompres Target SSCASN/CPNS, Susun Halaman, Filter `ToDocument`, dan Ekstrak TTD Transparan |
| `ScanAi.kt` & `QrManager.kt` | `Core/ScanAi.cs` & `Core/QrManager.cs` | Deteksi tepi dokumen otomatis (`DetectCrop` & `DetectSkew`) serta Generator & Pemindai Kode QR standar **ISO/IEC 18004** (Reed-Solomon) |
| `VoiceReaderManager.kt` & `PrintHelper.kt` | `Core/VoiceReaderManager.cs` & `Core/PrintHelper.cs` | Pembaca suara dokumen offline memakai **Windows SAPI 5** bawaan OS (`0.75x`–`2.0x`, `id-ID` & `en-US`) dan pencetakan langsung via `PrintDialog` Windows |
| `SignatureView.kt`, `Ui.kt` & `Util.kt` | `Controls/SignaturePadControl.cs`, `Controls/CustomControls.cs`, `Core/Theme.cs` & `Core/AppStorage.cs` | Sistem desain *Stitch Depup PDF System*, kanvas tanda tangan transparan, serta manajemen konfigurasi & folder `Documents\DepupOffice` |

## 🛡️ Kebijakan Privasi & Kepatuhan Donasi (v2.2.0)

* **Pernyataan Resmi**: `Depup Office Desktop v2.2.0 - FREE, 100% Offline, Zero Cloud Upload. Donasi sukarela via https://saweria.co/depupdonate (tidak membuka fitur premium, diproses di luar aplikasi)`
* **URL Privacy Policy Resmi**: `https://depupoffice-privacy.vercel.app/`
* **Fallback Offline Otomatis**: `Documents\DepupOffice\privacy.html` (dikonfigurasi di `DepupOfficeDesktop.csproj` dengan `CopyToOutputDirectory = PreserveNewest` / *Copy if newer* dan otomatis dibuat oleh `PrivacyManager.EnsurePrivacyFileExists()`).
* **Email Kontak Developer**: `depupdev@gmail.com` (`mailto:depupdev@gmail.com`)
* **Link Donasi Resmi**: `https://saweria.co/depupdonate`

## 🛠️ Cara Membuka & Menjalankan di Microsoft Visual Studio

### Prasyarat

* **Microsoft Visual Studio 2022** (versi 17.8+) atau **Visual Studio 2026**
* Workload: **.NET desktop development** (sudah menyertakan **.NET 8.0 SDK**)

### Langkah 1 — Buka Solusi di Visual Studio

1. Ekstrak seluruh isi arsip `DepupOfficeDesktop-VisualStudio.zip` ke sebuah folder di komputer Anda.
2. Klik dua kali file **`DepupOfficeDesktop.sln`** untuk membukanya di **Microsoft Visual Studio**.
3. Tekan **`F5`** (atau klik tombol hijau **Start / DepupOfficeDesktop** di toolbar atas) untuk melakukan kompilasi dan menjalankan aplikasi desktop secara langsung tanpa perlu koneksi internet.

### Langkah 2 — Cara Membuat File `.EXE` Mandiri (Standalone Single-File Executable)

1. Melalui **Visual Studio UI**:
   * Klik kanan pada proyek **`DepupOfficeDesktop`** di *Solution Explorer* ➔ pilih **Publish...**
   * Pilih target **Folder** ➔ klik **Next** ➔ klik **Finish**.
   * Klik **Show all settings** pada halaman profil Publish, lalu atur:
     * **Configuration**: `Release | Any CPU`
     * **Target Runtime**: `win-x64`
     * **Deployment Mode**: `Self-contained`
     * Centang **Produce single file** (`PublishSingleFile`)
   * Klik **Save**, lalu klik tombol **Publish**. File `privacy.html` akan otomatis ikut disalin ke folder output publish dan `Documents\DepupOffice\privacy.html`.

2. Atau melalui **Terminal / Developer Command Prompt**:

   ```bat
   dotnet publish DepupOfficeDesktop\DepupOfficeDesktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
   ```

   File siap pakai akan tersedia di folder `publish\DepupOffice.exe` beserta `publish\privacy.html`.

## 📁 Lokasi Penyimpanan Lokal di Windows

* **Folder Dokumen Utama (`OutDir`)**: `C:\Users\<NamaUser>\Documents\DepupOffice`
* **File Privacy Policy Offline**: `C:\Users\<NamaUser>\Documents\DepupOffice\privacy.html`
* **Folder Gambar & QR (`PicturesDir`)**: `C:\Users\<NamaUser>\Documents\DepupOffice\Gambar`
* **Folder Salinan (`DownloadsDir`)**: `C:\Users\<NamaUser>\Documents\DepupOffice\Salinan`
* **Folder Brankas Terenkripsi AES-256 (`VaultDir`)**: `%LocalAppData%\DepupOfficeDesktop\vault`
#   D e p u p O f f i c e - D e k s t o p  
 