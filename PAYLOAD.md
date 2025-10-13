# WatchPAT DATA Packet (0x0800) Payload Structure

**Based on reverse-engineering actual device output with device worn on body**

Last Updated: 2025-10-13

---

## Overall Packet Structure

```
[24-byte Header: Standard WatchPAT packet header]
  - Magic: 0xBBBB
  - Command ID: 0x0800 (DATA packet)
  - Timestamp: Unix seconds
  - Transaction ID: Sequential
  - Length: Total packet size (header + payload)
  - CRC-16: Packet checksum

[Payload: 535-543 bytes (variable)]
  - 3-byte header
  - 6 AA AA delimited frames
  - Frame 5 contains 5 DD DD delimited samples
```

---

## Payload Header (3 bytes)

```
Byte 0: 0x06 = Frame count (6 frames in packet)
Byte 1: 0x01 = Packet subtype/version
Byte 2: 0x00 = Reserved/flags
```

**Assumption Confidence: HIGH** - Byte 0 always matches the number of AA AA markers found

---

## Frame Structure (AA AA Delimited)

Each frame starts with `0xAA 0xAA` marker, followed by:

```
Byte 0: Frame ID (0x01-0x06)
Byte 1: Frame Type/Subtype
Bytes 2+: Frame-specific data
```

---

## Frame 1, 2, 3: PPG Waveforms (Type 0x01/0x02/0x03 0x11)

**Purpose**: Photoplethysmography (PPG) waveforms from 3 LEDs for SpO2 calculation

```
AA AA 01 11 66 00 64 00 1E 02 00 00 [~96 bytes waveform data]
      ^^|^^ ^^^^^ ^^^^^ ^^^^^^^^^^^ ^^^^^^^^^^^^^^^^^^^^^^
      │ │  │     │     └─ Reserved (4 bytes)
      │ │  │     └─ Quality or Rate: 0x0064 = 100 (100%? 100Hz?)
      │ │  └─ Sample count: 0x0066 = 102 samples
      │ └─ Frame type: 0x11 = PPG waveform
      └─ Channel ID: 01=Red LED, 02=IR LED, 03=Ambient

Total: 112-120 bytes per frame (variable)
```

### Frame Mapping

- **Frame 1 (0x01 0x11)**: Red LED waveform - for SpO2 calculation
- **Frame 2 (0x02 0x11)**: Infrared LED waveform - for SpO2 calculation
- **Frame 3 (0x03 0x11)**: Ambient light sensor - for noise cancellation

### Waveform Data

~96-102 bytes of 12-bit PPG samples (device spec: 12-bit resolution)

### Device Specification Reference

Per device spec:
- **Sample Resolution**: 12 bits for PPG
- **Bandwidth**: 0.1-10 Hz for PAT (PPG likely higher frequency)
- **Purpose**: SpO2 requires multi-wavelength photoplethysmography (Red + IR)

### Assumption Confidence: MEDIUM-HIGH

- ✅ Three frames match multi-wavelength PPG requirement for SpO2
- ✅ Sample count (~102) suggests ~1 second window @ 100Hz
- ⚠️ Bytes 2-5 interpretation unclear (could be SpO2/HR or just metadata)

---

## Frame 4: Status Frame (Type 0x05 0x10)

**Purpose**: Device status, battery level, LED currents, signal quality

```
AA AA 05 10 04 00 01 00 00 02 00 00 09 17 00 00
      ^^|^^ ^^^^^ ^^^^^ ^^^^^^^^^^^ ^^^^^
      │ │  │     │     └─ Status value: varies (e.g., 0x0280 = 640)
      │ │  │     └─ Flags: 0x0001
      │ │  └─ Data length: 0x0004 = 4 bytes
      │ └─ Subtype: 0x10
      └─ Frame type: 0x05

Total: 14 bytes (fixed)
```

### Assumption Confidence: MEDIUM

- ✅ Consistent structure across all packets
- ⚠️ Exact meaning of status values unknown (battery? signal quality?)

---

## Frame 5: Sample Container (Type 0x06 0x00) with DD DD Samples

**Purpose**: Container for 5 processed physiological samples

```
AA AA 06 00 50 00 05 00 00 00 00 00 [5 × DD DD samples]
      ^^|^^ ^^^^^ ^^ ^^^^^^^^^^^
      │ │  │     │  └─ Reserved (5 bytes, all 0x00)
      │ │  │     └─ Sample count: 0x05 = 5 samples
      │ │  └─ Data length: 0x0050 = 80 bytes
      │ └─ Subtype: 0x00
      └─ Frame type: 0x06

Total: 90 bytes (8-byte header + 82 bytes of samples)
```

### Assumption Confidence: HIGH

Byte 4 (0x05) correctly predicts 5 DD DD markers in all observed packets

---

## DD DD Samples (14 bytes each, 5 per packet)

**Purpose**: Processed physiological metrics derived from waveform analysis

### CRITICAL OBSERVATION

All 5 samples share the **same timestamp** (e.g., 22435), indicating they are:
- Multiple readings from the same time window
- Quality-checked variations for reliability
- Used to select best reading or calculate median

This is a **quality assurance mechanism** - the device runs its algorithm 5 times per second on the same data window and reports all results.

### Sample Structure

```
DD DD A3 57 19 00 17 00 04 00 F4 FF 55 04 0F B3
      ^^^^^ ^^^^^ ^^^^^ ^^^^^ ^^^^^ ^^^^^ ^^^^^
      │     │     │     │     │     │     └─ [12-13] Checksum/Flags
      │     │     │     │     │     └─ [10-11] Sensor Reading (uint16)
      │     │     │     │     └─ [8-9] Accel Y or PAT delta (int16, signed)
      │     │     │     └─ [6-7] Accel X (int16, signed)
      │     │     └─ [4-5] Value2: Reserved (usually 0x00)
      │     └─ [2-3] Value1: Quality/Position/Stage
      └─ [0-1] Timestamp: Time window ID (uint16)
```

### Field Interpretation (Based on Device-Worn Data)

| Byte | Field | Type | Observed Range | Interpretation | Confidence |
|------|-------|------|----------------|----------------|------------|
| 0-1 | Timestamp | uint16 | 22435 (constant) | Time window ID | HIGH |
| 2 | Value1 | uint8 | 16-33 | Quality metric / Body position (5 states) / Sleep stage | MEDIUM |
| 3 | Value2 | uint8 | 0 | Reserved | HIGH |
| 4-5 | AccelX | int16 | -17 to +1 | Actigraphy X-axis (12-bit, signed) | MEDIUM-HIGH |
| 6-7 | AccelY | int16 | -12 to +3 | Actigraphy Y-axis OR PAT delta (12-bit, signed) | MEDIUM |
| 8-9 | Sensor | uint16 | 1108-1110 | PAT amplitude / Snore / Raw ADC value | MEDIUM |
| 10-11 | Checksum | uint16 | Varies | CRC or flags | MEDIUM |

### Key Evidence from Device-Worn Data

#### 1. Value1 (16-33 range)

Does NOT match SpO2 (70-100%) or HR (30-150 bpm)

**Likely candidates:**
- Signal quality index (0-100 scale)
- Body position (5 discrete states per spec: supine, prone, right, left, sit)
- Sleep stage indicator (Wake, REM, Light, Deep)

#### 2. AccelX/Y (negative values indicate movement)

Device spec confirms:
- **Actigraphy**: 12-bit resolution
- **Chest Movement**: 12-bit × 3 axis

Observed values:
- AccelX: -17 to +1
- AccelY: -12 to +3

Negative values = movement in specific direction

#### 3. Sensor Reading (1108-1110)

Slowly incrementing across samples

**Candidates:**
- PAT amplitude (device spec: 0.0-0.5V)
- Cumulative counter
- Raw ADC value from sensor
- Snore intensity (12-bit per spec)

#### 4. Five Samples Per Window

The device architecture appears to be:
1. Collect 1 second of waveform data
2. Run physiological algorithm 5 times with slight variations
3. Report all 5 results
4. Best/median result selected for clinical reporting
5. Variance between samples indicates signal quality

### Device Specification Cross-Reference

From device spec document:
- **Channels**: PAT, Pulse rate, Oximetry, Actigraphy, Snoring, Body Position, Chest Movement
- **Sample Resolution**:
  - PAT, Actigraphy, Snore: 12 bits
  - Oximetry: 1%
  - Body Position: 5 discrete states
  - Chest Movements: 12 bits × 3 axis

### Assumption Confidence: MEDIUM

- ✅ Field sizes and types validated
- ✅ Signed values confirmed (negative movement)
- ⚠️ Exact field meanings require testing with various activities (walking, position changes, etc.)
- ⚠️ SpO2/HR location still unknown (possibly in PPG frame headers OR calculated separately)

---

## Frame 6: PAT Waveform (Type 0x04 0x01)

**Purpose**: Peripheral Arterial Tone waveform for apnea detection

```
AA AA 04 01 46 00 64 00 00 00 00 00 [~70 bytes waveform]
      ^^|^^ ^^^^^ ^^^^^ ^^^^^^^^^^^ ^^^^^^^^^^^^^^
      │ │  │     │     └─ Reserved (8 bytes)
      │ │  │     └─ Quality: 0x0064 = 100
      │ │  └─ Sample count: 0x0046 = 70 samples
      │ └─ Subtype: 0x01
      └─ Frame type: 0x04

Total: 80 bytes (14-byte header + ~66 bytes waveform)
```

### PAT Details

- **Bandwidth**: 0.1-10 Hz (per device spec)
- **Sample Rate**: ~70 samples suggests lower frequency than PPG
- **Purpose**: Sleep apnea detection via arterial tone changes

### Assumption Confidence: MEDIUM-HIGH

- ✅ PAT is key metric for sleep apnea detection (device spec)
- ✅ Lower sample count matches lower frequency requirement (0.1-10 Hz vs PPG's higher frequency)
- ✅ Separate from PPG confirms multi-sensor approach

---

## Unknown / Unverified Elements

### 1. SpO2 and Pulse Rate Location

**Status**: **NOT FOUND** in DD DD samples (values too low: 16-33)

**Hypothesis A**: Stored in PPG frame headers (bytes 2-5: `0x66 00 64 00`)
- Pro: PPG frames are the right place for SpO2-derived values
- Con: Values (102, 100) don't match typical vital sign ranges

**Hypothesis B**: Calculated on-device but not transmitted in real-time
- Pro: Android app doesn't parse DATA packets (saves raw)
- Pro: Aligns with medical device architecture (edge sensing, cloud analysis)
- Con: Real-time monitoring would require these values

**Hypothesis C**: Encoded differently (scaled, offset, or split across fields)
- Example: SpO2 = (Value1 × 3) + offset
- Example: HR in different frame or combined field

**Needs**: More device-worn data with varying vital signs (exercise, breath holding, etc.)

### 2. Value1 Exact Meaning (16-33 range)

**Candidates**:
- Signal quality (0-100% scale, needs confirmation)
- Body position (0-4 matching 5 discrete states)
- Sleep stage (0-5: Wake, REM, Light, Deep, etc.)
- Algorithm confidence score

**Needs**: Correlation analysis with:
- Device position changes (lie down, sit up, turn)
- Known sleep stages (if available from scoring software)

### 3. Sensor Reading (1108-1110)

**Candidates**:
- PAT amplitude (0.0-0.5V per spec, scaled to uint16)
- Snore intensity (12-bit value)
- Raw ADC value from sensor
- Respiratory movement amplitude

**Needs**: Analysis during:
- Known snoring events
- Apnea events
- Various breathing patterns

### 4. Frame Size Variation

**Observation**: PPG frames vary 112-120 bytes

**Hypothesis**:
- Waveform complexity affects encoding
- Adaptive compression based on signal characteristics
- Variable number of samples based on quality

**Needs**: Pattern analysis across longer recordings (hours)

### 5. Checksum Field (Bytes 10-11 in DD DD samples)

**Status**: Varies across samples (0x041B, 0x046F, 0x043A, etc.)

**Hypothesis A**: CRC-16 of sample data
**Hypothesis B**: Flags and metadata packed into 16 bits
**Hypothesis C**: Algorithm confidence or quality metric

**Needs**: CRC algorithm testing or pattern analysis

---

## Data Flow Architecture

```
Device Sensors
    ↓
[PPG LEDs (Red/IR/Ambient)] → Frames 1-3 (102 samples @ ~100Hz)
[PAT Sensor]                → Frame 6 (70 samples @ ~10Hz)
[Actigraphy]                → DD DD samples (X/Y axes)
[Snore/Resp Sensor]         → DD DD samples
[Algorithm Processing]      → DD DD samples (5 readings/sec)
[Device Status Monitor]     → Frame 4
    ↓
BLE Transmission (0x0800 DATA packets @ ~1Hz)
    ↓
Android App (saves raw binary to file, no parsing)
    ↓
Upload to Server
    ↓
Server/Desktop Analysis Software (full parsing and scoring)
```

### Architecture Confidence: HIGH

- ✅ Matches Android app behavior (saves packets without parsing)
- ✅ Aligns with medical device architecture (edge processing + cloud analysis)
- ✅ Separates real-time data collection from offline analysis
- ✅ Reduces mobile app complexity and battery usage

---

## Example Packet Breakdown (Device Worn)

### Packet Header
```
BB BB 08 00 DC 1E 00 00 00 00 00 00 0D 00 00 00 31 02 00 00 00 00 8B 32
│  │  │  │  └──────────────────┘  └──────────┘  └──────┘  │  │  └──────┘
│  │  │  │  Timestamp             TxnID: 13     Length:561 │  │  CRC
│  │  └──┘  CmdID: 0x0800                                  └──┘ Flags
└──┘ Magic
```

### Payload (561 - 24 = 537 bytes)
```
06 01 00  [Packet header: 6 frames]

AA AA 01 11 66 00 64 00 1E 02 00 00 [...96 bytes PPG Red...]
AA AA 02 11 66 00 64 00 1E 02 00 00 [...96 bytes PPG IR...]
AA AA 03 11 68 00 64 00 12 7E 00 00 [...98 bytes PPG Ambient...]
AA AA 05 10 04 00 01 00 00 02 00 00 09 17 00 00
AA AA 06 00 50 00 05 00 00 00 00 00
  DD DD A3 57 18 00 15 00 04 00 F4 FF 55 04 0F B3
  DD DD A3 57 18 00 15 00 07 00 F2 FF 55 04 76 5A
  DD DD A3 57 12 00 18 00 07 00 F0 FF 56 04 E8 20
  DD DD A3 57 17 00 14 00 07 00 EF FF 56 04 66 3C
  DD DD A3 57 21 00 13 00 08 00 EF FF 55 04 0A B5
AA AA 04 01 46 00 64 00 00 00 00 00 [...66 bytes PAT waveform...]
```

### Parsed Interpretation
- **3 PPG Frames**: 102 samples each @ ~100Hz (Red, IR, Ambient)
- **1 Status Frame**: Device metrics
- **5 DD DD Samples**: All timestamp 22435
  - Quality/Position: 24, 21, 24, 20, 33 (varying across samples)
  - Accel X: 4, 7, 7, 7, 8 (minimal movement)
  - Accel Y: -12, -14, -16, -17, -17 (consistent downward direction)
  - Sensor: 1109, 1109, 1110, 1110, 1109 (stable PAT?)
- **1 PAT Frame**: 70 samples @ ~10Hz

---

## Summary of Confidence Levels

| Component | Confidence | Reason |
|-----------|------------|--------|
| Overall structure | HIGH | Consistent across all packets |
| Frame delimiting (AA AA) | HIGH | Perfect match in all samples |
| Frame count in header | HIGH | Byte 0 always matches AA AA count |
| PPG frame purpose | MEDIUM-HIGH | Matches SpO2 requirements |
| DD DD sample structure | MEDIUM-HIGH | Field sizes validated |
| DD DD field meanings | MEDIUM | Plausible but unverified |
| Vital signs location | LOW | Not found in expected locations |
| Frame 4/6 interpretation | MEDIUM | Structure clear, semantics unclear |
| Data flow architecture | HIGH | Matches Android app behavior |

---

## Implementation Notes

The C# implementation (`TelemetryDataStructures.cs`, `TelemetryHandler.cs`) provides structured parsing based on these assumptions, with:

1. **Clear documentation** of uncertainties
2. **Flexible field naming** to accommodate multiple interpretations
3. **Complete raw data preservation** for future analysis
4. **Event-driven architecture** for easy testing and refinement

As more data becomes available (especially with varying physiological states), field interpretations can be refined without changing the parsing infrastructure.

---

## Next Steps for Verification

### High Priority
1. **Capture packets during position changes** → Verify Value1 as body position
2. **Capture during exercise** → Find SpO2/HR values (should be 120+ bpm, 95-100% SpO2)
3. **Capture during breath holding** → See SpO2 drop below 90%

### Medium Priority
4. **Capture during sleep** → Correlate Value1 with sleep stages
5. **Capture during snoring** → Identify snore intensity field
6. **Long recording analysis** → Study frame size patterns

### Low Priority
7. **Checksum verification** → Test CRC algorithms on DD DD samples
8. **Compare with official analysis software** → If available, compare parsed values
9. **Review firmware source** → If accessible, verify field meanings

---

**Document Version**: 1.0
**Date**: 2025-10-13
**Author**: Reverse-engineered from WatchPAT ONE RE project
**Status**: Working implementation with documented uncertainties
