# Changelog

The API is public and **deliberately unstable through 0.x**. Breaking changes are expected
between minor versions and are listed first in each entry. Consumers should call
`Harness.RequireVersion("0.3")` from their `Initialize`, so a mismatch is reported clearly
instead of surfacing later as a `MissingMethodException`.

## Unreleased

`main` reports `0.4.0-dev`, so a mod pinned to `"0.3"` is refused here and should stay on the
v0.3.0 release until there is something to move for.

First-class headful GUI testing, from the needs writeup in `HEADFUL-GUI-TESTING.md`. A test
that asserts on live UI no longer reaches into game internals for any of it:

### Added

- **`[GameTest(RequiresDisplay = true)]`.** The runner ends such a test with no verdict under
  a headless display server, before its body runs, so the ordinary suite stays green and a
  `--headful` run is where it counts. Tests no longer sniff `DisplayServer` themselves.
- **`View.LookAt(tile)`** aims the view at a tile and holds it there for the rest of the
  test. The game's view is built entirely around the avatar (the camera is leashed to within
  200 units of it, and the render and fog windows are computed from its tile), so the hold
  anchors the avatar just above the target every frame with its momentum zeroed, re-closes
  any window that opens, and clears the tile of fog of war first. `View.Release()` undoes it
  early; ending the test undoes it always.
- **`View.RevealFog(tile, radius)`.** Fog of war turns out to be fog material in the
  simulation field, so revealing a spot is a world edit: fog pixels become air, with enough
  margin that the blurred fog mask cannot reach back over the asked-for cell.
- **`Cursor.Hover(tile)`** warps the mouse to a tile through the current camera and completes
  only once `Utils.GetTileMousePosition()` has agreed for a few consecutive frames, so a test
  places one pixel and trusts the game is reading it, instead of a block sized to absorb
  drift.
- **`Hud.HoverBoxVisible` / `Hud.HoverBoxText()`** read the hover box the player sees. The
  reflection into the game's private label lives here alone, and
  `Hud.HoverBoxLabelFieldExists` lets the headless suite catch the game renaming it.
- **`Session.CloseAllWindows()`** closes whatever UI window a fresh session opened, one-shot;
  `View.LookAt` is the held version.

### Fixed

- **`Session.Enter` drains the game's stale audio buffers itself.** Pixel-movement audio
  counts accumulated during sessionless region tests reached the first session frame before
  `Game.LocalAvatar` existed and crashed `Audio.Process`; every session-entering test carried
  the same reflective drain as a workaround, and none has to now.

## 0.3.0

Driven by a second round of the Pressure mod's needs document, and by testing the harness
against the other mods in the AtomcraftMods repository. Several entries below are bugs in the
harness that made a test report success without testing anything; those are the ones worth
reading first.

**Breaking:** `Severity` gains a `Lint` member. `FieldSpec<T>.Clear` is no longer a required
property, so a channel supplying neither `Clear` nor `Group` is refused at registration rather
than at compile time, and one supplying both is refused as well. `--atomtest-filter` is now a
regular expression rather than a substring, which is a superset in practice since matching is
unanchored.

### Fixed

- **A save and reload never reloaded, for anyone.** `FileManager` caches the universe it last
  loaded and returns it whenever the world name matches, so re-entering the same world in one
  process handed back the object already in memory, never read the save file, and never ran the
  mod loader's `OnUniverseLoad`. Every assertion about persistence passed on state that had not
  left memory: a full run showed 22 saves and 0 loads. `Session.SaveAndReload` now clears the
  cache and fails if the re-entry did not read a file.
- **`Persistence.AssertFieldIsReplacedOnLoad` had the same defect**, open-coding its own leave
  and re-entry. For a mod that clears state when a world ends it reported a false *failure*
  reading as a broken save hook. The reload half is now `Session.Reload()`, so one place knows
  about the cache.
- **A run that selected no tests reported success.** A filter matching nothing ran zero tests
  and exited 0, which is indistinguishable from everything passing. Selecting nothing now fails
  the run, naming the filter, the exclude, or the empty discovery as the cause.
- **A passing run could report as a harness crash.** `run-tests.sh` extracted results by
  grepping `godot.log` without `-a`. The game writes bytes that make GNU grep classify the log
  as binary, and a binary-mode grep writes nothing into a redirect while still exiting 0, so
  `results.jsonl` came out empty and the missing `run_end` was read as the harness dying. A run
  that passed 108 tests reported exit 70.
- **A mod refused for a version mismatch still had its tests run.** The loader loads a mod's
  assembly before calling `Initialize`, and discovery scans loaded assemblies, so a refused
  mod's tests executed against a harness its `Initialize` never registered anything with. They
  are now failed, not skipped: a skip leaves the run green.
- **`FieldSpec<T>` could not supply `ClearEverything`.** The interface declared it; the class
  every mod registers through exposed no delegate for it, so the world-scoped reset was a
  silent no-op for every channel registered that way. New `ClearWorld` property.
- **`build-mod.sh` published the harness assembly only when the project was named exactly
  `src/TestHarness.csproj`.** Any other spelling installed a new harness while leaving
  consumers compiling against the old assembly. `TestRoot` is now passed unconditionally.
- **Registered state now resets when a world ends**, via `Simulation.Reset`. `OnUniverseLoad`
  is not a substitute: it runs only when a universe file is actually read, so starting a freshly
  generated world would otherwise inherit the previous world's state.
- **A harness test asserted an RNG tie-break.** `SolidsSlideDownASlope` dropped a grain on a
  peak with open air on both sides, where the direction is a coin flip the game decides with a
  deterministic roll, and asserted the way vanilla breaks it. A mod that changes the rolls
  failed it, correctly. The peak now backs against a wall, so there is one way down and the
  assertion is about slopes. Reported by an RNG-modifying mod.
- `FieldRegistry` gains the `Unregister` the other two registries always had.

- **`Harness.Version` is read at runtime rather than inlined.** It was a `const`, so a mod
  compiled against one harness and run against another reported the version it was built with,
  which is the confusion `RequireVersion` exists to prevent.
- **Generating a world no longer writes it to disk.** Creating one serialized all 1536 planet
  segments and flushed them, about two and a half seconds every fresh session entry, by a path
  that reached `SavePlanet` without passing the existing suppression. A fresh entry also
  inherited the previous world's segments, since deleting the files does not touch the universe
  `FileManager` caches in a static.
- **The first save of a generated world is always complete.** With creation no longer writing
  the segments, an incremental first save wrote only what the test touched, and the reload
  returned those segments with air everywhere else. The assertion that would notice is the one
  that passes, since the test's own pixels are exactly what was saved. Asking for `incremental`
  on a world that has never been saved is overridden, and logged.
- **Unasked saves are refused at `WriteUniverseToDisk` as well as the higher entry points**, so
  a path nobody enumerated still stops, and a refusal names the calling mod rather than leaving
  it to wonder why nothing was written. `Session.AllowSaves` is the way through for a test that
  calls the game's save API directly.

### Changed

- **`--atomtest-filter` is a case-insensitive, unanchored regular expression** over the full
  test name. Unanchored makes it a superset of the substring matching it replaces: a plain word
  still selects every name containing it.
- **The version constant carries the next version between releases**, so a mod pinned to the
  last release is refused at load rather than failing later with a missing method. `main`
  reports `0.3.0-dev`.

### Added

- **`Harness.Inapplicable(reason)`**, for a test that looked and found nothing to judge. Its
  own status, counted separately and shown in the summary line, and neither a pass nor a
  failure. A run in which nothing was judged exits nonzero, for the same reason a filter
  matching no tests does.
- **Benchmarks.** `[GameBenchmark]` is excluded from an ordinary run and appears under
  `--atomtest-bench`; `Bench.Measure` and `Bench.Compare` discard a warmup pass and report
  ns/op as `benchmark` records. They assert nothing, because a timing threshold on a shared
  desktop is a flaky test.
- **`Original.Bind`**, giving a mod a callable copy of a method as it was before anyone patched
  it, so "what would this have returned" needs no unpatching. Verified against `RNG.Roll` with a
  patch active, since a hot inlined method is where the technique would fail if it were going
  to.
- **`Log.For(modId)`**, a log bound to your mod's name, so a line in `godot.log` can be
  attributed when several mods are in a run.
- **`bootstrap.sh --seed-from <root>`**, filling a private test root from an already-patched
  one instead of provisioning 441 MB again. Hardlinked where the filesystem allows, copied
  otherwise, and it says which. Mod zips are not carried over.
- **Validation**, nine generic rules for mistakes that produce no error at load and no crash:
  dangling material names in material fields and reactions, a manifest data path matching
  nothing in the zip, an unregistered `ColorDelegate`, a missing translation, a craftable in no
  category, and two fields that are unusable because of game bugs. Errors fail, warnings log,
  lint is silent unless asked for. Found the cause of a boot crash in another mod on first use.
- **`ChannelContract.Check(region)`**, which exercises a registered channel rather than reading
  its declaration: write a probe, read it back, clear the rectangle, then clear the world.
  Scoped to the caller's own channels. This is the check that would have caught the
  `ClearEverything` gap above.
- **`StateRegistry`**, for mod state that no rectangle describes: a config flag, an inventory, a
  running total. `ResetModState` covers it, and it can opt into determinism comparisons.
- **An artifacts directory.** `Artifacts.Write`, `WriteBytes`, and `Path` give a test one
  blessed place for output, filed under the running test, emptied when a run begins, announced
  per file in the results, and copied to `$TEST_ROOT/out/artifacts`.
- **`FieldGroup`**, so channels that are views of one store clear once rather than once each,
  and a read-only view no longer nominates a `Clear` for storage it does not own.
- **`FieldSpec<T>.Checksum`**, an optional bulk hook so a flat-array mod is not charged a
  per-cell delegate call per channel for determinism.
- **`TickSpec.When`**, choosing whether a per-tick pass runs before or after the quadrant
  passes. Before is where `FlagActiveChunks` sits in the real game, which is how a mod's own
  movement takes precedence over gravity.
- **`SimFeature` and `SimFeatures.Disable`**, switching off movement, heat conductance, or the
  pull toward ambient temperature, per test through `[GameTest(Disable = ...)]` or per scope.
  Movement patches only the tail of a material's `Step`, so reactions and the rest still run,
  and a material overriding `StepSolid` or `StepLiquid` is unaffected.
- **`--atomtest-exclude`**, the same kind of pattern as the filter, applied after it and winning
  over it for a test both match.
- `Region.ConveyInto`, the only way to reach a material's `OnImpact`.
- `Session.Reload`, `Session.UniverseLoads`, and `Session.Enter(..., worldName:)`, for tests
  that switch worlds or need to prove a load happened.

## 0.2.1

Tooling and documentation only. The harness API is unchanged, so `RequireVersion("0.2")`
is satisfied by this release as well.

- `build-mod.sh` can build the harness itself, which it previously refused to do: it failed
  its own precondition, because on a fresh checkout nothing has produced the assembly it
  insists on finding. That left `run-tests.sh` as the only way to install a harness, so
  testing one mod meant also running the harness's own suite.
- The README leads with what a release actually contains. Both the source archive and
  `TestHarness.zip` are usable on their own, for different purposes, and neither requires
  the other.

## 0.2.0

Additions driven by a second mod testing itself against the harness. Everything below was a
workaround in someone's test project first.

**Breaking:** none to existing call sites, but `RequireVersion` checks major and minor, so a
mod built against 0.1 must update its check to `"0.2"`.

- `TickRegistry`, so a mod's per-tick pass is driven by region tests. `Region.Ticks` calls
  `SimulateQuadrant` directly and never reaches `Simulation.Step`, the game's only hook for
  per-tick work that is not per-material, so such a mod was not driven at all and its tests
  failed for unrelated reasons. Passes are given the region's rectangle.
- Region slices are released when a test finishes. The allocator only ever advanced, so a
  band held about forty-eight tests for the lifetime of the process and crossing that line
  failed whichever tests sorted last, with a message about geometry rather than behavior.
- Registered channels take part in determinism, via `IFieldSpec.ChecksumRect` and
  `FieldRegistry.ChecksumAll`. The game's own checksum covers material and heat, so a mod's
  per-cell state, which has none of the engine's partitioning guard rails, was invisible to
  the mode that exists to catch order-dependence.
- `IFieldSpec.ClearEverything`, for a channel holding state outside the region a test is
  handed, such as a running total or an index. Defaults to a no-op;
  `[GameTest(ResetModState = false)]` opts out.
- `Persistence.AssertFieldIsReplacedOnLoad`, for a load hook that restores saved state on top
  of what was already there. Every save round trip passes; the bug appears when a player
  leaves one world for another.
- `Region.SetAir`, `Region.FillAir`, `FieldView.AssertNoneSet`, and `TickRegistry.Unregister`.
- `Region.SetRaw` bypassing a mod's write hooks is now documented as intended rather than
  left as an accident of implementation.

## 0.1.0

First release. Runs tests inside the game, headless, against a patched copy of the install.

- Region tests: a private rectangle of the world, painted and stepped by hand, with no world
  loaded.
- Session tests: synthetic fixture worlds, the real simulation, and save/reload round trips.
- A per-cell field registry, so a mod's own state can be asserted over and round tripped
  without the harness knowing how it is stored.
- Determinism checks, including across core counts.
- Engine exception throttling and attribution.
- Game diagnostics: material id space divergence, ambient temperature by depth.

Verified against Atomcraft Steam buildid 25221481.
