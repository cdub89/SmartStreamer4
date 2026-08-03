# PLAN: Issue #59 — SmartDeck radio control surface

## Context

Issue #59 asks for a control surface inside SmartStreamer4 that replaces three
third-party accessory apps: Stream Deck (physical), FlexButtons (soft buttons),
and FRStack (keyboard shortcuts). Working name: **SmartDeck**.

The driver is not consolidation or theming. It is supply-chain risk. Accessory
apps break across FlexRadio server upgrades and the operator's only recourse is
to wait on someone else's schedule; the SliceMaster upgrade gap (a year, never
fully resolved) is why SmartStreamer4 exists at all. Adding a button or a
keyboard shortcut today means filing a request with a stranger. Owning the
control path converts an unbounded, externally-scheduled cost into a bounded one
we schedule ourselves.

The honest counterweight: the same exposure exists one layer down through
FlexLib. The repo already lived the 4.1.5 -> 4.2.18 migration. Today the radio
write surface is five verbs; this issue multiplies it. The mitigation is the
abstraction already in the codebase — `IRadioConnection` is deliberately
FlexLib-type-free, so every control verb goes through it and the next migration
stays contained to `FlexLibRadioConnection.cs`. That also makes the control
logic unit-testable against a fake, which matters because the live-radio smoke
matrix is the thing that grows fastest here.

Scope limit to state plainly in the issue and in user-facing text: SmartStreamer
connects as a **non-GUI client** (`FlexLibRadioDiscovery.cs:39`,
`API.IsGUI = false`), and slices and panadapters exist only while SmartSDR or
Maestro is running. SmartDeck replaces the accessory layer. It does not replace
SmartSDR.

Targeted at **v0.3.0** (feature landing, MINOR bump). The issue title says
v0.2.1, which shipped 2026-07-24; it should be retitled.

## Ground truth in the code today

- `IRadioConnection` is read-mostly: connect, enumerate panadapters / slices /
  DAX-IQ, request and stop IQ streams, `SetSliceFrequencyAsync`,
  `PublishSpotAsync`. One slice write verb, one spot write verb.
- **No meter code exists anywhere.** Telemetry is entirely new plumbing.
- **No keyboard-shortcut or key-binding infrastructure exists.**
- `SliceInfo` has letter, mode, freq, RIT, tune step, panadapter stream id,
  station, DAX channel. No antenna, gain, or active-slice concept.
- `SelectedControlStation` (`MainWindowViewModel.cs:294`) already answers "which
  station do we act on", already drives the CW and Digital tabs, and already
  carries presence-tracking logic from issues #28 and #45.
- `HamBands.cs` carries the full band plan in Hz, already used by the Digital
  rows and `SliceViewModel`.

## Decisions (locked)

### Delivery vehicle: a window in this process

Not a tab, and not a standalone app.

A tab is functionally wrong: SmartStreamer4 is configured and then minimized,
while a control surface has to stay on screen. Buried as tab eight of eight it
is never visible when it is wanted.

A standalone app was raised in the issue comment as "cleaner". It is cleaner
conceptually and worse in practice for a solo maintainer: two release pipelines,
two update checks, two settings stores, two client sessions on the radio, and
`SmartSDRIQStreamer.FlexRadio` promoted from a project reference to a versioned
library shared across repos. It also recreates the fragmentation the issue is
trying to remove. The asymmetry decides it: splitting a window into its own app
later is straightforward; merging two apps back is not.

SmartDeck launches from the Launch tab, remembers size and position, and offers
always-on-top.

**Available in both CW and Digital app modes.** Band, antenna, RF gain, and
telemetry are mode-agnostic. Only the mode row carries a CW/phone bias (WSJT-X
owns mode selection for digital operating), and that is not reason enough to
gate the whole window behind `IsCwMode`.

### Keyboard shortcuts are in-app, not system-wide

The issue said "hot keys"; the requirement is keyboard control of radio settings
without reaching for the mouse. In-app scope removes the largest cost in the
issue: no Win32 `RegisterHotKey`, no cross-app conflict detection, no rebinding
UI.

What replaces it is a constraint rather than a subsystem, and it applies from
phase 1: SmartDeck is designed keyboard-first. Focus order, visible focus
indicators, arrow keys to traverse the grid, Enter/Space to activate, and
Avalonia `KeyBinding` accelerators for the frequent controls. Cheap to design
in, painful to retrofit, so it is not deferred to a later phase.

### Phasing

1. **Telemetry footer.** Read-only, no write path, no TX risk. Proves out meter
   subscription, off-thread marshaling, and throttling before anything can key
   the radio.
2. **Non-TX controls, fixed layout.** Antenna, RF gain, band, mode.
3. **TX, CWX, power presets.** Guarded, after 1 and 2 have run against a real
   radio.

Diversity is deferred, not cut. `ModelInfo.IsDiversityAllowed` is per model, so
when it returns the control can be hidden on hardware that cannot do it rather
than failing silently.

### Control shapes, after collapsing the issue's list

The issue's inventory collapses substantially once the API is taken into
account. Roughly seventeen controls, not thirty.

- **RF gain: one up/down pair with a readout**, not eleven named levels and not
  a separate "antenna gain" control. `Panadapter.RFGain` is a panadapter
  property, and `RFGainLow` / `RFGainHigh` / `RFGainStep` / `RFGainMarkers`
  (`Panadapter.cs:198-246`) are reported by the radio via `GetRFGainInfo()`
  (`Panadapter.cs:39-65`, command `display pan rfgain_info 0x<streamid>`). No
  per-model table to maintain, and correct on any model. The write target is the
  panadapter behind the active slice; `SliceInfo.PanadapterStreamId` already
  carries that hop.
- **Antenna: one selector**, not three button groups. `Slice.RXAnt` takes
  `ANT1, ANT2, XVTR, RX_A, RX_B`, so "Antenna Inputs, RxA, RxB" from the issue
  is a single control over radio-reported values. `Slice.TXAnt` takes
  `ANT1, ANT2, XVTR`.
- **Band: ten buttons (160m-10m) with per-band frequency memory.** Band change
  is not an API verb; it is `Slice.Freq`, so this is app-side logic reusing
  `HamBands.cs` and adds no new FlexLib exposure. Mechanic: pressing a band
  button stores the current slice frequency under the band being left, then
  tunes to the stored frequency for the band being entered, falling back to a
  sensible default the first time. No continuous frequency tracking needed.
- **Mode: four buttons** (CW, USB, LSB, AM), per the issue. DIGU/DIGL was
  considered and dropped: WSJT-X owns mode selection for digital operating. One
  line each to add later if wanted. `Slice.Mode` is a string discriminator, so
  per CLAUDE.md it gets a typed wrapper rather than bare literals at call sites.

### Telemetry values, and why we never touch meter names

**Subscribe to the `Radio`-level events, not to `Meter.DataReady` by name.**
FlexLib already does the name matching internally and re-publishes each meter as
a typed event:

| Footer item | Radio event | Radio.cs | Underlying meter name | Units |
|-------------|-------------|----------|-----------------------|-------|
| Voltage | `VoltsDataReady` | 7107 | `+13.8A` (pre-fuse rail) | Volts |
| Temp | `PATempDataReady` | 7100 | `PATEMP` | degrees C |
| Power out | `ForwardPowerDataReady` | 7066 | `FWDPWR` | **dBm** (converted, see below) |
| SWR | `SWRDataReady` | 7088 | `SWR` | ratio |

### Display units

The operator-facing units, locked 2026-08-02:

| Footer item | Displayed as | Wire units | Conversion |
|-------------|--------------|------------|------------|
| Power out | watts, 1-100 W range | dBm | `watts = 10^((dBm - 30) / 10)` |
| SWR | ratio to 1 decimal (1.1, 1.5, 2.1) | ratio | none |
| Temp | degrees C | degrees C | none |
| Voltage | volts | volts | none |

Only power needs converting. FlexLib documents `ForwardPowerDataReady` as dBm
at `Radio.cs:7063-7066`, and the gating spike confirmed it: 49.69 dBm on a
100 W radio, which is 93.1 W. An earlier draft of the table above wrongly
listed this event as delivering watts.

The conversion is a real element with logic, not formatting, so it gets its own
unit test (see Test decisions).

The delegate is `MeterDataReadyEventHandler(float data)`. Name matching happens
in `Radio.AddMeter` (`Radio.cs:6949-6968`).

This matters more than it looks. The v4.1.5 reference doc names three of these
four meters wrongly (`VOLTAGE`, `TEMP`, `FWD` against the real `+13.8A`,
`PATEMP`, `FWDPWR`), and voltage in particular is not guessable: it is the
pre-fuse supply rail, named `+13.8A`, and FlexLib wires no other voltage meter.
Subscribing to `Radio.VoltsDataReady` makes that naming FlexLib's problem rather
than ours, so meter-name drift across FlexLib versions cannot reach our code.
The names above are recorded for diagnosis only; nothing in SmartStreamer should
string-match them.

**Power and SWR read zero on receive, and that is displayed as zero.** Zero is a
real measurement and a useful passive signal that the radio is not transmitting.
A held last-TX value was considered and rejected: it lies about the radio's
current state, which is worse than useless on a surface glanced at mid-QSO.

This sits with the nullable-over-sentinel rule rather than against it, because
the absent case is different from the zero case:

- `null` = no meter data yet, or the meter is not present on this radio.
  Display dashes.
- `0` = the radio reported zero. Display zero. This is the TX-idle indicator.

So the footer distinguishes "not transmitting" from "not connected to
telemetry".

### Telemetry lives in the SmartDeck window, not the main app

The footer belongs to the SmartDeck window's own status bar. Nothing is added
to the main SmartStreamer window: no new footer row, no status-bar fields, no
change to `FooterStatusBuffer` or `ThrottledStatusEmitter` output. The only
main-window change in phase 1 is the Launch tab button that opens SmartDeck.

Stated explicitly because issue #59 says "show in the tab footer", written when
the surface was still expected to be a tab. Once it became a window (see
Delivery vehicle above), the telemetry has to travel with it: the main window
is minimized during operating, which is exactly when these values matter.

This also makes the subscription lifetime below trivially correct. Telemetry is
scoped to a window that is either open or not, with no path by which meter data
outlives the surface displaying it.

### Meter subscription lifetime

Subscribe while the SmartDeck window is open; unsubscribe when it closes. The
app's day job is streaming IQ and it spends most of its life minimized.
Continuous meter traffic buys nothing when nobody is looking at the footer.

### No user-facing layout editor in phases 1-2

Owning the button set (a commit) and letting end users rearrange it (a settings
schema, a layout editor, persistence, migrations, maintained forever) are two
different features. The issue's motivation is served entirely by the first. Ship
a fixed layout and let real use show which buttons nobody presses; per-button
show/hide is cheap to add later once the set is stable. Naming the accretion
pressure explicitly: four modes became six in discussion and went back to four.

### FlexLib version

Build against **4.2.18.41174**, the version the repo project-references today.
The 4.2.20.41343 drop is a separate change with its own smoke test. Bundling a
library upgrade into a new subsystem means a misbehaving radio gives no signal
about which one caused it.

## Phase 1 scope

The deliverable is a SmartDeck window containing only the telemetry footer, plus
the plumbing beneath it.

### Surfaces

| Surface | Change |
|---------|--------|
| `src/SmartSDRIQStreamer.FlexRadio/RadioDetails.cs` | New `RadioTelemetryInfo` record: four `double?` values plus a timestamp. No FlexLib types. |
| `src/SmartSDRIQStreamer.FlexRadio/IRadioConnection.cs` | `StartTelemetryAsync()` / `StopTelemetry()`, `RadioTelemetryInfo Telemetry { get; }`, `event Action<RadioTelemetryInfo> TelemetryChanged`. |
| `src/SmartSDRIQStreamer.FlexRadio/FlexLibRadioConnection.cs` | Subscribe the four `Radio`-level meter events, coalesce, raise `TelemetryChanged`. Unsubscribe on stop and on disconnect. |
| `SmartDeckWindow.axaml` / `.axaml.cs` | Window shell, keyboard-first, footer only. Size/position restore, always-on-top toggle. |
| `SmartDeckViewModel.cs` | Formatting, dash-vs-zero display, subscription lifetime tied to window open/close. |
| `MainWindow.axaml` (Launch tab) | Button to open SmartDeck. |
| `AppSettings.cs` | Window position, size, always-on-top. |

### Threading and rate

The meter events fire off the UI thread, once per meter per update. Four meters
means four independent event streams that must be coalesced into one
`RadioTelemetryInfo` and marshaled to the UI thread. Display updates get
throttled following the `ThrottledStatusEmitter` precedent rather than a second
throttle mechanism.

The gating spike measured the real rates, and they are not uniform: forward
power and SWR arrive at **~13.4 Hz**, PA temperature and volts at **~0.4 Hz**
(see the spike result below). Together that is roughly 28 events/sec.

**Throttle interval: 250 ms (4 Hz).** It cuts UI marshals from ~28/sec to
4/sec, sits well clear of the 0.4 Hz slow pair so temperature and volts never
look stalled, and reads smoothly for a numeric display.

**Forward power and SWR coalesce as peak-hold between emits; temperature and
volts as last-sample.** A 250 ms window holds ~3.4 samples of a value that
swings hard under CW keying, so last-sample would show whichever sample landed
on the emit boundary and the reading would jitter. Peak-hold is one `Math.Max`
in the coalescer and matches how an analog power meter behaves. The slow pair
has at most one sample per window, so the distinction does not arise there.

Note that FlexLib exposes these as *events*, not properties. There is no
`Radio.Volts` or `Radio.SWR` to poll, so the value we hold is whatever the last
event delivered, which is exactly what `RadioTelemetryInfo` is for: before the
first event for a given meter, that value is `null` and the footer shows dashes.

The spike confirmed this absent-vs-zero design is load-bearing rather than
theoretical: on receive, forward power reports a real `0.00` dBm and SWR reports
its `1.0` floor, at the full 13.4 Hz. Zero therefore cannot be overloaded to
mean "no reading yet".

The "power and SWR read zero on receive" decision above holds for power (a real
`0.00` dBm, which converts to 0.0 W and reads correctly as TX-idle) but **not**
for SWR. The SWR meter's floor is `1.0`, not zero, so at rest it reports
`1.00`, which is indistinguishable from a real 1:1 match while transmitting.
The phase-1 live test confirmed this reads wrong on the footer, and the
operator called it: at rest SWR shows dashes.

**SWR displays only while forward power is above 0.01 W.** Gated on power
rather than on `Radio.Mox` because power is already in the snapshot and "no RF
going out" is the condition that actually makes SWR meaningless; MOX would need
new plumbing to reach the same answer. The threshold has an order of magnitude
of clearance on both sides: the receive floor is 0.001 W and the lowest real
transmit power is 1 W. Dashes rather than `0` because SWR is undefined below
1.0, so a displayed `0` would be a value that cannot physically occur.

This narrows the "dashes mean no telemetry" rule: dashes on **all four** values
means no telemetry, while dashes on SWR alone, alongside live power, temp, and
volts, means not transmitting.

### Test decisions

Per the Test Coverage Discipline table. Every phase-1 element is routed:

| Element | Decision |
|---------|----------|
| `RadioTelemetryInfo` record | **Extend** existing record tests: absent-vs-zero construction, and that `null` and `0` stay distinguishable through the type. |
| Meter coalescing (four streams to one snapshot) | **New** unit test against a fake meter source. Pure logic, no FlexLib. |
| dBm to watts conversion for power out | **New** unit test: 49.69 dBm is 93.1 W (the spike's own reading), 50 dBm is 100 W, 0 dBm is 0.001 W. Pure math, no FlexLib. |
| Telemetry throttle behavior | **New** test alongside the existing `ThrottledStatusEmitter` tests. |
| Footer display formatting (dash vs zero) | **New** ViewModel test: `null` renders dashes, `0` renders `0`. |
| `FlexLibRadioConnection` meter subscribe/unsubscribe | **Skip** with reason: FlexLib-facing, not unit-testable; covered by the live-radio smoke gate. |
| Window shell, keyboard navigation, always-on-top | **Skip** with reason: UI-only wiring; covered by the live-radio smoke gate. |
| `AppSettings` window position fields | **Extend** the existing load/save round-trip test. |

Tests land in `tests/SmartSDRIQStreamer.App.Tests/` for ViewModel and settings
work, and alongside the FlexRadio module for the telemetry record.

## Gating spike: RESULT (passed 2026-08-02)

The question was whether a non-GUI client receives meter data at all, given
that SmartStreamer sets `API.IsGUI = false`. If meters were withheld from
non-GUI clients, or required binding to a GUI client first, phase 1 changed
shape entirely.

**It does.** Captured on a FLEX-6400M running SmartSDR-MB 4.2.20, FlexLib
client 4.2.20, over a 15 s window with TX power ramped up and down. All four
radio-level events fired with `API.IsGUI = False` and no GUI binding step,
which matches the source: `sub meter all` is sent unconditionally at
`Radio.cs:2273`, outside the `if (API.IsGUI)` branch.

| Meter | Rate | Observed over the window |
|-------|------|--------------------------|
| ForwardPower | 13.4 Hz | 0.00 to 49.69 dBm (about 93 W) |
| SWR | 13.4 Hz | 1.00 to 1.48 |
| PATemp | 0.4 Hz | 31.17 to 35.52 C |
| Volts | 0.4 Hz | 14.09 to 13.64 V |

Radio-side meter names all present and mapped: `FWDPWR` (idx 7, range
0.0-53.0), `SWR` (idx 9, range 1.0-999.0), `PATEMP` (idx 10, range 0.0-120.0),
`+13.8A` (idx 4, range 10.5-15.0, "Main radio input voltage at PA"). `+13.8B`
("at CPU") exists but drives no radio-level event; `+13.8A` is the rail we
want, since it sags 14.09 V to 13.64 V under key-down, which is the reading an
operator cares about.

Full capture recorded as a comment on issue #59. The temporary diagnostic that
produced it (`FlexLibRadioConnection.TelemetrySpike.cs`, the
`CaptureTelemetrySpike` command, the Logs-tab button) is deleted once the 4.1.5
capture below is also taken.

## Open items requiring the Windows seat

1. ~~The gating spike.~~ **Done 2026-08-02, passed.** See the result above.
2. Meter names confirmed against the **SmartSDR server** versions in the field,
   not against a client library version. **4.2.x done** (4.2.20 server, above).
   **4.1.5 outstanding.** The radio reports these names, so only a live capture
   confirms them, and a mismatch is silent: FlexLib never wires the event and
   the value stays absent forever.
3. ~~Actual meter update rate, to fix the throttle interval.~~ **Done:** two
   distinct rates, 13.4 Hz and 0.4 Hz; throttle fixed at 250 ms. See Threading
   and rate above.
4. Live-radio smoke against both SmartSDR 4.1.5 and 4.2.x servers.

All C# in this plan is written on whichever seat, but no part of it is verified
until the Windows-side gates and the live-radio smoke have passed.

## Phase 2 outline (not committed)

Antenna selector, RF gain up/down with readout, ten band buttons with per-band
memory, four mode buttons. Requires the active-slice path first:
`Radio.BoundClientID` calls `BindGUIClient` (`Radio.cs:795-804`, `14395`) and
`Slice.Active` exists (`Slice.cs:104`), so binding to the GUI client behind
`SelectedControlStation` is the route. That binding is new connection state and
interacts with the existing multi-station logic in the CW and Digital tabs, so
it gets its own design pass.

## Phase 3 outline (deferred)

TX and PTT (`Radio.Mox`), guarded by `Radio.InterlockState` /
`InterlockReason`. QRP/QRO presets are cheap when they arrive: `Radio.RFPower`
(`Radio.cs:8370`) is an int in watts clamped 0-100, emitting
`transmit set rfpower=N`, with `TunePower` separate. CWX is a full surface, not
a stub: `CWX.cs` has `SendMacro(int)`, `Send(string)`, a `Macros[]` array with
`GetMacro` / `SetMacro`, plus `Speed`, `Delay`, `QskEnabled` and
`MessageQueued` / `CharSent` events, so "send CW from memories" is
`SendMacro(index)`.

Everything in this phase transmits. A mis-click into a cold amplifier or the
wrong antenna is a hardware-damage path, not a UI bug, so each control here
needs an explicit guard and a materially higher live-smoke bar.

## Alternatives considered and rejected

| Alternative | Why not |
|-------------|---------|
| New tab in MainWindow | The app gets minimized after setup; a control surface has to stay visible. |
| Standalone app reusing SmartStreamer4 | Two pipelines, two update checks, two radio sessions, library versioning across repos. Recreates the fragmentation being removed. Reversible later if it earns an audience. |
| System-wide hotkeys | Win32 P/Invoke, conflict detection, rebinding UI. The actual requirement is mouse-free control of a window that is already on screen. |
| Named RF gain level buttons (-8..+32) | Eleven buttons for what two do better, and the radio reports its own range and step. |
| Held last-TX power/SWR readings | Misrepresents current radio state. Zero on RX is a useful live signal. |
| User-configurable grid now | Solves a problem not yet measured. Fixed layout first. |
| Building against 4.2.20 | Couples a feature to a library bump; failures become ambiguous. |

## Follow-ups outside this plan

- Regenerate `cdub89/FlexLib-API-4.1.5-Docs` against 4.2.20. The meter-name
  drift found here shows the cost of designing against the stale copy.
- Retitle issue #59 from v0.2.1 to v0.3.0.
- Consider capturing the "own the dependency rather than wait on third-party
  developers" rationale in the CLAUDE.md Design Philosophy section. It is the
  reason this app exists and it will keep driving build-vs-depend calls on both
  seats.
