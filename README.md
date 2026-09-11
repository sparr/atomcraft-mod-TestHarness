# Atomcraft TestHarness

Automated tests for Atomcraft mods, run inside the real game.

A test takes a private rectangle of the world, paints pixels into it, steps the simulation
by hand, and asserts on the result. No session, no saved world, no clicking. The whole
suite runs headless in a few seconds.

```csharp
[GameTest]
public static void SandFallsToTheFloor(Region r)
{
    r.Fill(0, 11, r.Width, 1, "Granite");
    r.Set(4, 0, "Sand");

    r.TicksUntil(() => r.At(4, 10) == "Sand", 200, "sand comes to rest on the floor");
    r.AssertCount("Sand", 1);
}
```

## Why it can do this

`Materials`, `Reactions`, and `Craftables` are all live once `Game._Ready` finishes, and the
world's 6144x6144 field is allocated as air from static init. So pixel behavior can be
tested without loading a world at all. Tests run 10 to 300 ms each.

## Setup

You need the game installed, and either Proton or Wine (or Windows).

```sh
cp atomcraft-test.conf.example atomcraft-test.conf   # then edit it
./bootstrap.sh --detect                              # suggests values for the config
./bootstrap.sh                                       # provision a patched copy of the game
```

`bootstrap.sh` copies the game to `$TEST_ROOT` (default `~/.cache/atomcraft-test`) and
patches **that copy** with GodotMonoModLoader. Your real install is never touched, and
tests run against a throwaway prefix so your saves and blueprints are untouchable.

Re-run `./bootstrap.sh` after a game update.

## Running

```sh
./run-tests.sh                          # everything
./run-tests.sh -- --atomtest-filter=Blood   # matching tests only
./run-tests.sh --determinism            # same scene twice, and across core counts
./run-tests.sh --cores 2                # pretend to be a small CI runner
```

Exit code is the suite's: 0 clean, 1 a test failed, 70 the harness crashed, 71 an engine
exception storm, 72 the engine logged exceptions, 124 timed out.

Results land in `$TEST_ROOT/out/`: `results.jsonl` one record per test, plus a copy of
`godot.log`.

## Testing your own mod

Tests ship as a **separate mod** that depends on yours and on the harness. They are not a
module of your mod: the loader treats a missing dependency as an error, so a test module
inside your zip would show a red entry in the mod loader report for every player who does
not have the harness installed.

```
MyMod/            mod.json id "MyMod",      module "MyMod/Main"
MyMod.Test/       mod.json id "MyMod.Test",  module "MyMod.Test/Main"
                  dependencies: ["MyMod/Main", "TestHarness/Main"]
```

The one hard constraint is that **your mod's project must not compile the test project's
files**. C# projects glob `**/*.cs` by default, so a test project inside your project's
directory gets swept in and the build fails with `CS0579` duplicate assembly attributes.
Any of these satisfies it:

- `MyMod/` and `MyMod.Test/` as sibling directories (what the Centrifuges tests do)
- `MyMod/src/` and `MyMod/test/` as siblings, with the csproj in each
- keep the tests nested and exclude them: `<Compile Remove="Test/**" />` in your csproj

Pick whichever fits your repo. A single-mod repo often reads better with `src` and `test`;
a repo holding several mods usually wants siblings, because a test project is a peer mod
with its own id, zip, and lifecycle.

Check the harness version from your `Initialize`, since the loader's dependencies carry no
version constraint and the 0.x API changes between minor versions:

```csharp
public static void Initialize() => Harness.RequireVersion("0.1");
```

Reference the harness assembly, and ship nothing but your own DLL and `mod.json`:

```xml
<Reference Include="Atomcraft.TestHarness">
    <HintPath>$(TestHarnessDir)\Atomcraft.TestHarness.dll</HintPath>
    <Private>false</Private>
</Reference>
```

`TestHarness.zip` from a [release](https://github.com/sparr/atomcraft-mod-TestHarness/releases)
serves both purposes: drop it in the harness's `Mods/` to install, and extract it to build
against it. It contains a single `TestHarness/` folder, so `TestHarnessDir` points inside:

```sh
unzip TestHarness.zip -d ~/atomcraft-testharness
dotnet build MyMod.Test.csproj \
    -p:GameInstallDir=<Atomcraft install> \
    -p:TestHarnessDir=~/atomcraft-testharness/TestHarness
```

`build-mod.sh` sets both for you if you are building from a checkout of the harness.

Then build and run:

```sh
./build-mod.sh /path/to/MyMod/MyMod.csproj --install
./build-mod.sh /path/to/MyMod.Test/MyMod.Test.csproj --install
./run-tests.sh
```

`build-mod.sh` supplies the game's reference assemblies, a neutral `AppData` so a
Windows-style install target does not misfire on Linux, and `TestHarnessDir`. You do not
have to change your mod's build to make it testable.

## Writing tests

A test is a static method with `[GameTest]`, taking nothing or a single `Region`.

```csharp
[GameTest(Wall = "Granite", ChunksWide = 2, Mode = WorldMode.Survival, StartTick = 4)]
public static void Example(Region r) { ... }
```

| Attribute | Meaning |
| --- | --- |
| `ChunksWide` / `ChunksTall` | Region size in 64x64 chunks, the engine's own unit. Default 1. |
| `Wall` | Seal the region with this material, so liquids and gases stay put. Use a static material. |
| `Band` | `Deep` (default), `Underground`, or `Surface`. See the trade-off below. |
| `StartTick` | Absolute starting tick. Machines commonly gate on tick parity. |
| `Mode` | `WorldMode` for this test. |
| `Skip` | Skip with a stated reason rather than deleting or commenting out. |

Region basics:

```csharp
r.Set(x, y, "Water", kelvin: 400);     // material and heat together
r.Fill(x, y, w, h, "Granite");
r.Paint(art, legend);                  // ASCII layout
r.At(x, y)                             // material name, or null for air
r.Count("Steam"); r.HeatAt(x, y);
r.Ticks(10);
r.TicksUntil(() => r.Count("Steam") > 0, 500, "water boils");
r.Dump();                              // glyph grid, for failure messages
r.PinnedHeat = 400;                    // hold a temperature against decay
```

Assertions throw `AssertionException`; anything else escaping a test also fails it. There is
no assertion library to install.

### Things that will bite you

**Prefer `TicksUntil` to a tick count.** It states what the test expects, stops as soon as
it happens, and says how long it waited when it does not. Guessed counts hide sloppy
thinking behind generous margins.

**A spawned pixel is not a heat source.** `Set(..., kelvin)` sets that cell. Heating a
region afterwards walks the material through every temperature band on the way up, where it
can react.

**Heat decays toward ambient**, which is about 1573 K at the default `Deep` band and 295 K
at `Underground`. `Clear` leaves a flat 290 K either way, so long runs drift. Use
`PinnedHeat` or `FillAmbientHeat`.

**`Deep` is the default for a reason**: above the workshop line the game condenses noble
gases out of air roughly once per 24 ticks in a chunk-sized region, which appears as pixels
you never placed. The price is that ambient is far from what `Clear` leaves.

**Material names, never ids.** Ids shift with whatever else is installed.

**Static pixels stay put; solids, liquids, and gases all move.** "Hard" is not "fixed".

## Tests that need a world

Most tests do not. Pixel behavior runs against the live field with no world loaded, which
is why it costs milliseconds. A session is for the things that only exist inside one:
saving, loading, inventories, and anything reading `Game.SaveData_World`.

The game only makes progress between frames, so a test that needs one returns `IEnumerator`
and yields:

```csharp
[GameTest]
public static IEnumerator MyStructureSurvivesAReload()
{
    yield return Session.Enter("flat");          // a synthetic world, not real worldgen

    Session.SetPixel(3000, 4990, "Iron");        // writes and marks the segment dirty
    Session.WorldTicks(60);                      // the real simulation, parallel passes and all

    yield return Session.SaveAndReload();        // save, leave, load it back

    if (Simulation.CurrentState.Field.Get(3000, 4990).ToMaterialName() != "Iron")
        throw new AssertionException("the pixel did not survive the round trip");

    yield return Session.Leave();
}
```

| Yield | Meaning |
| --- | --- |
| `Wait.NextFrame` | resume next frame |
| `Wait.Frames(n)` | resume after n frames |
| `Wait.Until(cond, "what")` | resume when true; the description appears if it never is |
| another `IEnumerator` | run it to completion first |

Fixture worlds are flat and empty rather than procedurally generated: your structures, and
nothing else that could explain a result. `Session.Enter` deletes any world a previous run
left, so a fixture is the same every time.

`Enter` also **pauses the simulation**. The world would otherwise evolve in real time
between your writes and your assertions, by an amount that depends on how long a frame took.
`WorldTicks` is how time passes.

Saving writes every segment by default. The incremental path only covers changes the game
itself made, so a test that writes into the field directly and saves incrementally saves
nothing and reads back a world that was never written.

### Comparing worlds

```csharp
Session.ChunkChecksums(x, y, w, h)          // material and heat, the game's own
Session.ChunkMaterialChecksums(x, y, w, h)  // material only
```

Use the first to check determinism: heat makes it more sensitive, and heat is where
order-dependence surfaces first. Use the second across a save and reload, because **heat is
not persisted**. A saved segment holds material ids only, and loading recomputes every
temperature from the material's default or the planet's ambient at that depth. A cell
written at 800 K comes back at whatever ambient is there.

`./run-tests.sh --determinism` runs the suite at two core counts and compares the recorded
checksums.

## Mod state

If your mod keeps per-cell data, register it and the harness can assert over it without
knowing how you store it. A dictionary, a per-chunk map, and a flat array all work.

```csharp
FieldRegistry.Register(new FieldSpec<int>
{
    Name  = "mymod.pressure",
    Unset = 0,
    Read  = (x, y) => Store.TryGetValue((x, y), out var v) ? v : 0,
    Write = (x, y, v) => Store[(x, y)] = v,
    Clear = (x, y, w, h) => { /* required: per-test isolation depends on it */ },
});
```

```csharp
var pressure = r.Field<int>("mymod.pressure");
pressure[3, 4] = 120;
pressure.AssertUniform(0);
pressure.CountSet();
```

The game's own `core.material`, `core.heat`, and `core.charge` register through the same
call, so there is no privileged path. (`core.charge` is registered for completeness but the
game never touches it: power is a single global float, and circuit signals are encoded in
the material id, so a material checksum already captures all circuit state.)

### Does your state survive a save?

Persisting per-cell data means implementing the loader's `OnUniverseSave` and
`OnUniverseLoad`, whose failure mode is quiet: the mod keeps working, the world keeps
loading, and the state is simply gone or subtly wrong.

```csharp
[GameTest]
public static IEnumerator PressureSurvivesAReload()
{
    yield return Session.Enter("flat");

    var pressure = FieldRegistry.Get<int>("mymod.pressure");
    for (var i = 0; i < 10; i++)
        pressure.Put(2800 + i, 4980, 100 + i);

    yield return Persistence.AssertFieldSurvivesSaveLoad<int>("mymod.pressure", 2800, 4980, 12, 1);

    yield return Session.Leave();
}
```

It refuses a rectangle with nothing in it, because a test asserting that empty state
survives passes whether or not persistence works at all. It also catches the unstable-id
hazard for free: modded material ids are assigned by load order, so anything persisted as a
raw id comes back as a different material once the installed mod set changes.

`TestHarness.Test`, the harness's own test mod, persists an example channel the same way, if
you want a worked example of the save hooks. It is also built exactly as a consumer builds
theirs: a separate mod with its own id and zip, a dependency on `TestHarness/Main`, a version
check in `Initialize`, and the harness resolved through `TestHarnessDir` rather than a
project reference. If that path breaks, it breaks there first.

## Diagnostics

`./run-tests.sh -- --atomtest-diagnose` reports facts about the running game rather than
testing a mod: how far the two material id spaces disagree, and the ambient temperature
profile by depth. Re-run it after a game update rather than trusting numbers in docs.
