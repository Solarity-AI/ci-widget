# CI Widget design

## Brief

- **Who:** a developer or small team that runs GitHub Actions on their own machines (self-hosted runners) and on GitHub's.
- **When:** while working, at a glance, many times a day; and when a pull request "is taking forever".
- **Decision it helps make:** is my CI moving, which machine is busy or stuck, and is the queue growing?
- **Next action:** open the job on GitHub, or go fix the machine whose queue is waiting.
- **Evidence they need:** running jobs per machine with how long each has run and its current step; queued work per repo
  with its oldest wait; whether the data is fresh and the API budget is fine.

Hardest screen: the hover card with a dense load (5 machines, 20+ running jobs, a queue of hundreds).
Then: the pill, the "Show all" window, the error states (signed out, rate-limited, offline).

## Direction: "Solarity widget family"

The pill and card follow the existing Solarity taskbar widgets (HW Widget, Machines Widget, AI usage widgets): a dark
or light capsule on the taskbar or the shared widget strip, a card that opens on hover. A brand that exists keeps its
look; this widget changes the composition only:

- The pill is a **row of machine cells**: a 2-letter code over a running-job count, then 1 queue cell. Busy cells glow in
  the run colour; idle cells are flat; offline cells are a hollow outline with a dimmed code, so state never relies on
  colour alone.
- The card is a **list grouped by machine**, each job 2 lines (where it runs, what it is) with its elapsed time on the
  right and a thin step-progress bar under it, so the eye scans down the right edge for long runs.
- The queue is a **count and oldest wait per repo**, never a row per queued run.

Runner-up: a timeline (Gantt) card with a bar per job. It lost because 20+ jobs at taskbar-card size give bars too thin to
read, and the start time matters less than "how long so far" and "which step".

## Tokens

Colours are roles in `windows/src/Render.cs` (`Palette`); components never use raw hex outside it.

| Role | Dark | Light |
| --- | --- | --- |
| Background (pill, card) | #2A2A40 → #18182A | #FFFFFF → #EEF1F8 |
| Text / sub text | #F8FAFC / #A5ABC3 | #111827 / #5B6478 |
| Running (busy) | #34D399 | #047857 |
| Queued (waiting), and a job running over 1 h | #FBBF24 | #92400E |
| Failed or stuck (> 2× the job's limit, errors) | #FF8FA3 | #BE123C |
| Offline / unknown | #6B7280 outline | #9CA3AF outline |
| Link (open on GitHub) | #93C5FD | #1D4ED8 |

- Type: Segoe UI; pill code 8.5 pt semibold, count 11 pt bold; card title 12 pt semibold, rows 9.5 / 8.5 pt.
- Spacing: 4-DIP grid; card padding 14, row gap 6, section gap 12.
- Radius tiers: pill = full capsule, card 12, cells 7, progress bar 2.
- Elevation: the card has a soft shadow (the family's); nothing else casts one.
- Motion: none; the card opens about 0.15 s after the pointer stops on the pill (drawn ahead, never on the hover path).

## Do and don't

- Do say what each number is ("12 running", "579 queued · oldest 8 min"), with units.
- Do show the data's age when it is older than 2 refreshes, and say why (signed out, rate-limited, offline).
- Don't add keyboard shortcuts.
- Don't use purple-to-blue gradients, emoji icons or "supercharge"-style copy.
- Don't put real repo, runner or machine names in screenshots: renders use the invented sample data.

## Taste

- Owner: machines must be very easy to tell apart (1 block each); the hover card must open
  and close in under 1 s, so it is never drawn on the hover path; no keyboard shortcuts.
