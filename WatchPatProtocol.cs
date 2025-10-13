using System;

namespace WatchPatBLE;

/// <summary>
/// WatchPAT BLE Protocol Constants
/// Based on reverse-engineered protocol from ITAMAR Medical WatchPAT device
/// </summary>
public static class WatchPatProtocol
{
    // GATT Service UUIDs (Nordic UART Service)
    public static readonly Guid ServiceUuid = Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
    public static readonly Guid TxCharacteristicUuid = Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e"); // Write
    public static readonly Guid RxCharacteristicUuid = Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e"); // Notify
    public static readonly Guid ClientCharacteristicConfigDescriptorUuid = Guid.Parse("00002902-0000-1000-8000-00805f9b34fb");

    // Device name prefix
    public const string DeviceNamePrefix = "ITAMAR_";
    public const string DeviceNameSuffixNew = "N"; // New device marker

    // Session mode constants
    public enum SessionMode : byte
    {
        Sleep = 0x01,      // Start sleep session
        Recording = 0x02,  // Recording mode
        Prepare = 0x04     // Prepare mode
    }

    // Connection states
    public enum ConnectionState
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2
    }

    // Device discovery states
    public enum DiscoveryState
    {
        NotFound = 0,
        SingleDevice = 1,
        MultipleDevices = 2
    }

    /// <summary>
    /// Create START SESSION command packet
    /// From DeviceCommands.java SessionStartCommandPacket
    /// </summary>
    public static WatchPatPacket CreateStartSessionCommand(int mobileId, SessionMode mode, string androidVersion)
    {
        var versionBytes = System.Text.Encoding.ASCII.GetBytes(androidVersion);

        // Payload: mobileId (4 bytes) + mode (1 byte) + version (variable) + null terminator (1 byte)
        var payload = new List<byte>();
        payload.AddRange(BitConverter.GetBytes(mobileId));
        payload.Add((byte)mode);
        payload.AddRange(versionBytes);
        payload.Add(0x00); // Null terminator

        // Get current timestamp with timezone offset (milliseconds -> seconds)
        var now = DateTimeOffset.Now;
        long timestamp = now.ToUnixTimeSeconds();

        return new WatchPatPacket(
            WatchPatPacket.CommandId.StartSession,
            payload.ToArray(),
            flags: 0,
            timestamp: timestamp
        );
    }

    /// <summary>
    /// Create STOP ACQUISITION command
    /// From DeviceCommands.java line 542-546
    /// </summary>
    public static WatchPatPacket CreateStopSessionCommand()
    {
        return new WatchPatPacket(
            WatchPatPacket.CommandId.StopAcquisition,
            payload: null
        );
    }

    /// <summary>
    /// Create TECHNICAL STATUS REQUEST command
    /// From DeviceCommands.java line 470-474
    /// </summary>
    public static WatchPatPacket CreateGetStatusCommand()
    {
        return new WatchPatPacket(
            WatchPatPacket.CommandId.TechnicalStatusRequest,
            payload: null
        );
    }

    /// <summary>
    /// Create START ACQUISITION command
    /// </summary>
    public static WatchPatPacket CreateStartAcquisitionCommand()
    {
        return new WatchPatPacket(
            WatchPatPacket.CommandId.StartAcquisition,
            payload: null
        );
    }

    /// <summary>
    /// Create SEND STORED DATA command
    /// </summary>
    public static WatchPatPacket CreateSendStoredDataCommand()
    {
        return new WatchPatPacket(
            WatchPatPacket.CommandId.SendStoredData,
            payload: null
        );
    }

    /// <summary>
    /// Create SET LEDs command
    /// From DeviceCommands.java line 512-516
    /// </summary>
    /// <param name="ledByte">LED control byte (0x00=off, 0xFF=all on, or bit pattern)</param>
    public static WatchPatPacket CreateSetLEDsCommand(byte ledByte)
    {
        return new WatchPatPacket(
            WatchPatPacket.CommandId.SetLEDs,
            payload: new byte[] { ledByte }
        );
    }

    /// <summary>
    /// Create SET LEDs OFF command (convenience)
    /// </summary>
    public static WatchPatPacket CreateSetLEDsOffCommand()
    {
        return CreateSetLEDsCommand(0x00);
    }

    /// <summary>
    /// Create SET LEDs ON command (convenience)
    /// </summary>
    public static WatchPatPacket CreateSetLEDsOnCommand()
    {
        return CreateSetLEDsCommand(0xFF);
    }

    /// <summary>
    /// Create IS_DEVICE_PAIRED command (required after connection)
    /// From DeviceCommands.java line 476-480
    /// </summary>
    public static WatchPatPacket CreateIsDevicePairedCommand()
    {
        return new WatchPatPacket(
            WatchPatPacket.CommandId.IsDevicePaired,
            payload: null
        );
    }

    /// <summary>
    /// Create RESET DEVICE command
    /// From DeviceCommands.java line 482-486 (ResetCommandPacket)
    /// </summary>
    /// <param name="resetType">0 = soft reset, 1 = hard reset</param>
    public static WatchPatPacket CreateResetDeviceCommand(byte resetType = 0)
    {
        return new WatchPatPacket(
            WatchPatPacket.CommandId.ResetDevice,
            payload: new byte[] { resetType }
        );
    }

    /// <summary>
    /// Create START FINGER DETECTION command
    /// From DeviceCommands.java line 530-534
    /// </summary>
    public static WatchPatPacket CreateStartFingerDetectionCommand()
    {
        return new WatchPatPacket(
            WatchPatPacket.CommandId.StartFingerDetection,
            payload: null
        );
    }

    /// <summary>
    /// Parse serial number from device name
    /// Device name format: ITAMAR_[HEX]N or ITAMAR_[HEX]
    /// </summary>
    public static string ParseSerialNumber(string deviceName)
    {
        if (!deviceName.StartsWith(DeviceNamePrefix))
            return null;

        var hexPart = deviceName.Substring(DeviceNamePrefix.Length).Replace("N", "");

        try
        {
            // Convert hex to decimal and format as 9-digit serial
            int serialInt = Convert.ToInt32(hexPart, 16);
            return serialInt.ToString("D9");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Convert serial number to hex format for device name
    /// </summary>
    public static string SerialToHex(string serial)
    {
        if (int.TryParse(serial, out int serialInt))
        {
            return serialInt.ToString("X");
        }
        return null;
    }

    /// <summary>
    /// Generate mobile ID from Bluetooth adapter MAC address
    /// </summary>
    public static int GenerateMobileId(string macAddress)
    {
        // Take first 4 bytes of MAC address
        var bytes = System.Text.Encoding.ASCII.GetBytes(macAddress);
        if (bytes.Length >= 4)
        {
            return BitConverter.ToInt32(bytes, 0);
        }
        return Environment.TickCount; // Fallback to timestamp-based ID
    }

    /// <summary>
    /// Format byte array as hex string for logging
    /// </summary>
    public static string ByteArrayToHex(byte[] data)
    {
        return BitConverter.ToString(data).Replace("-", " ");
    }
}
