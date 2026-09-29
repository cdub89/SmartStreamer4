# Architecture

SmartStreamer4 is an Avalonia desktop app (.NET 8 / `net8.0-windows`) that sits between a FlexRadio and the operator's decoding software. In CW Mode it bridges DAX-IQ streams into one or more CW Skimmer instances and reconciles CW Skimmer's spots back to the radio; in Digital Mode it provisions and launches WSJT-X, JTDX, or WSJT-Z per slice. This document describes the implementation as it stands. For deferred and planned work, see [PLAN.md](PLAN.md).

## Goal

The operator picks one mode on the Launch tab ([AppMode](AppMode.cs): `Cw` or `Digital`). The modes are exclusive: one family at a time.

**CW Mode**, per DAX-IQ channel:

- Launch a CW Skimmer process with a channel-specific INI.
- Connect a telnet client to that CW Skimmer instance.
- Push panadapter centre (`SKIMMER/LO_FREQ`) and slice frequency (`SKIMMER/QSY`) into CW Skimmer whenever they change in SmartSDR.
- Forward decoded spots (`DX de ...`) back to the radio so they appear on the panadapter.
- Tune the radio's slice when the operator clicks a callsign in CW Skimmer.

**Digital Mode**, per slice:

- Seed a per-instance engine config bound to that slice's DAX RX audio channel, SmartSDR CAT TCP port, and UDP reporting port.
- Launch the engine with its own `--rig-name` so several slices run side by side.

The app coexists with SmartSDR, DAX, and SmartSDR CAT (it does not replace any of them). It does not own the radio, the audio pipeline, or the decoders' UIs; it owns the wiring between them.

## Module layout

Projects are listed in [SmartStreamer4.sln](SmartStreamer4.sln).

| Project | Path | Responsibility |
|---------|------|----------------|
| `SmartSDRIQStreamer` (app) | repo root | Avalonia App, MainWindow, SmartDeck, ViewModels, wizards, settings, workflow services, composition. Output: `SmartStreamer4.exe`. |
| `SmartSDRIQStreamer.FlexRadio` | [src/SmartSDRIQStreamer.FlexRadio/](src/SmartSDRIQStreamer.FlexRadio/) | FlexLib isolation. Exposes `IRadioDiscovery` and `IRadioConnection`; FlexLib types do not leak through. |
| `SmartSDRIQStreamer.CWSkimmer` | [src/SmartSDRIQStreamer.CWSkimmer/](src/SmartSDRIQStreamer.CWSkimmer/) | CW Skimmer adapter: INI generation, process launch, telnet, sync tracker, audio device enumeration, runtime paths, log rotation. |
| `SmartSDRIQStreamer.Digital` | [src/SmartSDRIQStreamer.Digital/](src/SmartSDRIQStreamer.Digital/) | Digital engine adapter: engine definitions, config templates and provisioning, CAT settings reader and port probe, profile harvester, launcher. |
| `SmartSDRIQStreamer.CWSkimmer.Tests` | [tests/SmartSDRIQStreamer.CWSkimmer.Tests/](tests/SmartSDRIQStreamer.CWSkimmer.Tests/) | xUnit tests for the CWSkimmer module. |
| `SmartSDRIQStreamer.Digital.Tests` | [tests/SmartSDRIQStreamer.Digital.Tests/](tests/SmartSDRIQStreamer.Digital.Tests/) | xUnit tests for the Digital module. |
| `SmartSDRIQStreamer.App.Tests` | [tests/SmartSDRIQStreamer.App.Tests/](tests/SmartSDRIQStreamer.App.Tests/) | xUnit tests for root-project services, helpers, and the SmartDeck ViewModel. |

Diagnostic helpers under [tools/](tools/), not shipped with the app:

- [WinMMEnum](tools/WinMMEnum/) prints the Windows WinMM capture device list.
- [Test-WsjtxUdpPorts.ps1](tools/Test-WsjtxUdpPorts.ps1) listens on the per-slice UDP ports and reports which engine instance is sending on each, to catch cross-talk between concurrent Digital Mode instances.

The vendor FlexLib source (`FlexLib_API_v4.2.20.41343/`, issue #61) sits at the repo root and is project-referenced by the FlexRadio project. It is gitignored and downloaded separately by the developer.

## Composition root

[AppServices.cs](AppServices.cs) is a single sealed class that wires the dependency graph at startup. There is no DI framework. New services should be added there, not via service-locator patterns elsewhere.

```text
AppSettingsStore
  └─ AppSettingsSession  ┐
FlexLibRadioDiscovery    │
FlexLibRadioConnection   │
WdmAudioDeviceFinder     ├── MainWindowViewModel (7 constructor arguments)
ReleaseUpdateService     │
DigitalAppLauncher       │
CwSkimmerLauncher        ┘
  ├─ CwSkimmerIniModelFactory (IAudioDeviceFinder)
  ├─ CwSkimmerIniWriter
  ├─ IAudioDeviceFinder
  └─ () => CwSkimmerTelnetClient   (factory: one per channel)
```

`MainWindowViewModel` also takes an optional `ICatPortProbe` (defaulting to `TcpCatPortProbe`) so tests can substitute it. It constructs [CwSkimmerWorkflowService](CwSkimmerWorkflowService.cs) and the per-slice Digital row ViewModels itself.

## FlexRadio integration

Discovery uses FlexLib's local-network UDP broadcast listener (UDP 4992) wrapped behind [IRadioDiscovery](src/SmartSDRIQStreamer.FlexRadio/IRadioDiscovery.cs). Connection wraps FlexLib's `Radio` behind [IRadioConnection](src/SmartSDRIQStreamer.FlexRadio/IRadioConnection.cs), which exposes, by category:

- **Connection lifecycle.** `ConnectAsync`, `Disconnect`, `ConnectionStateChanged`, radio identity (model, nickname, serial, versions), our own client handle and station.
- **Live snapshots and change events.** Panadapters, slices, DAX-IQ streams, and GUI clients (`GuiClients`, surfaced on the Logs tab as a multi-station aid), each with added / removed / updated events.
- **CW Skimmer bridging.** `RequestDaxIQStreamAsync`, `StopDaxIQStreamAsync`, `SetSliceFrequencyAsync`, `PublishSpotAsync`.
- **Slice and panadapter controls** (SmartDeck). Mode, RIT / XIT, AGC threshold, APF / NR / NB / diversity, RX and TX antennas, panadapter RF gain.
- **Transmit.** Power setting and rated maximum (`RfPowerWatts`, `MaxRfPowerWatts`, `SetRfPowerAsync`), keyed state (`IsTransmitting`, `TransmitStateChanged`). `ControlStation` binds the connection to the selected station's GUI client so client-scoped state such as transmit power is readable (issue #64).
- **Telemetry.** A coalesced `RadioTelemetryInfo` snapshot and `TelemetryChanged`, published only between `StartTelemetry` and `StopTelemetry`. Subscription is on demand so a session that never opens SmartDeck does no coalescing work.
- **Network and diagnostics.** `AvgDAXKbps`, `NetworkStatus` (RTT), `ResetNetworkStatus`, `VerboseDiagnostics`, `DiagnosticEvent`.

Why the abstraction: keeping FlexLib types out of the rest of the app makes it possible to test the CW Skimmer module and the app's services without spinning up FlexLib, and isolates breakage when FlexLib evolves (e.g. the 4.1.5 → 4.2.x migration documented in [Flexlib4-2-Migration-Guide.md](Flexlib4-2-Migration-Guide.md)).

The FlexRadio module targets SmartSDR 4.2.x server radios. Support for 4.1.5 and earlier was dropped 2026-08-03.

## CW Skimmer integration

The CWSkimmer module owns everything between "operator clicked Start" and "CW Skimmer is decoding on the right frequency."

### Workflow service

[CwSkimmerWorkflowService](CwSkimmerWorkflowService.cs) (root project) sits between the ViewModel and the launcher. On Start it:

- Guards the multi-station DAX-IQ case (issue #39): when the requested channel is also assigned on another station's panadapter, it asks an `IDaxStationConfirmer` before launching. `MainWindowViewModel` implements it by raising an event that `MainWindow` answers with a dialog; with no confirmer wired, a collision cancels.
- Resolves the panadapter and slice for the selected control station only (issue #40).
- Builds the `CwSkimmerConfig`: paths, delays, telnet callsign, telnet port `AppSettings.TelnetPortBase` (default 7300) + N × 10, initial LO and slice frequency, and the operator's per-channel MME or WDM device index for the selected driver mode.
- Turns the launcher's `LaunchResult` into an operator-facing status line, including specific messages for a missing, folder-shaped, or uncalibrated `cwskimmer.ini` (issues #74, #75) and an unwritable channel INI.

### Per-channel ownership

[CwSkimmerLauncher](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerLauncher.cs) owns one set of resources per DAX-IQ channel:

- The `CwSkimmer.exe` process (one per channel).
- A managed INI at `<artifacts>/cwskimmer/ini/CwSkimmer-ch{N}.ini` (see Runtime artifacts).
- A telnet client connected to `127.0.0.1` on the configured port. If the channel INI already carries a `[Telnet] Port=`, that value wins over the computed one.
- A `CwSkimmerSyncTracker` driving that telnet.
- A telnet-lifecycle `CancellationTokenSource`.

Channel state lives in `Dictionary<int, ...>` maps guarded by a single `_sync` lock. The launcher exposes events (`RunningStateChanged`, `FrequencyClicked`, `SpotReceived`, `TelnetStatusChanged`, `OutboundQsyEmitted`) that the ViewModel subscribes to.

### INI generation

On first launch of a channel, the launcher copies the operator's master `CwSkimmer.ini` to the channel INI (tiling the window position for channels above 1), then [CwSkimmerIniWriter](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerIniWriter.cs) replaces the app-owned `[Audio]` and `[Telnet]` sections and preserves every other section. The writer runs only when the channel INI does not yet exist, so the whole managed INI is written once; after that, operator edits made in CW Skimmer survive subsequent launches.

[CwSkimmerIniModelFactory](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerIniModelFactory.cs) reads the master INI's `[Audio]` calibration, then builds the per-channel model. The driver family comes from `AppSettings.SkimmerSoundcardDriverMode`, set by the Set Up Wizard:

- **MME (default).** Per-channel `MmeSignalDev` comes from the wizard's `MmeDeviceIndexCh{N}` when set; otherwise it is looked up as `DAX IQ {N}` (DAX v2) or `DAX IQ RX {N}` (DAX v1) in the live WinMM capture list; otherwise it falls back to the master's index plus the channel offset. `MmeAudioDev` (local speakers / headphones) is copied verbatim.
- **WDM (operator opt-in).** Requires driver mode `WDM`, a wizard-supplied 1-based `WdmDeviceIndexCh{N}`, and WDM audio calibration in the master INI. The channel INI is then written with `UseWdm=1` and that index. WDM cannot be auto-derived because CW Skimmer's WDM enumeration does not match WinMM or DirectSound order (see [issue #19](https://github.com/cdub89/SmartStreamer4/issues/19)). Without all three the channel stays MME.

Each launch also writes a device-enumeration snapshot to `device-diagnostic.txt` beside the channel INIs.

### Telnet

[CwSkimmerTelnetClient](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerTelnetClient.cs) speaks CW Skimmer's telnet protocol:

- Outbound: `SKIMMER/LO_FREQ <hz>`, `SKIMMER/QSY <khz>`.
- Inbound: spot lines (`DX de <call>: <freq> <comment>`), click-tune lines (`Clicked on "<call>" at <freq_khz>`), login prompts.

Some CW Skimmer configurations send no session-ready banner, so a login timeout after credentials are sent is treated as success. That case is surfaced as a status line on the Logs tab. The client's own trace (`LogDiag`) goes to `cwskimmer-telnet-client.log`.

### Sync tracker

[CwSkimmerSyncTracker](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerSyncTracker.cs) is the model-bearing component. Every source of frequency change (panadapter event, slice event, RIT change, startup) calls `RequestSync(loHz?, vfoMHz?)`. A single background loop coalesces requests over a 50 ms window, then emits only what changed against last-sent.

Three properties:

1. **Idempotent.** Repeated `RequestSync` calls with the same values produce no telnet traffic.
2. **LO before VFO in one iteration.** Within a single coalesced wakeup, `SKIMMER/LO_FREQ` is emitted first, then `SKIMMER/QSY`. CW Skimmer's IQ pipeline rebuild sees the new LO before any QSY in that context.
3. **Post-LO VFO invalidation.** After a successful LO emit, `_lastSentVfoMHz` is cleared so a stale QSY (sent against the previous LO context before the panadapter recenter event arrived) is re-asserted on the next iteration.

On every slice event the ViewModel's `TrySyncSliceToSkimmer` passes the current pan centre alongside the effective RX frequency to `CwSkimmerLauncher.RequestSkimmerSync`, which forwards to that channel's tracker ([MainWindowViewModel.cs](MainWindowViewModel.cs)). Property #3 plus that pan centre covers both halves of the FlexLib slice / pan event ordering race.

## Digital Mode integration

The Digital module is setup-and-launch only: no telnet, sync, or spot brokering. Design record: [docs/design/PLAN-issue28-multimode.md](docs/design/PLAN-issue28-multimode.md).

- **Engines.** [DigitalEngines](src/SmartSDRIQStreamer.Digital/DigitalEngine.cs) defines WSJT-X, JTDX, and WSJT-Z by display name, exe path, and `%LOCALAPPDATA%` config root. WSJT-Z is a WSJT-X fork that reads the WSJT-X config root, so it reuses the WSJT-X template.
- **Launch.** [DigitalAppLauncher](src/SmartSDRIQStreamer.Digital/DigitalAppLauncher.cs) runs one process per slice as `exe --rig-name=Slice<letter>`, keyed by rig name, so several slices run at once. Process start sits behind `IDigitalProcessRunner` for tests.
- **Config provisioning.** [DigitalConfigProvisioner](src/SmartSDRIQStreamer.Digital/DigitalConfigProvisioner.cs) writes `%LOCALAPPDATA%\<ConfigRoot> - <rig>\<ConfigRoot> - <rig>.ini`. The first run seeds it from the bundled template ([Templates/](src/SmartSDRIQStreamer.Digital/Templates/)); later runs start from the existing file so the engine's own saved state survives. Either way only the operator identity and per-slice binding keys (call, grid, rig, DAX RX sound-in, DAX TX sound-out, `CATNetworkPort`, `UDPServerPort`) are set, through the preservation-first [IniEditor](src/SmartSDRIQStreamer.Digital/IniEditor.cs), which leaves Qt `@Variant` values untouched.
- **CAT discovery and gate.** [CatSettingsReader](src/SmartSDRIQStreamer.Digital/CatSettingsReader.cs) reads SmartSDR CAT's `CAT.settings` to find the TCP port serving each slice. Before launch, [TcpCatPortProbe](src/SmartSDRIQStreamer.Digital/CatPortProbe.cs) connects to that loopback port and blocks the start with a hint naming the fix if nothing is listening (issue #66).
- **Profile harvest.** [WsjtxProfileHarvester](src/SmartSDRIQStreamer.Digital/WsjtxProfileHarvester.cs) reads an existing engine config's FlexRadio profile (call, grid, rig) to prepopulate the Digital Config tab. It never feeds provisioning directly.

## SmartDeck

SmartDeck (issue #59) is a separate window for radio telemetry and slice controls, opened from a button on the main window's tab strip while connected. Design record: [docs/design/PLAN-issue59-smartdeck.md](docs/design/PLAN-issue59-smartdeck.md).

- [SmartDeckWindow](SmartDeckWindow.axaml.cs) owns the shell: position and size restore, the always-on-top toggle, and calling the ViewModel's `Start` / `Stop` on open and close.
- [SmartDeckViewModel](SmartDeckViewModel.cs) owns value formatting and the control surface, and calls `StartTelemetry` / `StopTelemetry` on the connection, so telemetry runs only while the window is open. Controls target an explicitly selected slice, not the radio's active slice.
- Helpers: [BandMemory](BandMemory.cs) captures and restores per-band slice state, [WheelNotchCounter](WheelNotchCounter.cs) turns wheel deltas into one step per detent (issue #65), [DeckOption](DeckOption.cs) is one lit-or-not grid button.

## UI shell

The root project hosts the Avalonia UI:

- [App.axaml](App.axaml) / [App.axaml.cs](App.axaml.cs): application-level setup and theme.
- [MainWindow.axaml](MainWindow.axaml) / [MainWindow.axaml.cs](MainWindow.axaml.cs): tabs Launch, CW and Config (shown when `IsCwMode`), Digital and Config (shown when `IsDigitalMode`), Logs, Help. The SmartDeck button and theme toggle sit on the tab strip.
- [MainWindowViewModel.cs](MainWindowViewModel.cs): orchestration for both modes (about 3,170 lines).
- [SliceViewModel.cs](SliceViewModel.cs): per-slice row on the CW tab.
- [DigitalOperatingRowViewModel.cs](DigitalOperatingRowViewModel.cs) / [DigitalSliceConfigViewModel.cs](DigitalSliceConfigViewModel.cs): per-slice rows on the Digital and Digital Config tabs.
- [SetupWizardWindow](SetupWizardWindow.axaml.cs): in-app Setup Guide viewer (opened from the Help tab); renders [SETUP_GUIDE.md](SETUP_GUIDE.md) as an embedded resource. Despite the name, it is not the CW wizard.
- [ResetSkimmerWizardWindow](ResetSkimmerWizardWindow.axaml.cs): the four-step CW Skimmer Set Up Wizard, opened from the CW Config tab. It records the driver mode and per-channel MME / WDM indices.
- [ThrottledStatusEmitter.cs](ThrottledStatusEmitter.cs) / [FooterStatusBuffer.cs](FooterStatusBuffer.cs): rate-limit status writes to the Logs tab and footer.

Avalonia compiled bindings are on by default (`AvaloniaUseCompiledBindingsByDefault=true`); reflection-based `Path=` bindings should not be used.

## Settings persistence

[AppSettings](AppSettings.cs) is the persisted shape: last mode, CW Skimmer paths, callsign, telnet base port and cluster toggle, launch delays, spot defaults, driver mode and per-channel MME / WDM indices (plus last-seen MME indices for change detection), Digital engine paths, active engine, identity and per-slice bindings, theme, update-check settings, main window and SmartDeck placement, SmartDeck always-on-top and band memory. [AppSettingsStore](AppSettingsStore.cs) serializes it to `%AppData%\SmartStreamer4\settings.json` via `System.Text.Json`. [AppSettingsSession](AppSettingsSession.cs) loads once at startup and saves once at shutdown. The folder was renamed from `SDRIQStreamer` to `SmartStreamer4` (issue #34); [AppDataPaths](AppDataPaths.cs) migrates the legacy folder via `Directory.Move`, falling back to the legacy folder for the session if the move fails.

## Runtime artifacts

`<artifacts>` is `<repo>/artifacts/` when the app runs inside a checkout ([RuntimePathResolver](src/SmartSDRIQStreamer.CWSkimmer/RuntimePathResolver.cs) finds `SmartSDRIQStreamer.csproj` above the binary or working directory), and `%APPDATA%\SmartStreamer4\artifacts\` otherwise.

| Path | Owner | Purpose |
|------|-------|---------|
| `%AppData%\SmartStreamer4\settings.json` | `AppSettingsStore` | Persisted user settings. |
| `<artifacts>/cwskimmer/ini/CwSkimmer-ch{N}.ini` | `CwSkimmerLauncher` | Managed per-channel CW Skimmer INI. |
| `<artifacts>/cwskimmer/ini/device-diagnostic.txt` | `CwSkimmerLauncher` | Snapshot of audio device enumeration written on every launch. |
| `<artifacts>/logs/streamer-status.log` | `MainWindowViewModel` | Append-only status log. |
| `<artifacts>/logs/spot-publish.log` | `MainWindowViewModel` | Per-spot publish results. |
| `<artifacts>/logs/cwskimmer-telnet-client.log` | `CwSkimmerTelnetClient` | Telnet connect, login, and TX / RX trace. |
| `%LOCALAPPDATA%\<ConfigRoot> - <rig>\` | `DigitalConfigProvisioner` | Per-slice WSJT-X / JTDX / WSJT-Z config. |
| `artifacts/release/SHA256SUMS.txt` | release pipeline | Frozen legacy hash snapshot (tracked). |

At startup each log is rotated to `<name>.old` if it exceeds 10 MB ([LogFiles](src/SmartSDRIQStreamer.CWSkimmer/LogFiles.cs)). The repo's `artifacts/` tree is gitignored except for `release/SHA256SUMS.txt`.

## Threading

- **UI thread.** Avalonia dispatcher. All bindable property writes happen here.
- **FlexLib and connection events.** Raised on the thread pool. Subscribers marshal to the UI thread themselves (`MainWindowViewModel` via `Dispatcher.UIThread.Post`; `SmartDeckViewModel` through an injectable post delegate).
- **Per-channel sync tracker loops.** One background loop per active channel. Idempotent; safe to interrupt at any wakeup boundary.
- **Telnet client.** One read loop per channel. Emits `FrequencyClicked` / `SpotReceived` / `StatusChanged` events. Diagnostic log writes drain through a single background channel.
- **Update checker.** A `PeriodicTimer` loop in `MainWindowViewModel` calls [ReleaseUpdateService](ReleaseUpdateService.cs) (GitHub releases API) and drives the update banner through `IsUpdateAvailable`.

State that crosses threads (launcher channel maps, tracker desired-state) is guarded by single per-component locks (`_sync`, `_gate`).

## Testing

- [CWSkimmer.Tests](tests/SmartSDRIQStreamer.CWSkimmer.Tests/): INI generation (calibration, MME / WDM selection, overrides, preserved sections), sync tracker behaviour, log rotation, runtime path resolution.
- [Digital.Tests](tests/SmartSDRIQStreamer.Digital.Tests/): CAT port probe, CAT settings reader, digital launcher, config provisioner, profile harvester.
- [App.Tests](tests/SmartSDRIQStreamer.App.Tests/): settings round-trip, AppData paths, workflow service and launch messages, DAX channel ownership and pan attribution, audio index change detection, release update evaluation, radio telemetry and RF power scaling, SmartDeck ViewModel and toggles, band memory, wheel notch counting, stepped ranges.

Not covered by automated tests:

- FlexLib integration (live radio required).
- CW Skimmer and digital engine process behaviour and CW Skimmer telnet (live processes required).
- Avalonia UI behaviour.

The CLAUDE.md live-radio smoke test gate exists for this reason: unit tests cannot catch sync-tracking or audio-routing regressions.

## Conventions

- **Modernization.** Latest stable APIs. .NET 8 / C# 12 idioms: collection expressions, primary constructors, `required` members, raw string literals, `ArgumentNullException.ThrowIfNull`, `TimeProvider`. Nullable reference types on; no `!` suppressions.
- **Gates.** Build, test, markdown, and live-radio gates and their scope are defined in [CLAUDE.md](CLAUDE.md).
- **No em dashes in user-facing prose.** MessageBox text, status bar messages, dialog labels, release notes, and in-app help use periods, commas, or parentheses. CLAUDE.md and code comments are exempt.
- **ViewModel, not VM.** Spell it out in code review and discussion.
