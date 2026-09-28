# Skyrim Vein Navigator (Keizaal)

Walks your character along a 33-vein iron route around Darkwater Crossing and
mines each vein. No mods, no load-order changes — it reads your position from
the game's memory (found by a one-time calibration each session) and steers
with simulated keyboard/mouse input.

## Files
| File | What it is |
|---|---|
| `VeinNavigator.exe` | The navigator (built from `VeinNavigator.cs`) |
| `route_iron_darkwater.csv` | 33 iron veins, exact coords from your own Skyrim.esm |
| `veins.csv` | All 1,320 ore veins + 397 map markers (for building more routes) |
| `EsmVeinExtractor.cs` | The Skyrim.esm parser that produced veins.csv |
| `KeizaalMiningHelper.ahk` | The simple F8 tap-helper from earlier (independent of this) |

## How to run
1. Start Keizaal and get in game. **Windowed or borderless mode** (not exclusive
   fullscreen), third or first person both fine. Put your pickaxe away from hotkeys
   that could interfere; keep weapons sheathed.
2. Travel to **Darkwater Crossing** (Eastmarch, on the White River south of Windhelm).
3. Double-click `VeinNavigator.exe` (a console window opens).
4. Follow its steps — you press **F6** in game when told:
   - **Step 1:** stand touching the iron vein on the riverbank at Darkwater
     Crossing (the route's waypoint 1), press F6. It scans the game's memory
     for your position.
   - **Step 2:** walk 3–4 steps, press F6. It keeps the address that moved with you.
   - **Step 3:** turn on the spot, press F6. It confirms your facing angle.
   - **Steps 4–5 are automatic:** it wiggles the mouse and taps W briefly to
     measure your turn sensitivity and movement direction.
5. It then walks the route: turn → walk → stop at vein → aim → activate → wait
   for the mining animation → next vein.

**Hotkeys while running:** `F9` pause/resume · `F10` quit · alt-tabbing away
auto-pauses (it never plays while the game isn't focused).

## Watch it
This has no eyes — it knows where you are, but not what's in front of you.
Expect it to need help sometimes:
- It walks straight lines between veins. It jumps/backs up when stuck and skips
  a vein after 5 failed attempts or 90 seconds, but a cliff or deep river on a
  leg may need you to steer past manually (F9, walk, F9).
- It can't tell if the "Mine" prompt appeared; it activates 4 times with small
  aim adjustments and waits 12 s. A missed vein just costs that stop.
- It doesn't fight. If wolves show up, F9 and handle it.
- Enable **auto-loot / quick loot** if Keizaal has it, or grab drops yourself.

## If calibration fails
- "No candidates found" → make sure you're at the right vein (the console
  prints its coordinates) and standing still.
- "Character didn't move / mouse had no effect" → the game window wasn't
  focused, or a menu was open.
- If the game runs under a different process name, run:
  `VeinNavigator.exe route_iron_darkwater.csv <ProcessName> 1`
- Resume mid-route: `VeinNavigator.exe route_iron_darkwater.csv SkyrimSE 14`
  (starts at waypoint 14).

Calibration is per game session — restart the game, redo the F6 steps (30 s).

## Other routes
`veins.csv` has every vein in the game with exact coordinates. Ask Claude to
cut a new route (any ore, any area) — it's one command against that file.
