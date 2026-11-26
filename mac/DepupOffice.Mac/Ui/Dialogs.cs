using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DepupOfficeDesktop.Core;
using static DepupOffice.Mac.Ui;

namespace DepupOffice.Mac;

/// <summary>Dialog modal sederhana pengganti MessageBox / InputBox / OpenFileDialog Windows.</summary>
public static class Dialogs
{
    private static Window MakeWindow(string title, Control content, double width = 440)
        => new()
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = B(Surface),
            Content = new Border { Padding = new Thickness(22), Child = content },
        };

    private static StackPanel Buttons(params Button[] buttons)
    {
        var row = Row(8, buttons);
        row.HorizontalAlignment = HorizontalAlignment.Right;
        row.Margin = new Thickness(0, 10, 0, 0);
        return row;
    }

    public static async Task Info(Window owner, string title, string message)
    {
        Window? win = null;
        var ok = Btn("OK", () => win!.Close(), true);
        win = MakeWindow(title, Col(10, H2(title), T(message, 13, Text, null, true), Buttons(ok)));
        win.Opened += (_, _) => ok.Focus();
        await win.ShowDialog(owner);
    }

    public static Task Error(Window owner, string message) => Info(owner, "Terjadi kesalahan", message);

    public static async Task<bool> Confirm(Window owner, string title, string message, string okText = "Ya", string cancelText = "Batal", bool danger = false)
    {
        Window? win = null;
        var ok = Btn(okText, () => win!.Close(true), true, danger ? Red : Accent);
        var cancel = Btn(cancelText, () => win!.Close(false));
        win = MakeWindow(title, Col(10, H2(title), T(message, 13, Text, null, true), Buttons(cancel, ok)));
        win.Opened += (_, _) => ok.Focus();
        return await win.ShowDialog<bool>(owner);
    }

    /// <summary>Pilihan beberapa tombol. Mengembalikan indeks atau -1 bila dibatalkan.</summary>
    public static async Task<int> Choose(Window owner, string title, string message, params string[] options)
    {
        Window? win = null;
        var list = Col(6);
        for (int i = 0; i < options.Length; i++)
        {
            int idx = i;
            var b = Btn(options[i], () => win!.Close(idx), i == 0);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            list.Children.Add(b);
        }
        var cancel = Btn("Batal", () => win!.Close(-1));
        var body = Col(10, H2(title));
        if (!string.IsNullOrWhiteSpace(message)) body.Children.Add(T(message, 13, Muted, null, true));
        body.Children.Add(list);
        body.Children.Add(Buttons(cancel));
        win = MakeWindow(title, body);
        int? result = await win.ShowDialog<int?>(owner);
        return result ?? -1;
    }

    public static async Task<string?> Prompt(Window owner, string title, string message, string initial = "",
        bool password = false, string? watermark = null, bool multiline = false)
    {
        Window? win = null;
        var box = Input(watermark ?? "", initial, multiline);
        if (password) box.PasswordChar = '•';
        if (multiline) { box.Height = 140; }
        void Submit() => win!.Close(box.Text ?? "");
        var ok = Btn("OK", Submit, true);
        var cancel = Btn("Batal", () => win!.Close(null));
        if (!multiline)
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Submit(); } };
        var body = Col(10, H2(title));
        if (!string.IsNullOrWhiteSpace(message)) body.Children.Add(T(message, 13, Muted, null, true));
        body.Children.Add(box);
        body.Children.Add(Buttons(cancel, ok));
        win = MakeWindow(title, body);
        win.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        return await win.ShowDialog<string?>(owner);
    }

    public static Task<string?> Password(Window owner, string fileName, bool retry = false)
        => Prompt(owner, "PDF dilindungi password",
            (retry ? "Password salah. " : "") + $"Masukkan password untuk membuka \"{fileName}\".",
            password: true, watermark: "Password PDF");

    /// <summary>Dialog "selesai" dengan tombol Buka / Tampilkan di Finder.</summary>
    public static async Task Done(Window owner, string title, string message, string path)
    {
        Window? win = null;
        bool isDir = Directory.Exists(path);
        var open = Btn(isDir ? "Buka Folder" : "Buka", () => { Services.MacSystem.Open(path); win!.Close(); }, true);
        var reveal = Btn("Tampilkan di Finder", () => { Services.MacSystem.Reveal(path); win!.Close(); });
        var close = Btn("Tutup", () => win!.Close());
        var body = Col(10,
            Row(10, IconBadge("✓", Teal, TealBg, 34), H2(title)),
            T(message, 13, Text, null, true),
            Caption(path));
        body.Children.Add(Buttons(close, reveal, open));
        win = MakeWindow(title, body, 480);
        win.Opened += (_, _) => open.Focus();
        await win.ShowDialog(owner);
    }

    /// <summary>Atur urutan daftar (naik/turun/hapus). Mengembalikan null bila dibatalkan.</summary>
    public static async Task<List<string>?> Reorder(Window owner, string title, string message, IEnumerable<string> items, int minCount = 1)
    {
        var list = items.ToList();
        Window? win = null;
        var host = Col(4);
        var okBtn = Btn("Lanjutkan", () => win!.Close(true), true);

        void Render()
        {
            host.Children.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                int idx = i;
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                var num = T($"{i + 1}.", 12, Muted);
                num.Width = 26;
                var name = T(System.IO.Path.GetFileName(list[i]), 13);
                Grid.SetColumn(name, 1);
                var actions = Row(4,
                    SmallBtn("↑", () => { if (idx > 0) { (list[idx - 1], list[idx]) = (list[idx], list[idx - 1]); Render(); } }),
                    SmallBtn("↓", () => { if (idx < list.Count - 1) { (list[idx + 1], list[idx]) = (list[idx], list[idx + 1]); Render(); } }),
                    SmallBtn("✕", () => { list.RemoveAt(idx); Render(); }, Red));
                Grid.SetColumn(actions, 2);
                grid.Children.Add(num);
                grid.Children.Add(name);
                grid.Children.Add(actions);
                host.Children.Add(new Border { Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(8), Background = B(Surface2), Child = grid });
            }
            okBtn.IsEnabled = list.Count >= minCount;
        }

        Render();
        var cancel = Btn("Batal", () => win!.Close(false));
        var scroll = new ScrollViewer { Content = host, MaxHeight = 360 };
        var body = Col(10, H2(title));
        if (!string.IsNullOrWhiteSpace(message)) body.Children.Add(T(message, 13, Muted, null, true));
        body.Children.Add(scroll);
        body.Children.Add(Buttons(cancel, okBtn));
        win = MakeWindow(title, body, 520);
        bool ok = await win.ShowDialog<bool>(owner);
        return ok ? list : null;
    }

    // ---------- Berkas ----------
    public static readonly (string Name, string[] Ext)[] PdfFilter = { ("PDF", new[] { "pdf" }) };
    public static readonly (string Name, string[] Ext)[] ImageFilter = { ("Gambar", new[] { "jpg", "jpeg", "png", "heic", "bmp", "webp" }) };
    public static readonly (string Name, string[] Ext)[] DocumentFilter =
    {
        ("Semua dokumen", new[] { "pdf", "docx", "xlsx", "csv", "txt", "md", "jpg", "jpeg", "png", "heic", "bmp", "webp" }),
        ("PDF", new[] { "pdf" }),
        ("Word", new[] { "docx" }),
        ("Excel / CSV", new[] { "xlsx", "csv" }),
        ("Gambar", new[] { "jpg", "jpeg", "png", "heic", "bmp", "webp" }),
        ("Teks", new[] { "txt", "md" }),
    };

    private static List<FilePickerFileType> ToTypes((string Name, string[] Ext)[] filters)
        => filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Ext.Select(e => "*." + e).ToList() }).ToList();

    public static async Task<List<string>> PickFiles(Window owner, string title, bool multiple, (string Name, string[] Ext)[] filters)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = filters.Length == 0 ? null : ToTypes(filters),
        });
        return files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
    }

    public static async Task<string?> PickFile(Window owner, string title, (string Name, string[] Ext)[] filters)
        => (await PickFiles(owner, title, false, filters)).FirstOrDefault();

    public static async Task<string?> PickSave(Window owner, string title, string suggestedName, string ext, string? startDir = null)
    {
        IStorageFolder? start = null;
        try { start = await owner.StorageProvider.TryGetFolderFromPathAsync(startDir ?? AppStorage.OutDir); } catch { }
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase) ? suggestedName : $"{suggestedName}.{ext}",
            DefaultExtension = ext,
            SuggestedStartLocation = start,
            ShowOverwritePrompt = true,
            FileTypeChoices = new List<FilePickerFileType> { new(ext.ToUpperInvariant()) { Patterns = new[] { "*." + ext } } },
        });
        string? path = file?.TryGetLocalPath();
        if (path != null && !path.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase)) path += "." + ext;
        return path;
    }

    public static async Task<string?> PickFolder(Window owner, string title)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>
    /// Menjalankan pekerjaan berat di thread latar dengan jendela progres modal.
    /// Callback <c>report</c> boleh dipanggil dari thread mana pun.
    /// </summary>
    public static async Task<TResult> Busy<TResult>(Window owner, string message, Func<Action<string>, TResult> work)
    {
        var label = T(message, 13, Text, null, true);
        var bar = new ProgressBar { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 6, 0, 0) };
        bool done = false;
        var win = MakeWindow("Memproses", Col(10, H2("Mohon tunggu…"), label, bar), 380);
        win.Closing += (_, e) => { if (!done) e.Cancel = true; };
        _ = win.ShowDialog(owner);
        try
        {
            return await Task.Run(() => work(msg => Dispatcher.UIThread.Post(() => label.Text = msg)));
        }
        finally
        {
            done = true;
            win.Close();
        }
    }

    public static Task Busy(Window owner, string message, Action<Action<string>> work)
        => Busy<bool>(owner, message, report => { work(report); return true; });
}
