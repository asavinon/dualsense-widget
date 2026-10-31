using System;
using System.IO;
using System.Text.Json;

namespace DualSenseWidget;
public sealed class Settings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Pinned { get; set; } = true;
    public bool Compact { get; set; }
    public bool Restore { get; set; }
    public byte Red { get; set; } = 72;
    public byte Green { get; set; } = 175;
    public byte Blue { get; set; } = 255;
    public double Brightness { get; set; } = 70;
    public byte Players { get; set; } = 4;
    public LightingEffect Effect { get; set; } = LightingEffect.Static;
    public double EffectSpeed { get; set; } = 1;
    public WidgetTheme Theme { get; set; } = WidgetTheme.Glass;
    public double PanelOpacity { get; set; } = 76;
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualSenseWidget", "settings.json");
    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException) { return new(); }
    }
    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(FilePath + ".tmp", FilePath, true);
            return true;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return false; }
    }
}
