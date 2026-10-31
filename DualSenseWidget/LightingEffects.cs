using System;

namespace DualSenseWidget;

public enum LightingEffect { Static, Rainbow, Breathe, PlayerChase, Battery }
public record LightingProgram(Lighting Base, LightingEffect Effect = LightingEffect.Static, double Speed = 1);

public static class LightingEffects
{
    public static string Name(LightingEffect effect) => effect switch
    {
        LightingEffect.Rainbow => "Rainbow", LightingEffect.Breathe => "Breathe",
        LightingEffect.PlayerChase => "Player chase", LightingEffect.Battery => "Battery reactive", _ => "Static"
    };

    public static Lighting Frame(LightingProgram program, double seconds, BatteryInfo? battery = null)
    {
        double speed = double.IsFinite(program.Speed) ? Math.Clamp(program.Speed, .25, 3) : 1;
        double time = double.IsFinite(seconds) ? Math.Max(0, seconds) * speed : 0;
        var basis = program.Base with { Brightness = Math.Clamp(program.Base.Brightness, 0, 1) };
        // Start a breathing cycle at full intensity so Apply gives immediate feedback.
        double pulse = .12 + .88 * (.5 + .5 * Math.Cos(time * Math.PI / 2));
        return program.Effect switch
        {
            LightingEffect.Rainbow => Hue(basis, time / 8 % 1),
            LightingEffect.Breathe => basis with { Brightness = basis.Brightness * pulse },
            LightingEffect.PlayerChase => basis with { Players = (byte)(1 << ChasePosition(time)) },
            LightingEffect.Battery => BatteryFrame(basis, battery, pulse),
            _ => basis
        };
    }

    private static int ChasePosition(double time)
    {
        int step = (int)(Math.Floor(time * 3) % 8);
        return step <= 4 ? step : 8 - step;
    }
    private static Lighting BatteryFrame(Lighting basis, BatteryInfo? battery, double pulse)
    {
        if (battery?.Percent == null) return basis;
        if (battery.State == "Fully charged") return basis with { Red = 76, Green = 235, Blue = 145 };
        if (battery.State == "Charging") return basis with { Red = 255, Green = 150, Blue = 44, Brightness = basis.Brightness * pulse };
        var color = Hue(basis, Math.Clamp(battery.Percent.Value / 100d, 0, 1) / 3);
        return battery.Percent <= 15 ? color with { Brightness = basis.Brightness * pulse } : color;
    }
    private static Lighting Hue(Lighting basis, double hue)
    {
        double h = hue * 6;
        double x = 255 * (1 - Math.Abs(h % 2 - 1));
        (double r, double g, double b) = (int)h switch
        {
            0 => (255d, x, 0d), 1 => (x, 255d, 0d), 2 => (0d, 255d, x),
            3 => (0d, x, 255d), 4 => (x, 0d, 255d), _ => (255d, 0d, x)
        };
        return basis with { Red = (byte)Math.Round(r), Green = (byte)Math.Round(g), Blue = (byte)Math.Round(b) };
    }
}
