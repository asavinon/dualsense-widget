using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DualSenseWidget;

public record ControllerState(bool Connected, bool Writable, string Name, string Connection, BatteryInfo? Battery, string Message, BatteryTelemetry? Telemetry = null);

public sealed class ControllerService : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<LedCommand> commands = Channel.CreateBounded<LedCommand>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private ControllerState current = new(false, false, "DualSense", "SEARCHING", null, "Connect by USB or pair in Windows Bluetooth settings.");
    private LightingProgram? restore;
    private int restoreEnabled;
    private Task? worker;
    private readonly object stateGate = new();
    public ControllerState State => Volatile.Read(ref current);
    public void Start() => worker ??= Task.Run(Run);
    public LightingProgram? ActiveLighting => Volatile.Read(ref activeLighting);
    private LightingProgram? activeLighting;
    public void SetRestore(LightingProgram lighting, bool enabled) { Volatile.Write(ref restore, lighting); Volatile.Write(ref restoreEnabled, enabled ? 1 : 0); }
    public bool Apply(LightingProgram lighting, bool release = false)
    {
        if (!State.Connected || !State.Writable) return false;
        commands.Writer.TryWrite(new(lighting, release));
        return true;
    }

    private async Task Run()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var devices = HidNative.Discover();
                if (devices.Count == 0)
                    Volatile.Write(ref current, new(false, false, "DualSense", "NOT CONNECTED", null, "Connect by USB or pair in Windows Bluetooth settings."));
                foreach (var device in devices)
                {
                    try { await Connected(device, stop.Token); break; }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                    catch (Exception e) when (e is IOException || e is Win32Exception || e is UnauthorizedAccessException || e is OperationCanceledException)
                    { Volatile.Write(ref current, new(false, false, "DualSense", "RECONNECTING", null, "Controller unavailable. Check the connection, DS4Windows or HidHide. Retrying automatically.")); }
                }
            }
            catch (Exception e) when (e is Win32Exception || e is IOException || e is UnauthorizedAccessException)
            { Volatile.Write(ref current, new(false, false, "DualSense", "ACCESS UNAVAILABLE", null, "Windows could not open the controller. Retrying automatically.")); }
            try { await Task.Delay(2500, stop.Token); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task Connected(HidDevice device, CancellationToken token)
    {
        using var stream = HidNative.Open(device, out bool writable);
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
        while (commands.Reader.TryRead(out _)) { }
        Volatile.Write(ref current, new(true, writable, device.Edge ? "DualSense Edge" : "DualSense", device.Bluetooth ? "BLUETOOTH" : "USB", null,
            writable ? "Connected · waiting for battery report" : "Battery only · LED access blocked by another app"));
        if (writable && Volatile.Read(ref restoreEnabled) == 1 && Volatile.Read(ref restore) is LightingProgram saved)
            commands.Writer.TryWrite(new(saved));
        Task reading = Read(stream, device, connection.Token);
        Task writing = Write(stream, device, connection.Token);
        try
        {
            Task finished = await Task.WhenAny(reading, writing);
            await finished;
        }
        finally
        {
            connection.Cancel();
            try { await Task.WhenAll(reading, writing); }
            catch (Exception e) when (e is IOException || e is OperationCanceledException || e is ObjectDisposedException) { }
            Volatile.Write(ref activeLighting, null);
        }
    }

    private async Task Read(FileStream stream, HidDevice device, CancellationToken token)
    {
        byte[] report = new byte[device.InputLength];
        var tracker = new ChargeTracker();
        DateTimeOffset published = DateTimeOffset.MinValue;
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            int count = await stream.ReadAsync(report.AsMemory(), timeout.Token);
            if (count == 0) throw new IOException("Controller disconnected");
            var battery = Protocol.ReadBattery(report.AsSpan(0, count), device.Bluetooth);
            if (battery != null)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                bool changed = tracker.Current?.Battery != battery;
                var telemetry = tracker.Observe(battery, now);
                // Input can arrive hundreds of times a second. Publish a live
                // heartbeat five times a second; publish charge changes immediately.
                if (!changed && now - published < TimeSpan.FromMilliseconds(200)) continue;
                published = now;
                lock (stateGate)
                {
                    var state = State;
                    Volatile.Write(ref current, state with
                    {
                        Battery = battery, Telemetry = telemetry,
                        Message = state.Message == "Connected · waiting for battery report" ? "Ready · choose your lights, then press Apply." : state.Message
                    });
                }
            }
        }
    }

    private Task Write(FileStream stream, HidDevice device, CancellationToken token)
    {
        return LightingOutput.Run(stream, device.Bluetooth, commands.Reader,
            () => State.Telemetry?.IsFresh(DateTimeOffset.UtcNow) == true ? State.Battery : null,
            (program, message) =>
        {
            Volatile.Write(ref activeLighting, program);
            lock (stateGate)
            {
                var state = State;
                Volatile.Write(ref current, state with { Message = message });
            }
        }, token);
    }

    public void Dispose() { stop.Cancel(); }
}
