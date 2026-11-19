using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using DepupOfficeDesktop.Core;
using global::Windows.Devices.Enumeration;
using global::Windows.Graphics.Imaging;
using global::Windows.Media.Capture;
using global::Windows.Media.Capture.Frames;
using global::Windows.Media.MediaProperties;
using global::Windows.Storage.Streams;

namespace DepupOfficeDesktop.Forms;

internal sealed class ScanCameraForm(Func<string> nextPhotoPath) : Form
{
    private readonly PictureBox _preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly Label _status = new() { Dock = DockStyle.Top, AutoEllipsis = true, UseMnemonic = false };
    private readonly ComboBox _devices = new() { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(DeviceInformation.Name) };
    private readonly List<string> _photos = new();
    private Button _snap = null!;
    private Button _finish = null!;
    private MediaCapture? _camera;
    private MediaFrameReader? _reader;
    private bool _busy;
    private bool _stopping;
    private bool _stopped;
    private long _lastFrame;
    private int _queuedFrame;

    public static string[] TakePhotos(IWin32Window owner, Func<string> nextPhotoPath)
    {
        using var camera = new ScanCameraForm(nextPhotoPath);
        camera.Build();
        return camera.ShowDialog(owner) == DialogResult.OK ? camera._photos.ToArray() : [];
    }

    private void Build()
    {
        Text = "Kamera dokumen";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(Theme.S(700), Theme.S(540));
        BackColor = Theme.SURFACE;
        _preview.AccessibleName = "Pratinjau kamera";
        _devices.AccessibleName = "Pilih kamera";
        _devices.Width = Theme.S(184);

        _status.Height = Theme.S(40);
        _status.Font = Theme.FontBody(10f);
        _status.ForeColor = Theme.MUTED;
        _status.Padding = new Padding(Theme.S(12), Theme.S(10), 0, 0);
        _status.Text = "Mencari kamera…";
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true,
            Padding = new Padding(Theme.S(12), Theme.S(8), Theme.S(12), Theme.S(8))
        };
        _snap = Theme.CreateButton("Ambil foto", true, Theme.ACCENT, Theme.S(36), Theme.S(120));
        _finish = Theme.CreateButton("Gunakan foto", false, Theme.TEXT, Theme.S(36), Theme.S(130));
        _snap.Enabled = _finish.Enabled = false;
        _snap.Click += async (s, e) => await CaptureAsync();
        _finish.Click += (s, e) => DialogResult = DialogResult.OK;
        var cancel = Theme.CreateButton("Batal", false, Theme.TEXT, Theme.S(36), Theme.S(74));
        cancel.Click += (s, e) => DialogResult = DialogResult.Cancel;
        CancelButton = cancel;
        bar.Controls.AddRange(new Control[] { _devices, _snap, _finish, cancel });
        Controls.Add(_preview);
        Controls.Add(bar);
        Controls.Add(_status);
        Shown += async (s, e) =>
        {
            _busy = true;
            try
            {
                var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
                if (_stopping) return;
                foreach (var device in devices) _devices.Items.Add(device);
                if (_devices.Items.Count == 0)
                {
                    _status.Text = "Kamera tidak ditemukan. Anda tetap bisa mengimpor foto dari PC.";
                    return;
                }
                _devices.SelectedIndex = 0;
                _devices.SelectedIndexChanged += async (_, _) => await OpenSelectedAsync();
            }
            catch (Exception ex) { if (!_stopping) _status.Text = $"Daftar kamera tidak dapat dibuka. {ex.Message}"; }
            finally { _busy = false; }
            if (!_stopping) await OpenSelectedAsync();
        };
    }

    private async Task OpenSelectedAsync()
    {
        if (_busy || _stopping || _devices.SelectedItem is not DeviceInformation device) return;
        _busy = true;
        _snap.Enabled = _devices.Enabled = false;
        _status.Text = "Membuka kamera…";
        try
        {
            await ReleaseCameraAsync();
            if (_stopping) return;
            _camera = new MediaCapture();
            await _camera.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = device.Id, StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu
            });
            if (_stopping) return;
            var source = _camera.FrameSources.Values
                .Where(frame => frame.Info.SourceKind == MediaFrameSourceKind.Color &&
                    frame.Info.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord)
                .OrderBy(frame => frame.Info.MediaStreamType != MediaStreamType.VideoPreview)
                .FirstOrDefault() ?? throw new InvalidOperationException("Kamera ini tidak mendukung pratinjau foto.");
            var format = source.CurrentFormat.VideoFormat;
            int width = format == null ? 0 : (int)format.Width;
            int height = format == null ? 0 : (int)format.Height;
            int longestSide = Math.Max(width, height);
            _reader = longestSide > 960
                ? await _camera.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Argb32, new BitmapSize
                {
                    Width = (uint)Math.Max(2, width * 960 / longestSide / 2 * 2),
                    Height = (uint)Math.Max(2, height * 960 / longestSide / 2 * 2)
                })
                : await _camera.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Argb32);
            if (_stopping) return;
            _reader.FrameArrived += FrameArrived;
            if (await _reader.StartAsync() != MediaFrameReaderStartStatus.Success)
                throw new InvalidOperationException("Pratinjau kamera gagal dimulai.");
            if (!_stopping) _status.Text = "Posisikan dokumen di dalam pratinjau, lalu ambil foto.";
        }
        catch (Exception ex)
        {
            await ReleaseCameraAsync();
            if (!_stopping) _status.Text = ex is UnauthorizedAccessException
                ? "Akses kamera ditolak. Izinkan akses kamera desktop di Windows atau impor foto."
                : $"Kamera tidak dapat dibuka. {ex.Message}";
        }
        finally
        {
            _busy = false;
            if (!_stopping) _devices.Enabled = true;
        }
    }

    private void FrameArrived(MediaFrameReader reader, MediaFrameArrivedEventArgs args)
    {
        long now = Environment.TickCount64;
        if (Volatile.Read(ref _stopping) || now - Interlocked.Read(ref _lastFrame) < 85 ||
            Interlocked.Exchange(ref _queuedFrame, 1) != 0) return;
        Bitmap? image = null;
        bool posted = false;
        try
        {
            using var frame = reader.TryAcquireLatestFrame();
            using var pixels = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (pixels == null) return;
            Interlocked.Exchange(ref _lastFrame, now);
            using var bgra = SoftwareBitmap.Convert(pixels, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            int width = bgra.PixelWidth, height = bgra.PixelHeight;
            var bytes = new byte[checked(width * height * 4)];
            bgra.CopyToBuffer(bytes.AsBuffer());
            image = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            var locked = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, image.PixelFormat);
            try
            {
                for (int y = 0; y < height; y++)
                    Marshal.Copy(bytes, y * width * 4, locked.Scan0 + y * locked.Stride, width * 4);
            }
            finally { image.UnlockBits(locked); }
            Bitmap preview = image;
            BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _queuedFrame, 0);
                if (_stopping || reader != _reader) { preview.Dispose(); return; }
                var previous = _preview.Image;
                _preview.Image = preview;
                previous?.Dispose();
                if (!_busy) _snap.Enabled = true;
            }));
            posted = true;
            image = null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or COMException) { }
        finally
        {
            image?.Dispose();
            if (!posted) Interlocked.Exchange(ref _queuedFrame, 0);
        }
    }

    private async Task CaptureAsync()
    {
        if (_stopping || _busy || _camera == null || _preview.Image is not Bitmap preview) return;
        _busy = true;
        _snap.Enabled = _devices.Enabled = _finish.Enabled = false;
        try
        {
            using var fallback = (Bitmap)preview.Clone();
            string path = nextPhotoPath();
            using var stream = new InMemoryRandomAccessStream();
            try { await _camera.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), stream); }
            catch (Exception ex) when (ex is COMException or NotSupportedException or InvalidOperationException) { stream.Size = 0; }
            if (_stopping) return;
            if (stream.Size > 0)
            {
                using var input = stream.GetInputStreamAt(0).AsStreamForRead();
                using var output = File.Create(path);
                await input.CopyToAsync(output);
            }
            else
            {
                await Task.Run(() => fallback.Save(path, ImageFormat.Jpeg));
            }
            if (_stopping) return;
            _photos.Add(path);
            _status.Text = $"{_photos.Count} foto diambil. Ambil halaman berikutnya atau pilih Gunakan foto.";
        }
        catch (Exception ex) { if (!_stopping) _status.Text = $"Foto gagal disimpan. {ex.Message}"; }
        finally
        {
            _busy = false;
            if (!_stopping) { _snap.Enabled = _devices.Enabled = true; _finish.Enabled = _photos.Count > 0; }
        }
    }

    private async Task ReleaseCameraAsync()
    {
        var reader = _reader;
        _reader = null;
        if (reader != null)
        {
            reader.FrameArrived -= FrameArrived;
            try { await reader.StopAsync(); } catch (Exception) { }
            reader.Dispose();
        }
        _camera?.Dispose();
        _camera = null;
        var old = _preview.Image;
        _preview.Image = null;
        old?.Dispose();
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (_stopped) { base.OnFormClosing(e); return; }
        e.Cancel = true;
        if (_stopping) return;
        var result = DialogResult;
        Volatile.Write(ref _stopping, true);
        Enabled = false;
        await Task.Yield();
        while (_busy) await Task.Delay(30);
        try { await ReleaseCameraAsync(); }
        finally
        {
            _stopped = true;
            if (!IsDisposed)
            {
                DialogResult = result == DialogResult.OK ? result : DialogResult.Cancel;
                Close();
            }
        }
    }
}
