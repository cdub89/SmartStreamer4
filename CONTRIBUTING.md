# Contributing to SmartStreamer4

## Overview

SmartStreamer4 is solo-developed by @cdub89. All development targets the
`main` branch.

- **Project owner**: development commits land directly on `main`. There
  is no PR ceremony for routine work; the review gate is the Codex
  adversarial audit and the blocking gates in [CLAUDE.md](CLAUDE.md),
  which run *before* a commit exists.
- **Branches** are for parking incomplete work, not process: a change
  blocked mid-flight (for example, waiting on the Windows seat for
  build, live-radio, or release-script verification) sits on a branch
  so `main` stays shippable. When the work completes, it merges back;
  a PR wrapper is optional.
- **PRs** are reserved for two cases: changes the owner genuinely wants
  to stare at as a standalone reviewable diff before landing, and all
  outside contributions (below).
- **Issues** track work across sessions and dev seats. Reference them
  from commit messages with `(#N)` so the log links back and the issue
  thread shows the commits.

## Prerequisites

- .NET 8 SDK
- Windows (the app targets `net8.0-windows`; see the Dev Environment
  section of [CLAUDE.md](CLAUDE.md) for the two-seat setup)
- FlexLib API DLLs (intentionally excluded from version control — see
  README for setup)

## Development workflow (owner)

1. **Start from main, on either seat**: `git checkout main && git pull`.
   Never work on a stale clone; a clone left behind invalidates
   file:line references and audit work.
2. **Make the change** following the Task Lifecycle in
   [CLAUDE.md](CLAUDE.md). Run the blocking gates before committing:

   ```bash
   dotnet build SmartSDRIQStreamer.csproj
   dotnet test tests/SmartSDRIQStreamer.CWSkimmer.Tests
   ```

   `dotnet test` from the repo root finds nothing because there's no
   `.sln` file — point it at a folder that contains a test csproj.
   Then live-test in the running app: unit tests verify code
   correctness, not feature correctness — exercise the change against
   real hardware (radio + DAX + CW Skimmer as relevant).
3. **Commit directly to `main`**, staging by name (never `git add -A`).
   Write messages that explain *why*, not just *what*, and reference
   the issue with `(#N)`.
4. **Push at session end**, even for doc-only work — git is the only
   channel the two dev seats share.
5. **Park incomplete work on a branch** named for what it does, with
   the issue number when available (bare number, no `#`, because `#`
   becomes `%23` in GitHub URLs): `fix/29-dax-not-running-gate`.
   Merge it back to `main` once verification completes.

## Outside contributions

Fork the repo, push a branch to your fork, and open a PR against
`cdub89/SmartStreamer4:main`. For non-trivial changes (new features,
refactors, anything touching FlexRadio or DAX-IQ integration), please
open an issue first so we can agree on the approach before you invest
time.

- Keep each PR to one bug or one feature, ideally under ~200 lines of
  diff.
- Run both gates above before pushing, and note any FlexRadio firmware,
  SmartSDR, or CW Skimmer version dependencies in the PR body.
- The PR body should explain the motivation and alternatives
  considered; a short paragraph is plenty for a small change.

Review: @cdub89 reviews (typically with a Claude review pass first),
using GitHub's "Suggested change" UI for small fixes rather than
pushing to your branch. On *changes requested*, push follow-up commits
to your fork's branch (no force-push, no amend), summarize what you
addressed in a PR comment, and re-request review. Approved PRs are
squash-merged by @cdub89; delete the fork branch afterward when GitHub
prompts.

## Reporting Bugs

When filing an issue, please include:

- SmartStreamer4 version (or commit hash)
- SmartSDR / FlexRadio firmware version
- CW Skimmer version (if relevant)
- Windows version
- Reproduction steps, expected vs. actual behavior
- Relevant log output

## Releases

Releases are cut from `main` using `publish-release.ps1` and tagged
`v<version>` (see the Build & Release section of
[CLAUDE.md](CLAUDE.md)).

## License

By submitting a pull request, you agree your contribution will be
licensed under the project's MIT License (see [LICENSE](LICENSE)).
