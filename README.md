# SmartStreamer4

Modernizes CW Skimmer integration with FlexRadio DAX-IQ streams using a dedicated Avalonia desktop app.
License: MIT (see `LICENSE`).

<img width="620" alt="SmartStreamer4 operating view" src="Assets/README/operating-screenshot-v0.1.11b.png" />

## 1) Quick Start

### First-Time Setup / Get Started

- Launch `SmartStreamer4.exe`. If Windows prompts for firewall access, allow the app through Windows Firewall.
- Click the streamer's `Config` tab and set the local path to `CwSkimmer.exe` and the associated INI file.
- Before first streamer launch on a machine, run CW Skimmer manually and configure the `Audio` tab: set **Soundcard Driver** to **MME** (the only mode SmartStreamer4 currently supports reliably for multi-channel — WDM is experimental, see notes below), set **Signal I/O Device** to `DAX IQ 1 (FlexRadio DAX)`, and set **Audio I/O Device** to any local audio output (not a DAX device). Exit CW Skimmer to save `CwSkimmer.ini`.
- In the `Operating` tab, click the radio and press **Connect**. After a few seconds you should see the available slices and IQ streams needed to launch CW Skimmer.
- Once CW Skimmer is running, view settings and verify the `Radio`, `Audio`, and `Operator` tabs are correct for your station.
- Click **Start** in the CW Skimmer toolbar to begin decoding.
- Finally, click the streamer's `Logs` tab to see real-time events from `[STREAMER]`, `[SKIMMER]`, and `[TELNET]` to verify connect, launch, and sync direction (VFO vs Skimmer click-tune).

### When to Reset Channel INIs

Generated channel INIs (`CwSkimmer-chN.ini`) are seeded once from the master INI and then preserved across launches to protect your settings. Most upgrades do **not** require a reset — the streamer rewrites the `[Audio]` and `[Telnet]` sections on every launch.

Use `Config` → `Streamer INI Files` → **Reset** when:

- You changed **Soundcard Driver** (MME ↔ WDM) in the master INI.
- You changed **Signal I/O** or **Audio I/O** device selection in the master INI.
- A beta release explicitly notes an INI schema change.
- CW Skimmer hangs on launch after upgrade and other causes are ruled out.

The Reset button is disabled while CW Skimmer is running, and only deletes generated channel files — your manual `CwSkimmer.ini` baseline is untouched.

**Logs are separate** and not affected by Reset. They are append-only diagnostic data under `artifacts/logs/`; delete them manually if disk usage is a concern.

## 2) Technology Stack

- UI: Avalonia (`net8.0-windows`)
- App pattern: MVVM (`CommunityToolkit.Mvvm`)
- Radio integration: FlexRadio FlexLib API `v4.2.18.41174` (SmartSDR 4.x)
- CW decoder integration: CW Skimmer process + Telnet control channel

## 3) High-Level Architecture

- `MainWindowViewModel` orchestrates discovery, connection, DAX stream state, CW Skimmer launch/sync, and spot publishing.
- `src/SmartSDRIQStreamer.FlexRadio` isolates FlexLib-specific discovery/connection and radio operations.
- `src/SmartSDRIQStreamer.CWSkimmer` isolates CW Skimmer INI generation, process launch, Telnet control, and spot parsing.
- UI is organized into tabs:
  - `Operating`: radio target selection + stream/launch operations + live event line
  - `Config`: CW Skimmer paths + spot controls + Telnet INI view
  - `Logs`: consolidated streamer status output + quick open to logs folder

## 4) FlexRadio Integration Approach

- Use local-network discovery and a single-radio connect model.
- Track panadapters, slices, and DAX-IQ streams via FlexLib events.
- Publish CW spots through FlexLib spot API with app-defined text/background color and lifetime.

## 5) CW Skimmer Integration Approach

- Build channel-specific managed INI files from a user-selected template.
- Device mapping model is **MME-only auto-derivation**:
  - operator calibrates the manual `CwSkimmer.ini` once with **Soundcard Driver = MME**, **Signal I/O = `DAX IQ 1 (FlexRadio DAX)`**, and **Audio I/O** = any local audio output,
  - streamer resolves each channel's `MmeSignalDev` independently at launch by looking up `DAX IQ {N}` in the live WinMM enumeration — no per-channel offset, no calibration anchor required for ch 2-4,
  - generated channel INIs always set `UseWdm=0` regardless of the master INI setting, because CW Skimmer's WDM device list uses an opaque kernel-streaming enumeration that cannot be replicated programmatically (see issue #19).
- **WDM is experimental**: if a user chooses to operate in WDM mode they must launch each channel manually once, manually pick the correct device in CW Skimmer's Audio tab, and exit to save the per-channel INI. The streamer cannot auto-derive WDM indices for ch 2-4.
- If operator corrects channel device selections in CW Skimmer and exits, streamer preserves existing channel INI `[Audio]` values on later launches.
- Launch CW Skimmer per DAX-IQ channel and connect a Telnet client.
- Runtime sync model:
  - Slice frequency updates drive channel-matched `SKIMMER/QSY`.
  - Pan center or band changes trigger channel-matched `SKIMMER/LO_FREQ` plus an immediate effective-RX `SKIMMER/QSY` re-assert.
  - On any `SKIMMER/LO_FREQ` change the matching `SKIMMER/QSY` is re-asserted in the same iteration so CW Skimmer's VFO display tracks panadapter recenters and cross-band slice tunes even when slice and pan events arrive out of order.
- Parse `DX de` lines and forward valid spots to radio when spot forwarding is enabled.
- Preserve CW Skimmer-owned config sections while writing runtime-managed sections (`[Audio]`, `[Telnet]`).

## 6) Development Prerequisites

- Windows 10/11
- .NET SDK 8.x
- SmartSDR + DAX installed and running
- CW Skimmer installed
- FLEX-6x00/8x00 radio reachable on local network
- Local radio required in the current implementation (SmartLink/VPN deferred)
- FlexLib API package downloaded and extracted to `FlexLib_API_v4.2.18.41174` in project root

## 7) Build, Run, Test

### Build

```powershell
dotnet build SmartSDRIQStreamer.csproj
```

### Run (development)

```powershell
dotnet run
```

### Run (Release executable)

```powershell
dotnet build SmartSDRIQStreamer.csproj -c Release
.\bin\Release\net8.0-windows\SmartStreamer4.exe
```

### Release Packaging

```powershell
# 1. Bump <Version> in SmartSDRIQStreamer.csproj

# 2. Build the release exe and zip (script prints the SHA256 and gh command at the end)
powershell -ExecutionPolicy Bypass -File .\publish-release.ps1 -Configuration Release -Runtime win-x64

# 3. Add the SHA256 line printed by the script to artifacts/release/SHA256SUMS.txt

# 4. Commit release assets
git add SmartSDRIQStreamer.csproj
git add -f artifacts/release/SHA256SUMS.txt
git commit -m "Prepare v<version> beta release assets."

# 5. Push and tag
git push origin main
git tag v<version>
git push origin v<version>

# 6. Create the GitHub release — attach the ZIP, use --latest, NOT --prerelease
gh release create v<version> \
  "bin\Release\net8.0-windows\win-x64\publish\SmartStreamer4-v<version>-win-x64.zip#SmartStreamer4-v<version>-win-x64.zip" \
  --title "SmartStreamer4 v<version>" \
  --notes "..." \
  --latest
```

> **Notes:**
>
> - Always attach the **zip** file, not the raw exe — browsers may block direct exe downloads.
> - Always use `--latest` (never `--prerelease`) so the new release becomes the default download on the GitHub releases page.

### Tests

```powershell
dotnet test tests/SmartSDRIQStreamer.CWSkimmer.Tests
```

## 8) Project Layout

```text
SmartStreamer4/
├── SmartSDR-IQ-Streamer.MDC
├── SmartSDRIQStreamer.csproj
├── README.md
├── SmartSDRIQStreamer.slnx
├── Program.cs
├── App.axaml / App.axaml.cs
├── AppServices.cs
├── MainWindow.axaml / MainWindow.axaml.cs
├── CwSkimmerWorkflowService.cs
├── FooterStatusBuffer.cs
├── src/
│   ├── SmartSDRIQStreamer.FlexRadio/
│   └── SmartSDRIQStreamer.CWSkimmer/
├── tests/
│   └── SmartSDRIQStreamer.CWSkimmer.Tests/
└── artifacts/
```

## 9) Phase Status

- Phase 1 (Foundation): COMPLETE
- Phase 2.1 (CW config + INI write): COMPLETE
- Phase 2.2 (CW launch): COMPLETE
- Phase 2.3 (Runtime sync): COMPLETE (including fine-tuned LO/panadapter/slice synchronization hardening)
- Phase 3 (Polish / hardening): COMPLETE
- Phase 3.1 (Bridge spots): COMPLETE baseline (forwarding, controls, diagnostics)
- Phase 3.2 (RIT fine tuning sync): COMPLETE
- Phase 3.3 (Network quality monitor): COMPLETE
- Phase 3.4 (Configuration pages): COMPLETE
- Phase 3.5 (Operating page simplification): COMPLETE

## 10) Notes

- `FlexLib_API_v4.2.18.41174` is intentionally excluded from version control.
- Download FlexLib API (SmartSDR v4): [https://www.flexradio.com/software/smartsdr-v4-x-api-flexlib/](https://www.flexradio.com/software/smartsdr-v4-x-api-flexlib/)
- Build may emit legacy FlexLib warnings on `net8.0-windows`; tracked separately.
- Phase status and roadmap tracking are maintained in `SmartSDR-IQ-Streamer.MDC` (single source of truth).
- Runtime artifacts:
  - `artifacts/cwskimmer/ini` for per-channel INI and diagnostics
  - `artifacts/logs` for runtime status logs

## 11) FlexLib 4.2.x Upgrade

SmartStreamer4 targets **FlexLib 4.2.18** (`FlexLib_API_v4.2.18.41174`). This section summarises what changed from 4.1.5 and what is needed to build against the new library.

### Radio / firmware prerequisite

FlexLib 4.2.x requires radio firmware **≥ 3.3.32.8203**. Update the radio before connecting with a 4.2.x-built application.

### Files changed in the upgrade

| File | Change |
|------|--------|
| `src/SmartSDRIQStreamer.FlexRadio/SmartSDRIQStreamer.FlexRadio.csproj` | `ProjectReference` updated from `FlexLib_API_v4.1.5.39794` to `FlexLib_API_v4.2.18.41174` |
| `SmartSDRIQStreamer.csproj` | Added `<EmbeddedResource Remove>` and `<None Remove>` glob exclusions for both FlexLib folders so the SDK does not auto-include `.resx` and other assets from the unbuilt library tree |

No application source code changes were required. The breaking API changes in FlexLib 4.2.x (`HAAPI.AmplifierFault` rename, `HAAPI.AmpIsSelected` removal) do not affect this app; `Radio.InUseIP` / `Radio.InUseHost` deprecations are internal to FlexLib and produce warnings only in the library build, not in application code.

### Getting the FlexLib package

`FlexLib_API_v4.2.18.41174` is not included in version control. Download the FlexLib API source from FlexRadio and extract it to the project root so the folder exists at:

```text
SmartSDR-IQ-Streamer/
└── FlexLib_API_v4.2.18.41174/
    └── FlexLib/
        └── FlexLib.csproj
```

### Migration reference

`Flexlib4-2-Migration-Guide.md` in the repository root documents the full 4.1.5 → 4.2.18 API surface change, including all breaking changes, deprecations, and new features.
