# Rules for AI agents

Verify: `sh tools/verify.sh` (every check this machine can run; CI runs `privacy` and `windows`, 1 per job).

CI Widget: a small taskbar pill that shows every running and queued GitHub Actions job across your repos, grouped by the
machine it runs on, with a hover card and a "Show all" list. Windows only for now. Built from source on the user's machine;
no admin rights. Its only network calls go to api.github.com, with the token `gh` already holds.

## Commands

- `sh tools/verify.sh windows-core`: build with `windows/build.ps1`, then the self-test: the fast loop for `windows/src`.
- `sh tools/verify.sh windows`: the same, then `windows/tests/scripts-test.ps1` (scratch folders only, never the real
  Startup folder or config). Run it for a change to the installers or `windows/*.cmd`/`*.ps1`.
- `sh tools/verify.sh privacy`: `tools/privacy-check.sh`, no tokens, user folders or private names in tracked files.
- Timing on Windows: `privacy` ~1 s, `windows-core` ~4 s, `windows` ~18 s. Output: `PASS <check>` or `FAIL <check>` per check;
  the exit code is non-zero on any failure. Off Windows only `privacy` runs (`SKIP windows`).
- After a build: `windows/bin/CIWidget.exe --selftest <report.txt>`, `--render <dir>` (PNG renders of every state) and
  `--probe <out.json>` (1 live round against GitHub with the real config: a network call, run it only when asked). Always
  pass the file or folder: without it the exe starts the real widget.

## Layout

- `windows/src/GitHub.cs`: the API client (ETag cache, rate-limit budget); `Model.cs` turns runs and jobs into machines;
  `Config.cs` reads `%USERPROFILE%\.ci-widget\config.json` (`CIWIDGET_HOME` overrides the folder).
- `windows/src/Render.cs` draws the pill and the card; `AllView.cs` is the Show all window; `App.cs` the widget and modes;
  `Install.cs` the install and uninstall steps; `TestModes.cs` the self-test, the fake data and `--render`.
- Root `Install CI Widget (Windows).cmd` and `Uninstall CI Widget (Windows).cmd`: tested by `windows/tests/scripts-test.ps1`.

## Tests and changelog

- Self-test checks live in `windows/src/TestModes.cs` (`c.That(ok, "TC-<n> what")`, `c.Eq(want, got, "TC-<n> what")`); give a
  new check the next free TC number (`grep -o 'TC-[0-9]*' windows/src/TestModes.cs`). Installer and script checks live in
  `windows/tests/scripts-test.ps1`.
- Every user-visible change adds a line under `## Unreleased` in `CHANGELOG.md` (plain words, 1 line per change).

## Rules

- `windows/src/WidgetStrip.cs` is the shared top strip, the same file in every Solarity taskbar widget repo: change all copies
  together, or not at all.
- Read `DESIGN.md` before any visual change, and check the renders after it.
- C# 5 only (the in-box csc v4): no `$""`, no `?.`, no `nameof`.
- Line endings come from `.gitattributes`: `.cs`, `.ps1` and `.cmd` are CRLF; `.sh` is LF. Keep them.
- Never put real machine, runner, org or repo names in the repo: examples use the invented org `acme`.
- Tokens are read at run time from `gh` or an environment variable, never stored, logged or sent anywhere but api.github.com.
- Do not run the root installers: they install into the user's session.
- No keyboard shortcuts and no new dependencies.

## Shipping

- 1 issue per piece of work, then a branch `feat/<issue>-<slug>` or `fix/<issue>-<slug>`.
- Commit subjects `feat: … (#<issue>)` or `fix: … (#<issue>)`; the pull request body says `Fixes #<issue>`.
- Squash merge once CI is green.
