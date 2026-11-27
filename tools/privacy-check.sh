#!/usr/bin/env sh
# Fails when a tracked file names a private machine, runner or repo. Examples in this repo use the invented org "acme";
# real names live only in the user's own config.json, outside the repo. The words come from CIW_PRIVATE_WORDS
# ("a|b|c", case-insensitive): a CI secret in this repo, or the owner's shell. Without it only the generic checks run.
set -u
cd "$(dirname "$0")/.." || exit 1
fail=0
if [ -n "${CIW_PRIVATE_WORDS:-}" ]; then
  hits=$(git ls-files -z | xargs -0 grep -I -n -i -E "$CIW_PRIVATE_WORDS" -- 2>/dev/null)
  if [ -n "$hits" ]; then echo "$hits" | cut -d: -f1,2 | head -20; echo "privacy-check: private names found (file:line above)"; fail=1; fi
else
  echo "privacy-check: CIW_PRIVATE_WORDS not set, private-name scan skipped"
fi
# Generic: no tokens and no Windows user folders in tracked files.
hits=$(git ls-files -z | xargs -0 grep -I -n -E 'gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|C:\\Users\\[A-Za-z0-9]+\\' -- 2>/dev/null | grep -v '^tools/privacy-check.sh:')
if [ -n "$hits" ]; then echo "$hits" | cut -d: -f1,2 | head -20; echo "privacy-check: token or user folder found (file:line above)"; fail=1; fi
[ "$fail" -eq 0 ] && echo "privacy-check: clean, $(git ls-files | wc -l | tr -d ' ') tracked files"
exit $fail
