# Plan — CW Skimmer post-band-change resync fix + refactor

Working notes for the `fix/skimmer-resync-after-band-change` branch.
Two independent tracks: **Part A** is an operational bug fix, **Part B** is a
multi-phase cleanup. Part A should ship on its own; Part B is incremental and
can be sequenced however we like.

---

## Part A — Operational gap: post-band/large-QSY resync drop

### Symptom

After a band change or large QSY, CW Skimmer sometimes does not resync — its
main-window VFO display stays on the old frequency and decoded callsigns/spots
go stale. Operator's manual workaround is to nudge the slice VFO knob ±a few
Hz, which forces the VFO display to update.

Frequency of occurrence: not quantified, but reproducible enough that the
operator has learned the workaround.

### Root cause

Almost certainly a regression introduced when `CwSkimmerSyncTracker` replaced
the older event-push / debounce / stability-resend stack. Three pieces line
up:

1. `README.md:71–72` still advertises *"A short delayed QSY stability resend
   follows pan/band-driven LO updates to keep CW Skimmer VFO display aligned
   after transient UI/state shifts."*
2. `CwSkimmerSyncTracker.cs:9–13` says it *"Replaces the previous mix of
   event-push, debounce queues, stability resends, and telnet-layer duplicate
   suppression."* The stability resend was deleted on purpose.
3. `CwSkimmerSyncTracker.cs:90–111` — the loop only emits LO/QSY when
   `desired != _lastSent`. After an LO change, the QSY *is* sent once and
   `_lastSentVfoMHz` is updated. The loop never re-asserts.

Why CW Skimmer drops the QSY: an `LO_FREQ` change forces CW Skimmer to tear
down and rebuild its IQ pipeline. For a few hundred ms after that change,
commands can be accepted on the telnet socket but quietly ignored before the
new pipeline is ready. The old design covered this with an explicit resend;
the rewrite cleaned it out.

The operator's manual nudge works because it generates a *new*
`_desiredVfoMHz`, which breaks the idempotence guard and emits a fresh QSY
*after* the pipeline has settled.

### Fix — two layers (Layer 3 dropped)

#### Layer 1 — invalidate VFO last-sent when LO changes

**File:** `src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerSyncTracker.cs`
**Where:** inside `RunAsync`, immediately after a successful `SendLoFreqAsync`.

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

// Layer 1: a successful LO change forces the QSY in this same iteration to
// re-emit, even if numerically identical to the last value sent. CW Skimmer's
// IQ pipeline restart on band change can silently drop a QSY that matches
// the pre-restart state.
if (loEmitted)
    _lastSentVfoMHz = null;
```

Rationale: this alone catches the case where LO and QSY are coalesced into
the same iteration but CW Skimmer hadn't finished rebuilding by the time we
sent QSY.

**Adds zero new telnet traffic.** Same call count as today; just stops
suppressing one of the two QSYs we already wanted to send.

#### Layer 2 — coalesced +1.2s stability resend

**File:** same.
**Where:** after the QSY emit block in `RunAsync`, plus a new private helper
`ScheduleStabilityResend`, plus a new field `_stabilityResendCts`, plus
disposal handling in `DisposeAsync`.

```csharp
private static readonly TimeSpan StabilityResendDelay = TimeSpan.FromMilliseconds(1200);
private CancellationTokenSource? _stabilityResendCts;

// At end of the loop body, after the QSY emit block:
if (loEmitted)
    ScheduleStabilityResend(ct);

private void ScheduleStabilityResend(CancellationToken parentCt)
{
    CancellationTokenSource fresh;
    CancellationTokenSource? previous;
    lock (_gate)
    {
        previous = _stabilityResendCts;
        fresh = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        _stabilityResendCts = fresh;
    }

    if (previous is not null)
    {
        try { previous.Cancel(); } catch (ObjectDisposedException) { }
        previous.Dispose();
    }

    _ = Task.Run(async () =>
    {
        try { await Task.Delay(StabilityResendDelay, fresh.Token); }
        catch (OperationCanceledException) { return; }

        lock (_gate) { _lastSentVfoMHz = null; }
        _onStatus?.Invoke(
            $"QSY stability re-assert firing (+{StabilityResendDelay.TotalMilliseconds:F0}ms after LO change).");

        try { _wakeup.Release(); }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException)  { }
    });
}
```

Update `DisposeAsync` to dispose the latest `_stabilityResendCts` after
cancelling `_cts` (the linked token will already cause the pending
`Task.Delay` to throw `OperationCanceledException`, so the resend task exits
quickly).

#### Layer 3 — dropped

Originally proposed as a manual "Resync" button per channel. **Dropped**
because the operator is normally focused on CW Skimmer or the radio, not on
SmartStreamer4. By the time someone alt-tabs to find a button, they've
already nudged the VFO knob.

The status-log breadcrumb that was the only useful part of Layer 3 is folded
into Layer 2's `_onStatus` callback. The launcher prefixes channel context
(`CwSkimmerLauncher.cs:607`), so a typical operator log entry will read
`ch 2: QSY stability re-assert firing (+1200ms after LO change).`

### Spam-prevention analysis (why this won't flood telnet)

Three guards, in order of how much they limit traffic:

1. **Layer 1 adds zero new traffic.** It removes a suppression, doesn't add a
   send. Per band change: still 1 LO_FREQ + 1 QSY. The QSY just gets through
   reliably now.
2. **Layer 2 is bounded by the existing idempotence check** at
   `CwSkimmerSyncTracker.cs:101`. The +1.2s re-assert clears `_lastSentVfoMHz`
   and pulses the wakeup; the loop body still reads
   `if (desiredVfo != _lastSentVfoMHz)`. So:
   - Operator's desired VFO unchanged → resend fires once, idempotence shuts
     it down.
   - Operator kept tuning → loop already emitted the newer value, resend
     becomes a no-op.

   **Worst case per band change: 1 LO_FREQ + 2 QSY.** Less than a manual VFO
   nudge generates today.
3. **Coalesce overlapping re-asserts.** `_stabilityResendCts` is replaced on
   every new LO change. A 3-second scroll-tune burst with ten LO changes
   produces exactly one stability resend, fired 1.2s after the *last* LO
   change.

Disposal: `_cts.Cancel()` cascades to the linked stability-resend CTS via
`CreateLinkedTokenSource`, so tracker shutdown kills any pending resend
cleanly. No extra wiring needed beyond disposing the latest CTS in
`DisposeAsync`.

### Tests to add

New file: `tests/SmartSDRIQStreamer.CWSkimmer.Tests/CwSkimmerSyncTrackerTests.cs`

Internal `FakeTelnetClient` implementing `ICwSkimmerTelnetClient` that records
every `SendLoFreqAsync` and `SendQsyAsync` call. Optional
`Func<long,Exception?> LoFreqException` / `Func<double,Exception?>
QsyException` for failure injection.

| Test | Asserts |
|------|---------|
| `RequestSync_LoChangeAfterPriorVfoSent_ReassertsVfoEvenIfUnchanged` | After initial sync (LO+VFO), an LO-only change re-emits the same VFO value (Layer 1) |
| `RequestSync_LoChange_FiresStabilityReassertAround1200ms` | After 1.5s wait, a second QSY at the same frequency was sent (Layer 2) |
| `RequestSync_RapidLoChanges_OneStabilityReassertAtEnd` | 5 LO changes in ~400ms → 1+5+1 = 7 total QSY calls (1 initial, 5 immediate re-asserts, 1 coalesced stability resend) |
| `DisposeAsync_DuringPendingResend_CancelsCleanly` | Dispose before the +1.2s window → no further telnet TX after the wait |
| `RequestSync_RepeatedSameVfoNoLo_DoesNotResend` | Idempotence still holds when LO doesn't change (no regression of the original tracker win) |
| `LoChange_TelnetThrows_ResendDoesNotPropagate` | `SendQsyAsync` throws → status callback receives "QSY sync failed", no unhandled exception escapes the resend task |

These tests use real `Task.Delay` and run on wall-clock time. Each takes
~1–2s. If timing flakiness becomes a problem, inject a clock — but that's a
future cleanup, not a Part A blocker.

### Doc updates that ride with Part A

- `README.md:71–72` — keep the wording, since post-fix it's accurate again
  (or update the language to be specific: *"After a panadapter LO change,
  the streamer re-asserts the VFO/QSY 1.2 s later as a safety net against
  CW Skimmer dropping the command during IQ pipeline restart."*).
- `CwSkimmerSyncTracker.cs:9–13` — remove the *"stability resends"* item
  from the "replaces" list, or amend to say *"replaces the prior mix of
  debounce queues and per-event resends with a single coalescing loop, plus
  a coalesced one-shot stability resend after LO changes."*

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

Realistic landing: VM drops to ~1500 lines, which is still big but is
genuinely *"owns the bindable UI state and command surface."*

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

1. **Part A (Layer 1+2)** — small, fixes a real operator-visible bug, ~30
   lines of code plus tests.
2. **Phase 1 (doc drift)** — fast, low risk, immediately makes the README
   match what the code actually does.
3. **Phase 2/3/4 in any order** — pick whichever extraction feels most
   painful next time the VM is touched.

## Open questions

- Is 1200 ms the right stability resend delay, or should it be tunable per
  installation? Default-and-forget is probably fine; revisit only if field
  reports indicate CW Skimmer takes longer than that to settle on some PCs.
- Should the stability resend status line surface only when it actually
  results in a TX, vs every time the timer fires? Current proposal emits
  on every fire (clearer breadcrumb); could move to "emit-on-TX" later if
  the log feels noisy.
