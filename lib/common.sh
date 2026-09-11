# Shared configuration and platform abstraction for the Atomcraft test harness.
# Sourced by bootstrap.sh, run-game.sh and run-tests.sh. Not executable on its own.
#
# Nothing here knows where anything is installed. Every location comes from a config file
# or the environment, because installs differ by store (Steam, itch.io, direct), by
# platform (Linux via Proton or Wine, native Windows), and by user.

# --- configuration loading ---------------------------------------------------------------
# Precedence: environment > config file > nothing. There are deliberately no path defaults.
#
#   $ATOMCRAFT_TEST_CONF            explicit path
#   ./atomcraft-test.conf           per checkout
#   $XDG_CONFIG_HOME/atomcraft-test/config   per user
load_config() {
  local here candidate
  here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
  for candidate in \
      "${ATOMCRAFT_TEST_CONF:-}" \
      "$here/atomcraft-test.conf" \
      "${XDG_CONFIG_HOME:-$HOME/.config}/atomcraft-test/config"; do
    [ -n "$candidate" ] && [ -f "$candidate" ] || continue
    # Environment wins: snapshot, source, restore anything that was already set.
    local _g="${GAME_DIR:-}" _r="${RUNNER:-}" _p="${PROTON:-}" _w="${WINE:-}"
    local _s="${STEAM_CLIENT:-}" _a="${APPMANIFEST:-}" _t="${TEST_ROOT:-}"
    # shellcheck disable=SC1090
    . "$candidate"
    [ -n "$_g" ] && GAME_DIR="$_g"
    [ -n "$_r" ] && RUNNER="$_r"
    [ -n "$_p" ] && PROTON="$_p"
    [ -n "$_w" ] && WINE="$_w"
    [ -n "$_s" ] && STEAM_CLIENT="$_s"
    [ -n "$_a" ] && APPMANIFEST="$_a"
    [ -n "$_t" ] && TEST_ROOT="$_t"
    CONFIG_FILE="$candidate"
    break
  done

  : "${TEST_ROOT:=${XDG_CACHE_HOME:-$HOME/.cache}/atomcraft-test}"
  : "${INSTALL:=$TEST_ROOT/install}"
  : "${PREFIX:=$TEST_ROOT/prefix}"
  : "${OUT:=$TEST_ROOT/out}"
  : "${DATA_DIR_NAME:=data_Atomcraft_windows_x86_64}"
  : "${RUNNER:=$(default_runner)}"
  # Defaults must live here, not in require_runner: launch_game runs in a subshell that
  # loads config without re-validating, and an unset WINE there becomes an empty argv[0].
  : "${WINE:=wine}"
}

die() { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }
say() { printf '\033[1m==>\033[0m %s\n' "$*"; }

is_windows() {
  case "$(uname -s 2>/dev/null || echo unknown)" in
    MINGW*|MSYS*|CYGWIN*|Windows_NT) return 0 ;;
    *) return 1 ;;
  esac
}

default_runner() { is_windows && echo native || echo ""; }

config_hint() {
  cat >&2 <<HINT

Configure the harness before running it. Copy the example and edit it:

    cp atomcraft-test.conf.example atomcraft-test.conf

or set the values in the environment. Run './bootstrap.sh --detect' to have the harness
look for installs in the usual places and print a config for you.
HINT
}

# --- validation --------------------------------------------------------------------------
require_game_dir() {
  [ -n "${GAME_DIR:-}" ] || { printf '\033[1;31merror:\033[0m GAME_DIR is not set.\n' >&2; config_hint; exit 1; }
  [ -d "$GAME_DIR" ] || die "GAME_DIR does not exist: $GAME_DIR"
  [ -f "$GAME_DIR/$DATA_DIR_NAME/Atomcraft.dll" ] \
    || die "no $DATA_DIR_NAME/Atomcraft.dll under GAME_DIR: $GAME_DIR"
}

require_runner() {
  case "${RUNNER:-}" in
    proton)
      [ -n "${PROTON:-}" ] || { printf '\033[1;31merror:\033[0m RUNNER=proton but PROTON is not set.\n' >&2; config_hint; exit 1; }
      [ -x "$PROTON" ] || die "PROTON is not executable: $PROTON"
      [ -n "${STEAM_CLIENT:-}" ] || { printf '\033[1;31merror:\033[0m RUNNER=proton but STEAM_CLIENT is not set.\n' >&2; config_hint; exit 1; }
      ;;
    wine)
      command -v "$WINE" >/dev/null 2>&1 || die "wine not found: $WINE"
      ;;
    native)
      is_windows || die "RUNNER=native is only valid on Windows; use proton or wine"
      ;;
    "")
      printf '\033[1;31merror:\033[0m RUNNER is not set (proton, wine, or native).\n' >&2
      config_hint; exit 1 ;;
    *) die "unknown RUNNER: $RUNNER (expected proton, wine, or native)" ;;
  esac
}

# --- build identity ------------------------------------------------------------------------
# Steam records a buildid; itch.io and direct installs do not. A hash of the game assembly
# is the portable identity, and is what actually matters: it changes exactly when the code
# under test changes.
game_identity() {
  local dll="$1/$DATA_DIR_NAME/Atomcraft.dll"
  local hash="unknown"
  [ -f "$dll" ] && hash="$(sha256sum "$dll" 2>/dev/null | cut -d' ' -f1)"
  echo "$hash"
}

steam_buildid() {
  [ -n "${APPMANIFEST:-}" ] && [ -f "$APPMANIFEST" ] || { echo ""; return; }
  grep -oP '"buildid"\s+"\K[0-9]+' "$APPMANIFEST" 2>/dev/null || echo ""
}

# --- display isolation ---------------------------------------------------------------------
# A headless game still has a GUI-capable host: Wine and Proton pop winedbg crash dialogs,
# Gecko/Mono install prompts, and assorted message boxes onto whatever DISPLAY they find.
# Those land on the user's real desktop. Running with no display at all makes that
# impossible and costs nothing, because --headless needs no display anyway.
no_display() { env -u DISPLAY -u WAYLAND_DISPLAY "$@"; }

# For headful runs, allocate a private framebuffer rather than borrowing the user's screen.
XVFB_PID=""
start_xvfb() {
  command -v Xvfb >/dev/null 2>&1 || die "Xvfb is required for headful runs (--headful)"
  local n=99
  while [ -e "/tmp/.X11-unix/X$n" ] && [ "$n" -lt 200 ]; do n=$((n + 1)); done
  Xvfb ":$n" -screen 0 "${XVFB_GEOMETRY:-1280x720x24}" -nolisten tcp >/dev/null 2>&1 &
  XVFB_PID=$!
  sleep 1
  kill -0 "$XVFB_PID" 2>/dev/null || die "Xvfb failed to start on :$n"
  export DISPLAY=":$n"
  say "headful run on private display :$n (Xvfb pid $XVFB_PID)"
}
stop_xvfb() { [ -n "$XVFB_PID" ] && kill "$XVFB_PID" 2>/dev/null || true; XVFB_PID=""; }

# Belt and braces: tell Wine not to raise a crash dialog even if a display is reachable.
#
# Deliberately does NOT set WINEDLLOVERRIDES="mscoree,mshtml=". That is the usual recipe for
# suppressing Wine's Mono and Gecko install prompts, but disabling mscoree breaks .NET
# hosting: the game fails with "Failed to get GodotPlugins initialization function pointer"
# and never starts the runtime. The prompts are not a problem here anyway, since the game
# bundles its own CoreCLR.
suppress_wine_dialogs() {
  local reg="$1/user.reg"
  [ -f "$reg" ] && grep -q 'ShowCrashDialog' "$reg" 2>/dev/null && return
  [ -f "$reg" ] || return 0
  printf '\n[Software\\Wine\\WineDbg]\n"ShowCrashDialog"=dword:00000000\n' >> "$reg"
}

# --- launching -------------------------------------------------------------------------------
# Proton and Wine both need an absolute path to the executable: a bare "AtomCraft.exe" fails
# with "Failed to create process ...: 2" even from the install directory.
launch_game() {
  local exe="$INSTALL/AtomCraft.exe"
  # The loader is passed as `-s GodotMonoModLoader.gd`, which Godot resolves relative to the
  # game directory, so the process must start there regardless of where the wrapper was run.
  cd "$INSTALL" || die "cannot enter $INSTALL"

  # HEADFUL is set by the caller; anything else runs with no display so stray dialogs
  # cannot reach the user's desktop.
  local wrap=(no_display)
  [ "${HEADFUL:-0}" = 1 ] && wrap=(env)

  case "$RUNNER" in
    proton)
      suppress_wine_dialogs "$PREFIX/pfx"
      STEAM_COMPAT_DATA_PATH="$PREFIX" \
      STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM_CLIENT" \
        "${wrap[@]}" "$PROTON" run "$exe" "$@"
      ;;
    wine)
      suppress_wine_dialogs "$PREFIX"
      WINEPREFIX="$PREFIX" WINEDEBUG="${WINEDEBUG:--all}" \
        "${wrap[@]}" "$WINE" "$exe" "$@"
      ;;
    native)
      # Godot resolves user:// under %APPDATA%, read as an environment variable rather than
      # through a shell API: the string APPDATA appears in the export template and no XDG
      # variable does. Redirecting it is therefore how a native Windows run stays isolated
      # from the player's real saves.
      #
      # Note this does NOT work under Wine, which overrides APPDATA from the prefix registry
      # before the process sees it. That is fine, because the wine runner isolates through
      # WINEPREFIX instead; but it is also why this line cannot be verified without a real
      # Windows machine. See PLAN.md section 9.6.
      APPDATA="$PREFIX/AppData/Roaming" "$exe" "$@"
      ;;
  esac
}

# Kill only processes belonging to this harness's prefix.
kill_game() {
  case "$RUNNER" in
    proton)
      local ws="$(dirname "$PROTON")/files/bin/wineserver"
      [ -x "$ws" ] && WINEPREFIX="$PREFIX/pfx" "$ws" -k >/dev/null 2>&1 || true
      ;;
    wine)
      WINEPREFIX="$PREFIX" wineserver -k >/dev/null 2>&1 || true
      ;;
    native)
      taskkill //F //IM AtomCraft.exe >/dev/null 2>&1 || true
      ;;
  esac
}

# --- user:// discovery -------------------------------------------------------------------
# Godot writes to <appdata>/Godot/app_userdata/<project>. Casing differs between freshly
# created prefixes ("Godot") and long-lived ones ("godot"), so the search is case-insensitive.
user_dir() {
  local roots=()
  case "$RUNNER" in
    proton) roots=("$PREFIX/pfx/drive_c/users/steamuser/AppData/Roaming") ;;
    wine)   roots=("$PREFIX/drive_c/users/$USER/AppData/Roaming" \
                   "$PREFIX/drive_c/users/crossover/AppData/Roaming") ;;
    native) roots=("$PREFIX/AppData/Roaming" "${APPDATA:-}") ;;
  esac
  local root
  for root in "${roots[@]}"; do
    [ -n "$root" ] && [ -d "$root" ] || continue
    local found
    found="$(find "$root" -maxdepth 3 -ipath '*/app_userdata/Atomcraft' -type d 2>/dev/null | head -1)"
    [ -n "$found" ] && { echo "$found"; return 0; }
  done
  return 1
}

godot_log() {
  local dir
  dir="$(user_dir)" || return 1
  echo "$dir/logs/godot.log"
}

# --- patcher ------------------------------------------------------------------------------
# The patcher ends in Console.ReadKey(), which throws under redirected stdin *after* writing
# a correct patch. It needs a pty: `script` on Linux and macOS, `winpty` under MSYS2.
run_patcher() {
  local patcher="$1" dll="$2" log="$3" cmd
  cmd="$(printf '%q %q' "$patcher" "$dll")"
  # The patcher crashes on purpose-built error paths; with a display reachable that raises
  # winedbg dialogs on the user's desktop, so it never gets one.
  if command -v script >/dev/null 2>&1 && ! is_windows; then
    printf 'x\nx\n' | no_display script -qec "$cmd" /dev/null >"$log" 2>&1 || true
  elif command -v winpty >/dev/null 2>&1; then
    printf 'x\nx\n' | winpty "$patcher" "$dll" >"$log" 2>&1 || true
  else
    printf 'x\nx\n' | "$patcher" "$dll" >"$log" 2>&1 || true
  fi
}

patcher_binary() {
  if is_windows || [ "$RUNNER" = native ]; then
    echo "$INSTALL/AtomcraftPatcher.exe"
  else
    echo "$INSTALL/AtomcraftPatcher"
  fi
}
