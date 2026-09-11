#!/usr/bin/env bash
# Build the harness, launch the isolated game copy headless, run the suite, report.
#
#   ./run-tests.sh [--no-build] [--timeout N] [--cores N] [--force-exit N] [-- extra game args]
#
# Exit code is the suite's own: 0 all passed, non-zero otherwise. 124 means the run timed out.
set -euo pipefail
cd "$(dirname "$0")"
. lib/common.sh
load_config

BUILD=1; TIMEOUT=300; CORES=""; DETERMINISM=0; GAME_ARGS=(--atomtest-run); STAGE_MODS=()
while [ $# -gt 0 ]; do
  case "$1" in
    --no-build)   BUILD=0; shift ;;
    --timeout)    TIMEOUT="$2"; shift 2 ;;
    --cores)      CORES="$2"; shift 2 ;;
    --determinism) DETERMINISM=1; shift ;;
    --mod)        STAGE_MODS+=("$2"); shift 2 ;;
    --force-exit) GAME_ARGS+=("--atomtest-force-exit=$2"); shift 2 ;;
    --) shift; GAME_ARGS+=("$@"); break ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

[ -d "$INSTALL" ] || die "no test install at $INSTALL; run ./bootstrap.sh first"
mkdir -p "$OUT" "$INSTALL/Mods"

# Mods under test. The harness and a test mod install themselves via TestInstallDir, but
# the mod actually being tested is someone else's project with its own conventions, so its
# zip is staged here rather than by changing their build.
for mod in "${STAGE_MODS[@]+"${STAGE_MODS[@]}"}"; do
  [ -f "$mod" ] || die "mod zip not found: $mod"
  cp "$mod" "$INSTALL/Mods/" && echo "==> staged $(basename "$mod")"
done

# --no-build skips packaging as well as compiling, so the zip in the install can be older
# than the sources. Silently testing stale code is worse than spending the build.
warn_if_stale() {
  local zip="$INSTALL/Mods/${ModId:-TestHarness}.zip"
  [ -f "$zip" ] || { echo "==> WARNING: no mod zip installed at $zip" >&2; return; }
  local newer
  newer="$(find src -name '*.cs' -newer "$zip" 2>/dev/null | head -3)"
  if [ -n "$newer" ]; then
    echo "==> WARNING: installed mod zip is older than sources; --no-build is testing stale code" >&2
    echo "$newer" | sed 's/^/      newer: /' >&2
  fi
}

# Builds offline when the restore assets are already good, which is the common case and
# costs nothing, and falls back to a restoring build for a fresh checkout. Keeps a working
# tree buildable on a machine where package restore is slow or unreachable.
build_project() {
  local project="$1"; shift
  nice -n "${NICE:-19}" dotnet build "$project" --no-restore -v q --nologo "$@" >/dev/null 2>&1 && return 0
  echo "    restoring $project"
  nice -n "${NICE:-19}" dotnet build "$project" -v q --nologo "$@" >/dev/null
}

if [ "$BUILD" = 1 ]; then
  echo "==> building harness"
  require_game_dir
  GameInstallDir="$GAME_DIR" TestInstallDir="$INSTALL" TestRoot="$TEST_ROOT" \
    build_project src/TestHarness.csproj

  # The harness's own tests are a separate mod, built the way a consumer builds theirs:
  # against the exported assembly rather than a project reference, so the path consumers
  # use breaks here first if it breaks at all.
  echo "==> building harness tests"
  GameInstallDir="$GAME_DIR" TestInstallDir="$INSTALL" TestHarnessDir="$TEST_ROOT/harness" \
    build_project test/TestHarness.Test.csproj
fi

# Runs the suite twice at different core counts and compares the recorded checksums.
# Stepping is single-threaded, so the results must match; a difference means something in
# the simulation depends on how many cores the machine has, which would show up elsewhere
# as tests that pass locally and fail on a runner.
if [ "$DETERMINISM" = 1 ]; then
  echo "==> determinism: comparing checksums across core counts"
  for n in 2 "$(nproc)"; do
    "$0" --no-build --timeout "$TIMEOUT" --cores "$n" \
      -- --atomtest-filter=Determinism >/dev/null 2>&1 || true
    grep -h '"event":"determinism"' "$OUT/results.jsonl" > "$OUT/determinism-$n.jsonl" || true
    echo "    cores=$n: $(wc -l < "$OUT/determinism-$n.jsonl") record(s)"
    sed 's/^/      /' "$OUT/determinism-$n.jsonl"
  done
  # Comparing nothing would otherwise succeed: two empty files diff clean, and the mode
  # would report a match having measured no determinism at all.
  for n in 2 "$(nproc)"; do
    [ -s "$OUT/determinism-$n.jsonl" ] || {
      echo "==> determinism: no records at cores=$n; the filter matched no tests" >&2
      exit 1
    }
  done

  if diff <(sed 's/,"processorCount".*//' "$OUT/determinism-2.jsonl") \
          <(sed 's/,"processorCount".*//' "$OUT/determinism-$(nproc).jsonl") >/dev/null; then
    echo "==> determinism: checksums match across core counts"
    exit 0
  fi
  echo "==> determinism: CHECKSUMS DIFFER across core counts" >&2
  exit 1
fi

[ "$BUILD" = 1 ] || warn_if_stale

CORE_ARGS=(); [ -n "$CORES" ] && CORE_ARGS=(--cores "$CORES")
# Two background watchdogs, both needed because a run that never reaches the harness gives
# no records and no useful exit code on its own.
#
# 1. Boot: if the harness has not announced itself within BOOT_TIMEOUT, nothing downstream
#    will ever happen. Covers the whole class of "the mod never loaded": an unpatched DLL,
#    a missing mod zip, or a mod loader incompatible with this game build.
# 2. Size: engine and native errors never pass through Godot's C# exception path, so the
#    managed suppressor cannot see them and the log can still run away.
BOOT_TIMEOUT="${BOOT_TIMEOUT:-90}"
LOG_CAP_MB="${LOG_CAP_MB:-64}"

watch_boot() {
  local waited=0 log
  while [ "$waited" -lt "$BOOT_TIMEOUT" ]; do
    sleep 5; waited=$((waited + 5))
    log="$(godot_log 2>/dev/null || true)"
    [ -n "$log" ] && [ -f "$log" ] || continue
    grep -qF '##ATOMTEST##' "$log" && return
  done
  echo "==> harness did not start within ${BOOT_TIMEOUT}s; killing run" >&2
  kill_game
}

watch_log_size() {
  local log
  while sleep 5; do
    log="$(godot_log 2>/dev/null || true)"
    [ -n "$log" ] && [ -f "$log" ] || continue
    if [ $(( $(stat -c%s "$log" 2>/dev/null || echo 0) / 1048576 )) -ge "$LOG_CAP_MB" ]; then
      echo "==> godot.log exceeded ${LOG_CAP_MB}MB; killing run" >&2
      kill_game
      return
    fi
  done
}

watch_boot &
BOOT_WATCHER=$!
watch_log_size &
WATCHER=$!

set +e
./run-game.sh --timeout "$TIMEOUT" "${CORE_ARGS[@]}" -- -- "${GAME_ARGS[@]}" >"$OUT/run.log" 2>&1
launch_rc=$?
set -e
kill "$WATCHER" "$BOOT_WATCHER" 2>/dev/null || true
sed 's/^/    /' "$OUT/run.log"

RECORDS="$OUT/results.jsonl"
if [ -f "$OUT/godot.log" ]; then
  grep -F '##ATOMTEST##' "$OUT/godot.log" | sed 's/^.*##ATOMTEST## //' >"$RECORDS" || true
else
  : >"$RECORDS"
fi

echo "==> records: $RECORDS ($(wc -l <"$RECORDS") lines)"
sed 's/^/    /' "$RECORDS"

if [ "$launch_rc" = 124 ]; then
  echo "==> TIMED OUT after ${TIMEOUT}s"
elif ! grep -q '"event":"run_end"' "$RECORDS"; then
  echo "==> NO run_end RECORD: the harness did not complete (launch rc=$launch_rc)"
  # Distinguish "the harness ran and died" from "the harness never loaded", which is
  # usually an install problem rather than a test failure.
  if [ -f "$OUT/godot.log" ] && ! grep -qF '##ATOMTEST##' "$OUT/godot.log"; then
    echo "==> the harness never initialized. Likely causes, in order:"
    grep -qF 'the game needs to be patched' "$OUT/godot.log" \
      && echo "    - the game copy is not patched; re-run ./bootstrap.sh"
    grep -qF 'Error initializing mod loader' "$OUT/godot.log" \
      && echo "    - the mod loader failed against this game build (see godot.log for the type it could not load)"
    grep -qF 'Modules to load: []' "$OUT/godot.log" \
      && echo "    - no mod zip found under $INSTALL/Mods"
    grep -qF 'Failed to get GodotPlugins initialization' "$OUT/godot.log" \
      && echo "    - the .NET runtime did not start; check WINEDLLOVERRIDES (never disable mscoree)"
    echo "    full log: $OUT/godot.log"
  fi
  [ "$launch_rc" = 0 ] && launch_rc=70
fi

echo "==> suite exit code: $launch_rc"
exit $launch_rc
