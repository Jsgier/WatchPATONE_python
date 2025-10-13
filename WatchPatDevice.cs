using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace WatchPatBLE;

/// <summary>
/// Represents a connected WatchPAT device
/// </summary>
public class WatchPatDevice : IDisposable
{
    private BluetoothLEDevice _device;
    private GattCharacteristic _txCharacteristic;
    private GattCharacteristic _rxCharacteristic;
    private readonly TelemetryHandler _telemetryHandler;
    private bool _isConnected;
    private string _serialNumber;

    public event EventHandler<string> ConnectionStateChanged;
    public event EventHandler<Exception> ErrorOccurred;

    public bool IsConnected => _isConnected;
    public string SerialNumber => _serialNumber;
    public TelemetryHandler Telemetry => _telemetryHandler;

    public WatchPatDevice()
    {
        _telemetryHandler = new TelemetryHandler();
    }

    /// <summary>
    /// Connect to a WatchPAT device
    /// </summary>
    public async Task<bool> ConnectAsync(BleDeviceScanner.DeviceInfo deviceInfo)
    {
        try
        {
            Console.WriteLine($"[Device] Connecting to {deviceInfo.Name} (S/N: {deviceInfo.SerialNumber})...");
            _serialNumber = deviceInfo.SerialNumber;

            // Get the BluetoothLEDevice
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(deviceInfo.BluetoothAddress);
            if (_device == null)
            {
                Console.WriteLine($"[Device] Failed to create device object");
                return false;
            }

            Console.WriteLine($"[Device] Device object created: {_device.Name}");
            Console.WriteLine($"[Device] Connection status: {_device.ConnectionStatus}");

            // Get GATT services
            var servicesResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success)
            {
                Console.WriteLine($"[Device] Failed to get GATT services: {servicesResult.Status}");
                return false;
            }

            Console.WriteLine($"[Device] Found {servicesResult.Services.Count} GATT services");

            // Find the Nordic UART service
            var service = servicesResult.Services.FirstOrDefault(s => s.Uuid == WatchPatProtocol.ServiceUuid);
            if (service == null)
            {
                Console.WriteLine($"[Device] Nordic UART Service not found");
                Console.WriteLine($"[Device] Available services:");
                foreach (var s in servicesResult.Services)
                {
                    Console.WriteLine($"  - {s.Uuid}");
                }
                return false;
            }

            Console.WriteLine($"[Device] Found Nordic UART Service");

            // Get characteristics
            var characteristicsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (characteristicsResult.Status != GattCommunicationStatus.Success)
            {
                Console.WriteLine($"[Device] Failed to get characteristics: {characteristicsResult.Status}");
                return false;
            }

            Console.WriteLine($"[Device] Found {characteristicsResult.Characteristics.Count} characteristics");

            // Get TX (write) and RX (notify) characteristics
            _txCharacteristic = characteristicsResult.Characteristics
                .FirstOrDefault(c => c.Uuid == WatchPatProtocol.TxCharacteristicUuid);

            _rxCharacteristic = characteristicsResult.Characteristics
                .FirstOrDefault(c => c.Uuid == WatchPatProtocol.RxCharacteristicUuid);

            if (_txCharacteristic == null || _rxCharacteristic == null)
            {
                Console.WriteLine($"[Device] TX or RX characteristic not found");
                Console.WriteLine($"[Device] TX: {_txCharacteristic != null}, RX: {_rxCharacteristic != null}");
                return false;
            }

            Console.WriteLine($"[Device] Found TX and RX characteristics");

            // Enable notifications on RX characteristic
            var cccdValue = GattClientCharacteristicConfigurationDescriptorValue.Notify;
            var enableNotificationsResult = await _rxCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(cccdValue);

            if (enableNotificationsResult != GattCommunicationStatus.Success)
            {
                Console.WriteLine($"[Device] Failed to enable notifications: {enableNotificationsResult}");
                return false;
            }

            Console.WriteLine($"[Device] Notifications enabled");

            // Subscribe to value changed events
            _rxCharacteristic.ValueChanged += OnCharacteristicValueChanged;

            // Subscribe to DATA packet events to send ACK responses
            // This is CRITICAL - device will retransmit if ACKs are not sent!
            _telemetryHandler.DataPacketReceived += async (sender, e) =>
            {
                var (packet, transactionId, commandId) = e;
                Console.WriteLine($"[Device] Sending ACK for DATA packet (TxnID={transactionId})...");

                // Create and send ACK packet
                var ackPacket = WatchPatProtocol.CreateAckCommand(commandId, status: 0, transactionId);
                await WriteCommandAsync(ackPacket, "ACK");
            };

            // Subscribe to connection status changes
            _device.ConnectionStatusChanged += OnConnectionStatusChanged;

            _isConnected = true;
            ConnectionStateChanged?.Invoke(this, "Connected");

            Console.WriteLine($"[Device] ✓ Successfully connected to {deviceInfo.Name}");

            // Send IS_DEVICE_PAIRED initialization command
            Console.WriteLine($"[Device] Initializing device (sending IS_DEVICE_PAIRED)...");
            await Task.Delay(1000); // Wait 1000ms like Android app

            // Create a TaskCompletionSource to wait for the response
            var initCompletionSource = new TaskCompletionSource<bool>();

            // Subscribe to both response types:
            // 1. IS_DEVICE_PAIRED_RESPONSE (0x2B00) - for first-time connections
            // 2. ACK with error 3 for IS_DEVICE_PAIRED (0x2A00) - if device wasn't reset

            EventHandler<bool> pairedResponseHandler = null;
            EventHandler<(ushort ackedCommand, byte status)> ackHandler = null;

            pairedResponseHandler = (sender, isPaired) =>
            {
                // Unsubscribe both handlers
                _telemetryHandler.DevicePairedResponseReceived -= pairedResponseHandler;
                _telemetryHandler.AckReceived -= ackHandler;
                // Signal success
                initCompletionSource.TrySetResult(true);
            };

            ackHandler = (sender, args) =>
            {
                // Check if this is an ACK for IS_DEVICE_PAIRED (0x2A00)
                if (args.ackedCommand == 0x2A00)
                {
                    // Unsubscribe both handlers
                    _telemetryHandler.DevicePairedResponseReceived -= pairedResponseHandler;
                    _telemetryHandler.AckReceived -= ackHandler;

                    // Error 3 = ACK_NON_UNIQ_ID (device already has pairing ID)
                    // This can occur if device wasn't reset on disconnect
                    if (args.status == 3)
                    {
                        Console.WriteLine($"[Device] ACK_NON_UNIQ_ID (status 3): Device already paired");
                        initCompletionSource.TrySetResult(true);
                    }
                    else if (args.status == 0)
                    {
                        // Success
                        initCompletionSource.TrySetResult(true);
                    }
                    else
                    {
                        // Other error
                        Console.WriteLine($"[Device] Initialization ACK returned error {args.status}");
                        initCompletionSource.TrySetResult(false);
                    }
                }
            };

            _telemetryHandler.DevicePairedResponseReceived += pairedResponseHandler;
            _telemetryHandler.AckReceived += ackHandler;

            // Send the command
            var pairedPacket = WatchPatProtocol.CreateIsDevicePairedCommand();
            await WriteCommandAsync(pairedPacket, "IS_DEVICE_PAIRED");

            // Wait for response with 5 second timeout
            var responseTask = initCompletionSource.Task;
            var timeoutTask = Task.Delay(5000);
            var completedTask = await Task.WhenAny(responseTask, timeoutTask);

            // Unsubscribe if timeout occurred
            _telemetryHandler.DevicePairedResponseReceived -= pairedResponseHandler;
            _telemetryHandler.AckReceived -= ackHandler;

            if (completedTask == responseTask && responseTask.Result)
            {
                Console.WriteLine($"[Device] Device initialized, ready for commands");
            }
            else if (completedTask == responseTask && !responseTask.Result)
            {
                Console.WriteLine($"[Device] Initialization failed - received error response");
                return false;
            }
            else
            {
                Console.WriteLine($"[Device] Warning: Initialization response timeout");
                Console.WriteLine($"[Device] Continuing anyway, but device may not be fully ready");
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Device] Connection error: {ex.Message}");
            ErrorOccurred?.Invoke(this, ex);
            return false;
        }
    }

    /// <summary>
    /// Start a sleep study session (configuration only)
    /// Sends START_SESSION command to configure the session
    /// Call ReceiveStudyDataAsync() to start data acquisition
    /// </summary>
    public async Task<bool> StartSleepStudyAsync()
    {
        if (!_isConnected)
        {
            Console.WriteLine($"[Device] Cannot start session - not connected");
            return false;
        }

        try
        {
            Console.WriteLine($"[Device] Configuring sleep study session...");

            // IMPORTANT: Wait for device to be fully ready (Android app waits 1000ms)
            Console.WriteLine($"[Device] Waiting for device to initialize...");
            await Task.Delay(1500);

            // First, request device status to establish communication
            Console.WriteLine($"[Device] Checking device readiness...");
            await RequestStatusAsync();
            await Task.Delay(1000);

            // Generate mobile ID (simplified version)
            int mobileId = WatchPatProtocol.GenerateMobileId("DESKTOP");

            // Send START_SESSION (0x0100) - Sets up session parameters
            Console.WriteLine($"[Device] Sending START_SESSION command (0x0100)...");
            Console.WriteLine($"[Device] This configures the session but does not start data acquisition");

            var sessionPacket = WatchPatProtocol.CreateStartSessionCommand(
                mobileId,
                WatchPatProtocol.SessionMode.Sleep,
                "Windows/10.0"
            );

            // Create TaskCompletionSource to wait for START_SESSION_CONFIRM
            var confirmCompletionSource = new TaskCompletionSource<bool>();

            // Subscribe to telemetry events to catch START_SESSION_CONFIRM (0x0200)
            EventHandler<TelemetryHandler.TelemetryData> confirmHandler = null;
            confirmHandler = (sender, telemetry) =>
            {
                if (telemetry.PacketType == "START_SESSION_CONFIRM")
                {
                    // Unsubscribe immediately
                    _telemetryHandler.TelemetryDataReceived -= confirmHandler;
                    Console.WriteLine($"[Device] ✓ START_SESSION_CONFIRM received");
                    confirmCompletionSource.TrySetResult(true);
                }
            };

            _telemetryHandler.TelemetryDataReceived += confirmHandler;

            bool sessionSuccess = await WriteCommandAsync(sessionPacket, "START_SESSION");

            if (!sessionSuccess)
            {
                _telemetryHandler.TelemetryDataReceived -= confirmHandler;
                Console.WriteLine($"[Device] ✗ Failed to send START_SESSION command");
                return false;
            }

            Console.WriteLine($"[Device] ✓ START_SESSION command sent successfully");
            Console.WriteLine($"[Device] Waiting for START_SESSION_CONFIRM (0x0200)...");

            // Wait for confirmation with 10 second timeout
            var confirmTask = confirmCompletionSource.Task;
            var timeoutTask = Task.Delay(10000);
            var completedTask = await Task.WhenAny(confirmTask, timeoutTask);

            // Unsubscribe if timeout occurred
            _telemetryHandler.TelemetryDataReceived -= confirmHandler;

            if (completedTask == confirmTask && confirmTask.Result)
            {
                Console.WriteLine($"[Device] ✓ Session configured successfully");
                Console.WriteLine($"[Device] Next step: Use 'Receive Study Data' to start data acquisition");
                return true;
            }
            else
            {
                Console.WriteLine($"[Device] ⚠ START_SESSION_CONFIRM timeout after 10 seconds");
                Console.WriteLine($"[Device] Session may not be properly configured");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Device] Error starting session: {ex.Message}");
            ErrorOccurred?.Invoke(this, ex);
            return false;
        }
    }

    /// <summary>
    /// Stop the current session
    /// </summary>
    public async Task<bool> StopSessionAsync()
    {
        if (!_isConnected)
        {
            Console.WriteLine($"[Device] Cannot stop session - not connected");
            return false;
        }

        try
        {
            Console.WriteLine($"[Device] Stopping session...");

            var packet = WatchPatProtocol.CreateStopSessionCommand();
            bool success = await WriteCommandAsync(packet, "STOP_SESSION");

            if (success)
            {
                Console.WriteLine($"[Device] ✓ Session stopped");
            }

            return success;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Device] Error stopping session: {ex.Message}");
            ErrorOccurred?.Invoke(this, ex);
            return false;
        }
    }

    /// <summary>
    /// Request device status
    /// </summary>
    public async Task<bool> RequestStatusAsync()
    {
        if (!_isConnected)
        {
            Console.WriteLine($"[Device] Cannot request status - not connected");
            return false;
        }

        try
        {
            var packet = WatchPatProtocol.CreateGetStatusCommand();
            return await WriteCommandAsync(packet, "GET_STATUS");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Device] Error requesting status: {ex.Message}");
            ErrorOccurred?.Invoke(this, ex);
            return false;
        }
    }

    /// <summary>
    /// Start data acquisition and receive live study data from device
    /// Sends START_ACQUISITION command then saves incoming data to file
    /// Session must be configured first with StartSleepStudyAsync()
    /// </summary>
    /// <param name="durationSeconds">How long to receive data (0 = until stopped manually)</param>
    public async Task<(int packetCount, long bytesReceived)> ReceiveStudyDataAsync(int durationSeconds = 60)
    {
        if (!_isConnected)
        {
            Console.WriteLine($"[Device] Cannot receive data - not connected");
            return (0, 0);
        }

        try
        {
            // Send START_ACQUISITION (0x0600) - Actually begins data collection
            Console.WriteLine($"[Device] Sending START_ACQUISITION command (0x0600)...");
            Console.WriteLine($"[Device] This command triggers actual data collection from the device");

            var acquisitionPacket = WatchPatProtocol.CreateStartAcquisitionCommand();
            bool acquisitionSuccess = await WriteCommandAsync(acquisitionPacket, "START_ACQUISITION");

            if (!acquisitionSuccess)
            {
                Console.WriteLine($"[Device] ✗ Failed to send START_ACQUISITION command");
                Console.WriteLine($"[Device] Cannot start data reception without acquisition command");
                return (0, 0);
            }

            Console.WriteLine($"[Device] ✓ START_ACQUISITION command sent successfully");
            Console.WriteLine($"[Device] Waiting for device to begin sending data...");
            Console.WriteLine();

            // Wait for device to start sending data
            await Task.Delay(2000);

            // Create data file with timestamp
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string filename = $"watchpat_data_{_serialNumber}_{timestamp}.dat";
            string filepath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), filename);

            Console.WriteLine($"[Device] Ready to receive live data...");
            Console.WriteLine($"[Device] Saving to: {filepath}");
            Console.WriteLine($"[Device] Duration: {(durationSeconds > 0 ? durationSeconds + " seconds" : "Until stopped")}");
            Console.WriteLine();

            int packetCount = 0;
            long totalBytes = 0;
            System.IO.FileStream fileStream = null;

            try
            {
                fileStream = new System.IO.FileStream(filepath, System.IO.FileMode.Create, System.IO.FileAccess.Write);

                // Subscribe to data packet events
                EventHandler<(byte[] packet, int transactionId, ushort commandId)> dataHandler = (sender, e) =>
                {
                    var (packet, transactionId, commandId) = e;
                    // Write packet to file (same as Android app)
                    fileStream.Write(packet, 0, packet.Length);
                    fileStream.Flush();

                    packetCount++;
                    totalBytes += packet.Length;

                    // Log every 10 packets to avoid spam
                    if (packetCount % 10 == 0)
                    {
                        Console.WriteLine($"[Data] Received {packetCount} packets, {totalBytes} bytes");
                    }
                };

                _telemetryHandler.DataPacketReceived += dataHandler;

                // Wait for specified duration or until timeout
                if (durationSeconds > 0)
                {
                    await Task.Delay(durationSeconds * 1000);
                }
                else
                {
                    // Wait indefinitely (will need manual cancellation)
                    await Task.Delay(System.Threading.Timeout.Infinite);
                }

                // Unsubscribe
                _telemetryHandler.DataPacketReceived -= dataHandler;

                Console.WriteLine();
                Console.WriteLine($"[Device] ✓ Data reception complete");
                Console.WriteLine($"[Device] Total packets: {packetCount}");
                Console.WriteLine($"[Device] Total bytes: {totalBytes:N0}");
                Console.WriteLine($"[Device] File saved: {filepath}");

                return (packetCount, totalBytes);
            }
            finally
            {
                fileStream?.Close();
                fileStream?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Device] Error receiving data: {ex.Message}");
            ErrorOccurred?.Invoke(this, ex);
            return (0, 0);
        }
    }

    /// <summary>
    /// Request stored data from device flash memory
    /// Used to download complete recording after a study is finished
    /// </summary>
    public async Task<(int packetCount, long bytesReceived)> RequestStoredDataAsync()
    {
        if (!_isConnected)
        {
            Console.WriteLine($"[Device] Cannot request stored data - not connected");
            return (0, 0);
        }

        try
        {
            // Create data file with timestamp
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string filename = $"watchpat_stored_{_serialNumber}_{timestamp}.dat";
            string filepath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), filename);

            Console.WriteLine($"[Device] Requesting stored data from device...");
            Console.WriteLine($"[Device] Saving to: {filepath}");
            Console.WriteLine();

            int packetCount = 0;
            long totalBytes = 0;
            System.IO.FileStream fileStream = null;
            bool dataComplete = false;

            try
            {
                fileStream = new System.IO.FileStream(filepath, System.IO.FileMode.Create, System.IO.FileAccess.Write);

                // Subscribe to data packet events
                EventHandler<(byte[] packet, int transactionId, ushort commandId)> dataHandler = (sender, e) =>
                {
                    var (packet, transactionId, commandId) = e;
                    // Write packet to file (same as Android app)
                    fileStream.Write(packet, 0, packet.Length);
                    fileStream.Flush();

                    packetCount++;
                    totalBytes += packet.Length;

                    // Log every 10 packets to avoid spam
                    if (packetCount % 10 == 0)
                    {
                        Console.WriteLine($"[Data] Received {packetCount} packets, {totalBytes:N0} bytes");
                    }
                };

                // Subscribe to END_OF_TEST_DATA to know when transfer is complete
                EventHandler<TelemetryHandler.TelemetryData> endHandler = (sender, telemetry) =>
                {
                    if (telemetry.PacketType == "END_OF_TEST_DATA")
                    {
                        Console.WriteLine($"[Device] END_OF_TEST_DATA received - transfer complete");
                        dataComplete = true;
                    }
                };

                _telemetryHandler.DataPacketReceived += dataHandler;
                _telemetryHandler.TelemetryDataReceived += endHandler;

                // Send SEND_STORED_DATA command
                var packet = WatchPatProtocol.CreateSendStoredDataCommand();
                bool commandSent = await WriteCommandAsync(packet, "SEND_STORED_DATA");

                if (!commandSent)
                {
                    Console.WriteLine($"[Device] Failed to send SEND_STORED_DATA command");
                    _telemetryHandler.DataPacketReceived -= dataHandler;
                    _telemetryHandler.TelemetryDataReceived -= endHandler;
                    return (0, 0);
                }

                Console.WriteLine($"[Device] ✓ SEND_STORED_DATA command sent");
                Console.WriteLine($"[Device] Waiting for device to send stored data...");
                Console.WriteLine();

                // Wait for data (max 5 minutes for large recordings)
                // Check every second if END_OF_TEST_DATA was received
                for (int i = 0; i < 300; i++)
                {
                    await Task.Delay(1000);

                    if (dataComplete)
                    {
                        Console.WriteLine($"[Device] Data transfer complete (END_OF_TEST_DATA received)");
                        break;
                    }

                    // If no packets after 30 seconds, assume no data
                    if (i == 30 && packetCount == 0)
                    {
                        Console.WriteLine($"[Device] No data received after 30 seconds");
                        break;
                    }
                }

                // Unsubscribe
                _telemetryHandler.DataPacketReceived -= dataHandler;
                _telemetryHandler.TelemetryDataReceived -= endHandler;

                Console.WriteLine();
                Console.WriteLine($"[Device] ✓ Stored data retrieval complete");
                Console.WriteLine($"[Device] Total packets: {packetCount}");
                Console.WriteLine($"[Device] Total bytes: {totalBytes:N0}");
                Console.WriteLine($"[Device] File saved: {filepath}");

                return (packetCount, totalBytes);
            }
            finally
            {
                fileStream?.Close();
                fileStream?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Device] Error requesting stored data: {ex.Message}");
            ErrorOccurred?.Invoke(this, ex);
            return (0, 0);
        }
    }

    /// <summary>
    /// Set LEDs on the device
    /// </summary>
    /// <param name="ledByte">LED control byte (0x00=off, 0xFF=all on)</param>
    public async Task<bool> SetLEDsAsync(byte ledByte)
    {
        if (!_isConnected)
        {
            Console.WriteLine($"[Device] Cannot set LEDs - not connected");
            return false;
        }

        try
        {
            Console.WriteLine($"[Device] Setting LEDs: 0x{ledByte:X2}");

            // Create a TaskCompletionSource to wait for the ACK
            var ackCompletionSource = new TaskCompletionSource<bool>();

            // Subscribe to the ACK event
            EventHandler<(ushort ackedCommand, byte status)> ackHandler = null;
            ackHandler = (sender, args) =>
            {
                // Check if this is an ACK for SET_LEDS (0x2300)
                if (args.ackedCommand == 0x2300)
                {
                    // Unsubscribe immediately
                    _telemetryHandler.AckReceived -= ackHandler;
                    // Signal result based on status
                    ackCompletionSource.TrySetResult(args.status == 0);
                }
            };

            _telemetryHandler.AckReceived += ackHandler;

            // Send the command
            var packet = WatchPatProtocol.CreateSetLEDsCommand(ledByte);
            bool commandSent = await WriteCommandAsync(packet, "SET_LEDS");

            if (!commandSent)
            {
                _telemetryHandler.AckReceived -= ackHandler;
                return false;
            }

            // Wait for ACK with 5 second timeout
            var ackTask = ackCompletionSource.Task;
            var timeoutTask = Task.Delay(5000);
            var completedTask = await Task.WhenAny(ackTask, timeoutTask);

            // Unsubscribe if timeout occurred
            _telemetryHandler.AckReceived -= ackHandler;

            if (completedTask == ackTask)
            {
                bool ackSuccess = ackTask.Result;
                if (ackSuccess)
                {
                    Console.WriteLine($"[Device] ✓ LED command acknowledged by device");
                }
                else
                {
                    Console.WriteLine($"[Device] ⚠ LED command acknowledged with error");
                }
                return ackSuccess;
            }
            else
            {
                Console.WriteLine($"[Device] ⚠ LED command ACK timeout");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Device] Error setting LEDs: {ex.Message}");
            ErrorOccurred?.Invoke(this, ex);
            return false;
        }
    }

    /// <summary>
    /// Turn all LEDs on
    /// </summary>
    public async Task<bool> SetLEDsOnAsync()
    {
        return await SetLEDsAsync(0xFF);
    }

    /// <summary>
    /// Turn all LEDs off
    /// </summary>
    public async Task<bool> SetLEDsOffAsync()
    {
        return await SetLEDsAsync(0x00);
    }

    /// <summary>
    /// Start finger detection test
    /// From DeviceCommands.java line 530-534
    /// </summary>
    public async Task<(bool success, int result)> StartFingerDetectionAsync()
    {
        if (!_isConnected)
        {
            Console.WriteLine($"[Device] Cannot start finger detection - not connected");
            return (false, 0);
        }

        try
        {
            Console.WriteLine($"[Device] Starting finger detection test...");

            // Create a TaskCompletionSource to wait for the response
            var fingerTestCompletionSource = new TaskCompletionSource<int>();

            // Subscribe to the finger test response event
            EventHandler<int> responseHandler = null;
            responseHandler = (sender, result) =>
            {
                // Unsubscribe immediately
                _telemetryHandler.FingerTestResponseReceived -= responseHandler;
                // Signal that we received the response
                fingerTestCompletionSource.TrySetResult(result);
            };

            _telemetryHandler.FingerTestResponseReceived += responseHandler;

            // Send the command
            var packet = WatchPatProtocol.CreateStartFingerDetectionCommand();
            bool commandSent = await WriteCommandAsync(packet, "START_FINGER_DETECTION");

            if (!commandSent)
            {
                _telemetryHandler.FingerTestResponseReceived -= responseHandler;
                return (false, 0);
            }

            Console.WriteLine($"[Device] ✓ Finger detection test started");
            Console.WriteLine($"[Device] Device will test if finger probe is attached and detecting pulse");
            Console.WriteLine($"[Device] Waiting for response...");

            // Wait for response with 10 second timeout
            var responseTask = fingerTestCompletionSource.Task;
            var timeoutTask = Task.Delay(10000);
            var completedTask = await Task.WhenAny(responseTask, timeoutTask);

            // Unsubscribe if timeout occurred
            _telemetryHandler.FingerTestResponseReceived -= responseHandler;

            if (completedTask == responseTask)
            {
                int result = responseTask.Result;
                Console.WriteLine($"[Device] ✓ Finger test response received: 0x{result:X8}");
                return (true, result);
            }
            else
            {
                Console.WriteLine($"[Device] ⚠ Finger test response timeout after 10 seconds");
                return (false, 0);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Device] Error starting finger detection: {ex.Message}");
            ErrorOccurred?.Invoke(this, ex);
            return (false, 0);
        }
    }

    /// <summary>
    /// Reset the device
    /// From DeviceCommands.java line 482-486
    /// </summary>
    /// <param name="resetType">0 = soft reset (default), 1 = hard reset</param>
    public async Task<bool> ResetDeviceAsync(byte resetType = 0)
    {
        if (!_isConnected)
        {
            Console.WriteLine($"[Device] Cannot reset - not connected");
            return false;
        }

        try
        {
            string resetTypeName = resetType == 0 ? "Soft Reset" : "Hard Reset";
            Console.WriteLine($"[Device] Sending {resetTypeName} command...");

            var packet = WatchPatProtocol.CreateResetDeviceCommand(resetType);
            bool success = await WriteCommandAsync(packet, $"RESET_DEVICE_{resetTypeName.Replace(" ", "_").ToUpper()}");

            if (success)
            {
                Console.WriteLine($"[Device] ✓ {resetTypeName} command sent");
                Console.WriteLine($"[Device] Device will reset and clear pairing state");

                // Wait a bit for the reset command to process
                await Task.Delay(500);
            }

            return success;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Device] Error resetting device: {ex.Message}");
            ErrorOccurred?.Invoke(this, ex);
            return false;
        }
    }

    /// <summary>
    /// Download device log file in chunks
    /// From DeviceCommands.java GetLogFilePacket and IncomingPacketHandler.java lines 462-472
    /// </summary>
    public async Task<(bool success, int totalBytes)> DownloadDeviceLogAsync()
    {
        if (!_isConnected)
        {
            Console.WriteLine($"[Device] Cannot download log - not connected");
            return (false, 0);
        }

        try
        {
            // Create log file with timestamp
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string filename = $"watchpat_log_{_serialNumber}_{timestamp}.bin";
            string filepath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), filename);

            Console.WriteLine($"[Device] Downloading device log...");
            Console.WriteLine($"[Device] Saving to: {filepath}");
            Console.WriteLine();

            int offset = 0;
            int totalBytes = 0;
            bool isEOF = false;
            System.IO.FileStream fileStream = null;

            try
            {
                fileStream = new System.IO.FileStream(filepath, System.IO.FileMode.Create, System.IO.FileAccess.Write);

                while (!isEOF)
                {
                    // Create completion source for this chunk
                    var chunkCompletionSource = new TaskCompletionSource<(byte[] chunk, int bytesReceived)>();

                    // Subscribe to log chunk event
                    EventHandler<(byte[] chunk, int bytesReceived, int transactionId)> logHandler = null;
                    logHandler = (sender, e) =>
                    {
                        var (chunk, bytesReceived, transactionId) = e;

                        // Unsubscribe immediately
                        _telemetryHandler.LogChunkReceived -= logHandler;

                        // Signal that chunk was received
                        chunkCompletionSource.TrySetResult((chunk, bytesReceived));
                    };

                    _telemetryHandler.LogChunkReceived += logHandler;

                    // Send GetLogFile command with current offset
                    Console.WriteLine($"[Device] Requesting log chunk at offset {offset}...");
                    var packet = WatchPatProtocol.CreateGetLogFileCommand(offset);
                    bool commandSent = await WriteCommandAsync(packet, $"GET_LOG_FILE (offset={offset})");

                    if (!commandSent)
                    {
                        _telemetryHandler.LogChunkReceived -= logHandler;
                        Console.WriteLine($"[Device] Failed to send GET_LOG_FILE command");
                        return (false, totalBytes);
                    }

                    // Wait for chunk with 10 second timeout
                    var chunkTask = chunkCompletionSource.Task;
                    var timeoutTask = Task.Delay(10000);
                    var completedTask = await Task.WhenAny(chunkTask, timeoutTask);

                    // Unsubscribe if timeout occurred
                    _telemetryHandler.LogChunkReceived -= logHandler;

                    if (completedTask == chunkTask)
                    {
                        var (chunk, bytesReceived) = chunkTask.Result;

                        // Write chunk to file
                        fileStream.Write(chunk, 0, chunk.Length);
                        fileStream.Flush();

                        totalBytes += bytesReceived;
                        Console.WriteLine($"[Device] ✓ Received {bytesReceived} bytes (total: {totalBytes} bytes)");

                        // Check if EOF (chunk less than 2048 bytes means last chunk)
                        if (bytesReceived < 2048)
                        {
                            isEOF = true;
                            Console.WriteLine($"[Device] ✓ EOF reached (chunk size {bytesReceived} < 2048)");
                        }
                        else
                        {
                            // Increment offset for next chunk
                            offset += 2048;
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[Device] ⚠ Log chunk timeout after 10 seconds");
                        return (false, totalBytes);
                    }
                }

                Console.WriteLine();
                Console.WriteLine($"[Device] ✓ Device log download complete");
                Console.WriteLine($"[Device] Total bytes: {totalBytes:N0}");
                Console.WriteLine($"[Device] File saved: {filepath}");

                return (true, totalBytes);
            }
            finally
            {
                fileStream?.Close();
                fileStream?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Device] Error downloading log: {ex.Message}");
            ErrorOccurred?.Invoke(this, ex);
            return (false, 0);
        }
    }

    /// <summary>
    /// Write a command packet to the TX characteristic (with chunking)
    /// </summary>
    private async Task<bool> WriteCommandAsync(WatchPatPacket packet, string description = "")
    {
        if (_txCharacteristic == null)
            return false;

        try
        {
            Console.WriteLine($"[Device] Sending command: {packet.GetCommandName()} ({description})");
            Console.WriteLine($"[Device] Full packet: {packet.ToHexString()}");

            // Split into 20-byte chunks
            var chunks = packet.SplitIntoChunks();
            Console.WriteLine($"[Device] Packet split into {chunks.Count} chunk(s)");

            // Send each chunk
            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                Console.WriteLine($"[Device] TX Chunk {i + 1}/{chunks.Count}: {WatchPatProtocol.ByteArrayToHex(chunk)} ({chunk.Length} bytes)");

                var writer = new DataWriter();
                writer.WriteBytes(chunk);

                var result = await _txCharacteristic.WriteValueAsync(
                    writer.DetachBuffer(),
                    GattWriteOption.WriteWithoutResponse
                );

                if (result != GattCommunicationStatus.Success)
                {
                    Console.WriteLine($"[Device] Failed to send chunk {i + 1}: {result}");
                    return false;
                }

                // Small delay between chunks to avoid overwhelming the device
                if (i < chunks.Count - 1)
                {
                    await Task.Delay(10);
                }
            }

            Console.WriteLine($"[Device] ✓ All {chunks.Count} chunk(s) sent successfully");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Device] Write error: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Handle incoming data from RX characteristic
    /// </summary>
    private void OnCharacteristicValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var data = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(data);

            // Log ALL incoming data
            Console.WriteLine($"[RX] Received {data.Length} bytes: {WatchPatProtocol.ByteArrayToHex(data)}");

            // Process through telemetry handler
            _telemetryHandler.ProcessIncomingData(data);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Device] Error processing incoming data: {ex.Message}");
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    /// <summary>
    /// Handle connection status changes
    /// </summary>
    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        var status = sender.ConnectionStatus;
        Console.WriteLine($"[Device] Connection status changed: {status}");

        _isConnected = status == BluetoothConnectionStatus.Connected;
        ConnectionStateChanged?.Invoke(this, status.ToString());

        if (!_isConnected)
        {
            Console.WriteLine($"[Device] Device disconnected");
        }
    }

    /// <summary>
    /// Disconnect from the device
    /// </summary>
    public void Disconnect()
    {
        try
        {
            Console.WriteLine($"[Device] Disconnecting...");

            if (_rxCharacteristic != null)
            {
                _rxCharacteristic.ValueChanged -= OnCharacteristicValueChanged;
                _rxCharacteristic = null;
            }

            if (_txCharacteristic != null)
            {
                _txCharacteristic = null;
            }

            if (_device != null)
            {
                _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                _device.Dispose();
                _device = null;
            }

            _isConnected = false;
            ConnectionStateChanged?.Invoke(this, "Disconnected");

            Console.WriteLine($"[Device] Disconnected");
            Console.WriteLine($"[Device] Wait 2 seconds before reconnecting to allow cleanup...");

            // Give Windows and device time to cleanup
            System.Threading.Thread.Sleep(2000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Device] Error during disconnect: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Disconnect();
    }
}
