# SmartStreamer4 TODO

## Strict-typing and linting audit

Tracked in [issue #50](https://github.com/cdub89/SmartStreamer4/issues/50),
which is the single source of truth for the audit checklist, phase status,
risk ratings, and completion notes. The full checklist previously duplicated
here was an artifact of the dual-seat CLAUDE.md refactor and had drifted
(Phases 1 and 2 were completed 2026-07-23 but never ticked here); it was
removed 2026-07-24. Do not restore a copy; update the issue instead.

## Release readiness: copyright and licensing (outside the linting audit)

Added 2026-07-23 from the wx7v.net distribution work. These block distributing
SmartStreamer4 on the site (see wx7v-site `TODO.md`) and are independent of the
audit tracked in issue #50.

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
- [x] **DO.** Author `THIRD-PARTY-NOTICES.txt` with the verbatim upstream
  license texts for every dependency bundled into the shipped binary (Avalonia
  including the Fluent theme and Inter font packages, CommunityToolkit.Mvvm,
  NAudio, FlexLib, and the .NET runtime, which ships in the zip because
  `publish-release.ps1:153` publishes `--self-contained true`; test-only
  packages such as xUnit are excluded, and the new Digital module adds no NuGet
  packages), and wire `publish-release.ps1` to pack `LICENSE` and the notices
  file into the release zip (today `publish-release.ps1:182` zips only the
  exe). wx7v.net will not host an artifact until its notices ship inside it,
  matching the bar TheMill meets. Risk: Low. *Done: notices file authored in
  the licensing PR, and the FlexLib section 6 placeholder was resolved with
  verifiable license facts in commit `8722056` (2026-07-23); no TODO
  placeholders remain in `THIRD-PARTY-NOTICES.txt`.*
