---
name: issue
description: Drive a SmartStreamer4 GitHub issue through its lifecycle. Use when Chris says "update the issue", "flip it to implemented-confirmed", "label it confirmed", "close <n>", "what issue are we tracking this against", "open an issue for this", or when code for a tracked defect is finished and gates are green.
---

# Issue Lifecycle

Repo: `cdub89/SmartStreamer4`. Pass `--repo cdub89/SmartStreamer4` on every
`gh` call so the skill works from any cwd. Ported from SKCCLogger's `issue`
skill on 2026-09-27 (operator decision: the two projects share one label
vocabulary).

## Label vocabulary (exact strings, do not improvise)

| Label | When |
|---|---|
| `status: implemented-not-confirmed` | Code done, gates green. Apply the moment coding finishes, before the commit. |
| `implemented-confirmed` | Chris's live test passed. Remove the not-confirmed label in the same call. |
| `status: deferred` | Pushed past the current release line, with the triggers that reopen it in a comment. |
| `status: won't-do` | Decided against. |
| `Monitoring` | Shipped, watching field feedback. |

The confirmed label is deliberately `implemented-confirmed` with **no**
`status:` prefix, matching SKCCLogger (settled there 2026-07-09; a
"unification" attempt was reverted the same day). Do not rename it or create
a prefixed duplicate. It does not appear in `gh label list --search
"status"`; search for "implemented".

Chris's own live test against a real radio is the confirmation bar. It does
not require a tagged release or a second tester. For a change that needs a
radio or hardware the dev machine lacks (an Aurora, a Maestro, a specific
FLEX model), the reporter's confirmation on a preview build counts.

History: before 2026-09-27 this repo used `resolved` (coded and tested,
ready for next release), `deferred`, `wontfix` and `monitoring`. They were
renamed in place to the strings above, so closed issues from the beta era
carry the new names. The `beta-*`, `release-gate`, `go-no-go` and
`needs-evidence` labels are historical and are not part of the lifecycle.

## Argument forms

**`/issue <n> implemented`** (or: coding just finished on a tracked issue)

Standing instruction, do this proactively without being asked:

```bash
gh issue edit <n> --repo cdub89/SmartStreamer4 --add-label "status: implemented-not-confirmed" >/dev/null \
  && gh issue comment <n> --repo cdub89/SmartStreamer4 --body-file <scratchpad>/comment_<n>.md
```

**`/issue <n> confirmed`** (Chris said the live test passed)

```bash
gh issue edit <n> --repo cdub89/SmartStreamer4 \
  --remove-label "status: implemented-not-confirmed" --add-label "implemented-confirmed" >/dev/null \
  && gh issue comment <n> --repo cdub89/SmartStreamer4 --body-file <scratchpad>/comment_<n>.md
```

Then ask about closing (see below). **Open plus confirmed is a valid resting
state**, not an oversight: an issue stays open when someone beyond Chris
still has to validate, for example a requester with hardware nobody else
has, or until the release that carries it ships.

**`/issue <n> deferred`** / **`won't-do`**: swap to the matching status
label and comment with the reason and what would reopen it.

**`/issue new`** (or Chris asks "what issue are we tracking this against?")

When a change is more than a one-line edit and no issue covers it, raise the
tracking issue at the START of the work, not at the end. Draft it and get
Chris's go before posting:

- Title: `<version being tested>: <title>`, version first, matching the
  existing `v0.3.3-Preview3 ...` convention.
- Body: symptom in the reporter's words if there is a reporter, evidence,
  root cause or best hypothesis, proposed direction.
- Apply design-by-subtraction: if an existing surface already covers the
  need, say so and recommend against building.
- **No issue for trivial edits.** One-line fixes, typos, comment wording:
  apply and report.

## Comment content standard

Write the comment to a scratchpad `.md` file and post with `--body-file`.
Do not inline long bodies in `--body "..."`; the quoting breaks on
backticks, apostrophes and newlines, and a heredoc inside a compound command
is fragile.

A good implementation comment carries, in prose:

- What was actually wrong (root cause, not the symptom restated).
- What changed, with file names and the key method or helper.
- Why this fix over the alternatives that were considered and rejected.
- What is deliberately untouched, and why the neighbouring behaviour is
  safe.
- Commit state: whether it is committed or left in the working tree.
- Which gates ran and what the live smoke covered, and what it did not.

A good confirmation comment names who confirmed, on what build and date, on
what radio and SmartSDR version, and what the pre-fix build could not do. If
the confirmation came with a twist worth recording (a trailing space on a
station name, a second client bound to the slice), record it; that detail
is the reason the next reporter gets diagnosed quickly.

Reporter-facing language only: plain, operator-benefit, symptom-first. No
em dashes.

## Closing

Closing is Chris's call. Propose it, do not assume it. When he says go:

```bash
gh issue close <n> --repo cdub89/SmartStreamer4 --reason completed
```

Use `--reason not planned` for won't-do. Do not close an issue whose fix is
only in the working tree, and do not close one waiting on an outside
validator. The `release` skill's post-ship step closes the
`implemented-confirmed` issues that the release carries, with a one-line
comment naming the release.

## Reading an issue

`gh issue view <n> --repo cdub89/SmartStreamer4 --comments`. Comments
posted under `cdub89` are often Claude's earlier drafts. Re-judge them on
merit; never cite them back as Chris's decisions. Reporters paste `[FLEX]`
log excerpts and screenshots into issue bodies; read those before advancing
any theory a two-minute read would have falsified (see the Debugging
section of CLAUDE.md on data over prior triage notes).
