using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Controls;

internal sealed class PagedReaderCanvas : ScrollableControl
{
    private const int CacheLimit = 6;
    private const int RenderDimension = 1600;

    private readonly PagedDocument _document;
    private readonly Dictionary<int, Bitmap> _pages = new();
    private readonly LinkedList<int> _usedPages = new();
    private readonly HashSet<int> _failedPages = new();
    private readonly int[] _offsets;
    private readonly Size[] _sizes;
    private int _current;
    private float _zoom;
    private int _paperTheme;
    private bool _rendering;
    private bool _arranging;
    private Task? _pendingRender;
    private System.Windows.Forms.Timer? _scrollTimer;
    private int _scrollStart, _scrollEnd;
    private bool _stepping;
    private long _scrollStarted;

    public event Action<int>? PageChanged;

    public PagedReaderCanvas(PagedDocument document, float zoom, int paperTheme)
    {
        _document = document;
        _offsets = new int[document.Count];
        _sizes = new Size[document.Count];
        _zoom = zoom;
        _paperTheme = paperTheme;
        AccessibleName = "Halaman dokumen";
        AccessibleRole = AccessibleRole.Pane;
        TabStop = true;
        DoubleBuffered = true;
        ResizeRedraw = true;
        AutoScroll = true;
        BackColor = Theme.BG;
    }

    public void SetAppearance(float zoom, int paperTheme)
    {
        _paperTheme = paperTheme;
        if (Math.Abs(zoom - _zoom) > 0.001f)
        {
            _zoom = zoom;
            ArrangePages();
        }
        Invalidate();
    }

    public void GoToPage(int index)
    {
        _scrollTimer?.Stop();
        int page = Math.Clamp(index, 0, _sizes.Length - 1);
        AutoScrollPosition = new Point(-AutoScrollPosition.X, Math.Max(0, _offsets[page] - Theme.S(12)));
        SetCurrent(page);
        Invalidate();
    }

    public void StepPage(int direction)
    {
        int target = Math.Clamp(_current + direction, 0, _sizes.Length - 1);
        if (target == _current) return;
        int destination = Math.Clamp(_offsets[target] - Theme.S(12), 0, Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height));
        int distance = Math.Abs(destination + AutoScrollPosition.Y);
        if (!AppStorage.Config.PdfSmoothScroll || !Theme.MotionEnabled || distance > ClientSize.Height * 2)
        {
            GoToPage(target);
            return;
        }

        if (_scrollTimer == null)
        {
            _scrollTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _scrollTimer.Tick += (_, _) =>
            {
                float t = Math.Clamp((Environment.TickCount64 - _scrollStarted) / 155f, 0, 1);
                float eased = 1 - (1 - t) * (1 - t);
                _stepping = true;
                try { AutoScrollPosition = new Point(-AutoScrollPosition.X, _scrollStart + (int)Math.Round((_scrollEnd - _scrollStart) * eased)); }
                finally { _stepping = false; }
                if (t >= 1) _scrollTimer.Stop();
                Invalidate();
            };
        }
        _scrollStart = -AutoScrollPosition.Y;
        _scrollEnd = destination;
        _scrollStarted = Environment.TickCount64;
        SetCurrent(target);
        _scrollTimer.Start();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_sizes is { Length: > 0 }) ArrangePages();
    }

    private void ArrangePages()
    {
        if (_arranging || ClientSize.Width <= 0) return;
        _scrollTimer?.Stop();
        _arranging = true;
        try
        {
            int current = _current;
            int gap = Theme.S(20);
            int inset = Theme.S(24);
            var scroll = AutoScrollPosition;
            int oldHeight = _sizes[current].Height;
            float anchor = oldHeight > 0 ? (-scroll.Y - _offsets[current]) / (float)oldHeight : 0;
            int viewport = ClientSize.Width + (VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0);
            int fit = Math.Max(1, viewport - inset * 2 - SystemInformation.VerticalScrollBarWidth);
            int nominalWidth = Math.Min(Theme.S(820), fit);
            int offset = gap;
            int pageAreaWidth = 0;
            for (int i = 0; i < _sizes.Length; i++)
            {
                var source = _document.Sizes[i];
                float aspect = Math.Clamp(source.Width / Math.Max(0.01f, source.Height), 0.05f, 20f);
                int width = Math.Max(1, (int)(nominalWidth * _zoom * Math.Min(1, aspect * 1.42f)));
                int height = Math.Max(1, (int)(width / aspect));
                _sizes[i] = new Size(width, height);
                _offsets[i] = offset;
                offset += height + gap;
                pageAreaWidth = Math.Max(pageAreaWidth, width);
            }
            AutoScrollMinSize = new Size(pageAreaWidth + inset * 2, offset);
            int top = oldHeight > 0 ? _offsets[current] + (int)(_sizes[current].Height * anchor) : 0;
            AutoScrollPosition = new Point(-scroll.X, Math.Max(0, top));
        }
        finally { _arranging = false; }
    }

    private int FindPage(int y)
    {
        int index = Array.BinarySearch(_offsets, y);
        return index >= 0 ? index : Math.Clamp(~index - 1, 0, _offsets.Length - 1);
    }

    private void SetCurrent(int page)
    {
        if (_current == page) return;
        _current = page;
        PageChanged?.Invoke(page);
    }

    private void UpdateAfterScroll()
    {
        if (_arranging || _sizes.Length == 0) return;
        SetCurrent(FindPage(-AutoScrollPosition.Y + Math.Min(ClientSize.Height / 3, Theme.S(160))));
        Invalidate();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        if (_stepping) { base.OnScroll(se); return; }
        _scrollTimer?.Stop();
        base.OnScroll(se);
        UpdateAfterScroll();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scrollTimer?.Stop();
        base.OnMouseWheel(e);
        UpdateAfterScroll();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        SetCurrent(FindPage(e.Y - AutoScrollPosition.Y));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        int page = keyData switch
        {
            Keys.PageUp => _current - 1,
            Keys.PageDown => _current + 1,
            Keys.Home => 0,
            Keys.End => _sizes.Length - 1,
            _ => -2
        };
        if (page == -2) return base.ProcessCmdKey(ref msg, keyData);
        if (keyData is Keys.PageUp or Keys.PageDown) { StepPage(keyData == Keys.PageUp ? -1 : 1); return true; }
        GoToPage(page);
        return true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_sizes.Length == 0 || _sizes[0].IsEmpty) return;
        var scroll = AutoScrollPosition;
        int first = FindPage(-scroll.Y);
        int last = FindPage(ClientSize.Height - scroll.Y);
        int center = Math.Max(ClientSize.Width, AutoScrollMinSize.Width) / 2 + scroll.X;
        int next = -1;

        using var paper = new SolidBrush(_paperTheme switch
        {
            1 => Color.FromArgb(18, 22, 30),
            2 => Color.FromArgb(250, 235, 216),
            3 => Color.FromArgb(225, 250, 230),
            _ => Color.White
        });
        using var edge = new Pen(Theme.LINE);
        using var tint = CreatePaperTint(_paperTheme);
        var graphics = e.Graphics;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        for (int i = first; i <= last; i++)
        {
            var size = _sizes[i];
            var area = new Rectangle(center - size.Width / 2, _offsets[i] + scroll.Y, size.Width, size.Height);
            if (!area.IntersectsWith(ClientRectangle)) continue;
            if (_pages.TryGetValue(i, out var image))
            {
                if (tint == null) graphics.DrawImage(image, area);
                else graphics.DrawImage(image, area, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, tint);
                Touch(i);
            }
            else
            {
                graphics.FillRectangle(paper, area);
                bool failed = _failedPages.Contains(i);
                if (!failed && (next < 0 || i == _current)) next = i;
                var message = new Rectangle(area.Left, area.Top, area.Width, Math.Min(area.Height, Theme.S(88)));
                TextRenderer.DrawText(graphics, failed ? "Halaman gagal ditampilkan" : $"Memuat halaman {i + 1}…",
                    Theme.FontBody(10f), message, _paperTheme == 1 ? Color.WhiteSmoke : Color.FromArgb(53, 58, 68),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            graphics.DrawRectangle(edge, area);
        }

        // Low-zoom viewports can fit more pages. Their cached bitmaps are rendered at a smaller size.
        if (next >= 0 && !_rendering)
            _pendingRender = RenderAsync(next, Math.Max(CacheLimit, last - first + 1));
    }

    internal static ImageAttributes? CreatePaperTint(int theme)
    {
        if (theme == 0) return null;
        var matrix = new ColorMatrix();
        (matrix.Matrix00, matrix.Matrix11, matrix.Matrix22, matrix.Matrix40, matrix.Matrix41, matrix.Matrix42) = theme switch
        {
            1 => (-1f, -1f, -1f, 1f, 1f, 1f),
            2 => (0.92f, 0.85f, 0.72f, 0.06f, 0.03f, -0.02f),
            _ => (0.86f, 0.95f, 0.84f, -0.02f, 0.03f, -0.02f)
        };
        var tint = new ImageAttributes();
        tint.SetColorMatrix(matrix);
        return tint;
    }

    private void Touch(int index)
    {
        if (_usedPages.Last?.Value == index) return;
        _usedPages.Remove(index);
        _usedPages.AddLast(index);
    }

    private async Task RenderAsync(int index, int cacheLimit)
    {
        _rendering = true;
        try
        {
            // Narrow pages at low zoom need fewer pixels, keeping a multi-page viewport affordable.
            int dimension = Math.Clamp(Math.Max(_sizes[index].Width, _sizes[index].Height) * 2, 480, RenderDimension);
            Bitmap bitmap = await Task.Run(() => _document.Render(index, dimension));
            if (IsDisposed) { bitmap.Dispose(); return; }
            _pages.Add(index, bitmap);
            Touch(index);
            while (_pages.Count > cacheLimit && _usedPages.First is { } oldest)
            {
                _pages.Remove(oldest.Value, out var old);
                old?.Dispose();
                _usedPages.RemoveFirst();
            }
        }
        catch (Exception) { if (!IsDisposed) _failedPages.Add(index); }
        finally
        {
            _rendering = false;
            if (!IsDisposed) Invalidate();
        }
    }

    public Task PendingRender => _pendingRender ?? Task.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scrollTimer?.Dispose();
            foreach (var bitmap in _pages.Values) bitmap.Dispose();
            _pages.Clear();
            _usedPages.Clear();
        }
        base.Dispose(disposing);
    }
}
