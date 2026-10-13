# Apex 0.4.0

- **Stock Library.** With the Stock Library installed, search lists the game's shipped assets after your own. Open one
  to read it, preview its model, material or image, and see what it uses. **Create local copy** brings it into your
  GDTs with everything it needs, and can be undone, even when the copy fails part way. With Dragon, a map can use a
  stock asset by name without copying it. **Components** in the app menu shows which Mod Tools parts this install has.
- **Stock sounds.** Browse and play the game's sounds from the app menu or the palette, and copy one into
  `sound_assets` with its alias line. Copies can be undone.
- **LOD fields.** The xmodel LOD settings now use the keys the linker reads. Each LOD has Auto-Generate, its percent
  and Force Rigid, and there are fields for the auto-generate priorities and the LOD bias.
- **Auto LODs in the preview.** Auto-generated LODs now show in the preview, and the info line gives each LOD's face
  count. Below 40,000 faces they are the LODs Dragon builds too. For larger models Dragon decimates differently; the
  preview will show its LODs once Dragon writes them in a form the preview reads.
