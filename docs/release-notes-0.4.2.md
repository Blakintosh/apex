# Apex 0.4.2

A fixes release.

- **Fix: Apex lists every GDT Radiant and APE do.** Apex now takes its GDT folders from the same list the GDT database
  uses (`bin\converter_gdt_dirs_0.txt`). Until now it read a fixed set, so GDTs in `usermaps`, `mods`, `archetypes`,
  `aitype` and `temp_assets` were missing, for example a map's own GDTs in `usermaps\<map>\`. 0.4.1 added
  `texture_assets` and `art_assets`; if you still can't find a stock asset such as `t7_concrete_sidewalk_2x2_01`, check
  that you're on 0.4.1 or later (app menu, Updates).
