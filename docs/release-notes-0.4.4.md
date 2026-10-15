# Apex 0.4.4

- **Material slots.** An xmodel's Skin Override lists each of the model's materials as a slot: its swatch and the
  name of the material that draws there. Click the name to open the material. An overridden slot shows the model's own
  material under it, struck through. **Replace ▾** chooses another material, hides the surface (`nodraw`), puts the
  model's material back or opens it; Delete puts it back too. Up to twenty slots show before the list scrolls.
- **Stock links open.** An `asset://` link to an asset that isn't in your GDTs, such as Radiant's Open in Apex on a
  shipped model, now opens it from the Stock Library.
- **Fix:** a long name in a list (a material, a tag) could push the list past its column, cutting off its right edge.
