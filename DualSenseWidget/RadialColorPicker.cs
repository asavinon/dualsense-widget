using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DualSenseWidget;

public sealed class RadialColorPicker : FrameworkElement
{
    private double hue, saturation = 1, value = 1;
    private BitmapSource? wheel;
    public event EventHandler? ColorPicked;
    public Color SelectedColor => FromHsv(hue, saturation, value);
    public double Value => value;
    public RadialColorPicker() { Focusable = true; Cursor = Cursors.Cross; }
    public void SetColor(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        if (delta > 0) hue = ((max == r ? (g - b) / delta : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4) / 6 + 1) % 1;
        saturation = max == 0 ? saturation : delta / max;
        if (Math.Abs(value - max) > .00001) wheel = null;
        value = max; InvalidateVisual();
    }
    public void SetValue(double next)
    {
        value = Math.Clamp(next, 0, 1); wheel = null; Changed();
    }
    private double Radius => Math.Max(1, Math.Min(ActualWidth, ActualHeight) / 2 - 8);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (wheel == null) BuildWheel();
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawImage(wheel, new Rect(center.X - Radius, center.Y - Radius, Radius * 2, Radius * 2));
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 220, 235, 255)), 1), center, Radius, Radius);
        var marker = new Point(center.X + Math.Cos(hue * Math.PI * 2) * saturation * Radius,
            center.Y - Math.Sin(hue * Math.PI * 2) * saturation * Radius);
        dc.DrawEllipse(new SolidColorBrush(SelectedColor), new Pen(Brushes.Black, 4), marker, 5, 5);
        dc.DrawEllipse(null, new Pen(Brushes.White, 2), marker, 5, 5);
        if (IsKeyboardFocused) dc.DrawEllipse(null, new Pen(Brushes.LightSkyBlue, 1), center, Radius + 5, Radius + 5);
    }
    internal void Pick(Point position)
    {
        double x = position.X - ActualWidth / 2, y = ActualHeight / 2 - position.Y;
        double distance = Math.Sqrt(x * x + y * y);
        if (distance > .01) hue = (Math.Atan2(y, x) / (2 * Math.PI) + 1) % 1;
        saturation = Math.Clamp(distance / Radius, 0, 1); Changed();
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Point p = e.GetPosition(this);
        if ((p - new Point(ActualWidth / 2, ActualHeight / 2)).Length > Radius + 6) return;
        Focus(); CaptureMouse(); Pick(p); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) { Pick(e.GetPosition(this)); e.Handled = true; }
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e); if (IsMouseCaptured) { ReleaseMouseCapture(); e.Handled = true; }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Left: hue = (hue + 1 - 1d / 180) % 1; break;
            case Key.Right: hue = (hue + 1d / 180) % 1; break;
            case Key.Up: saturation = Math.Min(1, saturation + .02); break;
            case Key.Down: saturation = Math.Max(0, saturation - .02); break;
            default: return;
        }
        Changed(); e.Handled = true;
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    private void Changed() { InvalidateVisual(); ColorPicked?.Invoke(this, EventArgs.Empty); }
    private void BuildWheel()
    {
        const int size = 320;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            double dx = (x + .5 - size / 2d) / (size / 2d), dy = (size / 2d - y - .5) / (size / 2d);
            double s = Math.Sqrt(dx * dx + dy * dy);
            if (s > 1) continue;
            Color color = FromHsv((Math.Atan2(dy, dx) / (2 * Math.PI) + 1) % 1, s, value);
            int i = (y * size + x) * 4;
            pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255;
        }
        wheel = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        wheel.Freeze();
    }
    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h * 6 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (int)(h * 6) switch
        { 0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x), 3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
