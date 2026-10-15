# Apex 0.4.3

- **EXR images preview.** Image assets and material slot thumbnails now show `.exr` sources, read by the Mod Tools' own
  OpenEXR reader (`bin\openexr64r.dll`), as APE and the linker read them. Values are shown as stored, 0 to 1, so
  brighter HDR values show as white. A file named `.exr` that isn't an OpenEXR file (a PNG renamed, say) doesn't
  preview, because the converter rejects it too.
