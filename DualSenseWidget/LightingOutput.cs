using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DualSenseWidget;

internal record LedCommand(LightingProgram Program, bool Release = false);

// One writer owns the stream and sequence numbers. Static settings send once;
// animated effects run at up to 20 frames/sec until replaced or released.
internal static class LightingOutput
{
    internal static async Task Run(Stream stream, bool bluetooth, ChannelReader<LedCommand> commands,
        Func<BatteryInfo?> battery, Action<LightingProgram?, string> changed, CancellationToken token)
    {
        int sequence = 0;
        bool initialized = false;
        LightingProgram? active = null;
        var clock = new Stopwatch();
        long lastWrite = 0;
        while (true)
        {
            LedCommand? command;
            if (active == null || active.Effect == LightingEffect.Static)
                command = await commands.ReadAsync(token);
            else
            {
                await Task.Delay(50, token);
                commands.TryRead(out command);
            }
            // Pace drag updates as well as animations. While waiting, retain
            // only the newest selection so the final drag color is delivered.
            if (lastWrite != 0)
            {
                double remaining = 50 - Stopwatch.GetElapsedTime(lastWrite).TotalMilliseconds;
                if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), token);
            }
            while (commands.TryRead(out var newer)) command = newer;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            if (command != null)
            {
                if (command.Release)
                {
                    await stream.WriteAsync(Protocol.Output(bluetooth, sequence++, command.Program.Base, release: true), timeout.Token);
                    lastWrite = Stopwatch.GetTimestamp();
                    active = null; initialized = false;
                    changed(null, "LED control returned to the controller");
                    continue;
                }
                active = command.Program;
                clock.Restart();
                if (!initialized)
                {
                    await stream.WriteAsync(Protocol.Output(bluetooth, sequence++, active.Base, setup: true), timeout.Token);
                    await Task.Delay(80, timeout.Token);
                    initialized = true;
                }
            }
            if (active == null) continue;
            Lighting frame = LightingEffects.Frame(active, clock.Elapsed.TotalSeconds, battery());
            await stream.WriteAsync(Protocol.Output(bluetooth, sequence++, frame), timeout.Token);
            lastWrite = Stopwatch.GetTimestamp();
            if (command != null)
                changed(active, active.Effect == LightingEffect.Static ? "Static LED settings sent" : LightingEffects.Name(active.Effect) + " lighting is running");
        }
    }
}
