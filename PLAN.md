# Implementation plan

Working notes for the next beta release. For the architecture this plan applies to, see [ARCHITECTURE.md](ARCHITECTURE.md).

## Current state

- **Branch.** `main`.
- **Last shipped beta.** v0.1.18b (2026-05-11).
- **Working version.** csproj `<Version>` is `0.1.0` (clean numeric default). Release version comes from the git tag at HEAD per [publish-release.ps1](publish-release.ps1). Next beta cuts as `v0.1.19b`.
- **Status:** ready to cut v0.1.19b once release notes are drafted. All blocking items below are done; #36 deferred to v0.1.20b.

## What landed since v0.1.18b (the v0.1.19b feature set)

- **[#34](https://github.com/cdub89/SmartStreamer4/issues/34) — `%APPDATA%` folder rename to `SmartStreamer4`.** Atomic single-release migration via `Directory.Move` (`6dd68ba`), with `RuntimePathResolver` split-brain fix for the locked-fallback path (`ca409e9`).
- **[#35](https://github.com/cdub89/SmartStreamer4/issues/35) — release-pipeline SHA256SUMS handling.** `cd19ed5` keeps the SHA256 line out of the pre-release commit. Validates end-to-end on the v0.1.19b cut.
- **[#37](https://github.com/cdub89/SmartStreamer4/issues/37) — Setup Wizard link visibility.** Config tab link restyled bold + DarkOrange (`45b592f`).
- **[#38](https://github.com/cdub89/SmartStreamer4/issues/38) — MME audio-index change detection.** Auto-warn on SmartSDR/DAX upgrades that shift device indices, with one-click Set Up Wizard from the dialog (auto-stops Skimmer for convenience). `45b592f`. WDM detection ruled out as infeasible — CW Skimmer's WDM list is opaque to outside-the-app enumeration.
- **[#40](https://github.com/cdub89/SmartStreamer4/issues/40) — multi-station pan/slice mixup at Skimmer launch.** Fixed launcher to filter pan + slice by the connected station (`04fb2aa`). Eliminates wrong-frequency-at-startup for operators running Maestro + SmartSDR-Windows on the same radio.
- **[#39](https://github.com/cdub89/SmartStreamer4/issues/39) — multi-station DAX-IQ attribution + station-mismatch detection.** Fixed UI attribution leak (`PanadapterInfo` / `DaxIQStreamInfo` now carry `ClientHandle`; five station-blind `FirstOrDefault` joins filtered by `(channel, handle)`). Added soft-warning detection: when our connected station and another both hold the same DAX-IQ channel, a modal "DAX-IQ Channel Conflict" dialog at launch and a `[STREAMER]` log line at connect / pan-change ask the operator to verify DAX-the-app's selection. FlexLib does not expose DAX's bind state, so the warning is operator-confirmed not programmatically enforced. `997d79e`.
- **Diagnostic logging.** `Clients on …:` line on connect (`c21d112`) for multi-station troubleshooting. CW Skimmer launch line now shows runtime-effective MME or WDM indices and the active mode (`6a5ab3e`).
- **Comment audit.** Five stale comments fixed (DirectSoundProbe header, CwSkimmerLauncher diagnostic labels, CwSkimmerLauncher heartbeat claim, ICwSkimmerLauncher heartbeat claim, CwSkimmerIniModelFactory WDM enumeration claim). Part of `45b592f`.

## Open issues for the v0.1.19b cut

### 1. Triage [#30](https://github.com/cdub89/SmartStreamer4/issues/30) (Maestro-C discovery on 4.2)

Steve AI9T's Maestro-C is not appearing in Network Discover on SmartSDR 4.2 while SmartSDR/DAX work normally. Root cause unknown. The `Clients on …:` connect-time log added in `c21d112` is the next diagnostic step — Steve installs v0.1.19b, runs against his rig, and shares the log so we can see what FlexLib reports for GUI clients on his radio. Document in the v0.1.19b release notes as "still investigating, please report logs."

### 2. Draft release notes

`RELEASE_NOTES-v0.1.19b.md` at repo root (gitignored per CLAUDE.md). Lead with #40 (multi-station fix — most operators with multi-station rigs were silently affected) and #38 (auto-warn on audio device shifts). Mention the #34 folder rename so testers know about the one-time migration. Call out the still-open #30 with a request for Steve's log.

## Deferred

- **[#36 in-place update via Velopack](https://github.com/cdub89/SmartStreamer4/issues/36) — deferred to v0.1.20b.** Decision recorded 2026-05-18: Velopack chosen over roll-our-own. Design and scaffold breakdown captured in the [PLAN comment on #36](https://github.com/cdub89/SmartStreamer4/issues/36#issuecomment-4481564662). Has open research questions that warrant a focused session before any code.

## Deferred to post-beta

### Phase 2: slim `MainWindowViewModel.cs` (currently ~2200 lines)

Independent extractions, one PR each:

| Extract | Owns | Approx. lines |
|---------|------|---------------|
| `UpdateCheckCoordinator` | polling loop, last-announced-tag state, manual / auto check entrypoints | 120 |
| `SpotColorPalette` | the two `SpotColorOptions` lists and selection sync | 80 |
| `EchoSuppressionState` | `_lastOutboundQsyByChannel` / `_lastInboundClickByChannel`, `ShouldSuppressInboundClick`, `RecordOutboundQsy` | 100 |
| `RitSyncCoordinator` | `_ritStatusEmitter` plus RIT change tracking and last-RIT-state map | 80 |
| `DaxKbpsCalculator` | `UpdateDisplayedDaxKbps` fallback math | 30 |
| `SkimmerStatusFormatter` | `FormatLaunchSuccess` / `FormatDeviceNotFound` plus device-preview status | 50 |

Realistic landing: ViewModel drops to ~1500 lines. Still big, but genuinely "owns the bindable UI state and command surface."

### Phase 3: tighten error visibility (remaining items)

A single `Action<string> reportError` (or use existing `AddStreamerStatus`) plumbed into:

- [AppSettingsStore.Load/Save](AppSettingsStore.cs). Currently `catch { /* … */ }`.
- [MainWindow.OnOpenSupport](MainWindow.axaml.cs). Currently `catch { }`.
- [WdmAudioDeviceFinder.Enumerate*](src/SmartSDRIQStreamer.CWSkimmer/WdmAudioDeviceFinder.cs). Currently swallows the WinMM count call failure.

### Phase 4: test coverage gaps

- [CwSkimmerWorkflowService.IsLikelyCallsign](CwSkimmerWorkflowService.cs). Pin the `0x32F4599F` rejection plus slash / hyphen acceptance.
- [ReleaseUpdateService.CompareTags](ReleaseUpdateService.cs). Pin `alpha < beta < rc < stable` ordering plus numbered suffixes.
- [FlexLibRadioDiscovery.ResolveStations](src/SmartSDRIQStreamer.FlexRadio/FlexLibRadioDiscovery.cs). Dedup + sort behaviour.

### Bug: `UpdatePan` station-blind LO sync (surfaced by #39 live test)

`MainWindowViewModel.UpdatePan` at the `TrySyncSkimmerForPanChange` call (~line 800) fires for *any* station's pan update without filtering on the connected station, so a foreign station's pan update emits a sync with the foreign LO. Live trace 2026-05-18 showed `LO 7057936 Hz` (WX7V 40m pan) pushed while CW Skimmer was launched for Maestro's 20m. Telnet auth failed in that trace so the bad LO never reached Skimmer, but the bug is real: with telnet working, Skimmer would shift to the wrong band on any foreign-pan update. Independent of #39; not a v0.1.19b blocker because #39's launch dialog catches the primary symptom and the same-station case is unaffected. Fix: filter the sync forward by pan ownership.

### Open enhancement issues

- [#28 FT8 / WSJTX layout support](https://github.com/cdub89/SmartStreamer4/issues/28). UI work, broader scope than a beta point release.
- [#36 in-place update](https://github.com/cdub89/SmartStreamer4/issues/36). See §4 above; track here if cut from v0.1.19b.

## Sequencing recommendation

1. `CwSkimmerIniModelFactory.IsCalibrated` doc/code drift at [line 152-154](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerIniModelFactory.cs#L152-L154): comment says we accept WDM-or-MME, code only accepts WDM. Decide which is right and align both. If MME is accepted, pin with a test in [CwSkimmerIniTests.cs](tests/SmartSDRIQStreamer.CWSkimmer.Tests/CwSkimmerIniTests.cs). Small PR.
2. Cut v0.1.19b. Validation of the SHA256SUMS-on-release pipeline (#35) happens as part of the cut.
3. Triage #30 with Steve's logs from v0.1.19b. Fix on a branch if reproducible.
4. Phase 2 / 3 / 4 / `UpdatePan` LO sync bug / #28 / #36 in any order after the beta.
