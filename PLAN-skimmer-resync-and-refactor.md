# Plan — CW Skimmer post-band-change resync fix + refactor

Working notes for the `fix/skimmer-resync-after-band-change` branch.
Two independent tracks: **Part A** is an operational bug fix, **Part B** is a
multi-phase cleanup. Part A should ship on its own; Part B is incremental and
can be sequenced however we like.

---

## Part A — Operational gap: post-band/large-QSY resync drop

### Symptom

After a direct slice QSY that crosses bands (e.g., dial 3.550 MHz from
the operating page while the slice is on 7.055 MHz), CW Skimmer's main-
window VFO display does not update and decoded callsigns/spots go stale.
A proper band change initiated from the SmartSDR client UI works fine —
the failure mode is specifically the slice-only QSY path.

Operator's manual workaround is to nudge the slice VFO ±a few Hz, which
forces the VFO display to update.

### Root cause — slice/pan event ordering plus tracker idempotence

The bug is *not* CW Skimmer dropping commands during IQ-pipeline rebuild
(the original hypothesis). Proof: a SmartSDR-UI band change, which also
emits `SKIMMER/LO_FREQ`, works without any settle delay.

The actual cause is a two-event ordering race in `MainWindowViewModel`:

- `TrySyncSliceToSkimmer` (fires on `slice.Frequency` change) only sends
  `vfoMHz` to the tracker — never `loHz`.
- `TrySyncSkimmerForPanChange` (fires on `pan.CenterFreq` change) sends
  both.

When the operator does a direct slice QSY across bands, FlexLib fires the
slice-frequency event first; the panadapter-recenter event follows some
ms later. The tracker sees:

1. `RequestSync(vfoMHz: 3.550)` → emits `SKIMMER/QSY 3550.000` against
   CW Skimmer's old LO context (still on 7 MHz). CW Skimmer accepts the
   command but it's outside the IQ passband — no decode.
2. `RequestSync(loHz: 3_500_000, vfoMHz: 3.550)` → LO changed → emits
   `SKIMMER/LO_FREQ 3500000`. VFO matches `_lastSentVfoMHz` → **QSY is
   suppressed by idempotence**. CW Skimmer rebuilds IQ at 3.5 MHz but
   never receives a fresh QSY in that new context.

The operator's nudge works because it generates a new desired VFO value,
breaking idempotence and emitting a fresh QSY *after* the LO is correct.

A SmartSDR-UI band change avoids the race because slice and pan events
fire in the right order (or together), so step 2 above is the *first*
emit and the QSY-after-LO is correctly sent in the same iteration.

### Fix — two complementary changes

#### Layer 1 — invalidate VFO last-sent after a successful LO emit

**File:** `src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerSyncTracker.cs`
**Where:** inside `RunAsync`, between the LO emit block and the VFO emit
block.

```csharp
bool loEmitted = false;
if (desiredLo.HasValue && desiredLo != _lastSentLoHz)
{
    try
    {
        await _telnet.SendLoFreqAsync(desiredLo.Value, ct);
        _lastSentLoHz = desiredLo;
        loEmitted = true;
    }
    catch (OperationCanceledException) { return; }
    catch (Exception ex) { _onStatus?.Invoke($"LO sync failed: {ex.Message}"); }
}

// Re-assert VFO/QSY after a successful LO change so a stale QSY (sent
// before the LO caught up) is replayed in the new LO context.
if (loEmitted)
    _lastSentVfoMHz = null;
```

This handles the slice-fires-before-pan ordering: when the late pan
event finally pushes the new LO through the tracker, Layer 1 forces the
VFO branch to re-emit even though desired VFO matches last-sent.

#### Layer 2 (Option 2) — pass current pan center on every slice change

**File:** `MainWindowViewModel.cs`
**Where:** inside `TrySyncSliceToSkimmer`, replacing the single-arg
`RequestSkimmerSync` call.

```csharp
var daxIqChannel = ResolveSliceDaxIqChannel(slice);
if (daxIqChannel <= 0)
    return;

var pan = _connection.Panadapters.FirstOrDefault(p => p.StreamId == slice.PanadapterStreamId);
var loHz = pan is not null && pan.CenterFreqMHz > 0
    ? (long)Math.Round(pan.CenterFreqMHz * 1_000_000d)
    : (long?)null;

_launcher.RequestSkimmerSync(daxIqChannel, loHz: loHz, vfoMHz: effectiveRxFreqMHz);
```

This handles the pan-fires-before-slice ordering: by the time the slice
event arrives, the pan model is already current, and the tracker sees a
matching LO + VFO change in one call. The tracker's existing code emits
`LO_FREQ` then `QSY` in the right order, and CW Skimmer never sees a
stale QSY.

#### Layer 3 — dropped (delayed stability resend)

The original plan proposed a +1.2 s coalesced re-assert after LO changes
to defeat an IQ-pipeline-rebuild settling window. That hypothesis turned
out to be wrong (proper UI band change works without any delay), so the
delayed resend has been removed from scope. CTS lifecycle, status-log
noise question, and the tunable-delay open question all fall out with
it.

### Together: why both layers are needed

Each layer covers one half of the FlexLib event-ordering race we cannot
control:

| FlexLib event order        | Today  | Layer 1 alone | Option 2 alone | Layer 1 + Option 2 |
|----------------------------|--------|---------------|----------------|--------------------|
| pan event before slice     | bug    | fixed         | fixed          | fixed              |
| slice event before pan     | bug    | fixed         | broken         | fixed              |

### Spam-prevention analysis

Both changes are bounded by the existing idempotence checks in the
tracker. Neither adds resend timers, retries, or per-event amplification.

- **Option 2** passes the current pan center on every slice change.
  When LO is unchanged, idempotence skips the LO emit. Telnet TX count
  is unchanged for in-band tunes.
- **Layer 1** only fires when an LO actually emits. Per LO emit it
  causes one extra VFO emit (or reuses one that would have happened
  anyway in a band change). Worst case per LO change: +1 QSY.

### Tests

New file: `tests/SmartSDRIQStreamer.CWSkimmer.Tests/CwSkimmerSyncTrackerTests.cs`

Internal `FakeTelnetClient` implementing `ICwSkimmerTelnetClient`,
records every `SendLoFreqAsync` and `SendQsyAsync` call, with optional
`Func<long, Exception?> LoThrow` / `Func<double, Exception?> QsyThrow`
for failure injection.

| Test                                                       | Asserts                                                                                                          |
|------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------|
| `LoChangeWithUnchangedVfo_ReassertsVfoOnSameIteration`     | After initial sync, an LO-only change re-emits the same VFO value (Layer 1)                                      |
| `RepeatedSameVfoNoLo_DoesNotResend`                        | Idempotence still holds when LO doesn't change (regression check)                                                |
| `LoChange_TelnetThrowsOnLo_DoesNotInvalidateVfoState`      | If LO emit throws, `loEmitted` stays false → Layer 1 invalidation skipped → no spurious QSY re-emit              |

Option 2 is covered indirectly by the tracker tests (the change is a
6-line lookup that funnels into the same `RequestSync(loHz, vfoMHz)`
contract).

### Doc updates that ride with Part A

- `README.md:71–72` — drop the stale "short delayed QSY stability
  resend" line and replace with: *"On any `SKIMMER/LO_FREQ` change the
  matching `SKIMMER/QSY` is re-asserted in the same iteration so CW
  Skimmer's VFO display tracks panadapter recenters and cross-band
  slice tunes even when slice and pan events arrive out of order."*
- `CwSkimmerSyncTracker.cs:9–13` — drop "stability resends" from the
  "replaces" list and document the Layer 1 invalidation behavior.

---

## Part B — Refactoring plan

Independent of Part A. Each phase is its own PR per CONTRIBUTING.md.

### Phase 1 — Doc/code drift (1 PR, ~30 min)

- `README.md:65` — strike the *"always set `UseWdm=0`"* claim. Describe the
  operator-WDM-index branch that already exists at
  `CwSkimmerIniModelFactory.cs:74`.
- `README.md:149` and `README.md:185` — `SmartSDR-IQ-Streamer.MDC` does not
  exist at the repo root. The actual file is
  `.cursor/rules/architecture-decisions.mdc`. Either move it up or fix the
  references.
- `CONTRIBUTING.md:48` — recheck whether the .NET 8 SDK respects
  `SmartSDRIQStreamer.slnx` for `dotnet test` from the repo root. If yes,
  simplify the doc.
- `README.md` §9 (Phase Status) — mostly stale victory laps. Either prune or
  replace with a "What's next" list.
- `README.md` §11 — duplicates `Flexlib4-2-Migration-Guide.md`. Collapse to a
  one-line pointer.
- Rename the misleading test
  `Build_AlwaysForcesUseWdmFalse_RegardlessOfMasterIniSetting` to
  `Build_DefaultsToMmeMode_WhenNoOperatorWdmIndex`. Add a paired
  `Build_EmitsWdmMode_WhenOperatorWdmIndexSupplied` to lock in the actual
  current behaviour.
- `CwSkimmerIniModelFactory.cs:139–141` — comment says calibration is
  accepted *"if WDM fields are present (legacy users) OR if MmeAudioDev is
  set"*, but the `return` only allows the WDM path. Decide which is right
  and align both.

### Phase 2 — Slim `MainWindowViewModel.cs` (currently 2188 lines)

Independent extractions. Do them one PR at a time. Target sizes are rough.

| Extract | Owns | Approx. lines |
|---------|------|---------------|
| `UpdateCheckCoordinator` | the polling loop, "last announced tag" state, manual/auto check entrypoints | ~120 |
| `SpotColorPalette` | the two `SpotColorOptions` lists + selection sync logic | ~80 |
| `EchoSuppressionState` | `_lastOutboundQsyByChannel` / `_lastInboundClickByChannel` + `ShouldSuppressInboundClick` + `RecordOutboundQsy` | ~100 |
| `RitSyncCoordinator` | `_ritStatusEmitter` + RIT change tracking + last-RIT-state map | ~80 |
| `DaxKbpsCalculator` | `UpdateDisplayedDaxKbps` fallback math | ~30 |
| `SkimmerStatusFormatter` | `FormatLaunchSuccess` / `FormatDeviceNotFound` (already in `CwSkimmerWorkflowService`) plus device-preview status | ~50 |

Realistic landing: ViewModel drops to ~1500 lines, which is still big but
is genuinely *"owns the bindable UI state and command surface."*

### Phase 3 — Tighten error visibility

A single `Action<string> reportError` (or use existing `AddStreamerStatus`)
plumbed into:

- `AppSettingsStore.Load/Save` — currently `catch { /* ... */ }`
- `MainWindow.OnOpenSupport` — currently `catch { }`
- `WdmAudioDeviceFinder.Enumerate*` — currently swallows the WinMM count
  call failure
- `CwSkimmerTelnetClient.PerformLoginAsync` — the *"treated timeout as
  success"* path is logged via `LogDiag` but not surfaced to the Logs tab;
  add an `EmitStatus` line so it's visible.

### Phase 4 — Test coverage gaps

- `CwSkimmerSyncTracker` — covered by Part A's tests, listed above.
- `CwSkimmerWorkflowService.IsLikelyCallsign` — pin the `0x32F4599F`
  rejection and the slash/hyphen acceptance.
- `ReleaseUpdateService.CompareTags` — pin `alpha < beta < rc < stable`
  ordering plus numbered suffixes.
- `FlexLibRadioDiscovery.ResolveStations` — dedup + sort behaviour.

---

## Sequencing recommendation

1. **Part A (Layer 1 + Option 2)** — small, fixes a real operator-visible
   bug, ~20 lines of code plus tests.
2. **Phase 1 (doc drift)** — fast, low risk, immediately makes the README
   match what the code actually does.
3. **Phase 2/3/4 in any order** — pick whichever extraction feels most
   painful next time the ViewModel is touched.

## Open questions

None remaining for Part A — the IQ-pipeline-rebuild hypothesis was
discarded, and with it the stability-resend timer, CTS lifecycle, and
status-log noise questions.
