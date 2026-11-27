# Changelog

## 1.0.0 (2026-11-27)

First release, Windows 10 and 11.

- Taskbar pill: running jobs per machine, the total, and the queue coloured by the oldest wait.
- Hover card: busy machines with each job's step and elapsed time, the queue per repo; click a row to open it on GitHub.
- Show all window: search, machine and status filters, grouping and sorting.
- Config in `%USERPROFILE%\.ci-widget\config.json`, reloaded on save; several GitHub accounts through `gh` or a token variable.
- Stays inside GitHub's rate limit: ETag cache, quiet repos read less often, a per-account call budget.
- Installer and uninstaller with no admin rights and a self-test gate.
