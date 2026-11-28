# Changelog

## Unreleased

Fixes from a manual QA pass (#152).

- Installing from a terminal inside an app (like the Claude desktop app) ends in about 1 min instead of waiting 10 min and warning.
- Every machine cell has its own code: a derived code that is taken gets another letter; codes set in the config never change.
- The queue cell shows "…" while reading and "–" without fresh data, instead of 0.
- The README names the right license: the Solarity AI Use License, not MIT.

## 1.0.1 (2026-11-27)

Fixes from a deep QA pass (#97).

- GitHub read failures (a refused token, a server error, a broken reply) show as an error instead of "nothing running"; a rate limit shows as a pause.
- One refused call no longer drops the token for the rest of the round; a hung `gh auth token` times out after 10 s.
- A repo seen by 2 accounts is counted once; runs, jobs and runners past the first 100 are read.
- A config saved mid-edit keeps the last good config and says so.
- Show all keeps the selected row and scroll position when it refreshes.
- The hover card keeps its height limit, and idle machines that don't fit end with "+N".
- Large counts show as "1.2k" in full; queue thresholds stay in order; machine codes never split an emoji.
- Start with Windows says when it could not change the Startup folder.
- The uninstaller finds a widget built inside its own folder.

## 1.0.0 (2026-11-27)

First release, Windows 10 and 11.

- Taskbar pill: running jobs per machine, the total, and the queue coloured by the oldest wait.
- Hover card: busy machines with each job's step and elapsed time, the queue per repo; click a row to open it on GitHub.
- Show all window: search, machine and status filters, grouping and sorting.
- Config in `%USERPROFILE%\.ci-widget\config.json`, reloaded on save; several GitHub accounts through `gh` or a token variable.
- Stays inside GitHub's rate limit: ETag cache, quiet repos read less often, a per-account call budget.
- Installer and uninstaller with no admin rights and a self-test gate.
