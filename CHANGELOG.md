# Changelog

The API is public and **deliberately unstable through 0.x**. Breaking changes are expected
between minor versions and are listed first in each entry. Consumers should call
`Harness.RequireVersion("0.2")` from their `Initialize`, so a mismatch is reported clearly
instead of surfacing later as a `MissingMethodException`.

## Unreleased

Driven by a second round of the Pressure mod's needs document, and by testing the harness
against the other mods in the AtomcraftMods repository.

**Breaking:** `Severity` gains a `Lint` member, and `FieldSpec<T>.Clear` is no longer a
required property, so a channel that supplies neither `Clear` nor `Group` is now refused at
registration rather than at compile time.

### Fixed

- **A save and reload never reloaded.** `FileManager` caches the universe it last loaded and
  returns it whenever the world name matches, so re-entering the same world in one process
  handed back the object already in memory, never read the save file, and never ran the mod
  loader's `OnUniverseLoad`. Every assertion about persistence passed on state that had not
  left memory: a full run showed 22 saves and 0 loads. `Session.SaveAndReload` now clears
  that cache and fails if the re-entry did not actually read a file.
- **`FieldSpec<T>` could not supply `ClearEverything`.** The interface declared it; the class
  every mod registers through exposed no way to provide it, so the world-scoped reset was a
  silent no-op for such a channel. New `ClearWorld` property.
- **Registered state now resets when a world ends**, via `Simulation.Reset`. `OnUniverseLoad`
  is not a substitute: it runs only when a universe file is actually read, so starting a
  freshly generated world would otherwise inherit the previous world's state.
- `FieldRegistry` gains the `Unregister` the other two registries always had.

### Fixed

- **A run that selected no tests reported success.** A filter matching nothing ran zero tests
  and exited 0, which is indistinguishable from everything passing. Selecting no tests, or a
  filter that will not compile, now fails the run with a message naming the filter and the
  matching rule. Reported by the Pressure mod.

- **`--atomtest-exclude`**, the same kind of pattern as the filter, applied after it and
  winning over it for a test both match. Expressing "this class except that test" with the
  filter alone needs a negative lookahead.

### Changed

- **`--atomtest-filter` is a regular expression**, case-insensitive and unanchored, rather
  than a substring. Unanchored makes it a superset: a plain word selects every name containing
  it exactly as before, so existing filters are unaffected, while `Session|Artifact` now does
  what it looks like it does.
- **A passing run could report as a harness crash.** `run-tests.sh` extracted results by
  grepping `godot.log` without `-a`. The game writes bytes that make GNU grep classify that
  log as binary, and a binary-mode grep writes nothing into a redirect while still exiting 0,
  so `results.jsonl` came out empty and the missing `run_end` was read as the harness dying. A
  run that passed 108 tests reported exit 70. Every grep of the log now passes `-a`.

### Added

- **An artifacts directory.** `Artifacts.Write`, `WriteBytes`, and `Path` give a test one
  blessed place for output, filed under the running test. Emptied at the start of every run,
  announced per file in the results stream, and copied to `$TEST_ROOT/out/artifacts` beside
  the log and the records.
- **`ChannelContract.Check(region)`**, which exercises a registered channel rather than
  reading its declaration: write a probe, read it back, clear the rectangle, then clear the
  world. Asked for by the Pressure mod, and it is the check that would have caught
  `ClearEverything` being a silent no-op. Scoped to the caller's own channels by default.
- **Validation**, nine generic rules for mistakes that produce no error at load and no crash.
  `Validation.Check(modId)` fails on errors, logs warnings, and stays silent about lint
  unless asked. Found the cause of a boot crash in another mod on first use.
- **`StateRegistry`**, for mod state that no rectangle describes: a config flag, an inventory,
  a running total. `ResetModState` covers it, and it can opt into determinism comparisons.
- **`TickSpec.When`**, choosing whether a per-tick pass runs before or after the quadrant
  passes. Before is where `FlagActiveChunks` sits in the real game, which is how a mod's own
  movement takes precedence over gravity.
- **`FieldGroup`**, so channels that are views of one store clear once rather than once each,
  and a read-only view no longer has to nominate a `Clear` for storage it does not own.
- **`FieldSpec<T>.Checksum`**, an optional bulk hook so a flat-array mod is not charged a
  per-cell delegate call per channel for determinism.
- `Region.ConveyInto`, the only way to reach a material's `OnImpact`.
- `Session.Enter(..., worldName:)` and `Session.UniverseLoads`, for tests that switch worlds
  or need to prove a load happened.

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
