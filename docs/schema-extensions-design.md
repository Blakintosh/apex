# Schema extensions (weapon-tech first)

Status: steps 1 to 4b built and hardened (2026-10-06), for 0.2.0; then the editor pass and the preview pass (both
"As built" sections below). The "As built" sections are a dated record:
a later step supersedes what an earlier one lists as not done (marked where it does). Scope:
declarative schema extensions and one native preview-simulator module per extension are in; other code plugins stay out.

## Decided
- An extension is data: a folder in `%AppData%\Apex\extensions\<id>\` with a manifest that adds fields, key groups,
  record rows, sections and visibility rules to an existing asset type. No code, no file writes; saving still goes
  through the save path. Apex ships none.
- Per-asset on/off is an ordinary GDT key (e.g. `wtEnabled`), inherited through parents. Off hides, never deletes.
- weapon-tech owns and generates its manifest from its key table (`kWtcTails`, `wt_cfgv2.h`). Apex does not hardcode wt keys.
- Key names are a permanent contract: version the key set, plan migrations before renaming.
- Manifest format is internal and versioned (`apexSchema`) until a second consumer exists.
- First goal beyond scalars: the IW8 kick/recoil tech (`wtKick#`, `wtWop#`, springs, curves) visible and editable.

## Phase 0 result (2026-10-04, run in APE via computer control)
Probe: a bulletweapon asset with `wtEnabled`, `wtFireTimeMs`, `wtKick1/2`, `wtAdditiveSlot1`. In APE: opened it,
changed `displayName`, Ctrl+S. Result: **APE dropped every `wt*` key.** It rewrote the asset as a full 35 KB
bulletweapon block (1,293 lines, every deffile-declared key at its value/default) and kept only keys its deffile
declares. So extension keys cannot live inside a stock-typed asset in a GDT APE may also save: one APE save of that
GDT silently loses them. (The probe asset as written is `docs/phase0/zz_wt_probe.gdt`; it was removed from the install
afterwards, and APE's post-save copy was kept in %TEMP% only.)
Not tested: linker tolerance (moot for this layout).

### Consequence: where the data lives (decision needed before phase 1)
A. **Sidecar file owned by Apex** (e.g. `<gdt>.apexext` or per-mod `apex-extensions.json`), keyed by weapon asset
   name, written only through the save path; weapon-tech's compiler/linker feature reads it instead of GDT keys.
   Safe from APE; needs weapon-tech's GDT-compiler side changed to read the sidecar; renames of weapons need care.
B. **Separate GDT asset of a custom type** (`weapontech.gdf`) per weapon, ref'd by name. APE behaviour for an
   unknown gdf is untested (may drop, preserve or refuse), and the linker may reject an unknown type. Next test.
C. Keep `wt*` in the weapon GDT and accept APE loss: rejected (silent data loss).
Leaning A (or B if the unknown-gdf test shows APE preserves such assets verbatim). The extension manifest, widgets,
visibility rules and enable flag are unaffected; only the storage backend changes (a small interface in the save path).

## Decision: `.gdtx` sidecar (2026-10-04)
Extension data lives in `<name>.gdtx` next to `<name>.gdt`, in GDT syntax, one block per asset tagged with the
extension id (`"ar_foo_zm" ( "weapon-tech" ) { "wtKick1" "0.5" ... }`). Apex reads and writes it through the same
save path (splice, atomic replace, backups, conflict detection, round-trip tests). The user accepts it is imperfect
but the least painful option.

Verified (probe `.gdt` + `.gdtx` in source_data, APE open, sibling `.gdt` edited and saved with Ctrl+S):
- APE saved the `.gdt` normally and left the `.gdtx` byte-identical (same SHA-256); nothing about it in the APE log.
- Static check: APE's file filter is "GDT Files (*.gdt)"; GdtDBTray watches ".awi, .gdf and .gdt" only.
Not verified: linker/packaging tools ignoring `.gdtx` (linker_modtools.exe has no gdt-extension strings, so likely
fine); weapon-tech's compiler/linker feature must be changed to read `.gdtx`.

Known costs to design for: orphaned blocks after an APE rename/delete (flag on load, offer re-point/remove); a GDT
copied without its `.gdtx` (show "no extension data"; weapon-tech linker check warns); parent lookups across files
by asset name, with provenance.

## As built: `.gdtx` read/write (step 1, 2026-10-04)
Infrastructure only: no UI, manifest or schema merge yet.

- **A sidecar is a GDT to the save path.** `ExtensionSidecar` (Models) wraps a `GdtFile` (`File`, named
  `<gdt name>x`) whose records are the blocks: `Name` = asset, `Type` = extension id, lazy bodies, `Disk` refs and
  baselines exactly as for assets. So `GdtSavePlanner`, `GdtSaveService`, `GdtSplicer`, `GdtStamp`, `BackupStore` and
  `GdtWatcher` are reused; the differences are one flag, `GdtSaveRequest.Sidecar`.
- **Loading.** `GdtLoader.IndexToGdtFile` checks for `<path>x` (one `File.Exists` per GDT) and indexes it with the
  GDT indexer: offsets only, values read on first use. `GdtCatalog` never lists a `.gdtx` (`*.gdt` doesn't match it in
  .NET; checked). The header `( "weapon-tech" )` indexes as a root block whose type is the id (no `.gdf` to strip); it
  can't be read as a parent. A `[ "x" ]` block in a `.gdtx` isn't Apex's format: it is kept and never read as data.
- **API for the next step** (`GdtFile.Extensions`, null while a GDT has no sidecar):
  `ExtensionSidecar.Of(gdt)`, `Find(asset, id)`, `Values(asset, id)`, `Get(asset, id, key)`,
  `Set(asset, id, key, value, defaultValue = null)` (null or the default removes the key), `Orphans()`,
  `CoreType(asset, key)`, `Blocks`, `Owner`, `PathFor(gdtPath)`. Saving needs nothing new: `GdtSavePlanner.Plan`
  adds a request per changed sidecar (`PlanSidecar`), and `GdtSavePlanner.Commit(result)` commits either kind.
- **Only when needed.** A block is written only with a value; a sidecar is created by the first block and never
  otherwise. A saved block emptied this session is deleted from the file; when that leaves nothing but `{ }`, the
  file is removed: backed up (as `.gdtx`, in `<name>.gdtx-<hash>`), moved aside atomically, compared with the bytes
  read under the lock, and only then deleted; a write in that window is put back and reported. A file holding
  anything else besides blocks is kept.
- **Splicing.** In a `.gdtx` a block is known by asset *and* id (an asset has one block per extension), for lookup,
  ambiguity and name clashes; new blocks are written with the id as is. Everything else is the GDT splice: untouched
  bytes identical, the file's own EOL and indentation, raw values.
- **Core keys never go in a sidecar**: `Set` throws for a key the asset's deffile declares, and the planner refuses a
  save that would write one (a problem, nothing written).
- **Orphans** (blocks whose asset isn't in the GDT) are listed by `Orphans()`, computed from the GDT's current assets,
  and never touched by a save. Renaming an asset in Apex made its blocks orphans (step 3 renames them with the
  asset: "The gaps").
- **Conflicts and reloads** mirror GDTs: stamps per sidecar; `GdtLoader.ApplySidecar` reloads (edited blocks keep
  their edits; a block the session made that the file now also has becomes that block, so a save asks per key);
  `GdtLoader.RemoveSidecar` handles deletion on disk (edited blocks stay, to be written to a new file). The watcher
  reports `.gdtx` changes and the app routes them to these; the conflict dialog's reload does the same.
- **Not in step 1** (step 2 added both, under "History, dirty, save"): extension edits in the session journal and the
  undo history; the status line counts them as their asset's change. Tests: `Apex.Shots/ExtensionSaveChecks.cs` (in every `--save` run).

## As built: manifests, merge and flat fields (step 2, 2026-10-04)
Flat scalar fields only. Record lists (`wtKick#`) came in step 3; the `groups` member below was never built (an Apex
reading one notes it as unknown).

### Manifest format (`apexSchema: 1`, internal)
One folder per extension: `%AppData%\Apex\extensions\<id>\extension.json` (`APEX_EXTENSIONS_DIR` overrides the
folder; Apex.Shots always sets it, so no run reads the user's own). JSON with `//` comments and trailing commas allowed.

| Member | Type | Meaning |
|---|---|---|
| `apexSchema` | number, required | Must be `1`. Anything else (or none) leaves the extension out. Additions stay within 1; only a change an older Apex would misread bumps it. |
| `id` | string, required | `[A-Za-z0-9][A-Za-z0-9_.-]{0,63}`. Written into the `.gdtx` as each block's type, so it is a permanent contract like the keys. A folder named otherwise is noted, not refused. A second manifest with a taken id is left out. |
| `version` | string | Shown in the provenance tooltip. Missing: noted. |
| `targets` | string[], required | Asset types as the GDTs name them (`bulletweapon`). `weapon` means every type whose name ends in `weapon` (bulletweapon, projectileweapon, dualwieldweapon, dualwieldprojectileweapon, grenadeweapon, gasweapon, meleeweapon, turretweapon, cybercomweapon; mock `weapon`), so manifests don't list them and a new weapon deffile is covered. Not weaponcamo or sharedweaponsounds. |
| `enabledBy` | string | The per-asset on/off key (`wtEnabled`). Effective value = the asset's own block, else its parent chain's, else the field's default. Off (`VisibleWhen.Truthy` false) hides every field of the extension but the switch; stored values are never touched. With no field of that key, Apex adds a toggle "Enabled" (default 0) at the top of the first section. |
| `sections` | object[], required | In order, after the deffile's sections and before "Other". |
| section `title` | string, required | The section header and rail entry. |
| section `visibleWhen` | string | A rule (grammar below); false hides the section's fields. |
| section `fields` | object[] | The flat fields. |
| field `key` | string, required | `[A-Za-z_][A-Za-z0-9_]{0,127}`. Unique within the manifest. |
| field `kind` | string, required | `number`, `toggle` (0/1), `choice`, `text`, `assetRef` (needs `refType`), `anim` (an xanim reference: assetRef with refType `xanim`). |
| field `label` | string | Row label (default: the key). |
| field `description` | string | Tooltip and Inspector text. |
| field `default` | string, number, or true/false (toggle) | What a missing key means; numbers keep the manifest's spelling. Toggle default must be 0/1 (else 0, noted). |
| field `min`, `max` | number | Both or neither (a one-sided range is noted and dropped); max must exceed min. Out of range is a ⚠, never clamped. |
| field `step` | number > 0 | Scrub/arrow step. Default: 1 for integers, else a hundredth of the range's order of magnitude (0.001 to 1), else 0.1, as the deffile importer does. |
| field `integer` | bool | Whole numbers only (number kind). |
| field `choices` | (string or number)[] | Choice kind; none means any value is accepted (noted). |
| field `refType` | string | assetRef's asset type. |
| field `visibleWhen` | string | A rule; false hides the field. |

Any other member, at any level, is noted and ignored.

### Loader rules and diagnostics
`Services/Extensions/ExtensionLoader` never throws. Problems are `ExtensionDiagnostic(Extension, Problem, Message)`:
- **Disabled** (the extension is left out, the others load): unreadable or over 1 MB, not JSON (with the line), not
  an object, apexSchema missing or not 1, bad or taken id, no targets, enabledBy not a key name, no sections list.
- **Skipped** (that part is left out): a section without a title or not an object; a field without a valid key or
  kind, declared twice, or an assetRef without refType; a rule that doesn't parse (its field or section always shows).
  At merge (`ExtensionRegistry.For(type)`): a field whose key the type's deffile declares (it belongs in the GDT, and
  step 1's planner would refuse it anyway), or one another extension already adds (the first by id keeps it). An
  extension whose enabledBy key is a deffile key is left out of that type whole.
- **Note** (ignored, everything else loads): unknown members, bad optional values, a rule reading a key that isn't one of
  the extension's fields (it reads as empty), a folder not named for its id.

Startup: manifests are read off the UI thread inside the deffile task (so an asset opened as soon as it can be has
them); cold first read of the fixture ~10 ms (JSON code warming up), ~0.5 ms after. Once the catalog is in, every
targeted type is merged and one neutral banner (never a dialog) says what was left out, with each item in its tooltip;
it is skipped when there is nothing to say. Notes aren't announced (a manifest written for a newer Apex would otherwise
nag every launch): they are listed in the extension's section-header tooltip.

### visibleWhen grammar
```
or      := and ('||' and)*
and     := compare ('&&' compare)*
compare := unary (('==' | '!=' | '<' | '<=' | '>' | '>=') unary)?     (no chaining)
unary   := '!' unary | primary
primary := number | "string" | 'string' | true | false | key | '(' or ')'
```
Precedence as in C. A key reads the row's effective value (own, inherited or default) of the same extension. A bare
value is true unless empty, 0, false, off or no (case-insensitive). `==`/`!=`: against true/false as switches, numbers
numerically, else text case-insensitively. Orderings need two numbers; anything else is false. Strings have no escapes
(backslashes literal, like GDT values). At most 1,000 characters and 32 levels of nesting. Nothing else exists: no
functions, assignment or core-key reads. A rule that fails to parse is a Skipped diagnostic and never hides anything.

### The editor
- **Merge.** `AssetEditorViewModel` adds one `CategoryViewModel` per extension section (`Extension` = id) after the
  deffile's sections, rows made by the same `Create` as deffile rows; `PropertyDef.Extension` marks their defs. The
  header shows the id after the count, its tooltip the manifest, version, the `.gdtx` the values go to and any notes;
  the row tooltip and the Inspector ("From the weapon-tech extension. Saved in x.gdtx, beside the GDT.") say the same.
  With nothing installed for the type, `_extensions` stays null and every path is the old one (checked row for row).
- **Values.** Rows read and write `ExtensionSidecar` of the asset's GDT (`MainViewModel.GdtOf`, passed as `gdtOf`), never
  `record.Properties`. A value equal to what the key reads without it (inherited, else default), on a key the block
  didn't hold at the session's start, is removed rather than stored (step 1 writes only blocks with values).
- **Inheritance.** Parents are resolved as for deffile rows; each ancestor's block is read from that ancestor's own
  GDT's sidecar (one block per ancestor, never a whole file). Provenance, override count, "Use parent value" and the
  Overrides view work as for deffile rows; `RefreshInherited(keys)` follows a parent edited in another tab.
- **Rules.** Re-evaluated on any extension edit (a few dozen rows) and with the deffile's rules. Hidden rows count as
  hidden in the empty-state text, attributed to the extension.
- **History, dirty, save.** Edits go into the asset's undo history (`PropertyChange.Extension` =
  `ExtensionTarget(Gdt, Id)` writes undo/redo to the sidecar), Undo all covers them, and `RecountSession` adds the
  asset's blocks' changes to its count, so the chip, tree dot and Ctrl+S treat them as the asset's. The session
  journal records them as `xset` entries (newest per asset and extension wins; replay puts them back into the
  blocks); Discard all drops them (`ExtensionSidecar.DiscardEdits`). A watcher reload of a `.gdtx` rebases open tabs.
- **Fixed on the way:** `MainViewModel.PlanSave` copied requests without `Sidecar`, so an in-app save spliced a `.gdtx`
  by asset name alone; it keeps the flag now. The form's scroll padding moved to the presenter's margin: Avalonia
  arranged the padded panel 20 px short of its height, so the last row of every form could not be scrolled fully into
  view (the extension's switch is the last row while it is off).
- **With no extension installed** every editor shows the same content and behaves the same as before extensions,
  except for two fixes that ship to every editor, extension or not: that scroll padding, and Undo leaving the keyboard
  in a row it is already in (`AssetEditorView.FocusRow`, under "The table").
- **Orphans** (`Orphans()`, blocks whose asset isn't in its GDT) are counted once at startup into the same banner,
  listed in its tooltip; nothing is offered or removed.
- **Not wired in step 2** (all wired in step 3, "The gaps"): Explorer search (`prop:`), the table and compare read
  `record.Properties` and the deffile schema, so extension keys didn't appear there. Validation problems on extension
  rows showed on the row and in the editor's Problems view but not in the Explorer's ⚠ count. Renaming or moving an
  asset in Apex left its blocks behind as orphans.

### Extending (key groups, record rows)
(Written before step 3, which added `records` this way; `groups` wasn't needed.)
Add members to a section next to `fields` (`groups`, `records`): an Apex without them notes and ignores them and still
loads the flat fields, so `apexSchema` stays 1. In code: `ExtensionLoader.Reader.Section` parses them, `ExtensionSection`
carries them, `ExtensionRegistry.For` filters them against the deffile, and `AssetEditorViewModel.AddExtensionSections`
makes their rows; `ExtensionRows.Fields` maps each row to its definition, and every value path already goes through
`ExtensionValue` / `WriteExtensionEdit` / `RevertExtension` keyed by the row's key. Rules read
`ExtensionRuleValue(key)`, which a group would extend to its numbered keys.

Tests: `Apex.Shots/ExtensionChecks.cs` (`--extensions`, and every full run): evaluator, loader tolerance, merge,
the editor on temp GDTs (rules, inheritance across GDTs, undo, save byte-exactness), the journal and discard in the
app, and the live app on a temp install with real input. Fixture: `Apex.Shots/Fixtures/extensions/weapon-tech`.

## As built: record lists and the step-2 gaps (step 3, 2026-10-04)
Typed tables for numbered keys (`wtKick#`, `wtAdditiveSlot#`); extension blocks follow their asset through rename,
delete, duplicate, copy and move; search, the table, compare and the Explorer's ⚠ count read extension values.
Not here: `wtWop#`, `wtInterrupt#`, a kick plot, native modules, any preview. (Since then weapon-tech's own manifest
declares `wtWop#` and `wtInterrupt#` as record lists; native modules and the preview are step 4; there is no kick plot.)

### Manifest additions (still `apexSchema: 1`)
A section may hold `records` beside `fields` (an Apex from before step 3 notes the unknown member and loads the fields).

| Member | Type | Meaning |
|---|---|---|
| record `key` | string, required | A key name ending in `#` (`wtKick#`): row n is `wtKick<n>`. A stem whose keys another field or list would own (`wtA#` and `wtA1#`, a flat `wtKick3`) is Skipped. |
| record `label`, `description` | string | The table's label line and tooltip. |
| record `max` | whole number > 0 | Most rows; Add stops there, and more rows read from a file are a problem. None: no limit. |
| record `visibleWhen` | string | As a field's. |
| record `columns` | object[], required | In record order; at least one positional column. |
| column `name` | string, required | `[A-Za-z_][A-Za-z0-9_]*`, unique in the list; checks and `unique` read it. |
| column `kind` | string, required | `number`, `choice`, `toggle`, `text`, `anim`. |
| column `label`, `description`, `default`, `min`/`max`, `step`, `integer`, `choices` | | As a field's. `default` is what an empty field means (shown as the dropdown's placeholder; never written). |
| column `prefix` | string | Written before the value (`slot:`); the cell edits what follows. A field without it is a problem, kept as written. |
| column `named` | bool | Found by its prefix anywhere after the required fields, written last, left out when empty (`side:left`). Needs a prefix. |
| column `optional` | bool | May be left out at the end. A column after an optional one is optional too (noted). |
| record `checks` | `{ when, message }[]` | `when` is a visibleWhen rule over the row's columns (an empty one reads as its default); true puts `message` on the row. |
| record `unique` | string[] | Columns no two rows may share all of (empty read as default): "Same purpose and side as row 2". |

Loader and merge diagnostics follow step 2's levels (a bad column or check is Skipped, unknown members are Notes);
`ExtensionRegistry.For` also leaves out a list whose numbered keys would hold a deffile key or another extension's
field or list.

### Records and rows
- **Codec** (`Services/Extensions/RecordCodec`): fields split on commas, nothing escaped, values raw (backslashes
  literal), as weapon-tech splits them. A row nobody edits keeps its text byte for byte; an edited row is formatted
  again: positional columns, a trailing run of empty optional ones dropped, extra fields (past the last column, a
  trailing comma included) kept as written, named columns last.
- **Row problems**, never rewrites: an empty required column, a prefix missing, a bad number, choice or switch, a
  fraction in an integer column, an empty optional column before a set one (weapon-tech would read the next number in
  its place), extra non-empty fields, the manifest's checks and repeats on `unique`. The table's row shows the first
  row's problem and how many more rows have one; the Explorer counts a table with any bad row as one problem.
- **Numbering.** Rows are read in number order (`wtKick1`, `wtKick01`, `wtKick3`, `wtKick10`). Every edit writes
  the rows as keys 1..N in row order and removes the list's other keys (a gap, a trailing row, `wtKick01`), so an
  empty table removes them all and, through step 1, the block and the file when nothing else is left.
- **File order = row order.** A `.gdtx` sorts keys with digit runs compared as numbers (`ExtensionKeyComparer`; GDTs
  keep APE's order), so new keys and new blocks go in row order. A block whose numbered keys an edit touches and
  whose lines aren't in that order (hand-written) is rewritten in order, in place; blocks with odder text are spliced
  as they are. (Since the gdtx rules pass, below: never a block with comments or blank lines between keys, nor one
  whose untouched tables are out of order themselves; and reading never depends on file order.)
- **One property to the editor.** `RecordsPropertyViewModel.RawValue` is the rows each ended by a line break (a GDT
  value can't hold one, so an empty row still counts). Change tracking, the Changes list ("2 rows → 3 rows", "row 2:
  …"), Undo all, the journal (the keys are ordinary `xset` keys) and the filter work on it. A table edit is one undo
  step (`EditStep` of every key it changed); steps in one cell change one key and coalesce like a field's.
- **Inherited whole.** A derived asset with any row of its own has exactly its own rows; with none, its nearest
  ancestor's table, dim, with ↑. Editing an inherited table writes every row as its own; removing its last own row
  shows the parent's again (status line says so). weapon-tech's GDT compiler must read a list the same way.
- **New rows** copy the row above (kick sets and slot lines are mostly the previous row with a value changed); the
  first row starts from each column's default.

### The table
`RecordTableEditor` in a row of its own across the form (eight columns don't fit the value column): label line with
the key, "3 of 24", ⚠ and ↶ / ↑ for the whole table; a header; one row per record with its number, a cell editor per
column (the form's own: scrub number, switch, dropdown, xanim reference, text) and ⚠ ↑ ↓ ✕ in a reserved column
(nothing moves when a problem or the hover shows them); "+ Add row" at the end; an empty table says so on the line the
first row takes.

Keys: Tab walks cells, ↑↓ walk a column (falling through to the form at either end; a number being typed keeps them),
Ctrl+Enter adds a copy of the row below it, Alt+Delete removes the row, Alt+↑ / Alt+↓ move it, Enter commits and Esc
reverts a typed cell, Ctrl+Z undoes any of them. Focus follows the row. Undo revealing a row the keyboard is already
in leaves it there (`AssetEditorView.FocusRow`; true of every row now).

Rows are built once per list and kept in the tree: another weapon's table rebinds them, an added row shows a spare one,
a move moves values. Taking a row out of the tree and putting it back restyled its eight editors: switching tabs onto
a 24-row table took 650–800 ms in the harness that way, 16–23 ms now. A fresh editor view still builds a table's rows
once (about 100 ms for 24 rows, on top of the jump to it).

Hardening: below about 1600 px the cells' minimum widths (some 480–600 px for a kick table) pushed ⚠ ↑ ↓ ✕ past the
editor's edge. Each line is now the row number, a clipped canvas holding the cells grid (laid out at the room there
is, or at its minimum and translated by the scroll offset), and the row controls, so the number and ⚠ ↑ ↓ ✕ never
move; a horizontal bar beside "+ Add row" (`Visibility="Auto"`, on that line so it moves nothing; it runs on under the
row controls' column, and its thumb is never under 24 px), Shift+wheel, and focus entering a hidden cell scroll it.
Every switch's track sits 2 px in from its edge, so its focus ring is never cut by the switch's own clip or a cell's;
the square Fluent adorner is off for every switch, since it covered On/Off's edge ("On" read "Or"). Each cell editor is named "{column} row {n}" (`PropertyItemViewModel.
AccessibleName`, set on renumbering).

### The gaps
- **Rename and move.** `ExtensionSidecar.Rename` renames the asset's blocks with it (the save renames the block's
  header in place, so no orphan); a move takes them out of the old GDT's `.gdtx` (`Detach`: the save deletes them
  there) and into the new one's with the values they hold (`Add`, new blocks); moving back restores the originals in
  place. Undo of a move or paste (Ctrl+Z) and Discard all take them back. Journal: `ren` renames on replay, `mov`
  carries the values (`xprops`) and the old file's blocks are detached; pending `xset`s are kept by the asset they
  name, so a rename or move replayed after them still finds them.
- **Delete.** The asset's blocks leave the session's sidecar with it (`ExtensionSidecar.Removed`); the next save
  deletes them (backed up like any save), Discard all puts them back. A `del` replays the same.
- **Duplicate, Duplicate to…, copy and paste.** The copy gets the source's blocks' values as new blocks, unchanged
  (its baseline), so they don't count as edits; the `add` journal entry carries them (`xprops`). Derive copies none
  (the derived asset inherits).
- **Search** (`prop:`): also matches the asset's blocks of installed extensions (keys and values, as GDT values),
  from a snapshot taken on the UI thread; with none installed or loaded the matcher is the old one.
- **Table**: the picker offers the type's extension fields as columns (never a default column; record tables stay in
  the editor); cells read the asset's own block value else the default, write it as the editor does, and undo.
- **Compare**: extension fields and whole tables are rows after the GDT's keys, effective values as the editor shows
  them (own block, nearest ancestor's, default); ◀ writes into the base's block as one undo step.
- **Explorer ⚠**: `ExtensionRegistry.CountProblems` adds the asset's own blocks' problems to the deffile count.
- With nothing installed every one of these is the old code path (checked: compare rows, table columns and picker,
  search hits).

### Not done, and known limits
- Underive doesn't flatten extension values (the asset still inherits its old parent's tables until saved and
  re-derived). (Renaming onto an orphan block's name and a `.gdtx` reload with a delete pending were gaps here; see
  "Hardening (save path)" below.)
- weapon-tech must adopt: reading the `.gdtx`, `wtEnabled`, numbered lists in number order and inherited whole.

Tests: `Apex.Shots/RecordChecks.cs` (`--records`, and every full run): the codec (round trips, odd values, trailing
commas, backslashes, slot-line rules, repeats, key order), manifest tolerance and merge, the table on temp GDTs (cell
edits, add / move / remove / renumber, 24-row limit and file order, undo, Undo all, inheritance, empty tables and
files), the journal and problem count (mock app), rename / duplicate / copy / move / delete with saves and a restart,
search / table / compare with and without the extension, and the live app with real keys and mouse, screenshots
(64–67) and timings.

## As built: the preview-simulator host (step 4a, 2026-10-06)
The native module host, consent and diagnostics; no preview drew a module's output in this step (step 4b added it). Author-facing reference: `docs/extensions.md`. The ABI is `docs/plugin-abi/apex_sim.h`, version 1.

### Manifest (still `apexSchema: 1`)
`"simulator": { "module": "<relative path>.dll" }`. `ExtensionLoader.CheckModulePath` refuses (Skipped: the module is
left out, the fields load) an empty, absolute, rooted or drive-relative path, `..`/`.`/empty parts, parts ending in a
dot or space, `:` (streams), wildcards, anything not ending `.dll`, a path that resolves outside the folder, and any
existing part below the folder that is a reparse point (junction, symbolic link). A module that isn't there is a Note.
Only stats: no file is opened or hashed at startup. Unknown members of `simulator` are Notes. The result is
`ExtensionManifest.Simulator` (`SimulatorModuleRef(Module, FullPath, Folder)`).

### Host (`Services/Extensions/Simulation`)
- **Lazy.** `MainViewModel.Simulators` is made on first use. A module is touched only by `GetModuleAsync` /
  `CreateAsync`, which a preview calls with a request from `AssetEditorViewModel.SimulatorRequests()`; that returns
  nothing unless an extension with a module is on (`enabledBy`, own or inherited) for the asset. Startup, the save path
  and the journal never reach it.
- **Load.** Per extension, once a session: `CheckModulePath` again; SHA-256 streamed on the thread pool (64 MB cap);
  consent (below); the file opened with `FileShare.Read` (no writer, rename or delete), hashed again (a change since
  the question asks once more, then fails), then `LoadLibraryExW(fullPath, LOAD_LIBRARY_SEARCH_SYSTEM32)`: the full
  path only, its imports from System32 only (a module importing anything Windows doesn't supply is refused before the
  question, see `ModuleImage` and "Hardening" in `docs/extensions.md`), never PATH, the current directory, Apex's folder
  or its own. Then the six exports, `apex_sim_abi_version() == 1`, `apex_sim_info() == 0` with
  `id` equal to the manifest id (ordinal). Asks while one is under way share it.
- **Calls.** Function pointers (`delegate* unmanaged[Cdecl]`), structs laid out as the header with `size` set; every
  out buffer is zeroed and followed by 32 guard bytes, and a module that changes them is turned off before anything is
  read. Strings in: one native block per create (pairs, then NUL-terminated UTF-8), freed when create returns. Strings
  out: up to the first NUL or the field's end, control characters as spaces. Supported bits outside the four known are
  dropped; unsupported parts read as zero; a non-finite value is a failed step. `ads` is clamped to 0..1, a step with
  `dt <= 0` isn't sent.
- **Failures** are a result `Message` (a sentence for beside the preview) and, once per module, a `Reported`
  diagnostic (`ExtensionProblem.Skipped`), which the app shows in the same neutral banner as startup's extension
  problems ("The X extension: its preview module … so the preview is off. Editing and saving work as usual.", the
  Win32 error in the tooltip). Create returning NULL is the weapon's message, not a module failure.
  `MaxFailuresInARow` (10) failed calls in a row across a module's simulations (since hardening, a step over 50 ms, a
  create over a second and a call that changes the floating-point state count too), or a guard-byte write, turn the
  module off for the session (`TurnedOff`); its simulations then fail every step without calling it.
- **Threads.** The host remembers the thread that made it; every public call from another thread throws
  `InvalidOperationException` before touching a module. Continuations after the thread-pool hash need the UI thread's
  `SynchronizationContext` (Avalonia's dispatcher has one).
- **Crashes.** An access violation in a module can't be caught in .NET and ends Apex. `MainViewModel` passes
  `FlushSessionToDisk` as `beforeNativeCall`, run before each `LoadLibraryExW` (DllMain is the first native code) and,
  since hardening, before every create (an edit that crashes create is 150 ms old, the journal's delay is 250 ms), so
  the journal holds every edit made until then.
- **Unload.** A module is never freed during a session (not even one that failed a check after loading): freeing code
  that may still run is worse than keeping it. `SimulatorHost.Dispose` (from `MainViewModel.Dispose`, app exit)
  destroys every simulation still alive, then `FreeLibrary`s the modules. A simulation used after that, or after its own
  Dispose, fails its steps quietly.

### Consent
- `ISimulatorConsent.AskAsync(SimulatorConsentRequest(id, version, path, sha256, changed))` returns true (Load), false
  (Don't load) or null (no answer: the question was replaced by another; nothing remembered). The app's provider is
  the shared confirmation dialog (`AskConfirm` gained `cancelLabel`): title "Load X's preview module?" (or "X's
  changed preview module?"), body naming extension, version and path, and "It runs inside Apex with your permissions,
  so load it only if you trust where the extension came from. Editing and saving work either way." It opens on
  **Don't load**, as every confirmation opens on its safe button; Esc and a click outside are Don't load; Tab reaches
  Load. If another dialog is open it waits for it to close.
- `SimulatorConsentStore`: `extension-modules.dat` in `UiSettings.LocalFolder` (APEX_SETTINGS_DIR, else
  `%LocalAppData%\Apex`; hardening moved it out of `%AppData%\Apex`), DPAPI-sealed for the current user,
  `{ modules: [ { id, sha256, load, file, answeredUtc } ] }`, the last 8 answers per id, written whole and moved into
  place. Read on first use; missing or unreadable reads as no answers (so the user is asked: the safe direction). In
  runs that keep no files (the harness without APEX_SETTINGS_DIR) answers live for the session.
- **Reconsidering** a "Don't load": `SimulatorHost.ReconsiderAsync` forgets that answer and asks again (also retries
  a module that failed before it was loaded). The proposed place for it is the preview's own "preview is off" notice,
  with a single "Load module…" action, rather than any settings page; the preview step builds that notice from
  `StatusOf` / `MessageOf` (built in step 4b). A remembered decline is silent (no banner: it was the user's choice, and
  a banner every session would nag).

### Fixture and tests
`Apex.Shots/Fixtures/simulator`: `sim_fixture.c` (outputs are plain functions of keys and inputs; `fxMode` asks for
create-fail, step-fail, unsupported, overrun, create-overrun, nan, fpu, slow-step, slow-create), `build.ps1` (MSVC via
vswhere, `/Brepro`, no CRT, ~5 KB each: the module plus ABI 2, info ABI mismatch, wrong id, id with a space, no step
export, 32-bit, sibling-import and delay-load variants, and `sim-helper.dll`) and the committed `built/` DLLs with
`hashes.txt`. The first check fails, never skips, when the source or header changed since the DLLs were built.
`Apex.Shots/SimulatorChecks.cs` (`--simulator`, and every full run): manifest paths (14 refusals, a junction when read
and when loading), consent (first ask, Load and Don't load remembered, reconsider, changed hash, missing and corrupt
answers, no answer, shared asks), each load failure, create and step failures, turning off, exact steps, partial
support with a UTF-8 note, 1,000 steps and 1,000 simulations without growth, the thread guard, unloading, and the app
(lazy startup, `enabledBy`, row order, the prompt with real keys in both themes, shots 68–69, the journal flushed
before the first load, the banner).

### Not done
- No preview used a module yet, and "Load module…" had no button (both done in step 4b).
- The id check is exact (`weapon-tech` ≠ `Weapon-Tech`), unlike manifest id clashes, which ignore case.
- Windows keeps a loaded DLL from being overwritten, so a module author rebuilding while Apex runs must close Apex.
  Loading a private copy would avoid that (a module's imports resolve from System32 only, so nothing would break), at
  the cost of a copy per session to manage.
- A hostile module is out of scope by design: consent is the only gate (see `docs/extensions.md`, "What a module can do").

## As built: the weapon recoil preview (step 4b, 2026-10-06)
The preview half of step 4: a weapon an extension with a module is on for previews its first-person viewmodel through
APE's renderer, moved by the module. Author-facing behaviour: `docs/extensions.md`, "What the preview does with it".
The two "Not done" items of step 4a about the preview are done here.

### Which weapons, and where
- `MainViewModel.SyncWeaponPreview` gives the active tab a preview (`PreviewPaneViewModel.ForWeapon`, content
  `WeaponPreviewViewModel`) while `_env.IsAvailable && tab.IsWeapon && tab.SimulatorRequests().Count > 0`, and takes
  it away (disposing the simulation) when that stops holding: on tab activation, and 150 ms after an edit on a weapon
  an extension with a module targets (`AssetEditorViewModel.HasSimulatorExtension`; Ctrl+Z included).
  `AssetEditorViewModel.PreviewPane` became settable for this. Every other weapon (and mock mode) has no preview, as
  before: no subject "This asset", no module touched.
- It is the tab's own preview, so it docks, maximizes and pops out like an xmodel or xanim preview; no new layout.

### Weapon → viewmodel (`Services/Preview/ToolsGfx/WeaponPreviewSource`)
- `Resolve(weapon, resolve)`, UI thread: each key is the weapon's own value, else the nearest ancestor's
  (`ScanProperties`, so nothing is retained), else the deffile default: `gunModel`, `handModel`, `viewmodelTag`,
  `attachViewModel1..16` / `attachViewModelTag1..16` (one per tag, as `WeaponViewmodelLookup` does; now shared as
  `WeaponViewmodelLookup.FromValues`), `idleAnim`, `adsUpAnim`, `fireTime`, `adsTransInTime`, `adsTransOutTime`. The
  anims are the xanim records of those names. `WeaponViewmodelLookup`'s session index is never used here, so live edits
  (the weapon's, a parent's, an anim's) are seen; the plan's `Key` (all of the above as text) decides whether a later
  edit reloads anything.
- Missing pieces, each one sentence and no preview (and no module asked for): no `gunModel`, a `gunModel` that isn't a
  loaded xmodel, no `idleAnim`, an `idleAnim` that isn't a loaded xanim. No `adsUpAnim` (or one not loaded, or whose
  files can't be read): a note, and ADS keeps the hip pose. A `fireTime` that isn't a positive number reads 0.1 s.
- `Prepare` (worker) is the xanim preview's composition: `AnimPreviewSource.PrepareViewmodel` on the idle anim with the
  weapon given explicitly (new optional parameter; the lookup is skipped) and its `gunModel` as the chosen gun, so its
  attachments come with it; viewhands are `handModel`, else the xanim preview's session hands. The ADS clip is
  `LoadAuthoredClip` of the ADS anim. A renderer that can't draw (no ToolsGfx data, interop) is one line with the
  reason in its tooltip; this preview has no GL fallback.

### Hip, ADS and the mix (`AnimBlendClip`)
Hip is the idle anim at t = 0 (held: a breathing loop would keep the preview drawing). ADS is the ADS-up anim at t = 1
layered per bone, parent-relative, as the game's anim tree layers: a bone the ADS clip keys (`AnimPose.Drives`, new,
virtual, true by default; `XAnimClip` and `WorldSpaceClip` answer from their bone maps) is slerped/lerped from the idle
local to the ADS local by the weight, other bones keep the idle local, and the hierarchy is composed again. Found on the
real data: an94's `vm_an94_t7_ads_base_up` keys only `tag_view` and `tag_torso`, so mixing whole model-space poses put
the arms in their bind pose, off screen; layered, the sights centre (tag_weapon y ≈ 0). Weight 0 is the idle pose bit
for bit. The weight is the driver's `ads`; changing it calls `AnimPlaybackPose.Refresh` (new).

### The driver (`Services/Extensions/Simulation/RecoilDriver`)
Pure logic over an `ISimulation`. Trigger: a press fires at once if the fire time has passed, then every fire time
while held, the remainder carried (a held trigger keeps a backlog over a long frame, a fresh press doesn't); a tap is
one round; rounds never come faster than the fire time. Fire time: `wtFireTimeMs` when the weapon or an ancestor sets
it (`AssetEditorViewModel.SetExtensionValue`; weapon-tech's key named in Apex, by the user's decision), else `fireTime`.
ADS: linear over `adsTransInTime` / `adsTransOutTime` (0: at once). Steps: real dt clamped to 0.1 s, frames under
0.5 ms carried into the next. Frames are asked for only while the trigger is held or a tap is due, ADS moves, or an
output moved by 0.0001 or more within the last 0.25 s, and at most 10 s after the last input; a failed step is rest.

### The view model and the view
- Display frames are `TopLevel.RequestAnimationFrame` of the view showing it (as the anim preview's clock), asked for
  only while the driver needs them: at rest nothing ticks. The view attaches and detaches as the anim preview's does;
  the last detach, and keyboard focus leaving while Space is down, let go of the trigger.
- The simulation: the request's values keyed as text; a change destroys the simulation and asks the host again
  (`CreateAsync`). Each ask has a generation, and a result that arrives after a newer ask is disposed unused. The host's
  `StatusChanged` is followed (loaded by another preview's "Load module…": ask again; turned off: drop it and say so).
  "Load module…" is `ReconsiderAsync`, then a new ask.
- The line under the preview (two lines reserved): the module's state, else the step's note (`NoteOf`: the module's
  `note` as written, else "Not simulated: …" naming the parts left out), and the plan's notes. Controls: Fire (held:
  `Button.IsPressed`; Enter on it is one round), Hip | ADS, Reset, the round count. Keys in the preview: Space (down
  = trigger, repeats ignored, up = let go), A, R (`CommandScope.RecoilPreview`, also in the palette).
- `KickOf(frame)` keeps only the supported parts; an all-zero kick is passed as null.

### The renderer (`PreviewKick`, `PreviewSceneRenderer.Kick`, `ToolsGfxPreviewViewport.Kick`)
Null (the default) or all zeros: none of the new code runs and the frame is the old one (checked byte for byte).
Otherwise the camera (orbit or the view tag) is moved by `View`; every draw of the model and its attachments is placed
at `world · Gun · View` (the skybox's `PlacedItem`); the joint overlay moves the same way. `View` and `Gun` are rigid
transforms of model space from engine angles: rows forward, left (−right of BO3's `AngleVectors`), up; translation the
origin. The viewmodel's model space is the view at rest (the game puts its origin at the eye, axes along the view), so
`gun*` turns the gun about its own origin and `view*` turns camera and viewmodel together: the gun holds still on screen
and the world moves. Verified: pitch −5 turns +X up and the kicked camera's APE pitch is −5 (looking up); yaw +5 turns
+X towards +Y (left); with the fixture module (fxGain −3) the drawn view kick's forward gains +Z; in the real app with
weapon-tech the horizon drops while firing.

### Verification
- `Apex.Shots/RecoilChecks.cs` (`--recoil`, and every full run): signs and matrices; the driver (cadence, taps, early
  taps, clamps, ADS, rest, the 10 s cap, failed steps, reset); the layered mix (hip bit-exact, a torso-only ADS anim,
  halfway, a full ADS anim); resolution (inheritance, live parent and anim edits, every missing piece); the app on a
  temp install with the fixture module (no preview while off or for a plain weapon, no idleAnim, consent and Don't
  load with real keys, Load module… with a real click and a newer ask landing alone, no frames at rest, Space held and
  released, cadence, the frame reaching the viewport unchanged, A / R / Hip clicks, the Fire button held and Enter on
  it, edits remaking the simulation, notes and unsupported parts, create failure, turned off, switched off and back
  with Ctrl+Z, a derived weapon, a module that can't load), shots 90–93 in both themes; and on the real install an
  offscreen D3D11 frame of `wpn_t7_loot_ar_an94_view` with no kick, a kick of zeros and after a kick: byte-identical.
- End to end with the real `weapon_tech_sim.dll`, outside the repo (a scratch program driving the real app on a mirror
  of the install whose GDTs are copies; smg_charlie9's wt values on `ar_an94_zm` in a `.gdtx`): every step the preview
  sent, replayed into a fresh simulation, gave identical bits, and the viewport's kick was the frame's supported parts
  at every shot.

### Not done, and limits
- Only the first extension with a module that is on drives the preview.
- The idle anim is held at its first frame; ADS doesn't change the field of view (`adsZoomFov`); no sway, bob or fire
  anim plays. BO3's own kick isn't simulated (by design: only what the module owns).
- Whether the drawn motion matches the game frame for frame (the view kick on the world, the gun's pivot at the eye,
  the layered ADS pose, linear ADS timing) can only be confirmed against the game.

## As built (preview pass) (2026-10-06)
The preview half of the product pass on weapon-tech (the critique's "Recoil setup (1)" and "Preview/consent"): the
recoil preview became a place to read recoil, not a thumbnail. Author-facing behaviour: `docs/extensions.md`, "What
the preview does with it" and "Loading and consent". No setting was added.

### The spray overlay and readout
- **Measured** by `SprayTrace` (`Services/Extensions/Simulation`) from the driver's `Stepped` (dt, shots, frame): view
  angles only, only while the frame says `ViewAngles` is supported. Definitions (burst, shot peak, climb, drift,
  settle time, trail) are in its summary and in `docs/extensions.md`; settled = within `SettleDegrees` (0.05°) of rest
  for `SettleHoldSeconds` (0.1 s), timed from the start of the step that fired the last round, in the steps' own time.
  `Rest()` (the driver stopped asking for frames) ends an unsettled burst as "doesn't return". Fixed rings (512 dots,
  2,048 path points): a step allocates nothing (checked over 20,000 steps; ~0.4 µs a step).
- **Drawn** by `Controls/RecoilOverlay`, a plain control over the viewport, so the D3D11 frame is never touched:
  `RecoilRender` checks the offscreen frame byte-identical with the overlay drawn between frames, plain and kicked.
  Angles map through the first-person camera's focal length (`cg_fov` at 4:3, vertical fixed: the renderer's Hor+), so
  dots sit where those directions are at rest and scale with the pane. Hairlines are one device pixel on pixel centres.
  It redraws only on the trace's `Changed`: at rest nothing draws (checked: 0 draws over 5 frames). A 2 s burst draws in
  ~0.16 ms; the fullest overlay (2,048 points, 500 dots) ~1.9 ms, path points closer than a pixel skipped.
- **Readout**: Climb, Drift, Settles in, Shots under Fire (`WeaponPreviewViewModel`), texts made once and reused;
  without view kick one sentence takes the three measurements' place. Reset and a new simulation clear it.
- Colours: `OverlayTextBrush` (dots, reticle), `OverlayAccentBrush` (latest dot), new `OverlayTraceBrush` (path, ruler;
  Paper at 45%, the same in both themes as the render is dark in both), `ScrimBrush` (dot edge).

### No gun to draw
`WeaponPreviewSource.Resolve` returns the weapon's timing (`WeaponTiming`) with or without a plan, and its problems now
read "No gun to draw: …". The view model asks for the module either way: Fire, the overlay and the readout work, and the
reason sits on the render where the viewmodel's caption goes. (Before, a weapon without a gun asked for nothing.)

### Real size
`MainViewModel.HasRecoilPreview`; `MainWindow.ApplyLayout` hides the Inspector while the docked preview is a recoil
preview, so the preview row is the column (the Preview splitter has nothing to split). The same move as an xanim's
editor taking its preview: no new layout, no setting. ⧉ pops it out (the column is all Inspector again), ⤢ widens it.
The viewport keeps 160 px. Known cost: in a narrow column the view is tall and thin (the renderer is Hor+), so at hip
the gun sits at the right edge, partly cut; ADS is centred. ⤢ shows it whole.

### The question in place
- `ModuleQuestions` (the app's `ISimulatorConsent`) asks in every weapon preview watching for that extension
  (`Watch`, registered before the preview asks the host); `ModuleQuestion` holds the words and the answer. A question
  with no preview watching goes to the old confirmation dialog (`ConsentPrompt`, same words): nothing in the app asks
  that way, but the harness drives the host directly and a question must never be unanswerable. The `ISimulatorConsent`
  contract is unchanged; `SimulatorConsentRequest` gained `Size`, `ModifiedUtc` and `Previous` (`SimulatorModuleBuild`)
  at the end, and the answers file keeps size and write time (older answers read them as unknown).
- Safety kept: one gate (nothing loads before Load); answers by id and hash; a question is created only when the host
  asks and closed (off the screen) before its answer runs; withdrawn when every preview waiting closes, remembering
  nothing; Load on a closed question does nothing; all manifest and disk text through `ExtensionLoader.Displayable`.
- Keyboard: the card never takes focus; with the keyboard in the preview (or dropped there by Load module…, which
  disables itself while it asks) it lands on Don't load. Esc or Enter in the preview is Don't load; Tab reaches Load;
  an answer gives the keyboard back to the preview.

### Notes in the form's words
`ExtensionKeyText.ToLabels` (rules in `docs/extensions.md`) on the notice line, the module's `err` and step notes alike;
the tooltip keeps the text as written. Generic over any manifest. weapon-tech's own notes still say "BO3's own kick"
for the GDT's View Kick settings; that wording belongs to its module and is left to it.

Tests: `Apex.Shots/RecoilOverlayChecks.cs` (in `--recoil`): the definitions on scripted frames, the driver feeding the
trace (tap, held, ADS), drawing (where a dot lands, nothing at rest, cost), the broker and the words (changed, old
answers, withdrawn, stale Load), notes in labels; and in `RecoilChecks` the app on the temp install: the question in
place with real keys and no focus theft, Tab/Enter/Esc, no gun, ADS, no view kick, Reset, pane size, a changed module
(shots 94–102). `SimulatorChecks` covers the request's new members and the dialog fallback's words.

## Hardening (save path) (2026-10-06)
An adversarial review reproduced eight ways extension data could be lost, misplaced or silently changed when a save,
a reload or an undo goes wrong halfway. Each is now a check in `Apex.Shots/HardeningChecks.cs` (`--hardening`, and
every full run), run failing before its fix.

- **A save that writes one file of two.** A `.gdt` and its `.gdtx` can't be replaced in one transaction. The GDT went
  first, so a `.gdtx` that then couldn't be replaced (a reader without delete sharing) left the GDT renamed or holding
  a duplicate while the `.gdtx` was behind; the save then dropped the rename, copy or move from the session (its GDT
  had it), and a restart found the blocks orphaned and the copy without its data. **Decision: every `.gdtx` is
  replaced before any GDT** (`GdtSavePlanner.Plan` orders the requests), **and any file that doesn't end up as staged
  stops the rest** (a failed swap, and now also a failed check after the swap). So a partial save can only leave a
  `.gdtx` ahead of its GDT. That direction is safe: the rename, copy or move stays in the session and journal until its
  GDT saves, and replaying it finds the `.gdtx` as it now is: `ExtensionSidecar.Rename` keeps a block already under
  the new name when the asset brings none of that extension, and `AddReplayed` gives a copy or move the block the file
  already has under its name instead of making a second. Considered and not done: journaling the `.gdtx`'s own
  pending structure (an `xblk` entry: name on disk → current name, new blocks' values) so a GDT could safely go first.
  With the order fixed nothing reaches that state except a crash between two swaps, and that leaves the journal as it
  was before the save, which replays the same way. The banner names the files written ("Saved x.gdtx, but not all: …"),
  and a GDT counts as saved only when every file of it is.
- **Undo across a move.** An extension edit's undo step kept the GDT it was made in, so after a move redo wrote an
  orphan block into the old `.gdtx`, and undo used up the step there leaving the moved value. `ExtensionTarget` now
  holds the lookup (`GdtOf`) and the GDT is resolved when the step runs.
- **Names an orphan block holds.** Duplicate, copy and paste skip a name any `.gdtx` still has data under (`FreeName`).
  Rename to such a name says so once in the dialog ("x.gdtx has extension data named … that belongs to no asset. Press
  Enter again to rename; this asset's own data replaces it."); a second Enter (or Rename) goes ahead. Where the asset
  brings a block of that extension, the orphan's is replaced (deleted by the save, backed up; Discard all puts it back);
  where it brings none, the orphan's becomes its own, as a build reading the file by name would see it anyway.
  `ExtensionSidecar.Find` prefers the block that came with the asset over one the file had under the name.
- **A `.gdtx` reload with a delete or move pending** matched only live blocks, so the deleted blocks came back as new.
  `GdtLoader.ApplySidecar` now rebinds the removed blocks (they stay removed, still holding what Apex read, so a change
  to one on disk is a "changed before delete" conflict); one the file no longer has leaves nothing to delete. `Detach`
  takes that baseline when it takes a saved block out.
- **A key written twice in a block** was collapsed to one line when an edit sorted the block. Such a block is now
  spliced as it is.
- **A removal killed after moving the file aside** left only `x.gdtx.apex-tmp`. Loading puts it back
  (`GdtSaveService.RecoverRemovedSidecar`, under `WriteGuard`) and the startup banner says so; the journal re-applies
  the edits that emptied it. With both files there the `.gdtx` is read and the next save deletes the temp file.
- **A GDT arriving after its `.gdtx`** (a pull delivering them in two watcher bursts) never read it. A GDT the watcher
  brings in reads its `.gdtx` if one is there, and a `.gdtx` another program created before a save is a "changed on
  disk" conflict (the dialog's Save reads it in and merges) instead of a banner with nothing to do.
- **Not changed, by policy:** a GDT deleted on disk leaves the session with its extension data, unsaved edits included,
  as its GDT values do (`docs/extensions.md`).

Known limits: Discard all after a partial save puts the session back to the files, which then disagree (the `.gdtx`
renamed, its GDT not): the blocks are kept as orphans, nothing is deleted. A crash after a save's swaps but before its
journal is rewritten replays operations the files already hold (true of GDTs before extensions too). The orphan banner
is computed before the journal replays, so it can list a block a replayed rename or copy then claims.

## As built (editor pass) (2026-10-06)
A product pass on what a modder meets after turning weapon-tech on (a wall of comma strings, a kick table typed cell by
cell, the extension the last of forty sections, a save line that didn't name the file it wrote). Every word is the
manifest's; Apex hardcodes nothing of weapon-tech. No setting was added. Author reference: `docs/extensions.md`
("Values in parts" to "Export"); this section records how it is built and why.

### Manifest (still `apexSchema: 1`)
New members, each optional, each noted and ignored by an older Apex: top-level `title`, `offNotice`, `notice`,
`export { format, header, command }`; section `collapsedUnlessSet`, `notice`; field `parts`; record list `combine`;
choice entries `{ value, label }`; `refType: "weapon"`. Loader rules (`ExtensionLoader`):
- A mistake leaves that member out and never hides a stored value: one bad part leaves out all parts (the field is one
  text box, so no value can show under another part's name); a bad combine group leaves its columns one by one; a bad
  export (format other than `ini-section`, no header, no command) is left out (Skipped); a bad title or notice is a
  Note; `offNotice` without `enabledBy` is a Note.
- `parts` only on a `text` field (else a Note); part kinds `number`, `choice`, `toggle`, `text`; a part default with a
  comma is dropped. Parts are `ExtensionField.Parts`, plain `PropertyDef`s (Key = the part's name).
- Labelled choices: `PropertyDef.ChoiceLabels` parallel to `Choices` (null: none); `ChoiceLabel(value)` gives the label,
  or `custom: x`. A value listed twice is now noted and the first kept (before, both were offered).
- `combine`: `ExtensionRecordList.Combines` (`RecordCombine`: a choice def whose values are the columns' values joined
  by commas, which a record field can't hold, so splitting is exact) and `DisplayColumns` (what the table shows: a
  stored column, or a group in its first column's place). Without combine, `DisplayColumns` is one per column with the
  column's own def, so every table is as before.

### Values in parts (`PartsCodec`, `PartsPropertyViewModel`)
- One property to the editor: the row's `RawValue` is the stored value, so change tracking, undo and redo, revert,
  provenance, Undo all, the journal (an `xset` of the one key) and inheritance are the field's, unchanged.
- Each part is an editor from `AssetEditorViewModel.Create` over the part's def (the form's own scrub number, switch,
  dropdown, text). Its baseline and parent value are the field's split (`OnMarksChanged`), so ↶ and ↑ beside a part act
  on that part, and the bar marks the part changed.
- Writing: `PartsCodec.Set` replaces field *n* only; every other byte is kept (spaces, empty fields, extras, a trailing
  comma). Gaps before a part past the end are filled with defaults, else empty; a part emptied with nothing set after it
  ends the value there; the same number spelled differently is no edit; a comma is refused on the part.
- Problems (`PartsCodec.Problems`): none for an unset value; per part its kind, range, wholeness, choices, and "empty"
  for a part without a default; extras said and kept. The Explorer's ⚠ count reads the same (`ExtensionRegistry`).
- The form: label line as a table's (↶ ↑ ⚠ for the value), then a 30 px row per part (`ItemsControl.partlist`);
  `AssetEditorView.RowOf` maps a part's editor to the field, ↑↓ walk parts then the form, `HeightOf` knows the height.

### Labelled choices and combined columns
- `ChoiceBox` binds labelled options as `ChoiceItem(Value, Label)` objects (`ToString` is the label) rather than
  strings, so a recycled dropdown moved to a row with the same values and other labels shows the new labels; a pick
  writes the item's value. Unlabelled choices are bound as strings, as before.
- A combined cell's value is the choice its columns match (`RecordCodec.DisplayCell`: trimmed, empty read as the
  column's default, numbers by value), else the fields as written (custom). Reading never rewrites; a pick writes each
  column's field (the row formatted as any cell edit); picking the matched choice writes nothing.
- Old record checks run on the fixture without `combine` (`PlainTablesDir`): a table per stored column is still what a
  manifest without combine gets, and those checks test it.

### Paste and copy
- `RecordCodec.ParseRows`: one record per non-blank line; tabs split cells (trimmed, joined by commas); a line that is a
  key of the list with its record (`"wtKick1" "…"`, `wtKick1 = …`) gives the record. `RecordsPropertyViewModel.Paste`
  replaces from a row and adds past the last (spreadsheet paste), cut at `max`, as one `Write`, so one undo step.
- `RecordTableEditor`: Ctrl+V (a cell: from its row or the first marked row; + Add row: append), Shift+↑↓ marks rows
  (`RecordRowViewModel.IsSelected`, `Grid.rrow.selected`), Ctrl+C copies marked rows or the row as stored (a TextBox's
  own selection is copied as text), Esc, a plain ↑↓ or a click unmarks. The clipboard is the shell's
  (`IShellView.GetClipboardTextAsync`, `CopyText`); a single bare value goes to the text field's own paste.
- Found on the way: showing a spare table row again rebuilt and restyled all of its editors, because `Sync` unbound
  spares; a 24-row paste took ~200 ms to lay out. Spares now keep the row they last showed (hidden), and `Load` keeps
  existing row view models, adding or removing only the tail: the same paste is ~6 ms to its rows laid out (median).

### Folding, the filter, the rail
- `CategoryViewModel`: `IsCollapsible` (the section's `collapsedUnlessSet`), `IsCollapsed` over the existing
  `IsExpanded` (the xanim panel's card state; a section is one or the other), `SetCount` / `SetText`. A row holds a
  value when the asset's block holds its key (a table: any row) or `Inherited` has it. The tab sets the state when it
  opens, `RecountSet` after each extension edit (the section of the row) and parent refresh; 0 → some opens it
  (`SetExpanded`, not the user's toggle). The header's toggle is the user's (`ExpandedChanged` → `RefreshVisible`).
  Session state of the tab only.
- `RefreshVisible` leaves a folded section's rows out only in the All view with no filter. `RevealProperty` and the rail
  open a folded section first.
- The filter: `CategoryViewModel.TitleMatches` (extension sections only, so no core behaviour changes): the query in
  the title, or in the id (as written and with `-`/`_` as spaces) or the manifest's title, passes every row of the
  section (`Shows`, used where an edit decides whether its row still matches). Parts and columns match by label and
  name (`MatchesMore`).
- The rail lists an extension's sections first (the form keeps them after the deffile's, as documented).

### The off line, notices, export
- `ExtensionOffRowViewModel` and `ExtensionNoticeRowViewModel` are form rows (FlatRows) with places in `_rowOrder`
  (`PlaceExtensionRows`): the off line before every section (All view), a notice right after its header while the
  extension is on (`ExtensionRows.IsOn`, kept by `EvaluateExtensionRules`). Turn on sets the switch's `RawValue` (the
  ordinary edit path, one undo step) and reveals it.
- The first section's header carries the export button (its label a TextBlock: a string content read the underscore
  in `weapon_tech.cfg` as an access key). `ExtensionExporter.Write` builds the text; `MainViewModel.CopyExtensionExport`
  copies it and says how many values went. **Decision: effective values** (own, else the nearest ancestor's), because
  the user pastes a section for one weapon into a file read per weapon, and a derived weapon's section must say what the
  weapon has; defaults and the `enabledBy` key are left out (weapon-tech logs unknown keys, and the section is the on).
  The palette offers it under the manifest's command while the extension is on.

### Many assets at once
- `MainViewModel.ExtensionPaletteItems`: for each manifest with `enabledBy`, "Turn on/off <title> for N assets" when the
  query matches and N > 0, over `BulkSelection()`: the open table's checked rows, else the Explorer's selection (the
  Explorer view hands the VM a function, `ExplorerSelection`, read only when a palette query could match, never pushed
  per selection change). N counts assets whose switch in effect would change.
- `SetExtensionOn`: candidates sorted by depth (parents first) and re-read as it goes; an asset whose effective value is
  already right is skipped; one that would read the wanted value without its own drops its own, else it is written.
  Each write is a step on its record's history sharing one batch token; with the table open the batch joins the
  table's undo (`TableViewModel.RecordExternal`), else a placement step undoes and redoes each record's step while it
  is still that record's newest (as the table's own batches do). A save clears placement steps, as for paste and move;
  each record's history keeps its step.
- Explorer selection: a multiple-selection list (Ctrl+click, Shift+click); `SelectedAssets` keeps asset rows only. The
  table: its rows' check boxes and the header's all box.

### Status lines and tooltips
- Save: with any `.gdtx` written the files are named (`Saved ar_x.gdt and ar_x.gdtx`, `Saved ar_x.gdtx`); more than
  two files count GDTs and .gdtx files; without a `.gdtx` the line is the old one.
- Undo and redo of a table or a value in parts say what changed (`PropertyItemViewModel.DescribeStep`): "Undid Kick
  patterns row 1", "rows 1 and 2", "adding row 3 to …", "removing row 1 from …", "… from 3 rows to 5 rows", "Undid Spring:
  view, hip: Max drift". Taken when every change of the step is one row's.
- Apex's own tooltip line for an extension row names the key and where it is saved ("Saved as wtKick1, wtKick2… in the
  .gdtx beside the GDT"); claims about the game or a compiler are only ever the manifest's description.

### Verification
`Apex.Shots/EditorPassChecks.cs` and `EditorPassUiChecks.cs` (`--editor-pass`, and every full run). Pure: the parts codec
(round trips, gaps, extras, trailing commas, problems, words), the loader (the fixture uses every member and loads
clean; each mistake at its level; older manifests). Editor on temp GDTs: parts (bytes kept, part edits, undo/redo
words, part revert, comma refusal, inheritance and ↑), labelled choices with a custom stored value, combine both ways,
paste (all line forms, replace and add, max, one undo, problems) and copy, undo words for move/add/remove, folding
(starts folded, inherited values, opens on a value, filter, rail, reveal), the filter by title and id, the off line,
notices, the export (golden). The app on mock data: the journal keeps parts and pasted rows across a restart. The app on
a temp install (25 weapons: three families with variants, the AK, eight stock weapons) with real keys and clicks: Turn
on, parts with Ctrl+↑ and ↑↓, Recoil from offering only weapons, the combined dropdown with F4 ↓ Enter, Ctrl+V and its
undo, Shift+↓ Ctrl+C, paste on + Add row, the folded header by click and Space, the export button and palette, save
status lines, turning weapon-tech on for 25 from the Explorer and the table with inheritance, shots 96 (dark and light).

Timings (headless harness, Release): a part's Ctrl+↑ 2.1 ms median; turning it on for 25 weapons (12 written) 3 ms
median on the UI thread, 15–22 ms the first time; a 24-row paste into a 3-row table ~6 ms to its rows laid out (was
~200 ms before spare rows kept their rows); pasting 22 rows into a table no view shows 1.1 ms; opening the AK with the
fixture 138–150 ms median (its editor alone, a 1,300-row bulletweapon).

### Not done, and decisions left to the user
- A column that should not show (weapon-tech's pitch scale, which IW8 doesn't use) has no member: dropping it from the
  manifest leaves stored values as an extra field (kept, flagged). Make it `optional` with a default, or weapon-tech
  reads seven fields; a `hidden` column member would need a reason a fold can't serve. (Superseded: the gdtx rules
  pass added `hidden`, below.)
- Parts on a record column: impossible by construction (a record's fields are split on commas).
- Undo of a bulk on/off from the Explorer lasts until the next save (as paste and move do); after it, each asset's own
  Ctrl+Z still takes back its write.
- The rail lists an extension's sections first while the form keeps them last; the other reading of "near the top"
  (moving them in the form) would change the documented section order.

## As built (gdtx rules and manifest gaps) (2026-10-06)
Closing what the weapontech linker brief (`docs/linker-handoff/`) and weapon-tech's manifest author found: the `.gdtx`
read rules made single and stated, and two small manifest members. Author reference: `docs/extensions.md`
("Record lists", "Hidden columns", "Where values live"). No setting was added; `apexSchema` stays 1.

### Numbered keys: one order
- Before, `RecordCodec.Rows` broke a tie between two spellings of one number by ordinal key (`wtKick01` before
  `wtKick1`), while the docs and the save comparer (`ExtensionKeyComparer`) put the shorter first. **Rule now: by
  number, then fewer digits first; never by position.** File order was the other candidate and was rejected: a block's
  dictionary isn't in file order once an undo has put a removed key back (a freed slot is reused), and the splicer
  writes a restored `wtKick01` at its sorted place, so a file-order rule would reorder rows across an undo and a save.
  The rule is what `ExtensionKeyComparer` writes, so a file Apex wrote reads back in its file order.
- Gaps close up on read (0, 1, 3 are three rows); nothing is rewritten by reading. An edit of a table writes it 1..N
  in that order and removes its other spellings, as before.
- **An untouched table stays as written.** The splicer's sorted rewrite (`NeedsSorting`) now also needs the block's
  other keys, those of tables the edit doesn't touch included, to be in order already, so a hand-written `grTail01`
  before `grTail1` is never moved by an edit of another table or field; and it is skipped for a block with a comment
  or blank line between keys (the rewrite would drop them).

### Comments
- Before, a `//` line inside a block ended Apex's read there (every later key invisible), one between blocks holding
  `"` or `}` misread or ended the file, and a save into such a block was refused. **Rule now: `//` outside a quoted
  string, wherever whitespace may be, is a comment to the end of its line (LF), skipped by the indexer, the save
  path's layout, the body parser and the property reader alike** (`GdtParser.SkipWhitespace`, `IsComment`,
  `SkipComment`), in GDTs and `.gdtx` files. It is the comment Apex's data-layer GDT reader
  (`Apex.Render.Data.Gdt.GdtFile`) already skipped; APE writes none, and no GDT under the install's `source_data`,
  `xanim_export` or `model_export` contains `//` at all (checked), so no stock file reads differently. Whether APE itself accepts one wasn't checked (APE isn't launched by
  the tests): Apex only keeps comments a person wrote, never writes one.
- Not comments: `/* */`, `#`, `;`, a lone `/`. Other text in a body still ends the read of that block (as before), and
  a save into it is refused ("…hold text that isn't a "key" "value" pair or a // comment, so Apex can't change them
  safely. Edit that block in a text editor."). Flagging such blocks on load was not done.
- Saving: values change between their quotes; a removed key whose line holds a trailing comment loses the pair and
  keeps the comment; new keys go after the last key's line (after its comment); a comment between a key and its value
  survives a value change, and removing that key is refused, naming the key ("Apex can't remove wtKick1 without the
  comment between it and its value. Move the comment off that line in a text editor.").

### Manifest: `hidden` and `after`
- Column `hidden` (needs `default`, else noted and shown): `RecordColumn.Hidden`, left out of `DisplayColumns`, so
  the table, its header, Tab order and accessible names never see it. `RecordCodec.FillHidden` writes the default:
  every hidden column in a new row (`Insert`), only a required empty one in an edited (`CellEdited`) or pasted
  (`Paste`) row. A stored or pasted value is kept as written. Problems name it ("…The table doesn't show it; changing
  any cell of the row writes 1."). A `combine` naming one is left out.
- List `after: "<field key>"`: `ExtensionRecordList.After`; `ExtensionSection.Items()` is the section's order (each
  field, then the lists placed after it; then the rest), used by the form and the export (the simulator's request
  follows the form). Not a field of the section: noted, the list goes last; a field the merge leaves out for a type:
  the list goes last. Chosen over an ordered `items` array, which would duplicate `fields`/`records` and need rules for
  a manifest giving both.
- Not done, by decision: per-group notices, choice-conditional fields.

### Verification
`Apex.Shots/GdtxGrammarChecks.cs`: the order (both insertion orders, against the comparer), comments read by the
indexer, layout, parser and data-layer reader, only `//`, splicing around comments (.gdtx and .gdt), the refusals'
words (in `--save` and every full run); the manifest members, their mistakes, the editor on a temp GDT (form order,
cells, ⚠, filter, a field's edit leaving tables as written, cell edits, new rows, paste, 1..N on edit, the export),
and the app on a temp install with real keys (↓ through the table between the fields, Ctrl+Enter, Ctrl+↑, Ctrl+V on
+ Add row, shots 103 in both themes) (in `--editor-pass` and every full run). The linker brief's vectors 03 and 15
were regenerated (`docs/linker-handoff/`).
