# Contributing to SmartStreamer4

Thanks for your interest. SmartStreamer4 is maintained by @cdub89 and
welcomes bug fixes and small features from outside contributors. This
guide takes you from a fresh machine to an open pull request.

## Before you start

For anything beyond a small, obvious fix (a new feature, a refactor,
anything touching FlexRadio or DAX-IQ integration), **open an issue
first** so we can agree on the approach before you invest time. Issues
labelled
[`good first issue`](https://github.com/cdub89/SmartStreamer4/labels/good%20first%20issue)
are a good place to begin.

To get oriented in the code, read [ARCHITECTURE.md](ARCHITECTURE.md)
(module layout, sync model, threading). The
[Quick Start section of CLAUDE.md](CLAUDE.md#quick-start-after-clear)
maps common tasks to the files that own them.

## Prerequisites

- **Windows 10 or 11.** The app targets `net8.0-windows` and does not
  build on Linux or macOS.
- **.NET 8 SDK, version 8.0.419 or a later 8.0.4xx patch.**
  [`global.json`](global.json) pins this. Having only the .NET 9 SDK
  installed is not enough; install the .NET 8 SDK alongside it. Check
  with `dotnet --list-sdks`.
- **Git.**
- **An editor.** Visual Studio 2022, VS Code with the C# Dev Kit, or
  Rider all work.
- **The FlexLib API source**, which is not in this repo (FlexRadio's
  license does not allow us to redistribute it). See the next section.

## Get the code

1. Fork the repo on GitHub, then clone your fork:

   ```powershell
   git clone https://github.com/<your-username>/SmartStreamer4.git
   cd SmartStreamer4
   git remote add upstream https://github.com/cdub89/SmartStreamer4.git
   ```

2. Download **FlexLib API v4.2.20** from
   [FlexRadio](https://www.flexradio.com/software/smartsdr-v4-x-api-flexlib/)
   and extract it so this folder exists at the repo root, spelled
   exactly like this:

   ```text
   SmartStreamer4/
     FlexLib_API_v4.2.20.41343/
       FlexLib/
         FlexLib.csproj
   ```

   The folder is gitignored, so it will never show up in your commits.
   If the build reports that `FlexLib.csproj` cannot be found, this
   folder is missing or named differently.

## Build, test, and run

Always name the solution file. The repo root also contains the app's
`.csproj`, so a bare `dotnet build` fails with `MSB1011`.

```powershell
dotnet build SmartStreamer4.sln
dotnet test SmartStreamer4.sln
dotnet run --project SmartSDRIQStreamer.csproj
```

**Expected warnings.** A clean build prints about 40 `MSB3245`,
`MSB3243`, and `MSB3277` warnings whose paths contain
`FlexLib_API_v4.2.20.41343`. They come from FlexRadio's projects, not
ours, and appear on `main` too. Ignore them. Any **other** warning is
yours to fix before opening a PR.

If the build fails with `MSB3021` or `MSB3027` (file locked), close any
running copy of SmartStreamer4 and build again.

## Coding rules

The full rulebook is [CLAUDE.md](CLAUDE.md). It is written for AI
coding agents, so much of it (the two dev machines, Codex reviews, the
release process) does not apply to you. These are the rules that do.

Two rules come first, because breaking them makes the project harder
to maintain even when the code works:

- **Change only what your fix or feature needs.** Don't reformat,
  reorder, rename, or tidy code and docs you aren't otherwise changing.
  Unrelated edits bury the real change in review and cause merge
  conflicts. If something else needs cleaning up, open an issue or a
  separate PR.
- **Keep docs and comments clear and concise.** Don't add Markdown
  that isn't needed (by-product summaries or notes, often
  AI-generated), and don't expand existing docs with prose that
  restates what's already there. Code comments should say why, briefly.
  Explain your change in the PR description.

The rest:

- **Zero new warnings, zero failing tests.** Build and test must be
  clean (apart from the FlexLib warnings above) before you push.
- **Nullable reference types are on.** Do not silence them with the
  `!` operator, `#pragma warning disable`, or `SuppressMessage`; fix
  the type or add a real null check. When a value can be genuinely
  absent, use `int?` / `string?` rather than a sentinel like `0` or
  `-1`.
- **Modern C# 12 / .NET 8 style**: collection expressions (`[a, b]`),
  primary constructors, target-typed `new()`, raw string literals for
  multi-line text, `System.Text.Json`, and
  `ArgumentNullException.ThrowIfNull`. Match the style of the file you
  are editing.
- **Named constants** for any value used in more than one place, any
  identifier or discriminator, and any threshold or timeout. Use
  underscores in long numbers (`48_000`).
- **Tests for new logic.** New logic in `src/` gets a test in the
  matching `tests/` project (`SmartSDRIQStreamer.CWSkimmer.Tests`,
  `SmartSDRIQStreamer.Digital.Tests`, or `SmartSDRIQStreamer.App.Tests`
  for non-UI root-project code). If something cannot sensibly be unit
  tested (UI wiring, live radio behavior), say so in the PR.
- **No em dashes (—) in text operators see**: dialog messages, status
  lines, labels, in-app help. Use a period, comma, or parentheses.
  Code comments are exempt.
- **Markdown must lint clean.** If you change a `.md` file, run:

  ```powershell
  npx markdownlint-cli2 "**/*.md" "!**/node_modules/**" "!.claude/**" "!.trunk/**" "!RELEASE_NOTES-*.md" "!artifacts/**"
  ```

- **Bug fixes get a short comment** at the fix site: what users saw,
  the root cause, and why this fix. Two to four lines.

## If you don't have a FlexRadio

Most contributors won't own a FlexRadio or run CW Skimmer, and that is
fine. The unit tests cover INI generation, sync math, and digital app
configuration without any hardware. What they cannot cover is live
radio behavior (discovery, slice tracking, audio routing), and those
areas have regressed in code that passed every test.

If your change touches FlexRadio calls, CW Skimmer sync, audio device
selection, or `CwSkimmerWorkflowService`, say in the PR whether you
tested it against a real radio. If you couldn't, that's okay; the
maintainer will run the live check before merging.

## Submit a pull request

1. Sync with upstream and create a branch named for the change, with
   the issue number if there is one (no `#`):

   ```powershell
   git fetch upstream
   git checkout -b fix/29-dax-not-running-gate upstream/main
   ```

2. Make your change, then build and test as above.
3. Commit with a message that explains **why**, not just what, and
   reference the issue as `(#29)`. Stage files by name rather than
   `git add -A` so stray files don't slip in.
4. Push to your fork and open a PR against `cdub89/SmartStreamer4:main`:

   ```powershell
   git push -u origin fix/29-dax-not-running-gate
   ```

Keep each PR to one bug or one feature, ideally under about 200 lines
of diff. In the PR description, include:

- what the change does and why, and any alternatives you considered
  (a short paragraph is plenty for a small change)
- how you tested it, including whether you ran it against a radio
- any FlexRadio firmware, SmartSDR, or CW Skimmer version it depends on

**Review.** @cdub89 reviews, often with an AI review pass first, and
uses GitHub's "Suggested change" feature for small fixes rather than
pushing to your branch. If changes are requested, push follow-up
commits to the same branch (please don't force-push or amend), reply
in the PR summarizing what you addressed, and re-request review.
Approved PRs are squash-merged; delete your branch when GitHub
prompts.

## Reporting bugs

Open an issue on
[GitHub](https://github.com/cdub89/SmartStreamer4/issues) and include:

- SmartStreamer4 version (Help tab, or the commit hash if you built it)
- SmartSDR / FlexRadio firmware version
- CW Skimmer version (if relevant)
- Windows version
- Steps to reproduce, and what you expected vs. what happened
- Relevant lines from `streamer-status.log`, found in
  `%APPDATA%\SmartStreamer4\artifacts\logs\` for an installed copy, or in
  `artifacts\logs\` at the repo root when you run from source

## Maintainer workflow

For reference, the owner works differently from outside contributors:
routine work commits directly to `main` after the blocking gates in
[CLAUDE.md](CLAUDE.md) pass, branches only park incomplete work, and
releases are cut with `publish-release.ps1` (see the Build & Release
section of CLAUDE.md). None of this changes the PR process above.

## License

By submitting a pull request, you agree your contribution will be
licensed under the project's MIT License (see [LICENSE](LICENSE)).
