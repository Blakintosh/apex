# Apex 0.3.0

- **Change weapon class.** A weapon can become another kind of weapon: a bulletweapon a projectileweapon, a
  grenadeweapon a meleeweapon, and so on, between any weapon types the deffiles have. It is in the asset's ⋯ menu, the
  Explorer's right-click menu and the palette (**Change weapon class…**). The class belongs to the root of a derivation
  chain, so the root and every asset derived from it change together; opened on a derived asset, the dialog says so.
  Most values carry over as they are. The dialog lists what doesn't: a choice the new class doesn't offer (a
  bulletweapon's Type `bullet`, say) takes a value you pick, starting at the new class's default, and the values the
  new class has no field for are dropped. Values someone set are listed one by one; ones still at the old class's
  default are counted. **Keep them in the GDT anyway** keeps those lines instead (APE and the linker ignore them). It is
  one step: Ctrl+Z takes it back until you save, and it is kept across a restart like any other change. Save writes
  the new `( "type.gdf" )` into the root's header; derived assets keep `[ "parent" ]`.
- **A file field's ⚠ explains itself.** Hovering the warning beside a file path now shows why it is there ("No file
  at …"). The tip was always there, but only the triangle's thin outline took the pointer, so it hardly ever showed.
- **Decals show in a material preview.** A decal is drawn onto what's under it, and a material preview has nothing
  else, so it used to show only the sky (APE's preview does the same). The preview now puts a plain grey surface under a
  decal material, so you see the decal as it lands on a wall.
- **Water flows, and scrolling materials scroll.** Materials that animate over time (water sim flow, emissive scroll,
  flags, vehicle treads, foliage sway) were frozen on their first frame. The preview now runs the clock as APE does, and
  keeps drawing while such a material is on screen; anything static is still drawn only when something changes.
- **asset:// links open in Apex.** Started with `asset://<name>.<type>` (e.g. `asset://ar_standard.bulletweapon`), Apex
  opens that asset once the GDTs have loaded. When Apex is already running, the new start hands the link to it and
  exits, so following links from Radiant reuses one window.
- **Radiant hears about Apex's changes.** As APE does, each value you change is sent to Radiant (and APE, and other
  tools) through the install's `gdtDB\live\changes.log` a moment after you make it, and saving runs
  `gdtdb\gdtdb.exe /update` so Radiant's asset database has what you saved. It's on by default; **Sync changes with
  Radiant** in the app menu (the Apex mark) turns it off, like APE's "Sync changes with Radiant and other applications".
- **A colour picker you can see.** Colour fields (Albedo Tint, the tints, specular and scatter colours, and every
  deffile colour) show a wider colour chip; clicking it opens a saturation and brightness square, a hue strip and an
  alpha strip to pick by eye, the colour as it was beside the colour now (click the old one to go back), and the R, G,
  B, A and hex fields as before. A drag is one undo step, and only the components you change are rewritten.
- **Custom shaders compile.** A techset whose shader source is under `share\raw\shaders_stable_toolsgfx` (your own,
  not the shipped ones) is compiled from it when no cache has a current build, as APE does, into Apex's own cache. Edit
  the source or one of its includes and the next load compiles it again; if it doesn't compile, the material's warning
  carries the compiler's errors. A transparent shader can sample the opaque scene behind it (`iResolveScene`), so
  frosted or refracting glass shows what is behind it.
- **Material settings for one component.** When a techsetdef tweaks only some components of a value (a blur radius in
  the `x` of a `float4`), each such component is its own row with the tweak's title, section, default and range.
- **Lock the preview.** The lock beside the preview keeps it on what it shows while you move to other assets; click it
  again to follow the active asset.
- **Pin a GDT.** **Pin this GDT** on a GDT's right-click menu in the Explorer puts it at the top of the GDT tree.
- **What's new.** These notes, in a panel under the Apex mark (**What's new** in the app menu and the palette). It opens
  by itself the first time a new version starts.
