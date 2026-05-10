# Contributing to SmartStreamer4

## Overview

SmartStreamer4 uses a branch-per-change workflow with pull requests. All development targets the `main` branch.

- **Listed collaborators** work directly in branches off `main` in this repo.
- **Outside contributors** fork the repo on GitHub, push branches to your fork, and open PRs from there.

For non-trivial changes (new features, refactors, anything touching FlexRadio or DAX-IQ integration), please open an issue first so we can agree on the approach before you invest time.

## Prerequisites

- .NET 8 SDK
- Windows (the app targets `net8.0-windows`)
- FlexLib API DLLs (intentionally excluded from version control — see README for setup)

## Workflow

### 1. Start from main

```bash
git checkout main && git pull
```

### 2. Create a focused branch

Name it after what it does and include the issue number when available. Use the bare number — no `#` — because `#` becomes `%23` in GitHub URLs:

```bash
git checkout -b fix/29-dax-not-running-gate
git checkout -b feature/42-skimmer-port-config
```

Save the `#` for commit messages and PR titles, where GitHub auto-links to the issue.

Keep each branch to one bug or one feature. Small scope = fast review.

### 3. Make your changes

Run these gates before committing — all must pass:

```bash
dotnet build SmartSDRIQStreamer.csproj
dotnet test tests/SmartSDRIQStreamer.CWSkimmer.Tests
```

`dotnet test` from the repo root finds nothing because there's no `.sln` file — point it at a folder that contains a test csproj.

Then live-test the change in the running app. Unit tests verify code correctness, not feature correctness — exercise the change against real hardware (radio + DAX + CW Skimmer as relevant) before moving on.

### 4. Commit

Stage specific files rather than `git add -A`:

```bash
git add MainWindow.axaml.cs MainWindowViewModel.cs
git commit -m "Add DAX.exe-running startup gate (#29)"
```

Write commit messages that explain *why*, not just *what*. The diff shows what changed. Reference the issue number with `(#N)` so the commit links back from the git log and shows up on the issue thread.

**Tangential edits picked up along the way?** Don't include them in this PR. Either stash them (`git stash push -- <file>`) and handle them on their own branch + PR, or leave them in your working tree and tackle them after this branch wraps up. Keep each PR focused on one bug or one feature.

### 5. Push and open a pull request

Collaborators:

```bash
git push -u origin fix/29-dax-not-running-gate
gh pr create --base main --title "Add DAX.exe-running startup gate" --body "..."
```

Outside contributors: push to your fork and open the PR from the GitHub UI against `cdub89/SmartStreamer4:main`.

PR body should explain the motivation and any alternatives considered. A short paragraph is plenty for a small change. If the change depends on specific FlexRadio firmware, SmartSDR, or CW Skimmer versions, note them.

### 6. Review

Reviewer (@cdub89) will either approve the PR or request changes. Run a Claude review pass first to catch obvious issues:

- `/ultrareview <PR#>` — full multi-agent cloud review (more thorough, billed)
- `/review <PR#>` — lighter pass; **output stays in your local Claude Code session and is not posted to the PR.** Use it as a triage aid, then either leave your own review comments on GitHub, or copy the relevant findings into a PR comment with `gh pr review <PR#> --comment --body "..."` if you want the contributor to see them verbatim.

**Fixing issues on behalf of the contributor:**

For small fixes (typos, missing disposes, formatting), use GitHub's "Suggested change" UI in a review comment — the contributor clicks "Commit suggestion" and it's applied to their branch with full attribution. Don't silently push commits to a contributor's branch — if you must push a fix yourself, leave a top-level PR comment explaining *what* you changed and *why* so the contributor isn't surprised when they pull.

**After review — what to expect as the PR author:**

*Listed collaborators (branches in this repo):*

- *Approved:* you squash-merge into `main` yourself — either via GitHub's "Squash and merge" button on the PR page, or `gh pr merge <PR#> --squash --delete-branch`.
- *Changes requested:* address each comment, push follow-up commits to the same branch (no force-push, no amend). Leave a brief PR comment summarizing what you addressed so the review → fix trail is visible later. Then click "Re-request review" on the PR. Iterate until approved, then merge.

*Outside contributors (branches on your fork):*

- *Approved:* @cdub89 merges into `main`. After the merge, delete the branch on your fork (GitHub will prompt you).
- *Changes requested:* address each comment, push follow-up commits to your fork's branch (the PR updates automatically). Leave a brief PR comment summarizing what you addressed so the review → fix trail is visible later. Click "Re-request review". Iterate until @cdub89 approves and merges.

## Reporting Bugs

When filing an issue, please include:

- SmartStreamer4 version (or commit hash)
- SmartSDR / FlexRadio firmware version
- CW Skimmer version (if relevant)
- Windows version
- Reproduction steps, expected vs. actual behavior
- Relevant log output

## Tips for Smooth Reviews

- Keep PRs under ~200 lines of diff (added/changed, excluding generated files).
- Run both gates before pushing — reviewers should see a clean diff, not fixup commits.
- Reference the issue number in the PR body if one exists.

## Merging and Releases

PRs are squash-merged into `main` after at least one approval. Releases are cut from `main` using `publish-release.ps1` and tagged `v<version>`.

## License

By submitting a pull request, you agree your contribution will be licensed under the project's MIT License (see [LICENSE](LICENSE)).
