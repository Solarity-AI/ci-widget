#!/usr/bin/env sh
# The repo's 1 verify command: sh tools/verify.sh [windows|windows-core|privacy ...]
#   windows       windows/tests/run.ps1: build, --selftest, scripts-test.ps1 (needs Windows)
#   windows-core  windows/tests/run.ps1 -SkipScripts: build and --selftest only, the fast loop for windows/src (needs Windows)
#   privacy       tools/privacy-check.sh: no private machine, runner or repo names in tracked files
# With no argument it runs every check this machine can run. Prints PASS, FAIL or SKIP and exits non-zero on a failure.
set -u
root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root" || exit 1
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) win=1 ;; *) win=0 ;; esac
if [ $# -eq 0 ]; then
  set -- privacy
  if [ "$win" = 1 ]; then set -- "$@" windows; else echo "SKIP windows (needs Windows)"; fi
fi
fails=0
for p in "$@"; do
  echo "== $p"
  case "$p" in
    windows) powershell -NoProfile -ExecutionPolicy Bypass -File windows/tests/run.ps1 ;;
    windows-core) powershell -NoProfile -ExecutionPolicy Bypass -File windows/tests/run.ps1 -SkipScripts ;;
    privacy) sh tools/privacy-check.sh ;;
    *) echo "unknown check: $p"; false ;;
  esac
  if [ $? -eq 0 ]; then echo "PASS $p"; else echo "FAIL $p"; fails=$((fails + 1)); fi
done
[ "$fails" -eq 0 ]
