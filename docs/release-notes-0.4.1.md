# Apex 0.4.1

A fixes release.

- **Fix: assets in `texture_assets` and `art_assets` are listed.** Apex read GDTs only from `source_data`,
  `xanim_export` and `model_export`, so stock GDTs elsewhere were missing even though Radiant and APE show them,
  for example `t7_concrete_sidewalk_2x2_01` in `texture_assets\t7_concrete.gdt`. Apex now reads and watches the same
  folders the GDT database indexes, which adds about 8,800 assets on a standard install.
