using System.Collections.Concurrent;
using System.Drawing;
using System.Windows.Forms;
using DepupOfficeDesktop.Controls;
using DepupOfficeDesktop.Core;
using Xunit;

namespace DepupOfficeDesktop.Tests;

public sealed class PagedReaderCanvasTests
{
    [Fact]
    public async Task PaintingAndJumpingOnlyRenderPagesInTheViewport()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var requests = new ConcurrentQueue<int>();
                var sizes = Enumerable.Repeat(new SizeF(595, 842), 400).ToArray();
                using var source = new PagedDocument(sizes, (index, _) =>
                {
                    requests.Enqueue(index);
                    return new Bitmap(42, 60);
                }, new MemoryStream());
                using var canvas = new PagedReaderCanvas(source, 1, 0) { Size = new Size(650, 440) };
                int selected = 0;
                canvas.PageChanged += index => selected = index;
                using var frame = new Bitmap(canvas.Width, canvas.Height);

                PaintAndWait();
                Assert.Equal(new[] { 0 }, requests.ToArray());

                canvas.GoToPage(350);
                PaintAndWait();
                Assert.Equal(350, selected);
                Assert.Equal(new[] { 0, 350 }, requests.ToArray());

                canvas.SetAppearance(1.25f, 2);
                PaintAndWait();
                Assert.Equal(350, selected);
                Assert.Equal(new[] { 0, 350 }, requests.ToArray());
                completion.SetResult();

                void PaintAndWait()
                {
                    canvas.DrawToBitmap(frame, new Rectangle(Point.Empty, frame.Size));
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    while (!canvas.PendingRender.IsCompleted)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        Application.DoEvents();
                        Thread.Sleep(1);
                    }
                    canvas.PendingRender.GetAwaiter().GetResult();
                }
            }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
