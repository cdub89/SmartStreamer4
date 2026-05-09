# Migration Guide

This guide covers upgrading between FlexLib versions.

## Table of Contents

- [Version 4.1.5 to 4.2.18](#version-415-to-4218)
- [Version 4.x to 4.1.5](#version-4x-to-415)
- [Version 3.x to 4.x](#version-3x-to-4x)

---

## Version 4.1.5 to 4.2.18

FlexLib 4.2.18 adds significant new subsystems, expands radio model support, and makes a small number of breaking API changes. Most 4.1.5 programs will compile with only one or two targeted changes.

### Required Firmware

The minimum compatible firmware version is **3.3.32.8203**. Update the radio firmware before connecting with a 4.2.18-built application.

---

### Breaking Changes

#### 1. HAAPI Fault Event Renamed

The fault event on the `HAAPI` class was renamed and its delegate signature changed.

| 4.1.5 | 4.2.18 |
|-------|--------|
| `event HAAPI.AmplifierFaultEventHandler AmplifierFault` | `event HAAPI.HaapiFaultEventHandler HaapiFault` |

**Old**:

```csharp
radio.HAAPI.AmplifierFault += OnAmplifierFault;

void OnAmplifierFault(string noun, string reason)
{
    Console.WriteLine($"Fault [{noun}]: {reason}");
}
```

**New**:

```csharp
radio.HAAPI.HaapiFault += OnHaapiFault;

void OnHaapiFault(string noun, string reason)
{
    Console.WriteLine($"Fault [{noun}]: {reason}");
}
```

The handler signature (`string noun, string reason`) is unchanged; only the event name and delegate type name changed.

---

#### 2. HAAPI.AmpIsSelected Removed

The `AmpIsSelected` property has been removed from `HAAPI` with no direct replacement. If your code read this property, remove the reference. Use `AmpMode` to track the amplifier's operating state instead.

```csharp
// Old — no longer compiles
if (radio.HAAPI.AmpIsSelected) { ... }

// Alternative — check operating mode
if (radio.HAAPI.AmpMode == AmplifierMode.OPERATE) { ... }
```

---

### Deprecated APIs

The following properties still compile but emit `[Obsolete]` warnings. Update before a future version removes them.

| Deprecated | Replacement |
|-----------|-------------|
| `Radio.InUseIP` | `Radio.GuiClientIPs` |
| `Radio.InUseHost` | `Radio.GuiClientHosts` |

```csharp
// Deprecated (still works, but warns)
string ip = radio.InUseIP;
string host = radio.InUseHost;

// Correct
string ip = radio.GuiClientIPs;
string host = radio.GuiClientHosts;
```

---

### New Features

#### 1. NAVTEX Waveform Support

NAVTEX (Narrow-band direct-printing) is now fully supported for FLEX-9x00 series radios. Access it via `radio.NAVTEX`.

```csharp
// Toggle NAVTEX mode on/off
radio.NAVTEX.TryToggleNAVTEX();

// Or specify the broadcast frequency (default is international 518 kHz)
radio.NAVTEX.TryToggleNAVTEX(NAVTEX.LOCAL_BROADCAST_FREQ_HZ); // 490 kHz

// Monitor status
radio.NAVTEX.PropertyChanged += (s, e) =>
{
    if (e.PropertyName == nameof(NAVTEX.Status))
        Console.WriteLine($"NAVTEX status: {radio.NAVTEX.Status}");
};

// Send a NAVTEX message
var msg = new NAVTEXMsg(
    dateTime: null,
    idx: null,
    serial: null,
    txIdent: 'A',
    subjInd: 'B',
    msgStr: "NAVTEX TEST MESSAGE",
    status: NAVTEXMsgStatus.Pending
);
radio.NAVTEX.Send(msg);
```

**NAVTEXStatus values**: `Inactive`, `Active`, `Transmitting`, `QueueFull`, `Unlicensed`, `Error`

**NAVTEXMsgStatus values**: `Pending`, `Queued`, `Sent`, `Error`

**Broadcast frequency constants**:

```csharp
NAVTEX.INTERNATIONAL_BROADCAST_FREQ_HZ           // 518,000 Hz
NAVTEX.LOCAL_BROADCAST_FREQ_HZ                   // 490,000 Hz
NAVTEX.MARINE_SAFETY_INFORMATION_BROADCAST_FREQ_HZ // 4,209,500 Hz
```

---

#### 2. HAAPI — New Warning Events and Mode Control

In addition to the renamed fault event, `HAAPI` now exposes warning events and an explicit mode-change method.

**New events**:

```csharp
// Warning raised (state becomes WARNING)
radio.HAAPI.HaapiWarning += (noun, reason) =>
{
    Console.WriteLine($"Warning [{noun}]: {reason}");
};

// Warning cleared (state returns to OK)
radio.HAAPI.HaapiWarningCleared += (noun) =>
{
    Console.WriteLine($"Warning cleared: {noun}");
};
```

**New method — change amplifier mode**:

```csharp
// Request mode change (result confirmed via AmpMode PropertyChanged)
radio.HAAPI.HaapiChangeMode(AmplifierMode.OPERATE);
radio.HAAPI.HaapiChangeMode(AmplifierMode.STANDBY);
```

**New metering events** (subscribe to live data from the Overlord PA):

```csharp
radio.HAAPI.HaapiFwdPwrDataReady  += (data) => { /* forward power, watts */ };
radio.HAAPI.HaapiVswrDataReady    += (data) => { /* SWR */ };
radio.HAAPI.HaapiHVDataReady      += (data) => { /* HV supply voltage */ };
radio.HAAPI.HaapiCurrentDataReady += (data) => { /* HV supply current */ };
radio.HAAPI.HaapiTempPsuDataReady += (data) => { /* PSU temperature */ };
radio.HAAPI.HaapiTempPa0DataReady += (data) => { /* PA module 0 temperature */ };
radio.HAAPI.HaapiTempPa1DataReady += (data) => { /* PA module 1 temperature */ };
radio.HAAPI.HaapiTempDrvADataReady+= (data) => { /* Driver A temperature */ };
radio.HAAPI.HaapiTempCombDataReady+= (data) => { /* Combiner temperature */ };
radio.HAAPI.HaapiTempHpfDataReady += (data) => { /* HPF load temperature */ };
```

---

#### 3. New Radio Model Support

The following models are now recognized by `ModelInfo.GetModelInfoForModel()`. Previously they fell through to the `DEFAULT` entry.

| Model | Platform | Notes |
|-------|----------|-------|
| `FLEX-8400` / `FLEX-8400M` | BigBend | 2-slice, RapidM dev-only |
| `FLEX-8600` / `FLEX-8600M` | BigBend | 4-slice, diversity, RapidM dev-only |
| `ML-9600W` / `ML-9600X` / `ML-9600` | BigBend | Full RapidM modem support |
| `MLS-9601` | BigBend | Full RapidM modem support |
| `CL-9300` / `CLS-9301` | BigBend | RapidM dev-only |
| `RT-2122` | DragonFire | New platform; RapidM supported |
| `AU-510` / `AU-510M` | BigBend | Overlord PA (`HasOverlordPa = true`) |
| `AU-520` / `AU-520M` | BigBend | Overlord PA, 4-slice, diversity |

If your code switches on `ModelInfo.Platform`, add a case for the new `RadioPlatform.DragonFire` value:

```csharp
var info = ModelInfo.GetModelInfoForModel(radio.Model);
switch (info.Platform)
{
    case RadioPlatform.Microburst: break;
    case RadioPlatform.DeepEddy:   break;
    case RadioPlatform.BigBend:    break;
    case RadioPlatform.DragonFire: break; // NEW in 4.2.18
}
```

---

#### 4. New Radio Properties

| Property | Type | Description |
|----------|------|-------------|
| `IsBigBend` | `bool` | `true` when the radio's platform is `RadioPlatform.BigBend` |
| `IsOverlord` | `bool` | `true` when the radio has an Overlord PA (`AU-510/520` family) |
| `IsSystemModel` | `bool` | Identifies system/government model variants |
| `TurfRegion` | `string` | Turf/region string from the radio |
| `ExternalPortLink` | `bool` | External port link status |
| `IsGnssPresent` | `bool` | GNSS receiver is present on the radio |
| `ProfileAutoSave` | `bool` | Read/write; enables automatic profile saving on the radio |

```csharp
// Example: detect Overlord PA and use HAAPI
if (radio.IsOverlord)
{
    radio.HAAPI.HaapiFault += OnHaapiFault;
    radio.HAAPI.HaapiFwdPwrDataReady += (pwr) =>
        Console.WriteLine($"Forward power: {pwr} W");
}

// Enable auto profile save
radio.ProfileAutoSave = true;

// Check GNSS
if (radio.IsGnssPresent)
    Console.WriteLine("GNSS is available on this radio");
```

---

#### 5. TlsCommandCommunication

A new `ICommandCommunication` implementation (`TlsCommandCommunication`) provides TLS-encrypted command transport for WAN connections. This is used internally by the library when connecting via SmartLink; most applications do not need to instantiate it directly.

---

---

### DAX Audio API Changes

The DAX internals were significantly rewritten in 4.2.18. The public surface is mostly backward-compatible, but there are behavioral changes and one new method that multiFLEX applications must adopt.

#### DAXTXAudioStream — New: RequestTX for multiFLEX ownership

A new `RequestTX(bool tx)` method lets a client explicitly claim or yield DAX TX ownership when another client currently holds it. This is required in multiFLEX scenarios. In 4.1.5, there was no programmatic way to take back TX; you had to rely on the radio's automatic arbitration.

```csharp
// Request TX ownership (e.g. your client wants to transmit)
txStream.RequestTX(true);

// Yield TX ownership (e.g. allow another client to transmit)
txStream.RequestTX(false);

// Watch Transmit property to see if the radio granted ownership
txStream.PropertyChanged += (s, e) =>
{
    if (e.PropertyName == "Transmit")
        Console.WriteLine($"TX state: {txStream.Transmit}");
};
```

The `Transmit` property still reflects the radio's actual grant; setting `RequestTX` does not immediately set `Transmit`.

---

#### DAXTXAudioStream — AddTXData performance rewrite

The `AddTXData(float[] tx_data_stereo, bool sendReducedBW = false)` signature is unchanged, but the implementation was completely rewritten with pre-allocated packet buffers. There are no per-call heap allocations in 4.2.18.

**Action required**: None — no code changes needed. Applications that call `AddTXData` at high rates will see lower GC pressure and reduced latency jitter.

Packet size is still fixed at 128 stereo float pairs (256 floats total). Passing a buffer of any other length will be ignored with a debug warning, same as before.

---

#### DAXMICAudioStream — Microphone gain curve changed

The `RXGain` property range (0–100) is unchanged, but the underlying dB mapping changed in 4.2.18:

| Version | Gain range | Midpoint (50) |
|---------|-----------|---------------|
| 4.1.5 | maps to some internal range | varies |
| 4.2.18 | maps −10 dB to +10 dB | 0 dB (unity) |

The new curve is symmetric around unity gain at the midpoint (50). If your application set `RXGain` to a specific value and tuned the level by ear, you may need to readjust. A value of 50 now delivers exactly 0 dB (unity gain, no amplification or attenuation).

---

#### DAXRXAudioStream — Slice binding race condition fixed

In 4.1.5, there was a race condition where the `Slice` property could be resolved before `_clientHandle` was set, causing the slice lookup to fail and leaving `Slice` null. In 4.2.18, slice resolution is deferred until all status keys in a single update have been parsed.

**Action required**: None — this is a bug fix. Code that worked around a null `Slice` by checking repeatedly or subscribing to `PropertyChanged` should continue to work, and will now also see `Slice` set correctly on the first status update.

---

#### Stream request API — Unchanged

The stream creation and teardown methods on `Radio` are unchanged:

```csharp
// Create streams (same as 4.1.5)
radio.RequestDAXRXAudioStream(int channel);
radio.RequestDAXTXAudioStream();
radio.RequestDAXMICAudioStream();
radio.RequestDAXIQStream(int channel);
radio.RequestRXRemoteAudioStream();
radio.RequestRXRemoteAudioStream(bool isCompressed);
radio.RequestRemoteAudioTXStream();

// Remove streams (same as 4.1.5)
radio.RemoveAudioStream(uint stream_id);
radio.RemoveDAXTXAudioStream(uint stream_id);
radio.RemoveDAXMICAudioStream(uint stream_id);
radio.RemoveDAXIQStream(uint stream_id);
radio.RemoveRXRemoteAudioStream(uint stream_id);
radio.RemoveTXRemoteAudioStream(uint stream_id);

// Events (same as 4.1.5)
radio.DAXRXAudioStreamAdded   += ...;
radio.DAXRXAudioStreamRemoved += ...;
radio.DAXTXAudioStreamAdded   += ...;
radio.DAXTXAudioStreamRemoved += ...;
radio.DAXMICAudioStreamAdded  += ...;
radio.DAXMICAudioStreamRemoved+= ...;
radio.DAXIQStreamAdded        += ...;
radio.DAXIQStreamRemoved      += ...;
```

---

### Migration Checklist (4.1.5 → 4.2.18)

- [ ] Rename `HAAPI.AmplifierFault` subscriptions to `HAAPI.HaapiFault`
- [ ] Rename `HAAPI.AmplifierFaultEventHandler` to `HAAPI.HaapiFaultEventHandler`
- [ ] Remove any references to `HAAPI.AmpIsSelected`
- [ ] Replace `Radio.InUseIP` with `Radio.GuiClientIPs`
- [ ] Replace `Radio.InUseHost` with `Radio.GuiClientHosts`
- [ ] Add `RadioPlatform.DragonFire` case to any platform switch statements
- [ ] Update firmware on all target radios to 3.3.32.8203 or later
- [ ] **DAX**: Add `RequestTX(bool tx)` calls where multiFLEX TX ownership handoff is needed
- [ ] **DAX**: Verify `DAXMICAudioStream.RXGain` values still produce expected audio levels (gain curve changed)
- [ ] **DAX**: Remove any workarounds for null `DAXRXAudioStream.Slice` on first status update

---

## Version 4.x to 4.1.5

### Summary

Primarily a maintenance release: .NET 8.0 multi-targeting, dependency updates, no breaking API changes.

### Target Framework Changes

**Previous**:

- .NET Framework 4.6.2

**Current**:

- .NET Framework 4.6.2
- .NET 8.0 (new)

### Multi-Targeting Support

```xml
<!-- Recommended: target .NET 8.0 -->
<PropertyGroup>
  <TargetFramework>net8.0-windows</TargetFramework>
</PropertyGroup>

<!-- Or continue using .NET Framework 4.6.2 -->
<PropertyGroup>
  <TargetFramework>net462</TargetFramework>
</PropertyGroup>
```

### Dependency Updates

| Package | Old Version | New Version |
|---------|-------------|-------------|
| AsyncAwaitBestPractices | 7.x | 9.0.0 |
| System.Collections.Immutable | 6.x | 9.0.0 |
| Newtonsoft.Json | 13.0.1 | 13.0.3 |

```bash
dotnet add package AsyncAwaitBestPractices --version 9.0.0
dotnet add package System.Collections.Immutable --version 9.0.0
```

---

## Version 3.x to 4.x

This was a major version update with significant breaking changes.

### v3 to v4 Breaking Changes

#### 1. Namespace Changes

```csharp
// Old
using FlexLib;

// New
using Flex.Smoothlake.FlexLib;
```

#### 2. Radio Discovery

```csharp
// Old (v3.x)
Discovery.Start();
Discovery.RadioDiscovered += OnRadioDiscovered;

// New (v4.x)
API.RadioAdded += (radio) => { /* handle radio */ };
API.Init();
```

#### 3. Connection

```csharp
// Old (v3.x)
radio.Connect("192.168.1.100");

// New (v4.x)
radio.Connect(); // IP comes from discovery
```

#### 4. Slice Creation

```csharp
// Old (v3.x)
Slice slice = radio.CreateSlice();

// New (v4.x)
radio.SliceAdded += (slice) => { slice.Freq = 14.200; };
radio.RequestSlice();
```

#### 5. Audio Streams

```csharp
// Old (v3.x)
AudioStream stream = new AudioStream(radio, 1);
stream.DataAvailable += OnAudioData;

// New (v4.x)
radio.DAXRXAudioStreamAdded += (audioStream) =>
{
    audioStream.DataReady += OnAudioData;
};
radio.RequestDAXRXAudioStream(1);
```

#### 6. Property Renames

| Old (v3.x) | New (v4.x) |
|-----------|-----------|
| `Radio.IsConnected` | `Radio.Connected` |
| `Slice.Frequency` | `Slice.Freq` |
| `Slice.RxAntenna` | `Slice.RXAnt` |
| `Slice.TxAntenna` | `Slice.TXAnt` |

### Complete Before/After Example

**Before (v3.x)**:

```csharp
using FlexLib;

class Program
{
    static void Main()
    {
        Discovery.Start();
        Discovery.RadioDiscovered += (radio) =>
        {
            radio.Connect("192.168.1.100");

            if (radio.IsConnected)
            {
                Slice slice = radio.CreateSlice();
                slice.Frequency = 14.200;
            }
        };
    }
}
```

**After (v4.x)**:

```csharp
using Flex.Smoothlake.FlexLib;

class Program
{
    static async Task Main()
    {
        API.ProgramName = "MyApp";
        API.RadioAdded += async (radio) =>
        {
            radio.Connect();

            for (int i = 0; i < 50 && !radio.Connected; i++)
                await Task.Delay(100);

            if (radio.Connected)
            {
                radio.SliceAdded += (slice) => { slice.Freq = 14.200; };
                radio.RequestSlice();
            }
        };
        API.Init();

        await Task.Delay(-1);
    }
}
```

### Removed APIs (v3.x → v4.x)

| Removed | Use Instead |
|---------|------------|
| `Radio.CreateSlice()` | `radio.RequestSlice()` |
| `Discovery.Start()` | `API.Init()` |
| `Discovery.Stop()` | `API.CloseSession()` |

---

## Common Migration Issues

### "Type or namespace 'FlexLib' could not be found"

Update using statements: `using Flex.Smoothlake.FlexLib;`

### Radio connection fails immediately

Remove the IP address argument: `radio.Connect();`

### Slice is null after creation

Subscribe to `radio.SliceAdded` before calling `radio.RequestSlice()`.

### Events not firing

Subscribe to events **before** calling `API.Init()` so you do not miss radios discovered immediately on startup.

### UI freezing with events

Events fire on background threads. Dispatch UI updates:

```csharp
radio.PropertyChanged += (s, e) =>
    Dispatcher.Invoke(() => { /* update UI */ });
```

---

## Version History

| Version | Notable Changes |
|---------|----------------|
| 4.2.18 | NAVTEX support, HAAPI warning events, new radio models (FLEX-8x00, ML-9600, AU-510/520), DragonFire platform |
| 4.1.5 | .NET 8.0 multi-targeting, dependency updates |
| 4.1.0 | Bug fixes, performance improvements |
| 4.0.0 | Major refactor, new API design |
| 3.x | Original FlexLib implementation |

---

**Questions?** See [Getting Started](Getting-Started.md) or contact <support@flexradio.com>.
