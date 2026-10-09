# Apex 0.2.1

A fixes release.

- **The preview opens at 16:9.** Until you size it yourself, the docked preview's height follows the width of its
  column, so the render is 16:9 and the column can be widened without the picture going letterboxed. Your own size
  is kept once you set one; the preview height saved by 0.2.0 is not carried over.
- **Resize handles are easier to find and to grab.** The edge between two panes lights up in the accent colour under
  the pointer, and the grab area is wider (11 px on the preview and editor edges, 4 px more either side of the
  Explorer's). The handles sit above the panes beside them, so the pointer no longer has to be exact.
- **A corner handle on the preview.** Where the column's edge meets the preview's, a grip sizes both at once: drag it
  to set the width and the height together, double-click it to put the height back to 16:9.
- **Derived assets preview.** An xmodel or xanim that derives from another and sets no `filename` of its own showed
  "No model filename set". It now previews the file it inherits.
