# Depup Office Desktop v4.2.0 — tampilan sama dengan Android, versi desktop

Sumber desain: app Android `C:\Project-App\DepupOffice` — `Ui.kt` (object `C`, `Theme`), `MainActivity.kt`, `res/font`, `res/drawable/ic_logo_tile.xml`.

## Sama dengan Android
- **Token warna** (`Core/Theme.cs`) diambil persis dari `Ui.kt`: latar abu batu `#F1F0EC`, kartu putih rata tanpa gradasi,
  aksen biru royal `#1652F6`, tinta `#111111`, warna jenis berkas (PDF terakota, Word biru batu, Excel hijau sage, Gambar lavender).
- **Tema terang & gelap**: *Ikuti Windows / Terang / Gelap* di **Saya › Tampilan** (Android: *Ikuti HP / Terang / Gelap*),
  plus tombol bulan/matahari di bilah atas. Seluruh jendela dibangun ulang saat tema berganti; bilah judul Windows ikut terang/gelap.
- **Huruf Funnel Display** (Regular / Medium / SemiBold) dibawa dari Android ke `Assets/Fonts` (lisensi SIL OFL 1.1).
- **Logo asli**: ubin biru `#0037FF` + tanda "D" digambar dari path vektor Android (`LogoTile`).
- **Komponen**: kartu rata radius besar, tombol pil (utama / tonal), kolom cari pil, ubin alat (ikon di wadah sewarna + lencana),
  daftar berkelompok dengan chevron & garis pemisah (`ListRow` + list card), segmen (`Segmented`), chip filter, kartu kosong.
- **Struktur layar** mengikuti Android: **Beranda · Dokumen · Buat · Alat · Saya** (tombol Scan di tengah diganti tombol biru *Edit PDF*,
  karena scanner tidak dibawa ke desktop).
  - Beranda: sapaan "Selamat siang, / mau kerjakan apa?", kartu biru, ubin angka, *Buka berkas*, *Buat baru*, *Terakhir dibuka*.
  - Dokumen: segmen *Terkini / Ditandai / Hasil olahan* + chip kategori; mengetik di kolom cari langsung ke sini.
  - Alat: *Pilih berkas dulu*, lalu *Kelola PDF · Konversi · Gambar & foto · Tanda tangan & keamanan · Baca & bagikan*.
  - Saya: Keamanan (Brankas, Privasi), Penyimpanan, Tampilan (tema), Format bawaan, Dukungan.

## Khusus desktop
- Navigasi samping (padanan navigasi bawah), grid 3–4 kolom, dua kolom berdampingan di layar lebar, status bar bawah.
- Seret berkas ke jendela; Ctrl+K untuk cari; Esc menutup Reader.
- Editor Word/PDF/Excel: kertas putih di atas kanvas, toolbar membungkus ke baris kedua bila jendela sempit.
- Semua dialog otomatis ikut tema (bilah judul, menu, kotak isian, tombol); jendela yang lebih besar dari layar dipaskan.

## Perbaikan fungsi
- Dialog berukuran piksel tetap diskalakan DPI (125% / 150% / 200%).
- Galat tak terduga tidak menutup aplikasi; berkas rusak / OCR gagal / clipboard sibuk ditangani dengan pesan.
- Brankas: PIN wajib angka 4–6 digit, enkripsi banyak berkas melaporkan yang gagal, hapus tidak crash.
- Tombol bulat bilah atas (tema & Saya) memakai event `Pressed` sendiri (klik mouse sebelumnya tidak terbaca).
- Path berspasi (`C:\Users\Hype AMD\…`) dikutip saat membuka Explorer.
