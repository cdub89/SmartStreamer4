# PLAN: v0.3.3 — controls the hardware has and the software does not

## Context

v0.3.1 shipped SmartDeck as a control surface that replaces the accessory-app
layer (issue #59). It did not replace the *knobs*. An operator running headless,
or on a remote seat, or simply without a Maestro or FlexControl on the desk, has
no way to set RIT at all — the one control on that hardware with no software
equivalent anywhere in the app. That gap is the theme of this release.

The slate is deliberately narrow. Everything on it either hands the operator a
control they physically do not have today, or removes a click or a confusing
failure between them and operating. Items that only make the app prettier or
tidier were pushed to v0.3.3 (see Deferred).

Release-bar check (CLAUDE.md, "No release without operator-facing benefit"):
five of the seven items are things an operator feels directly. The two that are
not (#74 is already merged, #75 is hygiene) ride along rather than justifying
anything.

## Slate

| # | Item | Operator-facing | Size |
|---|------|-----------------|------|
| 73 | RIT/XIT control on SmartDeck | yes, anchor | M |
| 69 | TX indication: slice chip goes red on transmit | yes | M |
| 62 | Startup persistence, Phase A (auto-connect) | yes | S |
| 66 | CAT port preflight probe for Digital Start | yes | S |
| 70 | QRP 3-way toggle (QRP / saved / QRO) | yes | S |
| 74 | CW Skimmer INI failure handling | yes, already merged | done |
| 75 | Launcher diagnostics cleanup | no, rides along | S |

## 1. Issue #73 — RIT/XIT (anchor)

### What already exists

- `SliceInfo` carries `RitEnabled` and `RitOffsetHz`
  (`src/SmartSDRIQStreamer.FlexRadio/RadioDetails.cs:118-119`).
- FlexLib exposes settable `RITOn`, `RITFreq`, `XITOn`, `XITFreq`
  (`FlexLib_API_v4.2.20.41343/FlexLib/Slice.cs:1482-1552`).
- `WheelNotchCounter` and the `Nudge` helper give one-step-per-detent wheel
  handling, already live-tested (`WheelNotchCounter.cs`,
  `SmartDeckWindow.axaml.cs:89-112`).
- CW Skimmer sync already folds RIT into the effective RX frequency
  (`MainWindowViewModel.cs:2921`, `GetEffectiveRxFrequencyMHz`).

### What is missing, verified

1. **XIT is invisible end to end.** `SliceInfo` has no XIT fields, and the slice
   property-change filter at `FlexLibRadioConnection.cs:1007-1008` passes only
   property names containing `RIT` or `Step`. `XITOn` and `XITFreq` fail that
   test, so XIT changes are dropped before reaching the UI. This is the
   non-obvious part of the change; a build that adds the UI without widening
   this filter will look correct and never update.
2. **No write path.** `IRadioConnection` has no RIT or XIT methods.
3. **FlexLib silently no-ops out-of-range writes.** `Slice.cs:1500-1505` and
   `1537-1542` fire `RaisePropertyChanged` and return *without sending* when the
   value exceeds +/-99999 Hz. It does not clamp. We clamp our side or the wheel
   dies at the limit with no feedback and no command on the wire.

### Operator decisions (2026-08-24), do not re-litigate

- **Step is 10 Hz per detent, fixed, not configurable.** This is what the
  Maestro and FlexControl do today; matching them is the point of the feature.
  Explicitly *not* the slice's `TuneStepHz` (which the frequency wheel uses):
  a slice on a 500 Hz SSB step would move RIT 500 Hz per click.
- **RIT and XIT stay independent on the radio.** Both can be on, or neither.
  An earlier draft proposed making them mutually exclusive; rejected, because it
  would remove a state the radio legitimately supports.
- **Both are shown at once, side by side. There is no view toggle.** An earlier
  draft had a toggle switching one readout between RIT and XIT to save width;
  the operator replaced it with showing both, which fits. This removes a control
  *and* removes the failure mode that came with it: with only one side visible,
  XIT could be engaged from the Maestro while the operator was looking at the
  RIT view, and the app would be hiding radio state from the person it exists to
  inform. Showing both makes that structurally impossible.
- **Colour carries enabled state, the number carries the offset.** Lit when
  `RITOn` / `XITOn` is true, muted when not. Both use existing theme brushes, so
  Light and Dark both work without new tokens.
- **Styled as the MHz and Mode readouts above it**, not as a new visual idiom:
  SemiBold value paired with a 10px `AppMutedBrush` label, bottom-aligned on a
  shared baseline with the same 3px gap. See "Type treatment" below for the one
  place this deviates and why.
- **Click toggles enabled, wheel changes the offset.** One gesture each, per
  control. The wheel does **not** auto-enable as a side effect; it only moves
  the number. Consequence, accepted deliberately: wheeling a muted RIT changes
  the stored offset with no on-air effect until it is clicked on, and the muted
  colour is the only cue. That is the predictable behaviour rather than the
  convenient one, and the offset is then already set when the operator does
  enable it.
- **No dedicated reset control.** Enable/disable is the operator's "back to
  normal", and scrolling covers typical offsets in about 20 detents at 10 Hz.
  Long-press was the original thought and was dropped: it needs press-and-hold
  timer plumbing that exists nowhere in the codebase, for a gesture nothing else
  in the app teaches. See "Reset, deliberately absent" below.

### Placement — decided: option D

The constraint that decides this: SmartDeck is **fixed at 360px wide**
(`SmartDeckWindow.axaml:10`, `Width="360" MinWidth="356"`) with
`SizeToContent="Height"`. Width is scarce and height is cheap. The deck's
established idiom for an adjustable value is row 3's pattern, a caption above a
`-` / readout / `+` stepper with the wheel handler on the whole group so the
target is large (`SmartDeckWindow.axaml:410-492`).

Row 3's width is already committed: RF gain 104px, AGC-T 84px, TX power 78px,
20px of column spacing and 20px of margins come to about 306 of 360. **Roughly
54px spare**, against the ~84px a bare stepper needs. That arithmetic rules out
one option outright and shapes the rest.

**Option A, its own row in the deck's idiom (rejected: adds height).** A fourth control
row between row 3 and the telemetry footer, holding the RIT/XIT view toggle, the
enable button, and the stepper. Consistent with every other control on the deck,
full room for the wheel target, no collision with anything. Cost: SmartDeck
grows roughly 48px taller (caption + 32px button row + 6px row spacing). Height
is the axis the window can absorb.

**Option B, fold into row 3 as a fourth group.** Not viable at 360px: four
stepper groups need about 340px before spacing, leaving the readouts no room to
grow. Would require removing an existing control to make space. Recorded here so
it does not get re-proposed.

**Option C, in the header strip, as the issue text asks.** Worst of the set. The
header is `ColumnDefinitions="Auto,*,Auto"` (chips, centred readout, pin), and
the comment at `SmartDeckWindow.axaml:210-218` records that adding "Mode" to the
centred readout already moved a slice-chip collision from four slices down to
three. RIT plus XIT plus a toggle pushes that to two.

**Option D, a second line beneath the frequency readout.** Goes vertical inside
the header centre column instead of widening it, so it sidesteps the chip
collision, and costs only about 16px of height. But there is no room beside it
for the stepper or the enable button, so the control becomes wheel-and-click
only. The codebase already names that cost: "Wheel-only means undiscoverable,
hence the tooltip on the readout" (`SmartDeckWindow.axaml:467`). It also
disturbs the header baseline alignment that three separate comments warn took
several passes to get right.

**Option E, hybrid: a small always-visible enable chip, full row on demand.**
The row appears only while RIT or XIT is engaged, so the deck is unchanged at
rest. Costs no height until used, but adds a control that moves under the
operator and splits one feature across two places.

**Decision (operator, 2026-08-24): option D, with both values shown.** The
governing constraint is no new height and no new width for now, which rules out
A. The operator's refinement improves on D as described above: rather than one
readout with a toggle, show `RIT <value>  XIT <value>` side by side under the
frequency. Recommendation A is recorded above as the rejected alternative, not
as pending.

Honest cost, since "no new height" is not quite free: the header's height is
currently set by the mode button, which Fluent floors at 32px
(`SmartDeckWindow.axaml:67`), against a 17px frequency readout of roughly 23px.
That leaves about 9px of slack inside the centre column. The RIT/XIT line at 13px
runs about 17px, so the header grows by roughly **8px** rather than the ~16px a
free-standing line would cost. The operator accepted that as reading unchanged
(2026-08-24). Confirm on the bench rather than trusting this arithmetic.

Width check at the worst case: with four slice chips and the pin claiming their
columns, the centre column keeps roughly 190px. `RIT +1200  XIT -1200` at 13px
runs about 135px. It fits with margin, and no `Hz` unit is carried on the
readouts. Verify at four slices during the smoke.

### Shape

A second line inside the header's centre column, beneath the frequency and mode
readout, holding two independent readouts:

- `RIT <value>` and `XIT <value>`, side by side.
- **Wheel over either one** adjusts that offset, 10 Hz per detent. No stepper
  buttons.
- **Colour is the enabled state**: lit when on, muted when off.
- **Type matches the readouts above**: 13 SemiBold value, 10px muted label,
  bottom-aligned. See "Type treatment" below.
- **Click either readout to toggle its enabled state.** The only affordance
  left once the stepper and the view toggle are gone, and enough on its own.

Wheel-only is a real cost and the codebase already names it: "Wheel-only means
undiscoverable, hence the tooltip on the readout"
(`SmartDeckWindow.axaml:467`). But TX power is already wheel-only with a tooltip
and shipped that way in v0.3.1, so this follows an accepted precedent rather
than setting a new one. Both readouts get tooltips.

Code surface:

- `RadioDetails.cs`: add `XitEnabled` / `XitOffsetHz` as `init` properties on
  `SliceInfo`, not positional parameters — same approach as the antenna fields
  (`RadioDetails.cs:126-134`), so existing `SliceInfo` construction sites are
  untouched.
- `FlexLibRadioConnection.cs`: `ResolveXitEnabled` / `ResolveXitOffsetHz`
  mirroring the RIT resolvers at `:1011-1026`; widen the property filter at
  `:1007-1008` to admit `XIT`.
- `IRadioConnection`: four methods following the existing verb granularity —
  `SetSliceRitEnabledAsync`, `SetSliceRitOffsetAsync`, `SetSliceXitEnabledAsync`,
  `SetSliceXitOffsetAsync`.
- Named constants, per CLAUDE.md (tunable thresholds must not be bare literals):
  `RitXitStepHz = 10` and `RitXitLimitHz = 99_999`.
- `SmartDeckViewModel`: offset text and enabled state per side. No view-selector
  enum, no hidden-side flag — both are gone with the toggle.
- `SmartDeckWindow.axaml` / `.axaml.cs`: wrap the centre column's existing
  horizontal readout row in a vertical StackPanel and add the RIT/XIT line
  beneath it, plus `OnRitWheel` / `OnXitWheel` handlers routed through the
  existing `Nudge` so notch counting stays in one place.

**Markup risk to respect:** the centre column's baseline alignment is warned
about in three separate comments (`SmartDeckWindow.axaml:224-231`, `249-260`)
and took several passes to get right. Wrapping the existing horizontal row in a
vertical parent should leave the inner row's baselines untouched, because
nothing inside it changes. Verify visually before anything else in this item is
called done; do not restructure the inner row.

### Type treatment

SmartDeck already runs a three-tier type hierarchy, and size encodes rank
deliberately. The comment at `SmartDeckWindow.axaml:30-35` states the principle:
the telemetry footer was stepped down from the control readouts because
"matched, the footer competed with the controls above it".

| Tier | Value | Label |
|------|-------|-------|
| Header identity (frequency, mode) | 17 SemiBold | 10 muted |
| Control rows (RF gain, AGC-T, TX power) | 13 SemiBold | 10 muted |
| Telemetry footer | 11 SemiBold | 8 muted |

RIT/XIT takes the header's *pattern* — SemiBold value, 10px `AppMutedBrush`
label, bottom-aligned, 3px gap — at the **control-row value size of 13, not the
header's 17**. Two reasons, both worth keeping when someone later wonders why it
does not match the line above it exactly:

1. At 17 the RIT offset reads as visually equal to the frequency the radio is
   tuned to. That is the failure the footer pass already found and reverted, one
   tier up.
2. At 17, two readouts run about 175px against the roughly 190px the centre
   column keeps with four slices running. It fits only if the chip-width
   estimate is right, and breaks at five slices. At 13 there is real margin.

This uses no new type sizes: 13 SemiBold and 10 muted are both already in the
deck. Height cost is about 8px rather than the 4-5px a 10px line would cost,
which the operator accepted as reading unchanged (2026-08-24).

### Label order: label first, deviating from the deck

The deck's shape is value-then-label (`14.025000 MHz`, `CW Mode`). RIT/XIT
inverts it to `RIT +120  XIT -50`. The reason is specific to there being two of
them on one line: value-first gives the eye two bare numbers it must backtrack
to disambiguate, while the label anchors each pair on first read. Radio UIs
prefix RIT universally for the same reason. Deliberate deviation, not an
oversight.

### Enable and adjust, settled

Click a readout to toggle its `RITOn` / `XITOn`; wheel over it to move its
offset. The wheel deliberately does not enable as a side effect (see Operator
decisions above). Both readouts carry tooltips saying so, since neither gesture
is visible.

### Reset, deliberately absent

The issue asks for press-and-hold to zero. Dropped by operator decision on
2026-08-24 in favour of the simpler pattern: disable/enable is the control an
operator actually reaches for to get back to normal, and the wheel walks the
offset back when a true zero is wanted.

One semantic worth knowing, because it will look like a bug otherwise:
`RITOn` and `RITFreq` are independent in FlexLib (`Slice.cs:1482-1516`).
Turning RIT off leaves the offset stored, so flipping it back on restores the
previous offset rather than coming back at zero. That matches hardware
behaviour, but off/on is *not* a zeroing gesture.

If live testing shows scrolling back is tedious, click-to-zero on the offset
readout is nearly free to add later: that TextBlock already needs
`Background="Transparent"` and an event handler for the wheel. Deferred on
purpose, to be decided on evidence rather than guessed at now.

### Test decisions

- Clamp and step math (`RitXitLimitHz`, `RitXitStepHz`, nudge accumulation):
  **new tests** in `tests/SmartSDRIQStreamer.App.Tests/`. There is no FlexRadio
  test project, so SmartDeck logic is tested there.
- `SliceInfo` XIT fields and any `DisplayLabel` change: **extend** the existing
  `RadioDetails` / SmartDeck tests.
- Offset formatting and the enabled-to-colour mapping for both sides:
  **new test** — signed formatting (`+120` / `-50` / `0`) and the lit/muted
  decision are real logic. Type sizes and brushes are markup: **skip**, covered
  by the live-radio smoke.
- A nudge from the disabled state moves the offset without enabling:
  **new test**, since this is the behaviour most likely to be "fixed" later by
  someone assuming it is a bug.
- FlexLib property mapping and the widened change filter: **skip**, not
  unit-testable without a radio; covered by the live-radio smoke gate below.
- Avalonia row markup and wheel event wiring: **skip**, UI-only; covered by the
  live-radio smoke.

### Risks

- **Command rate on a fast spin.** One `slice set N rit_freq=` per detent. The
  frequency wheel already behaves this way against `slice set freq` and survived
  live test, so the precedent holds, but watch it during the smoke.
- **Header width at four slices.** The centre column is a `*` between the slice
  chips and the pin, so the RIT/XIT line has least room exactly when the most
  slices are running. Checked arithmetically above; confirm at four slices.
- **Header height creep.** Roughly 4-5px by the estimate above, against an
  explicit "no new height" constraint. If it lands worse than that on the bench,
  come back to this rather than absorbing it silently.
- **Discoverability**, mitigated by tooltips and by the TX power precedent.
- **Not a risk, despite appearances:** CW Skimmer sync. The sync path reacts to
  slice updates regardless of origin, and already folds RIT into the effective
  RX frequency, so a RIT change we send is indistinguishable from a Maestro knob
  turn. The smoke test should still confirm spot alignment holds with RIT
  engaged.

## 2. Issue #69 — TX indication (slice chip red on transmit)

A control surface that replaces hardware should tell the operator when the radio
is transmitting. This is also the piece that makes the SWR readout honest: the
workaround documented at `SmartDeckViewModel.cs:1019-1033` currently infers
transmit from a power-watts threshold, because "MOX would need new plumbing to
reach the same answer". This is that plumbing.

### Why the plumbing is missing

MOX is already observed but never surfaced. `FlexLibRadioConnection.cs:238`
handles the `Mox` property change and calls `LogMoxTransition` (`:252-280`),
which throttles it to a log line and stops there. `IRadioConnection` exposes no
transmit state at all.

### Shape of the change

- `IRadioConnection`: an `IsTransmitting` property plus a
  `TransmitStateChanged` event, following the shape of the existing
  `RfPowerChanged` / `ConnectionStateChanged` pairs.
- `FlexLibRadioConnection`: raise it from the existing `Mox` case at `:238`,
  alongside the logging that already happens there. Keep the log throttle for
  the log line only. The UI event must not be throttled or the chip lags the
  radio.
- `SmartDeckViewModel`: an `IsTransmitting` flag.
- `SmartDeckWindow.axaml`: a `Classes.tx` trigger on the slice chips, following
  the `Classes.lit="{Binding IsQrp}"` pattern already used on the QRP button
  (`:487`). Red comes from a theme brush, not a hard-coded colour, so it works
  in both Light and Dark (issue #63 established the token set in `App.axaml`).
- ~~Retire the power-threshold SWR workaround~~ **Reversed during the build,
  2026-08-25. Doing this would have been a regression.** The plan assumed the
  power threshold was a workaround standing in for transmit state. Reading it
  showed the opposite: forward power is the *better* gate for SWR, and its own
  comment says so. `IsTransmitting` means the radio is keyed, which is true
  between CW elements and on SSB with no audio, while no RF is going out. Gating
  SWR on it would put the meter's 1.0 floor back on screen as a fake 1:1 match,
  which is exactly the bug the threshold fixed on 2026-08-02. The two now sit
  side by side deliberately: transmit state says "keyed", forward power says
  "RF". Pinned by a test so this does not get "tidied up" later.

**Costs no layout space**, which is the useful contrast with #73: it recolours a
control that already exists.

### Test decisions for TX indication

- Transmit-state to chip-state mapping, and the SWR formatter now keyed on real
  transmit state rather than the power threshold: **extend** the existing
  `FormatSwr` tests in `tests/SmartSDRIQStreamer.App.Tests/`.
- MOX event plumbing through `FlexLibRadioConnection`: **skip**, needs a radio;
  covered by the live-radio smoke.

### Risk: chip flicker at CW speed

- **Chip flicker on fast keying.** CW at speed toggles MOX many times a second.
  The log path is throttled at 2s (`MoxLogThrottle`, `:25`); the UI path must not
  be, but it may need a short trailing hold so the chip reads as transmitting
  rather than strobing. Decide that against the live radio, not on the bench.

## 3. Issue #62 — startup persistence, Phase A only

Auto-connect on launch. One new bool (`StartupResumeEnabled`) and one new
`LastRadioSerial` (`string?`; absent means never connected). On launch: one
radio discovered, connect it; several, connect the one matching
`LastRadioSerial`; no match, stop and let the operator choose.

**Rejected: the issue's option 2** (independent auto-start toggles per control).
Four settings fields, four UI controls, and sixteen combinations to reason about
and live-verify. `AppSettings` already carries 48 properties. One field with one
behavior does the same job — CLAUDE.md's subtraction rule applied directly.

**Phase B** (auto-resume last mode and stream) is deferred to v0.3.3. It is the
item that auto-launches CW Skimmer before the radio and DAX have settled, which
is precisely the failure class issue #74 just untangled. It would reuse the
existing `ConnectDelaySeconds` / `LaunchDelaySeconds` (`AppSettings.cs:66-67`)
when it lands.

Note: `AppSettings.LastMode` already persists (`AppSettings.cs:26`, written at
`MainWindowViewModel.cs:779`) but is only a memory, never a restore trigger.
Phase A does not change that.

**Test decisions:** settings load/save round-trip for both new fields — **new
test** (CLAUDE.md routes every `AppSettings` field to a round-trip test). Radio
selection logic (one radio / match / no match) — **new test** against a fake
connection. Actual connect-on-launch wiring — **skip**, live-radio smoke.

## 4. Issue #66 — CAT preflight probe for Digital Start

A fresh install fails with WSJT-X's own configuration error, which says nothing
about SmartStreamer4. There is no TCP probe anywhere in the codebase today (the
only `TcpClient` is the Skimmer telnet client,
`src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerTelnetClient.cs:36`); the "Test CAT"
referenced in the help doc is WSJT-X's button, not ours.

Add a connect-with-timeout against the slice's `CatPort` before launching the
engine, failing with a message naming the port and the SmartSDR CAT > ADD fix.
Per the operator's own comment on the issue, the gates belong on their
respective Start paths: CAT gates Digital Start, DAX gates CW Start.

**Test decisions:** probe result handling and message formatting — **new tests**.
The socket call itself — **skip**, needs a listener; covered by live smoke.

## 5. Issue #70 — QRP/QRO power presets (built, and simplified twice)

**Final design (operator, 2026-08-25): two states, no saved power.** The
operator's model is QRP and not-QRP, the latter labelled QRO. The label is a
**state readout**, not a promise about the press: at 54 W it reads QRO because
that is true, you are not at QRP. Lit marks the two exact preset powers and
keeps the deck's single meaning of lit, the value in force. Pressing toggles the
state, so the first press from an ordinary operating power drops to QRP, the
habit the original button taught. Any other power is set on the TX power wheel
or in SmartSDR.

**QRP is a range, not a value** (operator, 2026-08-25): 5 W and under is QRP by
convention, so the button reads QRP across it rather than only at exactly 5 W.
Treating it as a single value was the last thing making this feel wrong on the
air, because trimming 5 W to 4 W with the wheel darkened the button and flipped
the label to QRO while the operator was plainly still running QRP.

| radio power | label | lit | press gives |
|-------------|-------|-----|-------------|
| 0 W | QRP | no | 100 W |
| 1 to 5 W | QRP | yes | 100 W |
| 6 to 99 W | QRO | no | 5 W |
| 100 W | QRO | yes | 5 W |

Lit marks a preset power actually being run, which is why 0 W reads QRP but
stays dark: it is below the range rather than in it.

**The invariant, pinned by test:** the label must never claim a state the radio
is not in. Both interim designs broke it and the operator caught both in live
test, the second time as a missing blue rather than a wrong word.

Recorded so it does not read as drift: a QRO preset was explicitly declined
during issue #59 on the grounds that the QRP button already saves and restores.
The operator filed #70 himself on 2026-08-05, reversing that.

**How it got here, because the discarded designs are the interesting part.** The
first build kept the existing save-and-restore and alternated which preset the
next press engaged, giving four positions: QRP, saved, QRO, saved. Bench-tested
2026-08-25, the operator hit the incoherence directly: the button could read
"QRP" while the radio sat at the saved power, so the label and the radio
disagreed. The fix was not a better cycle, it was deleting the saved power
altogether.

The second build then over-corrected, dropping the lit state on the grounds that
the readout beside the button already shows the power. Live test caught that too:
every other control on the deck lights for the value in force and this one had
stopped. The root cause was the same one as the first failure, wearing different
clothes. The label named the *next press*, which is what made lighting it
impossible without lying. Making the label a state readout fixed the lit state
and the wording together.

What that bought, beyond a simpler button:

- The "radio wins" stand-down is gone. It existed only to invalidate a cached
  power, and there is no cache now.
- A whole bug class went with it, including a saved power surviving a reconnect
  and being written into the next session (a Codex deep audit finding from
  2026-08-04, and the test that guarded it).
- The label became a pure function of the radio's reported power, so it cannot
  disagree with the radio at all.
- A documented wart disappeared: wheeling off 5 W and back used to leave the
  toggle dark because it tracked the saved power rather than the number.

Accepted cost, named: from QRO there is no one-press route back to the power
you were running. That is the trade the operator chose ("any power adjustments
can be made manually"); the wheel over the TX power readout is that path.

**Do not reintroduce a remembered power here.**

**Test decisions:** the saved-power tests were **deleted**, not adapted, since
the behaviour they described no longer exists. Replaced with preset alternation,
label-follows-radio, and the two wheel interactions. Net effect on the suite is
a small reduction, which is the expected shape of a subtraction.

## 6. Issue #75 — launcher diagnostics cleanup

Both items, as written in the issue. Item 2 is a *deletion* (retire
`LaunchResult.DeviceNotFound`, its launcher gate, and `FormatDeviceNotFound`),
which is the cheapest thing on the slate and shrinks surface the rest touches.
Item 1 keys `LastDiagnostics` per channel so overlapping multi-channel launches
cannot cross their status text.

**Test decisions:** per-channel diagnostics — **new test**. The folded
`TemplateIniNotFound` message variant — **extend** `CwSkimmerIniTests`.

## 7. Issue #74 — rides along

Merged at `7c133f5`, live-test passed 2026-08-24, unreleased. Held from its own
release on 2026-08-24 because the reporter (N4CZ) has a working manual
workaround and a one-commit release was not worth pushing at every operator via
the hard-coded `--latest`.

## Sequencing

Build in this order so the surface shrinks before it grows and the droppable
item is last:

1. **#75** — deletion first; every later item touches less code.
2. **#70** — small, self-contained, unit-testable.
3. **#66** — small, self-contained, one new probe.
4. **#69 TX indication** — the smaller of the two `IRadioConnection` changes,
   and it lands the event-plumbing pattern that #73's setters then follow.
5. **#73** — the anchor. Radio layer (`SliceInfo`, filter, `IRadioConnection`)
   before ViewModel before markup, so each layer is testable as it lands.
6. **#62 Phase A** — last, and droppable if the live-radio budget runs out.

## Gates

- `dotnet build` and `dotnet test` after every edit, per CLAUDE.md. Windows seat
  only.
- **Live-radio smoke is required for #73, #69, #66 and #62** (FlexLib calls,
  workflow service, audio device selection). #75 and #70 are unit-testable but #75 rides
  the same smoke since it touches the launcher.
- Specific smoke items for #73: RIT set from the app appears on the Maestro; RIT
  set on the Maestro appears in the app; XIT the same both ways; colour tracks
  enabled state for both; clicking toggles enable and wheeling does not; the
  wheel does not overshoot at +/-99999; the RIT/XIT line sits on the frequency
  block as one visual group without competing with it; the header
  does not grow noticeably and the RIT/XIT line still fits with four slices
  running; the frequency and mode baselines are unchanged; CW Skimmer spot
  alignment holds with RIT engaged.
- Specific smoke items for #69: the chip reddens on key-down and clears on
  key-up; it does not strobe at CW speed; SWR reads dashes on receive and a real
  value on transmit, now from MOX rather than the power threshold; both themes.
- No new NuGet dependencies anywhere on this slate.

## Will not do, unless a user asks again

Operator decision, 2026-09-20. These are closed on intent, not on effort:
none is blocked or hard, and the cheapest of them got cheaper after #76.
They are declined because nobody has asked for them since the original
issue was filed. A fresh user request reopens the question; nothing else
should.

- **#69, all remaining scope.** *Bar meters*: justified in the issue as
  animating the page, which is aesthetics, against a 13.4 Hz telemetry
  feed. *Auto-select active slice*: real value, but the app has no
  active-slice concept today (FlexLib has `Slice.Active`), and
  live-following would move the deck's selection under the operator's
  hand mid-wheel, so it needs its own design rather than a ride-along.
  *Mute slice*: `Slice.Mute` exists and #76 built the exact toggle
  pattern, so the code is now trivial; the real cost is a sixth column
  on a strip whose constraint is fixed geometry.
- **#68 rotator widget** — new widget plus an external PST Rotator TCP
  dependency; no protocol work done yet.
- **#73 click-to-zero on the offset readout** — see "Reset, deliberately
  absent". The gate was "add only if live testing shows scrolling back is
  tedious"; live testing across two builds did not show that.

**Correction to the record.** This section previously listed *reflected
power* here as "a pure algebraic restatement of SWR ... recommend
against". That was wrong: the radio has its own `REFPWR` meter
(`Radio.ReflectedPowerDataReady`), so it is an independent measurement,
and deriving it from SWR would have inherited that meter's 1.0 floor and
read 0 W on every good match. It shipped in `9e5e759`.

## Deferred to v0.3.4 or later

- **#62 Phase A** — auto-connect on launch. Fully specified above,
  including its test decisions; deferred 2026-09-20, not declined.
- **#62 Phase B** — auto-resume mode and stream. Follows Phase A.
- **#71 VITA-49 direct stream** — a research program, not an issue. Would
  replace the CW Skimmer engine and require a WSJT-X fork.
- **#72 SmartLink** — architectural, and needs a private repo to hold
  ClientID and secrets before any code is written.
- **#48 help/reporting doc** — not a slate item; refresh it at publish
  time to cover whatever actually ships.
