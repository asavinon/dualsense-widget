# Controller Lights and Battery v1.5.2

DualSense Widget is now **Controller Lights and Battery**. The app title, header, tray tooltip, executable, documentation, and preview use the new name. This release also includes the per-monitor DPI awareness added for the Microsoft Store build.

Battery readings, charging information, live color picking, lighting effects, glass/translucent themes, and saved preferences continue to work as before. The settings location is unchanged, so existing preferences are preserved.

## Download and run

Download **ControllerLightsAndBattery-v1.5.2-win-x64.zip**, extract it, and open **ControllerLightsAndBattery.exe**. Close the previous version before starting the new one. The .NET runtime is included; Windows x64 is required. The portable executable is unsigned.

**SHA256SUMS.txt** includes hashes for the Windows package and optional source archive.

## Validation and limits

96 protocol, charging, and lighting checks passed. UI smoke checks and live Bluetooth battery reports were verified. Battery percentages remain coarse estimates; charging current, wattage, and time remaining are not available. DualSense Edge support is implemented but has not been hardware-tested. Other controller software may overwrite lighting or prevent access.

The Microsoft Store version has been submitted for certification under the same name; it is not yet approved.

Source remains public under the MIT license. Bundled runtime licenses and notices are included. This independent project is not affiliated with or endorsed by Sony.
