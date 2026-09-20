# CLAUDE.md

Operating manual for AI coding agents in this repository. Claude Code
loads this file directly; Codex reads the same content through the
`AGENTS.md` symlink. The rules here are binding and mechanical: where
this file states a rule, apply it as written instead of relying on
judgment or model defaults.

## Project Overview

SmartStreamer4 is a Windows desktop application that streams SmartSDR IQ
audio to CW Skimmer. It connects to a FlexRadio over the local network via
FlexLib, configures Skimmer's audio routing and INI, and reconciles
Skimmer's spotted CW signals back to the radio's slice frequency. Built
with Avalonia / .NET 8, distributed as a single signed release zip
attached to GitHub Releases.

## Task Lifecycle

Follow this sequence for every code change. Do not skip or reorder steps.

1. **Propose.** No code gets written or edited until the proposed change
   has been explained and the user has given input on it. A proposal
   covers: what will change and why, the shape of the implementation
   (files touched, new surfaces added), and the alternatives considered,
   including the do-nothing or subtraction option. Exempt: fixes the
   blocking gates demand on a change the user already approved, and
   edits the user has already fully specified.
2. **Locate.** Use the Quick Start table (bottom of this file) to find
   the right files. Never guess a path, symbol name, or API; confirm
   with search (Grep or an Explore subagent) before editing.
3. **Read first.** Read the code you are about to change, and its
   callers when behavior changes.
4. **Edit** following Design Philosophy, Code Quality, and Conventions
   below.
5. **Gate.** Immediately after each edit, run every matching blocking
   gate the current seat can run (see Dev Environment). A failing gate
   blocks all further work until it passes. On the Linux seat the
   dotnet and live-radio gates cannot run; they are deferred to
   Windows, and every deferred gate must be named in the report.
6. **Test decision.** For every new element, explicitly choose add /
   extend / skip-with-stated-reason (see Test Coverage Discipline).
   Silent skips are not acceptable.
7. **Verify.** Demonstrate the change does what was asked: run the
   relevant test or exercise the flow against the real app. Never claim
   a fix works without evidence. A change edited on the Linux seat is
   not verified until the Windows-side gates and any required
   live-radio smoke have passed.
8. **Report.** State what changed, which gates ran and their results,
   which gates were deferred to the Windows seat, and anything skipped
   with the reason. If a test fails, say so and show the output; never
   paper over it.

### Definition of Done

A change is complete only when every applicable item holds. Check the
list before reporting completion.

- [ ] Every blocking gate matching the touched file types passes with
  zero errors/warnings on a seat that can run it; gates the current
  seat cannot run are explicitly named as deferred to Windows.
- [ ] Live-radio smoke passed for changes touching FlexLib calls, CW
  Skimmer sync logic, audio device selection, or the workflow service,
  against a SmartSDR 4.2.x server.
- [ ] A test add/extend/skip decision is recorded for every new element
  (skip requires a one-line reason in the report or an inline comment).
- [ ] Dead code left by a pivot or replaced approach is deleted.
- [ ] A fix for a user-reported bug carries a 2-4 line comment at the
  fix site: symptom, root cause, why this fix over alternatives, report
  date (see Bug Fix Comments).
- [ ] No git commits were created (see Git).
- [ ] No em dashes introduced in user-facing prose output (see
  Conventions for scope).
- [ ] For non-trivial changes: a Codex deep audit ran and its findings
  were adjudicated (see Codex collaboration).
- [ ] The final report names the gates run and their actual results.
  "Done" without gate evidence is not done.

## Architecture

- **.NET 8 / `net8.0-windows`**, `WinExe` output, nullable reference types
  enabled, Avalonia compiled bindings on by default.
- **UI**: Avalonia 11.3.12 + `CommunityToolkit.Mvvm` 8.4.1, MVVM pattern.
  Root project (`SmartSDRIQStreamer.csproj`) hosts the App, MainWindow,
  SetupWizard, ResetSkimmerWizard, ViewModels, and workflow services.
- **FlexRadio module**: `src/SmartSDRIQStreamer.FlexRadio/` wraps FlexLib
  4.2.20 (discovery, connection, slice/spot model). Project-references
  `..\..\FlexLib_API_v4.2.20.41343\FlexLib\FlexLib.csproj`.
- **CW Skimmer module**: `src/SmartSDRIQStreamer.CWSkimmer/` owns INI
  generation, telnet client, launcher, sync tracker, frequency math, and
  WDM audio device discovery.
- **Tests**: `tests/SmartSDRIQStreamer.CWSkimmer.Tests/` (xUnit-style
  tests in the CWSkimmer module).
- **FlexLib source folders** (`FlexLib_API_v4.1.5.39794/`,
  `FlexLib_API_v4.2.18.41174/`, `FlexLib_API_v4.2.20.41343/`) are
  gitignored and live outside the compiled tree. Only 4.2.20 is
  project-referenced (issue #61); 4.1.5 and 4.2.18 are kept on disk for
  historical reference only.
- **Release**: `publish-release.ps1` takes exactly one of two mode
  flags. `-Preview` requires a `-previewN` tag: it builds, verifies the
  embedded version, zips `SmartStreamer4-<tag>-win-x64.zip`, uploads the
  zip to the R2 bucket for testers, and confirms the link serves it.
  `-Publish` requires a clean tag: same build, then
  `SHA256SUMS.txt` and `gh release create --latest` attaching the zip
  and the sidecar. Nothing is committed either way, so the release
  commit equals the tag commit. Notes pulled from
  `RELEASE_NOTES-<tag>.md` (gitignored).

## Dev Environment

Two clones of this repo, one on each machine. VS Code + Claude Code run
identically on both.

- **Windows 11 (primary)**: build, test, run, live radio. Every blocking
  gate (build, test, live-radio smoke, release script) executes here.
- **Linux (secondary)**: docs, scripts, planning, and audit work. No
  `dotnet` SDK and no `pwsh`, so build/test gates cannot run; any C# or
  NuGet errors a Linux IDE shows are environmental noise, not real bugs.
  After C# edits made on Linux, flag that Windows-side verification is
  still required before merge.
- **Git is the only channel the two seats share.** Claude Code
  auto-memory is machine-local and never syncs. Durable cross-seat
  knowledge belongs in this file (or TODO.md / PLAN.md for work state),
  not in memory. Start every session with `git pull` and check
  `git status` for "behind"; a stale clone invalidates file:line
  references. Push at session end, even for doc-only changes.

## Design Philosophy

### Simplicity through Subtraction (load-bearing)

> "Perfection is achieved, not when there is nothing more to add, but
> when there is nothing more to take away." (Antoine de Saint-Exupery)

The default answer to "should we add this?" is **no, until proven
otherwise**. A feature, option, settings field, wizard step, status
line, config key, code path, or abstraction earns its place only when
its value clearly exceeds the permanent cost it imposes: more surface
to learn, document, test, live-verify against a real radio, and
maintain forever. Adding is cheap to do and expensive to live with;
subtraction compounds the other way.

1. **Lead with the subtraction alternative.** Before describing what to
   add, check whether an existing surface already meets the goal, or
   whether removing or simplifying something meets it. If a capability
   already covers the need, say so and recommend against the addition.
2. **Make the value-vs-cost trade explicit.** State the concrete use
   case, who hits it, and how often. If the use case is thin, say "the
   trade-off is minor, I recommend we don't" rather than build it
   because it was asked about. Pushing back here is doing the job.
3. **Prefer the smallest thing that works.** One settings field beats
   two; no new option beats a new option with a sensible default;
   reusing an existing component beats a parallel one. Deleting dead
   code after a pivot is mandatory.
4. **Guard against accretion.** Each addition makes the next look
   small. Name that pressure when you see it.

This does not override an explicit, justified user request. Finished is
when there is nothing left to remove, not nothing left to add.

### Modernization

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

### Absent values: nullable types over sentinels

When a value can legitimately be **absent** (unknown, not yet measured,
not provided), use a nullable type (`int?`, `double?`, `string?`) so
absent is structurally distinct from a real zero or empty string. Do
not overload `0`, `""`, or `-1` as "absent" when those could be real
readings (0 dB is a real level; audio device index 0 is a real device;
an empty callsign could mean "not entered" vs "not yet fetched").
Unwrap with pattern checks (`is { } value`) at the boundary, never with
`!` (see Linting fixes).

- **Apply when all three hold**: zero/empty is a plausible real value;
  downstream consumers must distinguish "unknown" from "zero"; the
  value crosses a boundary where the distinction matters (settings
  JSON, INI output, status display).
- **Exempt**: counters where zero is the natural "none yet"; dictionary
  `TryGetValue` presence (already the idiomatic absent); framework
  zero-value conventions (`TimeSpan.Zero` where conventional).
- **JSON wire form** (`System.Text.Json`): nullable property +
  `JsonIgnoreCondition.WhenWritingNull`. `null` omits the field; a real
  zero emits. Never invent string sentinels (`"unknown"`, `"N/A"`).
- Precedent: `OperatorWdmSignalDevIndex` is `int?` in the CW Skimmer
  config; a null device index must never silently become device 0.

## Code Quality

**Build gate**: **After every `.cs` change, immediately run
`dotnet build` and fix any errors or warnings before proceeding.** This
is a blocking gate. Zero errors and zero first-party warnings allowed.

Third-party transitive warnings are exempt: the
`FlexLib_API_v4.2.20.41343/` csprojs (UiWpfFramework, Util, Vita,
FlexLib) emit ~40 MSB3245 / MSB3243 / MSB3277 warnings about WPF
assembly resolution on every build, including `main`. They are out of
our control. When counting warnings against the gate, exclude any
whose source path contains `FlexLib_API_v4.2.20.41343/`.

If a new warning seems unavoidable in first-party code, raise it before
suppressing.

One first-party suppression exists, operator-approved 2026-09-20:
`AVLN3001` in `SmartSDRIQStreamer.csproj`. `SmartDeckWindow` deliberately
has no parameterless constructor (see the note above its constructor),
and nothing loads it by `avares` URI, so the warning describes a
capability the app does not use. It is scoped to that one code; a second
window tripping it is a new decision, not covered by this one.

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
npx markdownlint-cli2 "**/*.md" "!**/node_modules/**" "!.claude/**" "!.trunk/**" "!RELEASE_NOTES-*.md" "!artifacts/**"
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

## Test Coverage Discipline

Any new element must be deliberately routed to one of: (1) a new test,
(2) an extension of an existing test, or (3) an explicit skip with a
one-line reason (trivial property, UI-only wiring covered by the
live-radio smoke, behavior already covered by an existing test the new
code participates in, or intentionally deferred to a named follow-up).
Default lean is (1) or (2); silent skips are not acceptable. "Element"
means: a C# class, method, or property with logic; an INI field or
section; a telnet parsing branch; a settings field; a frequency or sync
math change; a CLI flag or config surface.

| New element | Test lands in |
|-------------|---------------|
| Class or method in `src/SmartSDRIQStreamer.CWSkimmer/` | `tests/SmartSDRIQStreamer.CWSkimmer.Tests/` |
| Class or method in `src/SmartSDRIQStreamer.Digital/` | `tests/SmartSDRIQStreamer.Digital.Tests/` |
| Root-project service or helper (non-UI) | `tests/SmartSDRIQStreamer.App.Tests/` |
| INI field or section | `CwSkimmerIniWriter` / INI model factory tests |
| Settings field in `AppSettings` | load/save round-trip test |
| Frequency or sync math | sync tracker / frequency math tests |
| FlexLib-facing behavior, audio routing, ViewModel/UI wiring | usually not unit-testable; explicit skip with reason, covered by the live-radio smoke gate |

## Codex collaboration

Codex is a second LLM (different model family, different training, its
own file-reading tools) running in this same repo; it reads this file
through the `AGENTS.md` symlink. Pass **absolute file paths**, never
pasted blobs — Codex reads the files itself. Use it at **three
stages**, not just as a diff reviewer:

1. **Design**: before building a non-trivial change, hand Codex the
   plan plus the relevant whole files; ask it to attack the approach
   (failure modes, simpler alternatives, what breaks at the edges).
   Before writing code, not after.
2. **Implementation**: at a fork in the road, or after two failed fix
   attempts, hand it the failing output, the error, and the files.
   Don't churn a third low-confidence fix without an outside read.
3. **Final review**: before declaring done on any non-trivial change
   (new file, threading edit, FlexLib API change, CW Skimmer sync
   change, release-pipeline rework), run a **deep audit** over the
   whole change (whole files, severity-ranked findings), not just the
   diff.

Also useful for: **parallel work** on independent modules with a clean
interface (implement one, delegate the other; pass the interface,
types, and tests so the seams line up), and **spec / protocol
questions** (FlexLib API semantics, SmartSDR command framing, CW
Skimmer telnet quirks, INI format edge cases).

### Channels

Prefer the **CLI via Bash** (inherits `~/.codex/config.toml`, sandboxes
properly). Run from the repo root; trust is keyed to the project path,
so approve the repo once if prompted.

- **Always redirect stdin**: end every `codex exec` invocation with
  `< /dev/null`. When stdin is a non-TTY pipe held open (as under the
  Bash tool), `codex exec` prints "Reading additional input from
  stdin..." and blocks forever before doing any work (diagnosed
  2026-07-17 in TheMill: a two-hour hang with zero CPU). A hung run
  shows zero CPU time and no output; kill it and relaunch with stdin
  closed. Health check when a run seems stalled:
  `timeout 60 codex exec --sandbox read-only "Reply with exactly: HEALTHCHECK OK" < /dev/null`
- Read-only review: `codex exec --sandbox read-only "<prompt>" < /dev/null`
- Review that builds and runs tests:
  `codex exec --sandbox workspace-write "<prompt>" < /dev/null`. Only
  meaningful on the **Windows seat**; the Linux seat has no dotnet
  SDK, so Codex there is limited to read-only code review and must not
  claim build or test evidence.
- Follow-up in the same session (cheaper than re-priming):
  `codex exec resume --last "<prompt>" < /dev/null` or
  `codex exec resume <session-id>`
- Deep audits can run for minutes; use a generous Bash timeout or
  `run_in_background`.

The `mcp__codex__codex` MCP tool is a **fallback** only (some sandbox
modes cannot spawn processes and silently degrade Codex to text-only).
If it must be used: `sandbox: "danger-full-access"` plus
`approval-policy: "never"`, and state "read-only task, do not modify
files" in the prompt; `mcp__codex__codex-reply` continues a thread.

**No degraded reviews**: if Codex reports that commands failed or that
it could not read the files, the review is void. Switch channels and
rerun. Never accept findings from a Codex that has not actually read
(and, for a Windows-seat deep audit, built and run) the code.

### When NOT to call Codex

- Trivial mechanical edits, formatter / linter fixes, renames, doc
  tweaks.
- Anything answered by an existing memory file or by reading the code
  directly — read those first.
- As a stall when the user is waiting on a decision Claude should just
  make.
- Questions where Claude hasn't yet read the relevant code; Codex is a
  reviewer, not a substitute for primary grounding.

### Two prompt shapes, pick by scope

The focused shape gives shallow production-readiness passes; the
deep-audit shape wastes minutes on a one-line question.

- **Focused** (a diff, a function, a specific yes/no). Include: a
  one-sentence goal plus the deliverable shape (report, unified diff,
  method body, yes/no with reasoning); absolute paths with line ranges
  (`path:start-end`; paste only ephemeral content like test output);
  the project constraints (.NET 8 / `net8.0-windows`, Avalonia 11.3.12,
  nullable enabled, no new NuGet dependencies without reason) and the
  blocking gates from this file; acceptance criteria including which
  test or live-radio behavior must still pass; and what's already been
  tried and ruled out.
- **Deep audit** (design review, whole-file, production readiness; the
  default for non-trivial work). Depth comes from breadth of mandate,
  not prompt precision: give whole files, not line ranges; on the
  Windows seat explicitly authorize action ("build it, run the tests,
  investigate anything suspicious"; the workspace-write invocation);
  ask for severity-ranked findings, each with `file:line` and a
  concrete failure scenario; tell it to report findings outside the
  named scope too (Codex withholds adjacent findings as "out of scope"
  unless asked). Do not pre-narrow with a hypothesis list or tight
  acceptance criteria; stating project constraints is fine.

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

## Token Efficiency

- **Mechanical edits use local tools, never model-generated Edit
  calls**: `npx markdownlint-cli2 --fix` for markdown autofix;
  `git grep -l <pat> | xargs sed -i 's/old/new/g'` (or `perl -pi -e`)
  for bulk renames and repo-wide find-and-replace. If a mechanical
  transform recurs and has no script yet, write one under `tools/` and
  document it here so future sessions call the script.
- **Delegate simple edits to project subagents** (`.claude/agents/`):
  `editor-trivial` (Haiku) for typo fixes, comment/doc wording tweaks,
  single-line or single-file edits with zero design decisions, and
  applying an already-fully-specified diff; `editor-routine` (Sonnet)
  for well-specified multi-file edits that follow an existing pattern
  (adding a field everywhere, mirroring an existing method, mechanical
  refactors too fuzzy for sed). Reserve the primary model for
  architecture, debugging, sync logic, FlexLib integration, and
  reviewing subagent output. Both subagents run the gates their seat
  supports before returning; spot-check their diffs.
- **Context hygiene**: read only the relevant line ranges of large
  files (`MainWindowViewModel.cs` is 3,000+ lines); use Explore/search
  subagents for broad codebase questions instead of pulling whole files
  into main context; never re-read a file just edited; keep build
  output and test dumps out of the main context (pipe through
  `tail`/`grep`, or delegate to a subagent). Prefer ending the session
  (or `/clear`) over continuing unrelated work in a long context.

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
- **Data over prior triage notes**: When a diagnostic capture comes back
  from a tester (a `[FLEX]` devbuild log, a field trace), lead with what
  the log empirically shows before referencing any earlier triage
  write-up. Treat the hypotheses in prior notes as scaffolding that
  produced the diagnostic build, not as conclusions to confirm — the
  build exists to replace guesses with ground truth. State plainly what
  the data does and does not show, then use it to refute or support the
  old hypotheses; do not fit the data to them. Precedent: issue #30
  (Maestro empty Operating tab) had four triage hypotheses; the devbuild
  log refuted three in the first 100 ms and the real cause (a trailing
  space on the `Maestro` station name, desyncing runtime vs. discovery)
  was outside the hypothesis set entirely.

## Git

Never create commits or write commit messages. The user maintains full
control over all git write operations: staging, commits, pushes, tags,
PR creation, and merges. Leave changes in the working tree and report
them. Read-only git commands (`status`, `log`, `diff`, `show`) are fine
without asking; ask before any other mutating operation (`pull`,
`checkout`, `stash`).

## Build & Release

```powershell
dotnet build SmartStreamer4.sln                       # debug build (all projects incl. tests)
dotnet build SmartStreamer4.sln -c Release            # release build
dotnet test SmartStreamer4.sln                        # run all tests
.\publish-release.ps1 -Preview                        # tester build: build + zip + upload to R2
.\publish-release.ps1 -Publish                        # GA: build + zip + gh release create
```

`publish-release.ps1` is **pwsh 7 only** (`#Requires -Version 7.0`, added
2026-09-20). Under Windows PowerShell 5.1 a native command's stderr
becomes a terminating error when `ErrorActionPreference` is `Stop`, so an
untagged HEAD died inside `git describe` with a raw git error instead of
the script's own refusal. 5.1 is refused outright rather than supported.

The solution file must be named explicitly: the root also contains
`SmartSDRIQStreamer.csproj`, so bare `dotnet build` / `dotnet test` fail
with MSB1011 (ambiguous). Before 2026-07-23 the root solution was a
`.slnx`, which the .NET 8 CLI silently ignores; bare `dotnet test` fell
back to the app csproj and passed while running zero tests.

Release versioning conventions (GA as of v0.2.1; the `v0.1.Xb` beta
series is retired — issue #56):

- `vMAJOR.MINOR.PATCH` — general availability release (e.g. `v0.2.1`).
  Bump PATCH for a fixes-only release, MINOR when a feature lands.
- `vMAJOR.MINOR.PATCH-previewN` — numbered tester build (e.g.
  `v0.3.3-preview1`), adopted 2026-09-08 from the SKCCLogger convention.
  The base is pinned across a line: every preview leading to a release
  carries the same numeric version and only N advances (`-preview1`,
  `-preview2`, then the clean `v0.3.3` at GA). Annotated tag, phase 1
  only, zip handed to testers by hand. The suffix is embedded in the
  exe, so About and bug reports say which preview a tester is on, and
  the updater ranks every preview below the clean GA tag at the same
  version, so preview testers are prompted when GA ships.
- **Never publish a preview.** Phase 2 refuses a `-preview` tag. The
  updater in every build fielded before 2026-09-08 reads the releases
  list without skipping GitHub pre-releases, and numeric version wins
  before channel, so a pre-release `v0.3.2-preview1` on GitHub would
  prompt every operator on `v0.3.1`. Builds from this date on skip
  pre-releases, but that only becomes a safe channel once no earlier
  build is still in the field.
- **Do not use the `bN` suffix.** It retired with the beta series
  (operator decision, 2026-08-05) and the script no longer accepts it.
  Between that date and 2026-09-08 a tester build carried the plain GA
  tag; `v0.3.2` at `aabd5ed` is the one build minted that way. It was
  never published and never will be: the line moved on to `v0.3.3`
  (operator decision, 2026-09-20). The tag stays on origin as a record
  and is **not** retracted. Its testers need no manual nudge, because
  `v0.3.3` outranks `v0.3.2` numerically and their updater prompts when
  GA ships. The last published release is `v0.3.1`, so `v0.3.3` release
  notes diff from there, not from the `v0.3.2` tag.
- The tooling still ranks `a`/`alpha`, `b`/`bN` and `rc` suffixes when
  reading old tags; `preview` shares the rank `b` held. A suffix-free
  tag outranks any suffixed tag at the same numeric version.

**No release without operator-facing benefit**: No release ships unless
it carries at least one change an existing operator would actually
feel — a bug they hit gets fixed, a feature they asked for lands, or a
reliability/performance gain they would notice. Maintainer-hygiene work
alone (release-pipeline cleanups, internal refactors, doc/test renames,
cosmetic AppData-folder alignment with silent migration) does not justify
a release, even bundled together. Before recommending a release cut, list
each commit since the last published release and label it user-facing vs.
maintainer hygiene; "an operator might see a new log line on an edge
case" and "a silent migration ran on first launch" do not count. If
nothing on the slate clears the bar, recommend pushing the release back
and surface the gap rather than auto-shipping. Asking users to update
from a working install implies there is a reason to; shipping
hygiene-only releases trains operators to ignore update prompts.

Release publishing flow. Two modes, one per destination, selected by a
required flag. The script does not pause for human input: gates happen
between invocations, so a hung session can never strand a release.

**Two destinations, two flags.** A **tester build** (`-previewN` tag)
runs `-Preview`, which builds the zip and uploads it to R2 for testers.
A **published release** (clean tag) runs `-Publish`, which builds the
zip and creates the GitHub release. Both modes build; neither can reach
the other's destination, because `-Preview` refuses a clean tag and
`-Publish` refuses a preview tag. `v0.3.0b1` through `b5` and `v0.3.2`
were tester builds under the two earlier conventions: tagged, pushed
and zipped, never `gh release create`d.

**Why flags rather than inference** (adopted 2026-09-20 from
SKCCLogger's `build.py`). The script used to read the destination off
the tag shape, with bare invocation meaning "build". Inference cannot
catch the case that actually bites: intending a tester build, mistyping
a clean `vX.Y.Z` tag, and getting a GA-shaped zip with no complaint.
Stating the intent lets the script check the tag against it, and it
checks **before** the test run and build rather than after. SKCCLogger
hit the same class of bug from the other side (`build.py:2320`, pre-GA
review 2026-07-07): a full build plus three notarizations finished and
then both publish steps silently printed "Skipping ... not a clean
tag", shipping nothing.

### Preview — tester build (`.\publish-release.ps1 -Preview`)

Steps 1–4 are the operator's; Claude runs step 5.

1. Commit everything, including docs. The script tags nothing itself,
   and it **refuses a dirty working tree** (modified or untracked files;
   gitignored ones do not count). `dotnet publish` builds the working
   tree, not the tagged commit, so an uncommitted change would otherwise
   ship inside a build that claims the tag's version and sha. This file
   said the opposite until 2026-09-20 ("anything uncommitted is simply
   absent"); that was wrong, and the Codex design review caught it.
2. Tag the local HEAD, always as an **annotated** tag:
   `git tag -a v0.3.3-preview1 -m "SmartStreamer4 v0.3.3-preview1"`.
   Lightweight tags have caused busted releases before; annotated only.
   The csproj `<Version>` stays at the clean numeric default; release
   version comes from the tag.
3. `git push origin main` **before** pushing the tag, then
   `git push origin <tag>`. Otherwise origin carries a tag pointing at a
   commit unreachable from any branch. Every release tag goes to origin,
   including tester builds that will never be published.
4. Confirm no SmartStreamer4 instance is running. The build does a
   `dotnet publish`, and a live instance holds `bin\...\*.dll`, turning
   it into a wall of `MSB3021` / `MSB3027` lock errors that read like a
   code failure and are not one.
5. Run `.\publish-release.ps1 -Preview`. The script:
   - Refuses unless HEAD carries a `-previewN` tag. A clean tag is a GA
     tag, and the error says to run `-Publish` instead.
   - Does **not** check whether the zip's key already exists on R2
     (removed 2026-09-20, operator decision; do not restore it). That
     check asked the public URL before the upload, Cloudflare cached the
     404 it got, and the post-upload verify was then served the cached
     404: `v0.3.3-preview2` uploaded correctly and the script still
     exited 1. A re-run of the same tag now simply overwrites, which is
     what a re-run after a failed run needs. New code always gets a new
     tag and the tag is in the filename, so that is the only time a key
     repeats. Known cost: a tester who already fetched that key can be
     served the older copy from the edge until it expires.
   - **Runs `dotnet test SmartStreamer4.sln` first and aborts on any
     failure** (issue #50 fix). A failure here is as likely to be a red
     test as a packaging problem.
   - Builds with `-p:InformationalVersion=<label>+<commit-sha>` so the
     in-app About display and update check report the right version, and
     verifies the published exe's embedded `ProductVersion` matches.
     Refuses to package if not.
   - Produces `SmartStreamer4-<tag>-win-x64.zip`, tag in the filename,
     so a tester can tell builds apart on disk.
   - Writes **no checksum sidecar** for a preview (removed 2026-09-20:
     no tester ever asked for one). The SHA256 is still printed. GA
     keeps `SHA256SUMS.txt` as a release asset. (wx7v.net links GA
     straight from GitHub Releases, not from this bucket; the
     `SHA256SUMS*.txt` objects and the `v0.3.2` zip at that prefix are
     leftovers from earlier tester builds.)
   - Uploads the zip to `wx7v-downloads/smartstreamer4/`, then verifies:
     it HEADs the public URL with a unique query string, requires a 200
     with a `content-length` equal to the local zip, and retries for up
     to two minutes. The upload reports success without confirming
     anything landed, and a truncated object still answers 200. The
     query string is a separate cache key at the edge, so a cached
     response cannot answer for the object. **This proves the object is
     in R2, not that the plain link is fresh after an overwrite.**
   - Prints the tester link.

If the verify step fails after a successful upload, check by hand before
doing anything else: `curl -sI "<url>?cb=<random>"` should show 200 and
the right `content-length`, and `cf-cache-status` on the plain URL tells
you whether the edge is answering from cache. Never pre-check the plain
URL before a run; that is what caches a 404 for it.

**Upload only, no pruning.** Preview zips accumulate under that prefix
by design (operator decision, 2026-09-20). The bucket's
`expire-beta-builds` lifecycle rule is scoped to the literal `beta/`
prefix, which this repo does not use, so nothing we upload ever
auto-expires. Do not add a pruning step without being asked.

**Tell testers about SmartScreen up front.** No build is code-signed, so
the warning fires on every Windows artifact. Unmentioned, it becomes the
feedback instead of the bug report.

Preview builds carry no release notes: nothing reads
`RELEASE_NOTES-<tag>.md` in this mode.

### GA — published release (`.\publish-release.ps1 -Publish`)

**`--latest` is hard-coded**, so publishing any clean tag makes it the
current release and prompts every operator whose version it outranks.

Before running it:

1. Commit, tag the clean `vX.Y.Z` **annotated**, `git push origin main`
   then `git push origin <tag>`, and close any running instance, exactly
   as steps 1–4 above.
2. Confirm `RELEASE_NOTES-<tag>.md` exists at the repo root and is
   finalized. The file is gitignored. Claude drafts it by analyzing the
   actual code diffs between the prior tag and HEAD, never by
   summarizing commit messages alone; the operator reviews and edits in
   place.
3. Run `.\publish-release.ps1 -Publish`. The script:
   - Refuses a `-preview` tag. The in-app updater in every build fielded
     before 2026-09-08 reads the releases list without skipping
     pre-releases, so publishing a preview in any form would prompt
     every operator on the previous GA.
   - Fails fast **before** the build on a missing precondition: tag on
     `origin`, notes file present and non-empty.
   - Runs the same tests, build, embedded-version check and zip as
     `-Preview`, then writes the bare `SHA256SUMS.txt`.
   - Runs `gh release create $tag $zip SHA256SUMS.txt --title ...
     --notes-file ... --latest`, attaching the sidecar as a release
     asset. Nothing is committed, so `origin/main` HEAD stays equal to
     the tag commit. The script does not expose `--prerelease` (the
     retired `b` suffix caused that wrong-flag mistake before; the
     preview guard removes the temptation).
   - Attaches the zip, not a raw `.exe` (browsers block `.exe`
     downloads from GitHub Releases).
4. Post-publish (manual). Re-launch a clean install of the prior
   published release on a tester machine and confirm it sees the new
   release as available. Then install the new release and confirm it
   reports "up to date". If either fails, pull the release immediately.

**No separate live test of the GA zip, named deliberately.** `-Publish`
builds and publishes in one invocation, so there is no moment between
the two to extract the GA zip and check About. That is the cost of the
two-flag interface (operator decision, 2026-09-20): the code reaching
GA is the code already validated as a `-previewN` build, the two zips
differ only in the embedded version string, and step 4 catches a broken
update flow after the fact. If a GA-zip live test is ever wanted back,
it needs a third mode, not a silent re-split of this one.

**Tradeoff in the tag ordering, named deliberately.** The tag is pushed
before the zip is built, so a failed build or live test means deleting a
tag that is already on origin, not just a local one. The previous
ordering built first and pushed the tag only once the zip was good,
which made a bad tag cheap to retract. It was reordered to match how the
operator actually works, and because a pushed tag with no GitHub Release
attached is invisible to operators anyway. If it does fail:
`git push origin :refs/tags/<tag>` then delete locally, fix, retag.

**Never delete `artifacts/release/SHA256SUMS.txt`.** `/artifacts/` is
gitignored, but that one file is tracked (`git ls-files artifacts`
returns it and nothing else). It is the frozen v0.1.18b-era cumulative
hash snapshot, and it is the only thing in that whole tree the pipeline
still depends on. Everything else under `artifacts/release/` is legacy:
pre-GA zips (newest `v0.1.12b`, April 2026), extracted alpha build
directories, and three orphaned `RELEASE_NOTES-*.md`. The current script
has never written there. Treat a non-empty `artifacts/release/` as a
cleanup task, not as release output.

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
  FlexLib 4.2.20 client, targeting SmartSDR 4.2.x server radios.
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
- **Setup Guide viewer** (Help tab; despite the class name it is not the
  CW wizard):
  [SetupWizardWindow.axaml](SetupWizardWindow.axaml) /
  [SetupWizardWindow.axaml.cs](SetupWizardWindow.axaml.cs) +
  [SETUP_GUIDE.md](SETUP_GUIDE.md) (embedded resource).
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
- FlexLib runtime compatibility: the app targets FlexLib 4.2.20 against
  **SmartSDR 4.2.x servers only**. Support for 4.1.5 and earlier was
  dropped 2026-08-03; do not add code, tests, or docs that claim it.
  Historical 4.1.5 notes in the migration guide stay as a record of how
  the API got here, not as a supported configuration.
- Release versioning: the csproj `<Version>` stays at a clean numeric
  default; the release version comes from the git tag at HEAD
  (`vMAJOR.MINOR.PATCH` for GA, `vMAJOR.MINOR.PATCH-previewN` for a
  tester build — see Build & Release).
- Changelogs/release notes: always analyze actual code diffs between
  tags/commits, never summarize from commit messages alone.
- Ask questions and present decisions in prose dialog, not
  multiple-choice prompts (AskUserQuestion). The operator prefers
  discussing options conversationally.

## References

- [CONTRIBUTING.md](CONTRIBUTING.md) — direct-commit workflow (owner);
  branches park incomplete work; PRs for outside contributors.
- [Flexlib4-2-Migration-Guide.md](Flexlib4-2-Migration-Guide.md) —
  the 4.1.5 → 4.2.x migration record (corrected per issue #60; the app
  builds against 4.2.20 as of issue #61).
- [PLAN-skimmer-resync-and-refactor.md](PLAN-skimmer-resync-and-refactor.md) —
  current CW Skimmer sync redesign plan.
- [README.md](README.md) — user-facing project description, install,
  usage.
