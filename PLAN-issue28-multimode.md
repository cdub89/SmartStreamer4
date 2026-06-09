# PLAN: Issue #28 — Multi-app digital-mode support (WSJT-X / JTDX)

## Context

SmartStreamer4 today is built around a single external application, CW Skimmer:
the Operating tab lists the radio's slices / DAX-IQ channels with per-channel
Launch/Stop, and Config holds CW Skimmer INI / audio / telnet / spot settings.

Issue #28 asks to generalize the app into a multi-application launcher for
amateur digital modes, adding **WSJT-X** and **JTDX** alongside CW Skimmer, with
the Setup Wizard promoted to its own tab and Operating/Config able to manage
each app. Targeted as a **v0.2.x** release.

Intended outcome: an operator can set up and run CW Skimmer (CW Mode) or
WSJT-X / JTDX (Digital Mode, FT8/FT4) against a FlexRadio from one app — one
family at a time, but several instances of the active mode's app at once on
different slices — on current SmartSDR (v4.2.20 / DAXv2).

## Decisions (locked)

### Layout: Launch tab drives mode selection

A new **Launch tab** is the app's entry point and home. It absorbs radio
discovery + connection (today on the Operating tab) and guides the operator to a
mode *in context* (see Workflow below):

- **CW Mode** -> today's CW Skimmer-centric tabs, unchanged.
- **Digital Mode** -> a new WSJT-X / JTDX screen set that reuses CW Skimmer UI
  components where appropriate (see Component reuse below).

Rationale: existing CW Skimmer users land on Launch, connect, and pick CW Mode
to get exactly today's app (zero disruption — stronger than app-aware tabs);
digital operators pick Digital Mode and get purpose-built screens. Choosing on
the Launch tab (rather than a bare startup dialog) lets it be informed by the
connected slice's demod mode — a **soft notice** (for now) when the slice does
not match the chosen mode, without blocking. A left-nav / master-detail shell
(considered) is the better long-term IA but is a v0.3 rewrite, not a v0.2
feature.

JTDX is **not** a third mode. It is a WSJT-X fork with the same FT8/FT4 modes,
the same DAX-audio + CAT binding, and the same multi-instance pattern; only the
exe path, config directory, and a couple of CLI/UDP defaults differ. Digital
Mode therefore has a per-launch **engine selector (WSJT-X | JTDX)** on identical
screens, which also leaves room for other digital apps later without new modes.

### Concurrency: hard modes, multi-instance within a mode

Operate **one family at a time**: you are in CW Mode **or** Digital Mode, never
both. Switching mode cleanly stops the active family's apps (with confirmation
when something is running), frees their resources, then loads the other mode.

What is kept vs dropped:

- **Kept — multi-instance within a mode:** several WSJT-X/JTDX instances on
  different slices (and, as today, several CW Skimmer instances). This is the
  concurrency operators actually want, and it is cheap because the launcher
  pattern is already per-instance (`_processesByChannel`, `_telnetByChannel`,
  `_trackerByChannel`, ...). Practical ceiling: WSJT-X 3.0 FT8 decoding can use
  up to ~12 concurrent threads per instance, so instance count is CPU-bound —
  the UI should default conservatively, not encourage unbounded instances.
- **Dropped — cross-family concurrency:** CW Skimmer and WSJT-X running at the
  same time. Rare workflow; disproportionate design / coding / live-test cost
  (shared DAX + CAT arbitration, two binding types live, cross-mode UI state).

Decisive rationale: hard modes let **CW Mode be the existing CW Skimmer code
path, untouched** — no `IExternalApp` retrofit of `CwSkimmerLauncher` — with
Digital Mode as a parallel, isolated subsystem that only *reuses* shared
services. The proven CW experience carries near-zero new regression surface,
which a concurrency model could not promise. The decision is reversible: hard
modes do not preclude relaxing to concurrency in a later version if justified.

(Earlier framing correction: "concurrency is nearly free" applied only to
intra-mode multi-instance, not to cross-family concurrency, which is not cheap.)

### Integration depth: asymmetric by app

- **CW Skimmer** stays *deeply* integrated: DAX-IQ stream, telnet cluster, sync
  tracker, spot forwarding (all existing).
- **WSJT-X / JTDX** are *setup-and-launch* only: configure the radio-side DAX
  audio + slice via FlexLib, guide the operator through CAT + audio-device
  selection, then launch the exe. No telnet, no sync, no spot brokering.
  Rationale: WSJT-X/JTDX on a Flex talk to the radio directly via DAX audio +
  CAT; the real user value is removing the fiddly setup, not re-brokering it.

## Workflow (Launch tab)

1. **Start -> Launch tab.** The app opens on the Launch tab, which hosts the
   mode choice plus radio discovery and connect controls (moved here from the
   Operating tab).
2. **Connect to a radio.** Once connected, the Launch tab lists the radio's
   stations and, per station, the available slices.
3. **Pick a station, then a slice.** The Launch tab detects the slice's demod
   mode and shows a **soft notice** when it does not match the chosen mode, but
   **does not block** (decision: soft notice for now):
   - choosing **CW Mode** on a non-CW slice -> notice "slice is USB/DIGU; CW
     Skimmer expects a CW slice" (still launchable).
   - choosing **Digital Mode** on a non-USB/DIGU slice -> notice "slice is CW;
     WSJT-X/JTDX need USB/DIGU audio" (still launchable).
4. **Launch the app.** CW Mode -> CW Skimmer; Digital Mode -> choose engine
   (WSJT-X | JTDX). Launching enters that mode's working tabs.

Gate posture (decision: **soft notice for now**, reversible to a hard block
later): both checks warn but allow launch. Technically the **digital** mismatch
is the more consequential one (WSJT-X/JTDX consume the slice's USB/DIGU audio,
so a CW slice will not decode), while the **CW** mismatch is advisory (CW Skimmer
reads panadapter IQ, not slice audio). Starting soft avoids blocking valid edge
cases while we learn real usage.

## Grounding facts (verified 2026-06-04)

- **SmartSDR v4.2.20** (released 2026-06-03) is a feature-access + bug-fix
  release over v4.2.18. The relevant architecture is **DAXv2** (introduced in
  4.2.18): a re-architected, shared-memory audio transfer. SmartStreamer is
  already field-compatible with 4.2 (DAX-IQ path proven).
- **FlexLib client delta 4.2.18 -> 4.2.20** is two files
  (`Radio.cs`, `RXAudioStream.cs`), **no public API change**, and touches only
  in-process audio/data consumers SmartStreamer does not use. The audio
  drop/mute fixes that matter are radio firmware + DAXv2 driver (server-side),
  delivered by installing 4.2.20.
  No FlexLib reference bump required for #28.
- **DAXv2 audio device names** (from `artifacts/cwskimmer/ini/device-diagnostic.txt`,
  dev machine, MME + DirectSound enumerations):
  - RX audio (per slice channel N): `DAX RX {N} (FlexRadio DAX)`
  - TX audio (single, shared): `DAX TX (FlexRadio DAX)`
  - IQ (CW Skimmer): `DAX IQ {N} (FlexRadio DAX)`
  - Note this is the **MME / WinMM** name (DirectSound matches because the
    strings are short). It is **not** "DAX Audio RX N"; our
    `WdmAudioDeviceFinder` only has a `"DAX Audio RX "` fallback that would not
    match DAXv2 — a WSJT-X resolver needs a `"DAX RX {N}"` pattern.

## WSJT-X / JTDX setup facts (from the official user guide)

From the WSJT-X 3.0.0 user guide (see References):

- **Audio (Settings -> Audio):** choose Input + Output soundcard devices; must
  be **48000 Hz / 16-bit** (Mono usually). Input <- our DAX RX endpoint; Output
  -> our DAX TX endpoint.
- **Radio (Settings -> Radio):** PTT Method = VOX / CAT / DTR / RTS — for a CAT
  proxy like SmartSDR CAT, **PTT = CAT**. Mode = USB (or Data/Pkt; DIGU on
  Flex), or **None** to leave the slice mode untouched. **Split Operation =
  Rig or Fake It** (recommended; keeps Tx audio 1500-2000 Hz). `Test CAT` /
  `Test PTT` buttons validate the setup.
- **Reporting (Settings -> Reporting) -> UDP Server:** network address + port,
  consumed by JTAlert / GridTracker; unique port per instance.
- **Per-instance config via `--rig-name=xxx` (Windows):**
  - Settings: `%LOCALAPPDATA%\WSJT-X - xxx\WSJT-X - xxx.ini`
  - Logs / save: `%LOCALAPPDATA%\WSJT-X - xxx\` (no rig-name ->
    `%LOCALAPPDATA%\WSJT-X\`)
  - So a per-slice `--rig-name` isolates each instance's config, and (phase 2)
    we know exactly which `.ini` to pre-seed or patch. The install dir
    (`C:\WSJT\wsjtx`) holds only binaries — no `.ini`.
  - `.ini` is **Qt INI**, `[Configuration]` section. Plain-string keys we can
    patch directly: `SoundInName`, `SoundOutName`, `Rig`, `CATNetworkPort`,
    `Mode` / `ModeTx`. Enum keys (`PTTMethod`, `DataMode`, `SplitMode`,
    `TXAudioSource`) are Qt `@Variant(...)` binary blobs -> patch via a
    known-good template or byte-exact constants, not free text.
  - **Known-good template (operator's working `WSJT-X.ini`, 2026-06-08, on
    DAXv2):** `Rig=FlexRadio 6xxx`, `CATNetworkPort=127.0.0.1:60000`,
    `PTTMethod=`CAT, `DataMode=`data, `SplitMode=`none, `Mode=FT8`/`ModeTx=FT8`,
    `SoundInName=DAX RX 1 (FlexRadio DAX)`, `SoundOutName=DAX TX (FlexRadio
    DAX)`. The exact `@Variant` blobs for PTT/DataMode/Split are captured from
    this file for templating.
  - The earlier **non-working** `WSJT-X - Slice-A.ini` was a stale 2021 config
    (`Rig=Ham Radio Deluxe`, `CATNetworkPort=127.0.0.1:7831` = HRD's port, and
    stale v1 audio names `DAX Audio RX 1 (FlexRadio Systems DAX Audio)`) —
    exactly the wrong-rig/port + stale-device failure the helper must prevent.

JTDX (the "improved" fork) is **confirmed near-identical** (operator's
`JTDX.ini`, 2026-06-08): same `Rig=FlexRadio 6xxx`, `CATNetworkPort=127.0.0.1:60000`,
`SoundInName/Out = DAX RX 1 / DAX TX (FlexRadio DAX)`, and **byte-identical**
PTT/DataMode/SplitMode `@Variant` blobs. Only the **config root**
(`%LOCALAPPDATA%\JTDX\JTDX.ini`; `JTDX - xxx` with `--rig-name`) and the **exe
path** differ. So one `.ini` template serves both engines, and `IDigitalApp`
varies only by `{name, exePath, configRoot}`. Defaults on this machine:

- WSJT-X: exe `C:\WSJT\wsjtx\bin\wsjtx.exe`, config root `%LOCALAPPDATA%\WSJT-X`.
- JTDX: exe `C:\JTDX64\159\bin\jtdx.exe`, config root `%LOCALAPPDATA%\JTDX`.

The JTDX exe path embeds a version (`...\159\...`), so the exe path must be
operator-configurable / browseable (sensible default + Browse), not hard-coded.

### FlexRadio DAXv2 wiring (from FlexRadio's official article)

Confirmed from FlexRadio's "Configuring WSJT-X ... using DAXv2" (works for JTDX
too; the article explicitly states **SliceMaster6000 is not compatible with
DAXv2**):

1. In the **DAX** and **CAT** control panels, select the **Station** (client
   device) whose slice you will use.
2. In **CAT**: ADD a port -> name it -> choose **TCP** (not COM serial) -> set a
   **TCP port** (start at **60000**) -> pick the **Slice letter** -> enable
   **Auto Switch TX Slice** -> Save.
3. WSJT-X **Settings -> Radio** — **CONFIRMED WORKING** (operator's
   `%LOCALAPPDATA%\WSJT-X\WSJT-X.ini`, 2026-06-08): **Rig = `FlexRadio 6xxx`**;
   **Network Server = `127.0.0.1:60000`** (the SmartSDR CAT TCP port); **PTT =
   CAT**; **Mode = Data/Pkt** (radio -> **DIGU**); **Split = None**; `Test CAT`
   goes green. (Fake-It also works except on 60 m.)
4. WSJT-X **Settings -> Audio** (the **DAX app must be running first** or the
   devices do not appear): **Input = `DAX RX {N} (FlexRadio DAX)`**, **Output =
   `DAX TX (FlexRadio DAX)`** — confirmed verbatim in the working `WSJT-X.ini`.
   WSJT-X stores the **full** device string in its `.ini` and shows
   **"(Not found)"** when the saved device is absent.
   **Migration hazard (seen on the operator's box):** a DAX **v1** config holds
   `DAX Audio RX N (FlexRadio Systems DAX Audio)` / `DAX Audio TX (FlexRadio
   Systems DAX TX)`, which do not exist under DAXv2 -> both show "(Not found)"
   and must be re-selected to the DAXv2 names.
5. In SmartSDR: set the slice **MODE = DIGU** and its **DAX channel** to match
   WSJT-X's input (DAX RX 1 <-> DAX Channel 1 on Slice A).
6. In the **DAX** panel, TX stream ~45-46, RX stream ~35; the channel indicator
   goes BLUE (selected) -> GREEN (connected). Enable the **TX DAX** button in
   Radio Controls.

Implications for our setup-and-launch flow (open items now resolved):

- **Device names settled:** `DAX RX {N}` / `DAX TX` (no MME-vs-WDM ambiguity;
  WSJT-X shows exactly these).
- **CAT is TCP, not serial:** per-slice `LocalHost:<60000+>` with Auto Switch TX
  Slice; PTT rides the same CAT TCP port. Our wizard guides creating the CAT TCP
  port (SmartSDR CAT app) and pre-fills WSJT-X's Network Server.
- **Ordering dependency:** ensure the DAX app is running before WSJT-X audio
  config (devices are hidden otherwise).
- We can set the slice to **DIGU** + assign its DAX channel via FlexLib
  (`Slice.DemodMode`, `Slice.DAXChannel`); WSJT-X's Mode=Data/Pkt also drives
  DIGU. One minor unconfirmed detail: the exact WSJT-X **Rig** dropdown entry
  that exposes the Network Server field (article omits it).

## Architecture

### Isolation: CW path untouched, digital subsystem parallel

Because of the hard-mode decision, **CW Skimmer is not refactored.**
`CwSkimmerLauncher` and the existing tabs stay as-is (CW Mode = today's app),
preserving the proven path with zero new regression surface.

Digital Mode is a **separate subsystem**:

- A new `IDigitalApp` abstraction scoped to the digital family only — identity
  (display name, exe path, config dir), per-instance lifecycle (`LaunchAsync`,
  `Stop`, `IsRunning`), and the binding tuple below. WSJT-X and JTDX are two
  implementations (or one parameterized by engine).
- A new `DigitalAppLauncher` modeled on `CwSkimmerLauncher`'s per-instance
  pattern, but with no telnet / sync / spot machinery (setup-and-launch only).
- It **reuses** the already-separate shared services (radio connection, audio
  discovery, status/log, settings, slice list) without modifying them.

No generic cross-family launcher and no `IExternalApp` retrofit of CW Skimmer —
that was only needed for concurrency, which is now out of scope.

### Per-instance binding model

A running digital-mode instance is the tuple:

`(slice, DAX RX channel, CAT TCP port, --rig-name, UDP reporting port)`
sharing a single DAX TX.

This falls out of the v4.2 multiple-WSJT-X-instance workflow. Per instance:

| Knob | Rule | Example |
| --- | --- | --- |
| Instance profile | `wsjtx --rig-name=<name>` | `flex66001` |
| CAT port | SmartSDR CAT TCP port per slice (operator-defined) | Slice A `:60000`, Slice B `:60001` |
| RX DAX channel | unique per slice/instance | Slice A = `DAX RX 1`, Slice B = `DAX RX 2` |
| TX DAX channel | **shared** across all instances | single `DAX TX` |
| UDP reporting port | unique per instance | 2237 / 2233 / ... |

**Confirmed by operator (both slices, on DAXv2):**

- Slice A: `Rig=FlexRadio 6xxx`, CAT `127.0.0.1:60000`, `SoundInName=DAX RX 1`,
  `SoundOutName=DAX TX (FlexRadio DAX)`.
- Slice B: `Rig=FlexRadio 6xxx`, CAT `127.0.0.1:60001`, `SoundInName=DAX RX 2`,
  `SoundOutName=DAX TX (FlexRadio DAX)`, `UDPServerPort=2237`.

Full **FLEX-6600 (4-slice)** allocation (operator-stated): one shared `DAX TX`
across all slices; per slice a CAT TCP port in the 60000 range and a dedicated
DAX RX channel:

| Slice | CAT TCP port | DAX RX | DAX TX |
| --- | --- | --- | --- |
| A | `60000` | `DAX RX 1` | shared `DAX TX` |
| B | `60001` | `DAX RX 2` | shared `DAX TX` |
| C | `60002` | `DAX RX 3` | shared `DAX TX` |
| D | `60003` | `DAX RX 4` | shared `DAX TX` |

So RX is **per slice** (`DAX RX 1..4`), TX is the **single shared** `DAX TX`,
and each slice has its own CAT TCP port (operator-assigned, not a fixed
sequence). CAT ports are **discoverable** by parsing `CAT.settings` (see CAT
discovery in Open items), so the wizard can auto-fill WSJT-X's Network Server.

**Concurrency caveat (observed):** both Slice A and Slice B were configured in
the **single default `WSJT-X.ini`** (the later one overwrote the earlier), so
this is one reconfigured instance, not two running at once. True concurrent
instances each require their own `--rig-name` config dir **and** a **unique UDP
port** (both currently `2237`, which would collide). The per-instance binding
values are validated; the live two-at-once run is still the remaining check.

### Concurrent launch (per-slice instances)

To run several digital instances at once, the streamer launches the engine exe
**once per slice**, each with its own `--rig-name` config:

- **rig-name scheme:** a stable per-slice name (e.g. `SliceA`, `SliceB`) ->
  config dir `%LOCALAPPDATA%\WSJT-X - SliceA\WSJT-X - SliceA.ini` (or
  `JTDX - SliceA`).
- **Config provisioning is part of "setup":** the operator cannot be expected to
  hand-configure up to 4 instances, so `DigitalAppLauncher` **seeds each
  per-slice `.ini`** from the known-good template, substituting per-slice values
  — `SoundInName=DAX RX <N>`, `CATNetworkPort=127.0.0.1:<port from CAT.settings>`,
  `UDPServerPort=<unique>`, `Rig=FlexRadio 6xxx`, shared `SoundOutName=DAX TX
  (FlexRadio DAX)`. Plain-string keys are patched directly; `@Variant` enum keys
  come from the template verbatim.
- **Per-instance uniqueness required:** distinct `--rig-name`, distinct CAT TCP
  port, distinct `DAX RX <N>`, distinct UDP port. (Sharing the default profile or
  UDP port collides — see the concurrency caveat above.)
- **Cap:** number of rows = existing slices, bounded by the radio's slice limit
  (FLEX-6600 = 4; FLEX-6700 = 8; FLEX-6400 = 2). Watch the CPU ceiling (each
  instance can use ~12 FT8 decode threads).
- Still **setup-and-launch only** — no telnet/sync/spot brokering; the streamer
  provisions config + launches + monitors/stops the process.

### FlexLib capabilities for the WSJT-X path

- **`Slice.DAXChannel`** (`Slice.cs:340`, settable `int` 0-8; 0 = none) assigns
  the slice's DAX **audio** channel — the programmatic equivalent of the
  slice-flag "DAX" pulldown in SmartSDR. The setter sends
  `slice set <id> dax=<N>`; the radio then routes that slice's RX audio onto DAX
  audio channel N, which surfaces on the PC as `DAX RX N (FlexRadio DAX)` ->
  WSJT-X **Input**. This is **RX audio only**, and is distinct from
  `DAXIQChannel` (panadapter IQ, used by CW Skimmer). TX is the separate, shared
  `DAX TX` (DAX panel / TX DAX button), not a slice property.
  - Implementation gap: our `SliceInfo` exposes `DemodMode`/freq/RIT but **not**
    `DAXChannel` — the FlexRadio module must add a `DAXChannel` get/set for the
    Digital flow.
- `DAXRXAudioStream` / `DAXTXAudioStream` classes exist (alongside
  `DAXIQStream`) for RX/TX audio stream control.
- Out of FlexLib's reach (operator-guided / file-discovered): the SmartSDR
  **CAT** TCP port (separate app; discover via `CAT.settings`) and the WSJT-X
  audio-device selection (host OS).

## UI layout

Launch tab (entry point; discovery + connect + station/slice + mode choice with
a soft mode-mismatch notice). It is the only place the radio is
discovered/connected; the connection state is then shared by both modes:

```text
LAUNCH
 Radio:   (discovering...)  ->  FLEX-6600  [Connect] / [Disconnect]
 Station: [ WX7V v ]
 Slices:  ( ) A  14.074  DIGU      (o) B  7.030  CW
 Mode:    [ Launch CW Mode ]        [ Launch Digital Mode ]  Engine: [WSJT-X v]
 Notice:  slice B is CW; Digital needs USB/DIGU  (you can still launch)
```

CW Mode = today's app, unchanged (Launch tab added alongside existing tabs):

```text
[Launch] [Operating] [Config] [Logs] [Help]   <- existing CW Skimmer tabs
```

Digital Mode = new screens, reusing shared components:

```text
[Launch] [Operating] [Config] [Logs] [Setup] [Help]
OPERATING  (slice-centric)
 Slice A | 14.074 DIGU | DAX RX 1
   Engine: [WSJT-X v]   CAT 60001  UDP 2237   [Start] [Cfg] [Logs]
 Slice B | 7.074  DIGU | DAX RX 2
   Engine: [JTDX  v]    CAT 60002  UDP 2238   [Start] [Cfg] [Logs]
```

- The **Launch tab** is home and the sole discovery/connect surface; a slim
  connected-radio indicator persists on the other tabs.
- Mode buttons on Launch stay enabled; a **soft notice** appears when the
  selected slice's demod mode does not match the chosen mode (Workflow step 3).
- Digital Operating is **slice-centric**: **one Start/Stop row per existing
  slice** (the list is dynamic), each with a per-launch engine selector
  (WSJT-X | JTDX) plus its CAT/DAX-RX/UDP allocation. The operator runs 1 up to
  the radio's slice cap concurrently (**FLEX-6600 = 4**; varies by model). Each
  Start launches a **separate** engine process for that slice (see Concurrent
  launch below).
- Config and Logs are scoped to the active mode (and, in Digital Mode, to the
  selected engine).
- **Setup** (Digital Mode) is a launcher hub, not one long wizard:

```text
SETUP
  WSJT-X       DAX audio + CAT/PTT          [Open]
  JTDX         DAX audio + CAT/PTT          [Open]
```

### Component reuse (CW Skimmer UI -> Digital Mode)

- **Direct reuse:** global radio / connection bar; slice-list row pattern
  (`SliceViewModel` / `ClientGroup`); audio-device discovery
  (`WdmAudioDeviceFinder` / `DirectSoundProbe`); status + log infrastructure
  (`FooterStatusBuffer`, `ThrottledStatusEmitter`, Logs tab); settings
  persistence; the Setup-wizard shell.
- **Adapt:** the per-slice action panel (CW Skimmer launch-on-IQ -> digital
  launch-on-audio); Config (CW INI / telnet / spot -> engine path + DAX audio
  channel + CAT port + rig-name / UDP).
- **New:** digital launch-profile management (per-slice engine + port
  allocation) and CAT-port guidance.

## Phased rollout (v0.2.x)

1. **Launch tab + mode shell:** new Launch tab hosting radio discovery +
   connect (moved from Operating), station/slice listing, and mode buttons with
   a **soft slice-mode-mismatch notice** (non-blocking); launching mounts the
   chosen mode's working tabs; **CW Mode = today's tabs unchanged**. No change to
   `CwSkimmerLauncher`.
2. **Digital subsystem:** new `IDigitalApp` + `DigitalAppLauncher` (per-instance,
   no telnet/sync/spot), reusing shared services.
3. **Digital Mode screens:** slice-centric Operating with per-launch engine
   selector, Config/Logs/Setup, reusing shared UI components.
4. **WSJT-X setup-and-launch:** assign `Slice.DAXChannel`, surface `DAX RX {N}`
   / `DAX TX` names, guide CAT TCP port, launch with `--rig-name` + unique UDP
   port.
5. **JTDX engine:** same Digital Mode screens, engine = JTDX (separate
   exe/config dir); confirm parity.
6. **Help refactor** for the mode model.

## Implementation checklist

Ordered build sequence. **Multi-instance testing is intentionally last** (it
depends on everything below).

### A. Shell + mode framework — DONE (live-verified 2026-06-08)

- [x] Add a `Mode` concept (CW / Digital) + nullable `ActiveMode` (null until a
      mode is chosen; only Launch + Help show initially). `LastMode` persisted.
- [x] New **Launch tab** as entry point; radio discovery + connect moved here,
      plus a read-only stations/slices list and the event bar. (Network Status
      stays on the CW tab; not shown for digital for now.)
- [x] **CW Mode** mounts today's tabs **unchanged** (no `CwSkimmerLauncher` edits);
      Operating tab relabeled **CW**.
- [x] Mode switch confirms-and-stops the active family (confirm dialog).
- [x] **Close mode**: the active mode's Launch button toggles in place to
      "Close `<mode>` Mode" (the other stays a direct switch); returns to mode
      selection; radio stays connected; `LastMode` preserved for Auto Start.
- [x] Slim connected-radio indicator on non-Launch tabs (connection header).

### B. FlexRadio module (radio side, FlexLib)

- [~] `DAXChannel` **read** done (`SliceInfo.DaxAudioChannel` from
      `Slice.DAXChannel`, shown as `DAX RX N`); **set** (`slice set <id> dax=<N>`)
      deferred to its first caller (Digital config provisioning, E/F).
- [x] `DemodMode` surfaced for the mode notice (already in `SliceInfo`).

### C. CAT discovery (read-only) — DONE (unit-tested)

- [x] Parse `%APPDATA%\FlexRadio Systems\CAT.settings`; unescape `<PortList>` ->
      inner `ArrayOfPortSettings` (`CatSettingsReader`).
- [x] Map `Protocol=CAT && PortCommType=TCP` -> `TCPPortNumber` <-> `SliceIndex`;
      `FindPortForSlice` handles none / case-insensitive match.

### D. Digital subsystem — core DONE (unit-tested)

- [x] `DigitalEngineDefinition { Engine, DisplayName, ExePath, ConfigRoot }` +
      `DigitalEngines.WsjtX/Jtdx` factory.
- [x] `DigitalAppLauncher` (per-instance `--rig-name` launch/stop/IsRunning,
      `RunningStateChanged`; no telnet/sync/spot; injectable `IDigitalProcessRunner`
      for tests).
- [ ] Engine exe path config + Browse (defaults: `C:\WSJT\wsjtx\bin\wsjtx.exe`,
      `C:\JTDX64\<ver>\bin\jtdx.exe` — JTDX path is version-specific) — with the UI (F).
- [ ] Reuse shared services (audio discovery) — when the audio finder is
      extracted to a shared module (deferred until consumed, E/F).

### E. Config provisioning — core DONE (unit-tested)

- [x] **Template = clone the engine's working *default* config** (`DigitalConfigProvisioner`),
      so `@Variant` PTT/Data/Split, `Rig`, `Mode`, callsign, etc. carry over
      verbatim and version-safe. Returns `TemplateNotFound` if no default exists.
- [x] Per-slice seed via `ApplyOverrides`: `SoundInName=DAX RX <N>`,
      `CATNetworkPort=127.0.0.1:<port>`, unique `UDPServerPort`, shared
      `SoundOutName=DAX TX`. (`--rig-name` is a launch arg, applied in F/G.)
- [x] Write to `%LOCALAPPDATA%\<ConfigRoot> - <rig>\<ConfigRoot> - <rig>.ini`;
      preservation-first `IniEditor` (only touches `[Configuration]` keys).
- [~] Stale `(Not found)` / v1 audio names are **avoided by construction** for
      seeded instances (clone of the working default). A repair helper for
      pre-existing hand-made configs is optional/deferred.
- [ ] CAT port value comes from `CatSettingsReader` discovery — wired in F.

### F. Digital Mode UI — core DONE (live-verified 2026-06-08; design evolved, see notes)

- [x] Operating: **one Start/Stop row per existing slice** (dynamic). Row
      simplified to **Slice | Band (Freq) | Start/Stop** (band derived from the
      WSJT-X `Bands.cpp` ADIF table). Start button right-aligned.
- [x] **Soft** slice-mode-mismatch notice (non-blocking; `IsModeMismatch`,
      shown only when the slice is not USB/DIGU).
- [x] Config screen: **single active engine** (WSJT-X / JTDX radio buttons) +
      exe path/Browse; **per-slice editable DAX RX / CAT / UDP**; operator
      `Call`/`Grid` (prepopulated by harvesting an existing FlexRadio profile),
      `Rig` (fixed `FlexRadio 6xxx`, not shown).
- [ ] Logs scoped to mode/engine — **deferred** (future milestone).
- [~] Setup hub (WSJT-X / JTDX cards) — **deferred**; a "Digital Mode coming
      soon" placeholder was added to **Setup Guide page 1** instead. (Also fixed
      a latent bug: the guide's preamble page was never rendered.)

**Design deviations from the original sketch (decided during build):**

- **Engine is one active choice on the Config tab**, not a per-slice/per-launch
  selector. The operator runs WSJT-X *or* JTDX; switching is a config change and
  is **blocked while any instance runs** (engine radio buttons disabled).
- **DAX RX / CAT / UDP moved off the Operating row onto the Config tab** (one
  editable row per slice, pre-set to defaults: Slice A = DAX RX 1 / CAT 60000 /
  UDP 2237, B = 2/60001/2238, ...). The Operating row stays minimal.
- **Provisioning preserves an existing instance `.ini`** and re-applies only our
  keys (DAX RX / CAT / UDP / Call / Grid / Rig); it seeds from the bundled clean
  template only on first run. This keeps the geometry, last protocol
  (FT8/FT4/WSPR), and band that WSJT-X/JTDX save on exit. (A "last protocol"
  column was prototyped then **removed** — its readback updated inconsistently
  and added little value; light-touch reuse is the goal.)
- **Stop is graceful** (`Process.CloseMainWindow`, force-kill only after 5s) so
  the engine saves its settings; the tracked instance is held until it actually
  exits.

### G. Engines

- [x] WSJT-X engine end-to-end — launch `--rig-name`, provision, graceful stop,
      settings persistence **live-verified 2026-06-08**.
- [~] JTDX engine (config root `%LOCALAPPDATA%\JTDX`, default exe) — implemented
      via the same path (`JTDX-Improved` label); **single-instance live parity
      pending**.

### H. Quality gates

- [x] `dotnet build` warning-free (FlexLib transitive warnings exempt);
      `dotnet test` green (App 37 / CWSkimmer 42 / Digital 30); **existing CW
      Skimmer tests untouched**.
- [x] markdownlint clean; **Codex adversarial review run 2026-06-08** over
      `c6c3adc..HEAD`. Findings fixed: launcher restart-race + multi-instance
      stale-row + process-handle leak (lifecycle restructure: hold instance
      until exit, prune/dispose/notify per exit), engine-switch guard, and a
      nullable-suppression (`!`) removal. `IniEditor` "byte-exact" doc softened.

### I. Live validation (blocking)

- [x] CW Mode = CW Skimmer regression unchanged (CW path untouched except the
      shared disconnect handler + mode shell).
- [~] Single WSJT-X instance: seed `.ini`, launch, settings persist on stop —
      **verified**. (`Slice.DAXChannel` assignment via FlexLib still deferred,
      checkpoint B; operator sets the slice DAX channel in SmartSDR today.)
- [ ] JTDX single instance parity — pending.
- [x] Mode switch CW <-> Digital: clean teardown + resource release. Disconnect
      now stops **all** apps (CW + digital) and returns both modes to mode
      selection.
- [ ] **Multi-instance testing (LAST):** run up to 4 concurrent instances on a
      FLEX-6600 — per-slice `--rig-name`, CAT `60000-60003`, `DAX RX 1-4`, unique
      UDP ports, shared `DAX TX`; verify all decode and no port/UDP collisions.
      (In progress as of 2026-06-08.)

## Open items (confirm during implementation)

- **Auto Start Last Mode (planned UX).** Opt-in setting + a checkbox on the
  Launch tab; when enabled, set `ActiveMode = LastMode` on startup (or right
  after connect) instead of leaving it null. Hooks already in place: nullable
  `ActiveMode` + persisted `LastMode` (preserved across Close Mode). TBD: auto-
  activate at startup vs. after a radio connects.
- ~~WSJT-X audio-device name~~ **RESOLVED** by FlexRadio's DAXv2 article: WSJT-X
  Input = `DAX RX {N}`, Output = `DAX TX`. ~~CAT transport~~ **RESOLVED**: TCP
  Network Server `LocalHost:<60000+>`, not serial.
- ~~Exact WSJT-X **Rig** entry~~ **RESOLVED** by the working config: **Rig =
  `FlexRadio 6xxx`**, **Network Server = `127.0.0.1:60000`** (SmartSDR CAT TCP
  port). `Test CAT` green end-to-end on 2026-06-08.
- Whether assigning `Slice.DAXChannel` via FlexLib reliably brings up the DAX
  RX endpoint under DAXv2 (expected yes; confirm on hardware).
- **CAT TCP-port handling.** **Prefer FlexLib API throughout** — and it covers
  the whole radio side: radios, stations (`GuiClients`), slices + `DemodMode`,
  `Slice.DAXChannel`, DAX audio streams. **Finding (verified in FlexLib 4.2.20):**
  FlexLib does **not** expose the SmartSDR CAT.exe TCP ports; its only CAT
  surface is `IUsbCatCable` (a physical USB CAT *cable* accessory), unrelated to
  the CAT app's virtual ports. FlexLib can therefore **neither create/configure
  nor enumerate** SmartSDR CAT ports (full-source grep: no CAT-port API). CAT
  port creation is **operator-only** in CAT.exe.
- **CAT port discovery source (found, verified):** `%APPDATA%\FlexRadio
  Systems\CAT.settings` (XML, + `.backup`). Its `<PortList>` is an **XML-escaped
  inner** `ArrayOfPortSettings`; each `<PortSettings>` carries `<Protocol>`
  (`CAT`), `<PortCommType>` (`TCP`|`Serial`), `<TCPPortNumber>`, `<SliceIndex>`
  (`A`/`B`/...), `<Name>`, `<AutoSwitchTXSlice>`. **Discovery algorithm:** read
  CAT.settings -> unescape `<PortList>` -> parse inner XML -> filter
  `Protocol=CAT && PortCommType=TCP` -> map `TCPPortNumber` -> `SliceIndex`, then
  auto-fill WSJT-X Network Server `127.0.0.1:<port>` for the chosen slice. So the
  wizard can **guide** port creation (operator) yet **auto-discover** the result
  (read-only file parse) — no FlexLib, no registry. Writing CAT.settings to
  create ports is **not** advised (CAT.exe owns the file; reload semantics
  unknown).
- ~~JTDX parity~~ **RESOLVED**: settings identical to WSJT-X (incl. `@Variant`
  blobs); config root `%LOCALAPPDATA%\JTDX\JTDX.ini`. Default exes:
  `C:\WSJT\wsjtx\bin\wsjtx.exe`, `C:\JTDX64\159\bin\jtdx.exe` (JTDX path is
  version-specific -> keep configurable). Remaining: confirm `--rig-name` dir
  `JTDX - xxx` for multi-instance.
- ~~CW gate: hard block vs soft warning~~ **DECIDED: soft notice for now** for
  both modes (non-blocking); revisit a hard block later if misconfig is common.
- Slice demod-mode strings to gate on (e.g. `CW`/`CWU`/`CWL` vs `USB`/`DIGU`)
  as exposed by FlexLib `Slice.DemodMode` / our `SliceInfo`.

## Live-test checklist (operator)

**DONE 2026-06-08** — one WSJT-X instance running on DAXv2 for Slice A; known-good
values captured (Rig `FlexRadio 6xxx`, CAT `127.0.0.1:60000`, audio `DAX RX 1` /
`DAX TX (FlexRadio DAX)`) and recorded as the template in the WSJT-X facts
section. JTDX parity also confirmed (same settings; config root
`%LOCALAPPDATA%\JTDX\JTDX.ini`). Multi-instance **binding is known** (operator-
confirmed: shared `DAX TX`; per-slice RX, Slice B = `DAX RX 2`), so a second
instance just reuses the template with its own CAT TCP port + `DAX RX 2` +
`--rig-name` + UDP port. Remaining live confirmation (later pass): actually
running two concurrent instances, and `Slice.DAXChannel` assignment via FlexLib.

Original goal: get one WSJT-X instance working on DAXv2, then capture the
known-good config so we can templatize it.

1. **SmartSDR CAT:** create a **TCP** port; note its number; bind it to the
   slice letter; enable **Auto Switch TX Slice**.
2. **WSJT-X Settings -> Radio:** set **Network Server = `127.0.0.1:<that CAT TCP
   port>`**; **PTT = CAT**, **Mode = Data/Pkt**, **Split = None**; press
   **Test CAT** -> must go green. **Record which `Rig` entry makes Test CAT
   green** (FlexRadio 6xxx? Ham Radio Deluxe? Kenwood TS-2000? Hamlib NET?) —
   this is the key open item.
3. **WSJT-X Settings -> Audio** (DAX app running first): re-select **Input =
   `DAX RX 1 (FlexRadio DAX)`**, **Output = `DAX TX (FlexRadio DAX)`**; record
   the exact strings shown.
4. **SmartSDR:** slice **MODE = DIGU**, DAX channel matching the input; enable
   the **TX DAX** button. Confirm RX decodes and Test PTT keys the radio.
5. **Capture the working `.ini`** (`%LOCALAPPDATA%\WSJT-X - <name>\...ini`):
   the `Rig`, `CATNetworkPort`, `SoundInName`, `SoundOutName` values — these
   become our template/defaults.
6. (Optional, our side) confirm setting `Slice.DAXChannel` via FlexLib brings up
   the DAX RX endpoint under DAXv2.

## Verification

- Unit: all existing CW Skimmer tests stay green (CW path is untouched); add
  tests for `DigitalAppLauncher` and the `DAX RX {N}` name resolver.
- Build/markdown gates per `CLAUDE.md`.
- **Launch-tab mode notice:** with a mismatched slice (CW slice + Digital, or
  USB/DIGU slice + CW), a soft notice appears but launch is still allowed;
  changing the slice's mode on the radio re-evaluates the notice live.
- **Live-radio (blocking):** on SmartSDR 4.2.20, (a) CW Mode = CW Skimmer
  regression unchanged; (b) WSJT-X single instance: assign slice DAX channel,
  launch, RX decodes, TX keys via CAT; (c) two concurrent instances per the
  known binding (Slice A = DAX RX 1, Slice B = DAX RX 2, distinct CAT/UDP +
  `--rig-name`, shared DAX TX); (d) mode switch CW <-> Digital cleanly stops the
  active family and frees its resources.

## References

- WSJT-X User Guide (current, 3.0.1): <https://wsjt.sourceforge.io/wsjtx-main_en.html>
- FlexRadio: Configuring WSJT-X for Aurora/FLEX using DAXv2 (Cloudflare-gated): <https://helpdesk.flexradio.com/hc/en-us/articles/49224229297563-Configuring-WSJT-X-for-the-Aurora-and-FLEX-Series-Radios-using-DAXv2>
- SmartSDR v4.2.20 release notes (2026-06-03): <https://www.flexradio.com/documentation/smartsdr-v4-2-20-release-notes/>
- Configure Multiple WSJT-X Instances with SmartSDR v4.2 (video + community write-up): <https://youtu.be/mfA3jJSVtgU>
- Community: <https://community.flexradio.com/discussion/8033826/configure-multiple-wsjt-x-instances-with-smartsdr-v4-2>
- SliceMaster (prior-art benchmark; its WSJT-X automation is broken under DAXv2): FlexRadio videos page.

## Discarded approaches (for reference)

Kept for review in case we revisit; none are current.

- **Option A — Evolved Tabs (app-aware single tab set).** One shared
  Operating/Config/Logs tab set with an "active app" selector and per-slice app
  rows. Discarded: it modified the existing CW Skimmer tabs (regression risk to
  the proven path) and mixed IQ/telnet and audio concerns on one screen.
  Superseded by mode separation + the Launch tab.
- **Soft mode (UI lens) + cross-family concurrency.** Mode as the visible
  surface only, with CW Skimmer and WSJT-X able to run at once and a header
  "running in other mode" badge. Discarded in favor of hard modes: cross-family
  concurrency added shared DAX/CAT arbitration, dual live binding types, and a
  large live-test matrix for a rare workflow. Reversible later if justified.
- **`IExternalApp` retrofit of `CwSkimmerLauncher`.** A single generic
  cross-family launcher spanning CW Skimmer + digital apps. Only needed to serve
  concurrency; discarded so the CW path stays untouched and Digital gets its own
  `IDigitalApp` / `DigitalAppLauncher`.
- **Startup mode-selector dialog + header mode switch.** A bare mode picker
  before the main window plus a persistent header toggle. Superseded by the
  Launch tab, which makes the choice contextual (gated by slice state) and folds
  in discovery/connection.
