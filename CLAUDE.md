# CLAUDE.md

## Project Overview

SmartStreamer4 is a Windows desktop application that streams SmartSDR IQ
audio to CW Skimmer. It connects to a FlexRadio over the local network via
FlexLib, configures Skimmer's audio routing and INI, and reconciles
Skimmer's spotted CW signals back to the radio's slice frequency. Built
with Avalonia / .NET 8, distributed as a single signed release zip
attached to GitHub Releases.

## Architecture

- **.NET 8 / `net8.0-windows`**, `WinExe` output, nullable reference types
  enabled, Avalonia compiled bindings on by default.
- **UI**: Avalonia 11.3.12 + `CommunityToolkit.Mvvm` 8.4.1, MVVM pattern.
  Root project (`SmartSDRIQStreamer.csproj`) hosts the App, MainWindow,
  SetupWizard, ResetSkimmerWizard, ViewModels, and workflow services.
- **FlexRadio module**: `src/SmartSDRIQStreamer.FlexRadio/` wraps FlexLib
  4.2.18 (discovery, connection, slice/spot model). Project-references
  `..\..\FlexLib_API_v4.2.18.41174\FlexLib\FlexLib.csproj`.
- **CW Skimmer module**: `src/SmartSDRIQStreamer.CWSkimmer/` owns INI
  generation, telnet client, launcher, sync tracker, frequency math, and
  WDM audio device discovery.
- **Tests**: `tests/SmartSDRIQStreamer.CWSkimmer.Tests/` (xUnit-style
  tests in the CWSkimmer module).
- **FlexLib source folders** (`FlexLib_API_v4.1.5.39794/`,
  `FlexLib_API_v4.2.18.41174/`) are gitignored and live outside the
  compiled tree. Only 4.2.18 is project-referenced; 4.1.5 is kept on
  disk for historical reference only.
- **Release**: `publish-release.ps1` builds, signs, and zips
  `SmartStreamer4-v0.1.X-b.zip`. Notes pulled from
  `RELEASE_NOTES-v0.1.Xb.md` (gitignored).

## Modernization Philosophy

Always prefer the latest stable version of every tool, API, and language
feature supported by the project's minimum versions (.NET 8, C# 12,
Avalonia 11.3). When multiple approaches exist, choose the most modern.
Do not use deprecated or legacy patterns when a modern replacement exists.

## C# 12 / .NET 8 Preferred APIs

Use modern replacements: collection expressions (`[a, b, c]` not
`new[] {a, b, c}` or `new List<T> { ... }`), primary constructors on
classes and structs (not just records), `required` members instead of
constructor-enforced non-null, raw string literals (`"""..."""`) for any
multi-line string, `file`-scoped types for single-file helpers,
`System.Text.Json` (not Newtonsoft) for serialization, `TimeProvider`
abstraction for clocks in testable code (not `DateTime.UtcNow` directly),
`ArgumentNullException.ThrowIfNull(x)` (not manual null checks),
`ArgumentException.ThrowIfNullOrEmpty`, target-typed `new()` where the
type is obvious, pattern matching with property patterns over chained
`if`/`else`. Nullable reference types are enabled repo-wide.

## Code Quality

**Build gate**: **After every `.cs` change, immediately run
`dotnet build` and fix any errors or warnings before proceeding.** This
is a blocking gate. Zero errors, zero warnings allowed. If a warning
seems unavoidable, raise it before suppressing.

**Test gate**: **After any change in
`src/SmartSDRIQStreamer.CWSkimmer/`, `src/SmartSDRIQStreamer.FlexRadio/`,
or `tests/`, immediately run `dotnet test` and fix any failures before
proceeding.** Blocking. Zero failures allowed.

**Live-radio smoke test gate**: **Before declaring done on any change
that touches FlexLib calls, CW Skimmer sync logic, audio device
selection, or the workflow service, run the app against a real FlexRadio
and confirm the affected behavior.** Blocking. Unit tests are not
sufficient for this project — sync tracking and audio routing have
regressed in CI-clean code more than once.

**Markdown gate**: **After every `.md` change, run markdownlint and fix
any errors before proceeding.** Blocking. Zero warnings allowed.

```bash
npx markdownlint-cli2 "**/*.md" "!**/node_modules/**" "!.claude/**" "!RELEASE_NOTES-*.md" "!artifacts/**"
```

**Linting fixes**: Always fix the root cause rather than suppressing or
disabling the rule. Never add `#pragma warning disable`,
`SuppressMessage`, or nullable-suppression `!` operators without explicit
user approval. The `!` suppression in particular hides real null bugs;
prefer reshaping the code or adding a real null check.

**String building with separators**: Prefer `string.Join` over repeated
`+=` concatenation. Collect parts into a list, then join. Examples:
`string.Join("|", a, b, c)`, `string.Join(Environment.NewLine, lines)`,
`string.Join(", ", items.Select(x => x.Name))`.

**Multi-line templates and long delimited lists**: Any string literal
that contains two or more embedded newlines — whether written as a raw
string literal (`"""..."""`) or as a normal quoted string with `\n` /
`\r\n` escapes — should be built with a raw string literal where the
content is mostly static, or with `string.Join(Environment.NewLine,
[...])` where the content is mostly interpolated. Same rule for any
single-string literal longer than ~200 chars. Put interpolated values
inline on the line where they appear so the reader sees the value where
it is used, not cross-referenced from an argument list. Applies to
PowerShell scripts, multi-paragraph dialog messages, INI section bodies,
status-line output that spans multiple lines, and test-assertion diffs.
Single-line `string.Format` / interpolation (`$"frequency: {f:F3} kHz"`)
is still the right call where the format verb is doing real work.

**Numeric separators**: Use underscores in numeric literals with 4+
digits for readability: `120_000`, `48_000`, `0x_FFFF_FFFF`.

**Named constants over bare strings and magic numbers**: Any string or
number that (a) appears in two or more places, (b) is a tag /
discriminator / identifier (e.g. a transport name, audio device role,
slice mode, status key, INI section name, FlexLib command verb), or (c)
is a tunable threshold / limit / timeout, must be a named constant — not
a bare literal at the call site. Prefer typed string-derived structs or
enums (`enum class SliceMode { CW, USB, ... }`) over untyped consts when
the value is a discriminator dispatched on (the compiler then refuses
typo'd literals). Group related constants in a single `static class` or
alongside the type that uses them. Exemptions: one-off literals that are
obviously self-describing in context (`":"`, `"\n"`, `0`, `1`, `-1`,
format strings like `"F3"`, test fixture values), and framework-defined
sentinels (`StringComparison.Ordinal`, `CultureInfo.InvariantCulture`).

**No em dashes**: Never use em dashes (—) in user-facing prose output
(MessageBox text, status bar messages, dialog labels, release notes,
in-app help). Use periods, commas, or parentheses instead. CLAUDE.md
itself and code comments are exempt — the rule targets text that ships
to operators.

## Codex collaboration

Codex is a second LLM (different model family, different training, its
own file-reading tools) reachable via the `mcp__codex__codex` MCP tool.
It runs in this same repo, so pass **absolute file paths and line
ranges** rather than pasting large blobs — Codex will read the files
itself. Each `mcp__codex__codex` call starts a fresh session with no
memory of ours; use `mcp__codex__codex-reply` to continue an in-flight
thread (cheaper than re-priming context for follow-ups on the same
question).

### When to call Codex

1. **Adversarial review** — After any non-trivial change (new file,
   threading edit, FlexLib API change, CW Skimmer sync logic change,
   release-pipeline rework), ask Codex to review the diff for bugs, edge
   cases, race conditions, FlexLib API misuse, audio routing
   assumptions, and missed CLAUDE.md gates before declaring done.
2. **Stuck-loop breaker** — If a fix has failed twice and tests or
   live-radio behavior still aren't right, hand Codex the failing
   output, the error, and the relevant files and weigh its proposal
   before attempt three. Don't churn a third low-confidence fix without
   an outside read.
3. **Alternative implementation** — Before committing to a non-obvious
   design (new abstraction, new project, refactor that touches >5
   files), ask Codex to sketch an alternative. Compare trade-offs
   explicitly in the reply.
4. **Parallel work** — For independent modules with a clean interface
   (e.g. an INI writer vs. a telnet client), implement one and delegate
   the other. Pass the interface, types, and tests so the seams line up.
5. **Spec / protocol questions** — For FlexLib API semantics, SmartSDR
   command framing, CW Skimmer telnet quirks, INI format edge cases.
   Codex's training cutoff and emphasis differ; useful second read.

### When NOT to call Codex

- Trivial mechanical edits, formatter / linter fixes, renames, doc
  tweaks.
- Anything answered by an existing memory file or by reading the code
  directly — read those first.
- As a stall when the user is waiting on a decision Claude should just
  make.
- Questions where Claude hasn't yet read the relevant code; Codex is a
  reviewer, not a substitute for primary grounding.

### How to call Codex

Required in every prompt:

- **Goal** in one sentence and the **deliverable shape** (review report?
  unified diff? method body? yes/no with reasoning?).
- **Absolute file paths + line ranges** for everything Codex needs to
  read. Prefer `path:start-end` over pasting; paste only when the
  content is ephemeral (test output, a diff not yet committed).
- **Constraints**: .NET 8 / `net8.0-windows`, Avalonia 11.3.12, nullable
  enabled, `dotnet build` warning-free, `dotnet test` clean, no new
  NuGet dependencies without reason, the relevant blocking gates from
  this file, live-radio verification before declaring done.
- **Acceptance criteria** — what "correct" looks like, including which
  test or live-radio behavior must still pass.
- **What's already been tried and ruled out**, so Codex doesn't propose
  approaches that have been eliminated.

### After Codex responds

- Read the full response. Never paste Codex's diff into a file blind —
  re-derive the edit through Edit/Write so the change has actually been
  verified.
- Run the relevant blocking gates (`dotnet build`, `dotnet test`,
  markdownlint, live-radio smoke) on anything applied.
- If Codex disagrees with Claude's read: state the disagreement to the
  user explicitly, give both arguments, and pick the one Claude can
  defend — do not default to Codex just because it's the second
  opinion, and do not default to Claude's first answer just because it
  was first. If Claude can't adjudicate, surface the choice to the user
  rather than guess.
- If Codex's answer is wrong or missed context, note what context was
  missing so the next call includes it.

## Bug Fix Comments

When fixing a user-reported bug, add an inline comment at the fix site
documenting: (1) the symptom users saw, (2) the root cause, (3) why this
specific fix was chosen over alternatives, and (4) when it was reported.
The goal is to prevent regressions (e.g. a future session "improving" a
deliberately-chosen `Math.Floor` back to `Math.Ceiling` without
understanding why) and to support users who re-encounter the issue.
Keep it to 2–4 lines. If the fix spans multiple files, put the full
comment at the primary site and a cross-reference ("see X for
rationale") at secondary sites.

## Debugging

- **Scope**: When diagnosing UI or sync bugs, read the user's
  description carefully: if they say a "spot" is broken, check the
  entire spot lifecycle (telnet parse, sync tracker, slice tune, status
  emit), not just one stage. Always verify the full scope of the
  reported issue before proposing a fix.
- **Iteration limit**: When proposing fixes for elusive bugs, limit to
  2 iterations before pausing to reassess the approach with the user.
  Do not churn through multiple low-confidence fixes in rapid
  succession. After 2 failed iterations, prefer asking Codex for a
  second read (see Codex collaboration above) over a third blind
  attempt.

## Git

Batch all related edits into a single commit, then push. Don't commit
per file. Create commits when explicitly asked by the user — Claude does
not commit autonomously, but does maintain control of the commit
operation once asked (writing the message, staging the right files,
pushing).

- Never use `git add -A` / `git add .`; stage by name to avoid
  sweeping in `.env`-style files or scratch artifacts.
- Never use `--no-verify`, `--no-gpg-sign`, or any hook-skipping flag
  unless the user explicitly asks. If a hook fails, fix the underlying
  issue.
- Never amend an existing commit unless the user asks for an amend; if
  a hook blocks a commit, fix and create a new commit, don't amend.
- Never force-push to `main`.

## Build & Release

```powershell
dotnet build                                          # debug build
dotnet build -c Release                               # release build
dotnet test                                           # run all tests
.\publish-release.ps1                                 # build + sign + zip
```

Release publishing flow:

1. Update `<Version>` in `SmartSDRIQStreamer.csproj` (e.g. `0.1.18-b`).
2. Run `.\publish-release.ps1` — produces `SmartStreamer4-v0.1.18b.zip`.
   The csproj keeps the dash in `<Version>`; the script strips it for
   the release zip name.
3. Write release notes to `RELEASE_NOTES-v0.1.18b.md`. **This file is
   gitignored** — pipe it into `gh release create --notes-file`, do
   not try to commit it.
4. Verify the local build matches what will be tagged. `gh release
   create` creates the tag on the remote at the default branch's HEAD,
   not at your local HEAD. If those diverge, the zip in the release
   (built from your local working tree) won't match the commit the tag
   points at. Before publishing:

   ```powershell
   git fetch origin main
   git rev-parse HEAD
   git rev-parse origin/main
   ```

   The two SHAs must match. If they don't, push or rebase first, or
   pass `--target <sha>` to `gh release create` to pin the tag
   explicitly.

5. Publish:

   ```powershell
   gh release create v0.1.18b --latest `
     --notes-file RELEASE_NOTES-v0.1.18b.md `
     SmartStreamer4-v0.1.18b.zip
   ```

   Always `--latest`. Never `--prerelease` (the `b` suffix has caused
   wrong-flag mistakes before). The tag `v0.1.18b` is created on the
   remote as a side effect; run `git fetch --tags` if you want it
   locally afterwards.

6. Attach the zip, not a raw `.exe` — browsers block `.exe` downloads
   from GitHub Releases.

## Quick Start After /clear

Where to look first for common tasks:

- **CW Skimmer sync issue**:
  [CwSkimmerWorkflowService.cs](CwSkimmerWorkflowService.cs)
  (orchestration) →
  [src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerSyncTracker.cs](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerSyncTracker.cs) →
  [CwSkimmerTelnetClient.cs](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerTelnetClient.cs) /
  [CwSkimmerLauncher.cs](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerLauncher.cs) /
  [CwSkimmerIniWriter.cs](src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerIniWriter.cs).
  Tests:
  [tests/SmartSDRIQStreamer.CWSkimmer.Tests/](tests/SmartSDRIQStreamer.CWSkimmer.Tests/).
- **FlexLib radio bug**:
  [src/SmartSDRIQStreamer.FlexRadio/FlexLibRadioConnection.cs](src/SmartSDRIQStreamer.FlexRadio/FlexLibRadioConnection.cs) +
  [FlexLibRadioDiscovery.cs](src/SmartSDRIQStreamer.FlexRadio/FlexLibRadioDiscovery.cs).
  FlexLib 4.2.18 client must keep working against both SmartSDR 4.1.5
  and 4.2.x server radios in the field.
- **Audio device discovery (WDM/MME)**:
  [src/SmartSDRIQStreamer.CWSkimmer/WdmAudioDeviceFinder.cs](src/SmartSDRIQStreamer.CWSkimmer/WdmAudioDeviceFinder.cs).
- **Settings persistence**:
  [AppSettings.cs](AppSettings.cs) /
  [AppSettingsStore.cs](AppSettingsStore.cs) /
  [AppSettingsSession.cs](AppSettingsSession.cs).
- **MVVM root**:
  [MainWindow.axaml](MainWindow.axaml) /
  [MainWindow.axaml.cs](MainWindow.axaml.cs) +
  [MainWindowViewModel.cs](MainWindowViewModel.cs) +
  [SliceViewModel.cs](SliceViewModel.cs).
- **Setup wizard**:
  [SetupWizardWindow.axaml](SetupWizardWindow.axaml) /
  [SetupWizardWindow.axaml.cs](SetupWizardWindow.axaml.cs) +
  [SETUP_GUIDE_WIZARD.md](SETUP_GUIDE_WIZARD.md) (embedded resource).
- **Reset Skimmer wizard**:
  [ResetSkimmerWizardWindow.axaml.cs](ResetSkimmerWizardWindow.axaml.cs).
- **Update checks**:
  [ReleaseUpdateService.cs](ReleaseUpdateService.cs).
- **Status line throttling / footer**:
  [ThrottledStatusEmitter.cs](ThrottledStatusEmitter.cs) +
  [FooterStatusBuffer.cs](FooterStatusBuffer.cs).
- **Release pipeline**:
  [publish-release.ps1](publish-release.ps1).

## Conventions

- Spell out **ViewModel**, not VM, in code review and discussion.
  "VM" gets misread as Virtual Machine.
- Avalonia compiled bindings are on by default
  (`AvaloniaUseCompiledBindingsByDefault=true`). Don't fall back to
  reflection-based `Path=` bindings.
- Nullable reference types are enabled. No `!` suppressions without
  justification — fix the type, don't paper over it.
- FlexLib runtime compatibility: code targeting FlexLib 4.2.18 must
  remain compatible at runtime with SmartSDR servers 4.1.5 and 4.2.x.
  Verify on both before declaring a FlexLib-touching change done.
- Release versioning: csproj uses `0.1.X-b`; release tag and zip use
  `v0.1.Xb` (no dash).

## References

- [CONTRIBUTING.md](CONTRIBUTING.md) — branch-per-change PR workflow.
- [Flexlib4-2-Migration-Guide.md](Flexlib4-2-Migration-Guide.md) —
  the 4.1.5 → 4.2.18 migration record.
- [PLAN-skimmer-resync-and-refactor.md](PLAN-skimmer-resync-and-refactor.md) —
  current CW Skimmer sync redesign plan.
- [README.md](README.md) — user-facing project description, install,
  usage.
