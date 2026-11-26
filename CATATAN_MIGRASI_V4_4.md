# Catatan Migrasi Android v4.4.2 → Desktop v4.4.2

Sumber: `DepupOffice-main` (Android 4.4.2 / 40402, commit `310709a`). Target: `DepupOfficeDesktop.sln` (WinForms .NET 8).

## Fitur baru / diselaraskan
| Fitur | Berkas desktop | Port dari Android |
| :--- | :--- | :--- |
| **Dokumen Terlindungi v3**: kunci dari kode akses (PBKDF2-SHA256 600.000 → HKDF → AES-256-GCM + HMAC). Dokumen buatan PC bisa dibuka di Android/iPhone dan sebaliknya. v1 (4.1.0) tetap terbaca. Kode akses bisa diingat (DPAPI Windows), "Lupakan kode akses" di Reader. | `Core/SecurePackage.cs`, `Core/ProtectedDoc.cs`, `Forms/MainForm.Features.cs` | `SecurePackage.kt`, `ProtectedDoc.kt`, `AccessCodeUi.kt` |
| **Catatan**: teks & daftar centang, 7 warna kertas tempel, sematkan, cari, kisi masonry, korsel di Beranda. Format JSON v1 sama dengan Android. | `Core/Notes.cs`, `Forms/NotesForm.cs` | `Notes.kt`, `NoteViews.kt`, `NotesActivity.kt`, `NoteEditActivity.kt` |
| **Lembar Cetak DepUp**: pratinjau, pilih halaman ("1-3, 5" / klik gambar), salinan tersusun, kertas A4/F4/Letter/Legal/A5, warna / hitam putih, bolak-balik, pilih printer. | `Forms/PrintSheetForm.cs`, `Core/PrintPages.cs`, `Core/PrintHelper.cs` | `PrintSheet.kt`, `PrintPages.kt` |
| **Cetak spreadsheet utuh**: semua baris & kolom, lebar kolom mengikuti isi, mendatar/diperkecil otomatis, kolom dibagi per halaman, judul kolom diulang. | `Core/SheetLayout.cs` | `SheetLayout.kt`, `SheetPrint.kt` |
| **Cari isi dokumen** langsung dari kolom cari (tab Dokumen), hasil bertambah selagi berjalan. | `Forms/MainForm.Ui.cs`, `Core/DocTools.cs` | `DocSearch.kt` |
| **Sematkan berkas di Beranda** (menu ⋯ / klik kanan). | `Forms/MainForm.Ui.cs`, `Core/AppStorage.cs` | `Meta.pin`, `pinCard` |
| **Buka dari link** (Google Drive/Docs, share.google, LMS): tempel link, Ctrl+L, atau seret link dari browser. Hanya https, maks. 100 MB, jenis berkas dari isinya. | `Core/LinkFetch.cs` | `LinkFetch.kt` |
| **Ringkasan**: judul pendek terpisah, baris PDF disambung, isi halaman berulang dihitung sekali. | `Core/DocTools.cs` | `DocumentSummarizerManager.kt` |
| **Transisi ganti tema** (crossfade ±0,3 dtk, ikut setelan animasi Windows). | `Controls/ThemeFade.cs` | `ThemeFade.kt` |
| Kebijakan privasi resmi `depupstudio.com/office/privacy-policy`, isi diperbarui. Filter scan bawaan **Warna asli**. Versi 4.4.2. | `privacy.html`, `Core/PrivacyManager.cs` | `assets/privacy.html` |

## Tidak dibawa (khusus HP)
Widget layar utama, pembaruan wajib Google Play/App Store, pemeriksaan tanda tangan APK native, efek kaca cair GPU, geser baris (diganti menu klik kanan).

## Belum diport
- Auto-crop scan yang dilatih ulang (`DocFind.kt` ±1.400 baris + `ScanCore.kt`): desktop masih memakai `ScanAi` (potong persegi).
  Perlu editor scan berbasis segi-empat bebas agar setara.

## Verifikasi
- Vektor referensi v3 lintas platform (`SecurePackageTest.kt`) identik: 315 byte, SHA-256 `d3fd862f…a843`; semua 315 posisi byte yang diubah ditolak.
- Uji baru: `NotesTests`, `PrintLayoutTests`, `LinkFetchTests`, `SummarizerTests`, `MigrationTests` (v3). Logika murni diuji di macOS; jalankan `dotnet test` di Windows untuk uji penuh.
- **Belum**: uji klik UI di Windows.
