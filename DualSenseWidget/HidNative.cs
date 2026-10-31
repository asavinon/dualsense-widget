using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DualSenseWidget;

internal record HidDevice(string Path, bool Bluetooth, bool Edge, int InputLength);
internal static class HidNative
{
    public static string Diagnostics { get; private set; } = "Not scanned";
    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData { public int Size; public Guid ClassGuid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Attributes { public int Size; public ushort Vendor, Product, Version; }

    public static List<HidDevice> Discover()
    {
        HidD_GetHidGuid(out Guid guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        var devices = new List<HidDevice>();
        int interfaces = 0, readable = 0, sony = 0;
        var sonyProducts = new List<string>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var item = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref item))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                interfaces++;
                SetupDiGetDeviceInterfaceDetail(set, ref item, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                IntPtr detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref item, detail, size, out _, IntPtr.Zero)) continue;
                    string? path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    // Bluetooth HID paths use a different VID spelling from USB paths.
                    // Filter by device attributes rather than the path text.
                    if (path == null) continue;
                    using var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid) continue;
                    readable++;
                    var attributes = new Attributes { Size = Marshal.SizeOf<Attributes>() };
                    if (!HidD_GetAttributes(handle, ref attributes) || attributes.Vendor != 0x054C) continue;
                    sony++; sonyProducts.Add(attributes.Product.ToString("X4"));
                    if (attributes.Product != 0x0CE6 && attributes.Product != 0x0DF2) continue;
                    if (!HidD_GetPreparsedData(handle, out IntPtr parsed)) continue;
                    try
                    {
                        IntPtr caps = Marshal.AllocHGlobal(64);
                        try
                        {
                            if (HidP_GetCaps(parsed, caps) != 0x00110000) continue;
                            int usage = (ushort)Marshal.ReadInt16(caps, 0), page = (ushort)Marshal.ReadInt16(caps, 2);
                            int input = (ushort)Marshal.ReadInt16(caps, 4);
                            if (page == 1 && (usage == 5 || usage == 4) && (input == 64 || input == 78))
                                devices.Add(new(path, input == 78, attributes.Product == 0x0DF2, input));
                        }
                        finally { Marshal.FreeHGlobal(caps); }
                    }
                    finally { HidD_FreePreparsedData(parsed); }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        // Prefer the wired interface if the controller is visible on both transports.
        devices.Sort((a, b) => a.Bluetooth.CompareTo(b.Bluetooth));
        Diagnostics = $"HID interfaces: {interfaces}; queryable: {readable}; Sony: {sony}; Sony product IDs: {string.Join(", ", sonyProducts)}";
        if (interfaces > 0 && readable == 0) throw new UnauthorizedAccessException("Windows denied access to HID interfaces");
        return devices;
    }

    public static FileStream Open(HidDevice device, out bool writable)
    {
        var handle = CreateFile(device.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        writable = !handle.IsInvalid;
        if (!writable)
        {
            handle.Dispose();
            handle = CreateFile(device.Path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        }
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        if (device.Bluetooth)
        {
            // Reading calibration requests full Bluetooth reports, including battery status.
            var feature = new byte[41]; feature[0] = 0x05;
            HidD_GetFeature(handle, feature, feature.Length);
        }
        return new FileStream(handle, writable ? FileAccess.ReadWrite : FileAccess.Read, 1, true);
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr parsed, IntPtr caps);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] data, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData item);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData item, IntPtr detail, int size, out int required, IntPtr device);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
