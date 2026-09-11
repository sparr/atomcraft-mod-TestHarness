#!/usr/bin/env bash
# Launch the bootstrapped test install. Locations come from atomcraft-test.conf.
#
#   ./run-game.sh [--headful] [--timeout N] [--cores N] [--no-loader] -- [game args]
set -euo pipefail
cd "$(dirname "$0")"
. lib/common.sh

HEADFUL=0; TIMEOUT=120; CORES=""; LOADER=1; EXTRA=()
while [ $# -gt 0 ]; do
  case "$1" in
    --headful) HEADFUL=1; shift ;;
    --timeout) TIMEOUT="$2"; shift 2 ;;
    --cores)   CORES="$2"; shift 2 ;;
    --no-loader) LOADER=0; shift ;;
    --) shift; EXTRA=("$@"); break ;;
    *) die "unknown argument: $1" ;;
  esac
done

load_config
require_runner
[ -d "$INSTALL" ] || die "no test install at $INSTALL; run ./bootstrap.sh first"
mkdir -p "$PREFIX" "$OUT"

ARGS=()
[ "$LOADER" = 1 ] && ARGS+=(-s GodotMonoModLoader.gd)
export HEADFUL
if [ "$HEADFUL" = 0 ]; then
  ARGS+=(--headless)
else
  # A headful run gets a private framebuffer and silent audio: it must never land on the
  # desktop the user is looking at.
  ARGS+=(--audio-driver Dummy)
  start_xvfb
  trap stop_xvfb EXIT
fi
ARGS+=("${EXTRA[@]+"${EXTRA[@]}"}")

# Memory cap is best effort: absent on runners and on Windows.
MEMCAP=()
if systemctl --user show-environment >/dev/null 2>&1; then
  MEMCAP=(systemd-run --user --scope -q -p MemoryMax="${MEMORY_MAX:-8G}" --)
fi

[ -n "$CORES" ] && export DOTNET_PROCESSOR_COUNT="$CORES"

say "install=$INSTALL runner=$RUNNER prefix=$PREFIX"
say "args: ${ARGS[*]}"

set +e
nice -n "${NICE:-19}" "${MEMCAP[@]}" timeout --foreground -k 10 "$TIMEOUT" \
  bash -c 'set -e; . lib/common.sh; load_config; launch_game "$@"' _ "${ARGS[@]}" \
  >"$OUT/launch.log" 2>&1
rc=$?
set -e
say "exit code: $rc (124 = timed out after ${TIMEOUT}s)"

# `timeout` kills the launcher but not the processes it started, so clean up explicitly.
kill_game
sleep 1

GODOT_LOG="$(godot_log 2>/dev/null || true)"
if [ -n "$GODOT_LOG" ] && [ -f "$GODOT_LOG" ]; then
  cp "$GODOT_LOG" "$OUT/godot.log"
  say "godot.log: $OUT/godot.log ($(wc -l <"$GODOT_LOG") lines)"
else
  say "no godot.log found under the test prefix"
fi

# Artifacts a test wrote. They live under user://, which is inside the Wine prefix, so they
# are collected next to the log and the results rather than left where only this script knows
# to look. The harness empties its directory at the start of every run, so what is here
# belongs to the run that just finished.
# user_dir already resolves through Godot/app_userdata/Atomcraft, including the lowercase
# variant a long-lived prefix uses, so the artifact directory hangs directly off it.
ARTIFACTS="$(user_dir 2>/dev/null || true)/atomtest-artifacts"
if [ -d "$ARTIFACTS" ]; then
  rm -rf "$OUT/artifacts"
  cp -r "$ARTIFACTS" "$OUT/artifacts"
  say "artifacts: $OUT/artifacts ($(find "$OUT/artifacts" -type f | wc -l) file(s))"
fi
exit $rc
