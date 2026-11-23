using System.Drawing.Drawing2D;

namespace GameLobby;

internal sealed class AppearanceSlider : Control
{
    private static readonly string[] Names = ["跟随系统", "浅色", "深色"];
    private int selected;
    private float? dragPosition;
    public event EventHandler? SelectionChanged;

    public AppearanceSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleName = "外观";
        AccessibleDescription = "拖动滑块、点击选项或使用左右方向键选择外观";
        AccessibleRole = AccessibleRole.Slider;
        Size = new Size(224, 36);
        Anchor = AnchorStyles.Right;
        Margin = new Padding(6, 4, 12, 4);
    }

    public int SelectedIndex
    {
        get => selected;
        set
        {
            int next = Math.Clamp(value, 0, Names.Length - 1);
            if (next == selected) return;
            selected = next;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Color TrackColor { get; set; }
    public Color ThumbColor { get; set; }
    public Color SelectedText { get; set; }

    private float Inset => LogicalToDeviceUnits(3);
    private float SegmentWidth => Math.Max(1, (Width - 2 * Inset) / Names.Length);
    private float PositionAt(int x) => Math.Clamp((x - Inset) / SegmentWidth - .5f, 0, Names.Length - 1);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        Capture = true;
        dragPosition = PositionAt(e.X);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragPosition is null) return;
        dragPosition = PositionAt(e.X);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || dragPosition is null) return;
        int next = (int)MathF.Floor(PositionAt(e.X) + .5f);
        Capture = false;
        dragPosition = null;
        SelectedIndex = next;
        Invalidate();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) { dragPosition = null; Invalidate(); }
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left: SelectedIndex--; break;
            case Keys.Right: SelectedIndex++; break;
            case Keys.Home: SelectedIndex = 0; break;
            case Keys.End: SelectedIndex = Names.Length - 1; break;
            case Keys.Escape when Capture: Capture = false; break;
            default: return;
        }
        e.SuppressKeyPress = true;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    private static GraphicsPath Capsule(RectangleF r)
    {
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, r.Height, r.Height, 90, 180);
        path.AddArc(r.Right - r.Height, r.Y, r.Height, r.Height, 270, 180);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < Height || Height <= 2 * Inset) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var track = Capsule(new RectangleF(1, 1, Width - 2, Height - 2));
        using var trackBrush = new SolidBrush(TrackColor);
        g.FillPath(trackBrush, track);
        float position = dragPosition ?? selected;
        using var thumb = Capsule(new RectangleF(Inset + SegmentWidth * position, Inset, SegmentWidth, Height - 2 * Inset));
        using var thumbBrush = new SolidBrush(ThumbColor);
        g.FillPath(thumbBrush, thumb);
        // Paint text twice with a clip so it retains contrast on both sides of a moving thumb.
        void Labels(Color color)
        {
            for (int i = 0; i < Names.Length; i++)
            {
                var rect = Rectangle.Round(new RectangleF(Inset + SegmentWidth * i, 0, SegmentWidth, Height));
                TextRenderer.DrawText(g, Names[i], Font, rect, color, TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping);
            }
        }
        Labels(ForeColor);
        var state = g.Save();
        g.SetClip(thumb);
        Labels(SelectedText);
        g.Restore(state);
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(ForeColor) { DashStyle = DashStyle.Dot };
            g.DrawPath(pen, track);
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new SliderAccessibleObject(this);

    private sealed class SliderAccessibleObject(AppearanceSlider slider) : ControlAccessibleObject(slider)
    {
        public override string? Value
        {
            get => Names[slider.SelectedIndex];
            set { int index = Array.IndexOf(Names, value); if (index >= 0) slider.SelectedIndex = index; }
        }
    }
}
