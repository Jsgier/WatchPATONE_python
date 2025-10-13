using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WatchPatBLE;

namespace WatchPatBLE;

class Program
{
    private static BleDeviceScanner _scanner;
    private static WatchPatDevice _device;
    private static List<BleDeviceScanner.DeviceInfo> _discoveredDevices;

    static async Task Main(string[] args)
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║        WatchPAT BLE Console - ITAMAR Medical              ║");
        Console.WriteLine("║                                                            ║");
        Console.WriteLine("║  ⚠️  WARNING: FOR AUTHORIZED USE ONLY                     ║");
        Console.WriteLine("║  This tool interfaces with medical devices.               ║");
        Console.WriteLine("║  Ensure you have proper authorization before use.         ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        _scanner = new BleDeviceScanner();
        _device = new WatchPatDevice();

        // Subscribe to telemetry events
        _device.Telemetry.TelemetryDataReceived += OnTelemetryDataReceived;
        _device.ConnectionStateChanged += OnConnectionStateChanged;
        _device.ErrorOccurred += OnErrorOccurred;

        bool running = true;

        while (running)
        {
            try
            {
                if (!_device.IsConnected)
                {
                    await ShowMainMenuAsync();
                }
                else
                {
                    await ShowConnectedMenuAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
                Console.WriteLine();
                Console.Write("Press any key to continue...");
                Console.ReadKey();
            }
        }

        // Cleanup
        _device?.Dispose();
        _scanner?.Dispose();
    }

    static async Task ShowMainMenuAsync()
    {
        Console.Clear();
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║              MAIN MENU - Not Connected                    ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine("  1. Scan for ITAMAR devices");
        Console.WriteLine("  2. Connect to device");
        Console.WriteLine("  3. Exit");
        Console.WriteLine();
        Console.Write("Select option: ");

        var key = Console.ReadKey().KeyChar;
        Console.WriteLine();
        Console.WriteLine();

        switch (key)
        {
            case '1':
                await ScanForDevicesAsync();
                break;
            case '2':
                await ConnectToDeviceAsync();
                break;
            case '3':
                Environment.Exit(0);
                break;
            default:
                Console.WriteLine("Invalid option");
                await Task.Delay(1000);
                break;
        }
    }

    static async Task ShowConnectedMenuAsync()
    {
        Console.Clear();
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine($"║  Connected to: {_device.SerialNumber,-42} ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine("  1. Start Sleep Study");
        Console.WriteLine("  2. Receive Study Data (Live)");
        Console.WriteLine("  3. Stop Session");
        Console.WriteLine("  4. Request Device Status");
        Console.WriteLine("  5. Test Finger Probe Detection");
        Console.WriteLine("  6. Monitor Telemetry (30 seconds)");
        Console.WriteLine("  7. Test LEDs (Turn On)");
        Console.WriteLine("  8. Test LEDs (Turn Off)");
        Console.WriteLine("  9. Test LEDs (Blink Pattern)");
        Console.WriteLine("  r. Request Stored Data (After Study)");
        Console.WriteLine("  d. Disconnect");
        Console.WriteLine("  x. Exit");
        Console.WriteLine();
        Console.Write("Select option: ");

        var key = Console.ReadKey().KeyChar;
        Console.WriteLine();
        Console.WriteLine();

        switch (key)
        {
            case '1':
                await StartSleepStudyAsync();
                break;
            case '2':
                await ReceiveStudyDataAsync();
                break;
            case '3':
                await StopSessionAsync();
                break;
            case '4':
                await RequestStatusAsync();
                break;
            case '5':
                await TestFingerDetectionAsync();
                break;
            case '6':
                await MonitorTelemetryAsync();
                break;
            case '7':
                await TestLEDsOnAsync();
                break;
            case '8':
                await TestLEDsOffAsync();
                break;
            case '9':
                await TestLEDsBlinkAsync();
                break;
            case 'r':
            case 'R':
                await RequestStoredDataAsync();
                break;
            case 'd':
            case 'D':
                await DisconnectAsync();
                break;
            case 'x':
            case 'X':
                Environment.Exit(0);
                break;
            default:
                Console.WriteLine("Invalid option");
                await Task.Delay(1000);
                break;
        }
    }

    static async Task ScanForDevicesAsync()
    {
        Console.WriteLine("🔍 Scanning for ITAMAR devices...");
        Console.WriteLine();

        _discoveredDevices = await _scanner.ScanAsync(TimeSpan.FromSeconds(10));

        if (_discoveredDevices.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("❌ No ITAMAR devices found.");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine($"✓ Found {_discoveredDevices.Count} device(s):");
            Console.WriteLine();

            for (int i = 0; i < _discoveredDevices.Count; i++)
            {
                var dev = _discoveredDevices[i];
                Console.WriteLine($"  [{i + 1}] {dev.Name}");
                Console.WriteLine($"      Serial: {dev.SerialNumber}");
                Console.WriteLine($"      RSSI: {dev.SignalStrength} dBm");
                Console.WriteLine($"      New Device: {(dev.IsNew ? "Yes" : "No")}");
                Console.WriteLine();
            }
        }

        Console.WriteLine();
        Console.Write("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task ConnectToDeviceAsync()
    {
        if (_discoveredDevices == null || _discoveredDevices.Count == 0)
        {
            Console.WriteLine("❌ No devices found. Please scan first.");
            await Task.Delay(2000);
            return;
        }

        Console.WriteLine("Select device to connect:");
        Console.WriteLine();

        for (int i = 0; i < _discoveredDevices.Count; i++)
        {
            Console.WriteLine($"  [{i + 1}] {_discoveredDevices[i].Name} (S/N: {_discoveredDevices[i].SerialNumber})");
        }

        Console.WriteLine();
        Console.Write("Select device (1-{0}): ", _discoveredDevices.Count);

        if (int.TryParse(Console.ReadLine(), out int selection) &&
            selection >= 1 && selection <= _discoveredDevices.Count)
        {
            var selectedDevice = _discoveredDevices[selection - 1];
            Console.WriteLine();
            Console.WriteLine($"🔗 Connecting to {selectedDevice.Name}...");
            Console.WriteLine();

            bool success = await _device.ConnectAsync(selectedDevice);

            if (success)
            {
                Console.WriteLine();
                Console.WriteLine("✓ Connection successful!");
                Console.WriteLine();
                Console.Write("Press any key to continue...");
                Console.ReadKey();
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("❌ Connection failed.");
                Console.WriteLine();
                Console.Write("Press any key to continue...");
                Console.ReadKey();
            }
        }
        else
        {
            Console.WriteLine("Invalid selection.");
            await Task.Delay(1000);
        }
    }

    static async Task StartSleepStudyAsync()
    {
        Console.WriteLine("🌙 Starting sleep study session...");
        Console.WriteLine();

        bool success = await _device.StartSleepStudyAsync();

        if (success)
        {
            Console.WriteLine();
            Console.WriteLine("✓ Sleep study started successfully!");
            Console.WriteLine("  The device is now recording.");
            Console.WriteLine();
            Console.WriteLine("  Patient should:");
            Console.WriteLine("  - Wear the device on their wrist");
            Console.WriteLine("  - Place the finger probe on their finger");
            Console.WriteLine("  - Go to sleep");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("❌ Failed to start sleep study.");
        }

        Console.WriteLine();
        Console.Write("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task ReceiveStudyDataAsync()
    {
        Console.WriteLine("📥 Receive Live Study Data");
        Console.WriteLine();
        Console.WriteLine("This will save DATA packets (0x0800) from an active recording session.");
        Console.WriteLine();
        Console.Write("Enter duration in seconds (or 0 for continuous): ");

        string input = Console.ReadLine();
        if (!int.TryParse(input, out int duration) || duration < 0)
        {
            Console.WriteLine("Invalid duration. Using 60 seconds.");
            duration = 60;
        }

        Console.WriteLine();
        Console.WriteLine("⚠️  Note: Device must have an ACTIVE sleep study session running!");
        Console.WriteLine("   If no session is active, no data will be received.");
        Console.WriteLine();
        Console.Write("Press any key to start receiving data...");
        Console.ReadKey();
        Console.WriteLine();
        Console.WriteLine();

        var (packetCount, bytesReceived) = await _device.ReceiveStudyDataAsync(duration);

        Console.WriteLine();
        if (packetCount > 0)
        {
            Console.WriteLine($"✓ Successfully received {packetCount} data packets!");
            Console.WriteLine($"  Total: {bytesReceived:N0} bytes");
            Console.WriteLine($"  File saved to My Documents folder");
        }
        else
        {
            Console.WriteLine("⚠️  No data packets received.");
            Console.WriteLine("   Make sure a sleep study session is active on the device.");
        }

        Console.WriteLine();
        Console.Write("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task RequestStoredDataAsync()
    {
        Console.WriteLine("💾 Request Stored Data");
        Console.WriteLine();
        Console.WriteLine("This will download DATA packets from the device's flash memory.");
        Console.WriteLine("Use this to retrieve a completed sleep study recording.");
        Console.WriteLine();
        Console.WriteLine("⚠️  Note: This is for retrieving stored recordings AFTER a study is complete.");
        Console.WriteLine("   Device must have completed a sleep study session.");
        Console.WriteLine();
        Console.Write("Press any key to start download...");
        Console.ReadKey();
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("📥 Requesting stored data from device...");
        Console.WriteLine("⏳ This may take several minutes depending on recording length.");
        Console.WriteLine();

        var (packetCount, bytesReceived) = await _device.RequestStoredDataAsync();

        Console.WriteLine();
        if (packetCount > 0)
        {
            Console.WriteLine($"✓ Successfully downloaded {packetCount} data packets!");
            Console.WriteLine($"  Total: {bytesReceived:N0} bytes");
            Console.WriteLine($"  File saved to My Documents folder");
        }
        else
        {
            Console.WriteLine("⚠️  No data packets received.");
            Console.WriteLine("   Make sure the device has a completed study stored.");
            Console.WriteLine("   Or the device memory may be empty.");
        }

        Console.WriteLine();
        Console.Write("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task StopSessionAsync()
    {
        Console.WriteLine("🛑 Stopping session...");
        Console.WriteLine();

        bool success = await _device.StopSessionAsync();

        if (success)
        {
            Console.WriteLine("✓ Session stopped successfully!");
        }
        else
        {
            Console.WriteLine("❌ Failed to stop session.");
        }

        Console.WriteLine();
        Console.Write("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task RequestStatusAsync()
    {
        Console.WriteLine("📊 Requesting device status...");
        Console.WriteLine();

        bool success = await _device.RequestStatusAsync();

        if (success)
        {
            Console.WriteLine("✓ Status request sent.");
            Console.WriteLine("⏳ Waiting for device response (ACK + Status Report)...");
            Console.WriteLine();

            // Wait longer for device to send both ACK and status report
            await Task.Delay(5000);

            Console.WriteLine();
            Console.WriteLine("Note: Check above for [Telemetry] messages with status data.");
        }
        else
        {
            Console.WriteLine("❌ Failed to request status.");
        }

        Console.WriteLine();
        Console.Write("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task TestFingerDetectionAsync()
    {
        Console.WriteLine("🖐️ Testing finger probe detection...");
        Console.WriteLine();
        Console.WriteLine("Instructions:");
        Console.WriteLine("  - Attach the finger probe to the device");
        Console.WriteLine("  - Place your finger in the probe");
        Console.WriteLine("  - Ensure good contact for pulse detection");
        Console.WriteLine();

        var (success, result) = await _device.StartFingerDetectionAsync();

        Console.WriteLine();

        if (!success)
        {
            Console.WriteLine("❌ Failed to receive finger detection response.");
        }
        else
        {
            Console.WriteLine("✓ Finger detection test complete!");
            Console.WriteLine();
            Console.WriteLine($"  Result: 0x{result:X8} ({result})");
            Console.WriteLine();

            if (result == 0)
            {
                Console.WriteLine("  Interpretation: NO FINGER DETECTED");
                Console.WriteLine("  - Finger probe may not be connected");
                Console.WriteLine("  - OR no finger placed in the probe");
                Console.WriteLine("  - OR probe not detecting pulse signal");
            }
            else
            {
                Console.WriteLine($"  Interpretation: FINGER DETECTED ✓");
                Console.WriteLine("  - Probe is connected and working");
                Console.WriteLine("  - Finger is properly placed");
                Console.WriteLine("  - Pulse signal detected");
            }
        }

        Console.WriteLine();
        Console.Write("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task MonitorTelemetryAsync()
    {
        Console.WriteLine("📡 Monitoring telemetry for 30 seconds...");
        Console.WriteLine("   (Telemetry data will be displayed as it arrives)");
        Console.WriteLine();

        // Request status to trigger telemetry
        await _device.RequestStatusAsync();

        await Task.Delay(30000);

        Console.WriteLine();
        Console.WriteLine("Monitoring complete.");
        Console.WriteLine();
        Console.Write("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task TestLEDsOnAsync()
    {
        Console.WriteLine("💡 Testing LEDs - Turning ON...");
        Console.WriteLine();

        bool success = await _device.SetLEDsOnAsync();

        Console.WriteLine();

        if (success)
        {
            Console.WriteLine("✓ LEDs turned ON successfully!");
            Console.WriteLine("  Watch the device - LEDs should be lit.");
        }
        else
        {
            Console.WriteLine("❌ LED ON command failed or not acknowledged.");
        }

        Console.WriteLine();
        Console.Write("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task TestLEDsOffAsync()
    {
        Console.WriteLine("🌑 Testing LEDs - Turning OFF...");
        Console.WriteLine();

        bool success = await _device.SetLEDsOffAsync();

        Console.WriteLine();

        if (success)
        {
            Console.WriteLine("✓ LEDs turned OFF successfully!");
            Console.WriteLine("  Watch the device - LEDs should be off.");
        }
        else
        {
            Console.WriteLine("❌ LED OFF command failed or not acknowledged.");
        }

        Console.WriteLine();
        Console.Write("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task TestLEDsBlinkAsync()
    {
        Console.WriteLine("✨ Testing LEDs - Blink Pattern...");
        Console.WriteLine("   (5 cycles of on/off)");
        Console.WriteLine();

        for (int i = 0; i < 5; i++)
        {
            Console.WriteLine($"  Cycle {i + 1}/5: Turning ON...");
            bool onSuccess = await _device.SetLEDsOnAsync();
            if (!onSuccess)
            {
                Console.WriteLine("  ⚠ Failed to turn LEDs on");
            }
            await Task.Delay(500);

            Console.WriteLine($"  Cycle {i + 1}/5: Turning OFF...");
            bool offSuccess = await _device.SetLEDsOffAsync();
            if (!offSuccess)
            {
                Console.WriteLine("  ⚠ Failed to turn LEDs off");
            }
            await Task.Delay(500);
        }

        Console.WriteLine();
        Console.WriteLine("✓ Blink pattern complete!");
        Console.WriteLine();
        Console.Write("Press any key to continue...");
        Console.ReadKey();
    }

    static async Task DisconnectAsync()
    {
        Console.WriteLine("🔌 Disconnecting from device...");
        Console.WriteLine();

        // Send soft reset to clear pairing state
        Console.WriteLine("📤 Sending soft reset command...");
        bool resetSent = await _device.ResetDeviceAsync(0);

        if (resetSent)
        {
            Console.WriteLine("✓ Soft reset command sent successfully");
        }
        else
        {
            Console.WriteLine("⚠ Warning: Failed to send soft reset command");
        }

        Console.WriteLine();

        // Now disconnect
        _device.Disconnect();
        Console.WriteLine("✓ Disconnected from device.");
        Console.WriteLine();
        Console.WriteLine("Note: Soft reset clears pairing state.");
        Console.WriteLine("      Next connection will be treated as fresh pairing.");

        Console.WriteLine();
        Console.Write("Press any key to continue...");
        Console.ReadKey();
    }

    // Event handlers
    static void OnTelemetryDataReceived(object sender, TelemetryHandler.TelemetryData telemetry)
    {
        Console.WriteLine($"[{telemetry.Timestamp:HH:mm:ss}] {telemetry.PacketType}");

        if (telemetry.ParsedData != null && telemetry.ParsedData.Count > 0)
        {
            foreach (var kvp in telemetry.ParsedData)
            {
                Console.WriteLine($"  {kvp.Key}: {kvp.Value}");
            }
        }
    }

    static void OnConnectionStateChanged(object sender, string state)
    {
        Console.WriteLine($"[Connection] State changed: {state}");
    }

    static void OnErrorOccurred(object sender, Exception ex)
    {
        Console.WriteLine($"[Error] {ex.Message}");
    }
}
