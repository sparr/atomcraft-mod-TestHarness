# Atomcraft automated testing: approach and plan

Written 2026-09-10. Based on the decompile of `Atomcraft.dll` at Steam buildid 25221481
plus the shared modding notes in `../CLAUDE.md`. Every claim marked VERIFIED was read out
of the decompiled source or the export template binary; claims marked SPIKE are things no
one has run yet.

---

## 1. What the game gives us to work with

### 1.1 The engine binary is a full Godot 4.4 export template

`strings AtomCraft.exe` VERIFIED the following flags are present and handled:

```
--headless             --display-driver <driver>    --audio-driver <driver>
--fixed-fps <fps>      --disable-render-loop        --quit-after <frames>
--quit                 --main-loop <name>           --script (-s)   --verbose
--write-movie <file>
```

This matters more than anything else in this document. It means the test host can be the
real game binary running with no window, no GPU, no audio, at uncapped frame rate, and
that the mod loader's `-s GodotMonoModLoader.gd` entry point composes with all of it.

### 1.2 The game's own "headless server" concept is a trap -- do not use it

`Game.IsHeadlessServer` is a `public static bool`, defaults to `false`, and is VERIFIED
never assigned anywhere in the shipped code. It gates `Cursors`, `Inventory` and
`EditorHUD` node init in `Game._Ready`, `Simulation.ShouldShowClientFeedback`, and two
branches in `Players`. `SessionType.HeadlessServer` exists and is likewise never
constructed.

It reads like a free no-UI mode. **It is not, and this was proven the hard way in Phase 1.**
The rest of `Game._Ready`, `Client.Process`, and the HUD nodes dereference those skipped
objects unconditionally, so setting it true produces:

- a `NullReferenceException` inside `Game._Ready`, which therefore never completes, and
- `NullReferenceException` from `Atomcraft.Client.Process` and
  `Atomcraft.HUDInventorySlot._Process` **every frame** thereafter.

Because a Harmony postfix does not run when the original method throws, this also made the
harness's own `_Ready` and `_Process` hooks silently never fire, which is a confusing way
for the underlying fault to present (see section 8.3).

The harness keeps `--atomtest-headless-server` as an **opt-in, default off**, purely so a
future game build that wires the flag up can be retested in one command. Real headless
operation comes from Godot's `--headless`, which works perfectly (section 1.1) and needs no
cooperation from the game code at all.

### 1.3 Sim stepping is directly drivable

VERIFIED signatures:

```csharp
public static void Simulation.DoSimTick(float delta)                     // one tick, no pacing
public static void Simulation.Step(SimSnapshot state)                    // the whole-world scan
public static void Simulation.SimulateQuadrant(SimSnapshot state,
        int min_x, int min_y, int max_x, int max_y,
        RectInt window, int[] rasterizationOrder, int tickOutOfFour)     // one rectangle, one thread
public static void Simulation.ToggleSimulationPaused()
public static bool Simulation.IsSimulationPaused { get; private set; }
public static void Simulation.IncreaseFrameRateCap()  // FrameTimes[12] == float.MaxValue
```

`Simulation.Process(delta, tick)` is the real-time pacer: it accumulates wall-clock time
and calls `DoSimTick` when `TickAccumulator >= 1/TargetTicksPerSecond`, and returns early
if `IsSimulationPaused`. So the recipe for a deterministic clock is: pause the sim, then
call `DoSimTick` exactly N times. No Harmony patch is even required for that.

`SimulateQuadrant` being public is the other gift: a test can step an arbitrary rectangle
of the field, single-threaded, without touching chunk activation, avatars, or a session.

### 1.4 Randomness is already mostly deterministic

VERIFIED in `RNG`: the main RNG is a precomputed 512x512x256 volume generated from
`new Random(12345)`, plus permutation tables seeded 42. It is a pure function of
`(x, y, tick)`. The only nondeterministic source is `RNG.NonDeterministicRandom`, and
`RNG.SetNonDeterministicSeed(int)` is public. `Weather` state is the other live variable,
and `Simulation.StartSunny()` pins it.

Caveat: `Simulation.Step` runs `Parallel.ForEach` over active chunks in four disjoint
quadrant passes. It is *designed* to be order-independent, but that is an assumption to
test, not to trust. Conveniently, `SimSnapshot.GetChunkChecksums()` already exists (it
backs the multiplayer desync check) and is a ready-made determinism oracle.

### 1.5 World creation and loading are three lines of C#

VERIFIED from `NewWorldMenu.OnButtonCreatePressed` and `WorldButton.OnButtonPressed`:

```csharp
// create
var w = new SaveData_World {
    Name = "TEST_flat_01",                       // NOTE: this string is the worldgen seed
    EnemySetting = WorldEnemySetting.None,
    Mode = WorldMode.Creative,
    PlanetTypeBaseName = PlanetCarousel.SpotlightPlanetType.BaseName,
    PlanetsDiscovered = new List<string> { ... },
    Spaceship = new SaveData_Spaceship(),
};
FileManager.SaveWorldHeader(w, writeToDisk: true);

// load and enter
Game.LoadWorldHeaderData(w);
Game.StartHostSession(null, null, isOnline: false);   // -> Session.Start()
```

`Session.Start()` awaits `FileManager.LoadOrCreatePlanetAsync`, then `Client.JoinGame()`
and `UI.SetUIPage(PageId.Gameplay)`. There is no menu logic in the path worth reproducing;
the harness can call these directly.

### 1.6 Worldgen is replaceable, which is how we get "small" test worlds

`SimField` is VERIFIED hardcoded to **6144 x 6144** in the `SimSnapshot()` constructor.
That is 37.7M cells, ~151 MB of `short[]` per snapshot, and it is why the user's existing
save is 132 MB. There is no small-world knob, and checking fixture `.universe` files into
git is not viable at that size.

The way out: `FileManager.GetWorldGenerationDelegate(string planetTypeBaseName)` is a
private static returning a `WorldGenerationDelegate`:

```csharp
delegate Task<(SaveData_Map, short[] materialTypeIdMap, short[] heatMap,
               Dictionary<string, Vector2I?>, List<SaveData_NPCState>)> WorldGenerationDelegate(int seed);
```

A Harmony prefix on it can substitute a **fixture generator** that returns an
all-air/flat-floor field in milliseconds instead of running `ProcGen_3` (the real one costs
seconds, and `ProcGenRooms.Init` alone is ~3 s of the 4.3 s boot). The world stays 6144^2
in memory, but it is empty, generated instantly, deterministic by construction, and
`writeToDisk` can be skipped so nothing lands on disk.

So "small pre-defined test worlds" becomes "an empty world plus a painted fixture region",
which is both cheaper and more controllable than shipping saves.

### 1.7 Absolute Y coordinates are semantically loaded

`Simulation.SimulateCoords` VERIFIED branches on `y.IsWeatherRange()`, `y.IsBelowSpace()`,
`y.IsAboveWorkshop()`. Weather spawns at `window.min.Y`; noble gas condenses only in a
band. A "petri dish" region therefore has to declare which altitude band it sits in, and
fixtures must place themselves at a realistic Y. This is a real footgun worth encoding in
the fixture API rather than leaving to each test.

### 1.8 Current install state

- Mod loader is **not installed**: no `GodotMonoModLoader/` next to `AtomCraft.exe`, no
  `Atomcraft.dll.backup`. VERIFIED.
- Proton 10.0 is at `~/Games/Steam/steamapps/common/Proton 10.0`; the game's prefix is
  `compatdata/2803490`. Steam is running but a direct `proton run` does not need it.
- `dotnet` 8.0.130 SDK is installed. `ilspycmd` is at `~/.dotnet/tools/ilspycmd`.
- `Xvfb` and `xvfb-run` are available as a fallback if `--headless` turns out to be a lie.

---

## 2. Best-practice decisions (recommendations, with the reasoning)

### D1. Four tiers, and the default suite is the middle two

| Tier | Runs where | Cost | Covers | In default suite |
| --- | --- | --- | --- | --- |
| **T0 pure** | `dotnet test`, no game | ms | Pure helpers a mod extracted out of its `BaseMaterial` subclasses. Plain `dotnet test`, no harness involvement (see note below). | yes (separate command) |
| **T1 petri dish** | In-game headless, after `Game._Ready`, no session | ~10 ms/test | Pixel behavior: `BaseMaterial.Step`, reactions, heat, wire logic, machine pixels, mod-side per-cell state. The bulk of tests. | **yes** |
| **T2 session** | In-game headless, real session, frame-driven | ~1 s/test | Save/load round-trips, inventory, crafting, blueprints, anything needing `Game.SaveData_World`, avatars, or multi-frame async. | **yes** |
| **T3 headful** | In-game with a real renderer under Xvfb | seconds/test | Synthetic input events, UI, screenshots, anything that must actually draw. | **no**, opt-in only (D8) |

The single most important decision is that **T1 does not need a session**. `Materials`,
`Reactions`, and `Craftables` are all live once `Game._Ready` finishes (~4.3 s), and
`Simulation.CurrentState` is a fully allocated 6144^2 field of air from static init. A T1
test picks an unused rectangle, paints it, calls `Simulation.SimulateQuadrant` for N ticks,
and asserts. No worldgen, no procgen, no disk, no avatar. That keeps the expensive paths
(T2, T3) rare.

Corollary for how mod code should be written: push logic out of `BaseMaterial` subclasses
into pure static helpers wherever it is natural, so T0 can cover it.

**T0 needs nothing from this project and will not get a helper package.** A modder wanting
it writes an ordinary `dotnet test` project referencing their own pure code, and that is
the whole story. The only reusable thing a T0 package could plausibly offer is validation
of material and reaction JSON against `Serializable_MaterialType` and `ReactionType` -- but
those types live in `Atomcraft.dll`, so using them contradicts "no game present", and
mirroring the schema by hand would create a copy that silently rots at every game update
(the schema already grew between the 2026-08-28 pck snapshot and today's). That validation
belongs in T1, inside the game, against the real types, where it cannot drift. Documenting
the `dotnet test` pattern in one paragraph is the correct deliverable; a package is not.

### D2. Custom in-process runner; the harness core depends on no assertion library

There is no way to host VSTest or `dotnet test` inside the game process, and no reason to
try. The runner is ours. The question is only what tests `Assert` with.

**Why filename versioning cannot solve the collision.** The obvious fix -- ship
`Shouldly.4.2.1.dll` so a mod can bring its own -- does not work, because **.NET assembly
identity is metadata, not filename**. Renaming the file leaves the simple name `Shouldly`
untouched. Within one `AssemblyLoadContext` a simple name resolves to exactly one loaded
assembly: a second load of the same name either returns the already-loaded one or throws,
and either way two versions never coexist. Genuine coexistence requires separate ALCs, and
that is closed off here for two reasons: the loader deliberately loads every mod into the
game's own ALC so `ScriptManagerBridge.LookupScriptsInAssembly` can see Godot types, and
types are ALC-scoped, so a consumer's `[GameTest]` loaded in a private ALC would be a
*different type* from the harness's and discovery would silently find nothing. (.NET Core
also has no GAC and no binding redirects, so there is no runtime escape hatch.) Worth a
five-minute confirmation in Phase 0 rather than taking this on trust.

**So invert the problem: the harness core takes no assertion dependency at all.** This is
better than bundling, and it is what makes "bring your own" actually work:

- The harness's own domain assertions -- `r.AssertUniform(field, 0)`,
  `r.AssertContainsOnly(...)`, `r.AssertStable(ticks: 100)` -- throw
  `Atomcraft.Testing.AssertionException`, a type the harness owns.
- The runner treats **any** exception escaping a test as a failure. It does not need to
  know about `ShouldAssertException`, `NUnit.Framework.AssertionException`, or anything
  else, so it never references them.
- Each test mod therefore picks its own assertion library and ships it. Nothing in
  `Testing.zip` competes with that choice.

**No assertion library is adopted at all for now.** The harness's own domain assertions
cover the grid-shaped cases that matter, and plain `if (...) throw new AssertionException(...)`
covers the rest. If a real need for a general-purpose library appears later, the candidates
are Shouldly (MIT, standalone, messages quote the source expression) and `xunit.assert`
(Apache-2.0, leaner); `FluentAssertions` is worth avoiding since v8+ is commercially
licensed by Xceed. Adopting one later costs nothing, because the runner already fails on
any escaping exception and would not need to change.

Residual risk if two test mods eventually ship *different versions of the same* library:
first-load wins. The harness logs every assembly simple name loaded more than once, with
versions and paths, so that surfaces as a legible boot warning rather than a
`MissingMethodException` mid-suite. It only bites when two test mods are installed at once.

Test method shapes the runner supports:

```csharp
[GameTest]                       void FallsOneCellPerTick()     { ... }   // T1, synchronous
[GameTest, Fixture("flat")]      IEnumerator SurvivesSaveLoad() { ... yield return Ticks(60); ... }  // T2
[HeadfulTest]                   IEnumerator ToolbarOpens()     { ... }   // T3, opt-in (D8)
```

`IEnumerator` tests are pumped from a Harmony postfix on `Game._Process`, one step per
frame, which is the only honest way to await `LoadOrCreatePlanetAsync` and friends.

### D3. Determinism is a harness guarantee, not a per-test chore

Before every test the harness pins: sim paused, `Weather` sunny, `RNG.SetNonDeterministicSeed(fixed)`,
`Simulation.TimeOfDay` fixed, tick counter reset to a known value, and the fixture region
cleared to air at a declared Y band. Ticks only advance through `harness.Ticks(n)`.

Add a standing meta-test that runs a nontrivial fixture twice and compares
`GetChunkChecksums()` over the region, to catch the day the `Parallel.ForEach` assumption
breaks (or a mod introduces shared mutable state on a `BaseMaterial` instance, which is
the single most likely mod bug in this codebase).

### D4. One canonical JSONL stream out of the process; every other format is a wrapper job

The game's only reliable output channel is `godot.log` via `GD.Print`, and the runner lives
inside a Windows binary under Proton loaded through a patched DLL. Changing a format string
in there costs a rebuild / re-zip / re-install / relaunch cycle. Changing a `jq` script on
the outside costs nothing. So formatting stays out of the game process:

- the runner writes one JSON record per test to `user://TestResults/results.jsonl`
- the same records are mirrored to the log as `##ATOMTEST## {json}` lines for live tailing
- the process exits `GetTree().Quit(failCount == 0 ? 0 : 1)`
- `run-tests.sh` transforms that stream into whatever a given consumer wants

JSONL over JUnit XML because JUnit has no real spec (it is Jenkins folklore, and every
consumer supports a different subset), and because these tests emit artifacts a flat XML
schema has no slot for: ASCII region dumps, golden image paths, chunk checksums, tick
counts. Those are natural JSON fields and stringified sludge inside `<system-out>`.

GitHub Actions specifically does **not** ingest test XML natively; see section 6.

### D5. Installs and runners are orthogonal, and both are configured, never assumed

Not `steam -applaunch`: that needs Steam running, a logged-in user, and launch options
configured in the client, none of which survive contact with a runner.

**Nothing in the harness knows where anything is installed.** Every location comes from
`atomcraft-test.conf` or the environment, with no built-in path defaults, because installs
differ by store, by platform, and by user. `./bootstrap.sh --detect` proposes a config but
never applies one: a silent wrong guess is worse than an error that asks.

**Where the game came from and how it is executed are independent axes.** The store decides
where the files are; it does not decide how they run. An itch.io copy runs under Proton, a
Steam copy runs under plain Wine, and `--detect` therefore lists installs and runners as two
separate menus rather than pairing them.

| | what it is | needs |
| --- | --- | --- |
| `GAME_DIR` | directory holding `AtomCraft.exe`, `AtomCraft.pck`, `data_Atomcraft_windows_x86_64/` | Steam library, itch.io install, or a direct copy |
| `RUNNER=proton` | Steam's Proton on Linux | `PROTON`, `STEAM_CLIENT` |
| `RUNNER=wine` | plain Wine on Linux or macOS; the only option for an itch.io install with no Steam present | `WINE` (defaults to `wine` on PATH) |
| `RUNNER=native` | Windows, running the exe directly; default under MSYS2 or Git Bash | nothing |

The runner is an abstraction rather than a variable because more than the launch command
differs: prefix layout, `user://` location, the cleanup mechanism, and which patcher binary
to use all vary. `lib/common.sh` owns all four behind `launch_game`, `kill_game`,
`user_dir`, and `run_patcher`.

Two details that bite every runner, learned the hard way (section 8.5):

- **The executable path must be absolute.** `proton run AtomCraft.exe` from the install
  directory fails with `Failed to create process ...: 2`.
- **The process must start in the install directory**, because Godot resolves
  `-s GodotMonoModLoader.gd` relative to it.

itch.io installs wherever the user pointed the app, so `--detect` asks the app rather than
guessing: `find-itch-games app atomcraft` returns the path directly when that tool is
available, with a filesystem search as fallback.

A dedicated throwaway prefix under `$TEST_ROOT` gives a separate `user://`, so **the
player's real worlds and blueprints cannot be touched by a test run**. On native Windows
that isolation comes from redirecting `%APPDATA%`, which is the one part of the design not
yet verified (section 6.1).

Args reach the mod via `OS.GetCmdlineUserArgs()`, after a `--` separator.

### D6. The harness is a published mod that other mods' *test-only* mods depend on

Mod id is `Testing`, and the intent is to publish this for other modders as a de facto
standard. Two things follow.

**Test code ships in a separate mod zip, not in the mod under test.** The loader's
`dependencies` are hard: a module naming a missing dependency lands in ERROR state. So a
test module bundled inside `Pressure.zip` would show a red entry in the mod loader report
for every player who does not have `Testing` installed. Instead:

```
Pressure.zip          -> Pressure/Main                      (what players install)
Pressure.Tests.zip    -> Pressure.Tests/Main
                         dependencies: ["Pressure/Main", "Testing/Main"]
```

Nobody installs `Pressure.Tests.zip` in production, the shipped artifact carries no NUnit
reference, and the ERROR state never arises. This is also the layout other modders will
copy, so it is worth getting right in the example.

**Publishing raises the bar on two things** that would otherwise be deferred:
documentation aimed at someone who does not know Harmony and has not read the loader's
timing quirks, and a worked example mod, which `Pressure.Tests` will effectively be.

It explicitly does **not** yet raise the bar on API stability. The policy for 0.x is
*everything public, nothing promised*: the whole surface in section 3 is public so early
adopters are never blocked by a missing accessor, and breaking changes are expected between
minor versions. What that costs is one obligation -- the harness must expose its version at
runtime and every consumer must be able to assert against it, because the loader's
dependency mechanism carries no version constraints (`../CLAUDE.md` section 4), so a
mismatched pair would otherwise fail with a `MissingMethodException` instead of a message.
A `CHANGELOG.md` with a breaking-change section is the other half. API freeze is a 1.0
conversation, informed by what `Pressure.Tests` actually ends up using.

The harness's own value proposition to a stranger is that they never have to learn *why*
`Initialize()` runs before `Game._Ready`, or that `SimulateQuadrant` is public. If they do,
the API has failed.

Consumers reference `Atomcraft.Testing.dll` with `<Private>false</Private>` and ship only
their own test assembly plus whatever assertion library they chose (D2). A published
`Atomcraft.Testing.props` wires that reference correctly and fails the build if the harness
assembly would be copied into the consumer's zip, which is the same mistake
`../CLAUDE.md` warns about for the game assemblies.

On the id: sacroimper authors the mod loader, so their naming is the convention worth
following, and it is bare descriptive PascalCase with no author prefix --
`MoreSimAreaOptions`, `DirectShipInventory`, `Centrifuges`, `Contracts`. `Testing` is
consistent with that and is settled. The residual first-wins shadowing risk is handled by the
boot-time duplicate-assembly diagnostic from D2, so it costs nothing extra.

The project is **MIT licensed**, and imposes no conditions on mods that depend on it.

It will be published as **`sparr/atomcraft-mod-TestHarness`**. One consequence to settle
before then: a consumer's csproj currently resolves the harness through a `TestHarnessDir`
property pointing at a local build output. That is fine while both live on one machine, and
useless to anyone else. Published consumers need a documented way to reference a released
`Atomcraft.TestHarness.dll`, whether that is "download the release zip and point
TestHarnessDir at it" or something more automatic. The answer belongs in the docs before
the first external consumer, not after.

### D7. Mod-side per-cell state is a first-class concept: the field registry

Assume most nontrivial mods keep something simfield-shaped: an array of the same 6144^2
cell geometry with arbitrary per-cell contents. Pressure is expected to hold **9 shorts per
cell**. The harness must be able to assert over that state without knowing anything about
its layout, and this has to be designed in at Phase 2 rather than bolted on, because it
reshapes the entire region API.

The abstraction is a **registered field**: a mod tells the harness how to read, write,
clear, and render one logical per-cell channel.

```csharp
Harness.Fields.Register(new FieldSpec<short>("pressure.n2") {
    Read   = (x, y) => Pressure.Gas[Pressure.Index(x, y) + N2],
    Write  = (x, y, v) => Pressure.Gas[Pressure.Index(x, y) + N2] = v,
    Clear  = rect => Pressure.ClearRegion(rect),
    Format = v => v.ToString(),        // for ASCII dumps
    Color  = v => Gradient.Sample(v),  // optional, for image dumps
});
```

The game's own three channels register the same way (`core.material`, `core.heat`,
`core.charge`), so nothing about the harness is special-cased for vanilla. Every region
helper then becomes generic over a field name:

```csharp
r.Count("pressure.n2", v => v > 100)
r.Dump("pressure.n2")                  // ASCII art in the failure message
r.Checksum("pressure.n2")              // determinism oracle over mod state
r.AssertUniform("pressure.n2", 0)      // e.g. "nothing leaked into the sealed region"
r.SnapshotImage("pressure.n2", path)   // golden images over mod state
```

Three consequences that are cheap now and painful later:

1. **Per-test isolation only works if mod state resets too.** The `Clear` hook is not
   optional; the harness calls it for every registered field when handing out a region.
   Without it, test N+1 inherits test N's gas.
2. **Save/load round-trip becomes a reusable test, not a bespoke one.** Any mod with
   registered fields gets `Harness.AssertFieldSurvivesSaveLoad("pressure.n2", region)` for
   free, which exercises `OnUniverseSave`/`OnUniverseLoad` and the modded-material-id
   hazard in `../CLAUDE.md` gotcha 3 at the same time. That single helper is probably the
   highest-value thing the harness can offer another modder.
3. **Register per-channel scalars, not one wide struct, unless there is a reason not to.**
   Pressure's 9 shorts are most usefully registered as nine named `FieldSpec<short>`
   (`pressure.n2`, `pressure.o2`, ...) rather than one `FieldSpec<PressureCell>`. Each then
   dumps, checksums, and approves as its own grid, assertions read naturally
   (`r.AssertUniform("pressure.n2", 0)`), and the `PerChannel` renderer (D9) composes them
   on demand. A composite spec can be registered alongside where a test genuinely needs the
   cell as a unit.
4. **Sparse is the expected case, not the fallback.** A mod is unlikely to keep a full
   6144^2 array (9 shorts per cell would be ~648 MiB), but very likely to keep *something*
   structured and addressable by (x, y). The `FieldSpec` contract is `(x, y) -> value`
   precisely so a dictionary, a per-chunk map, a run-length store, and a flat array all
   satisfy it identically. Because sparse is expected, "absent" is first class: every spec
   declares an `Unset` value, dumps show sparsity rather than inventing zeros, and `Clear`
   is required rather than optional.

### D8. The headful tier is opt-in, separately launched, and never in the default run

Real input events and screenshots need a real renderer, which is exactly the thing every
other decision here works to avoid. So T3 is segregated at three levels, not one:

- **Attribute**: `[HeadfulTest]`, and the runner refuses to execute it unless explicitly
  enabled. A headful test accidentally landing in the default suite should fail loudly
  rather than silently pull in a display dependency.
- **Flag**: `run-tests.sh --headful`, which is a distinct launch profile, not a filter over
  the same one. It drops `--headless`, allocates an **unoccupied `DISPLAY` on a fresh
  `Xvfb`**, and forces audio to the dummy driver so a run cannot interfere with whatever is
  on the real desktop.
- **Location**: headful tests live in their own assembly or at minimum their own namespace,
  so it is obvious from the file tree which tests carry the heavier dependency.

Mechanisms available once inside: synthetic input via `Input.ParseInputEvent(...)` (the
game's `InputManager.Process` reads `Input.IsActionJustPressed`, so injected events flow
through the real input path), and screenshots via
`GetViewport().GetTexture().GetImage().SavePng(...)`.

T3 is expected to stay small: a handful of smoke tests proving the UI opens and a tool
fires, plus screenshot goldens where a picture is genuinely the assertion. Anything that
*can* be expressed as T1 or T2 should be.

### D10. Engine exceptions are throttled at the source, attributed, and fail the run

Godot routes every exception escaping a C# override through
`Godot.NativeInterop.ExceptionUtils.LogException`, once per occurrence, with no
backpressure. In Phase 1 a single throwing per-frame method produced a **1.3 million line
`godot.log` in under 90 seconds** (section 8.3). Unhandled at three levels this makes runs
both useless and expensive, so it is handled at all three.

**1. The harness never throws into Godot.** Every callback the harness owns takes a
try/catch: the `ProcessFrame` pump and every Harmony patch body. Patch bodies matter most,
because an exception from a prefix or postfix surfaces at the call site and reads as a game
bug rather than a harness bug.

**2. Throttle at the source.** VERIFIED from the decompiled `GodotSharp.dll`:

```csharp
internal static class ExceptionUtils {
    public static void LogException(Exception e)          // -> GD.PushError(e.ToString())
    public static void LogUnhandledException(Exception e) // -> GD.PushError("Unhandled exception\n" + e)
}
```

The type is internal and therefore not nameable from C#, so it is reached with
`AccessTools.TypeByName` and patched manually; a prefix returning false skips Godot's own
logging. Each distinct **signature** (exception type plus the top three frames) is printed
once in full and thereafter only counted. This is the level that matters, because the Phase
1 flood came from the *game's* `_Process` methods, which the harness cannot wrap.

Two hazards, both designed around. The prefix must never throw, since its own exception
would be logged through the method it is patching: it is wrapped and guarded by a
`[ThreadStatic]` re-entrancy flag, and sim threads mean it must be concurrency-safe. And
GodotSharp internals are not a stable contract, so a missing type or method degrades to "no
throttling" with a warning rather than to a failed boot.

**3. A wrapper backstop.** `run-tests.sh` watches the live `godot.log` and kills the run
past `LOG_CAP_MB` (default 64). Necessary because engine and native errors never pass
through the managed path at all, so levels 1 and 2 cannot see them.

**What the count is for.** Very little, on its own: nobody acts on "4,812". What is
recorded per signature is the first full stack, the owning context, the first frame, the
count, and the frames elapsed. Those give the four things that are actually actionable:

1. **Attribution.** The owning context localises the fault. Phase 2 sets it to the running
   test's name; Phase 1 sets it to `boot`, `run`, or `shutdown`, which already separates
   "the game was broken before we started" from "we broke it".
2. **A pass/fail gate.** Any engine exception fails the run (exit 72), overridable with
   `--atomtest-allow-engine-exceptions`. A test that passes while the game is screaming is
   not a passing test. Phase 2 narrows this from the run to the individual test, with an
   opt-out attribute for tests that deliberately provoke one.
3. **Storm versus blip.** The raw number only informs as a rate. Records carry `frames`,
   `elapsedMs`, and `perSecond`, and **wall clock is the load-bearing denominator**: a storm
   beginning inside `Game._Ready` happens before the pump can attach, so `frames` is still 0
   at exactly the moment the ratio matters most. Verified against the reproducer: 204.6/s
   from one signature against 0.1/s from another separates "this is per-frame and every
   later assertion is suspect" from "one edge case fired once".
4. **An abort threshold.** Past `AbortThreshold` (2000) the run is killed with exit 71,
   emitting the summary it has. The diagnostic value of exception number 50,000 is zero,
   and this converts a hang plus an unbounded log into a fast failure that still carries
   one full stack per signature.

Deliberately **not** built: cross-run tracking of counts for regression detection. Exception
counts are frame-dependent and noisy, so diffing them between runs would produce churn
rather than signal. A signature appearing or disappearing is meaningful; its magnitude is
not.

### D9. Approval snapshots: pluggable renderers, format deliberately unsettled

Terminology first, since "golden image" was the wrong term and drove a bad default. An
**approval test** (also called golden or characterization testing) does not spell out
expected values in code. It renders the subject to a serialized form, compares it against a
committed *approved* file, and fails with a diff. When a change is intentional an explicit
approve step overwrites the reference, and the diff shows up in code review. It suits a 2D
grid unusually well, because hand-writing assertions over a few hundred cells is miserable.

**The format is not one format, and should not be fixed yet.** The three known subjects
want genuinely different serializations:

| Subject | Density | Plausible rendering |
| --- | --- | --- |
| Material ids | 1 value/cell, small alphabet | one glyph per cell plus a legend; reads as a picture |
| Temperature | 1 value/cell, wide range | fixed-width numerics (cells stop being square on screen), or quantised band glyphs when the shape matters more than the values |
| Pressure-style mod state | ~9 values/cell | one grid per channel, or sparse records listing only cells differing from a baseline; a single grid is hopeless |

So the committed decision is the **abstraction**, not the output: a region is rendered by a
named `IRegionRenderer`, and the harness ships several while the shapes of real tests are
still being discovered.

- `Glyph(legend)` -- one char per cell, the picture case
- `FixedWidth(width, format)` -- numeric grid, for temperature and similar
- `PerChannel(fields...)` -- emits one grid per named field, for multi-channel mod state
- `Sparse(baseline)` -- lists only cells differing from a baseline, for mostly-empty regions
  and for 9-shorts-per-cell state where even per-channel grids are too much

Two properties every renderer must have, because they are what makes the format
changeable later without invalidating everything:

1. **Self-describing headers.** Each approved file carries field name(s), region bounds and
   altitude band, tick count, renderer name and options, and harness version. A renderer
   change then shows up as an intelligible diff rather than a wall of moved characters, and
   an approved file is readable years later without the test that produced it.
2. **Deterministic, line-oriented output.** Diffable in `git diff` and reviewable inline in
   a pull request. This is why text is the default and PNG is reserved for T3 screenshots,
   where a picture genuinely is the assertion -- binary blobs cannot be reviewed and invite
   blind re-approval.

The hazard worth naming regardless of format: approval tests make approving a regression
trivially easy. The approve step is explicit (`run-tests.sh --approve <filter>`), never
automatic on failure, and never part of the default run.

Expect this decision to be revisited once Phase 2 and 3 produce real tests. That is the
intent, not a deferral: renderers are cheap to add, and picking a canonical format before
seeing the tests would be guessing.

---

## 3. What the harness mod provides

```
Testing/
  mod.json
  Testing.dll
  Data/…
```

Public API surface, roughly:

- **Boot control** — Harmony patches to skip the
  `SafetyNotice`/`LocalizationIntro` pages, suppress the `SteamClient.Init` failure popup,
  and stop `DiscordIntegration`. Entry from `Initialize()`, which the loader runs before
  `Game._Ready`.
- **Fixture worlds** — prefix on `FileManager.GetWorldGenerationDelegate` returning a
  registry of named synthetic generators (`"empty"`, `"flat"`, `"flat+ceiling"`,
  `"vacuum-chamber"`), each a pure function of a fixed seed.
- **Field registry** — `Harness.Fields.Register(new FieldSpec<T>(name) { Read, Write, Clear,
  Format, Color })` per D7. The game's `core.material` / `core.heat` / `core.charge`
  register through the same path as any mod's channel.
- **Region API** — `var r = Harness.Region(name, w, h, band: Altitude.Surface)` hands out a
  disjoint rectangle of `CurrentState.Field`, cleared to air **and cleared in every
  registered field**, with helpers generic over field name:
  `r.Fill(rect, "Water")`, `r.Set(x, y, "Sand")`, `r.Heat(rect, 500)`,
  `r.Count(field, predicate)`, `r.Checksum(field)`, `r.AssertUniform(field, value)`,
  `r.Dump(field)` (text art for failure messages),
  `r.Approve(field, renderer)` (approval snapshot with a pluggable renderer, D9) **not yet
  implemented, deferred to Phase 3**.
- **Clock** — `Harness.Ticks(n)` steps `SimulateQuadrant` over the region, or
  `Harness.WorldTicks(n)` calls `DoSimTick` for T2.
- **Session control** — `Harness.EnterWorld(fixture, mode, enemies)` /
  `Harness.ExitToMenu()` / `Harness.SaveAndReload()` as `IEnumerator` coroutines.
- **Structures** — `r.Stamp(blueprintName, at)` using `Blueprints.Dict` /
  `Blueprints.GetBlueprintByName`, and `r.Paint(asciiArt, legend)` for hand-written
  layouts in a test file. ASCII layouts are almost certainly the right primary form: they
  are diffable, live next to the assertion, and need no external fixture files.
- **Round-trip helpers** — `Harness.AssertFieldSurvivesSaveLoad(field, region)` and
  friends, per D7 consequence 2.
- **Runner** — attribute discovery across all loaded mod assemblies, filtering, per-test
  isolation, timing, JSONL output, exit code.

---

## 4. Phased plan

### Phase 0 — prerequisites and spikes — **COMPLETE 2026-09-10**

Delivered: `bootstrap.sh`, `run-game.sh`. Results in section 8.

| # | Spike | Result |
| --- | --- | --- |
| 0.1 | Provision a patched install copy | **done** -- `bootstrap.sh`, patches a copy under `$TEST_ROOT`, live install never touched |
| 0.2 | Does `--headless` boot? | **yes** -- full `_Ready`, no GL context, `Textures`/`Prefabs`/`Tilesets` all fine |
| 0.3 | Does it boot with no Steam client? | **yes** -- `SteamClient.Init` fails, is caught, boot continues and exits cleanly |
| 0.4 | Does `-s GodotMonoModLoader.gd` compose with `--headless`? | **yes** -- loader, Harmony 2.4.2, module enumeration, then `Game._Ready` |
| 0.5 | Boot cost | **6.3-6.8 s** to first `_Process`; ~14 s wall clock per process including Proton |
| 0.6 | Isolation | **confirmed** -- test `user://` lives in the throwaway prefix; real prefix mtimes unchanged |
| 0.7 | Does `DOTNET_PROCESSOR_COUNT` constrain the sim? | **yes** -- closed in Phase 1: `--cores 2` reports `processorCount: 2` from inside the game |

Exit criteria fully met once Phase 1 supplied a mod to load.

### Phase 1 — walking skeleton — **COMPLETE 2026-09-10**

Proves the whole pipe before any framework is built on it: shell -> build -> zip -> loader
-> `Initialize()` -> frame pump -> JSONL records -> exit code -> shell. Contains no test
framework by design.

Delivered:

- `Directory.Build.props` (`GameInstallDir`, `TestRoot`, `ModId`), `src/Testing.csproj` with
  `CreateModZip` / `InstallModZip` targets producing the loader's required
  `Testing/<files>` zip layout into `$TEST_ROOT/install/Mods/`
- `mod.json` declaring module `Testing/Main` -> `Atomcraft.Testing.ModEntry`
- `src/ModEntry.cs` -- `OS.GetCmdlineUserArgs()` option parsing, the duplicate-assembly
  diagnostic from D2, and `Log` emitting `##ATOMTEST##` JSON records
- `src/Runner.cs` -- frame pump via `SceneTree.ProcessFrame`, environment probe, a
  consecutive-failure circuit breaker, and the storm abort path
- `src/ExceptionSuppressor.cs` -- engine exception throttling and accounting per D10
- `run-tests.sh` -- builds, launches, extracts `results.jsonl`, propagates the exit code,
  fails the run when no `run_end` record was produced, and kills a run whose `godot.log`
  passes `LOG_CAP_MB`

Verified end to end:

```
{"event":"boot","harness":"0.1.0","headlessServer":false,"run":true}
{"event":"env","processorCount":12,"dotnetProcessorCount":null,"headlessServer":false,
 "materials":1913,"fieldWidth":6144,"fieldHeight":6144,"tick":0,"simPaused":false,"frame":2}
{"event":"run_end","status":"ok","exitCode":0}
==> suite exit code: 0
```

- `--force-exit 3` exits the wrapper 3, so a red suite fails a build
- `--cores 2` reports `processorCount: 2` in-game, **closing spike 0.7**: the
  `DOTNET_PROCESSOR_COUNT` knob survives Proton into the game's CoreCLR
- the probe confirms D1's premise from inside the running game: 1913 materials loaded and a
  6144^2 field allocated at tick 0 with the sim unpaused, by frame 2, with no session

### Phase 2 — T1, the field registry, and the runner (the main event)

- attribute + reflection discovery across all loaded mod assemblies, filtering, reporting,
  JSONL output
- narrow D10's engine-exception gate from the run to the individual test: set
  `ExceptionSuppressor.Context` to the running test's name, move the failure onto that
  test's record, and add the opt-out attribute for tests that provoke one deliberately
- **the field registry (D7)**, with the three vanilla channels registered through the same
  public path a mod would use. Building it now rather than later is the difference between
  a generic API and a vanilla-shaped one with mod state stapled on.
- region allocator (clearing every registered field), paint/assert/dump helpers, ASCII
  layout parser
- deterministic clock and per-test state pinning
- the determinism meta-test from D3, run at several `--cores` values
- seed it with ~10 real tests against vanilla behavior (sand falls, water spreads, steam
  condenses, a known reaction fires within N ticks, heat conducts). Vanilla tests are the
  calibration: if the harness cannot reproduce vanilla behavior it cannot judge a mod's.

The harness tests itself here, before Pressure exists to test.

Exit criteria: `./run-tests.sh` runs the vanilla suite green in a single process in well
under a minute, a deliberately broken assertion produces a legible failure with an ASCII
dump of the region, and a toy registered field (a fake mod channel defined in the harness's
own tests) proves the registry generic path works end to end.

### Phase 3 — T2 sessions, and the first real consumer

- fixture worldgen delegates and the `GetWorldGenerationDelegate` prefix
- `IEnumerator` test pumping from `_Process`
- `EnterWorld` / `SaveAndReload` / `ExitToMenu`
- blueprint stamping
- `AssertFieldSurvivesSaveLoad`, exercising `OnUniverseSave`/`OnUniverseLoad` and the
  unstable-material-id hazard (`../CLAUDE.md` gotcha 3) together
- the `Glyph` approval renderer, moved here from Phase 2. Twenty-four real tests were
  written without wanting one: what they needed was good failure output, which `Dump()`
  already gives, and two of them converged on comparing an inline path signature rather
  than a committed snapshot. D9 said the format should follow real tests, and it has.
  Pressure is the case that changes this, since hand-writing assertions over nine channels
  per cell is the misery approval testing exists for, so the format should be designed
  against it rather than against the tests least likely to use it.
- stand up `Pressure.Tests` as a separate mod zip per D6, which is simultaneously the first
  external consumer of the API and the worked example other modders will copy

Whichever way Pressure goes on storage (a fourth game-hosted field versus its own parallel
structure), it registers through the same `FieldSpec` and the tests do not care. That is
the point of D7, and Phase 3 is where it gets proven rather than asserted.

### Phase 4 — ergonomics, docs, and release

- approval renderers beyond the Phase 2 minimum, and the explicit approve workflow (D9)
- per-test timing and a slow-test report
- `dotnet test` wiring for T0
- a `Directory.Build.props` carrying `GameInstallDir`, and `CreateModZip` / `InstallModZip`
  targets pointed at the test prefix
- documentation written for a modder who has never read the loader source, plus the
  `Testing.Tests` and `Pressure.Tests` layouts as copyable examples
- `LICENSE` (MIT) and `Atomcraft.Testing.props` published for consumers (D2, D6)
- API version exposed and checked at runtime (D6)
- documentation of the post-game-update ritual: re-run bootstrap, re-run the suite

### Phase 5 — T3 headful (deliberately last)

- `run-tests.sh --headful` as a distinct launch profile: no `--headless`, fresh `Xvfb` on
  an unoccupied `DISPLAY`, dummy audio
- `[HeadfulTest]`, refused by the default runner
- synthetic input via `Input.ParseInputEvent`, screenshots via
  `GetViewport().GetTexture().GetImage().SavePng`
- a handful of smoke tests only: UI opens, a tool fires, one screenshot golden

Last because everything above must not depend on it, and because it is the tier that will
break first on a game update.

---

## 5. Risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| ~~`--headless` fails on `Textures`/`Prefabs`/shaders~~ | **retired** | confirmed working in Phase 0.2; no `Xvfb` fallback needed |
| ~~Steam required to boot~~ | **retired** | confirmed Steam-less boot in Phase 0.3 |
| `Parallel.ForEach` makes ticks nondeterministic | high | determinism meta-test; T1 uses single-threaded `SimulateQuadrant` and is unaffected |
| Game API differs between builds, not just across updates | certain, present today | VERIFIED: `Simulation.IsSimulationPaused` exists in the 2026-09-10 build and not the 2026-08-25 one. Direct references only for what the harness truly needs; `Probe` reflection for the rest (section 9.3); game identity recorded in every run header |
| Mod loader supports a narrower range of game builds than the harness does | medium | not fixable from here; detected and reported clearly rather than hidden (section 9.2) |
| 151 MB per `SimSnapshot`, `WorldBuffer` is a `RingBuffer<SimSnapshot>(100)` | medium | cap memory via `systemd-run MemoryMax`, avoid triggering resimulation paths in tests |
| Mod-side fields are huge: 6144^2 x 9 shorts is ~648 MiB for Pressure alone | high | `FieldSpec` contract is `(x,y) -> value` so chunked/sparse providers satisfy it; raise the memory cap deliberately and measure per-registered-field cost in the run header |
| Another mod ships as id `Testing` and silently wins first-wins resolution | medium | harness asserts its own identity and version at boot and fails loudly; namespace assembly, keys, and CLI args (D6) |
| `BaseMaterial` instances are shared across the parallel sim | high (for the mods, not the harness) | this is exactly what T1 + the determinism meta-test are for |
| Tests silently pass because the world never ticked | high | every `Ticks(n)` asserts the tick counter actually advanced |

---

## 6. GitHub Actions compatibility (constraints now, wiring later)

Testing is local for the foreseeable future, and the assumption is that game assemblies
will eventually be obtainable on a runner the way Factorio publishes headless server
packages. So nothing here builds CI. This section exists only to list the decisions that
are cheap to make correctly now and expensive to retrofit.

**The governing principle: there is exactly one way to run the suite, and it is the way CI
would run it.** No "local convenience mode" that diverges. Every constraint below follows
from that.

### 6.1 Hard constraints on the harness

| Constraint | Why | Cost if retrofitted |
| --- | --- | --- |
| **Never requires Steam.** Runs Steam-less always, including locally. `SteamClient.Init` failure and its `PopUpMenu` are suppressed, not tolerated interactively. | A runner has no Steam client and no logged-in user. | Low now, high later: every test written against a Steam-present game may quietly depend on it. |
| **Never requires a GPU or an X server.** `--headless` is the only supported mode. Any feature that cannot work headless does not go in the harness. | Hosted runners have neither. | Medium. Chiefly affects the golden-image feature: use Godot's CPU-side `Image` for region dumps, and verify in Phase 0 that it works with no GL context. |
| **Self-provisioning: the run creates all state it needs.** No dependency on a pre-existing `user://`, hand-placed blueprints, or an install patched once by hand. Fixtures ship inside the mod zip and are written out at boot. | A runner starts with an empty filesystem and a fresh wine prefix. | High. This is the one most likely to be violated by accident during Phase 3. |
| **Non-interactive, always terminates.** Per-test and per-run watchdogs inside the harness (force `Quit`) plus a `timeout` in the wrapper. | A hung sim burns a CI job to its 6-hour limit and gives no diagnostics. | Low now, and it pays for itself locally the first time a test deadlocks. |
| **All diagnostics land in one output directory**, path given by CLI arg. Region dumps, images, the JSONL, and a copy of `godot.log`. | `actions/upload-artifact` takes a path. | Low now, tedious later once dumps are scattered. |

### 6.2 The wrapper is fully parameterized from day one

`run-tests.sh` takes, and defaults sensibly for local use:

```
--install <dir>     game install to copy from
--launcher <proton|wine>   not hardcoded to Proton; a runner may prefer plain wine
--prefix <dir>      throwaway compat/wine prefix
--out <dir>         all artifacts
--filter <pattern>  test selection
--timeout <sec>     run watchdog
--cores <n>         see below
```

Memory capping via `systemd-run --user --scope -p MemoryMax=` stays for local runs but must
**degrade gracefully** when there is no systemd user session, which is the case on hosted
runners.

### 6.3 Core count is a first-class knob

Hosted runners are 2-4 cores; the dev machine is not. `Simulation.Step` runs
`Parallel.ForEach` over active chunks, so a latent order dependence would present as
"passes locally, fails in CI" — the worst failure mode there is.

Two mitigations, both cheap now:

1. T1 already steps via `SimulateQuadrant` single-threaded, so the bulk of the suite is
   immune by construction.
2. `run-tests.sh --cores N` sets `DOTNET_PROCESSOR_COUNT`, which overrides
   `Environment.ProcessorCount` and constrains the thread pool. That makes a 2-core runner
   reproducible on a 16-core desktop, and lets the determinism meta-test from D3 run at
   several core counts locally.

### 6.4 Provisioning is scripted and pinned, not manual

The loader is currently uninstalled and would otherwise be installed by hand once. Instead,
a `bootstrap.sh` that:

1. downloads `GodotMonoModLoader.zip` at a **pinned version with a recorded checksum**
2. copies the game install into a scratch dir (hardlink the 246 MB pck, copy
   `data_Atomcraft_windows_x86_64/`)
3. runs `AtomcraftPatcher` against the **copy**, never the live install
4. records the Steam buildid it patched

Step 3 matters locally too: the patcher mutates `Atomcraft.dll` in place, and the live
install is the one the user actually plays. Step 4 goes into the JSONL run header so a
failure after a game update is attributable to the update rather than to a mod change.

### 6.5 Output format, revisited

GitHub Actions has no native test-XML ingestion — unlike Jenkins or GitLab, nothing in core
GHA parses JUnit, so producing it only pays off alongside a third-party action such as
`dorny/test-reporter`. GHA's native surfaces are `$GITHUB_STEP_SUMMARY` markdown,
`::error file=,line=::` annotations, and the exit code, all of which fall out of the JSONL
stream in the wrapper. The D4 decision stands unchanged, and the wrapper gains a
`--format` flag when there is finally something to feed.

### 6.6 Explicitly deferred

No workflow YAML, no runner setup, no JUnit converter, no artifact upload wiring. None of
it constrains anything above, and all of it is a day's work once assemblies are obtainable.

## 7. Decisions taken, and what is still open

Settled 2026-09-10:

- **Mod id is `Testing`**, consistent with the ecosystem's bare-PascalCase convention.
  Assembly/namespace stay `Atomcraft.Testing`; identity and version asserted at boot (D6).
- **Published for other modders**, aiming at a standard. Raises docs and a worked example to
  Phase 4 deliverables.
- **API is fully public and explicitly unstable through 0.x** -- everything in section 3 is
  public, breaking changes expected between minor versions, runtime version check and a
  changelog are the price. Freeze is a 1.0 conversation (D6).
- **`Testing.Tests` dogfoods the published layout**: a separate mod zip depending on
  `Testing/Main`, exactly as an external consumer would. It is both the harness's own test
  suite and the copyable example.
- **`../Pressure` is the first external consumer**, via `Pressure.Tests`. The harness tests
  itself first, in Phase 2.
- **Mod state is a first-class concern** (D7). Pressure is expected to be ~9 shorts per
  cell; the field registry is layout-agnostic, so Pressure's storage decision does not
  block harness work.
- **No assertion library, full stop, until a need appears.** Domain assertions throw a
  harness-owned exception and the runner fails on any escaping exception, so adopting one
  later is free (D2).
- **Project is MIT licensed** (D6).
- **Approval snapshots use pluggable renderers and text output**; the canonical format is
  deliberately left open until real tests reveal what they need (D9). Images reserved for T3.
- **A headful tier will exist and is explicitly segregated** (D8), scheduled last.

Still open: nothing blocking. The Harmony-overhead question from the first draft is
retired: the runner pumps from `SceneTree.ProcessFrame` and never patches `Game._Process`.

Deliberately not built, recorded so they are not revisited by default:

- *An assertion library.* The harness core depends on none: domain assertions throw a
  harness-owned exception and the runner fails on any escaping exception, so adopting one
  later costs nothing. Shouldly (MIT) and `xunit.assert` (Apache-2.0) are the candidates if
  a need appears.
- *A T0 helper package.* Plain `dotnet test` covers pure logic, and the one genuinely
  reusable piece, JSON schema validation, belongs in T1 where it cannot drift from the
  game's real types.
- *Cross-run tracking of engine exception counts.* Frame-dependent and noisy; a signature
  appearing or disappearing is meaningful, its magnitude is not.

---

## 8. Phase 0 results (2026-09-10)

Run `./bootstrap.sh` then `./run-game.sh`. Artifacts land in `$TEST_ROOT/out`
(`$TEST_ROOT` defaults to `~/.cache/atomcraft-test`).

### 8.1 What was built

- **`bootstrap.sh`** -- verifies the pinned loader zip by checksum, provisions
  `$TEST_ROOT/install` (hardlinking `AtomCraft.exe`/`AtomCraft.pck`/`steam_api64.dll`,
  copying `data_Atomcraft_windows_x86_64/` since the patcher rewrites `Atomcraft.dll` in
  place), extracts the loader, patches the copy, and records buildid to
  `.bootstrap.json`. Re-runs are a no-op unless the buildid changed or `--force` is given.
- **`run-game.sh`** -- launches the copy through Proton into `$TEST_ROOT/prefix`, with
  `--headful`, `--timeout`, `--cores`, and `--no-loader` flags, and copies `godot.log` out.

### 8.2 Findings worth keeping

1. **`--headless` is fully viable.** No GL context, no window, no Steam overlay. Every
   `_Ready` stage that was a risk (`Prefabs`, `Textures`, `Tilesets`, shader cache) runs
   clean. The `Xvfb` fallback is not needed and the plan's dependency on it is dropped.
2. **The load-order analysis is now empirically confirmed**, closing the open question in
   `../CLAUDE.md` section 10. Observed order:
   `[GodotMonoModLoader]: Loading Game main scene` -> `0Harmony` and
   `GodotMonoModLoader` loaded -> `Modules to load: []` -> `TimeBeginPeriod` ->
   `Game: _Ready - ...` -> `[GodotMonoModLoader]: Game loaded` last.
   Mods really do initialize before the game initializes anything.
3. **The patcher needs a pty.** It ends in `Console.ReadKey()`, which throws
   `InvalidOperationException` under redirected stdin -- *after* successfully patching, so
   naive automation leaves a patched DLL and a non-zero exit code. `bootstrap.sh` runs it
   under `script -qec`, feeds a keystroke, and judges success from the log plus the
   `.backup` file rather than the exit code.
4. **Proton needs an absolute path to the exe.** `proton run AtomCraft.exe` from the
   install directory fails with `Failed to create process ...: 2`.
5. **A fresh Proton prefix capitalises `AppData/Roaming/Godot`**, where the long-lived real
   prefix uses lowercase `godot`. Anything locating `user://` must be case-insensitive.
6. **Boot is ~6.5 s to first `_Process`, ~14 s wall clock per process.** `ProcGenRooms.Init`
   alone is ~3.3 s of that. This confirms D1's premise: one process must run the whole
   suite, and T1 tests needing no session are the right default.
7. **Steam-less boot logs a clear diagnostic and continues.** `SteamClient.Init failed:
   SteamApi_Init failed with FailedGeneric - error: Failed to load module
   'C:\Program Files (x86)\Steam\steamclient64.dll'`. Simulated by pointing
   `STEAM_COMPAT_CLIENT_INSTALL_PATH` at an empty directory, which is also how CI will look.
8. **Harmless noise to expect** on a fresh prefix: `Failed to open file:
   user://DeviceSettings.json` (defaults are used), the seven vanilla
   `Material with name '...' not found` reaction warnings, and
   `WARNING: ObjectDB instances leaked at exit`. The runner should not treat these as
   failures.

### 8.3 Phase 1 findings

1. **`Game.IsHeadlessServer` must not be set.** See section 1.2, which this replaced.
   Enabling it NREs inside `Game._Ready` and then every frame. The plan's original premise
   that it gave a partially supported no-UI mode "for free" was wrong; `--headless` is the
   real mechanism and needs no cooperation from game code.
2. **A Harmony postfix does not run when the original method throws.** Obvious stated
   plainly, but it presents badly: `GetPatchedMethods()` lists the method, the method
   demonstrably runs, and the postfix silently never fires. Two hours of the Phase 1 work
   went into suspecting Godot's `InvokeGodotClassMethod` dispatch and JIT inlining before
   reading far enough down `godot.log` to find the NRE inside the target. **Check the log
   for an exception in the patched method before suspecting Harmony.**
3. **Godot logs unhandled `_Process` exceptions every frame with no backpressure**, which
   turned one bad hook into a **1.3 million line `godot.log` in under 90 seconds**. The
   runner now self-terminates after `ConsecutiveFailureLimit` (5) consecutive pump
   failures. Any per-frame harness code must assume this failure mode exists.
4. **Pump via `SceneTree.ProcessFrame`, not a postfix on `Game._Process`.** It keeps
   Harmony off the game's hottest method and needs no custom `Node` type, whose Godot
   script registration would be an open question for a type that is not compiled into the
   game assembly.
5. **`timeout` does not clean up Proton.** Killing the wrapper leaves the wine process
   running and holding the prefix. `run-game.sh` now follows up with `wineserver -k` scoped
   to `WINEPREFIX`, which only ever touches the test prefix.
6. **The missing-`run_end` check earned its keep on its first real run.** Trusting the
   launcher's exit code alone would have reported the hung, exception-spamming run as a
   pass on the occasions where the launcher exited 0.

### 8.4 Engine exception throttling, verified (2026-09-10)

D10 was implemented at the end of Phase 1 rather than deferred, because the mechanism
stands alone and `--atomtest-headless-server` is a guaranteed reproducer (section 8.3).

Same reproducer, before and after:

| | before | after |
| --- | --- | --- |
| `godot.log` | 1,318,906 lines | **156 lines** |
| outcome | hang, killed at 90 s | **exit 71 in ~9 s** |
| diagnosis available | one NRE stack repeated 16,807 times | **4 distinct signatures, each with one full stack, counts, and rates** |

```
{"event":"engine_exception","sig":"NullReferenceException@HUDInventorySlot._Process<...","count":1802,"elapsedMs":8806,"perSecond":204.6}
{"event":"engine_exception","sig":"NullReferenceException@Client.Process<Game._Process<...","count":122,"perSecond":13.9}
{"event":"engine_exception","sig":"NullReferenceException@Game._Process<...","count":79,"perSecond":9}
{"event":"engine_exception","sig":"NullReferenceException@Gameplay.OnLocalizationChanged<UI.OnLocalizationChanged<SaveData_Device.ApplySettings","count":1,"perSecond":0.1}
{"event":"run_end","status":"exception_storm","exitCode":71}
```

The clean path is unchanged at 124 lines and exit 0, so the throttle costs nothing when
nothing is wrong.

**It improved the finding that motivated it.** The fourth signature, count 1, is the first
failure inside `Game._Ready`: `SaveData_Device.ApplySettings` -> `UI.OnLocalizationChanged`
-> `Gameplay.OnLocalizationChanged`. In the 1.3 million line log that line existed but was
buried under 16,807 copies of the others and was never found by hand. The bug report for the
game developer was revised to name it.

**A defect the verification exposed**, now fixed: every record initially read `frames: 0`,
because the storm began before the pump attached. Frames alone is not a usable denominator,
so records now also carry `elapsedMs` and `perSecond` from a stopwatch started in
`Initialize`, which is available whether or not the pump ever ran.

---

## 9. Install and runner support (2026-09-10)

Added after Phases 0 and 1, when the harness turned out to have quietly assumed one
machine's Steam install. Nothing here is a change of plan; it is the generalisation that
D5 always implied.

### 9.1 The verification matrix

Two game builds and two runners were exercised against each other. This is a genuine
compatibility matrix, not four copies of one thing: the itch.io build is dated 2026-08-25
with a 227 MB pck, the Steam build is 2026-09-10 with 236 MB.

| | `RUNNER=proton` | `RUNNER=wine` |
| --- | --- | --- |
| **Steam build** (2026-09-10) | green, 3 records, exit 0 | green, 3 records, exit 0 |
| **itch.io build** (2026-08-25) | mod loader incompatible | mod loader incompatible |

The matrix is what makes the result interpretable. The Steam build passes under **both**
runners, so the harness is runner independent. The itch build fails under **both**, so its
failure is a property of the game build and not of Wine. Either result alone would have been
ambiguous.

`RUNNER=native` on Windows is implemented but **unverified**: no Windows machine was
available. Its `%APPDATA%` redirection for `user://` isolation is the specific part most
likely to need work, since Godot may resolve the user directory through the shell API rather
than the environment variable.

### 9.2 The mod loader constrains which game builds are testable

The itch.io failure is not ours to fix:

```
HarmonyException: Patching exception in method null
 ---> TypeLoadException: Could not load type 'Atomcraft.SaveData_Universe'
      from assembly 'Atomcraft, Version=1.0.0.0'
   at GodotMonoModLoader.GodotMonoModLoader.Initialize()
```

GodotMonoModLoader 0.2.0 patches the universe save path, and the 2026-08-25 build predates
`SaveData_Universe` entirely, which is consistent with the legacy
`user://Worlds/<name>/modded/world.json` fallback noted in `../CLAUDE.md` section 4. **The
harness can only test game builds the loader itself supports**, and that floor moves with
the loader, not with us. Worth stating plainly in the published documentation, because a
modder on an older build will otherwise read it as a harness bug.

### 9.3 The game's API is not stable across builds

`Simulation.IsSimulationPaused` is absent from the 2026-08-25 build and present in the
2026-09-10 one, so the harness initially failed to **compile** against the older install.
That is too hard a failure for a diagnostic field, and it will recur: concurrent installs
differ today, not only after some future update.

`src/Probe.cs` draws the line:

- **Direct references** for what the harness genuinely depends on (`Simulation.DoSimTick`,
  `SimulateQuadrant`, `Materials`, `SimField`). A breaking change there should be a loud
  compile error, because the harness truly cannot work without them.
- **Reflection probes** for everything else, degrading to null on a build that lacks the
  member.

The build identity recorded in `.bootstrap.json` and in the run header is a **sha256 of
`Atomcraft.dll`**, not a Steam buildid: itch.io and direct installs have no appmanifest, and
the hash is the better identity anyway because it changes exactly when the code under test
changes. The buildid is still recorded when an appmanifest is configured.

### 9.4 Fresh-install bugs

Three bugs were found by running against a second install, and all three shared a shape:
**code that only worked because an earlier run had already established state.**

1. `run-tests.sh` redirected into `$OUT` before `run-game.sh` created it, surviving only
   because the directory already existed from previous runs.
2. `launch_game` lost the `cd "$INSTALL"` that `-s GodotMonoModLoader.gd` depends on.
3. The `WINE` default was set in `require_runner`, which the launch subshell never calls, so
   it executed an empty `argv[0]` and exited 127.

None would have appeared in further runs on the original setup. **A run from an empty
`TEST_ROOT` belongs in the suite**, not as an incidental consequence of testing a second
store, and it is the closest local approximation of a CI runner's empty filesystem.

### 9.5 Failing usefully when the harness never loads

The itch failure first presented as a 240 second timeout with zero records, which is a
terrible way to learn that a mod did not load. Two watchdogs now cover it:

- **Boot**: no `##ATOMTEST##` record within `BOOT_TIMEOUT` (90 s) kills the run. Generic by
  design, covering unpatched DLL, missing mod zip, and loader incompatibility alike without
  pattern matching any of them.
- **Size**: `godot.log` past `LOG_CAP_MB` (64) kills the run, for the engine and native
  errors that never reach Godot's managed exception path.

When the log contains no harness records at all, the wrapper names the likely cause from
three known signatures. Verified on the itch build: **exit 70 in ~90 s with "the mod loader
failed against this game build"**, rather than exit 124 after 300 s with nothing.

This distinction matters more than it looks: "the harness ran and died" is a test failure,
while "the harness never loaded" is an install problem, and they want completely different
responses from whoever is reading the output.

### 9.6 Display isolation, and what Wine can and cannot verify about Windows

**Wine and Proton raise their own GUI, regardless of how the guest program is configured.**
This was learned by leaking `winedbg` crash dialogs onto the user's desktop: the game ran
`--headless` throughout, but the patcher crashes on its `Console.ReadKey` path (section 8.3),
and Wine turned that into message boxes on the real display. Scoping "GUI program" to the
game under test was the mistake; **anything run through Wine needs display isolation.**

Three mechanisms, in `lib/common.sh`:

- `no_display` wraps every headless launch and the patcher with
  `env -u DISPLAY -u WAYLAND_DISPLAY`. A headless run needs no display, so no dialog can
  reach one. This is the mechanism that actually matters.
- `start_xvfb` gives headful runs (T3, D8) a private framebuffer on the first free display
  number, with cleanup trapped on exit.
- `suppress_wine_dialogs` sets `ShowCrashDialog=0` in the prefix registry.

**Do not add `WINEDLLOVERRIDES="mscoree,mshtml="`.** It is the usual recipe for suppressing
Wine's Mono and Gecko install prompts, and it breaks this game: disabling `mscoree` stops
.NET hosting, and the run dies with `Failed to get GodotPlugins initialization function
pointer` before the runtime starts. The prompts are irrelevant here because the game bundles
its own CoreCLR. That signature is now in the wrapper's install diagnosis.

**What Wine settled about the native Windows runner:**

- `APPDATA` is the correct lever. The string appears in the export template and no XDG
  variable does, so Godot reads it as an environment variable rather than through a shell
  API, and setting it in the parent process is the right mechanism.
- `AtomcraftPatcher.exe` has the identical `ReadKey` pathology as the Linux build: patch
  applied, backup written, then **exit 6** with two unhandled `InvalidOperationException`s.
  The fix proposed in the patcher brief applies to both builds.
- The patcher must be pointed at a DLL still inside its full `data_Atomcraft_windows_x86_64/`
  directory; Cecil resolves siblings from the same folder, and an isolated copy fails with
  `AssemblyResolutionException: Failed to resolve assembly: GodotSharp`.

**What Wine cannot settle:**

- Whether a parent-set `APPDATA` survives into the game on real Windows. Wine overrides
  `APPDATA` from the prefix registry before the process sees it, while an arbitrary variable
  propagates fine. That is Wine behavior and nothing on real Windows does it, so the
  mechanism is well founded, but it is not verified and is not described as such.
- The shell layer of `RUNNER=native`: `is_windows` detection, `taskkill`, `winpty`, and path
  translation all need a Windows bash (MSYS2 or Git Bash).
