# Depup Office Desktop v4.2.0 — Tema "Glass Navy" + perbaikan

## Tampilan (referensi WPS / MS Word / Adobe Acrobat)
- **Palet baru** (`Core/Theme.cs`): navy gelap berlapis, aksen biru, gradasi biru → ungu untuk elemen utama.
  Isi dokumen (halaman Word/PDF, sel Excel, hasil cetak, tanda tangan) tetap **kertas putih** lewat konstanta `PAPER_*` / `SHEET_*`.
- **Kartu kaca** (`CardPanel`): gradasi kilau halus, tepi tipis, garis sorot atas, garis aksen kiri opsional (`AccentEdge`).
- **Rangka jendela utama**: sidebar gelap dengan penanda aktif, kartu "Disk lokal" (data nyata), top bar (cari + `Ctrl+K`, status Offline, pemakaian disk), dan **status bar** di bawah seperti MS Word.
- Judul bagian bergaya grup ribbon (huruf kapital kecil + garis), ubin "Buka berkas cepat", tag fitur bergaya lencana.
- Editor Word / PDF / Excel & Reader: kanvas gelap di sekeliling halaman putih (seperti Acrobat / Word mode gelap); toolbar Word membungkus ke baris kedua saat jendela sempit.
- **Brankas** dirombak: skala DPI, kartu PIN di tengah, status "Terkunci / Sesi terbuka", tombol "Kunci Brankas", baris berkas responsif.

## Tema otomatis untuk semua jendela
`Theme.InstallAutoTheme()` (dipanggil di `Program.cs`):
- bilah judul gelap (DWM), menu konteks gelap (`DarkToolStripRenderer`), scrollbar gelap;
- kotak isian / tombol / label / tabel bawaan di dialog yang dibuat cepat (`new Form { … }`) otomatis disesuaikan kontrasnya;
- kontrol berlatar **putih eksplisit** dianggap kertas dan tidak disentuh;
- jendela yang lebih besar dari layar otomatis diperkecil & ditengahkan.

## Perbaikan fungsi
- Dialog lama berukuran piksel tetap kini diskalakan sesuai DPI (125% / 150% / 200%) — teks tidak terpotong.
- Galat tak terduga tidak lagi menutup aplikasi (`Application.ThreadException` → pesan ramah).
- Membuka berkas rusak / dokumen terlindungi yang gagal: pesan, bukan crash.
- OCR Edit PDF yang gagal: pesan, bukan crash.
- Brankas: PIN wajib angka 4–6 digit; enkripsi banyak berkas melaporkan yang gagal; hapus berkas tidak crash bila terkunci.
- "Bagikan berkas": aman bila clipboard sedang dipakai aplikasi lain.
- Buka folder hasil di Explorer: path berspasi (mis. `C:\Users\Hype AMD\…`) kini dikutip.
- Pindah tab menggulir halaman kembali ke atas.
- Versi disatukan ke 4.2.0 (`Theme.AppVersion`).
