# WatchPATONERE
WatchPAT ONE Reverse Engineering

## Update

1. This repo builds upon the incredible work done by Chri at MRIIOT (https://github.com/MRIIOT).  Thanks Chris!
2. The point of this repo is to provide a cross platform python interface to use the WatchPat ONE device, for heartrate and blood oxygen monitoring.  The vendor of this device sells it as dispoable, and it seems like an incredible waste of engineering and hardware to just get rid of it.  Why not use it to get a live monitor of this health telemetry, and get some use out of the device?

# Posterity

The following README sections preserved from the original repo for posterity sake.  

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

1. Start `claude` in `C:\source\itamar\WatchPATONERE`.

### Building

```bash
dotnet restore
dotnet build
```

### Running

```bash
dotnet run
```

## Protocol Overview

This application implements the ITAMAR WatchPAT BLE protocol using Nordic UART Service with 24-byte packet headers and CRC-16-CCITT validation.

**Key Features:**
- ✅ Device discovery and connection
- ✅ LED control (physically verified)
- ✅ Sleep study session management
- ✅ Data packet reception and file saving
- ✅ Finger probe detection testing
- ✅ Device status monitoring

**For detailed technical documentation, see:**
- **[PROTOCOL.md](PROTOCOL.md)** - Complete protocol specification with sequence diagrams, packet formats, command IDs, timing requirements, and debugging notes
- **[CLAUDE.md](CLAUDE.md)** - Architecture overview and development guidelines for Claude Code

## References

- [Nordic UART Service Specification](https://developer.nordicsemi.com/nRF_Connect_SDK/doc/latest/nrf/libraries/bluetooth_services/services/nus.html)
- [Windows Bluetooth LE APIs](https://learn.microsoft.com/en-us/windows/uwp/devices-sensors/bluetooth-low-energy-overview)
- [GATT Specification](https://www.bluetooth.com/specifications/specs/gatt-specification/)



