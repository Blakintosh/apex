# Apex 0.3.0

- **Change weapon class.** Turn a weapon into any other weapon type (bulletweapon to projectileweapon, and so on) from
  the ⋯ menu, the Explorer or the palette. The whole derivation chain changes together; the dialog lists the values
  that don't carry over. One undo step.
- **Radiant sync.** Changes are sent to Radiant as you make them, and saving updates its asset database, as APE does.
  Turn it off with **Sync changes with Radiant** in the app menu.
- **asset:// links.** `asset://<name>.<type>` opens that asset, in the Apex window that's already open.
- **Custom shaders compile.** Your own techset shaders in `share\raw\shaders_stable_toolsgfx` are compiled when APE
  hasn't cached them, and again after you edit them. Transparent shaders can sample the scene behind them.
- **Colour picker.** Colour fields open a visual picker with hue, saturation, brightness and alpha.
- **Material previews.** Decals show on a grey surface, and animated materials (water, scrolling, sway) move.
- **Per-component material settings.** A tweak on one component of a value gets its own row, default and range.
- **Lock the preview** to keep it on one asset while you work on others.
- **Pin a GDT** to the top of the Explorer from its right-click menu.
- **What's new** shows these notes under the Apex mark, and opens by itself after an update.
- **Fix:** the ⚠ beside a missing file now shows its tooltip.
