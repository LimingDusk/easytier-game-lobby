using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameLobby;

public sealed class Glyph : Control
{
    private static readonly Dictionary<string, Geometry> Shapes = SvgPaths.Data.ToDictionary(
        entry => entry.Key, entry =>
        {
            var shape = Geometry.Parse(entry.Value);
            shape.Freeze();
            return shape;
        });
    private static readonly Geometry Logo = Geometry.Parse(Brand.Path);
    private static readonly Brush BrandBlue = (Brush)new BrushConverter().ConvertFromString(Brand.Fill)!;
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(Glyph),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    static Glyph()
    {
        ForegroundProperty.OverrideMetadata(typeof(Glyph), new FrameworkPropertyMetadata(
            null, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
        FocusableProperty.OverrideMetadata(typeof(Glyph), new FrameworkPropertyMetadata(false));
    }

    protected override void OnRender(DrawingContext context)
    {
        bool brand = Kind == "logo";
        var shape = brand ? Logo : Shapes.GetValueOrDefault(Kind);
        if (shape == null) return;
        double scale = Math.Min(ActualWidth, ActualHeight) / (brand ? Brand.Size : 24);
        var pen = new Pen(brand ? Brushes.White : Foreground, brand ? Brand.Stroke : 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        context.PushTransform(new ScaleTransform(scale, scale));
        if (brand) context.DrawRoundedRectangle(BrandBlue, null, new Rect(0, 0, Brand.Size, Brand.Size), Brand.Radius, Brand.Radius);
        context.DrawGeometry(null, pen, shape);
        context.Pop();
    }
}
