# DualSense Widget v1.5.0

First public release: a portable Windows x64 desktop widget for DualSense battery, charging status, and lighting.

## Included

- Movable full and compact views, tray support, and always-on-top mode.
- Estimated battery percentage and real-time charging state, observed duration, and charge gain.
- Glass, translucent, and dark themes with adjustable opacity.
- Radial color picker that updates controller LEDs live while dragging, with output paced at 20 updates per second.
- RGB/hex controls, brightness, individual player LEDs, and five lighting modes.
- USB/Bluetooth support, reconnect handling, and saved preferences.

## Download and run

Download **DualSenseWidget-v1.5.0-win-x64.zip**, extract it, and open **DualSenseWidget.exe**. The .NET runtime is included. Windows x64 is required. The executable is unsigned.

**SHA256SUMS.txt** contains hashes for the Windows package and the optional source archive.

## Validation and known limits

96 protocol, charging, and lighting checks passed. UI rendering and live Bluetooth battery/report handling were checked. LED write access was verified; broader physical lighting and USB hardware testing is still needed. DualSense Edge support is implemented but untested on hardware.

Battery percentage reflects the controller's coarse charge bands. Charging current, wattage, and time-to-full are not available. Glass provides transparency without desktop blur. Other controller software may overwrite lighting or prevent access.

Source is available under the MIT license. Bundled runtime licenses and notices are included in the Windows package.
