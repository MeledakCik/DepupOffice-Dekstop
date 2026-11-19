using System.Runtime.InteropServices;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Controls;

/// <summary>Scrolls one page inside a clipped window while preserving its layout and child controls.</summary>
internal sealed class WorkspaceViewport : Panel, IMessageFilter
{
    private const int WmMouseWheel = 0x020A;
    private const int WheelDelta = 120;

    private readonly WorkspaceSurface _clip = new();
    private readonly VScrollBar _bar = new() { AccessibleName = "Gulir halaman", TabStop = false, Visible = false };
    private WorkspacePage? _page;
    private int _offset, _limit, _wheelRemainder;
    private bool _updatingRange;

    public WorkspaceViewport()
    {
        BackColor = Theme.BG;
        _bar.Width = SystemInformation.VerticalScrollBarWidth;
        Theme.DarkScrollbars(_bar);
        _bar.ValueChanged += (_, _) =>
        {
            if (!_updatingRange) ScrollOffset = _bar.Value;
        };
        _clip.AllowDrop = true;
        _clip.DragEnter += (_, e) => OnDragEnter(e);
        _clip.DragDrop += (_, e) => OnDragDrop(e);
        Controls.AddRange([_clip, _bar]);
    }

    public int ContentWidth => Math.Max(1, ClientSize.Width - _bar.Width - Padding.Horizontal);

    public int ScrollOffset
    {
        get => _offset;
        set
        {
            int next = Math.Clamp(value, 0, _limit);
            if (next == _offset) return;
            _offset = next;
            if (_bar.Value != _offset) _bar.Value = _offset;
            PositionPage();
        }
    }

    public WorkspacePage? SetPage(WorkspacePage page, int scrollOffset = 0)
    {
        var previous = _page;
        _clip.SuspendLayout();
        try
        {
            if (previous != null)
                previous.SizeChanged -= PageSizeChanged;

            // Keep the current page visible until the replacement has its final position.
            if (page.AutoSize) page.Size = page.PreferredSize;
            page.Visible = false;
            _page = page;
            _offset = scrollOffset;
            _wheelRemainder = 0;
            _clip.Controls.Add(page);
            UpdateScrollRange();
            page.BringToFront();
            page.Visible = true;
            if (previous != null) _clip.Controls.Remove(previous);
        }
        finally { _clip.ResumeLayout(true); }
        page.SizeChanged += PageSizeChanged;
        UpdateScrollRange(present: false);
        Redraw.PresentContent(_clip);
        return previous;
    }

    private void PageSizeChanged(object? sender, EventArgs e) => UpdateScrollRange();

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int width = Math.Max(0, ClientSize.Width - _bar.Width);
        _clip.SetBounds(0, 0, width, ClientSize.Height);
        _bar.SetBounds(width, 0, _bar.Width, ClientSize.Height);
        UpdateScrollRange();
    }

    private void UpdateScrollRange(bool present = true)
    {
        if (_updatingRange || Disposing || IsDisposed) return;
        int documentHeight = (_page?.Height ?? 0) + Padding.Vertical;
        int viewHeight = Math.Max(1, _clip.ClientSize.Height);
        _limit = Math.Max(0, documentHeight - viewHeight);
        _offset = Math.Clamp(_offset, 0, _limit);
        _updatingRange = true;
        try
        {
            int maximum = Math.Max(0, documentHeight - 1);
            _bar.LargeChange = viewHeight;
            _bar.SmallChange = Math.Max(1, Theme.S(36));
            _bar.Maximum = maximum;
            if (_bar.Value != _offset) _bar.Value = _offset;
            _bar.Visible = _limit > 0;
        }
        finally { _updatingRange = false; }
        PositionPage(present);
    }

    private void PositionPage(bool present = true)
    {
        if (_page == null) return;
        var origin = new Point(Padding.Left, Padding.Top - _offset);
        if (_page.Location == origin) return;
        _page.MoveTo(origin);
        if (present && _page.Visible) Redraw.PresentContent(_clip);
    }

    public bool ScrollByKey(Keys key)
    {
        if (!Visible || !Enabled || _limit == 0) return false;
        int page = Math.Max(1, _clip.ClientSize.Height - _bar.SmallChange);
        switch (key)
        {
            case Keys.PageUp: ScrollOffset -= page; break;
            case Keys.PageDown: ScrollOffset += page; break;
            case Keys.Home: ScrollOffset = 0; break;
            case Keys.End: ScrollOffset = _limit; break;
            default: return false;
        }
        return true;
    }

    bool IMessageFilter.PreFilterMessage(ref Message message)
    {
        if (message.Msg != WmMouseWheel || !Visible || !Enabled || _limit == 0) return false;
        var target = FromChildHandle(message.HWnd);
        var owner = FindForm();
        if (target == null || owner == null || target.FindForm() != owner || !owner.Enabled) return false;

        long coordinates = message.LParam.ToInt64();
        var pointer = new Point(unchecked((short)coordinates), unchecked((short)(coordinates >> 16)));
        if (!ClientRectangle.Contains(PointToClient(pointer))) return false;

        long parameters = message.WParam.ToInt64();
        const int controlKey = 0x0008, shiftKey = 0x0004;
        if ((parameters & (controlKey | shiftKey)) != 0) return false;
        int lines = SystemInformation.MouseWheelScrollLines;
        if (lines == 0) return false;
        int amount = lines < 0 ? Math.Max(1, _clip.ClientSize.Height - _bar.SmallChange) : lines * _bar.SmallChange;
        int delta = unchecked((short)(parameters >> 16));
        int movement = _wheelRemainder + delta * amount;
        _wheelRemainder = movement % WheelDelta;
        ScrollOffset -= movement / WheelDelta;
        return true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Application.AddMessageFilter(this);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Application.RemoveMessageFilter(this);
        base.OnHandleDestroyed(e);
    }

    private sealed class WorkspaceSurface : Panel
    {
        private const int WsExComposited = 0x02000000;

        public WorkspaceSurface()
        {
            TabStop = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                // Buffer the cards, labels and other child HWNDs together on the visible surface.
                var parameters = base.CreateParams;
                parameters.ExStyle |= WsExComposited;
                return parameters;
            }
        }
    }
}

internal sealed class WorkspacePage : FlowLayoutPanel
{
    private const uint NoSize = 0x0001, NoZOrder = 0x0004, NoRedraw = 0x0008, NoActivate = 0x0010;

    public WorkspacePage()
    {
        BackColor = Theme.BG;
        FlowDirection = FlowDirection.TopDown;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        WrapContents = false;
    }

    internal void MoveTo(Point origin)
    {
        // Move the clipped window without showing an intermediate erase or pixel copy.
        const uint moveFlags = NoSize | NoZOrder | NoRedraw | NoActivate;
        if (!IsHandleCreated || !SetWindowPos(Handle, IntPtr.Zero, origin.X, origin.Y, 0, 0, moveFlags))
            Location = origin;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
