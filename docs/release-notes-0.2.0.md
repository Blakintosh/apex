# Apex 0.2.0

Apex 0.2.0 is licensed under the GNU General Public License, version 3.

Besides extensions (below), this release brings:

- **A redesign, with three themes.** Graphite, Slate and Light, following the Windows light or dark setting by default.
  The Apex mark opens an app menu with the theme, Updates and About Apex.
- **Updates in place.** Apex checks for a release, downloads it in the background and installs it when you choose, now
  or when you close Apex. There is an installer, `apex-setup.exe`, which can put Apex in place of APE (APE is kept and
  comes back on uninstall) or beside it.
- **Views of the form.** **Set** lists only the properties an asset holds a value for, with **Add property** to bring
  in another. A section whose keys are a grid of two names (an entity's impact effects by surface and hit type) shows
  as that grid, with a switch back to the list. The deffile's sub-sections are titled in the form (each LOD in a
  model's list, for one).
- **xmodel materials** count every material of the model, open one when you click its name, and say which side of
  the list is the override.
- **Notetracks** you can drag, snap and scrub, a note's action picked from groups, and a vector as one row with a box
  per component.
- **The mouse's side buttons** are Back and Forward; the whole strip beside a scrollbar takes the pointer, not only
  its hairline; a box that opens a list keeps its edge at rest; a path shows its backslashes once.
- **Fixes** to the Explorer, search, tabs, banners, the status line and the preview's first-person camera, and a
  deffile's `<error>` surface type is no longer offered.
This release adds extensions: data that adds fields, tables and sections to asset types Apex already edits, and,
optionally, one native module that moves the weapon preview. weapon-tech is the first one. Apex still ships none.

Everything else works as in 0.1.0. With no extension installed, every editor shows and does what it did before, apart
from two fixes that reach every editor: the last row of a form now scrolls fully into view, Undo of a row the keyboard is
already in leaves the keyboard there.

## Extensions

- **Fields, tables and sections from a manifest.** An extension is a folder in `%AppData%\Apex\extensions\<id>\`
  holding an `extension.json`. It can add switches, numbers, choices, text, weapon pickers and xanim fields, and
  tables over numbered keys (`wtKick1`, `wtKick2`…). Sections and rows can show or hide by simple rules, and the whole
  extension turns on and off per asset with one key, inherited from parents. A broken manifest never stops Apex: what
  was left out is said once, in a banner. Reference for authors: `docs/extensions.md`.
- **Built to be read by modders.** A value made of comma-joined numbers shows as one row per named part ("Stiffness",
  "Return speed"…), each checked and undone by name, while the stored value stays one key and an untouched value is
  written back byte for byte. Choices can show words while storing numbers, and two stored columns that mean one thing
  (ADS and Gun) show as one choice ("Applies to: Hip view, Hip gun, ADS view, ADS gun"). A column nobody edits can be
  hidden, and a table can sit right after the field it belongs to. A section that holds nothing can start folded and
  opens by itself when a value appears; nothing you set is ever hidden.
- **Finding it.** The property filter finds an extension's sections by their titles ("weapon tech", "recoil"), and the
  section list lists them first. A weapon the extension is off for starts with one quiet line from the extension and
  **Turn on**.
- **Values in parts.** A value made of named parts (a spring's Stiffness, Return speed, Return curve, Max climb, Max
  drift) is one row per part. Paste the whole comma-joined value (`2200,0.06,1,10,10`) into any part and every part
  fills, as one undo step; a shorter list fills on from the part you pasted into; a list that doesn't fit changes
  nothing and says so.
- **Record tables.** One row per numbered key and one cell per column, with checks per row. Add, remove and move rows
  from the keyboard (Ctrl+Enter, Alt+Delete, Alt+↑/↓) or with the row's buttons. **Ctrl+V** pastes rows (as stored, from
  a spreadsheet, or as `wtKick1 = …` lines) as one undo step; **Shift+↑/↓** and **Ctrl+C** copy them. A table wider
  than the editor scrolls sideways.
- **Many weapons at once.** The palette offers "Turn on <extension> for N assets" (and Turn off) over the Explorer's
  selection or the open table's checked rows, as one undo step. Parents are done first; a variant that would inherit
  the value isn't written.
- **Words from the extension, and an export.** An extension can put one line of its own under its first header
  (weapon-tech says the game doesn't read these values yet while its linker patch isn't installed) and offer a "Copy as …" command that copies the asset's
  values, its own and inherited, as `key = value` lines under a header of its choosing (weapon-tech: "Copy as
  weapon_tech.cfg", under `[weapon:<name>]`). Apex shows no words of its own about an extension's tools or the game.
- **Values in a `.gdtx` beside the GDT.** APE drops keys its deffiles don't declare when it saves a GDT, so extension
  values live in `<name>.gdtx`, in GDT syntax, one block per asset. Apex writes it only when you save, through the same
  save path as GDTs: backups, conflict checks, crash recovery, undo, the session journal; a save that wrote only
  extension data says so ("Saved ar_an94.gdtx"). Keys are written in row order (`wtKick1`, `wtKick2`, … `wtKick10`);
  a list is read by row number, wherever its keys sit. `//` starts a comment to the end of the line anywhere outside a
  quoted value, and a save keeps every comment byte for byte. Rename, move, duplicate and delete in Apex take an
  asset's blocks with it; the same done in APE leaves them behind, and Apex lists them. Search, table mode, Compare and
  the Explorer's problem count read extension values too.

## The native preview module, and when Apex loads it

An extension may ship one 64-bit DLL that computes motion for the weapon preview. Nothing about it happens at
startup. The first time a preview needs it, Apex hashes the file (SHA-256) and asks in the preview itself, as a card
on the render that never takes the keyboard from where you are working: "Load weapon-tech's preview module?", naming
the extension, its version and the file, with the full path and SHA-256 behind **Details**. **Don't load** is the
default: Esc or Enter in the preview choose it. The answer is remembered for that exact file, sealed for your Windows
user in `%LocalAppData%\Apex\extension-modules.dat`. A different file (a new build) is a new question, which says what
changed: the hash, the size and the file's time, old and new.

Before asking, Apex refuses a module that would pull in a DLL Windows doesn't supply, a path outside the extension's
folder, or a folder that is a junction or symbolic link. What loads is the file that was hashed, held open from the
hash to the load and loaded by the path Windows reports for it. After every call Apex checks its buffers, its output,
the SSE floating-point state and how long it took. A module that misbehaves ten times in a row is turned off until
Apex restarts. "Don't load" leaves editing and saving exactly as they are; the preview offers **Load module…** to ask
again.

## Weapon recoil preview

A weapon an extension with a module is on for gets a preview of its own, docked at the right column's full height: the
first-person viewmodel through APE's renderer, moved by the module. **Fire** (hold it, or Space) fires at the weapon's
fire time. **Hip / ADS** (A) moves between the idle pose and the ADS pose over the weapon's transition times.
**Reset** (R) starts over. Edits show 150 ms after you stop typing. At rest it draws nothing new and costs nothing.

Over the render, drawn as UI so APE's frame under it is unchanged, the **spray overlay**: a reticle with a degree
ruler, the view's path through the last burst and a dot per round where its kick peaked. Under Fire, the **readout**
for that burst: Climb, Drift, Settles in and Shots. A weapon with no gun to draw still fires, and the overlay and
readout work as with one. The line under the preview says, in the form's own labels, why something isn't moving
("Set a Gun spring to see the gun kick").

## Known limits

- **The game reads `.gdtx` values only through weapon-tech's linker patch.** The patch turns a weapon's `.gdtx` values
  into its `weapon_tech.cfg` block when a map is linked with weapon tech on (`"weapontech": true` in the map's
  `linker.json`). Apex checks that the patched linker is installed in the BO3 `bin` folder and says nothing while it is;
  if it isn't, the Weapon tech header says the game doesn't read these values yet, and **Copy as weapon_tech.cfg** (the
  button on that header, or the palette) copies the weapon's section ready to paste into the cfg by hand. Apex can tell
  the patch is installed, not that it is switched on for a given map.
- **What the simulator has been checked against.** weapon-tech compared it with the real game (BO3, one IW9 rifle,
  about 150 fps): kick velocities, the springs (hip, ADS and the lerp between) and the offset patterns it exercised
  match, and the view output shows the game's one-frame lag. Not verified in game: camera-shake scaling, offset
  patterns on the view angles with a non-zero strength, noise and random-spline patterns (seeded by the game clock, so
  never comparable), kick return, kick percent other than 1, frame rates other than about 150 fps. Two things the
  preview can't show: once the view spring comes to rest BO3's own kick recovery can move the view by up to about
  0.07° for a few frames, and the game's spring keeps a 5 ms step phase from every frame since the weapon came out, so
  the same burst fired later in a game session can differ by up to about 0.23° of view pitch. See weapon-tech's
  `docs/APEX_SIM.md`, "Verified in game".
- A module that hangs hangs Apex. Calls run on the UI thread and nothing can interrupt them; the checks run after a
  call returns. A module that crashes ends Apex. Unsaved changes come back on restart: the session journal is written
  before a module loads and before each simulation is made, and otherwise within a quarter second of an edit.
- Consent is the only gate on what a module does. It runs with your permissions, and a DLL it loads itself at run
  time is not seen by the import check. Load modules only from people you trust.
- A file that isn't a 64-bit DLL at all is found out only when you choose Load: the question comes first.
- An extension folder that is a junction or symbolic link is left out; copy the folder in instead.
- Module answers given in development builds before 0.2.0 (`%AppData%\Apex\extension-modules.json`) are not carried
  over; you are asked once more.
- Apex checks and restores the SSE floating-point state (MXCSR) after each module call, but not the x87 control word.
- A loaded module can't be replaced while Apex runs (Windows locks the file): close Apex first.
- Only the first extension with a module that is on drives a weapon's preview. The idle anim is held at its first
  frame, ADS doesn't zoom, and no sway, bob or fire anim plays. The view is the first-person camera's (Hor+), so a
  tall, narrow preview shows little of the gun beside the reticle.
- Picked in the section list, a section below a record table that holds rows, or a folded one last in the form
  (weapon-tech's More), can land a few hundred pixels off the top of the view, or not move the form the first time.
  Scroll the last bit, or use the filter.

## Fixes

- A focused switch in a record table read "Or" instead of "On": the theme's square focus box covered the label's edge.
  Switches now show only their own focus ring, everywhere.
- The sideways scroll bar of a record table in a narrow editor had a thumb too small to grab. It is now at least 24 px.
- Number fields were invisible to screen readers and UI Automation apart from their bare value. They now read as a
  named field with its value, and a value set through assistive tech is entered as if typed.
