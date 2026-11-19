namespace DepupOfficeDesktop.Core;

/// <summary>One open PDF or presentation shared by the reader, slideshow, and print job.</summary>
internal sealed class PagedDocument(
    SizeF[] sizes, Func<int, int, Bitmap> render, IDisposable resource) : IDisposable
{
    private readonly object _gate = new();
    private bool _disposed;

    public IReadOnlyList<SizeF> Sizes { get; } = sizes;
    public int Count => Sizes.Count;

    public static (PdfOpenStatus Status, PagedDocument? Document) Open(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (path.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
        {
            var slides = new PptxRenderer(path);
            float aspect = float.IsFinite(slides.Aspect) && slides.Aspect > 0 ? slides.Aspect : 16f / 9f;
            var sizes = Enumerable.Repeat(new SizeF(aspect, 1), slides.SlideCount).ToArray();
            return (PdfOpenStatus.Ok, new PagedDocument(sizes,
                (index, dimension) => slides.RenderSlide(index, (int)(dimension * Math.Min(1, aspect))), slides));
        }

        var (status, pdf) = PdfRenderer.OpenDocument(path, PdfRenderer.GetCachedPassword(path));
        if (pdf == null) return (status, null);
        try
        {
            var sizes = new SizeF[pdf.Count];
            for (int index = 0; index < sizes.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sizes[index] = pdf.PageSize(index);
            }
            return (status, new PagedDocument(sizes, pdf.Render, pdf));
        }
        catch
        {
            pdf.Dispose();
            throw;
        }
    }

    public Bitmap Render(int index, int dimension)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return render(index, dimension);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            resource.Dispose();
        }
    }
}
