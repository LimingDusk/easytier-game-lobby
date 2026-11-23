using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace GameLobby;

// The order matches the 8-column monochrome atlas rendered from Assets/LobbyIcons.svg.
internal enum Glyph { Game, Copy, Eye, Join, Leave, Plus, People, Book, Monitor, Sun, Moon, Refresh, Pulse, Export, Back, Link }

internal static class LobbyArt
{
    private static readonly Bitmap Atlas = new(typeof(LobbyArt), "LobbyIcons.png");

    public static void Draw(Graphics g, Glyph icon, Rectangle target, Color color)
    {
        int index = (int)icon;
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix
        {
            Matrix00 = 0, Matrix11 = 0, Matrix22 = 0,
            Matrix40 = color.R / 255f, Matrix41 = color.G / 255f, Matrix42 = color.B / 255f
        });
        g.DrawImage(Atlas, target, index % 8 * 96, index / 8 * 96, 96, 96, GraphicsUnit.Pixel, attributes);
    }

    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Max(1, Math.Min(2 * radius, Math.Min(bounds.Width, bounds.Height)));
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class IconButton : Button
{
    public Glyph Glyph { get; set; }
    public bool Quiet { get; init; }
    public Color HoverColor { get; set; }
    public Color EdgeColor { get; set; }
    private bool hover;
    public IconButton(string text, Glyph glyph, bool quiet = false)
    {
        Text = text;
        Glyph = glyph;
        Quiet = quiet;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Anchor = AnchorStyles.Left;
        Size = new Size(text.Length == 0 ? 38 : 49 + text.Length * 16, 38);
        Margin = new Padding(3);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = LobbyArt.Rounded(new RectangleF(1, 1, Width - 2, Height - 2), LogicalToDeviceUnits(7));
        using var fill = new SolidBrush(hover && Enabled ? HoverColor : BackColor);
        g.FillPath(fill, path);
        if (!Quiet)
        {
            using var pen = new Pen(EdgeColor);
            g.DrawPath(pen, path);
        }
        var ink = Enabled ? ForeColor : SystemColors.GrayText;
        int size = LogicalToDeviceUnits(17), gap = LogicalToDeviceUnits(7);
        int x = Text.Length == 0 ? (Width - size) / 2 : LogicalToDeviceUnits(11);
        LobbyArt.Draw(g, Glyph, new Rectangle(x, (Height-size)/2, size, size), ink);
        if (Text.Length != 0)
            TextRenderer.DrawText(g, Text, Font, Rectangle.FromLTRB(x+size+gap, 0, Width-2, Height), ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle,-4,-4), ink, BackColor);
    }
}
