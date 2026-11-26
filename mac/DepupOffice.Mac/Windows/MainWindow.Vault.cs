using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DepupOffice.Mac.Services;
using DepupOfficeDesktop.Core;
using static DepupOffice.Mac.Ui;

namespace DepupOffice.Mac.Windows;

/// <summary>Brankas Pribadi (port dari VaultForm): AES-256-GCM + PIN 4–6 digit.</summary>
public sealed partial class MainWindow
{
    private bool _vaultUnlocked;
    private readonly List<string> _vaultTemp = new();

    private void LockVault()
    {
        _vaultUnlocked = false;
        foreach (var dir in _vaultTemp)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
        _vaultTemp.Clear();
    }

    private static bool ValidPin(string? pin) => pin != null && Regex.IsMatch(pin, @"^\d{4,6}$");

    private Control BuildBrankas()
    {
        if (!VaultManager.HasPin())
        {
            var setup = Btn("Buat PIN Brankas", () => _ = SetupPin(), true, Amber);
            return Card(Col(12,
                IconBadge("🔒", Amber, AmberBg, 56),
                T("Brankas belum diaktifkan", 18, Text, FontWeight.Bold),
                T("Simpan dokumen pribadi (KTP, KK, ijazah, slip gaji) dalam bentuk terenkripsi AES-256-GCM. " +
                  "Berkas hanya bisa dibuka dari aplikasi ini setelah memasukkan PIN.", 13, Muted, null, true),
                setup), 28, AmberBg, "#FDE68A");
        }

        if (!_vaultUnlocked)
        {
            var pinBox = Input("PIN (4–6 digit)");
            pinBox.PasswordChar = '•';
            pinBox.Width = 220;
            var error = T("", 12, Red);
            void TryUnlock()
            {
                if (VaultManager.CheckPin(pinBox.Text ?? ""))
                {
                    _vaultUnlocked = true;
                    Navigate("brankas");
                }
                else
                {
                    error.Text = "PIN salah.";
                    pinBox.Text = "";
                }
            }
            pinBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; TryUnlock(); } };
            var unlock = Btn("Buka Brankas", TryUnlock, true, Amber);
            var card = Card(Col(12,
                IconBadge("🔒", Amber, AmberBg, 56),
                T("Brankas terkunci", 18, Text, FontWeight.Bold),
                Caption("Masukkan PIN untuk melihat berkas terenkripsi."),
                Row(8, pinBox, unlock),
                error), 28);
            card.MaxWidth = 560;
            card.HorizontalAlignment = HorizontalAlignment.Left;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => pinBox.Focus());
            return card;
        }

        var root = Col(16);
        var bar = Row(8,
            Btn("＋ Tambah Berkas…", () => _ = PickAndAddToVault(), true, Amber),
            Btn("Ganti PIN", () => _ = ChangePin()),
            Btn("🔒 Kunci", () => { LockVault(); Navigate("brankas"); }));
        root.Children.Add(bar);
        root.Children.Add(Caption("Tip: seret berkas ke jendela ini untuk langsung mengenkripsinya. Berkas yang dibuka dari brankas " +
                                  "didekripsi sementara dan dihapus otomatis saat brankas dikunci."));

        var files = VaultManager.ListEncryptedFiles();
        if (files.Count == 0)
        {
            root.Children.Add(Card(T("Brankas masih kosong.", 13, Muted), 24));
            return root;
        }
        var list = Col(0);
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            string name = VaultManager.DisplayName(f);
            var actions = Row(6,
                SmallBtn("Buka", () => _ = OpenVaultFile(f)),
                SmallBtn("Ekspor", () => _ = ExportVaultFile(f)),
                SmallBtn("🗑", () => _ = DeleteVaultFile(f), Red, "Hapus dari brankas"));
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var text = Col(2, T(name, 13.5, Text, FontWeight.Medium),
                T($"{AppStorage.FmtSize(f.Length)} terenkripsi  •  {f.LastWriteTime:dd MMM yyyy HH:mm}", 12, Muted));
            text.Margin = new Thickness(12, 0);
            Grid.SetColumn(text, 1);
            Grid.SetColumn(actions, 2);
            grid.Children.Add(FileBadge(name));
            grid.Children.Add(text);
            grid.Children.Add(actions);
            var row = new Border
            {
                Padding = new Thickness(12, 10),
                BorderBrush = B(Line),
                BorderThickness = i < files.Count - 1 ? new Thickness(0, 0, 0, 1) : new Thickness(0),
                Child = grid,
            };
            row.DoubleTapped += (_, _) => _ = OpenVaultFile(f);
            list.Children.Add(row);
        }
        root.Children.Add(Card(list, 6));
        return root;
    }

    private async Task<bool> SetupPin()
    {
        string? pin = await Dialogs.Prompt(this, "Buat PIN Brankas", "PIN berupa 4–6 digit angka.", password: true, watermark: "PIN baru");
        if (pin == null) return false;
        if (!ValidPin(pin)) { await Dialogs.Error(this, "PIN harus 4–6 digit angka."); return false; }
        string? again = await Dialogs.Prompt(this, "Ulangi PIN", "Ketik ulang PIN Anda.", password: true, watermark: "Ulangi PIN");
        if (again == null) return false;
        if (again != pin) { await Dialogs.Error(this, "PIN tidak sama."); return false; }
        try { VaultManager.SetPin(pin); }
        catch (Exception ex) { await Dialogs.Error(this, ex.Message); return false; }
        _vaultUnlocked = true;
        if (_page == "brankas") Navigate("brankas");
        return true;
    }

    private async Task ChangePin()
    {
        string? old = await Dialogs.Prompt(this, "Ganti PIN", "Masukkan PIN lama.", password: true, watermark: "PIN lama");
        if (old == null) return;
        if (!VaultManager.CheckPin(old)) { await Dialogs.Error(this, "PIN lama salah."); return; }
        if (await SetupPin()) await Dialogs.Info(this, "PIN diganti", "PIN brankas berhasil diganti.");
    }

    private async Task PickAndAddToVault()
    {
        var files = await Dialogs.PickFiles(this, "Pilih berkas untuk dienkripsi", true, Array.Empty<(string Name, string[] Ext)>());
        if (files.Count > 0) await AddToVault(files);
    }

    private async Task AddToVault(List<string> paths)
    {
        if (!VaultManager.HasPin() && !await SetupPin()) return;
        var done = new List<string>();
        try
        {
            await Dialogs.Busy(this, "Mengenkripsi berkas…", r =>
            {
                foreach (var p in paths)
                {
                    r($"Mengenkripsi {Path.GetFileName(p)}…");
                    VaultManager.EncryptFile(p);
                    done.Add(p);
                }
            });
        }
        catch (Exception ex)
        {
            await Dialogs.Error(this, "Gagal mengenkripsi: " + ex.Message);
        }
        if (done.Count == 0) return;

        bool remove = await Dialogs.Confirm(this, "Berkas terenkripsi",
            $"{done.Count} berkas sudah masuk brankas. Pindahkan berkas aslinya ke Trash agar tidak tersisa salinan terbuka?",
            "Pindahkan ke Trash", "Biarkan");
        if (remove)
            foreach (var p in done)
            {
                MacSystem.MoveToTrash(p);
                AppStorage.RemoveRecent(p);
            }
        Navigate(_page);
    }

    private async Task OpenVaultFile(FileInfo f)
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "depup_vault_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _vaultTemp.Add(dir);
            string outPath = Path.Combine(dir, VaultManager.DisplayName(f));
            await Task.Run(() => VaultManager.DecryptFile(f.FullName, outPath));
            if (AppStorage.IsSupportedDocument(outPath))
            {
                var viewer = new ViewerWindow(this, outPath, temporary: true);
                viewer.Show();
            }
            else MacSystem.Open(outPath);
        }
        catch (Exception ex)
        {
            await Dialogs.Error(this, "Gagal membuka berkas brankas: " + ex.Message);
        }
    }

    private async Task ExportVaultFile(FileInfo f)
    {
        string name = VaultManager.DisplayName(f);
        string ext = Path.GetExtension(name).TrimStart('.');
        string? outPath = await Dialogs.PickSave(this, "Ekspor dari brankas", Path.GetFileNameWithoutExtension(name), string.IsNullOrEmpty(ext) ? "bin" : ext);
        if (outPath == null) return;
        try
        {
            await Task.Run(() => VaultManager.DecryptFile(f.FullName, outPath));
            await Dialogs.Done(this, "Berkas diekspor", "Salinan tanpa enkripsi sudah disimpan.", outPath);
        }
        catch (Exception ex)
        {
            await Dialogs.Error(this, "Gagal mengekspor: " + ex.Message);
        }
    }

    private async Task DeleteVaultFile(FileInfo f)
    {
        if (!await Dialogs.Confirm(this, "Hapus dari brankas?",
                $"\"{VaultManager.DisplayName(f)}\" akan dihapus permanen dari brankas. Tindakan ini tidak bisa dibatalkan.",
                "Hapus Permanen", danger: true)) return;
        try { f.Delete(); }
        catch (Exception ex) { await Dialogs.Error(this, ex.Message); }
        Navigate("brankas");
    }
}
