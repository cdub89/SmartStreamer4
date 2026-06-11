# Architecture

SmartStreamer4 is an Avalonia desktop app (.NET 8 / `net8.0-windows`) that bridges FlexRadio's DAX-IQ streams into one or more CW Skimmer instances and reconciles CW Skimmer's spotted signals back to the radio. This document describes the implementation as it stands. For what's planned next, see [PLAN.md](PLAN.md).

## Goal

Per DAX-IQ channel:

- Launch a CW Skimmer process with a generated channel-specific INI.
- Connect a telnet client to that CW Skimmer instance.
- Push panadapter centre (`SKIMMER/LO_FREQ`) and slice frequency (`SKIMMER/QSY`) into CW Skimmer whenever they change in SmartSDR.
- Forward decoded spots (`DX de …`) back to the radio so they appear on the panadapter.
- Tune the radio's slice when the operator clicks a callsign in CW Skimmer.

The app coexists with SmartSDR and DAX (it does not replace either). It does not own the radio, the audio pipeline, or CW Skimmer's UI; it owns the wiring between them.

## Module layout

| Project | Path | Responsibility |
|---------|------|----------------|
| `SmartSDRIQStreamer` (app) | repo root | Avalonia App, MainWindow, ViewModels, wizards, settings, composition. Output: `SmartStreamer4.exe`. |
| `SmartSDRIQStreamer.FlexRadio` | [src/SmartSDRIQStreamer.FlexRadio/](src/SmartSDRIQStreamer.FlexRadio/) | FlexLib isolation. Exposes `IRadioDiscovery` and `IRadioConnection`; FlexLib types do not leak through. |
| `SmartSDRIQStreamer.CWSkimmer` | [src/SmartSDRIQStreamer.CWSkimmer/](src/SmartSDRIQStreamer.CWSkimmer/) | CW Skimmer adapter: INI generation, process launch, telnet, sync tracker, audio device enumeration. |
| `SmartSDRIQStreamer.CWSkimmer.Tests` | [tests/SmartSDRIQStreamer.CWSkimmer.Tests/](tests/SmartSDRIQStreamer.CWSkimmer.Tests/) | xUnit tests for the CWSkimmer module (INI + sync tracker). |
| `WinMMEnum` | [tools/WinMMEnum/](tools/WinMMEnum/) | Diagnostic helper that prints the Windows WinMM capture device list. Not shipped with the app. |

The vendor FlexLib source (`FlexLib_API_v4.2.18.41174/`) sits at the repo root and is referenced by the FlexRadio project. It is gitignored and downloaded separately by the developer.

## Composition root

[AppServices.cs](AppServices.cs) is a single sealed class that wires the dependency graph at startup. There is no DI framework. New services should be added there, not via service-locator patterns elsewhere.

```text
AppSettingsStore       ┐
AppSettingsSession     │
FlexLibRadioDiscovery  │
FlexLibRadioConnection ├── MainWindowViewModel
WdmAudioDeviceFinder   │
ReleaseUpdateService   │
CwSkimmerLauncher      ┘
  ├─ CwSkimmerIniModelFactory
  ├─ CwSkimmerIniWriter
  ├─ IAudioDeviceFinder
  └─ () => CwSkimmerTelnetClient   (factory: one per channel)
```

## FlexRadio integration

Discovery uses FlexLib's local-network UDP broadcast listener (UDP 4992) wrapped behind [IRadioDiscovery](src/SmartSDRIQStreamer.FlexRadio/IRadioDiscovery.cs). Connection wraps `Radio.Connect` behind [IRadioConnection](src/SmartSDRIQStreamer.FlexRadio/IRadioConnection.cs), which exposes:

- Connection lifecycle (`ConnectAsync`, `Disconnect`, `ConnectionStateChanged`).
- Live snapshots of panadapters, slices, and DAX-IQ streams.
- Mutating operations: `RequestDaxIQStreamAsync`, `StopDaxIQStreamAsync`, `SetSliceFrequencyAsync`, `PublishSpotAsync`.
- Property change events for each (`PanadapterUpdated`, `SliceUpdated`, `DaxIQStreamUpdated`, …).
- Network status (`AvgDAXKbps`, `NetworkStatus`, RTT).

Why the abstraction: keeping FlexLib types out of the rest of the app makes it possible to write tests against the CW Skimmer module without spinning up FlexLib, and isolates breakage when FlexLib evolves (e.g. the 4.1.5 → 4.2.18 migration documented in [Flexlib4-2-Migration-Guide.md](Flexlib4-2-Migration-Guide.md)).

The FlexRadio module must remain runtime-compatible with both SmartSDR 4.1.5 and 4.2.x server radios.

## CW Skimmer integration

The CWSkimmer module owns everything between "operator clicked Start" and "CW Skimmer is decoding on the right frequency."

### Per-channel ownership

[CwSkimmerLauncher](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerLauncher.cs) owns one set of resources per DAX-IQ channel:

- The `CwSkimmer.exe` process (one per channel).
- A generated INI at `artifacts/cwskimmer/ini/CwSkimmer-ch{N}.ini`.
- A telnet client connected to `127.0.0.1` on port `7300 + (N × 10)`.
- A `CwSkimmerSyncTracker` driving that telnet.
- A telnet-lifecycle `CancellationTokenSource`.

Channel state lives in `Dictionary<int, …>` maps guarded by a single `_sync` lock. The launcher exposes events (`RunningStateChanged`, `FrequencyClicked`, `SpotReceived`, `TelnetStatusChanged`, `OutboundQsyEmitted`) that the ViewModel subscribes to.

### INI generation

[CwSkimmerIniModelFactory](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerIniModelFactory.cs) reads the operator's master `CwSkimmer.ini` for calibration values, then builds a per-channel INI model. Two device-resolution strategies:

- **MME (default).** Per-channel `MmeSignalDev` is auto-derived by looking up `DAX IQ {N}` (DAX v2) or `DAX IQ RX {N}` (DAX v1) in the live WinMM capture device list. The operator's `MmeAudioDev` (local speakers / headphones) is copied verbatim.
- **WDM (operator override).** When the operator supplies a 1-based UI index for a channel via the Setup Wizard (`AppSettings.WdmDeviceIndexCh{N}`), the channel INI is written with `UseWdm=1` and that index. WDM cannot be auto-derived because CW Skimmer's WDM device enumeration is opaque kernel-streaming order that doesn't match WinMM or DirectSound (see [issue #19](https://github.com/cdub89/SmartStreamer4/issues/19)).

[CwSkimmerIniWriter](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerIniWriter.cs) writes only the runtime-managed sections (`[Audio]`, `[Telnet]`); CW Skimmer-owned sections (window geometry, callsign, network) are preserved. The `[Audio]` section is written only on first INI creation, so operator edits in CW Skimmer survive subsequent launches.

### Telnet

[CwSkimmerTelnetClient](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerTelnetClient.cs) speaks CW Skimmer's telnet protocol:

- Outbound: `SKIMMER/LO_FREQ <hz>`, `SKIMMER/QSY <khz>`.
- Inbound: spot lines (`DX de <call>: <freq> <comment>`), click-tune events, login banner.

The client treats login timeout as success after a short window because some CW Skimmer configurations send no login banner. That branch is logged via `LogDiag` but is not currently surfaced to the Logs tab; see [PLAN.md](PLAN.md) for the planned status surface.

### Sync tracker

[CwSkimmerSyncTracker](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerSyncTracker.cs) is the model-bearing component. Every source of frequency change (panadapter event, slice event, RIT change, startup) calls `RequestSync(loHz?, vfoMHz?)`. A single background loop coalesces requests over a 50 ms window, then emits only what changed against last-sent.

Three properties:

1. **Idempotent.** Repeated `RequestSync` calls with the same values produce no telnet traffic.
2. **LO before VFO in one iteration.** Within a single coalesced wakeup, `SKIMMER/LO_FREQ` is emitted first, then `SKIMMER/QSY`. CW Skimmer's IQ pipeline rebuild sees the new LO before any QSY in that context.
3. **Post-LO VFO invalidation.** After a successful LO emit, `_lastSentVfoMHz` is cleared so a stale QSY (sent against the previous LO context before the panadapter recenter event arrived) is re-asserted on the next iteration.

Property #3, plus the ViewModel passing the current pan centre on every slice event ([MainWindowViewModel.TrySyncSliceToSkimmer](MainWindowViewModel.cs)), covers both halves of the FlexLib slice / pan event ordering race.

## UI shell

The root project hosts the Avalonia UI:

- [App.axaml](App.axaml) / [App.axaml.cs](App.axaml.cs) — application-level setup and theme.
- [MainWindow.axaml](MainWindow.axaml) / [MainWindow.axaml.cs](MainWindow.axaml.cs) — three tabs: Operating, Config, Logs.
- [MainWindowViewModel.cs](MainWindowViewModel.cs) — orchestration. Currently large (~2200 lines); see [PLAN.md](PLAN.md) for slimming targets.
- [SliceViewModel.cs](SliceViewModel.cs) — per-slice row in the Operating tab.
- [SetupWizardWindow](SetupWizardWindow.axaml.cs) — in-app Setup Guide viewer (opened from the Help tab); renders [SETUP_GUIDE.md](SETUP_GUIDE.md) as an embedded resource.
- [ResetSkimmerWizardWindow](ResetSkimmerWizardWindow.axaml.cs) — re-run the wizard after settings drift.
- [ThrottledStatusEmitter.cs](ThrottledStatusEmitter.cs) / [FooterStatusBuffer.cs](FooterStatusBuffer.cs) — rate-limit status writes to the Logs tab and footer.

Avalonia compiled bindings are on by default (`AvaloniaUseCompiledBindingsByDefault=true`); reflection-based `Path=` bindings should not be used.

## Settings persistence

[AppSettings](AppSettings.cs) is the persisted shape (CW Skimmer paths, callsign, telnet base port, spot defaults, window placement, per-channel device indices, soundcard driver mode). [AppSettingsStore](AppSettingsStore.cs) serializes it to `%AppData%\SmartStreamer4\settings.json` via `System.Text.Json`. [AppSettingsSession](AppSettingsSession.cs) wraps the store with a debounced save so rapid in-session changes don't hammer disk. The folder was renamed from `SDRIQStreamer` to `SmartStreamer4` in v0.1.19b; [AppDataPaths](AppDataPaths.cs) auto-migrates the legacy folder on first launch via atomic `Directory.Move`.

## Runtime artifacts

| Path | Owner | Purpose |
|------|-------|---------|
| `%AppData%\SmartStreamer4\settings.json` | `AppSettingsStore` | Persisted user settings. Legacy `SDRIQStreamer` folder auto-renamed on first launch of v0.1.19b. |
| `artifacts/cwskimmer/ini/CwSkimmer-ch{N}.ini` | `CwSkimmerLauncher` | Generated per-channel CW Skimmer INI. |
| `artifacts/cwskimmer/ini/device-diagnostic.txt` | `CwSkimmerLauncher` | Snapshot of WinMM / DirectSound enumeration written on every launch. |
| `artifacts/logs/streamer-status.log` | `MainWindowViewModel` | Append-only `[STREAMER]` / `[SKIMMER]` / `[TELNET]` log. |
| `artifacts/logs/spot-publish.log` | `MainWindowViewModel` | Per-spot publish results. |
| `artifacts/release/SHA256SUMS.txt` | release pipeline | Signed release hashes (in repo). |

The `artifacts/` tree is regenerated by the running app and is gitignored except for `release/SHA256SUMS.txt`.

## Threading

- **UI thread.** Avalonia dispatcher. All bindable property writes happen here.
- **FlexLib events.** FlexLib raises on its own threads. The connection wrapper marshals to the dispatcher only when the consumer needs UI thread guarantees; bulk events stay on the thread pool.
- **Per-channel sync tracker loops.** One `Task.Run` background loop per active channel. Idempotent; safe to interrupt at any wakeup boundary.
- **Telnet client.** One I/O loop per channel reading the telnet socket. Emits `FrequencyClicked` / `SpotReceived` / `StatusChanged` events.
- **Update checker.** Polling task in `MainWindowViewModel` against the GitHub Releases API.

State that crosses threads (launcher channel maps, tracker desired-state, telnet status callbacks) is guarded by single per-component locks (`_sync`, `_gate`).

## Testing

[tests/SmartSDRIQStreamer.CWSkimmer.Tests/](tests/SmartSDRIQStreamer.CWSkimmer.Tests/) covers:

- INI generation paths (calibration, MME / WDM mode, channel offset, preserved sections).
- Sync tracker behaviour (LO + VFO ordering, idempotence, post-LO invalidation, telnet failure handling).

Not covered by automated tests:

- FlexLib integration (live radio required).
- CW Skimmer process launch and telnet (live process required).
- Avalonia UI behaviour.

The CLAUDE.md "live-radio smoke test gate" exists for this reason: unit tests cannot catch sync-tracking or audio-routing regressions.

## Conventions

- **Modernization.** Latest stable APIs. .NET 8 / C# 12 idioms: collection expressions, primary constructors, `required` members, raw string literals, `ArgumentNullException.ThrowIfNull`, `TimeProvider`. Nullable reference types on; no `!` suppressions.
- **Build / test / markdown gates.** Every `.cs` change runs `dotnet build` and (where it touches `src/CWSkimmer`, `src/FlexRadio`, or `tests`) `dotnet test`. Every `.md` change runs `markdownlint`. See [CLAUDE.md](CLAUDE.md) for the full list.
- **No em dashes in user-facing prose.** MessageBox text, status bar messages, dialog labels, release notes, and in-app help use periods or parentheses. Code comments and repo docs are exempt.
- **ViewModel, not VM.** Spell it out in code review and discussion.
