# SmartStreamer4 Strict-Typing and Linting Remediation Plan

Audit of the SmartStreamer4 solution (.NET 8 / `net8.0-windows`, C# 12,
Avalonia 11.3.12, CommunityToolkit.Mvvm 8.4.1, NAudio 2.3.0, xUnit 2.9.3),
organized into phases sized for a commit-per-feature workflow with a live test
between commits. A Codex adversarial review adjudicated all 22 findings into
DO, DEFER, and SKIP verdicts; those verdicts are accepted for this repo and are
reflected below. Every original finding is still visible: actionable items are
`- [ ]` checkboxes in a phase or the Deferred section, and findings Codex marked
SKIP are listed (bulleted, with a reason) under Skipped with Codex Concurrence.

**Reference refresh (2026-07-23):** all `file:line` references below were
re-verified against the v0.1.20b tree. The original audit predated the
digital-mode work (v0.1.19b/v0.1.20b), which grew `MainWindowViewModel.cs`
from 2,221 to 3,068 lines, added `src/SmartSDRIQStreamer.Digital/`, and added
`tests/SmartSDRIQStreamer.App.Tests/` and
`tests/SmartSDRIQStreamer.Digital.Tests/`. The new module was never audited;
new findings it introduced are marked "new since audit" inline.

Each finding keeps its `file:line` reference and a self-regression risk rating:

- Risk: Low means compile or build-time only and cannot change runtime behavior.
- Risk: Medium means a behavioral change that is easy to verify.
- Risk: High means the change can itself break user-facing behavior and needs
  targeted live testing.

This is an audit deliverable only. No code was changed and no commits were made.

## Phase 1: Tooling and config enablement (compile-time only)

Goal: enforce the zero-warning gate and clear the small typing violations it
surfaces, without any repo-wide analyzer churn.

- [ ] **DO.** Add `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` so
  nullable and compiler warnings block the build (currently absent in every
  csproj: `SmartSDRIQStreamer.csproj:1`,
  `src/SmartSDRIQStreamer.CWSkimmer/SmartSDRIQStreamer.CWSkimmer.csproj:1`,
  `src/SmartSDRIQStreamer.FlexRadio/SmartSDRIQStreamer.FlexRadio.csproj:1`,
  and, new since audit,
  `src/SmartSDRIQStreamer.Digital/SmartSDRIQStreamer.Digital.csproj:1` plus the
  three test csprojs); put it in a shared `Directory.Build.props` with a
  targeted exclude for the FlexLib subtree. Risk: Low.
- [ ] **DO.** Remove the null-forgiving `!` operators, replacing each with a
  pattern check (`is { } value`) rather than suppression, per CLAUDE.md.
  Current sites:
  `src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerIniModelFactory.cs:88`
  (`OperatorWdmSignalDevIndex!.Value`), `AppSettingsStore.cs:37`
  (`Path.GetDirectoryName(FilePath)!`), and, new since audit,
  `MainWindowViewModel.cs:2292` (`x.Channel!.Value`). Risk: Low.
- [ ] **DO.** Add a GitHub Actions workflow that runs build, test, and
  markdownlint on every push; no `.github/workflows/` directory and no
  pre-commit config exist today. Risk: Low.

Phase risk: Low.

Live test before commit: build must be warning-free, then a smoke pass since a
code file changed. Start and stop audio streaming, connect to a FlexRadio, launch
and stop CW Skimmer, and change and reload a setting.

## Phase 2: Low-risk code hygiene

Goal: remove exact duplication with no intended behavior change.

- [ ] **DO.** Delete `ResolveCwSkimmerIniDirPath` (`MainWindowViewModel.cs:2392`),
  which is byte-for-byte identical to `ResolveCwSkimmerIniDir`
  (`MainWindowViewModel.cs:2201`), and point its two call sites (`:2283`, `:2347`)
  at the survivor. Risk: Low.

Phase risk: Low.

Live test before commit: launch CW Skimmer once to confirm INI files still land
in the expected `artifacts/cwskimmer/ini` folder, since both call sites resolve
through the deduplicated path helper.

## Phase 3: Guarded behavioral changes (one commit per item)

Goal: unify path resolution and stop hiding failures, one isolated and
live-tested commit each.

- [ ] **DO.** Route the four remaining path-resolution copies in
  `MainWindowViewModel.cs` onto the canonical `RuntimePathResolver`
  (`src/SmartSDRIQStreamer.CWSkimmer/RuntimePathResolver.cs:1`), retiring
  `ResolveStreamerLogPath` (`:2173`), `ResolveSpotPayloadLogPath` (`:2184`),
  `ResolveCwSkimmerIniDir` (`:2201`), and the private `TryFindRepoRoot` copy
  (`:2212`). Diff each copy against `RuntimePathResolver` first: a copy may
  behave differently between repo-root and AppData modes, so unifying blindly can
  move where logs and INIs are written. Risk: High.
- [ ] **DO.** Narrow the swallow-all `catch` blocks to the expected exception
  type and log the rest, being careful not to over-narrow and let a previously
  tolerated failure interrupt launch or UI flow
  (`src/SmartSDRIQStreamer.CWSkimmer/DirectSoundProbe.cs:53`,
  `src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerLauncher.cs:146`, `:329`, `:541`,
  `MainWindowViewModel.cs:2146`, `:2167`, `:2361`, `:2526`, `:2581`). The
  one-line `try { ... } catch { }` cancel/dispose guards
  (`MainWindowViewModel.cs:995`, `:1916`, `:2637`, `:2653`) are tolerated and
  out of scope. Risk: Medium.
- [ ] **DO.** Wrap the `async void` event handler bodies in try/catch so
  exceptions do not escape unobserved to the dispatcher (`MainWindow.axaml.cs:96`,
  `:176`, `:196`, `:388`, and, new since audit, `:82`
  (`OnAudioIndexChangesDetected`) and `:216` (`OnBrowseDigitalExe`)). Risk: Low.

Phase risk: High (driven by the path-resolution item).

Live test before commit (run per commit, not once for the phase): for the path
change, confirm CW Skimmer INI generation, the logs folder shortcut, and the spot
and streamer log files all resolve to the correct locations in both repo-root and
installed modes. For the catch narrowing, exercise a launch, telnet, and settings
failure path and confirm failures now surface or log without breaking normal
operation. For the async-void wrapping, trigger the main-window-opened, browse,
and reset-channel handlers and confirm no unobserved crash.

## Deferred: fold into adjacent work

Real findings, but not worth a dedicated commit now. Do each opportunistically
when a nearby change already touches the code.

- [ ] **DEFER.** Enable the full Roslyn analyzer set (`AnalysisMode=All`,
  `AnalysisLevel=latest-all`); valuable but `latest-all` on an existing app is a
  large churn item that can touch async, nullability, and interop paths, not a
  subtraction quick win (no `<AnalysisLevel>` in any csproj today). Risk: Medium.
- [ ] **DEFER.** Replace the FlexLib reflection property probes with typed access
  or one tested helper (`src/SmartSDRIQStreamer.FlexRadio/FlexLibRadioConnection.cs:597`,
  `:641`, `:657`, e.g. the `TryGetTuneStepHz` probe chain at `:590`); a bad
  rewrite breaks RIT, tune-step, or slice sync, so it needs live-radio
  verification and is best done while already touching Flex integration.
  Risk: Medium.
- [ ] **DEFER.** Convert the string discriminators `SliceInfo.Mode`
  (`src/SmartSDRIQStreamer.FlexRadio/RadioDetails.cs:26`) and
  `DiscoveredRadio.Status` (`src/SmartSDRIQStreamer.FlexRadio/DiscoveredRadio.cs:16`)
  into enums. Partially improved since audit: CW-mode comparisons now go through
  the `CwModeName` constant (`MainWindowViewModel.cs:95`) rather than scattered
  literals, but the underlying discriminators are still untyped strings; the
  migration surface (comparisons, serialization, Flex and UI mapping) remains
  wider than the payoff. Risk: Medium.
- [ ] **DEFER.** Add an `.editorconfig` and gate `dotnet format`; worth doing but
  broad formatting and naming enforcement causes noisy repo-wide churn, so bundle
  it with other hygiene work (none exists today). Risk: Medium.
- [ ] **DEFER.** Break up the 3,068-line `MainWindowViewModel.cs` God object
  (`MainWindowViewModel.cs:1`); grown from 2,221 lines since the original audit
  because the digital-mode work landed inside it. The size problem is real but
  this is a major architectural refactor that can sever event, status, and sync
  wiring, not a quick win. Risk: High.
- [ ] **DEFER.** Split the long methods in `CwSkimmerLauncher.cs` (`LaunchAsync`
  `:93`, `ConnectTelnetAsync` `:178`, `BuildDiagnostics` `:366`,
  `DisconnectTelnetAsync` `:697`); a maintainability win, but helper
  extraction in this high-coupling code can change launch and telnet lifecycle
  ordering. Risk: Medium.
- [ ] **DEFER.** Inject `TimeProvider` into the timing-sensitive paths per
  CLAUDE.md so debounce and echo windows are testable; not worth touching until
  timing code is already under change (`DateTime.UtcNow` / `Now` is used 16 times,
  `TimeProvider` zero: `src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerSyncTracker.cs:119`,
  `ThrottledStatusEmitter.cs:27`, `:101`). Risk: Medium.
- [ ] **DEFER.** Hoist the remaining repeated identifier literals into shared
  constants. Partially done since audit: `AppDataPaths.cs:14`-`:15` now centralize
  the `"SDRIQStreamer"` / `"SmartStreamer4"` folder names. Still repeated:
  `"SmartStreamer4"` (`src/SmartSDRIQStreamer.CWSkimmer/CwSkimmerTelnetClient.cs:59`,
  `src/SmartSDRIQStreamer.CWSkimmer/RuntimePathResolver.cs:36`,
  `src/SmartSDRIQStreamer.FlexRadio/FlexLibRadioDiscovery.cs:38`) and
  `"artifacts"` / `"cwskimmer"` / `"ini"` / `"logs"`
  (`src/SmartSDRIQStreamer.CWSkimmer/RuntimePathResolver.cs:17`, `:20`, `:31`,
  `:37`, `MainWindowViewModel.cs:2179`-`:2207`); worth doing opportunistically,
  but doing it now is mostly mechanical churn. Risk: Low.

## Skipped with Codex Concurrence

Findings Codex judged not worth acting on for this repo. Kept here so none is
silently dropped.

- Remove the runtime `git` version probe (`RunGit` at
  `MainWindowViewModel.cs:2532`, `TryGetGitTag` at `:2501`). Reason: the original
  audit overstated this and billed it as the direct-user-benefit fix; that
  billing was wrong. `ResolveReleaseTag` (`MainWindowViewModel.cs:2445`) already
  prefers the assembly `InformationalVersion` and only uses the git probe as a
  convenience fallback, so a shipped binary outside a repo still shows a sane
  version. No action needed. Risk: Low.
- Add external analyzer packages (StyleCop, Roslynator, Meziantou, SonarAnalyzer).
  Reason: built-in .NET analyzers plus `TreatWarningsAsErrors` and CI are the
  lower-cost win; extra packages add ongoing rule-surface and maintenance noise.
  Risk: Low.
- Re-enable markdownlint `MD013` line length (`.markdownlint.json:1`). Reason:
  low user benefit and high annoyance cost for a docs-heavy repo. Risk: Low.
- Add or delete `tools/WinMMEnum/WinMMEnum.csproj`. Reason: a small utility that
  is harmless outside the solution. Note the solution drift Codex flagged: two
  solution files exist (`SmartSDRIQStreamer.slnx:1` and
  `src/SmartSDRIQStreamer.slnx:1`), and `tools/WinMMEnum` is absent from both.
  Risk: Low.
- Apply `ConfigureAwait(false)` uniformly across the libraries
  (`CwSkimmerLauncher.cs`, `FlexLibRadioConnection.cs`). Reason: consistency-only
  in a desktop app that uses explicit callbacks and events; little user value.
  Risk: Low.
- Collapse the spot-color helpers `NormalizeSpotColor`
  (`MainWindowViewModel.cs:2229`) / `NormalizeSpotBackgroundColor` (`:2244`) and
  `UpdateSpotColorSelection` (`:2259`) / `UpdateSpotBackgroundColorSelection`
  (`:2270`). Reason: saves little code and the current duplication is still
  readable. Risk: Low.
- Replace the channel-1 and channel-2 duplicated surface
  (`MainWindowViewModel.cs:331` onward, plus roughly 40 repeated Ch1/Ch2 members)
  with a channel-indexed collection. Reason: for exactly two channels the
  abstraction cost and misrouting risk exceed the value. Risk: High.

## Coverage note

All 22 findings are retained: 3 in Phase 1, 1 in Phase 2, 3 in Phase 3, 8
deferred, and 7 skipped with Codex concurrence. This matches the Codex tally of
7 DO, 8 DEFER, and 7 SKIP.

## Release readiness: copyright and licensing (outside the linting audit)

Added 2026-07-23 from the wx7v.net distribution work. These block distributing
SmartStreamer4 on the site (see wx7v-site `TODO.md`) and are independent of the
audit phases above.

- [x] **DO.** Fix the LICENSE copyright line. It currently reads "Copyright (c)
  2026 Chris L. White, WX7V and Cursor.AI Agent generated code assistance"; the
  Cursor model inserted itself, and a tool is not a copyright holder. Change it
  to "Copyright (c) 2026 Chris L. White, WX7V" (`LICENSE:3`). Risk: Low.
  *Done in the licensing PR.*
- [x] **DO.** Sweep the repo for other copyright or attribution text that may
  have picked up the same tool credit and align it with the corrected LICENSE.
  Sweep result: exactly one code hit, the About-tab string `AboutDevelopedBy`
  (`MainWindowViewModel.cs:476`, "Developed by Chris L White, WX7V and
  Cursor.AI Premium Agent v31.14"); `README.md` and `CONTRIBUTING.md` are
  clean. Risk: Low. *Done in the licensing PR; verify the About tab on the
  next Windows live run.*
- [ ] **DO.** Author `THIRD-PARTY-NOTICES.txt` with the verbatim upstream
  license texts for every dependency bundled into the shipped binary (Avalonia
  including the Fluent theme and Inter font packages, CommunityToolkit.Mvvm,
  NAudio, FlexLib, and the .NET runtime, which ships in the zip because
  `publish-release.ps1:153` publishes `--self-contained true`; test-only
  packages such as xUnit are excluded, and the new Digital module adds no NuGet
  packages), and wire `publish-release.ps1` to pack `LICENSE` and the notices
  file into the release zip (today `publish-release.ps1:182` zips only the
  exe). wx7v.net will not host an artifact until its notices ship inside it,
  matching the bar TheMill meets. Risk: Low. *Mostly done in the licensing PR:
  notices file authored with verbatim Avalonia, Inter, CommunityToolkit,
  NAudio, and .NET runtime texts, and the script now packs LICENSE + notices
  and refuses to zip while the notices contain a TODO placeholder. Remaining:
  paste the verbatim FlexLib license/EULA text from the
  `FlexLib_API_v4.2.18.41174` distribution on the Windows box into section 6.*
