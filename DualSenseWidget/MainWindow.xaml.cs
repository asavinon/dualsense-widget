using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DualSenseWidget;
public partial class MainWindow : Window
{
    private readonly ControllerService controller = new();
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer saveDelay = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer preview = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Stopwatch previewClock = Stopwatch.StartNew();
    private readonly Settings settings = Settings.Load();
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private bool ready, updating, compact;
    private bool syncingWheel;
    private string? notice;
    private DateTime noticeUntil;
    private readonly bool smoke = Environment.GetCommandLineArgs().Contains("--smoke-test");

    public MainWindow() { InitializeComponent(); }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Red.Value = settings.Red; Green.Value = settings.Green; Blue.Value = settings.Blue;
        Brightness.Value = Math.Clamp(settings.Brightness, 0, 100);
        EffectChoice.SelectedIndex = Enum.IsDefined(settings.Effect) ? (int)settings.Effect : 0;
        EffectSpeed.Value = double.IsFinite(settings.EffectSpeed) ? Math.Clamp(settings.EffectSpeed, .25, 3) : 1;
        ThemeChoice.SelectedIndex = Enum.IsDefined(settings.Theme) ? (int)settings.Theme : 0;
        PanelOpacity.Value = double.IsFinite(settings.PanelOpacity) ? Math.Clamp(settings.PanelOpacity, 40, 100) : 76;
        var players = new[] { Player1, Player2, Player3, Player4, Player5 };
        for (int i = 0; i < 5; i++) players[i].IsChecked = (settings.Players & (1 << i)) != 0;
        PinOption.IsChecked = settings.Pinned; RestoreOption.IsChecked = settings.Restore;
        Topmost = settings.Pinned;
        var area = SystemParameters.WorkArea;
        double x = settings.Left is double sx && double.IsFinite(sx) ? sx : area.Right - Width - 24;
        double y = settings.Top is double sy && double.IsFinite(sy) ? sy : area.Top + 36;
        Left = Math.Clamp(x, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
        Top = Math.Clamp(y, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height));
        compact = settings.Compact; SetCompact();
        ready = true; ApplyAppearance(); UpdatePreview();
        controller.SetRestore(CurrentProgram(), !smoke && settings.Restore);
        controller.Start();
        refresh.Tick += (_, _) => Refresh(); refresh.Start();
        preview.Tick += (_, _) => RenderEffectPreview(); preview.Start();
        saveDelay.Tick += (_, _) => { saveDelay.Stop(); Save(); };
        LocationChanged += (_, _) => ScheduleSave();
        if (!smoke) CreateTray();
        Refresh();
        if (smoke)
        {
            try { await SmokeTest(); }
            catch (Exception ex)
            {
                string dir = SmokeDirectory(); Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "ui-error.txt"), ex.ToString());
                Environment.ExitCode = 1;
            }
            Close();
        }
    }

    private Lighting CurrentLighting()
    {
        var players = new[] { Player1, Player2, Player3, Player4, Player5 };
        byte mask = 0;
        for (int i = 0; i < 5; i++) if (players[i].IsChecked == true) mask |= (byte)(1 << i);
        return new((byte)Red.Value, (byte)Green.Value, (byte)Blue.Value, mask, Brightness.Value / 100);
    }
    private void OpenAppearance(object sender, RoutedEventArgs e) => AppearancePopup.IsOpen = !AppearancePopup.IsOpen;
    private void CloseAppearance(object sender, RoutedEventArgs e) => AppearancePopup.IsOpen = false;
    private void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        // Give each see-through theme a useful starting point; the slider can
        // then adjust it without fading the window's text or control content.
        if (ThemeChoice.SelectedIndex == (int)WidgetTheme.Glass) PanelOpacity.Value = 76;
        if (ThemeChoice.SelectedIndex == (int)WidgetTheme.Translucent) PanelOpacity.Value = 58;
        ApplyAppearance(); ScheduleSave();
    }
    private void PanelOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ready) { ApplyAppearance(); ScheduleSave(); }
    }
    private void ApplyAppearance()
    {
        var theme = (WidgetTheme)Math.Clamp(ThemeChoice.SelectedIndex, 0, 2);
        WidgetAppearance.Apply(theme, PanelOpacity.Value);
        PanelOpacity.IsEnabled = theme != WidgetTheme.Dark;
        PanelOpacityText.Text = theme == WidgetTheme.Dark ? "100%" : $"{PanelOpacity.Value:0}%";
        ThemeHint.Text = theme switch
        {
            WidgetTheme.Glass => "Tinted glass with soft reflective highlights.",
            WidgetTheme.Translucent => "A simple see-through tint. Text stays opaque.",
            _ => "Solid dark surfaces for maximum contrast."
        };
    }
    private LightingProgram CurrentProgram() => new(CurrentLighting(), (LightingEffect)Math.Max(0, EffectChoice.SelectedIndex), EffectSpeed.Value);
    private void EffectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        previewClock.Restart(); UpdatePreview();
    }
    private void EffectSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ready) { previewClock.Restart(); UpdatePreview(); }
    }
    private void StopEffectClick(object sender, RoutedEventArgs e)
    {
        EffectChoice.SelectedIndex = 0;
        if (controller.Apply(CurrentProgram())) Notice("Stopping animation · applying solid color…");
        Save();
    }
    private void RenderEffectPreview()
    {
        if (!ready || !IsVisible) return;
        var state = controller.State;
        var frame = LightingEffects.Frame(CurrentProgram(), previewClock.Elapsed.TotalSeconds,
            state.Telemetry?.IsFresh(DateTimeOffset.UtcNow) == true ? state.Battery : null);
        Color color = Color.FromRgb(frame.Red, frame.Green, frame.Blue);
        ColorPreview.Background = new SolidColorBrush(color);
        ColorPreview.Opacity = frame.Brightness;
        ControllerGlow.Stroke = new SolidColorBrush(color);
        ControllerGlow.Opacity = frame.Brightness;
        PreviewPlayers(frame.Players);
    }
    private void PreviewPlayers(byte mask)
    {
        var dots = new[] { PreviewPlayer1, PreviewPlayer2, PreviewPlayer3, PreviewPlayer4, PreviewPlayer5 };
        for (int i = 0; i < dots.Length; i++) dots[i].Opacity = (mask & (1 << i)) != 0 ? 1 : .12;
    }
    private void ColorChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (ready && !updating) UpdatePreview(); }
    private void WheelColorPicked(object? sender, EventArgs e)
    {
        if (!ready || syncingWheel) return;
        Color color = ColorWheel.SelectedColor;
        updating = true; Red.Value = color.R; Green.Value = color.G; Blue.Value = color.B; updating = false;
        // Manual wheel input takes control of the light bar so even a running
        // rainbow/battery effect cannot mask the color the user is dragging to.
        EffectChoice.SelectedIndex = (int)LightingEffect.Static;
        UpdatePreview();
        if (!smoke)
        {
            if (controller.Apply(CurrentProgram()))
            {
                notice = "Live color · controller follows the picker";
                noticeUntil = DateTime.UtcNow.AddSeconds(2);
            }
            else Notice("Color selected · connect a controller for live lighting.");
        }
    }
    private void WheelValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ready && !syncingWheel) ColorWheel.SetValue(WheelValue.Value / 100);
    }
    private void UpdatePreview()
    {
        var lights = CurrentLighting();
        Color color = Color.FromRgb(lights.Red, lights.Green, lights.Blue);
        syncingWheel = true;
        ColorWheel.SetColor(color); WheelValue.Value = ColorWheel.Value * 100;
        syncingWheel = false;
        ColorPreview.Background = new SolidColorBrush(color);
        ColorPreview.Opacity = .15 + .85 * lights.Brightness;
        ControllerGlow.Stroke = new SolidColorBrush(color);
        ControllerGlow.Opacity = .15 + .85 * lights.Brightness;
        HexInput.Text = $"#{lights.Red:X2}{lights.Green:X2}{lights.Blue:X2}";
        HexInput.SetResourceReference(Control.BorderBrushProperty, "ControlBorder");
        RedValue.Text = lights.Red.ToString(); GreenValue.Text = lights.Green.ToString(); BlueValue.Text = lights.Blue.ToString();
        BrightnessValue.Text = $"{Brightness.Value:0}%";
        var program = CurrentProgram();
        EffectPreviewLabel.Text = LightingEffects.Name(program.Effect) + " preview";
        SpeedValue.Text = $"{EffectSpeed.Value:0.00}×";
        EffectSpeed.IsEnabled = program.Effect != LightingEffect.Static;
        EffectHint.Text = program.Effect == LightingEffect.PlayerChase ? "Chase sweeps all five player lights. Press Apply to start." : program.Effect == LightingEffect.Battery ? "Charging: amber pulse · full: green · low: red. Apply to start." : "Effects start with Apply. Stop returns to a solid color.";
        controller.SetRestore(program, !smoke && RestoreOption.IsChecked == true);
        RenderEffectPreview();
        ScheduleSave();
    }
    private void SetColor(string hex)
    {
        Color color = (Color)ColorConverter.ConvertFromString(hex);
        updating = true; Red.Value = color.R; Green.Value = color.G; Blue.Value = color.B; updating = false;
        UpdatePreview();
    }
    private void PresetClick(object sender, RoutedEventArgs e) => SetColor((string)((Button)sender).Tag);
    private void HexLostFocus(object sender, RoutedEventArgs e) => CommitHex();
    private void HexKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { CommitHex(); e.Handled = true; } }
    private bool CommitHex()
    {
        string hex = HexInput.Text.Trim().TrimStart('#');
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit))
        {
            HexInput.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 118, 108));
            Notice("Use a six-digit color, such as #48AFFF."); return false;
        }
        SetColor("#" + hex); return true;
    }
    private void PlayerChanged(object sender, RoutedEventArgs e) => UpdatePreview();
    private void ApplyClick(object sender, RoutedEventArgs e)
    {
        if (!CommitHex()) return;
        if (controller.Apply(CurrentProgram())) Notice("Sending LED settings…");
        else Notice("Connect a controller with LED access first.");
        Save();
    }
    private void ReleaseClick(object sender, RoutedEventArgs e)
    {
        if (controller.Apply(CurrentProgram(), release: true)) Notice("Returning LED control to the controller…");
    }
    private void OptionChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        Topmost = PinOption.IsChecked == true;
        controller.SetRestore(CurrentProgram(), !smoke && RestoreOption.IsChecked == true);
        ScheduleSave();
    }
    private void Refresh()
    {
        var state = controller.State;
        ControllerName.Text = state.Name;
        ConnectionText.Text = state.Connection;
        BatteryValue.Text = state.Battery?.Percent?.ToString() ?? "—";
        PercentSign.Visibility = state.Battery?.Percent != null ? Visibility.Visible : Visibility.Collapsed;
        BatteryBar.Value = state.Battery?.Percent ?? 0;
        BatteryBar.Foreground = new SolidColorBrush(state.Battery?.Percent <= 15 ? Color.FromRgb(255, 145, 108) : Color.FromRgb(109, 206, 236));
        BatteryCaption.Text = state.Battery != null ? state.Battery.State + (state.Battery.Percent != null ? " · estimated" : "") : state.Connected ? "Waiting for battery report" : "Waiting for controller";
        BatteryCaption.ToolTip = "DualSense reports 10% charge bands. The display uses the midpoint: 0–9% = 5%, 10–19% = 15%, and so on.";
        UpdateCharging(state, DateTimeOffset.UtcNow);
        ApplyButton.IsEnabled = ReleaseButton.IsEnabled = state.Connected && state.Writable;
        StopEffectButton.IsEnabled = state.Connected && state.Writable && controller.ActiveLighting is { Effect: not LightingEffect.Static };
        StatusText.Text = notice != null && DateTime.UtcNow < noticeUntil ? notice : state.Message;
        if (tray != null)
        {
            string label = "DualSense · " + (state.Battery?.Percent is int pct ? pct + "% · " + state.Battery.State : state.Connection);
            tray.Text = label[..Math.Min(63, label.Length)];
        }
    }
    private void UpdateCharging(ControllerState state, DateTimeOffset now)
    {
        var telemetry = state.Telemetry;
        bool fresh = state.Connected && telemetry?.IsFresh(now) == true;
        BatteryValue.Opacity = BatteryBar.Opacity = fresh ? 1 : .45;
        if (telemetry == null)
        {
            ChargeSessionText.Text = state.Connected ? "Waiting for charging information" : "Charging session starts when detected";
            LiveReportText.Text = "Waiting for live report";
            LiveDot.Foreground = new SolidColorBrush(Color.FromRgb(118, 134, 158));
            LiveDot.BeginAnimation(OpacityProperty, null); LiveDot.Opacity = 1;
            LiveDot.Tag = null;
            return;
        }
        double age = Math.Max(0, (now - telemetry.LastReportAt).TotalSeconds);
        LiveReportText.Text = fresh ? "LIVE · updated just now" : $"STALE · last report {age:0}s ago";
        LiveReportText.ToolTip = $"Latest controller report: {telemetry.LastReportAt.ToLocalTime():h:mm:ss tt}\nLast battery or charging-state change: {telemetry.LastChangedAt.ToLocalTime():h:mm:ss tt}";
        LiveDot.Foreground = new SolidColorBrush(fresh ? Color.FromRgb(106, 221, 176) : Color.FromRgb(255, 177, 108));
        bool charging = telemetry.Battery.State == "Charging";
        if (charging && fresh)
        {
            if (!Equals(LiveDot.Tag, "charging"))
            {
                LiveDot.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(.35, 1, TimeSpan.FromMilliseconds(700))
                { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
                LiveDot.Tag = "charging";
            }
        }
        else { LiveDot.BeginAnimation(OpacityProperty, null); LiveDot.Opacity = 1; LiveDot.Tag = null; }
        if (!fresh) ChargeSessionText.Text = "Waiting for fresh charging information";
        else if (telemetry.ChargingSince is DateTimeOffset since)
        {
            TimeSpan elapsed = now - since;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            string duration = elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}" : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
            ChargeSessionText.Text = $"Charging observed {duration}" + (telemetry.ChargeGained is int gain ? $" · +{gain}% this session" : "");
        }
        else if (telemetry.Battery.State == "Fully charged") ChargeSessionText.Text = "Charge complete · 100%";
        else if (telemetry.Battery.State == "On battery") ChargeSessionText.Text = "On battery · connect power to charge";
        else ChargeSessionText.Text = "Charging paused or unavailable";
    }
    private void Notice(string text) { notice = text; noticeUntil = DateTime.UtcNow.AddSeconds(3); Refresh(); }
    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source != null) { if (source is ButtonBase) return; source = VisualTreeHelper.GetParent(source); }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void ToggleCompact(object sender, RoutedEventArgs e) { compact = !compact; SetCompact(); ScheduleSave(); }
    private void SetCompact()
    {
        ExpandedContent.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Height = compact ? 301 : 778;
        CompactButton.ToolTip = compact ? "Expand LED controls" : "Toggle compact view";
        if (Top + Height > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
            Top = Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height);
    }
    private void HideToTray(object sender, RoutedEventArgs e) { if (tray != null) Hide(); else WindowState = WindowState.Minimized; }
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show widget", null, (_, _) => Dispatcher.Invoke(ShowWidget));
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Close));
        using (var iconResource = Application.GetResourceStream(new Uri("pack://application:,,,/Widget.ico")).Stream)
            trayIcon = new System.Drawing.Icon(iconResource);
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "DualSense Widget", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWidget);
    }
    private void ShowWidget() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void ScheduleSave() { if (!ready || smoke) return; saveDelay.Stop(); saveDelay.Start(); }
    private void Save()
    {
        if (!ready || smoke) return;
        var lights = CurrentLighting();
        settings.Left = Left; settings.Top = Top; settings.Compact = compact;
        settings.Pinned = PinOption.IsChecked == true; settings.Restore = RestoreOption.IsChecked == true;
        settings.Red = lights.Red; settings.Green = lights.Green; settings.Blue = lights.Blue;
        settings.Brightness = Brightness.Value; settings.Players = lights.Players;
        settings.Effect = CurrentProgram().Effect; settings.EffectSpeed = EffectSpeed.Value;
        settings.Theme = (WidgetTheme)Math.Clamp(ThemeChoice.SelectedIndex, 0, 2); settings.PanelOpacity = PanelOpacity.Value;
        if (!settings.Save()) Notice("Settings could not be saved. Check folder permissions.");
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        Save(); refresh.Stop(); saveDelay.Stop(); preview.Stop(); controller.Dispose();
        if (tray != null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
        trayIcon?.Dispose();
    }

    private string SmokeDirectory()
    {
        string[] args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "--smoke-test");
        return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : AppContext.BaseDirectory;
    }
    private async Task SmokeTest()
    {
        await Task.Delay(1500);
        string dir = SmokeDirectory(); Directory.CreateDirectory(dir);
        var initialTelemetry = controller.State.Telemetry;
        if (initialTelemetry != null)
        {
            await Task.Delay(1250);
            var later = controller.State.Telemetry ?? throw new Exception("Live telemetry disappeared");
            if (later.LastReportAt <= initialTelemetry.LastReportAt) throw new Exception("Report heartbeat is not advancing");
            UpdateCharging(controller.State, later.LastReportAt.AddSeconds(5));
            if (!LiveReportText.Text.StartsWith("STALE")) throw new Exception("Stale report not indicated");
            Refresh();
            if (!LiveReportText.Text.StartsWith("LIVE")) throw new Exception("Fresh report not indicated");
        }
        compact = false; SetCompact(); UpdateLayout();
        var original = CurrentLighting();
        SetColor("#FFFFFF");
        ColorWheel.Pick(new Point(ColorWheel.ActualWidth - 8, ColorWheel.ActualHeight / 2));
        if (HexInput.Text != "#FF0000" || Red.Value != 255 || Green.Value != 0) throw new Exception("Wheel selection did not update RGB and hex");
        if (CurrentProgram().Effect != LightingEffect.Static) throw new Exception("Wheel input did not take manual color control");
        ColorWheel.Pick(new Point(ColorWheel.ActualWidth / 2, ColorWheel.ActualHeight / 2));
        if (HexInput.Text != "#FFFFFF") throw new Exception("Wheel center did not desaturate the color");
        WheelValue.Value = 0;
        if (HexInput.Text != "#000000") throw new Exception("Wheel value slider did not reach black");
        WheelValue.Value = 100;
        SetColor("#0000FF");
        if (ColorWheel.SelectedColor != Colors.Blue) throw new Exception("Hex color did not sync back to wheel");
        SetColor("#6ADDB0");
        if (CurrentLighting().Green != 221) throw new Exception("Preset / RGB conversion failed");
        HexInput.Text = "invalid";
        if (CommitHex()) throw new Exception("Invalid color accepted");
        SetColor($"#{original.Red:X2}{original.Green:X2}{original.Blue:X2}");
        notice = null; Refresh(); UpdateLayout();
        var bottom = ExpandedContent.TranslatePoint(new Point(0, ExpandedContent.ActualHeight), this);
        if (bottom.Y > ActualHeight - 4) throw new Exception($"Controls overflow: {bottom.Y} / {ActualHeight}");
        Capture(Path.Combine(dir, "widget-full.png"));
        int originalEffect = EffectChoice.SelectedIndex;
        double originalSpeed = EffectSpeed.Value;
        EffectChoice.SelectedIndex = (int)LightingEffect.Rainbow;
        EffectSpeed.Value = 2;
        await Task.Delay(160);
        Color previewColor = ((SolidColorBrush)ColorPreview.Background).Color;
        await Task.Delay(160);
        if (((SolidColorBrush)ColorPreview.Background).Color == previewColor) throw new Exception("Dynamic preview is not animating");
        if (controller.ActiveLighting != null) throw new Exception("Preview sent LEDs without Apply");
        UpdateLayout();
        if (ColorWheel.ActualWidth < 150) throw new Exception("Radial picker was not laid out");
        Capture(Path.Combine(dir, "widget-effects.png"));
        EffectChoice.SelectedIndex = originalEffect; EffectSpeed.Value = originalSpeed;
        int originalTheme = ThemeChoice.SelectedIndex;
        double originalOpacity = PanelOpacity.Value;
        foreach (WidgetTheme theme in Enum.GetValues<WidgetTheme>())
        {
            ThemeChoice.SelectedIndex = (int)theme; ApplyAppearance();
            UpdateLayout();
            var background = (LinearGradientBrush)ShellFrame.Background;
            bool transparent = background.GradientStops.All(stop => stop.Color.A < 255);
            if (theme != WidgetTheme.Dark && !transparent) throw new Exception("See-through theme is opaque");
            if (theme == WidgetTheme.Dark && background.GradientStops.Any(stop => stop.Color.A != 255)) throw new Exception("Dark theme must be solid");
            if (Opacity != 1) throw new Exception("Theme faded the whole window instead of its surfaces");
            Capture(Path.Combine(dir, "widget-" + theme.ToString().ToLowerInvariant() + ".png"));
        }
        ThemeChoice.SelectedIndex = originalTheme; PanelOpacity.Value = originalOpacity; ApplyAppearance();
        compact = true; SetCompact(); UpdateLayout();
        Capture(Path.Combine(dir, "widget-compact.png"));
        File.WriteAllText(Path.Combine(dir, "ui-check.json"), JsonSerializer.Serialize(new { Passed = true, ExpandedBottom = bottom.Y, Themes = "Glass / Translucent / Dark", State = controller.State }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private void Capture(string path)
    {
        var bitmap = new RenderTargetBitmap((int)ActualWidth * 2, (int)ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
