using System;
using System.Collections.Generic;
using System.Text;

namespace WatchPatBLE;

/// <summary>
/// Handles telemetry data reception and transmission for WatchPAT device
/// Parses response packets with 24-byte header (same format as commands)
/// </summary>
public class TelemetryHandler
{
    private readonly List<byte> _receiveBuffer;
    private readonly object _lock = new object();

    public event EventHandler<byte[]> PacketReceived;
    public event EventHandler<TelemetryData> TelemetryDataReceived;
    public event EventHandler<bool> DevicePairedResponseReceived;
    public event EventHandler<(ushort ackedCommand, byte status)> AckReceived;
    public event EventHandler<int> FingerTestResponseReceived;
    public event EventHandler<(byte[] packet, int transactionId, ushort commandId)> DataPacketReceived;  // Fired when DATA (0x0800) packets arrive with transaction ID for ACK
    public event EventHandler<(byte[] chunk, int bytesReceived, int transactionId)> LogChunkReceived;  // Fired when LOG_FILE_RESPONSE (0x4500) arrives

    public class TelemetryData
    {
        public DateTime Timestamp { get; set; }
        public byte[] RawData { get; set; }
        public string PacketType { get; set; }
        public Dictionary<string, object> ParsedData { get; set; }
    }

    public TelemetryHandler()
    {
        _receiveBuffer = new List<byte>();
    }

    /// <summary>
    /// Process incoming data from RX characteristic
    /// </summary>
    public void ProcessIncomingData(byte[] data)
    {
        if (data == null || data.Length == 0)
            return;

        Console.WriteLine($"[Telemetry] RX: {WatchPatProtocol.ByteArrayToHex(data)} ({data.Length} bytes)");

        lock (_lock)
        {
            _receiveBuffer.AddRange(data);

            // Try to extract complete packets
            while (TryExtractPacket(out byte[] packet))
            {
                ProcessPacket(packet);
            }
        }
    }

    /// <summary>
    /// Try to extract a complete packet from buffer
    /// Packets use 24-byte header with 0xBB 0xBB magic number
    /// </summary>
    private bool TryExtractPacket(out byte[] packet)
    {
        packet = null;

        // Need at least 24 bytes for header
        if (_receiveBuffer.Count < 24)
        {
            Console.WriteLine($"[Telemetry] Buffer has {_receiveBuffer.Count} bytes, need at least 24 for header");
            return false;
        }

        // Look for packet start marker (0xBB 0xBB)
        if (_receiveBuffer[0] != 0xBB || _receiveBuffer[1] != 0xBB)
        {
            // Invalid header, skip one byte and try again
            Console.WriteLine($"[Telemetry] Invalid magic, skipping byte: 0x{_receiveBuffer[0]:X2} (buffer has {_receiveBuffer.Count} bytes)");
            _receiveBuffer.RemoveAt(0);
            return false;
        }

        // Read length field (bytes 16-17, little-endian)
        int packetLength = _receiveBuffer[16] | (_receiveBuffer[17] << 8);
        Console.WriteLine($"[Telemetry] Found valid magic, packet length: {packetLength} bytes");

        // Validate length
        if (packetLength < 24 || packetLength > 2048)
        {
            Console.WriteLine($"[Telemetry] Invalid length: {packetLength}, skipping byte");
            _receiveBuffer.RemoveAt(0);
            return false;
        }

        // Check if we have the complete packet
        if (_receiveBuffer.Count < packetLength)
        {
            // Not enough data yet
            Console.WriteLine($"[Telemetry] Incomplete packet: have {_receiveBuffer.Count} bytes, need {packetLength}");
            return false;
        }

        // Extract packet
        packet = _receiveBuffer.GetRange(0, packetLength).ToArray();
        Console.WriteLine($"[Telemetry] Extracted packet: {WatchPatProtocol.ByteArrayToHex(packet)}");
        _receiveBuffer.RemoveRange(0, packetLength);
        Console.WriteLine($"[Telemetry] Buffer now has {_receiveBuffer.Count} bytes remaining");

        // Verify CRC-16
        Console.WriteLine($"[Telemetry] Verifying CRC...");
        if (!VerifyCrc16(packet))
        {
            Console.WriteLine($"[Telemetry] ✗ CRC FAILED for packet");
            return false;
        }

        Console.WriteLine($"[Telemetry] ✓ CRC verified successfully");
        return true;
    }

    /// <summary>
    /// Verify packet CRC-16 (same algorithm as WatchPatPacket)
    /// </summary>
    private bool VerifyCrc16(byte[] packet)
    {
        if (packet.Length < 24)
            return false;

        // Read CRC from packet (bytes 22-23, little-endian)
        ushort packetCrc = (ushort)(packet[22] | (packet[23] << 8));

        // Calculate CRC on packet with CRC field set to 0
        byte[] temp = new byte[packet.Length];
        Array.Copy(packet, temp, packet.Length);
        temp[22] = 0;
        temp[23] = 0;

        ushort calculatedCrc = WatchPatPacket.CalculateCrc16(temp);

        if (packetCrc != calculatedCrc)
        {
            Console.WriteLine($"[Telemetry] CRC mismatch: packet=0x{packetCrc:X4}, calculated=0x{calculatedCrc:X4}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Process a complete packet
    /// </summary>
    private void ProcessPacket(byte[] packet)
    {
        // Parse packet header (same byte order as we write them)
        // CommandId needs byte reversal (we reverse when writing, so reverse when reading)
        ushort commandIdRaw = (ushort)(packet[2] | (packet[3] << 8));
        ushort commandId = (ushort)((commandIdRaw >> 8) | (commandIdRaw << 8)); // Reverse bytes

        int transactionId = packet[12] | (packet[13] << 8) | (packet[14] << 16) | (packet[15] << 24);
        ushort length = (ushort)(packet[16] | (packet[17] << 8));

        // Extract payload (if any)
        byte[] payload = null;
        if (length > 24)
        {
            int payloadLength = length - 24;
            payload = new byte[payloadLength];
            Array.Copy(packet, 24, payload, 0, payloadLength);
        }

        Console.WriteLine($"[Telemetry] Packet: CmdID=0x{commandId:X4}, TxnID={transactionId}, Length={length}, Payload={payload?.Length ?? 0} bytes");

        // Notify raw packet listeners
        PacketReceived?.Invoke(this, packet);

        // Parse packet
        var telemetry = ParsePacket(commandId, transactionId, payload, packet);
        if (telemetry != null)
        {
            telemetry.RawData = packet;
            TelemetryDataReceived?.Invoke(this, telemetry);
        }
    }

    /// <summary>
    /// Parse packet into telemetry data based on command ID
    /// </summary>
    private TelemetryData ParsePacket(ushort commandId, int transactionId, byte[] payload, byte[] packet)
    {
        var telemetry = new TelemetryData
        {
            Timestamp = DateTime.Now,
            ParsedData = new Dictionary<string, object>()
        };

        telemetry.ParsedData["TransactionId"] = transactionId;
        telemetry.ParsedData["CommandId"] = $"0x{commandId:X4}";

        switch (commandId)
        {
            case 0x0000: // ACK
                telemetry.PacketType = "ACK";
                if (payload != null && payload.Length >= 5)
                {
                    // ACK Status Codes (from Java IncomingPacketHandler.java):
                    // 0 = ACK_OK
                    // 1 = ACK_CRC_ERR
                    // 2 = ACK_ILLEGAL_OP_CODE
                    // 3 = ACK_NON_UNIQ_ID (device already paired)
                    // 4 = ACK_INVALID_PARAM

                    // Acked command also needs byte reversal
                    ushort ackedCommandRaw = (ushort)(payload[0] | (payload[1] << 8));
                    ushort ackedCommand = (ushort)((ackedCommandRaw >> 8) | (ackedCommandRaw << 8));
                    byte status = payload[2];
                    telemetry.ParsedData["AckedCommand"] = $"0x{ackedCommand:X4}";
                    telemetry.ParsedData["Status"] = status;
                    telemetry.ParsedData["StatusText"] = status == 0 ? "OK" : $"Error {status}";
                    Console.WriteLine($"[Telemetry] ACK for command 0x{ackedCommand:X4}: {telemetry.ParsedData["StatusText"]}");

                    // Notify listeners about ACK
                    AckReceived?.Invoke(this, (ackedCommand, status));
                }
                break;

            case 0x0200: // START_SESSION_CONFIRM
                telemetry.PacketType = "START_SESSION_CONFIRM";
                Console.WriteLine($"[Telemetry] Session started successfully");
                break;

            case 0x0800: // DATA (telemetry data)
                telemetry.PacketType = "TELEMETRY_DATA";
                ParseTelemetryData(payload, telemetry);

                // Notify listeners - pass the FULL packet (header + payload) for saving
                // Android app saves complete packets to file without parsing
                // Pass packet, transaction ID, and command ID so caller can send ACK
                DataPacketReceived?.Invoke(this, (packet, transactionId, commandId));
                break;

            case 0x0900: // END_OF_TEST_DATA
                telemetry.PacketType = "END_OF_TEST_DATA";
                if (payload != null && payload.Length >= 1)
                {
                    telemetry.ParsedData["EndReason"] = payload[0];
                    Console.WriteLine($"[Telemetry] Recording ended, reason: {payload[0]}");
                }
                break;

            case 0x0A00: // ERROR_STATUS
                telemetry.PacketType = "ERROR_STATUS";
                if (payload != null && payload.Length >= 1)
                {
                    byte errorCode = payload[0];
                    telemetry.ParsedData["ErrorCode"] = errorCode;
                    string errorMsg = errorCode switch
                    {
                        17 => "Battery error",
                        71 => "Flash memory full",
                        _ => $"Error code {errorCode}"
                    };
                    telemetry.ParsedData["ErrorMessage"] = errorMsg;
                    Console.WriteLine($"[Telemetry] ERROR: {errorMsg}");
                }
                break;

            case 0x1600: // TECHNICAL_STATUS_REPORT
                telemetry.PacketType = "TECHNICAL_STATUS";
                ParseTechnicalStatus(payload, telemetry);
                break;

            case 0x2600: // FINGER_TEST (9728 decimal)
                telemetry.PacketType = "FINGER_TEST_RESPONSE";
                if (payload != null && payload.Length >= 4)
                {
                    // Response is a 32-bit integer (reversed bytes)
                    int fingerTestResult = BitConverter.ToInt32(payload, 0);
                    // Reverse bytes like Java does
                    fingerTestResult = ReverseInt32(fingerTestResult);

                    telemetry.ParsedData["FingerTestResult"] = fingerTestResult;
                    telemetry.ParsedData["ResultHex"] = $"0x{fingerTestResult:X8}";

                    Console.WriteLine($"[Telemetry] Finger Test Response: 0x{fingerTestResult:X8} ({fingerTestResult})");

                    // Notify listeners about finger test response
                    FingerTestResponseReceived?.Invoke(this, fingerTestResult);
                }
                break;

            case 0x2B00: // IS_DEVICE_PAIRED_RES
                telemetry.PacketType = "IS_DEVICE_PAIRED_RESPONSE";
                if (payload != null && payload.Length >= 5)
                {
                    // Payload contains: [OriginalCmdId:2 bytes][Status:1 byte][Reserved:2 bytes]
                    ushort originalCmdRaw = (ushort)(payload[0] | (payload[1] << 8));
                    ushort originalCmd = (ushort)((originalCmdRaw >> 8) | (originalCmdRaw << 8));
                    byte status = payload[2];

                    telemetry.ParsedData["OriginalCommand"] = $"0x{originalCmd:X4}";
                    telemetry.ParsedData["IsPaired"] = status != 0;
                    telemetry.ParsedData["Status"] = status;

                    Console.WriteLine($"[Telemetry] IS_DEVICE_PAIRED response: Paired={status != 0}, Status={status}");

                    // Notify listeners that initialization response was received
                    DevicePairedResponseReceived?.Invoke(this, status != 0);
                }
                break;

            case 0x4500: // LOG_FILE_RESPONSE
                telemetry.PacketType = "LOG_FILE_RESPONSE";
                if (payload != null && payload.Length >= 4)
                {
                    // Payload structure (from Java IncomingPacketHandler.java line 462-472):
                    // Bytes 0-3: Chunk size (int, actual bytes in this chunk)
                    // Bytes 4+: Log file data
                    int bytesInChunk = BitConverter.ToInt32(payload, 0);
                    // Reverse bytes (Java reads it reversed)
                    bytesInChunk = ReverseInt32(bytesInChunk);

                    telemetry.ParsedData["ChunkSize"] = bytesInChunk;
                    telemetry.ParsedData["IsEOF"] = bytesInChunk < 2048;

                    // Extract log chunk data (skip first 4 bytes which contain size)
                    byte[] logChunk = new byte[Math.Min(bytesInChunk, payload.Length - 4)];
                    Array.Copy(payload, 4, logChunk, 0, logChunk.Length);

                    Console.WriteLine($"[Telemetry] LOG_FILE_RESPONSE: {bytesInChunk} bytes{(bytesInChunk < 2048 ? " (EOF)" : "")}");

                    // Notify listeners with chunk data
                    LogChunkReceived?.Invoke(this, (logChunk, bytesInChunk, transactionId));
                }
                break;

            default:
                telemetry.PacketType = $"UNKNOWN_0x{commandId:X4}";
                Console.WriteLine($"[Telemetry] Unknown command ID: 0x{commandId:X4}");
                if (payload != null && payload.Length > 0)
                {
                    Console.WriteLine($"[Telemetry] Payload: {WatchPatProtocol.ByteArrayToHex(payload)}");
                }
                break;
        }

        return telemetry;
    }

    private void ParseTechnicalStatus(byte[] payload, TelemetryData telemetry)
    {
        if (payload != null && payload.Length >= 10)
        {
            // Parse technical status payload
            ushort batteryVoltage = (ushort)(payload[0] | (payload[1] << 8));
            ushort vddVoltage = (ushort)(payload[2] | (payload[3] << 8));
            ushort ledIR = (ushort)(payload[4] | (payload[5] << 8));
            ushort ledRed = (ushort)(payload[6] | (payload[7] << 8));
            ushort ledPAT = (ushort)(payload[8] | (payload[9] << 8));

            telemetry.ParsedData["BatteryVoltage"] = batteryVoltage;
            telemetry.ParsedData["VddVoltage"] = vddVoltage;
            telemetry.ParsedData["LED_IR"] = ledIR;
            telemetry.ParsedData["LED_Red"] = ledRed;
            telemetry.ParsedData["LED_PAT"] = ledPAT;

            Console.WriteLine($"[Telemetry] Tech Status - Battery: {batteryVoltage}mV, VDD: {vddVoltage}mV, LEDs: IR={ledIR}, Red={ledRed}, PAT={ledPAT}");
        }
    }

    private void ParseTelemetryData(byte[] payload, TelemetryData telemetry)
    {
        if (payload == null || payload.Length < 3)
            return;

        Console.WriteLine($"\n[Telemetry] ═══ DATA Packet Breakdown ═══");
        Console.WriteLine($"[Telemetry] Total Payload: {payload.Length} bytes");

        // First 3 bytes appear to be header
        Console.WriteLine($"[Telemetry] Packet Header: {payload[0]:X2} {payload[1]:X2} {payload[2]:X2}");
        telemetry.ParsedData["PacketType"] = payload[0];
        telemetry.ParsedData["PacketSubtype"] = payload[1];

        // Find and extract all AA AA frames
        var frames = ExtractFrames(payload, 0xAA, 0xAA);
        if (frames.Count > 0)
        {
            Console.WriteLine($"[Telemetry] Found {frames.Count} data frames (AA AA markers):");
            for (int i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                Console.WriteLine($"[Telemetry]   Frame {i + 1}: {frame.Length} bytes");
                if (frame.Length > 0)
                {
                    // Show first few bytes to identify frame type
                    int previewLen = Math.Min(16, frame.Length);
                    string preview = string.Join(" ", frame.Take(previewLen).Select(b => b.ToString("X2")));
                    Console.WriteLine($"[Telemetry]     Start: {preview}{(frame.Length > previewLen ? "..." : "")}");

                    // Try to identify frame type by first byte after AA AA
                    if (frame.Length >= 2)
                    {
                        byte frameId = frame[0];
                        byte frameType = frame[1];
                        Console.WriteLine($"[Telemetry]     Type: {frameId:X2} {frameType:X2}");
                    }
                }
            }
        }

        // Find and extract all DD DD samples
        var samples = ExtractFrames(payload, 0xDD, 0xDD);
        if (samples.Count > 0)
        {
            Console.WriteLine($"[Telemetry] Found {samples.Count} samples (DD DD markers):");
            for (int i = 0; i < samples.Count; i++)
            {
                var sample = samples[i];
                Console.WriteLine($"[Telemetry]   Sample {i + 1}: {sample.Length} bytes");
                if (sample.Length >= 14)
                {
                    // Parse sample fields (reverse-engineered structure)
                    ushort timestamp = (ushort)(sample[0] | (sample[1] << 8));
                    byte value1 = sample[2];
                    byte value2 = sample[3];
                    ushort value3 = (ushort)(sample[4] | (sample[5] << 8));
                    ushort value4 = (ushort)(sample[6] | (sample[7] << 8));
                    ushort value5 = (ushort)(sample[8] | (sample[9] << 8));
                    ushort value6 = (ushort)(sample[10] | (sample[11] << 8));

                    Console.WriteLine($"[Telemetry]     Time: {timestamp} | Val1: {value1:X2} | Val2: {value2:X2}");
                    Console.WriteLine($"[Telemetry]     Data: {value3} | {value4} | {value5} | {value6:X4}");

                    // Store parsed values
                    telemetry.ParsedData[$"Sample{i + 1}_Timestamp"] = timestamp;
                    telemetry.ParsedData[$"Sample{i + 1}_Value1"] = value1;
                    telemetry.ParsedData[$"Sample{i + 1}_Value2"] = value2;
                }
                else
                {
                    // Show full sample if short
                    string sampleHex = string.Join(" ", sample.Select(b => b.ToString("X2")));
                    Console.WriteLine($"[Telemetry]     Data: {sampleHex}");
                }
            }
        }

        Console.WriteLine($"[Telemetry] ═══════════════════════════════\n");
    }

    /// <summary>
    /// Extract all frames/samples separated by a 2-byte marker
    /// </summary>
    private List<byte[]> ExtractFrames(byte[] data, byte marker1, byte marker2)
    {
        var frames = new List<byte[]>();

        for (int i = 0; i < data.Length - 1; i++)
        {
            if (data[i] == marker1 && data[i + 1] == marker2)
            {
                // Found marker, find next marker or end of data
                int nextMarkerPos = -1;
                for (int j = i + 2; j < data.Length - 1; j++)
                {
                    if (data[j] == marker1 && data[j + 1] == marker2)
                    {
                        nextMarkerPos = j;
                        break;
                    }
                }

                // Extract frame (skip the marker itself)
                int frameStart = i + 2;
                int frameEnd = (nextMarkerPos != -1) ? nextMarkerPos : data.Length;
                int frameLength = frameEnd - frameStart;

                if (frameLength > 0)
                {
                    byte[] frame = new byte[frameLength];
                    Array.Copy(data, frameStart, frame, 0, frameLength);
                    frames.Add(frame);
                }

                // Skip to next marker position if found
                if (nextMarkerPos != -1)
                {
                    i = nextMarkerPos - 1; // -1 because loop will increment
                }
                else
                {
                    break; // No more markers
                }
            }
        }

        return frames;
    }

    /// <summary>
    /// Reverse 32-bit integer bytes (for endianness conversion)
    /// </summary>
    private int ReverseInt32(int value)
    {
        return ((value >> 24) & 0xFF) |
               ((value >> 8) & 0xFF00) |
               ((value << 8) & 0xFF0000) |
               ((value << 24) & unchecked((int)0xFF000000));
    }

    /// <summary>
    /// Clear receive buffer
    /// </summary>
    public void ClearBuffer()
    {
        lock (_lock)
        {
            _receiveBuffer.Clear();
        }
    }
}
