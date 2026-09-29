# Plan

Deferred and planned work. GitHub issues are the source of truth; this
file indexes them and records deferred items that have no issue yet.
For how the code is built, see [ARCHITECTURE.md](ARCHITECTURE.md).

## Current state

- **Latest release:** v0.3.3 (GA, 2026-09-27). Previous: v0.3.1
  (2026-08-06).
- **Next release:** TBD. Nothing is scheduled.

## Planned

Roadmap and sequencing: **TBD**. No target versions are assigned.

- [#87](https://github.com/cdub89/SmartStreamer4/issues/87) Update
  Available banner / SmartDeck label placement. Implemented, awaiting
  live confirmation.
- [#84](https://github.com/cdub89/SmartStreamer4/issues/84) Digital
  provisioner tests write to the real `%LOCALAPPDATA%`; inject a config
  root so they run sandboxed. Implemented, awaiting confirmation.
- [#48](https://github.com/cdub89/SmartStreamer4/issues/48) How to
  report issues or get help (documentation).

## Deferred: open issues

- [#62](https://github.com/cdub89/SmartStreamer4/issues/62) App startup
  persistence. Phase A (auto-connect on launch) is fully specified in
  the issue; Phase B (auto-resume mode and stream) follows it.
- [#71](https://github.com/cdub89/SmartStreamer4/issues/71) Decode the
  VITA-49 stream directly instead of DAX. A research program, not a
  single change.
- [#72](https://github.com/cdub89/SmartStreamer4/issues/72) SmartLink
  support. Prototype; needs a private home for credentials before any
  code.
- [#86](https://github.com/cdub89/SmartStreamer4/issues/86) Avalonia
  headless window-open smoke test. Revisit on the triggers named in the
  issue.

## Deferred: no issue yet

Verified still open on 2026-09-29. Open an issue before starting any of
these.

### Code health

- Slim `MainWindowViewModel.cs` (about 3,170 lines). Candidate
  extractions: update-check coordination, spot color palette, echo
  suppression state, RIT sync, DAX kbps math, Skimmer status
  formatting.
- Replace swallowed exceptions with a reported error in
  `AppSettingsStore` Load/Save, `MainWindow.OnOpenSupport`, and the
  WinMM device-count calls in `WdmAudioDeviceFinder`.
- Tests for `CwSkimmerWorkflowService.IsLikelyCallsign` and
  `FlexLibRadioDiscovery.ResolveStations` (both private today).

### Digital Mode

From the [#28 design record](docs/design/PLAN-issue28-multimode.md):

- JTDX single-instance parity live test.
- Multi-instance live test (up to four slices on a FLEX-6600).
- Logs scoped per mode / engine.
- Assign `Slice.DAXChannel` through FlexLib (nothing writes it today).
- Setup hub cards.
- Share the audio-device finder between CW and Digital.

### SmartDeck

From the [#59 design record](docs/design/PLAN-issue59-smartdeck.md):

- Diversity controls.
- CWX "send CW".
- Skin decision (Fluent vs Stream Deck look).

### Housekeeping

- Regenerate the FlexLib API docs repo against 4.2.20.

## Closed, reopen on demand

- [#36](https://github.com/cdub89/SmartStreamer4/issues/36) In-place
  update via Velopack. Closed as deferred 2026-05-18.
- [#85](https://github.com/cdub89/SmartStreamer4/issues/85) Update check
  takes the first publishable release instead of the highest version.
  Closed as deferred 2026-09-27; reopen on a hotfix to an older line or a
  re-created release. The fix is recorded in the issue.

## Declined unless a user asks again

Operator decision, 2026-09-20. None is blocked; each lacks a user
asking for it. A fresh request reopens the question.

- [#69](https://github.com/cdub89/SmartStreamer4/issues/69) remaining
  scope: bar meters, auto-select active slice, mute slice.
- [#68](https://github.com/cdub89/SmartStreamer4/issues/68) Rotator
  widget.
- [#73](https://github.com/cdub89/SmartStreamer4/issues/73)
  Click-to-zero on the offset readout.
