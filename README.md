# WatchPATONERE
WatchPAT ONE Reverse Engineering

## Setup

1. Clone this repository to `C:\source\itamar\WatchPATONERE`.
2. Install `.NET 9.0 SDK`.
3. Install `Claude Code`.

By the end of setup your folder structure should look something like this.

```
c:\source\itamar
|
|- apksrc
|- javasrc
|- WatchPATONERE
|- com.itamarmedical.watchpat.apk
|- .claude
```

### Get APK

1. Download APK from `https://apkpure.com/watchpat/com.itamarmedical.watchpat`.
2. Rename `WatchPAT_4.1.0_APKPure.xapk` to `WatchPAT_4.1.0_APKPure.zip`.
3. Extract `com.itamarmedical.watchpat.apk` to `c:\source\itamar`.

### Extract APK (optional)

1. Download `https://github.com/iBotPeaches/Apktool/releases/download/v2.12.1/apktool_2.12.1.jar`.
2. Rename `apktool_2.12.1.jar` to `apktool.jar`.
3. Copy `apktool.jar` and `apktool.bat` to `c:\windows` (so you can run batch file from PATH).
4. Open Command Prompt and run `apktool.bat d -o c:\source\itamar\apksrc c:\source\itamar\com.itamarmedical.watchpat.apk`.

### Decompile APK

1. Download `https://github.com/skylot/jadx/releases/download/v1.5.3/jadx-gui-1.5.3-with-jre-win.zip`.
2. Extract JADX and run `jadx-gui-1.5.3.exe`.
3. Open `c:\source\itamar\com.itamarmedical.watchpat.apk` and File > Export Project to `C:\source\itamar\javasrc`.

## Development

1. Start `claude` in ``C:\source\itamar\WatchPATONERE`.

### Building

```bash
cd WatchPatBLE
dotnet restore
dotnet build
```

### Running

```bash
dotnet run
```

## Technical Details

### BLE Protocol

The application implements the ITAMAR WatchPAT BLE protocol:

- **Service UUID**: `6e400001-b5a3-f393-e0a9-e50e24dcca9e` (Nordic UART Service)
- **TX Characteristic**: `6e400002-b5a3-f393-e0a9-e50e24dcca9e` (Write)
- **RX Characteristic**: `6e400003-b5a3-f393-e0a9-e50e24dcca9e` (Notify)

### Command Protocol

Commands use a custom binary protocol:
```
[Header: 0x55 0xAA] [Command ID] [Data Length] [Data...] [Checksum]
```

Supported commands:
- `0x10`: Start Session
- `0x11`: Stop Session
- `0x20`: Get Status
- `0x30`: Telemetry Data (received)
- `0x40`: Device Info (received)

### Device Naming Convention

Devices broadcast with name format:
- `ITAMAR_[HEX]` - Standard device
- `ITAMAR_[HEX]N` - New/unregistered device

The hex portion converts to a 9-digit decimal serial number.

## References

- [Nordic UART Service Specification](https://developer.nordicsemi.com/nRF_Connect_SDK/doc/latest/nrf/libraries/bluetooth_services/services/nus.html)
- [Windows Bluetooth LE APIs](https://learn.microsoft.com/en-us/windows/uwp/devices-sensors/bluetooth-low-energy-overview)
- [GATT Specification](https://www.bluetooth.com/specifications/specs/gatt-specification/)



