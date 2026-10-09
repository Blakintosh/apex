# Apex 0.2.1

A fixes release.

- **The preview opens at 16:9.** Until you size it yourself, the docked preview's height follows the width of its
  column, so the render is 16:9 and the column can be widened without the picture going letterboxed. Your own size
  is kept once you set one; the preview height saved by 0.2.0 is not carried over.
- **Resize handles are easier to find and to grab.** The edges between panes are an 8 px strip with the line in its
  middle, all of it grabbable, and the line lights in the accent colour under the pointer.
- **A corner handle on the preview.** At the left end of the preview's bottom edge, a grip sizes both at once: drag it
  to set the width and the height together, double-click it to put the height back to 16:9.
- **Derived assets preview.** An xmodel or xanim that derives from another and sets no `filename` of its own showed
  "No model filename set". It now previews the file it inherits.
