# Implementation plan

Working notes for the next beta release. For the architecture this plan applies to, see [ARCHITECTURE.md](ARCHITECTURE.md).

## Current state

- **Branch.** `main`.
- **Last shipped beta.** v0.1.18b (2026-05-11).
- **Working version.** csproj `<Version>` is `0.1.0` (clean numeric default). Release version comes from the git tag at HEAD per [publish-release.ps1](publish-release.ps1). Next beta cuts as `v0.1.19b`.

## Beta-blocking before v0.1.19b

### 1. Finish [#35](https://github.com/cdub89/SmartStreamer4/issues/35) — release-pipeline tail commit

`cd19ed5` stops the SHA256 line from being committed before the release is published, which removes the "1 commit ahead of release" footgun on the v0.1.18b release page. Validate end-to-end by cutting v0.1.19b through the new flow and confirming the tag at HEAD matches the release tag on `gh release view`, with no trailing SHA256SUMS commit.

### 2. Triage open issue [#30](https://github.com/cdub89/SmartStreamer4/issues/30) (Maestro-C)

v0.1.17b works with SmartSDR / DAX but not Maestro-C, per Steve AI9T. v0.1.18b has not been reconfirmed with Maestro-C. Either reproduce and fix, or document as a known limitation in the v0.1.19b release notes. We cannot ship another beta as a silent regression on top of an unconfirmed regression.

### 3. Remaining `CwSkimmerIniModelFactory` doc/code drift

In [CwSkimmerIniModelFactory.cs](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerIniModelFactory.cs), the comment at lines 147-149 says we accept the calibration if WDM fields are present OR if `MmeAudioDev` is set, but the `return` on line 150 only checks the WDM pair (`wdmIQ1 >= 0 && wdmAudio >= 0`). Decide which is right for post-pivot MME calibrations and align both. If MME is in fact accepted, pin it with a test in [CwSkimmerIniTests.cs](tests/SmartSDRIQStreamer.CWSkimmer.Tests/CwSkimmerIniTests.cs).

### 4. Decide on [#34](https://github.com/cdub89/SmartStreamer4/issues/34) and [#36](https://github.com/cdub89/SmartStreamer4/issues/36) scope for v0.1.19b

Both are post-v0.1.18b feedback items.

- **[#34 Rename `%APPDATA%\SDRIQStreamer\` → `%APPDATA%\SmartStreamer4\`](https://github.com/cdub89/SmartStreamer4/issues/34).** Single-release atomic rename via `Directory.Move` on first launch of v0.1.19b, performed in `Program.Main` before any settings load or log file open. If the legacy folder is locked (AV scanner, Explorer handle), fall back to it for the session and retry next launch. Migration outcome is surfaced to the Logs tab. No two-folder state, no marker file, no v0.1.20b follow-up. Include in v0.1.19b.
- **[#36 Install update in place](https://github.com/cdub89/SmartStreamer4/issues/36).** Currently we surface "update available"; the ask is a download + install + relaunch flow. Larger scope (download verification, unpack-over-running-exe handling, restart). Recommend defer past v0.1.19b unless the implementation lands cleanly.

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

### Open enhancement issues

- [#28 FT8 / WSJTX layout support](https://github.com/cdub89/SmartStreamer4/issues/28). UI work, broader scope than a beta point release.
- [#36 in-place update](https://github.com/cdub89/SmartStreamer4/issues/36). See §4 above; track here if cut from v0.1.19b.

## Sequencing recommendation

1. Validate the new SHA256SUMS-on-release pipeline end-to-end on the v0.1.19b cut (#35).
2. Triage #30. If reproducible, fix on a branch off `main`.
3. `CwSkimmerIniModelFactory` second doc-drift bullet plus its test, in one small PR.
4. #34 atomic rename in its own commit, with a stand-alone test for the migration logic (fresh / legacy-only / already-migrated / both-present / locked).
5. Cut v0.1.19b.
6. Phase 2 / 3 / 4 / #28 / #36 in any order after the beta.
