using System;
using System.Collections.Generic;
using System.Linq;

namespace WatchPatBLE;

/// <summary>
/// WatchPAT binary packet structure - matches Android DeviceCommands.java
/// Based on reverse-engineered protocol from ITAMAR Medical device
/// </summary>
public class WatchPatPacket
{
    // Magic number for packet header
    private const ushort MAGIC_NUMBER = 0xBBBB;

    // Command IDs (from DeviceCommands.java)
    public enum CommandId : ushort
    {
        StartSession = 0x0100,           // 256 decimal - SessionStartCommandPacket
        StopAcquisition = 0x0700,        // 1792
        StartAcquisition = 0x0600,       // 1536
        ResetDevice = 0x0B00,            // 2816
        GetParametersFile = 0x0D00,      // 3328
        SetParametersFile = 0x0C00,      // 3072
        SendStoredData = 0x1000,         // 4096
        BitRequest = 0x1200,             // 4608
        TechnicalStatusRequest = 0x1500, // 5376 - GET STATUS!
        GetEEPROM = 0x1D00,              // 7424
        SetEEPROM = 0x1F00,              // 7936
        SetLEDs = 0x2300,                // 8960
        SetDeviceSerial = 0x2400,        // 9216
        StartFingerDetection = 0x2500,   // 9472
        ClearData = 0x2700,              // 9984
        IsDevicePaired = 0x2A00,         // 10752
        FWUpgradeRequest = 0x3000,       // 12288
        ResetReason = 0x3900,            // 14592
        GetLogFile = 0x4400,             // 17408
        SetNightsCounter = 0x4600        // 17920
    }

    /// <summary>
    /// 24-byte packet header structure
    /// </summary>
    public class Header
    {
        public ushort Magic { get; set; } = MAGIC_NUMBER;
        public ushort CommandId { get; set; }
        public long Timestamp { get; set; }
        public int TransactionId { get; set; }
        public ushort Length { get; set; }
        public ushort Flags { get; set; }
        public ushort Zero { get; set; } = 0;
        public ushort Crc { get; set; }

        /// <summary>
        /// Convert header to byte array (24 bytes)
        /// </summary>
        public byte[] ToBytes()
        {
            var buffer = new List<byte>();

            // Java ByteBuffer writes big-endian, but pre-reverses values
            // C# BitConverter writes little-endian natively
            // So: DON'T reverse values that Java already reversed!
            buffer.AddRange(BitConverter.GetBytes(ReverseBytes(Magic)));      // Needs reverse (not pre-reversed in Java)
            buffer.AddRange(BitConverter.GetBytes(ReverseBytes(CommandId)));  // Needs reverse (not pre-reversed in Java)
            buffer.AddRange(BitConverter.GetBytes(ReverseBytes(Timestamp)));  // Needs reverse (IS pre-reversed in Java)
            buffer.AddRange(BitConverter.GetBytes(TransactionId));            // NO reverse (IS pre-reversed in Java)
            buffer.AddRange(BitConverter.GetBytes(Length));                   // NO reverse (IS pre-reversed in Java)
            buffer.AddRange(BitConverter.GetBytes(Flags));                    // NO reverse (not pre-reversed in Java)
            buffer.AddRange(BitConverter.GetBytes(Zero));                     // NO reverse (always 0)
            buffer.AddRange(BitConverter.GetBytes(Crc));                      // NO reverse (IS pre-reversed in Java)

            return buffer.ToArray();
        }
    }

    private Header _header;
    private byte[] _payload;
    private byte[] _builtPacket;
    private static int _nextTransactionId = 1;

    public WatchPatPacket(CommandId commandId, byte[] payload = null, ushort flags = 0, long timestamp = 0)
    {
        _header = new Header
        {
            CommandId = (ushort)commandId,
            Timestamp = timestamp,
            TransactionId = GetNextTransactionId(),
            Length = (ushort)(24 + (payload?.Length ?? 0)),
            Flags = flags
        };

        _payload = payload ?? Array.Empty<byte>();
    }

    /// <summary>
    /// Generate next transaction ID
    /// </summary>
    private static int GetNextTransactionId()
    {
        return _nextTransactionId++;
    }

    /// <summary>
    /// Build complete packet with CRC (cached to avoid recalculation)
    /// </summary>
    public byte[] Build()
    {
        // Return cached packet if already built
        if (_builtPacket != null)
        {
            return _builtPacket;
        }

        // Combine header and payload
        var headerBytes = _header.ToBytes();
        var fullPacket = new byte[headerBytes.Length + _payload.Length];
        Array.Copy(headerBytes, 0, fullPacket, 0, headerBytes.Length);
        Array.Copy(_payload, 0, fullPacket, headerBytes.Length, _payload.Length);

        // Calculate CRC (excluding the CRC field itself, but we set it to 0 first)
        _header.Crc = CalculateCrc16(fullPacket);

        // Rebuild with correct CRC
        headerBytes = _header.ToBytes();
        Array.Copy(headerBytes, 0, fullPacket, 0, headerBytes.Length);

        // Cache the result
        _builtPacket = fullPacket;

        return fullPacket;
    }

    /// <summary>
    /// Split packet into 20-byte chunks for BLE transmission
    /// From DeviceCommands.java line 82-96
    /// </summary>
    public List<byte[]> SplitIntoChunks()
    {
        var fullPacket = Build();
        var chunks = new List<byte[]>();

        int offset = 0;
        while (offset < fullPacket.Length)
        {
            int chunkSize = Math.Min(20, fullPacket.Length - offset);
            byte[] chunk = new byte[chunkSize];
            Array.Copy(fullPacket, offset, chunk, 0, chunkSize);
            chunks.Add(chunk);
            offset += chunkSize;
        }

        return chunks;
    }

    /// <summary>
    /// Calculate CRC-16 checksum
    /// From DeviceCommands.java line 407-431
    /// </summary>
    public static ushort CalculateCrc16(byte[] data)
    {
        ushort crc = 0xFFFF; // Start with -1 (all bits set)

        for (int i = 0; i < data.Length; i++)
        {
            byte currentByte = data[i];

            // Process each bit
            for (ushort mask = 0x80; mask > 0; mask >>= 1)
            {
                bool xorFlag = (crc & 0x8000) != 0; // Check MSB

                if ((currentByte & mask) != 0)
                {
                    xorFlag = !xorFlag;
                }

                crc <<= 1; // Shift left

                if (xorFlag)
                {
                    crc ^= 0x1021; // Polynomial for CRC-16-CCITT
                }
            }

            crc &= 0xFFFF; // Keep it 16-bit
        }

        return crc;
    }

    /// <summary>
    /// Reverse bytes for endianness conversion (little-endian)
    /// </summary>
    private static ushort ReverseBytes(ushort value)
    {
        return (ushort)((value >> 8) | (value << 8));
    }

    private static int ReverseBytes(int value)
    {
        return (int)((value >> 24) & 0xFF) |
               (int)((value >> 8) & 0xFF00) |
               (int)((value << 8) & 0xFF0000) |
               (int)((value << 24) & 0xFF000000);
    }

    private static long ReverseBytes(long value)
    {
        return ((value >> 56) & 0xFF) |
               ((value >> 40) & 0xFF00) |
               ((value >> 24) & 0xFF0000) |
               ((value >> 8) & 0xFF000000) |
               ((value << 8) & 0xFF00000000L) |
               ((value << 24) & 0xFF0000000000L) |
               ((value << 40) & 0xFF000000000000L) |
               ((value << 56) & unchecked((long)0xFF00000000000000L));
    }

    /// <summary>
    /// Format packet as hex string for logging
    /// </summary>
    public string ToHexString()
    {
        var fullPacket = Build();
        return BitConverter.ToString(fullPacket).Replace("-", " ");
    }

    /// <summary>
    /// Get command name for logging
    /// </summary>
    public string GetCommandName()
    {
        return ((CommandId)_header.CommandId).ToString();
    }
}
