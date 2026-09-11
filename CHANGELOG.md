# Changelog

The API is public and **deliberately unstable through 0.x**. Breaking changes are expected
between minor versions and are listed first in each entry. Consumers should call
`Harness.RequireVersion("0.1")` from their `Initialize`, so a mismatch is reported clearly
instead of surfacing later as a `MissingMethodException`.

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
