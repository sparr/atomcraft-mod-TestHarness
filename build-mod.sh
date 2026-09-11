#!/usr/bin/env bash
# Build a mod against the same game install the harness tests against.
#
#   ./build-mod.sh <project.csproj|dir> [--install]
#
# Exists because building someone else's mod needs two things they cannot be expected to
# have arranged: the game's reference assemblies, and a neutral AppData so an install target
# written for Windows does not try to write to a root path on Linux.
set -euo pipefail
cd "$(dirname "$0")"
. lib/common.sh
load_config
require_game_dir

INSTALL_IT=0
PROJECT=""
while [ $# -gt 0 ]; do
  case "$1" in
    --install) INSTALL_IT=1; shift ;;
    *) PROJECT="$1"; shift ;;
  esac
done
[ -n "$PROJECT" ] || die "usage: ./build-mod.sh <project.csproj|dir> [--install]"

# A scratch AppData absorbs install targets that hardcode a Windows user path.
SCRATCH="$TEST_ROOT/scratch-appdata"
mkdir -p "$SCRATCH"

ARGS=(-p:GameInstallDir="$GAME_DIR" -p:AppData="$SCRATCH")
[ "$INSTALL_IT" = 1 ] && ARGS+=(-p:TestInstallDir="$INSTALL")
# Consumers of the harness resolve it here; see the harness README.
ARGS+=(-p:TestHarnessDir="$TEST_ROOT/harness")

# Building in three escalating steps, because a restore against the default package source
# can stall for minutes on some machines even when the network is otherwise fine.
#
#   1. build offline, which works whenever restore assets are already good
#   2. restore from the local package cache, then build offline; this covers a brand new
#      project whose packages are all cached already, which is the usual case here
#   3. a normal restoring build, needed only for a package that is genuinely not cached
build_with_fallback() {
    say "building $PROJECT"
    # Quiet: a missing assets file here is the expected signal to restore, not an error
    # worth showing. A genuine compile error surfaces from the build after the restore.
    nice -n "${NICE:-19}" dotnet build "$PROJECT" --no-restore -v q --nologo "${ARGS[@]}" \
        >/dev/null 2>&1 && return 0

    say "restoring from the local package cache"
    if nice -n "${NICE:-19}" dotnet restore "$PROJECT" --source "$HOME/.nuget/packages" \
           "${ARGS[@]}" >/dev/null 2>&1; then
        nice -n "${NICE:-19}" dotnet build "$PROJECT" --no-restore -v q --nologo "${ARGS[@]}" && return 0
    fi

    say "restoring from configured package sources (this may be slow)"
    nice -n "${NICE:-19}" dotnet build "$PROJECT" -v q --nologo "${ARGS[@]}"
}

build_with_fallback

if [ "$INSTALL_IT" = 1 ]; then
  mkdir -p "$INSTALL/Mods"

  # Projects that know about TestInstallDir have already installed themselves. For the rest,
  # look for a zip named after the project in the conventional places.
  #
  # Deliberately not "any zip modified recently": an up-to-date build writes nothing, so a
  # recency filter silently stages nothing and the run then fails for want of a mod.
  name="$(basename "$PROJECT" .csproj)"
  staged=0
  for candidate in \
      "$(dirname "$PROJECT")/$name.zip" \
      "$(dirname "$PROJECT")/../Release/$name.zip" \
      "$(dirname "$PROJECT")/../build/$name.zip" \
      "$(dirname "$PROJECT")/bin/Debug/$name.zip"; do
    [ -f "$candidate" ] || continue
    cp "$candidate" "$INSTALL/Mods/" && say "staged $(basename "$candidate")"
    staged=1
    break
  done

  if [ "$staged" = 0 ] && [ ! -f "$INSTALL/Mods/$name.zip" ]; then
    say "warning: built $name but found no $name.zip to install"
    say "  looked next to the project, in ../Release, ../build, and bin/Debug"
  fi
fi
