# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

WatchPAT ONE RE is a .NET 9.0 console application that reverse-engineers and reimplements the Bluetooth Low Energy (BLE) communication protocol for ITAMAR Medical's WatchPAT ONE sleep apnea monitoring device. The project successfully communicates with physical WatchPAT hardware using the Nordic UART Service.

**Master Reference**: The original Android application source code is located in `C:\source\itamar\javasrc\sources\com\itamarmedical`. CRITICAL: Always consult the Android source (especially `BLEService.java`, `DeviceCommands.java`, `IncomingPacketHandler.java`) as the authoritative reference when modifying or adding protocol features.

## Build and Run Commands

```bash
# Build the project
dotnet build

# Run the application
dotnet run

# Run from compiled binary
cd bin/Debug/net9.0-windows10.0.19041.0/win-x64
./WatchPatBLE.exe
```

**Requirements**:
- .NET 9.0 SDK or later
- Windows 10/11 (build 19041 or later) - uses Windows.Devices.Bluetooth APIs
- BLE-capable Bluetooth adapter
- Administrator privileges may be required for BLE access

## Architecture Overview

### Core Components

1. **Program.cs** - Interactive CLI menu system
   - Main entry point with dual-menu structure (disconnected/connected states)
   - Event-driven UI responding to device state changes
   - Handles user input and orchestrates operations

2. **BleDeviceScanner.cs** - Device discovery layer
   - Scans for ITAMAR-branded BLE devices using `BluetoothLEAdvertisementWatcher`
   - Parses device serial numbers from broadcast names (format: `ITAMAR_[HEX]N`)
   - Tracks RSSI and device metadata

3. **WatchPatDevice.cs** - Device connection and command layer
   - Manages GATT service connections (Nordic UART Service: `6e400001-b5a3-f393-e0a9-e50e24dcca9e`)
   - Implements high-level command methods (start session, stop, status, LED control)
   - Handles BLE characteristic writes (TX: `6e400002`) and notifications (RX: `6e400003`)
   - Coordinates with TelemetryHandler for incoming data

4. **WatchPatProtocol.cs** - Protocol constants and command builders
   - Defines all command IDs matching `DeviceCommands.java`
   - Factory methods for creating protocol-compliant packets
   - Serial number conversion utilities (hex ↔ decimal)

5. **WatchPatPacket.cs** - Binary packet structure and CRC
   - Implements 24-byte header format with magic number `0xBBBB`
   - CRC-16-CCITT checksum calculation matching Java implementation
   - Handles endianness conversions (critical for cross-platform compatibility)
   - 20-byte chunking for BLE MTU compliance

6. **TelemetryHandler.cs** - Incoming packet parser
   - Reassembles 20-byte BLE chunks into complete packets
   - Validates CRC and packet integrity
   - Routes packets by type (ACK, DATA, STATUS, errors)
   - Fires events for application layer consumption

### Communication Flow

```
User Input (Program.cs)
    ↓
Command Method (WatchPatDevice.cs)
    ↓
Packet Builder (WatchPatProtocol.cs)
    ↓
Binary Packet (WatchPatPacket.cs)
    ↓
20-byte Chunks → BLE TX Characteristic
    ↓
Device Processing
    ↓
BLE RX Notification (chunks)
    ↓
Packet Reassembly (TelemetryHandler.cs)
    ↓
Event Handlers (Program.cs)
```

## Critical Protocol Details

### Byte Ordering (CRITICAL!)

The protocol uses mixed endianness due to Java's `ByteBuffer` behavior:

**In WatchPatPacket.cs Header.ToBytes():**
- **Magic, CommandId, Timestamp**: MUST byte-reverse (Java writes big-endian without pre-reversal)
- **TransactionId, Length, CRC**: DO NOT reverse (Java uses `Integer.reverseBytes()` / `Short.reverseBytes()` before writing)
- **Flags, Zero**: DO NOT reverse (simple bytes or zero values)

Incorrect byte ordering causes the device to silently ignore all commands. This was discovered through extensive debugging documented in PROTOCOL.md.

### Connection Initialization Sequence

After GATT connection is established:
1. Wait 1000ms for device stabilization (matches Android pattern)
2. Send `IS_DEVICE_PAIRED` (0x2A00) command - REQUIRED before other commands
3. Wait for ACK response
4. Device is now ready for operational commands

### Packet Structure

```
[Header: 24 bytes]
  0-1:   Magic (0xBBBB)
  2-3:   Command ID (little-endian after reversal)
  4-11:  Timestamp (Unix seconds, big-endian after reversal)
  12-15: Transaction ID (little-endian, pre-reversed in Java)
  16-17: Total Length (header + payload, little-endian, pre-reversed)
  18-19: Flags
  20-21: Zero (reserved)
  22-23: CRC-16-CCITT (little-endian, pre-reversed)

[Payload: Variable length]
```

### Command Timing

- **Post-connection delay**: 1000ms before first command
- **Chunk delay**: 10ms between 20-byte chunks (Android uses this)
- **Command retry**: 2000ms intervals, 10000ms total timeout
- **Scan duration**: Typically 5000-10000ms

## Common Development Tasks

### Adding a New Command

1. Find the command in Android source `DeviceCommands.java` (note the command ID and payload structure)
2. Add command ID enum to `WatchPatPacket.cs` CommandId enum
3. Create factory method in `WatchPatProtocol.cs` (e.g., `CreateXxxCommand()`)
4. Add public method to `WatchPatDevice.cs` to send command and handle response
5. Add response handler in `TelemetryHandler.cs` if command returns data
6. Wire up to UI in `Program.cs` connected menu

Example: LED control command (0x2300) takes a single byte payload (0x00=off, 0xFF=on).

### Testing Protocol Changes

Use LED commands as verification - they provide immediate visual feedback:
- `SetLEDsOnAsync()` → Device LED lights up (proven working)
- `SetLEDsOffAsync()` → Device LED turns off
- Transaction ID in ACK should match command transaction ID

### Debugging BLE Communication

1. Enable verbose logging in `WatchPatDevice.cs` (log TX writes and RX notifications)
2. Compare byte-by-byte with Android app using Bluetooth HCI sniffer
3. Verify CRC matches (packet should not change between `Build()` calls - cached)
4. Check transaction IDs in ACKs match sent commands
5. Ensure 1000ms post-connection delay is respected

## Key Implementation Notes

- **Windows-only**: Uses `Windows.Devices.Bluetooth` APIs (no Linux/macOS support)
- **Single device**: Application connects to one device at a time
- **No data persistence**: Telemetry displayed but not saved to files (yet)
- **Medical device**: Ensure proper authorization and compliance with regulations (FDA, HIPAA)
- **Reverse-engineered**: Protocol may have undocumented features or edge cases

## File References

| File | Lines | Key Functionality |
|------|-------|-------------------|
| Program.cs | 623 | Interactive menu, event handlers |
| WatchPatDevice.cs | - | BLE connection, GATT operations, command transmission |
| WatchPatProtocol.cs | 241 | Command factory methods, constants |
| WatchPatPacket.cs | 238 | Binary packet structure, CRC, chunking |
| TelemetryHandler.cs | - | Packet reassembly, parsing, event dispatch |
| BleDeviceScanner.cs | 191 | Device discovery, advertisement monitoring |

## Protocol Verification Status

✅ **VERIFIED WORKING**:
- Device discovery and connection
- IS_DEVICE_PAIRED initialization
- SetLEDs command (0x2300) - LED physically responds
- ACK reception and parsing
- CRC-16 validation
- 20-byte BLE chunking
- Transaction ID tracking

⏳ **IMPLEMENTED BUT UNVERIFIED**:
- START_SESSION (0x0100) - sleep study initiation
- STOP_ACQUISITION (0x0700) - end session
- TECHNICAL_STATUS_REQUEST (0x1500) - device status query
- START_FINGER_DETECTION (0x2500) - probe detection
- DATA packets (0x0800) - telemetry stream

## Additional Documentation

- **PROTOCOL.md**: Comprehensive reverse-engineering notes with Android BLE workflow, packet formats, state machines, and debugging lessons learned
- **README.md**: Project setup, APK decompilation instructions, development workflow, and BLE protocol overview

