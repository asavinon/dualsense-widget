using System;
using System.Windows;
using System.Windows.Media;

namespace DualSenseWidget;

public enum WidgetTheme { Glass, Translucent, Dark }

internal static class WidgetAppearance
{
    public static void Apply(WidgetTheme theme, double opacity)
    {
        double alpha = theme == WidgetTheme.Dark ? 1 : Math.Clamp(double.IsFinite(opacity) ? opacity / 100 : .76, .4, 1);
        bool glass = theme == WidgetTheme.Glass;
        bool dark = theme == WidgetTheme.Dark;
        var resources = Application.Current.Resources;
        resources["ShellBackground"] = Gradient(glass ? "#293A56" : "#101622", "#101622", alpha);
        resources["ShellBorder"] = Brush(glass ? "#D5EBFF" : "#6F87A9", dark ? .4 : .55);
        resources["HeroBackground"] = Gradient(glass ? "#5A779F" : "#21324B", "#151E2E", dark ? 1 : .40 * alpha);
        resources["HeroBorder"] = Brush(glass ? "#D0E6FF" : "#5F789C", dark ? .35 : .3);
        resources["ControlSurface"] = Brush("#1B2536", dark ? 1 : .42 + .25 * alpha);
        resources["ControlBorder"] = Brush(glass ? "#B7D6F5" : "#5D779B", dark ? .6 : .40);
        resources["ButtonSurface"] = Brush("#28364D", dark ? 1 : .45 + .25 * alpha);
        resources["PlayerSurface"] = Brush("#29344A", dark ? 1 : .40 + .25 * alpha);
        resources["TrackSurface"] = Brush("#405271", dark ? 1 : .62);
        resources["RuleSurface"] = Brush("#8BA5C9", dark ? .25 : .3);
        resources["PopupSurface"] = Brush("#1B2536", .97);
        resources["GlassHighlight"] = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(glass ? (byte)20 : (byte)0, 220, 240, 255), 0),
            new(Color.FromArgb(glass ? (byte)4 : (byte)0, 220, 240, 255), .45),
            new(Colors.Transparent, .46),
            new(Color.FromArgb(glass ? (byte)6 : (byte)0, 150, 195, 255), 1)
        }, new Point(0, 0), new Point(1, 1));
    }

    private static SolidColorBrush Brush(string hex, double alpha)
    {
        Color c = (Color)ColorConverter.ConvertFromString(hex);
        return new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B));
    }
    private static LinearGradientBrush Gradient(string first, string second, double alpha)
    {
        return new LinearGradientBrush(new GradientStopCollection
        {
            new(Brush(first, alpha).Color, 0), new(Brush(second, alpha).Color, 1)
        }, new Point(0, 0), new Point(1, 1));
    }
}
