using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Forms;

public partial class MainForm
{
    private void StartScanFlow(string[]? initialFiles = null, bool receipt = false)
    {
        using var session = new ScanSession();
        string[] files = initialFiles ?? [];
        if (files.Length == 0)
        {
            using var picker = new Dlg(receipt ? "Pindai struk" : "Pindai dokumen",
                "Ambil satu atau beberapa foto dengan kamera, atau impor foto dari PC.", 470);
            picker.Action("Buka kamera…", true, () =>
            {
                files = ScanCameraForm.TakePhotos(picker, session.NewPhotoPath);
                if (files.Length > 0) picker.DialogResult = DialogResult.OK;
            });
            picker.Action("Impor foto…", false, () =>
            {
                files = Ui.PickFiles(picker, "Pilih foto dokumen", ScanEditForm.ImageFilter) ?? [];
                if (files.Length > 0) picker.DialogResult = DialogResult.OK;
            });
            if (picker.ShowDialog(this) != DialogResult.OK) return;
        }

        var pages = ScanEditForm.ImportPages(this, files);
        if (pages.Count == 0) return;
        using var editor = new ScanEditForm(pages, session.NewPhotoPath, receipt);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        RenderCurrentTab(preserveScroll: true);
        if (editor.OutputFilePath is { } path)
            ShowOperationResultDialog("Hasil pindai tersimpan", path);
        else if (editor.OutputJpgCount > 0)
            Theme.Toast(this, $"{editor.OutputJpgCount} JPG tersimpan di folder Gambar.");
    }

    private sealed class ScanSession : IDisposable
    {
        private DirectoryInfo? _directory;
        private readonly List<string> _createdPaths = new();

        public string NewPhotoPath()
        {
            _directory ??= Directory.CreateTempSubdirectory("DepupScan-");
            string path = Path.Combine(_directory.FullName, $"{_createdPaths.Count + 1:D4}.jpg");
            _createdPaths.Add(path);
            return path;
        }

        public void Dispose()
        {
            foreach (string path in _createdPaths)
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            try { _directory?.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
