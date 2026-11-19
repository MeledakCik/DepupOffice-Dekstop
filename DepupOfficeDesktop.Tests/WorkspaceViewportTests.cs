using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DepupOfficeDesktop.Controls;
using Xunit;

namespace DepupOfficeDesktop.Tests;

public sealed class WorkspaceViewportTests
{
    [Theory]
    [InlineData(0xF6F7F9, 0xFFFFFF, 0xE9EDF3)]
    [InlineData(0x191B20, 0x272A32, 0x313540)]
    public Task RepeatedScrollRepaintsClippedCards(int background, int firstFill, int secondFill) =>
        RunOnSta(() =>
        {
            var backdrop = Opaque(background);
            var first = Opaque(firstFill);
            var second = Opaque(secondFill);
            using var viewport = new WorkspaceViewport
            {
                Size = new Size(240, 140),
                Padding = new Padding(12),
                BackColor = backdrop
            };
            using var page = new WorkspacePage { BackColor = backdrop };
            page.Controls.Add(Card(first));
            page.Controls.Add(new Panel { Size = new Size(180, 30), Margin = Padding.Empty });
            page.Controls.Add(Card(second));
            viewport.SetPage(page);
            viewport.CreateControl();
            int layouts = 0;
            page.Layout += (_, _) => layouts++;

            using var frame = new Bitmap(viewport.Width, viewport.Height);
            for (int i = 0; i < 8; i++)
            {
                PaintAt(0);
                Assert.Equal(first.ToArgb(), frame.GetPixel(90, 20).ToArgb());
                Assert.Equal(backdrop.ToArgb(), frame.GetPixel(90, 125).ToArgb());

                PaintAt(65);
                Assert.Equal(first.ToArgb(), frame.GetPixel(90, 10).ToArgb());
                Assert.Equal(backdrop.ToArgb(), frame.GetPixel(90, 60).ToArgb());
                Assert.Equal(second.ToArgb(), frame.GetPixel(90, 124).ToArgb());
            }
            Assert.Equal(0, layouts);

            void PaintAt(int offset)
            {
                viewport.ScrollOffset = offset;
                Assert.Equal(12 - offset, page.Location.Y);
                Assert.Equal(12 - offset, viewport.PointToClient(page.PointToScreen(Point.Empty)).Y);
                viewport.DrawToBitmap(frame, new Rectangle(Point.Empty, frame.Size));
            }
        });

    [Fact]
    public Task ScrollRangeFollowsContentAndWindowHeightWithoutChangingContentWidth() =>
        RunOnSta(() =>
        {
            using var viewport = new WorkspaceViewport { Size = new Size(380, 240), Padding = new Padding(16, 8, 16, 24) };
            using var page = new WorkspacePage();
            var content = new Panel { Size = new Size(300, 640), Margin = Padding.Empty };
            page.Controls.Add(content);
            viewport.SetPage(page);
            viewport.CreateControl();
            var bar = Assert.Single(viewport.Controls.OfType<VScrollBar>());
            int contentWidth = viewport.ContentWidth;

            Assert.True(viewport.ScrollByKey(Keys.End));
            Assert.Equal(432, viewport.ScrollOffset);
            Assert.Equal(432, bar.Value);

            bar.Value = 154;
            Assert.Equal(154, viewport.ScrollOffset);
            Assert.Equal(-146, page.Top);
            Assert.True(viewport.ScrollByKey(Keys.PageUp));
            Assert.Equal(0, viewport.ScrollOffset);

            viewport.ScrollOffset = int.MaxValue;
            viewport.Height = 500;
            Assert.Equal(172, viewport.ScrollOffset);
            content.Height = 100;
            Assert.Equal(0, viewport.ScrollOffset);
            Assert.Equal(8, page.Top);
            Assert.False(bar.Visible);
            Assert.False(viewport.ScrollByKey(Keys.End));
            Assert.Equal(contentWidth, viewport.ContentWidth);

            using var replacement = new WorkspacePage();
            replacement.Controls.Add(new Panel { Size = new Size(300, 900), Margin = Padding.Empty });
            Assert.Same(page, viewport.SetPage(replacement));
            Assert.True(bar.Visible);
            Assert.Equal(contentWidth, viewport.ContentWidth);
            Assert.Equal(0, viewport.ScrollOffset);
            Assert.True(viewport.ScrollByKey(Keys.End));
            Assert.Equal(432, viewport.ScrollOffset);
        });

    [Fact]
    public Task NativeSurfaceBuffersChildrenAndShowsTheReplacementAtItsRestoredOffset() =>
        RunOnSta(() =>
        {
            using var viewport = new WorkspaceViewport { Size = new Size(360, 240), Padding = new Padding(12) };
            using var oldPage = new WorkspacePage();
            oldPage.Controls.Add(new Panel { Size = new Size(300, 600) });
            viewport.SetPage(oldPage);
            viewport.CreateControl();

            const int extendedStyleIndex = -20, composited = 0x02000000;
            var surface = oldPage.Parent!;
            Assert.True((GetWindowLong(surface.Handle, extendedStyleIndex) & composited) != 0);

            using var nextPage = new WorkspacePage();
            nextPage.Controls.Add(new Panel { Size = new Size(300, 900) });
            var firstVisiblePosition = Point.Empty;
            bool oldPageWasVisible = false;
            nextPage.VisibleChanged += (_, _) =>
            {
                if (!nextPage.Visible) return;
                firstVisiblePosition = nextPage.Location;
                oldPageWasVisible = oldPage.Visible && oldPage.Parent == surface;
            };

            Assert.Same(oldPage, viewport.SetPage(nextPage, 180));
            Assert.True(oldPageWasVisible);
            Assert.Equal(new Point(12, -168), firstVisiblePosition);
            Assert.Equal(180, viewport.ScrollOffset);
            Assert.Null(oldPage.Parent);
        });

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [Fact]
    public Task WheelRoutesFromFocusedChromeOnlyWhenPointerIsOverWorkspace() =>
        RunOnSta(() =>
        {
            using var form = new NonActivatingForm
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-15000, -15000),
                ClientSize = new Size(360, 240),
                ShowInTaskbar = false
            };
            using var viewport = new WorkspaceViewport { Dock = DockStyle.Fill };
            var toolbar = new Button { Dock = DockStyle.Top };
            using var page = new WorkspacePage();
            page.Controls.Add(new Panel { Size = new Size(275, 1000) });
            viewport.SetPage(page);
            form.Controls.Add(viewport);
            form.Controls.Add(toolbar);
            form.Show();

            var filter = (IMessageFilter)viewport;
            var inside = viewport.PointToScreen(new Point(24, 24));
            var outside = toolbar.PointToScreen(new Point(12, 12));
            Message Wheel(Point pointer, int delta, int modifiers = 0) =>
                Message.Create(toolbar.Handle, 0x020A, (IntPtr)((delta << 16) | modifiers),
                    (IntPtr)((pointer.Y << 16) | (ushort)pointer.X));

            var message = Wheel(outside, -120);
            Assert.False(filter.PreFilterMessage(ref message));
            message = Wheel(inside, -120, modifiers: 0x0008);
            Assert.False(filter.PreFilterMessage(ref message));
            Assert.Equal(0, viewport.ScrollOffset);

            message = Wheel(inside, -120);
            if (SystemInformation.MouseWheelScrollLines == 0)
            {
                Assert.False(filter.PreFilterMessage(ref message));
                return;
            }
            Assert.True(filter.PreFilterMessage(ref message));
            Assert.True(viewport.ScrollOffset > 0);

            message = Wheel(inside, 120);
            Assert.True(filter.PreFilterMessage(ref message));
            Assert.Equal(0, viewport.ScrollOffset);

            viewport.Hide();
            message = Wheel(inside, -120);
            Assert.False(filter.PreFilterMessage(ref message));
        });

    private sealed class NonActivatingForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    private static CardPanel Card(Color fill) => new()
    {
        Size = new Size(180, 100),
        BackColor = fill,
        Margin = Padding.Empty,
        BorderThickness = 0
    };

    private static Color Opaque(int rgb) => Color.FromArgb(unchecked((int)0xFF000000) | rgb);

    private static Task RunOnSta(Action test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { test(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
