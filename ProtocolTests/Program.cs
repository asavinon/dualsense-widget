using DualSenseWidget;
using System.Buffers.Binary;
using System.Threading.Channels;

if (args.Contains("--probe"))
{
    var devices = HidNative.Discover();
    Console.WriteLine($"Compatible HID devices: {devices.Count}");
    Console.WriteLine(HidNative.Diagnostics);
    foreach (var device in devices)
    {
        Console.WriteLine($"{(device.Edge ? "DualSense Edge" : "DualSense")} / {(device.Bluetooth ? "Bluetooth" : "USB")}");
        try
        {
            using var stream = HidNative.Open(device, out bool write);
            Console.WriteLine($"LED write access: {write}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            byte[] report = new byte[device.InputLength];
            while (true)
            {
                int count = await stream.ReadAsync(report, timeout.Token);
                if (count == 0) break;
                var battery = Protocol.ReadBattery(report.AsSpan(0, count), device.Bluetooth);
                if (battery != null) { Console.WriteLine($"Battery: {battery.Percent?.ToString() ?? "unknown"}% / {battery.State}"); break; }
            }
        }
        catch (Exception e) { Console.WriteLine($"Probe: {e.GetType().Name}: {e.Message}"); }
    }
    return;
}

int checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
byte[] Usb(byte status) { var data = new byte[64]; data[0] = 1; data[53] = status; return data; }
byte[] Bt(byte status)
{
    var data = new byte[78]; data[0] = 0x31; data[54] = status;
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(74), Protocol.Crc(0xA1, data.AsSpan(0, 74))); return data;
}
// CRC reference independently calculated with Node's native zlib.crc32 for prefix A2 + 74 zero bytes.
Check(Protocol.Crc(0xA2, new byte[74]) == 0x0EAA90A9u, "CRC external reference vector");
for (byte n = 0; n <= 10; n++)
{
    Check(Protocol.ReadBattery(Usb(n), false)?.Percent == Math.Min(n * 10 + 5, 100), "USB charge band");
    Check(Protocol.ReadBattery(Bt((byte)(0x10 | n)), true)?.Percent == Math.Min(n * 10 + 5, 100), "Bluetooth charge band");
}
Check(Protocol.ReadBattery(Usb(0x20), false) is { Percent: 100, State: "Fully charged" }, "Full battery");
Check(Protocol.ReadBattery(Usb(0xA5), false)?.Percent == null, "Temperature fault must not look like 0%");
Check(Protocol.ReadBattery(Usb(0xF5), false)?.Percent == null, "Charging fault");
Check(Protocol.ReadBattery(Usb(0x0F), false)?.Percent == null, "Invalid level");
Check(Protocol.ReadBattery(new byte[] { 1, 2 }, true) == null, "Short compatibility input rejected");
Check(Protocol.ReadBattery(Usb(5), true) == null, "USB layout rejected on Bluetooth");
var corrupt = Bt(5); corrupt[54] ^= 1;
Check(Protocol.ReadBattery(corrupt, true) == null, "Corrupt Bluetooth input rejected");
var lights = new Lighting(100, 200, 80, 0b10101, .5);
byte[] output = Protocol.Output(false, 0, lights);
Check(output.Length == 63 && output[0] == 2, "USB output framing");
Check(output[2] == 0x14 && output[1] == 0, "LED-only enable flags");
Check(output[44] == 21 && output[45] == 50 && output[46] == 100 && output[47] == 40, "LED payload positions and brightness");
Check(output[3..39].All(x => x == 0), "No trigger, rumble or audio flags");
byte[] bluetooth = Protocol.Output(true, 17, lights);
Check(bluetooth.Length == 78 && bluetooth[0] == 0x31 && bluetooth[1] == 0x10 && bluetooth[2] == 0x10, "Bluetooth framing / sequence wrap");
Check(bluetooth[46] == 21 && bluetooth[47] == 50 && bluetooth[48] == 100 && bluetooth[49] == 40, "Bluetooth payload");
byte[] setup = Protocol.Output(false, 0, lights, setup: true);
Check(setup[39] == 2 && setup[42] == 2 && setup[2] == 0, "Lightbar initialization");
byte[] release = Protocol.Output(false, 0, lights, release: true);
Check(release[2] == 8 && release[39] == 0, "Release LEDs");
Check(Protocol.Output(false, 0, lights with { Brightness = -1 })[45] == 0, "Brightness lower bound");
Check(Protocol.Output(false, 0, lights with { Brightness = 2 })[45] == 100, "Brightness upper bound");
var start = DateTimeOffset.Parse("2026-10-31T12:00:00Z");
var tracker = new ChargeTracker();
var charge = tracker.Observe(new(15, "Charging"), start);
Check(charge.ChargingSince == start && charge.ChargeGained == 0, "Charging observation starts session");
charge = tracker.Observe(new(15, "Charging"), start.AddSeconds(2));
Check(charge.LastReportAt == start.AddSeconds(2), "Unchanged battery still updates live report time");
Check(charge.LastChangedAt == start && charge.ChargingSince == start, "Unchanged report preserves session and change time");
Check(charge.IsFresh(start.AddSeconds(4)), "Recent heartbeat is fresh");
Check(!charge.IsFresh(start.AddSeconds(6)), "Missing heartbeat becomes stale");
charge = tracker.Observe(new(25, "Charging"), start.AddMinutes(12));
Check(charge.ChargeGained == 10 && charge.ChargingSince == start, "Charge band gains accumulate in same session");
Check(charge.LastChangedAt == start.AddMinutes(12), "Battery change time updates");
charge = tracker.Observe(new(25, "On battery"), start.AddMinutes(13));
Check(charge.ChargingSince == null && charge.ChargeGained == null, "Unplug ends observed charging session");
charge = tracker.Observe(new(25, "Charging"), start.AddMinutes(14));
Check(charge.ChargingSince == start.AddMinutes(14) && charge.ChargeGained == 0, "Replug starts a new session");
charge = tracker.Observe(new(100, "Fully charged"), start.AddMinutes(90));
Check(charge.ChargingSince == null && charge.Battery.Percent == 100, "Full charge ends charging timer");
charge = tracker.Observe(new(null, "Charging paused · temperature / voltage"), start.AddMinutes(91));
Check(charge.Battery.Percent == null && charge.ChargingSince == null, "Charging fault does not continue timer");
Check(new ChargeTracker().Current == null, "New connection starts with no stale telemetry");
var baseLights = new Lighting(80, 140, 200, 21, .6);
var program = new LightingProgram(baseLights);
Check(LightingEffects.Frame(program, 99) == baseLights, "Static effect leaves settings unchanged");
var rainbow = program with { Effect = LightingEffect.Rainbow };
Check(LightingEffects.Frame(rainbow, 0).Red == 255 && LightingEffects.Frame(rainbow, 0).Green == 0, "Rainbow starts red");
Check(LightingEffects.Frame(rainbow, 8d / 3).Green == 255, "Rainbow reaches green");
Check(LightingEffects.Frame(rainbow, 1) == LightingEffects.Frame(rainbow, 9), "Rainbow loops continuously");
Check(LightingEffects.Frame(rainbow with { Speed = 2 }, 1) == LightingEffects.Frame(rainbow, 2), "Effect speed scales time");
var breathing = program with { Effect = LightingEffect.Breathe };
Check(Math.Abs(LightingEffects.Frame(breathing, 0).Brightness - .6) < .001, "Breathe starts at chosen brightness");
Check(LightingEffects.Frame(breathing, 2).Brightness < .08, "Breathe dims between cycles");
Check(LightingEffects.Frame(breathing, 4).Red == 80, "Breathe preserves selected hue");
var chase = program with { Effect = LightingEffect.PlayerChase };
Check(LightingEffects.Frame(chase, 0).Players == 1 && LightingEffects.Frame(chase, 4d / 3).Players == 16 && LightingEffects.Frame(chase, 2).Players == 4, "Player chase bounces from left to right and back");
var reactive = program with { Effect = LightingEffect.Battery };
Check(LightingEffects.Frame(reactive, 0, new(35, "Charging")).Red == 255, "Charging reactive mode uses amber");
Check(LightingEffects.Frame(reactive, 0, new(100, "Fully charged")).Green == 235, "Full charge reactive mode uses green");
Check(LightingEffects.Frame(reactive, 0, null) == baseLights, "Unknown battery does not invent a battery color");
Check(LightingEffects.Frame(reactive, 2, new(5, "On battery")).Brightness < .08, "Low battery reactive mode pulses");
foreach (LightingEffect effect in Enum.GetValues<LightingEffect>())
{
    bool valid = Enumerable.Range(0, 300).All(n => {
        Lighting frame = LightingEffects.Frame(program with { Effect = effect, Speed = 3 }, n / 20d, new(5, "Charging"));
        return frame.Brightness >= 0 && frame.Brightness <= baseLights.Brightness && frame.Players <= 31;
    });
    Check(valid, $"{effect} honors brightness and player-light bounds");
}

async Task WaitUntil(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    while (!condition()) await Task.Delay(10, timeout.Token);
}
foreach (bool isBluetooth in new[] { false, true })
{
    using var outputStream = new RecordingStream();
    var channel = Channel.CreateBounded<LedCommand>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    using var cancellation = new CancellationTokenSource();
    LightingProgram? active = null;
    int changes = 0;
    var pump = LightingOutput.Run(outputStream, isBluetooth, channel.Reader, () => new(35, "Charging"),
        (mode, message) => { Volatile.Write(ref active, mode); Interlocked.Increment(ref changes); }, cancellation.Token);
    await channel.Writer.WriteAsync(new(rainbow with { Speed = 3 }));
    await WaitUntil(() => outputStream.Count >= 6);
    var frames = outputStream.Snapshot();
    int payload = isBluetooth ? 3 : 1;
    Check(frames[0].Bytes[payload + 38] == 2, "Output pump initializes lightbar before animation");
    Check(frames.Skip(1).Select(f => Convert.ToHexString(f.Bytes.AsSpan(payload + 44, 3))).Distinct().Count() >= 3, "Output pump sends changing animation colors");
    Check(frames.Skip(1).All(f => f.Bytes[payload] == 0 && f.Bytes[payload + 1] == 0x14), "Animation only sets LED output flags");
    Check(frames.Skip(2).Zip(frames.Skip(1), (later, earlier) => (later.At - earlier.At).TotalMilliseconds).All(gap => gap >= 35), "Animation output rate is bounded");
    if (isBluetooth)
    {
        Check(frames.All(f => BinaryPrimitives.ReadUInt32LittleEndian(f.Bytes.AsSpan(74)) == Protocol.Crc(0xA2, f.Bytes.AsSpan(0, 74))), "Every animation frame has a valid Bluetooth CRC");
        Check(frames.Select((f, n) => f.Bytes[1] == ((n & 15) << 4)).All(ok => ok), "Animation increments Bluetooth sequence numbers");
    }
    await channel.Writer.WriteAsync(new(program));
    await WaitUntil(() => Volatile.Read(ref active)?.Effect == LightingEffect.Static);
    int stopped = outputStream.Count;
    await Task.Delay(160);
    Check(outputStream.Count == stopped, "Stop switches to solid color and stops recurring writes");
    for (int drag = 0; drag < 200; drag++)
        await channel.Writer.WriteAsync(new(program with { Base = baseLights with { Red = (byte)drag } }));
    var finalDrag = program with { Base = baseLights with { Red = 17, Blue = 223 } };
    await channel.Writer.WriteAsync(new(finalDrag));
    await WaitUntil(() => Volatile.Read(ref active) == finalDrag);
    var lastDragFrame = outputStream.Snapshot().Last().Bytes;
    Check(lastDragFrame[payload + 44] == 10 && lastDragFrame[payload + 46] == 134, "Rapid drag delivers the final selected RGB color");
    var dragFrames = outputStream.Snapshot().Skip(stopped - 1).ToArray();
    Check(dragFrames.Skip(1).Zip(dragFrames, (later, earlier) => (later.At - earlier.At).TotalMilliseconds).All(gap => gap >= 35), "Rapid static drag remains rate limited");
    stopped = outputStream.Count;
    await Task.Delay(160);
    Check(outputStream.Count == stopped, "Drag ends on the final color without delayed stale writes");
    await channel.Writer.WriteAsync(new(program, Release: true));
    await WaitUntil(() => Volatile.Read(ref active) == null);
    var released = outputStream.Snapshot().Last().Bytes;
    Check(released[payload + 1] == 8, "Release stops animation and returns LED ownership");
    stopped = outputStream.Count;
    await Task.Delay(160);
    Check(outputStream.Count == stopped, "No writes continue after LED release");
    await channel.Writer.WriteAsync(new(breathing));
    await WaitUntil(() => outputStream.Count >= stopped + 3);
    Check(outputStream.Snapshot()[stopped].Bytes[payload + 38] == 2, "Animation reinitializes after LED release");
    cancellation.Cancel();
    try { await pump; } catch (OperationCanceledException) { }
    stopped = outputStream.Count;
    await Task.Delay(100);
    Check(outputStream.Count == stopped, "Disconnect cancellation stops all animation writes");
}
Console.WriteLine($"PASS: {checks} protocol, charging and lighting checks");

sealed class RecordingStream : Stream
{
    private readonly List<(DateTimeOffset At, byte[] Bytes)> frames = new();
    public int Count { get { lock (frames) return frames.Count; } }
    public (DateTimeOffset At, byte[] Bytes)[] Snapshot() { lock (frames) return frames.ToArray(); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (frames) frames.Add((DateTimeOffset.UtcNow, buffer.ToArray()));
        return ValueTask.CompletedTask;
    }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
