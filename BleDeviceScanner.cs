using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;

namespace WatchPatBLE;

/// <summary>
/// Scans for ITAMAR WatchPAT BLE devices
/// </summary>
public class BleDeviceScanner : IDisposable
{
    private readonly BluetoothLEAdvertisementWatcher _watcher;
    private readonly Dictionary<ulong, DeviceInfo> _discoveredDevices;
    private readonly object _lock = new object();
    private TaskCompletionSource<List<DeviceInfo>> _scanCompletionSource;

    public event EventHandler<DeviceInfo> DeviceDiscovered;

    public class DeviceInfo
    {
        public string Name { get; set; }
        public string SerialNumber { get; set; }
        public ulong BluetoothAddress { get; set; }
        public short SignalStrength { get; set; }
        public bool IsNew { get; set; }
        public DateTime LastSeen { get; set; }
    }

    public BleDeviceScanner()
    {
        _discoveredDevices = new Dictionary<ulong, DeviceInfo>();
        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        _watcher.Received += OnAdvertisementReceived;
        _watcher.Stopped += OnWatcherStopped;
    }

    /// <summary>
    /// Scan for ITAMAR devices for specified duration
    /// </summary>
    public async Task<List<DeviceInfo>> ScanAsync(TimeSpan duration, string specificSerial = null)
    {
        Console.WriteLine($"[Scanner] Starting BLE scan for {duration.TotalSeconds} seconds...");

        lock (_lock)
        {
            _discoveredDevices.Clear();
            _scanCompletionSource = new TaskCompletionSource<List<DeviceInfo>>();
        }

        _watcher.Start();

        // Wait for scan duration
        await Task.Delay(duration);

        _watcher.Stop();

        // Wait for stopped event
        var devices = await _scanCompletionSource.Task;

        // Filter by specific serial if provided
        if (!string.IsNullOrEmpty(specificSerial))
        {
            devices = devices.Where(d => d.SerialNumber == specificSerial).ToList();
        }

        Console.WriteLine($"[Scanner] Scan complete. Found {devices.Count} ITAMAR device(s).");
        return devices;
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            // Try to get device name from advertisement
            var localName = args.Advertisement.LocalName;

            // If not in advertisement, try to get from DeviceInformation
            if (string.IsNullOrEmpty(localName))
            {
                // This requires async but we're in event handler, so we skip for now
                return;
            }

            // Check if this is an ITAMAR device
            if (!localName.StartsWith(WatchPatProtocol.DeviceNamePrefix))
                return;

            var address = args.BluetoothAddress;

            lock (_lock)
            {
                if (_discoveredDevices.ContainsKey(address))
                {
                    // Update existing device
                    _discoveredDevices[address].SignalStrength = args.RawSignalStrengthInDBm;
                    _discoveredDevices[address].LastSeen = DateTime.Now;
                }
                else
                {
                    // Parse serial number
                    var serialNumber = WatchPatProtocol.ParseSerialNumber(localName);
                    if (serialNumber == null)
                    {
                        Console.WriteLine($"[Scanner] Invalid device name format: {localName}");
                        return;
                    }

                    // Check if it's a new device (ends with 'N')
                    bool isNew = localName.EndsWith(WatchPatProtocol.DeviceNameSuffixNew);

                    var deviceInfo = new DeviceInfo
                    {
                        Name = localName,
                        SerialNumber = serialNumber,
                        BluetoothAddress = address,
                        SignalStrength = args.RawSignalStrengthInDBm,
                        IsNew = isNew,
                        LastSeen = DateTime.Now
                    };

                    _discoveredDevices[address] = deviceInfo;

                    Console.WriteLine($"[Scanner] Found device: {localName} (S/N: {serialNumber}, RSSI: {args.RawSignalStrengthInDBm} dBm, New: {isNew})");

                    // Notify listeners
                    DeviceDiscovered?.Invoke(this, deviceInfo);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Scanner] Error processing advertisement: {ex.Message}");
        }
    }

    private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        Console.WriteLine($"[Scanner] Watcher stopped. Status: {args.Error}");

        lock (_lock)
        {
            var devices = _discoveredDevices.Values.ToList();
            _scanCompletionSource?.TrySetResult(devices);
        }
    }

    /// <summary>
    /// Get device by Bluetooth address
    /// </summary>
    public async Task<BluetoothLEDevice> GetDeviceAsync(ulong bluetoothAddress)
    {
        try
        {
            Console.WriteLine($"[Scanner] Getting device for address: {bluetoothAddress:X}");
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);

            if (device == null)
            {
                Console.WriteLine($"[Scanner] Failed to get device.");
                return null;
            }

            Console.WriteLine($"[Scanner] Device retrieved: {device.Name}");
            return device;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Scanner] Error getting device: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_watcher != null)
        {
            _watcher.Stop();
            _watcher.Received -= OnAdvertisementReceived;
            _watcher.Stopped -= OnWatcherStopped;
        }
    }
}
