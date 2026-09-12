#!/usr/bin/env bash
# Provision an isolated, patched Atomcraft install for automated testing.
#
#   ./bootstrap.sh [--force] [--detect] [--seed-from <root>]
#
# Never touches the real game install: the copy is built under $TEST_ROOT and the mod
# loader patch is applied to that copy only. Re-run after a game update.
#
# All locations come from atomcraft-test.conf or the environment; see the .example file.
set -euo pipefail
cd "$(dirname "$0")"
. lib/common.sh

# Pinned mod loader. Identity is the checksum; filename and location are not load bearing.
LOADER_SHA256="0dfa7c8bdf8275400edd32fb126547564bd402c139dbd960954f57483f179e2a"
LOADER_SIZE="12240519"
LOADER_SOURCE="https://github.com/sacroimper/GodotMonoModLoader  (Release/GodotMonoModLoader.zip)"

FORCE=0; DETECT=0; SEED_FROM=""
while [ $# -gt 0 ]; do
  case "$1" in
    --force) FORCE=1; shift ;;
    --detect) DETECT=1; shift ;;
    --seed-from) SEED_FROM="${2:-}"; [ -n "$SEED_FROM" ] || die "--seed-from needs a path"; shift 2 ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done

load_config

# --- detection helper ----------------------------------------------------------------------
# Proposes a config by looking in conventional locations. Never used implicitly: a guess that
# silently picks the wrong install is worse than an error that asks.
#
# Game installs and runners are listed separately because they are orthogonal. Any install
# can be driven by any runner: an itch.io copy runs fine under Proton, a Steam copy runs fine
# under plain Wine. The store determines where the files are, not how they are executed.
steam_libraries() {
  local lib vdf line seen=""
  local roots=("$HOME/.local/share/Steam" "$HOME/.steam/steam" "$HOME/Library/Application Support/Steam")
  for lib in "${roots[@]}"; do
    [ -d "$lib" ] || continue
    echo "$lib"
    vdf="$lib/steamapps/libraryfolders.vdf"
    [ -f "$vdf" ] || continue
    while IFS= read -r line; do echo "$line"; done \
      < <(grep -oP '"path"\s+"\K[^"]+' "$vdf" 2>/dev/null || true)
  done | while IFS= read -r lib; do
    # ~/.local/share/Steam and ~/.steam/steam are usually the same directory by symlink,
    # so dedup on the resolved path rather than the spelling.
    lib="$(readlink -f "$lib" 2>/dev/null || echo "$lib")"
    case "$seen" in *"|$lib|"*) continue ;; esac
    seen="$seen|$lib|"; echo "$lib"
  done
}

detect() {
  echo "# Atomcraft test harness: candidates found on this machine."
  echo "# Game installs and runners are independent; pick one of each."
  echo

  echo "# --- game installs (set GAME_DIR to one) ---"
  local lib found=0
  while IFS= read -r lib; do
    [ -d "$lib/steamapps/common/Atomcraft" ] || continue
    echo "GAME_DIR=\"$lib/steamapps/common/Atomcraft\"   # Steam"
    [ -f "$lib/steamapps/appmanifest_2803490.acf" ] \
      && echo "APPMANIFEST=\"$lib/steamapps/appmanifest_2803490.acf\"   # optional, records buildid"
    found=1
  done < <(steam_libraries)

  # itch.io installs wherever the user pointed it, so ask the itch app rather than guessing.
  local itch=""
  if command -v find-itch-games >/dev/null 2>&1; then
    itch="$(find-itch-games app atomcraft 2>/dev/null | head -1 || true)"
  fi
  if [ -n "$itch" ] && [ -d "$itch" ]; then
    echo "GAME_DIR=\"$itch\"   # itch.io (via find-itch-games)"
    found=1
  else
    while IFS= read -r itch; do
      echo "GAME_DIR=\"$(dirname "$itch")\"   # found by search"
      found=1
    done < <(find "$HOME" -maxdepth 5 -name AtomCraft.exe \
               -not -path '*/steamapps/*' -not -path "$TEST_ROOT/*" 2>/dev/null | head -5)
  fi
  [ "$found" = 1 ] || echo "# none found; set GAME_DIR by hand"
  echo

  echo "# --- runners (set RUNNER to one, plus its paths) ---"
  local p client
  for lib in $(steam_libraries); do
    for p in "$lib"/steamapps/common/Proton*/proton; do
      [ -x "$p" ] || continue
      echo "RUNNER=\"proton\"; PROTON=\"$p\""
    done
  done
  for client in "$HOME/.local/share/Steam" "$HOME/.steam/steam"; do
    [ -d "$client" ] && { echo "STEAM_CLIENT=\"$client\"   # required by RUNNER=proton"; break; }
  done
  if command -v "${WINE:-wine}" >/dev/null 2>&1; then
    echo "RUNNER=\"wine\";   WINE=\"$(command -v "${WINE:-wine}")\"   # $("${WINE:-wine}" --version 2>/dev/null)"
  fi
  is_windows && echo "RUNNER=\"native\"   # run the exe directly"
  echo
  echo "# Nothing above is applied automatically. Edit atomcraft-test.conf yourself."
}

if [ "$DETECT" = 1 ]; then detect; exit 0; fi

require_game_dir
require_runner
say "config: ${CONFIG_FILE:-<environment only>}"
say "game:   $GAME_DIR"
say "runner: $RUNNER"

# --- locate and verify the pinned loader zip -----------------------------------------------
find_loader() {
  local c
  for c in "${LOADER_ZIP:-}" "./vendor/GodotMonoModLoader.zip" "$HOME/Downloads/GodotMonoModLoader.zip"; do
    [ -n "$c" ] && [ -f "$c" ] || continue
    if [ "$(sha256sum "$c" | cut -d' ' -f1)" = "$LOADER_SHA256" ]; then echo "$c"; return 0; fi
    echo "  (checksum mismatch, skipping: $c)" >&2
  done
  return 1
}

LOADER_ZIP_FOUND="$(find_loader)" || die "no GodotMonoModLoader.zip matching the pinned checksum.
  expected sha256 $LOADER_SHA256 ($LOADER_SIZE bytes)
  searched: \$LOADER_ZIP, ./vendor/, ~/Downloads/
  source:   $LOADER_SOURCE
  Place the file at one of those paths, or set LOADER_ZIP."
say "loader: $LOADER_ZIP_FOUND (checksum ok)"

IDENTITY="$(game_identity "$GAME_DIR")"
BUILDID="$(steam_buildid)"
say "game identity: ${BUILDID:+buildid $BUILDID, }sha256 ${IDENTITY:0:16}"

# --- seed from an already-provisioned root ----------------------------------------------------
# Two projects sharing one TEST_ROOT put their mod zips and their results in the same place, so
# a failure caused by one surfaces in the other. Overriding TEST_ROOT fixes that and used to
# cost a full re-provision of a 441 MB install; this fills the new root from an existing one
# instead. Hardlinked where the filesystem allows, so the copy is near-free and the two roots
# still cannot write through to each other: the patcher rewrites files rather than editing in
# place, and nothing here writes into the game files at run time.
#
# Mod zips are deliberately not carried over. They are the part that differs between projects,
# and inheriting another project's mods is the confusion this option exists to prevent.
if [ -n "$SEED_FROM" ]; then
  SRC="$SEED_FROM/install"
  [ -d "$SRC" ] || die "no install under $SEED_FROM; expected $SRC"
  [ -f "$SRC/$DATA_DIR_NAME/Atomcraft.dll.backup" ] \
    || die "$SRC is not patched (no Atomcraft.dll.backup); bootstrap it there first"

  if [ -e "$INSTALL" ] && [ "$FORCE" = 0 ]; then
    die "$INSTALL already exists; use --force to replace it from $SEED_FROM"
  fi

  say "seeding $INSTALL from $SRC"
  rm -rf "$INSTALL"
  mkdir -p "$(dirname "$INSTALL")"

  # Hardlinks cannot span filesystems, and a private root under /tmp against a shared one
  # under ~/.cache is exactly that case. cp -al fails partway, having already created the
  # destination, so the fallback has to clear it first or it copies the source *into* the
  # half-made directory and produces an install with no game at its top level.
  if cp -al "$SRC" "$INSTALL" 2>/dev/null; then
    say "hardlinked; the seeded copy costs almost nothing"
  else
    rm -rf "$INSTALL"
    cp -a "$SRC" "$INSTALL"
    say "copied; $SEED_FROM is on a different filesystem so hardlinks were not possible"
  fi

  rm -rf "$INSTALL/Mods"
  mkdir -p "$INSTALL/Mods"

  say "seeded; mods not carried over, install your own with ./build-mod.sh --install"
  exit 0
fi

# --- (re)build the install copy --------------------------------------------------------------
if [ -e "$INSTALL" ] && [ "$FORCE" = 0 ]; then
  PREV="$(sed -n 's/.*"identity": *"\([0-9a-f]*\)".*/\1/p' "$INSTALL/.bootstrap.json" 2>/dev/null || true)"
  if [ "$PREV" = "$IDENTITY" ]; then
    say "install already provisioned for this game build; use --force to rebuild"
    exit 0
  fi
  say "game build changed, rebuilding"
fi

rm -rf "$INSTALL"
mkdir -p "$INSTALL"
say "provisioning $INSTALL"

# Large read-only payloads are hardlinked when the filesystem allows, copied otherwise.
# Hardlinking also pins content: a later store update replaces the file rather than writing
# through, so the test install stays at the identity recorded below until re-bootstrapped.
for f in AtomCraft.exe AtomCraft.pck steam_api64.dll; do
  [ -f "$GAME_DIR/$f" ] || continue
  ln "$GAME_DIR/$f" "$INSTALL/$f" 2>/dev/null || cp -a "$GAME_DIR/$f" "$INSTALL/$f"
done
[ -d "$GAME_DIR/Licenses" ] && cp -a "$GAME_DIR/Licenses" "$INSTALL/"

# The managed assemblies must be writable: the patcher rewrites Atomcraft.dll in place.
cp -a "$GAME_DIR/$DATA_DIR_NAME" "$INSTALL/$DATA_DIR_NAME"

unzip -q -o "$LOADER_ZIP_FOUND" -d "$INSTALL"

# The loader scans <install>/Mods, and the README tells people to drop mod zips there, so
# it needs to exist after bootstrap rather than as a side effect of the first test run.
mkdir -p "$INSTALL/Mods"
chmod +x "$INSTALL/AtomcraftPatcher" 2>/dev/null || true

# --- patch the copy --------------------------------------------------------------------------
PATCHER="$(patcher_binary)"
[ -f "$PATCHER" ] || die "patcher not found in loader zip: $PATCHER"
say "patching $INSTALL/$DATA_DIR_NAME/Atomcraft.dll"
PATCH_LOG="$TEST_ROOT/bootstrap-patch.log"
run_patcher "$PATCHER" "$INSTALL/$DATA_DIR_NAME/Atomcraft.dll" "$PATCH_LOG"
sed 's/^/    /' "$PATCH_LOG"

# The patcher's exit code is unreliable: it can abort in its own exit handler after a
# successful patch, so success is judged from the log and the backup file.
grep -q "Patch applied" "$PATCH_LOG" || die "patcher did not report success (log: $PATCH_LOG)"
[ -f "$INSTALL/$DATA_DIR_NAME/Atomcraft.dll.backup" ] || die "patcher left no .backup (log: $PATCH_LOG)"

cat > "$INSTALL/.bootstrap.json" <<JSON
{
  "identity": "$IDENTITY",
  "buildid": "${BUILDID:-null}",
  "game_dir": "$(readlink -f "$GAME_DIR" 2>/dev/null || echo "$GAME_DIR")",
  "runner": "$RUNNER",
  "loader_sha256": "$LOADER_SHA256",
  "bootstrapped": "$(date -Iseconds)"
}
JSON

say "done. install=$INSTALL runner=$RUNNER"
