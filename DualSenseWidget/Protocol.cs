using System;
using System.Buffers.Binary;

namespace DualSenseWidget;

public record BatteryInfo(int? Percent, string State);
public record Lighting(byte Red, byte Green, byte Blue, byte Players, double Brightness);

// Packet layouts verified against Sony's upstream hid-playstation driver.
// This code implements the wire format independently; it does not embed driver code.
public static class Protocol
{
    public static BatteryInfo? ReadBattery(ReadOnlySpan<byte> report, bool bluetooth)
    {
        int offset;
        if (!bluetooth && report.Length == 64 && report[0] == 0x01) offset = 1;
        else if (bluetooth && report.Length == 78 && report[0] == 0x31)
        {
            if (Crc(0xA1, report[..74]) != BinaryPrimitives.ReadUInt32LittleEndian(report[74..])) return null;
            offset = 2;
        }
        else return null; // Bluetooth's short compatibility report has no battery field.

        byte status = report[offset + 52];
        int level = status & 15, charge = status >> 4;
        if (charge == 2) return new(100, "Fully charged");
        if (charge == 10 || charge == 11) return new(null, "Charging paused · temperature / voltage");
        if (charge != 0 && charge != 1) return new(null, "Battery status unavailable");
        if (level > 10) return new(null, "Battery level unavailable");
        return new(Math.Min(level * 10 + 5, 100), charge == 1 ? "Charging" : "On battery");
    }

    public static byte[] Output(bool bluetooth, int sequence, Lighting lights, bool setup = false, bool release = false)
    {
        byte[] report = new byte[bluetooth ? 78 : 63];
        int start = bluetooth ? 3 : 1;
        report[0] = bluetooth ? (byte)0x31 : (byte)0x02;
        if (bluetooth) { report[1] = (byte)((sequence & 15) << 4); report[2] = 0x10; }
        if (release) report[start + 1] = 0x08;
        else if (setup) { report[start + 38] = 0x02; report[start + 41] = 0x02; }
        else
        {
            // Only LED enable flags. Leave audio, rumble and adaptive triggers alone.
            report[start + 1] = 0x14;
            report[start + 43] = (byte)(lights.Players & 0x1F);
            double scale = Math.Clamp(lights.Brightness, 0, 1);
            report[start + 44] = (byte)Math.Round(lights.Red * scale);
            report[start + 45] = (byte)Math.Round(lights.Green * scale);
            report[start + 46] = (byte)Math.Round(lights.Blue * scale);
        }
        if (bluetooth) BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(74), Crc(0xA2, report.AsSpan(0, 74)));
        return report;
    }

    public static uint Crc(byte prefix, ReadOnlySpan<byte> data)
    {
        uint crc = Step(0xFFFFFFFF, prefix);
        foreach (byte value in data) crc = Step(crc, value);
        return ~crc;
    }
    private static uint Step(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
        return crc;
    }
}
