# Apex 0.2.2

A fixes release; it replaces 0.2.1, whose resize handles did not work.

- **Resize handles take the pointer.** The edges between the editor, the preview and the Inspector are an 8 px strip
  with the line in its middle, and all of it grabs; the line lights in the accent colour under the pointer. In 0.2.1
  only the Explorer's edge did.
- **The preview's corner handle is reachable.** A grip at the left end of the preview's bottom edge sizes the column's
  width and the preview's height together; double-click it to put the height back to 16:9.
- **The preview opens at 16:9.** Until you size it yourself, its height follows the width of its column. The preview
  height saved by 0.2.0 is not carried over.
- **Derived assets preview.** An xmodel or xanim that derives from another and sets no `filename` of its own showed
  "No model filename set". It now previews the file it inherits.
