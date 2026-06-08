# SmartStreamer4 Setup Guide

This guide is reference material - prerequisites, what each tab does, and a
troubleshooting decision tree. For the hands-on first-time setup flow, click
**Reset / Setup Wizard** on the Config tab (it auto-opens on first install
once both CW Skimmer paths are set). The wizard launches CW Skimmer, walks
the Settings tabs, and captures the per-PC MME and WDM device numbers needed
for multi-channel operation.

**Digital Modes (WSJT-X / JTDX):** this guide currently covers CW Skimmer
(CW Mode). Setup guidance for Digital Mode is coming soon.

---

## Step 0 - Prerequisites Checklist

Before launching the streamer, confirm:

- Windows 10/11 is supported today (FlexLib .NET 8.0 requirement)
- FLEX-6000/8000 radio is powered on and connected to the local network
- SmartSDR 4.x installed and running (SmartStreamer4 is designed for SmartSDR 4.x; firmware ≥ 3.3.32 required)
- DAX 4.x installed and running
- CW Skimmer v2.1 installed

If any item above is missing, stop and fix that first. Common issues include:

- Not enabling a DAX-IQ stream in the SmartSDR panadapter.
- Not enabling the matching DAX-IQ channel in SmartSDR DAX (blue/streaming).

- The radio and skimmer software prerequisites can be downloaded from these sites as of 4/20/2026:
  - [https://www.flexradio.com/ssdr/](https://www.flexradio.com/ssdr/)
  - [https://www.dxatlas.com/CwSkimmer/](https://www.dxatlas.com/CwSkimmer/)

![Streamer App](Assets/SetupWizard/StreamerApp.png)

---

## About the Reset / Setup Wizard

The **Reset / Setup Wizard** (Config tab) is the recommended way to do
first-time setup or reconfigure CW Skimmer for SmartStreamer4. It is *not*
just a destructive cleanup — it is an interactive 4-step walkthrough that:

1. Launches CW Skimmer with your configured exe path so you can size and
   position the window where you want it.
2. Guides you through the Settings → Radio tab values (SoftRock, 48 kHz,
   Audio IF=0).
3. Asks you to choose **MME (recommended)** vs **WDM (experimental)** as the
   Soundcard Driver mode, then captures the per-channel device numbers shown
   in CW Skimmer's Audio tab dropdowns. WDM dropdown ordering differs by PC,
   so this manual capture is the only reliable way to drive multi-channel
   WDM (see [issue #19](https://github.com/cdub89/SmartStreamer4/issues/19)).
4. Verifies CW Skimmer is closed (so your settings actually save), then
   deletes the per-channel INIs so they re-seed from your updated master.

Open the wizard when:

- You are setting up SmartStreamer4 for the first time (it auto-opens once
  both paths point to existing files).
- You want to switch Soundcard Driver mode (MME ↔ WDM).
- You moved to a different PC and the WDM device numbers changed.
- A release explicitly notes a channel-INI schema change.

The wizard is harmless to re-run: it never modifies your master
`cwskimmer.ini`, only the streamer-managed channel files.

---

## Step 1 - Paths and File Locations

The wizard needs two paths set on the Config tab before it can launch
CW Skimmer:

Typical CwSkimmer.exe path:

```text
C:\Program Files (x86)\Afreet\CwSkimmer\CwSkimmer.exe
```

Typical CwSkimmer.ini path (replace user name with your own):

```text
C:\Users\chris\AppData\Roaming\Afreet\Products\CwSkimmer\CwSkimmer.ini
```

Use the Browse buttons on the Config tab to locate both. Once both point at
existing files, the Reset / Setup Wizard auto-opens on first install.

---

## Step 2 - What the Wizard Configures (Reference)

The wizard handles the Radio and Audio tabs end-to-end. This section is
reference material if you are diagnosing what the wizard wrote.

### Radio tab values the wizard sets

- Hardware Type: SoftRock
- Sample Rate: 48000 Hz
- Audio IF: 0 Hz
- LO Frequency: rewritten on every launch from the live panadapter center.

![CW Skimmer Radio tab example](Assets/SetupWizard/image-8d3c7eef-09f5-4652-8c05-93bf3fe4a9bd.png)

### Audio tab values

- **Soundcard Driver**: MME (recommended) or WDM (experimental). The wizard
  asks the operator to pick.
- **Signal I/O Device**: in MME mode, auto-derived by looking up
  `DAX IQ {N}` in the live WinMM enumeration; in WDM mode, taken from the
  per-channel numbers the operator captures in the wizard.
- **Audio I/O Device**: copied verbatim from the master INI - this is your
  local speakers/headphones, used only for CW Skimmer's local audio
  monitoring.
- **Channels**: `Left/Right = I / Q`
- **Shift Right Channel Data by**: `0 samples`

#### DAX IQ endpoint friendly names

The Signal I/O Device dropdown the wizard auto-fills shows different
friendly names depending on which SmartSDR version installed the DAX
driver. The wizard matches on the `DAX IQ {N}` prefix so both forms
work, but the strings you see in the dropdown (and in the Windows
Sound control panel) differ:

| Channel | SmartSDR 4.2.x             | SmartSDR 4.1.5                              |
| ------- | -------------------------- | ------------------------------------------- |
| 1       | `DAX IQ 1 (FlexRadio DAX)` | `DAX IQ RX 1 (FlexRadio Systems DAX IQ)`    |
| 2       | `DAX IQ 2 (FlexRadio DAX)` | `DAX IQ RX 2 (FlexRadio Systems DAX IQ)`    |
| 3       | `DAX IQ 3 (FlexRadio DAX)` | `DAX IQ RX 3 (FlexRadio Systems DAX IQ)`    |
| 4       | `DAX IQ 4 (FlexRadio DAX)` | `DAX IQ RX 4 (FlexRadio Systems DAX IQ)`    |

If neither form appears in the dropdown, DAX is not installed or the
DAX service is not running. The startup gate (added in v0.1.18b)
catches the second case before you reach this step.

### Operator and Network tabs

These are not rewritten by the streamer (Network telnet port is the
exception - it is rewritten on every launch). Set callsign and operator
defaults manually inside CW Skimmer before closing.

![CW Skimmer Network tab example](Assets/SetupWizard/image-6013668c-9a3b-4a87-a15d-5e9acfd2b129.png)

---

## Step 3 - Spot Persistence and Colors

Configured on the Config tab independent of the wizard.

- In `Config`, use `Persist` to control spot lifetime (seconds) for newly published spots.
- Use `Txt` to choose spot text color and `Bg` to choose spot background color.
- Start with a readable combination (for example yellow text on transparent/dark background).
- Validate by publishing at least one known spot and confirming appearance in SmartSDR panadapter.

Streamer Config example:
![Streamer Config tab example](Assets/SetupWizard/image-f6601ad8-b7e1-485c-93aa-e2e9f46f62a8.png)

---

## Step 4 - Connect and Validate Radio State

1. Go to `Operating`.
2. Select radio target and click `Connect`.
3. Confirm connected station/pan/slice state appears correctly.
4. Confirm DAX-IQ context is available for intended channels.

Operating tab connected example:
![Operating tab connect example](Assets/SetupWizard/image-28013ef7-a67d-4c10-a6f4-02147c7584cd.png)

If connect fails, resolve radio/network/discovery issues before continuing.

---

## Step 5 - Start and Stop Skimming

Per slice, use the skimmer action button:

- `Start` means skimmer is not running for that slice channel.
- `Streaming` means skimmer is running for that slice channel.

Normal flow:

1. Click `Start` on target slice row.
2. Wait for CW Skimmer window and startup status.
3. Verify decode activity and expected frequency behavior.

Operating tab while skimming example:
![Connected operating view example](Assets/SetupWizard/image-5c26a00e-a128-4871-a1b8-7ac7a88e4355.png)

To stop from streamer:

1. Click `Streaming` on the active slice row.
2. Confirm CW Skimmer instance stops for that channel.

To stop manually (required if you've made config changes you want saved for the next time you start CW Skimmer):

1. Close CW Skimmer window directly.
2. Confirm streamer status reflects stopped state.

If radio is disconnected from streamer, streamer should also stop active skimmer instances.

---

## Step 6 - Troubleshooting (Quick Decision Tree)

### A) CW Skimmer does not launch

- Recheck `CwSkimmer.exe` path in streamer `Config`.
- Confirm DAX devices exist and are visible to CW Skimmer.
- Check `artifacts/logs` and streamer `Logs` tab for launch diagnostics.
- If CW Skimmer crashes within ~10 seconds with no message
  (`exit_code=-1073740771` / `STATUS_FATAL_USER_CALLBACK_EXCEPTION` in the
  logs), this is a known intermittent CW Skimmer startup fault. Click
  `Start` again from SmartStreamer4 — it usually launches cleanly on the
  second attempt.

### B) Wrong audio/input behavior after launch

- Re-run the **Reset / Setup Wizard** from the Config tab. Step 3 lets you
  re-capture the per-channel MME/WDM device numbers and switch driver mode.
- The wizard's "Reset and Done" deletes the per-channel INIs so the next
  launch re-seeds with your updated values. The streamer only writes the
  `[Audio]` section on first creation, so deleting is what makes wizard
  changes take effect.
- Your manual `CwSkimmer.ini` baseline is never modified.
- **Logs are separate** and not affected by Reset. They are append-only
  diagnostic data under `artifacts/logs/`; if disk usage is a concern,
  delete them manually.

### C) Settings not retained as expected

- Confirm master INI is stable when CW Skimmer is run standalone.
- Confirm per-channel INI already exists and is not being replaced unexpectedly.
- Verify whether close path was manual CW close, streamer stop, or disconnect.

### D) No decode / poor decode

- Confirm correct DAX IQ channel routing and levels.
- Check sample rate consistency between radio/DAX/CW expectations.
- Validate center frequency and tuning sync behavior in logs.

### E) Telnet/sync anomalies

- Check for local port conflicts.
- Check firewall/endpoint protection rules for local loopback behavior.
- Review `[TELNET]` lines in streamer logs.

---

## Step 7 - Artifacts, Logs, and First-Time Validation

Artifacts and logs reference:

- Streamer logs: `artifacts/logs/streamer-status.log`
- Spot publish logs: `artifacts/logs/spot-publish.log`
- CW Skimmer managed INIs: `artifacts/cwskimmer/ini`
- Device diagnostic log: `artifacts/cwskimmer/ini/device-diagnostic.txt`

Example of healthy connected operating state during validation:
![Operating view with active skimmer](Assets/SetupWizard/image-4966f52d-3ccc-4ecf-b274-2fe2c1a79fd6.png)

Recommended first-time validation run:

1. Complete Steps 1 through 4.
2. Start one channel with `START`.
3. Validate decode and sync for at least 5 minutes.
4. Stop and restart once.
5. Disconnect/reconnect radio once.
6. Confirm behavior and settings remain consistent.

If all checks pass, the system is ready for normal operation.
