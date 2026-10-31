using System;

namespace DualSenseWidget;

public record BatteryTelemetry(BatteryInfo Battery, DateTimeOffset LastReportAt,
    DateTimeOffset LastChangedAt, DateTimeOffset? ChargingSince, int? ChargingStartPercent)
{
    public int? ChargeGained => ChargingSince != null && Battery.Percent is int now && ChargingStartPercent is int start
        ? Math.Max(0, now - start) : null;
    public bool IsFresh(DateTimeOffset now) => now - LastReportAt <= TimeSpan.FromSeconds(3);
}

// Tracks the charging session observed by this app, rather than guessing when
// a controller was plugged in before the app started.
public sealed class ChargeTracker
{
    public BatteryTelemetry? Current { get; private set; }
    public BatteryTelemetry Observe(BatteryInfo battery, DateTimeOffset now)
    {
        var previous = Current;
        bool charging = battery.State == "Charging";
        bool continuing = charging && previous?.Battery.State == "Charging";
        Current = new(battery, now, previous?.Battery == battery ? previous.LastChangedAt : now,
            charging ? continuing ? previous!.ChargingSince : now : null,
            charging ? continuing ? previous!.ChargingStartPercent ?? battery.Percent : battery.Percent : null);
        return Current;
    }
}
