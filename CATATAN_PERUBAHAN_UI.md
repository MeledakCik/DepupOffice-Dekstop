# Perbaikan Tampilan Jendela Utama (Depup Office Desktop)

## Penyebab tampilan berantakan
Ukuran di kode ditulis dalam piksel tetap (tinggi 30 / 36 / 56 px, dst.), sedangkan font mengikuti skala DPI Windows.
Di layar 125% - 200% teks jadi lebih besar daripada kotaknya, sehingga terpotong (judul, tombol "Dukung",
"Privacy", "Kontak", item menu kiri, dan baris tab di bagian atas).

## File yang berubah
- `Core/Theme.cs`            : `Theme.S()` / `Theme.SetDpi()` (skala DPI), `FontLabel` tidak lagi di-bold dua kali, `CreateButton` memakai `PillButton`.
- `Controls/CustomControls.cs`: `CardPanel` (hover, border putus-putus), `PillButton`, `Pill`, `NavItem`, `SearchBox`, `StackedStorageBar` membulat.
- `Forms/MainForm.cs`        : kini `partial`; konstruktor DPI-aware, Ctrl+K, reflow saat maximize/restore, handler pindah monitor.
- `Forms/MainForm.Ui.cs`     : BARU. Seluruh rangka (sidebar, top bar) dan 4 halaman (Baca, Tools, Buat, Simpan).

## Perubahan tampilan (mengikuti mockup)
- Sidebar: logo teks "Depup Office", tombol Smart Scan, 4 menu dengan penanda aktif, tautan bawah.
- Top bar: judul halaman, kolom pencarian (Ctrl+K), badge penyimpanan lokal, avatar. Menu strip lama dihapus (isinya sudah ada di sidebar).
- Baca: hero Smart Scan, Format Dokumen, Fitur Cepat (Baca Lantang, Cetak Langsung, QR & Barcode), filter chip, daftar file terakhir.
- Tools: zona seret & lepas putus-putus, kartu proses terakhir dengan progress, grid 12 alat.
- Buat & Simpan: grid responsif, kartu brankas bergaya amber, daftar dokumen terproteksi.
- Grid kartu menyesuaikan jumlah kolom dengan lebar jendela. Drag & drop berkas kini juga berfungsi di atas area konten.

## Belum disentuh
Jendela lain (ViewerForm, ScanEditForm, DocEditorForm, SheetEditorForm, VaultForm) dan dialog di dalam MainForm
(Folder Explorer, QR, About, Rename, dll.) masih memakai ukuran piksel tetap. Tombolnya sudah ikut gaya baru,
tetapi tata letaknya belum diskalakan DPI.

---

# Perbaikan Tahap 2: Ikon Kartu, Responsif, Anti Tab/Jendela Ganda

## 1. Ikon kartu (selain sidebar)
- BARU `Controls/CardIcons.cs`: enum `CardIcon`, kontrol `IconChip`, dan `CardIconPainter` (ikon vektor GDI+, 26 ikon).
  Tajam di semua DPI dan tidak lagi memakai teks ("PDF", "◌", "↔") atau emoji sebagai ikon.
- `MainForm.Ui.cs`: puluhan fungsi glyph bertingkat yang saling memanggil dihapus, diganti `ResolveCardIcon(title)` dan `IconForFileBadge(...)`.
- Dipakai pada: kartu fitur (Baca/Tools/Buat), kartu aksi, daftar File Terakhir, kartu Brankas, dan baris Dokumen Terproteksi.

## 2. Responsif
- Tile fitur: tinggi menyesuaikan teks (maks 3 baris), lencana tag disembunyikan bila tidak muat, semua tile dalam satu grid disamakan tingginya.
- Manajer Folder: 4 tombol kategori jadi 2x2 saat sempit; tombol aksi menumpuk bila perlu.
- Kartu Privasi/Dukungan/Kontak: tiga tombol menumpuk vertikal saat sempit (tidak terpotong).
- Memperbaiki satu tanda `}` berlebih di `MainForm.Ui.cs` yang menutup class lebih awal.

## 3. Tidak ada lagi buka ganda
Penyebab: `OpenDocument` bersifat `async void` dan menunggu simpan riwayat sebelum menampilkan viewer.
Klik ganda / drop ganda pada jeda itu membuka dua viewer bertumpuk.
- `OpenDocument`: flag `_openingDocument` menolak permintaan kedua selama berkas sedang dibuka.
- `RunGuarded(...)`: semua kartu/tombol mengabaikan klik ulang dalam 600 ms atau saat aksi masih berjalan.
- Drag & drop: drop identik dalam 1,5 detik diabaikan, dan penanganannya ditunda (`BeginInvoke`) keluar dari callback OLE.

---

# Perbaikan Tahap 3: Reader tertanam (tanpa jendela kedua) + toolbar Reader responsif

- `MainForm.cs`: `OpenDocument` kini memanggil `ShowViewerEmbeddedAsync`. Reader ditanam di dalam area kanan jendela utama
  (menutupi konten, sidebar tetap terlihat), BUKAN lagi `ShowDialog` ke jendela baru. Tombol "Kembali", tombol Esc,
  pindah menu sidebar, atau mengetik di kolom pencarian akan menutup Reader.
- `ViewerForm.cs`: toolbar dirombak jadi 3 baris yang membungkus otomatis (header, aksi, status/zoom), semua ukuran
  memakai `Theme.S()` sehingga tidak terpotong di layar 125% - 200%. Dialog turunan memakai jendela utama sebagai pemilik.
- `MainForm.Ui.cs`: lencana "Lokal: ... terpakai" di top bar selalu diposisikan dulu sebelum ditampilkan
  (sebelumnya bisa nyasar ke pojok kiri).

Belum ditanam: editor Word/Excel, studio Scan, Brankas, dan dialog hasil masih berupa jendela modal.
