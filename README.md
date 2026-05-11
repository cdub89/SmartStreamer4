# SmartStreamer4

SmartStreamer4 is a Windows desktop app that streams FlexRadio DAX-IQ
audio into [CW Skimmer](https://www.dxatlas.com/CwSkimmer/) and reconciles
its decoded spots back to the radio's slice. It runs alongside SmartSDR
and gives you per-channel CW Skimmer instances synchronised to your
panadapter and slice frequencies.

License: MIT (see [LICENSE](LICENSE)).

<img width="620" alt="SmartStreamer4 operating view" src="Assets/README/operating-screenshot-v0.1.11b.png" />

## Status

- Beta. Latest release: see [GitHub Releases](https://github.com/cdub89/SmartStreamer4/releases).
- Requires SmartSDR 4.x and FlexRadio firmware ≥ 3.3.32.8203 (FlexLib 4.2.18 minimum).
- Tested against SmartSDR 4.1.5 and 4.2.x server radios.

## Install

1. Download `SmartStreamer4-v<version>.zip` from the latest [GitHub Release](https://github.com/cdub89/SmartStreamer4/releases).
2. Extract `SmartStreamer4.exe` somewhere convenient.
3. Run it. Allow firewall access if Windows prompts.

You'll need DAX and CW Skimmer installed and configured on the same PC. The first time you launch SmartStreamer4 with both CW Skimmer paths set, the **Reset / Setup Wizard** opens automatically and walks you through the rest.

For the full operator walkthrough see [SETUP_GUIDE_WIZARD.md](SETUP_GUIDE_WIZARD.md), or click **Reset / Setup Wizard** on the Config tab any time.

## Build from source

Prerequisites:

- .NET 8 SDK
- Windows 10/11 (the app targets `net8.0-windows`)
- FlexLib API source extracted to `FlexLib_API_v4.2.18.41174/` at the repo root. Download from FlexRadio: <https://www.flexradio.com/software/smartsdr-v4-x-api-flexlib/>

Build:

```powershell
dotnet build SmartSDRIQStreamer.csproj
```

Plain `dotnet build` from the repo root pulls in the vendor FlexLib API projects, which bring unresolvable WPF references on the Windows .NET SDK. Always point at the app csproj.

Run a debug build:

```powershell
dotnet run --project SmartSDRIQStreamer.csproj
```

## Test

```powershell
dotnet test tests/SmartSDRIQStreamer.CWSkimmer.Tests
```

The tests cover INI generation and the CW Skimmer sync tracker. They do not exercise FlexLib or live radio behaviour. See [ARCHITECTURE.md](ARCHITECTURE.md#testing) for what is and isn't covered.

## Release

Releases are produced by [`publish-release.ps1`](publish-release.ps1) in two phases, with live-test, tag push, and release-notes review happening between them:

```powershell
git tag v0.1.18b
.\publish-release.ps1            # phase 1: build, verify embedded version, zip, update SHA256SUMS
# live-test the zip, then: git push origin v0.1.18b
# confirm RELEASE_NOTES-v0.1.18b.md is finalized
.\publish-release.ps1 -Publish   # phase 2: commit SHA256SUMS, push, gh release create --latest
```

Phase 1 runs tests, publishes a self-contained single-file exe, verifies its embedded `ProductVersion` matches `<tag>+<sha>`, zips it as `SmartStreamer4-v0.1.18b-win-x64.zip` (runtime suffix matches the `-Runtime` parameter), and updates `artifacts/release/SHA256SUMS.txt`. Phase 2 fails fast if the tag isn't on `origin`, the zip is missing, the SHA256SUMS line doesn't match, or the notes file is empty; otherwise it commits the SHA256SUMS bump, pushes, and creates the GitHub release with the zip attached (browsers block `.exe` downloads, so always ship the zip). `--latest` is hard-coded.

Versioning: the git tag at HEAD is the single source of truth. The csproj `<Version>` stays at a clean numeric default; the script reads the tag and embeds `<tag>+<sha>` in the published exe so the in-app version display and update check report the right release. Beta tags follow `vMAJOR.MINOR.PATCHb` (e.g. `v0.1.18b`); bug-fix patches on a beta use `vMAJOR.MINOR.PATCHbN` (e.g. `v0.1.17b1`).

See [PLAN.md](PLAN.md) for what's slated for the next beta.

## Repo layout

```text
SmartSDRIQStreamer.csproj          Avalonia app (root project, output: SmartStreamer4.exe)
src/SmartSDRIQStreamer.FlexRadio   FlexLib isolation (radio discovery + connection)
src/SmartSDRIQStreamer.CWSkimmer   CW Skimmer adapter (INI + launcher + telnet + sync)
tests/                             xUnit tests (CWSkimmer module)
tools/                             Diagnostic helpers (e.g. WinMM enumeration)
artifacts/                         Runtime output (logs, generated INIs, release zips)
Assets/                            Icons + screenshots referenced by the UI / docs
```

## Documentation

| File | What it covers |
|------|----------------|
| [README.md](README.md) | This file. Install, build, test, release. |
| [SETUP_GUIDE_WIZARD.md](SETUP_GUIDE_WIZARD.md) | Operator guide. Embedded in the in-app Setup Wizard. |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Module layout, sync model, threading, conventions. |
| [PLAN.md](PLAN.md) | Current implementation plan. What's blocking the next beta and what's deferred. |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Branch / PR workflow for contributors. |
| [CLAUDE.md](CLAUDE.md) | Project guide for Claude Code sessions in this repo. |
| [Flexlib4-2-Migration-Guide.md](Flexlib4-2-Migration-Guide.md) | FlexLib 4.1.5 → 4.2.18 API surface reference. |

## Reporting bugs

Open an issue on [GitHub](https://github.com/cdub89/SmartStreamer4/issues) and include:

- SmartStreamer4 version (or commit hash)
- SmartSDR / FlexRadio firmware version
- CW Skimmer version
- Windows version
- Reproduction steps and what you expected vs. saw
- Relevant lines from `artifacts/logs/streamer-status.log`
