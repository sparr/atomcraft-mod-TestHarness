# Changelog

The API is public and **deliberately unstable through 0.x**. Breaking changes are expected
between minor versions and are listed first in each entry. Consumers should call
`Harness.RequireVersion("0.2")` from their `Initialize`, so a mismatch is reported clearly
instead of surfacing later as a `MissingMethodException`.

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
