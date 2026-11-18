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

## Efek kaca & zoom
- **Efek kaca (blur)** — padanan "Efek kaca (blur)" Android (`Controls/Glass.cs`): latar aurora lembut di belakang jendela utama,
  sidebar / bilah atas / status bar dan semua kartu menjadi kaca buram semi-transparan dengan tepi sorot. Windows 11: bilah judul memakai Mica.
  Bisa dimatikan di **Saya › Tampilan › Efek kaca** (*Kaca blur / Rata*).
- **Zoom antarmuka seperti VS Code**: `Ctrl +` perbesar, `Ctrl −` perkecil, `Ctrl 0` kembali 100%. Juga di **Saya › Tampilan › Ukuran tampilan**
  dan di status bar (klik = 100%). Bawaan 90% agar komponen tidak terlalu besar. Tersimpan otomatis.

### Perbaikan gulir, zoom & performa
- Latar kaca area konten kini **ikut bergulir** bersama isi (`GlassScrollPanel`): geseran piksel Windows saat menggulir selalu
  cocok, tidak ada lagi sisa gambar / tampilan dobel. Halaman tidak lagi melompat sendiri saat fokus berpindah.
- Latar aurora disimpan beresolusi rendah (1/4 dan 1/24) lalu diperbesar halus — jauh lebih ringan memori & CPU.
- Ganti zoom / tema / ukuran jendela membangun ulang tampilan tanpa kedip (penggambaran ditahan sampai selesai).
- Layar lebar: konten diletakkan di tengah dengan lebar baca maksimum; grid & kolom menyesuaikan lebar.

### Responsif & optimasi lanjutan
- **Jendela sempit** (dibagi dua layar, laptop kecil, atau zoom besar): sidebar otomatis menjadi **navigasi ringkas ikon saja**
  (seperti navigation rail Android) dengan tooltip; jarak tepi konten & bilah atas mengecil; kartu penyimpanan & versi di status bar disembunyikan.
  Kembali lebar otomatis saat jendela diperbesar. Ukuran jendela minimum kini lebih kecil (720×520).
- **Font di-cache**: tiap ukuran/gaya huruf dibuat sekali lalu dipakai bersama (sebelumnya setiap label & setiap gambar ulang membuat font baru)
  → jauh lebih sedikit handle GDI, ganti tab / zoom / scroll lebih ringan.
- Beranda tidak lagi membaca ulang riwayat & folder hasil dari disk setiap kali jendela diubah ukurannya atau di-zoom.
- Setelah zoom / ganti tema, fokus kembali ke menu (kolom cari tidak ikut aktif sendiri).

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
