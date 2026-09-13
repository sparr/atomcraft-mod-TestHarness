# Notes toward first-class headful GUI testing

> **Status:** implemented in 0.4.0-dev, in the shape this document asked for: `RequiresDisplay`
> on `[GameTest]`, `View.LookAt` (camera hold, windows held closed, fog lifted),
> `View.RevealFog`, `Cursor.Hover`, `Hud.HoverBoxText`/`HoverBoxVisible`, and the audio drain
> folded into `Session.Enter`. The one deliberate deviation: no `Session.EnterCleanView` entry
> mode; the pieces compose (`Session.Enter`, then `View.LookAt`, then `Cursor.Hover`) so a
> failure names the guarantee that broke. Two findings revised this document's model of the
> game. Fog of war is fog *material* in the field, so revealing it is an ordinary world edit,
> not ambient state to switch off. And the view cannot be decoupled from the avatar after
> all: the camera is leashed to within 200 world units of it every frame, and the render and
> fog windows are computed from its tile, so `View.LookAt` holds the view by anchoring the
> avatar itself. (The original test's spaceship pin never actually held; it worked because
> the avatar was still falling near the spaceship, and the sampled hover tile followed the
> real, leashed camera.) The rest of the document is preserved as the reasoning behind those
> calls.

Written by the author of the Pressure mod after getting one GUI assertion to run under the
harness: that a compressed pixel's density shows in the game's hover box
(`HoveredMaterialHint`). The test works today, but only because it reaches around the harness
into game internals and does a lot by hand. This is a writeup of what such a test actually
needs, what I had to do to get it, and where the harness could offer an affordance so the
next GUI test does not re-derive all of this. It is deliberately about the *shape* of the
problem, not a spec; translate it into whatever the harness chooses to support.

The working example lives in the Pressure mod at `test/HoverBoxGuiTest.cs`. Refer to it for
the concrete calls; this document explains the *why* behind each one.

## What a "headful GUI test" is, and why headless cannot do it

Most of a feature can be tested headless: pure logic in region tests, translation lookups by
poking `TranslationServer` directly. What headless cannot show is whether a piece of UI
*actually appears on screen* given the game's own gating. The hover box, for instance,
refuses to draw when a window is open, when the cursor is over HUD, over fog, or over a
hovered control, and it rebuilds its text every frame from the tile under the mouse. The
only way to know the feature survives all of that is to run the real game with a display,
put the world into the exact state the player would be in, and read what the box renders.

So a headful GUI test is: **launch with a display, drive the running game into a specific
on-screen state, let real frames render, and assert on live UI.** Every word of that is a
thing the harness currently makes hard.

## What already works (and is genuinely good)

- **Headful launch.** `run-game.sh --headful` already brings up a private Xvfb, mutes audio,
  and runs the patched game with a real framebuffer. This is the single biggest thing and it
  Just Worked. I only had to add a `--headful` passthrough to `run-tests.sh` so the suite (not
  just a bare launch) can run with a display. Consider making that passthrough official.

- **Frame stepping.** `Wait.Frames(n)` / `Wait.NextFrame` from an `IEnumerator` test advance
  *render* frames, which is what UI needs (the box updates in `_Process`, not on sim ticks).
  This is exactly right and I leaned on it heavily. Keep it; a GUI test cannot use
  `WorldTicks`, because those do not render.

- **Sessions.** `Session.Enter`/`Leave` put a real world up. Fine as-is.

- **Abstention.** `Harness.Inapplicable(reason)` let the one GUI test skip cleanly under the
  ordinary headless suite instead of failing it. Essential — see below.

## The hard parts, and the workarounds I used

Each of these is a place the test currently reaches into game internals. They are the
candidates for a harness affordance.

### 1. Knowing whether there is a display

The test must run only when there is a screen, and abstain otherwise, so the normal headless
suite stays green. I detect this with `DisplayServer.GetName() == "headless"` and call
`Harness.Inapplicable(...)`. This works but every GUI test will copy it.

*Affordance:* a first-class check — `Harness.RequireDisplay()` (abstains when headless), or a
`[GameTest(RequiresDisplay = true)]` flag the runner honors by skipping when the launch was
headless. The runner already knows whether it started Xvfb; the test should not have to sniff
`DisplayServer`.

### 2. A fresh session is not a clean gameplay view

On `Session.Enter`, a UI window is up (a welcome/hub window; `Gameplay.Windows` has a visible
entry). While any window is visible, `Gameplay.WindowIsOpen` is true, which both hides the
hover box *and* pauses the simulation. I close them by iterating `Gameplay.Windows` and
setting `.Visible = false` — and I have to redo it every frame in case something reopens one.

*Affordance:* a "clean gameplay view" entry mode, or `Session.DismissWindows()` /
`Gameplay.CloseAllWindows()`, so a test that wants the plain playfield gets it without knowing
the window list. More generally: a documented notion of "the game is now in bare gameplay,
nothing modal, not paused."

### 3. The camera will not hold still on its own

This was the worst part. `Client.FollowCam` lerps toward the avatar at ~0.03/frame. The
avatar spawns high in the sky and falls a long way to the ground, so for many hundreds of
frames the view is panning and the tile under a fixed cursor keeps moving. Worse, the sky is
unexplored **fog**, and the box refuses to draw over fog, so even a "stable" view up there is
useless.

What finally worked: ignore the avatar entirely (the hover tile is cursor-through-camera, not
avatar-relative), and each frame force `Client.FollowCam.GlobalPosition` to an *explored*
anchor. The only reliably-explored, stationary anchor at session start is the spaceship
(`Game.World.Spaceship.GlobalPosition`). Pinning the camera there, every frame, overrides the
lerp and gives a fixed, fog-free view.

*Affordance:* view control. Something like `View.LookAt(worldTile)` that pins the camera on a
cell and holds it (disabling follow, overriding the lerp), plus a guarantee that the cell is
out of fog. Bundling "aim the view at (x,y) and keep it there and make sure I can see it" into
one call would remove the single biggest source of fragility. A test should never need to
know about `FollowCam`, its lerp rate, or where the avatar spawns.

### 4. Fog hides everything, and there is no test-side way to lift it

The box will not draw over fog-of-war (`Gameplay.IsFogOfWar(tile)`), and I found no clean way
to reveal a region. My workaround was indirect: aim at the spaceship, which the game reveals
at start. If a test wants to inspect a pixel *anywhere else*, it is stuck.

*Affordance:* `View.RevealFog(rect)` or a session option to disable fog entirely for the test.
Fog is exactly the kind of ambient game state a test should be able to switch off.

### 5. Placing a pixel under the cursor, against residual drift

I want a known pixel under the cursor. I warp the OS mouse to window centre
(`Input.WarpMouse(center)`, `center = DisplayServer.WindowGetSize()/2`) and read the tile with
`Utils.GetTileMousePosition()` (which is `Game.World.GetGlobalMousePosition().GlobalToTilepos()`).
Even with the camera pinned, there is a pixel or two of residual drift between the frame I
sample the tile and the frames I read the box. To be robust I place a **block** of identical
compressed pixels (17x17) centred on the sampled tile, larger than any drift, so whichever
cell the cursor lands on while the box is read is one of mine. That is a hack around not being
able to guarantee "cursor is exactly over tile T for the next N frames."

*Affordance:* `Cursor.Hover(worldTile)` that maps a tile to a screen point through the current
(pinned) camera, warps the mouse, and waits until `GetTileMousePosition()` actually equals the
target for a couple of frames — returning only once the cursor is genuinely, stably over the
cell. With that, a test places one pixel and hovers it, no block, no drift math.

### 6. Reading the live UI requires reflection

The box's label is a private field (`HoveredMaterialHint.Label`), so I read its `.Text` via
reflection off `Gameplay.Instance.HoveredMaterialHint`, and check `.Visible` to confirm the
box is actually shown. Reflection into private game fields is exactly the kind of brittle
coupling the harness exists to spare tests.

*Affordance:* a small set of "read what the player sees" helpers. At minimum a way to get the
hover box's current text and visibility. More ambitiously, a generic "text under a named UI
node" reader, so tests assert on rendered strings without knowing field layouts. This also
pairs well with the translation story: a test could assert the box shows the *localized*
string in a chosen locale.

### 7. The audio crash on session entry

Unrelated to GUI, but it bit here too: entering a session after earlier region tests throws a
`NullReferenceException` from `Audio.Process` (per-frame pixel-audio counters accumulated while
sessionless reach the first session frame before `Game.LocalAvatar` exists). I drain two
private `Audio` buffers by reflection before `Session.Enter`. The session tests do the same.
Since every session-entering test needs this, the harness should centralize it — drain those
buffers as part of `Session.Enter` (or fix it once and note it), rather than each test
carrying the same reflection.

## The shape of the ideal test

If the harness offered the affordances above, the entire GUI test would read roughly:

```
[GameTest(RequiresDisplay = true)]
public static IEnumerator DensityShowsInTheHoverBox()
{
    yield return Session.EnterCleanView("flat");     // no window, not paused, fog off
    var tile = new Vector2I(...);                     // any explored cell
    Session.SetPixel(tile.X, tile.Y, "Granite");
    PressureApi.SetExcess(tile.X, tile.Y, 1);
    yield return View.LookAt(tile);                   // pin camera on the cell
    yield return Cursor.Hover(tile);                  // mouse genuinely over it
    yield return Wait.Frames(2);
    Assert.Contains(expected, Hud.HoverBoxText());    // read live UI, no reflection
}
```

Every line that is currently a workaround (window closing, camera pinning, fog, drift block,
reflection, audio drain) has collapsed into a named call. That is the target.

## Priority, if any of this gets built

From most to least valuable for enabling GUI tests generally:

1. **View control** (`LookAt` + hold + fog-free): removes the single biggest source of
   fragility and game-internals coupling. Without it, every GUI test re-derives the
   camera/avatar/fog dance.
2. **Reading live UI** without reflection: the actual assertion target of most GUI tests.
3. **Clean-view session entry** (windows dismissed, not paused): small, high-frequency.
4. **`Cursor.Hover(tile)`**: turns the drift-block hack into one reliable call.
5. **`RequiresDisplay` / `RequireDisplay()`**: trivial, but every GUI test wants it.
6. **Centralized audio drain on session entry**: a one-time fix that quietly helps all
   session tests, GUI or not.

None of these are blockers — the Pressure GUI test runs today without them. They are the
difference between "possible if you know the game internals" and "a use case the harness
supports."
