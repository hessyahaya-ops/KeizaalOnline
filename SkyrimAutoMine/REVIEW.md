# Beast's Maw pathing & minigame check — 28 Sep 2026

Everything in this folder was reconstructed from the session export
(`session-export-1790587649977.zip`). The export was created **before** the
desktop session's last round of work, so it holds the code as of
**VeinNavigator5**. Three things exist only on the gaming PC and are **not**
here: `MinigameClicker.cs`, the VeinNavigator6 changes (path-point pass radius
260 → 170), and the densified `route_corundum_beastsmaw.csv` (159 → 250 path
points). Line numbers below refer to the files in this folder.

## Verdict

The route planning is structurally sound: it plans over the cave's own navmesh
(2,226 vertices, 2,891 triangles from BSHeartland.esm), every one of the 10
stops is reachable from the entrance door, and every emitted path point is a
triangle centroid — a point that is *always* on walkable floor, never inside a
rock. The remaining wall-bump risk is not in the points themselves but in the
straight lines walked *between* them (finding 1). The minigame handoff has
five specific things to confirm on the first live run (finding 4) because the
clicker's source postdates the export and couldn't be reviewed.

## Route facts re-verified from the scan logs

- 8 corundum veins, 6 loose ore in 2 piles, 1 entrance door (`AutoLoadDoor01`
  at 11683.6, 4023.3, 7953.1) in cell `CYRTheBeastsMaw`.
- Order: door → pickup 02073573 → vein 0207334F → vein 020D3893 →
  pickup 020739E5 → vein 02073343 → vein 020D3892 → vein 020D3891 →
  vein 02073348 → vein 02073351 → vein 02073352.
- Wall veins sit 119–388 units off the navmesh; the navigator's
  "close as it gets" arrival (VeinNavigator.cs:273-275) covers them.

## Findings

**1. Corner cutting between emitted points — the one real "bumps into rocks"
mechanism.** `NavmeshRoute.cs:87-95` emits only every ~220 units of the
centroid chain (the PC's newer version: ~120), and the navigator walks a
straight line from one emitted point to the next. That straight segment is not
guaranteed to stay on the navmesh — it can clip a rock corner that the skipped
centroids went around. Densifying (already done in v6) shrinks the error but
doesn't eliminate it. Complete fix, ~20 lines in `Run`: before emitting point
*k*, sample the straight segment from the **last emitted** point every ~30
units and test 2-D containment against the mesh triangles; if any sample falls
outside, emit the last skipped centroid first (it is the corner). Then even
sparse routes cannot cut corners.

**2. Pass radius vs. point spacing.** With v6's numbers (points ~120 units
apart, passed at <170 — `VeinNavigator.cs:269`), the follower can "consume"
point k+1 while still approaching point k, re-coarsening the followed line
exactly at corners, where fidelity matters most. Suggest a path pass radius of
~100, i.e. slightly *below* the emission spacing.

**3. F10 during walking leaves W stuck down.** `VeinNavigator.cs:260` calls
`Environment.Exit(0)` on F10 without releasing W (the `Main` loop's F10 path
at line 101 does release it). Quit while walking → the character keeps walking
into whatever is ahead until W is tapped manually. Fix: `KeyUp(SC_W)` before
the exit. The same rule applies to the clicker: release everything before
exiting.

**4. Minigame handoff — confirm on first live run** (clicker source not
reviewable here):
   1. **EXIT before F8.** The navigator resumes walking the instant it sees
      F8 (`VeinNavigator.cs:348`). If the minigame box is still open, W and
      mouse input land in the menu; the navigator then reads "not moving",
      declares itself stuck, and starts jumping and side-stepping inside the
      menu. The depleted sequence must be: red bar → click EXIT → wait for
      the box to actually close (~0.5 s) → then F8.
   2. **F8 tap length.** The navigator polls `GetAsyncKeyState` every 50 ms;
      a very short synthetic tap can fall between polls. Hold F8 down
      ≥150 ms.
   3. **Shared F10.** Both programs treat F10 as quit and both hooks are
      global — quitting one quits both. Fine if intended as "stop
      everything"; there is currently no way to quit only the clicker
      mid-route.
   4. **Click only when the panel is visibly open.** At a wall vein the
      navigator can "arrive" without E having hit the vein
      (`VeinNavigator.cs:274` arrives after 3 s without progress inside 450
      units). If no minigame opened, the clicker is staring at the 3-D cave —
      torch glow is bright and warm like the gold sprite, and a click in the
      world swings the pickaxe. The clicker must require the dark panel
      (large, low-brightness, low-variance region) before any click.
   5. **Loose-ore stops don't wait for F8.** `VeinNavigator.cs:113-119`
      grabs pickups with E and walks on. If Keizaal opens the minigame for
      loose ore too, the navigator leaves with the box open (same failure as
      4.1). The **first stop of the route is a pickup pile**, so this shows
      up immediately if it's a problem.

**5. Cosmetic.** `GoTo`'s 30 s / 90 s leg timer keeps running while the game
is unfocused — a long alt-tab mid-leg gets the stop skipped as "stuck".

## Minigame photos → detection notes (reference/minigame-photos/)

- Sprites: diamond crystals with a bright glow halo — **gold**, **icy
  blue-white**, and **teal** variants seen. Panel is dark navy with a soft
  vignette. Progress bar below the panel reads **N / 5** → 5 catches per
  vein; "+N ores collected" (running total) top-left; **EXIT** button
  top-right, outside the panel.
- The panel is slightly translucent: warm torch-light patches bleed through
  (visible bottom-right in two photos). A brightness-only detector will
  false-positive on them — saturation matters for gold; for blue-white,
  blue-channel dominance (B > R) separates ore from the neutral white cursor.
- The game draws its own large white cursor **inside the frame** — it must be
  excluded by distance from the current cursor position (`GetCursorPos`), not
  by color, because its brightness matches the blue-white sprite.
- Small white rings appear on the panel (click/spawn ripples) — ignore blobs
  below ~12 px.
- **No photo shows the red depleted bar**, so that detector is untested
  against reality. The first depleted vein of the first live run is the test;
  if it misses, screenshot that moment.

## Respawn timer

`KzlOnlineMods.esp` contains no respawn/reset/depletion strings (checked in
the desktop session; the post-export scan additionally found only battle-NPC
respawn settings). The mining minigame, the ore counter and the depletion
state are server features — the respawn clock runs on Keizaal's server and
cannot be read from any local file. The observed **15–30 minutes** is the best
available answer; if the exact number matters, time one specific vein in game
(note the console clock when it goes red, check it again on each later pass).

## To finish the code review

Send these three files from `C:\Users\Yahaya Mohamed\SkyrimAutoMine\` (attach
them in the Claude app, or re-export the session after the new work):
`MinigameClicker.cs`, the current `VeinNavigator.cs`, and
`route_corundum_beastsmaw.csv`.
