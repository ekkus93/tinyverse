#!/usr/bin/env bash
# Drives the Unity editor in batch mode for the client project.
#
# Usage: scripts/unity.sh <compile|test-edit|test-play|test|build|run> [project-dir]
#   project-dir defaults to client/ next to this script's repository root.
#
# Only one editor can have a given project open at a time. Use a separate
# git worktree to run this in parallel with another editor.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Temporary files go in the repo's own tmp/ (git-ignored), not the system /tmp.
export TMPDIR="$root/tmp"
mkdir -p "$TMPDIR"
cmd="${1:-}"
project="$(cd "${2:-$root/client}" && pwd)"
version="$(sed -n 's/^m_EditorVersion: //p' "$project/ProjectSettings/ProjectVersion.txt")"
unity="${UNITY_EDITOR:-$HOME/Unity/Hub/Editor/$version/Editor/Unity}"
logs="$project/Logs"
mkdir -p "$logs"

[ -x "$unity" ] || { echo "Unity editor $version not found at $unity (set UNITY_EDITOR)" >&2; exit 1; }

# Prints the pass/fail totals of an NUnit result file and the failed tests.
summarise() {
  local xml="$1"
  [ -f "$xml" ] || { echo "no test results written; see the log" >&2; return 1; }
  grep -o '<test-run [^>]*' "$xml" | grep -oE '(total|passed|failed|skipped)="[0-9]+"' | tr '\n' ' '; echo
  grep -oE '<test-case [^>]*result="Failed"[^>]*' "$xml" | grep -oE 'fullname="[^"]*"' || true
}

run_tests() {
  local platform="$1" xml="$logs/$1-results.xml" log="$logs/$1.log"
  rm -f "$xml"
  "$unity" -batchmode -nographics -projectPath "$project" -runTests -testPlatform "$platform" \
    -testResults "$xml" -logFile "$log"
  local status=$?
  echo "== $platform tests (exit $status)"
  summarise "$xml" || status=1
  [ $status -eq 0 ] || { grep -E 'error CS|Scripts have compiler errors' "$log" | sort -u | head -40; echo "log: $log"; }
  return $status
}

case "$cmd" in
  compile)
    "$unity" -batchmode -nographics -quit -projectPath "$project" -logFile "$logs/import.log"
    status=$?
    if [ $status -ne 0 ]; then
      grep -E 'error CS|Scripts have compiler errors' "$logs/import.log" | sort -u | head -60
      echo "compile failed; log: $logs/import.log" >&2
    else
      echo "compile ok"
    fi
    exit $status ;;
  test-edit) run_tests EditMode ;;
  test-play) run_tests PlayMode ;;
  test) run_tests EditMode; a=$?; run_tests PlayMode; b=$?; exit $(( a || b )) ;;
  build)
    "$unity" -batchmode -nographics -projectPath "$project" -buildTarget Linux64 \
      -executeMethod Tinyverse.Editor.BuildScript.BuildLinux -logFile "$logs/build.log"
    status=$?
    grep -E '\[BuildScript\]|error CS' "$logs/build.log" | sort -u | head -40
    exit $status ;;
  run)
    shift; [ $# -gt 0 ] && shift
    exec "$project/Builds/Linux/tinyverse.x86_64" -logFile "$logs/player.log" "$@" ;;
  *)
    sed -n '2,8p' "${BASH_SOURCE[0]}" >&2; exit 2 ;;
esac
