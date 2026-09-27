---
name: release
description: Run the SmartStreamer4 preview or GA release runbook in order. Use when Chris says "prepare for preview<N>", "let's cut a preview", "prep the release", "prepare for GA", "ready to tag", or names a target version. Claude preps through the notes and stops; Chris executes every git and publish command.
---

# Release Runbook (preview and GA)

Follow this top to bottom. Do not improvise the order. Ported from
SKCCLogger's `preview` skill on 2026-09-27, after the v0.3.3 GA prep ran
tests and drafted notes before anyone had asked for the deep audit.

**Ownership line: Claude runs no git write operations at all.** Not
`git commit`, not `git tag` (not even a local tag), not `git push`, not
`gh release`, not `publish-release.ps1` under any flag. Claude preps,
reports, drafts, and hands Chris an exact command sequence. Chris owns the
moment anything goes public. `gh issue` operations are fine.

Everything here runs on the **Windows seat**. The Linux seat has no
`dotnet`, no `pwsh` and no radio, so it can draft notes and nothing else.

## Phase 1: Claude preps

### 1. Codex deep audit FIRST

Before any other release prep, audit the full unshipped diff. Baseline:

- **Preview**: the previous preview tag in this line, or the last published
  GA tag for preview1.
- **GA**: the last published GA tag (`gh release list --limit 1`), always,
  even when every preview in the line had its own audit. The per-change
  audits never see the slate as a unit (operator decision, 2026-09-27).

Hand Codex whole files, not line ranges, and authorize action:

```bash
codex exec --sandbox workspace-write -c sandbox_workspace_write.network_access=true "<deep-audit prompt>" < /dev/null
```

Always redirect stdin or the run hangs forever with zero CPU. Run it in the
background with a generous timeout; it takes ten minutes or more. The prompt
must say: read `AGENTS.md` first; read-only with respect to the repo, no git
writes; never execute `publish-release.ps1`; never read
`~\.skcclogger-signing`; build with `dotnet build SmartStreamer4.sln` and
test with `dotnet test SmartStreamer4.sln` (named explicitly, bare `dotnet
build` is ambiguous); ignore MSB3245/3243/3277 from
`FlexLib_API_v4.2.20.41343`; severity-ranked findings with `file:line` and a
concrete operator failure scenario; findings outside the named files too;
and, for GA, check `RELEASE_NOTES-<tag>.md` against the code.

**Known sandbox artefacts, not findings.** The workspace-write sandbox blocks
writes outside the repo, so inside Codex the Avalonia build telemetry log
fails the first build (Codex sets `AVALONIA_TELEMETRY_OPTOUT=1` and
retries) and the two Digital provisioner tests that write under the real
`%LOCALAPPDATA%` fail. Gate evidence for those two comes from this seat's
own `dotnet test` in step 3. Everything else Codex reports is real until
adjudicated.

Adjudicate every finding with Chris before moving on: blocker, fix in this
release, or follow-up issue. Re-derive any accepted fix through Edit
yourself and re-run the gates it touches. A Codex run that reports it could
not read files or run the build is void, not a pass.

### 2. Settle the version, once

Read the tag history (`git tag --sort=-v:refname | head`). **The base is
pinned across a version line**: every preview leading to a release carries
the same numeric version and only N advances (`v0.3.3-preview1`,
`-preview2`, then the clean `v0.3.3` at GA). The GA tag lands on the same
commit as the last preview, so that commit carries both tags and no new
commit is needed for GA.

State the planned tag and get Chris's explicit confirmation before drafting
anything. Every artifact (notes filename, notes h1, tester issue title)
uses that one string. Never the retired `bN` suffix, never the word "beta".

### 3. Run every gate and report the actual results

- `dotnet build SmartStreamer4.sln`: zero errors, zero first-party warnings
  (FlexLib_API warnings exempt).
- `dotnet test SmartStreamer4.sln`: zero failures. Report the counts.
- markdownlint over the repo, per CLAUDE.md.
- **Live-radio smoke** against a SmartSDR 4.2.x server for every area the
  slate touches (FlexLib calls, CW Skimmer sync, audio device selection,
  workflow service). The preview line's live testing counts for GA only
  when the GA commit is the preview commit. Name what was exercised.
- Repo state: `git status -sb` clean and not behind origin; HEAD is the
  commit that will be tagged.
- Seat state: no `SmartStreamer4` process running (`Get-Process
  SmartStreamer4`); the script's `dotnet publish` fails on locked dlls
  otherwise. Signing prerequisites present: `azure.conf` and
  `jsign-7.5.jar` under `~\.skcclogger-signing\windows`, `java` on PATH,
  `client_secret_expires` not within 30 days. `gh auth status` logged in.
- Tester feedback: nothing open against the previous preview that the slate
  does not answer. Check the issues list and mail since the previous cut.

"Gates pass" without the gate names and numbers is not evidence. A red gate
stops the release.

### 4. Draft the notes

Always from the actual code diff, never from commit messages. Fan out a
`general-purpose` subagent over the `BASE..HEAD` diff to inventory
operator-visible behaviour with `file:line` evidence, then write from that.
Net out intra-line churn: something added and removed inside the window is
neither new nor removed (reflected power in v0.3.3). No em dashes anywhere.

- **GA**: `RELEASE_NOTES-<tag>.md` at the repo root, gitignored, diffed from
  the last published GA tag. `-Publish` refuses to run without it. Chris
  reviews and edits in place. Shape: what the release is in two sentences,
  a line for preview testers, sections per area, a "Fixed" section, an
  issues table, compatibility, and how to verify the download against
  `SHA256SUMS.txt`.
- **Preview**: a GitHub issue titled `SmartStreamer4 <tag> Release Notes`,
  labelled `documentation`, holding the R2 link, an "If you ran an earlier
  preview" section, the issues table with each issue's state, and the
  "Before you install" paragraph about the signature and SmartScreen. Not a
  `RELEASE_NOTES` file; the script reads none in preview mode. Draft the
  body; Chris posts it after the zip is up.

Before recommending a GA cut, apply CLAUDE.md's "no release without
operator-facing benefit": label each commit since the last GA user-facing
or maintainer hygiene, and if nothing clears the bar say so.

### 5. Declare final

Verify the notes h1 or issue title matches the planned tag exactly, that
`git status --short` shows nothing but the gitignored notes file, and that
no pending Claude edits sit in the tree. Say "final" out loud. After this
point Claude makes no further edits; any late change restarts from step 1
for the audit or step 4 for the notes, because a gate run against a
pre-notes tree proves nothing about the tree being tagged.

## Phase 2: Chris executes

Hand him the sequence and run none of it. Annotated tags only; lightweight
tags have caused busted releases. Push `main` before the tag, or origin
carries a tag unreachable from any branch. The script refuses a dirty
working tree, and a running instance breaks its publish step.

**Preview** (`pwsh` 7):

```powershell
git add <named files>; git commit                       # never git add -A
git tag -a vX.Y.Z-previewN -m "SmartStreamer4 vX.Y.Z-previewN"
git push origin main
git push origin vX.Y.Z-previewN
.\publish-release.ps1 -Preview        # tests, build, sign, zip, upload to R2, verify
```

**GA** (`pwsh` 7), on the commit the last preview validated:

```powershell
git tag -a vX.Y.Z -m "SmartStreamer4 vX.Y.Z"
git push origin main
git push origin vX.Y.Z
.\publish-release.ps1 -Publish        # tests, build, sign, zip, SHA256SUMS, gh release create --latest
```

- `-Preview` refuses a clean tag; `-Publish` refuses a preview tag. Never
  publish a preview: builds fielded before 2026-09-08 read the releases
  list without skipping pre-releases.
- `--latest` is hard-coded, so publishing any clean tag prompts every
  operator it outranks.
- If the preview verify step fails after a good upload, check by hand with
  a cache-busted URL (`curl -sI "<url>?cb=<random>"`) before re-running.
  Never pre-check the plain URL; that caches a 404 at the edge.
- If a build fails after the tag is pushed: `git push origin :refs/tags/<tag>`,
  delete locally, fix, retag. The tag stays on origin only once the zip is
  good.

## Phase 3: Post-ship housekeeping

### Preview

1. Chris posts the notes issue with the R2 link. Tell testers about
   SmartScreen up front, or it becomes the feedback instead of the bug
   report. Preview zips accumulate in the bucket by design; no pruning.
2. Update memory: tag, commit, what shipped, what is being watched.

### GA

1. **Manual update-flow check.** On a tester machine, launch a clean install
   of the prior published release and confirm Check for Updates offers the
   new one; install it and confirm it reports up to date. If either fails,
   pull the release immediately.
2. Close the shipped issues with a one-line comment naming the release.
   Refresh the standing help and reporting issue (the "How to report
   issues or get help" issue) so it describes the shipped app, not the
   preview line.
3. File follow-up issues for every audit finding adjudicated as deferred.
4. Update memory: what shipped, what is queued next, what the field is
   being watched for. wx7v.net links GA straight from GitHub Releases, so
   there is no site step.

Never rewrite history to clean up a pushed fix-up commit or a pushed tag.
Cosmetic mess is cheaper than tag-conflict mess.

House rules apply: no em dashes in anything that ships to operators, no
"beta", no `bN` suffix.
