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

Originally: build against **4.2.18.41174**, the version the repo
project-referenced when this plan was written, keeping the 4.2.20.41343 drop as
a separate change with its own smoke test. Bundling a library upgrade into a
new subsystem means a misbehaving radio gives no signal about which one caused
it.

That held, and the upgrade has since landed on its own: issue #61 migrated the
repo to **4.2.20.41343** (commit `e402b5b`, live-tested and closed
2026-08-03), so SmartDeck now builds against 4.2.20. The separation did its
job, and the gating spike was captured against a 4.2.20 client and server.

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
`CaptureTelemetrySpike` command, the Logs-tab button) was to be kept until a
4.1.5 capture was also taken; with 4.1.5 support dropped on 2026-08-03 that
capture will never happen, so the diagnostic can go.

## Open items requiring the Windows seat

1. ~~The gating spike.~~ **Done 2026-08-02, passed.** See the result above.
2. ~~Meter names confirmed against the **SmartSDR server** versions in the
   field, not against a client library version.~~ **Done.** Confirmed against a
   4.2.20 server (above). A 4.1.5 capture was also outstanding until
   **2026-08-03, when support for SmartSDR 4.1.5 and earlier was dropped**;
   4.2.x is now the only target, so this item is closed.
3. ~~Actual meter update rate, to fix the throttle interval.~~ **Done:** two
   distinct rates, 13.4 Hz and 0.4 Hz; throttle fixed at 250 ms. See Threading
   and rate above.
4. ~~Live-radio smoke against both SmartSDR 4.1.5 and 4.2.x servers.~~
   **Done.** Phase 1, all of phase 2 and the layout pass all smoke-tested on a
   FLEX-6400M running SmartSDR 4.2.20. The 4.1.5 half of this item went away
   with 4.1.5 support on 2026-08-03.

All C# in this plan is written on whichever seat, but no part of it is verified
until the Windows-side gates and the live-radio smoke have passed.

## Phase 2: design pass done, split into 2a / 2b / 2c

Antenna selector, RF gain up/down with readout, ten band buttons with per-band
memory, four mode buttons. Roughly seventeen controls, split by risk profile:

- **2a (built 2026-08-02)**: slice selector, four mode buttons, RX and TX
  antenna selectors.
- **2b (built 2026-08-02)**: ten band buttons with per-band frequency memory.
  App-side logic in `BandMemory.cs` reusing `HamBands.Label`, no new FlexLib
  surface. Pressing a band stores the departing frequency under its own band,
  then tunes to the stored frequency for the band entered, falling back to that
  band's default the first time. **Memory is persisted** in
  `AppSettings.SmartDeckBandMemoryMhz`, not session-scoped: an operator who
  sets 20m to their CW spot expects the button to return there next session.
  The `BandMemory` instance is handed the settings dictionary itself, so
  departures land directly in what gets saved. Defaults are the **SKCC calling
  frequencies** (operator's choice after the first live test; an earlier
  band-edge-CW set sat too low in each range to be a useful landing point), and
  each is overwritten the first time that band is left, so a default only ever
  matters once. 60m is included in the ten; it is channelized and has no SKCC
  calling frequency, so its default is a US channel centre.
- **2c (built 2026-08-02)**: RF gain up/down with readout. Writes
  `Panadapter.RFGain` on the panadapter behind the selected slice, reached via
  `SliceInfo.PanadapterStreamId`. The range is radio-reported, so there is no
  per-model table: `RFGainLow` / `RFGainHigh` / `RFGainStep` arrive only in
  reply to `GetRFGainInfo()` (`Panadapter.cs:39-65`), which is **not** part of
  a panadapter's normal status. `TrackPanadapter` issues that request once per
  panadapter; until the reply lands, all three read zero, so the control stays
  disabled rather than stepping against a 0-to-0 range. Stepping clamps to the
  range (so one step below the ceiling reaches the ceiling) and returns absent
  when the value would not change, letting the caller skip the radio write
  rather than re-send a value the radio already holds.
- **AGC-T (built 2026-08-02)**, added at the operator's request and not in the
  original phase 2 inventory. `Slice.AGCThreshold`, sent as
  `slice set N agc_threshold=X`. **Slice-scoped, unlike RF gain**, so it sits
  with mode and antenna rather than with the panadapter controls. Its 0-100
  range is fixed by the protocol rather than radio-reported: FlexLib clamps on
  write and rejects out-of-range reads (`Slice.cs:1361-1380`, `2074-2085`), so
  there is no range request and no "not yet known" state. Step is 5, which is
  ours to choose rather than the radio's, giving 20 presses end to end.

RF gain and AGC-T both step an integer inside a bounded range, differing only
in where the bounds come from, so the clamping arithmetic is shared in
`SteppedRange.Next` rather than duplicated per control.

All of phase 2 is **live-validated against a FLEX-6400M on SmartSDR 4.2.20**
(2a/2b/2c and AGC-T built 2026-08-02, re-confirmed through the layout pass
2026-08-03). One operator-reported defect was found and fixed during 2b: with
two slices, selecting slice B and pressing a band button snapped the selector
back to slice A, because the ComboBox wrote null through the two-way
`SelectedItem` binding the moment `Slices` was cleared. Fixed by capturing the
selection before the clear; the fix survives the layout pass, which replaced
that ComboBox with chips.

### No GUI-client binding, and no "active slice"

An earlier draft of this plan said phase 2 required binding via
`Radio.BoundClientID` / `BindGUIClient` and following `Slice.Active`. **Both
were dropped.**

**Binding buys nothing these controls need.** Every phase-2 control targets a
slice (`RXAnt`, `TXAnt`, `DemodMode`, `Freq`) or the panadapter behind it
(`RFGain`, via `SliceInfo.PanadapterStreamId`). None is client-scoped, and the
CW Skimmer spot-click path has been writing `Slice.Freq` on the control
station's slice unbound, in the field, for releases.

**There is no single active slice to follow.** SmartStreamer launches CW
Skimmer and WSJT-X per slice, concurrently. A control target that moved
whenever the operator changed focus in SmartSDR would fight the way the app is
actually operated. `Slice.Active` is also per GUI client, so on a multi-station
radio more than one slice reports `active=1` at once.

**So SmartDeck uses an explicit slice selector**, scoped to
`SelectedControlStation` (matching how slice sync and pan visibility already
filter) and sticky: the selection survives list churn and re-resolves only when
the selected slice disappears.

### No app-side transmit guard on antenna changes

An earlier draft guarded antenna changes on `Radio.Mox`, on the grounds that
hot-switching is a hardware-damage path. The radio itself refuses antenna
changes while transmitting, so the guard was app-side code duplicating a
hardware interlock, and was removed.

One residual to watch in live testing: FlexLib's `RXAnt` / `TXAnt` setters
update their local cache *before* sending the command (`Slice.cs:185-198`), so
a radio-refused change could leave FlexLib's cached value briefly ahead of the
radio until the next status update corrects it.

## Layout pass (live-validated 2026-08-03)

Every phase appended a row to the window, so what phase 2 left behind was nine
labelled form rows with three ComboBoxes in it: a settings dialog rather than a
control surface. The controls and the plumbing beneath them were right; the
arrangement was not. This pass changed only the arrangement. No new radio
verbs, no behaviour change, and the antenna write path deliberately untouched.

### What the surface being replaced actually looks like

FlexButtons is a 5x4 grid of identical buttons in a 349x223 window: one press
per function, no dropdowns, and the current band outlined. Two things it does
not do set the bar rather than the target. It is write-only apart from that
outline, and its band buttons are hand-coded stacks of commands preset to fixed
SKCC calling frequencies, which SmartDeck's departure-capture band memory
already improves on.

### Chosen: sectioned deck

Three candidates were mocked and compared: a sectioned deck, a version keeping
the existing label gutter, and a landscape two-column form. Sectioned deck won.

- One small heading per group replaces the 40px label gutter on every row, and
  the buttons get that width back.
- The three ComboBoxes become button rows. Slice becomes letter chips, and both
  antennas become buttons over the radio's own reported options, so each row
  sizes itself to the three or five values the model actually reports rather
  than to a fixed five.
- Band, mode, both antennas and the slice light in the Windows accent when they
  hold the radio's current value. This is the substantive fix: the surface was
  write-only, so "what am I on" could only be answered by opening a dropdown.
- A header carries the selected slice's frequency, grouped MHz.kHz.Hz as
  SmartSDR groups it, plus its mode. That state previously existed only inside
  the slice dropdown's label, which is the one place it could not be read at a
  glance.
- Always-on-top moved out of the body into a header pushpin.

### Lit state is computed in the ViewModel, not compared in XAML

`DeckOption` carries a label and an `IsCurrent` flag per button, and the
ViewModel keeps the flags in step with the radio. Comparing each item against a
current-value property in XAML instead would need a multi-value converter
inside every item template. Keeping the comparison in the ViewModel leaves the
view declarative and makes the lit state unit-testable, which is where the new
button-state tests live.

Antenna buttons assign the same two-way properties the ComboBoxes bound to
rather than calling the connection directly, so `SetSliceRxAntennaAsync` still
has exactly one call site and the existing echo-suppression guard still covers
it.

### Keyboard

The phase-1 commitment to keyboard-first was unimplemented: no `KeyBinding`, no
focus visual and no tab order anywhere in the XAML. This pass added the
accelerators the operator already uses in FRStack. `F1`/`F2` step RF gain,
`F3`/`F4` step AGC-T. Both, and sensible tab traversal, confirmed in the
2026-08-03 live test.

They are in-app only, per the phase-1 decision against `RegisterHotKey`. They
fire while SmartDeck has focus and do nothing while SmartSDR has it. That is a
real difference from FRStack's system-wide bindings and is the cost of that
decision, not an oversight.

### Decisions this pass settled

- **RF gain stays an up/down pair.** FlexButtons offers named preset levels
  (`RX -8` through `RX 24`) and the operator uses them, which briefly looked
  like a reason to revisit the decision against named levels. It is not: those
  presets are a FlexButtons limitation, and the shift to `-`/`+` was
  deliberate.
- **No icons.** The Stream Deck icon set attached to the issue is artwork for
  the physical deck's buttons, not assets for this window. Text labels
  throughout.
- **The pushpin is a vector `Path`, not an icon-font glyph.** The Windows pin
  codepoints (`E718`, `E840`) are outlines, so recolouring one only tints the
  outline and the on state is hard to see. Drawing the shape gives `Fill` and
  `Stroke` as separate properties: hollow grey when off, interior filled with
  the lit blue when on. Filling the button box instead was tried first and read
  as a sixth radio control.
- **Skin deferred.** Whether SmartDeck keeps Windows Fluent light or adopts the
  Stream Deck look (bright blue on near-black) is open, and deliberately
  separated from the arrangement.

## Per-band memory beyond frequency (live-validated 2026-08-03)

`BandMemory` stores a frequency per band. The operator's FlexButtons and Stream
Deck buttons stack several commands behind one press, setting band, frequency,
antenna and RF gain together, and the equivalent here is to widen what a band
button restores.

Why this cannot be left to the radio, which is the part that is easy to get
wrong: a real band change on the radio tears the slice down and rebuilds it
from the radio's own slice persistence, and that is how antenna and gain follow
the band. SmartDeck never does that. It writes `Slice.Freq` on a live slice,
deliberately, because CW Skimmer and WSJT-X are bound per slice and the slice
surviving is the whole point. The cost of that choice is that nothing else
follows the frequency, so the restore has to be ours.

The per-band record therefore grows from a frequency to **frequency, mode, RX
antenna, TX antenna, AGC-T**. All five are slice-scoped, so a restore is one
write target.

### What follows a direct frequency change, and what does not

The load-bearing rule behind this whole section, established by bench testing
against SmartSDR with SmartDeck closed (2026-08-03). It predicts what SmartDeck
has to remember and what it must leave alone, so check a setting against it
before adding it to `BandState`.

| Scope | Follows a band change? | Examples |
|-------|------------------------|----------|
| Slice | **No.** SmartDeck must remember it. | Mode, RX antenna, TX antenna, AGC-T |
| Radio / panadapter | **Yes**, the radio persists it per band. | RF gain, RF power, tune power |

Slice-scoped settings do not follow because slice persistence loads only when a
slice is *created* (`Radio.RequestSlice`, `load_from=PERSISTENCE`,
`Radio.cs:5494-5501`), and SmartDeck never creates one: it writes `Slice.Freq`
on a live slice so CW Skimmer and WSJT-X stay bound. Radio and panadapter
settings do follow, because those objects are not recreated either. They simply
move to the new band and the radio applies what it has stored for them.

Verified directly: with SmartDeck closed, direct frequency entry across bands
leaves RX and TX antenna exactly where they were, while RF gain changes on its
own. **RF power and tune power behave the same way as RF gain** (operator,
2026-08-03), which matters for phase 3: QRP/QRO presets do not need per-band
memory, and building it would fight the radio.

**AGC-T follows the rule: slice-scoped, not band-persistent, so SmartDeck
remembers it.** Settled 2026-08-03 by direct bench test, twice. The decisive
run: set AGC-T on 15m, enter a 20m frequency (it does not move), reduce it,
return to 15m (it does not move), increase it. The radio simply leaves AGC-T
where it is.

Recorded because it cost an hour: a harness capture appeared to show the radio
volunteering a per-band AGC-T value ~200 ms after each frequency change, three
times out of three, and that reading was wrong. **Do not treat AGC-T values in a
slice status dump as evidence of anything.** The reachability capture proved the
radio never echoes `agc_threshold` in response to a set, unlike antennas which
are confirmed within ~200 ms. Our AGC writes are therefore never acknowledged,
FlexLib's cache and the radio drift apart freely, and AGC-T is the least
trustworthy field in any capture. The bench test is authoritative here; the wire
trace is not.

A useful consequence: because there is no echo to wait for, SmartDeck's
optimistic AGC-T readout is the only option available, not a shortcut. RF gain
is different, since the radio does echo that (`Panadapter.cs:1137`).

FlexLib's client-side handling was audited and is not the culprit: the setter
emits `slice set <n> agc_threshold=<v>` correctly, and the status parser maps
`agc_threshold` and `agc_off_level` to their own fields with correct clamping
(`Slice.cs:1360-1380`, `2056-2089`). If something is misbehaving it is
radio-side. Worth knowing for whoever looks next: **both AGC status branches
silently discard a value that fails `uint.TryParse` or exceeds 100**, logging
only to `Debug.WriteLine`. In a release build a malformed or out-of-range
`agc_threshold` disappears with no trace and the cached value simply persists,
which is indistinguishable from the radio never having reported.

**RF gain is deliberately excluded, and adding it would be a bug.** The weak
reason is that it is a panadapter property reached through
`SliceInfo.PanadapterStreamId`, so including it would make a restore two write
targets against two different objects. The real reason is empirical, confirmed
in the 2026-08-03 live test: **the radio already restores RF gain per band by
itself**, and writing our own remembered value would fight it.

The mechanism is the mirror image of the slice problem above.
`Panadapter.RFGain`'s setter only sends `display pan set 0x<id> rfgain=N`, and
`Panadapter.cs:1137` has a `case "rfgain":` branch that accepts the value *from*
radio status. There is no client-side band logic. Pressing a band button moves
`Slice.Freq`, the panadapter follows the slice, the radio applies its own stored
per-band gain for that panadapter and pushes it back, and `PanadapterUpdated`
runs `ApplyRfGainState()` so the readout tracks. The operator sees per-band RF
gain without SmartDeck doing anything.

Slice state gets none of this because `Radio.RequestSlice` loads persistence
only via an explicit `load_from=PERSISTENCE` flag at slice *creation*
(`Radio.cs:5494-5501`), and SmartDeck never creates a slice. The panadapter is
not recreated either; it simply moves, which is why its own persistence still
applies.

So RF gain stays a live `-`/`+` control that a band button does not touch. Do
not add it to `BandState`: doing so would race the radio's own per-band value on
every band change and sometimes overwrite it with a staler number.

**No migration, by the operator's choice.**
`AppSettings.SmartDeckBandMemoryMhz` (a `Dictionary<string, double>`) is
replaced outright by `SmartDeckBandMemory` (a `Dictionary<string, BandState>`).
The old key is simply left unread in existing settings files, and each band
re-learns on its first departure, so the only cost is one visit per band.

One persistence detail worth knowing before editing a settings file by hand:
`BandState.Mode` carries its own `JsonStringEnumConverter` rather than relying
on the store's options, which have no global string-enum converter. Without it
the mode would persist as an ordinal and reordering `SliceMode` would silently
remap every saved band. The converter writes the C# member name, so the JSON
reads `"Cw"` while the radio wire value is `"CW"`.

## Live-radio capture harness (recipe)

Three questions in the layout and band-memory work could not be settled by
clicking buttons and watching: they needed millisecond ordering of what we wrote
against what the radio reported. A temporary harness answered all three in an
afternoon, and the useful discovery is that **it needs no production code at
all**. `IRadioConnection` already exposes `SliceUpdated`, `PanadapterUpdated`,
`ConnectionStateChanged` and `DiagnosticEvent`, so a test-side subscriber sees
everything. This is unlike the phase-1 telemetry spike, which did need temporary
code in the app and had to be deleted from it afterwards.

Shape, should phase 3 want one:

- A `[Fact]` in `tests/SmartSDRIQStreamer.App.Tests/`, returning early unless an
  environment variable is set, so `dotnet test` stays offline and green.
- Discover, `ConnectAsync`, wait ~3 s for slices. SmartSDR must be running:
  SmartStreamer is a non-GUI client and cannot create a slice.
- Subscribe the connection events, stamping each with
  `Stopwatch.ElapsedMilliseconds` and `Environment.CurrentManagedThreadId`. The
  thread id is what distinguishes our own optimistic cache writes from
  radio-originated status arriving on the pump thread, and that distinction was
  the whole answer to the antenna question.
- Drive the real ViewModel command rather than reproducing its writes, so the
  capture exercises the shipping code path.
- Report the output path via `Assert.Fail`, since a passing test prints nothing.

Delete it once the questions are answered. A harness left behind is a test that
asserts nothing while inflating the count, and phase 3's captures will need
different bodies anyway.

**Phase 3 caution:** everything there transmits. A harness that keys the radio
is a different risk class from one that changes bands on receive, and deserves
its own guards written deliberately rather than inherited from this one.

## Phase 3 outline (deferred)

TX and PTT (`Radio.Mox`), guarded by `Radio.InterlockState` /
`InterlockReason`. QRP/QRO presets are cheap when they arrive: `Radio.RFPower`
(`Radio.cs:8370`) is an int in watts clamped 0-100, emitting
`transmit set rfpower=N`, with `TunePower` separate.

**Neither power setting needs per-band memory.** Both are radio-scoped, so per
"What follows a direct frequency change" above they already persist per band on
the radio and follow a direct frequency entry on their own (operator-verified
2026-08-03). Adding them to `BandState` would race the radio's own value, which
is the mistake that section exists to prevent. CWX is a full surface, not
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
