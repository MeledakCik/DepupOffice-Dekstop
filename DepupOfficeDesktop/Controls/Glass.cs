using System.Runtime.InteropServices;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Controls;

/// <summary>Stationary chrome with a theme tint and a separator on the content edge.</summary>
internal sealed class GlassPane : Panel
{
    private readonly AnchorStyles _edge;

    public GlassPane(Color surface, AnchorStyles edge)
    {
        _edge = edge;
        BackColor = Theme.GlassOn
            ? Theme.Mix(surface, Theme.Dark ? Theme.BG : Color.White, Theme.Dark ? 0.35 : 0.38)
            : surface;
        Padding = new Padding(0, edge == AnchorStyles.Top ? 1 : 0,
            edge == AnchorStyles.Right ? 1 : 0, edge == AnchorStyles.Bottom ? 1 : 0);
        ResizeRedraw = true;
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.LINE);
        int right = Width - 1, bottom = Height - 1;
        if (_edge == AnchorStyles.Top) e.Graphics.DrawLine(pen, 0, 0, right, 0);
        if (_edge == AnchorStyles.Bottom) e.Graphics.DrawLine(pen, 0, bottom, right, bottom);
        if (_edge == AnchorStyles.Right) e.Graphics.DrawLine(pen, right, 0, right, bottom);
    }
}

internal static class Redraw
{
    private const int WmSetRedraw = 0x000B;
    private const uint Invalidate = 0x0001, NoErase = 0x0020, AllChildren = 0x0080, UpdateNow = 0x0100, Frame = 0x0400;

    public static void PresentContent(Control control)
    {
        if (!control.IsHandleCreated || !control.Visible) return;
        RedrawWindow(control.Handle, IntPtr.Zero, IntPtr.Zero, Invalidate | NoErase | AllChildren | UpdateNow);
    }

    public static void Suspend(Control control)
    {
        if (control.IsHandleCreated) SendMessage(control.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
    }

    public static void Resume(Control control)
    {
        if (!control.IsHandleCreated) return;
        SendMessage(control.Handle, WmSetRedraw, (IntPtr)1, IntPtr.Zero);
        RedrawWindow(control.Handle, IntPtr.Zero, IntPtr.Zero, Invalidate | NoErase | Frame | AllChildren);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);
}
