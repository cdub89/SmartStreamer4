# Implementation plan

Working notes for the next beta release. For the architecture this plan applies to, see [ARCHITECTURE.md](ARCHITECTURE.md).

## Current state

- **Branch.** `fix/skimmer-resync-after-band-change`
- **Last shipped beta.** v0.1.17b (2026-05-02). Field-validated as the best beta to date; sync tracking spot-on across SmartSDR 4.1.5 and 4.2.
- **Working version.** `0.1.17-b` in [SmartSDRIQStreamer.csproj](SmartSDRIQStreamer.csproj). Next beta cuts as `v0.1.18b`.

The CW Skimmer cross-band resync fix shipped on this branch in commit `0e618c7`: Layer 1 in [CwSkimmerSyncTracker](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerSyncTracker.cs) (post-LO VFO invalidation) plus Layer 2 in [MainWindowViewModel.TrySyncSliceToSkimmer](MainWindowViewModel.cs) (pass current pan centre on every slice change). Covered by [CwSkimmerSyncTrackerTests](tests/SmartSDRIQStreamer.CWSkimmer.Tests/CwSkimmerSyncTrackerTests.cs).

## Beta-blocking before next release

### 1. Triage open issue #30 (Maestro-C)

[Issue #30](https://github.com/cdub89/SmartStreamer4/issues/30) reports that v0.1.17b works with SmartSDR / DAX but not Maestro-C. Either reproduce and fix, or document as a known limitation in the v0.1.18b release notes. We can't ship the next beta as a regression on top of an unconfirmed regression.

### 2. Code-side doc drift (caught during audit)

All in [CwSkimmerIniModelFactory.cs](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerIniModelFactory.cs):

- Class doc comment ([lines 5-22](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerIniModelFactory.cs#L5)) claims "Generated channel INIs always set UseWdm=false." The code at [line 74](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerIniModelFactory.cs#L74) supports operator-supplied WDM and writes `UseWdm=true` when present. Update the comment to match.
- Comment at [lines 139-141](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerIniModelFactory.cs#L139) says "We accept the calibration if WDM fields are present (legacy users) OR if MmeAudioDev is set." The `return` immediately below only checks the WDM path. Decide which is right and align both.
- Misleading test name in [CwSkimmerIniTests.cs](tests/SmartSDRIQStreamer.CWSkimmer.Tests/CwSkimmerIniTests.cs): `Build_AlwaysForcesUseWdmFalse_RegardlessOfMasterIniSetting`. Rename to describe actual behaviour (e.g. `Build_DefaultsToMmeMode_WhenNoOperatorWdmIndex`) and add a paired `Build_EmitsWdmMode_WhenOperatorWdmIndexSupplied`.

### 3. Surface telnet login-timeout-as-success

[CwSkimmerTelnetClient.PerformLoginAsync](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerTelnetClient.cs) treats a missing login banner as success after a short timeout. The branch is logged via `LogDiag` but not surfaced to the Logs tab. Add an `EmitStatus` line so a real login failure is visible to the operator without scraping diagnostic logs.

### 4. Decide on issues #25 and #26

Both paused before v0.1.17b. Cheap to ship if included; safe to defer if cut.

- [#26 Wizard MME / WDM clarity](https://github.com/cdub89/SmartStreamer4/issues/26). Markdown-only edit to the embedded [SETUP_GUIDE_WIZARD.md](SETUP_GUIDE_WIZARD.md) Step 2 section. Low risk, recommend include.
- [#25 Avalonia font rendering](https://github.com/cdub89/SmartStreamer4/issues/25). First attempt was reverted; next try is the FluentTheme resource-key path. Has failed once. Recommend defer unless the FluentTheme path lands cleanly without churn.

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

- [#27 Channel-specific INI and log storage](https://github.com/cdub89/SmartStreamer4/issues/27). Requires layout changes to `artifacts/`.
- [#28 FT8 / WSJTX layout support](https://github.com/cdub89/SmartStreamer4/issues/28). UI work, broader scope than a beta point release.

## Sequencing recommendation

1. Triage #30. If reproducible, fix on a branch off the current branch or off `main` depending on scope.
2. Code-side doc drift fixes plus the `SETUP_GUIDE_WIZARD.md` edit for #26 in one small PR.
3. Telnet login-timeout status surface in its own PR.
4. Cut v0.1.18b.
5. Phase 2 / 3 / 4 / #25 / #27 / #28 in any order after the beta.
