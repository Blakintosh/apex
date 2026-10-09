# Writing an Apex extension

An extension adds fields, tables and sections to asset types Apex already edits (a weapon's recoil settings, say),
and can ship one native module that drives the weapon preview. It is a folder with a manifest; Apex ships none.

This is the reference for extension authors. The design record, with the reasons behind each rule, is
`docs/schema-extensions-design.md`.

## The folder

```
%AppData%\Apex\extensions\
  weapon-tech\
    extension.json        the manifest (required)
    weapon_tech_sim.dll   the preview module (optional)
```

One folder per extension, named for its id. Apex reads every `extension.json` under that folder when it starts, off
the UI thread, and never writes there. `APEX_EXTENSIONS_DIR` points Apex at another folder (for testing an extension
without installing it).

## The manifest

JSON, with `//` comments and trailing commas allowed. At most 1 MB.

```jsonc
{
  "apexSchema": 1,
  "id": "weapon-tech",
  "version": "0.4.0",
  "targets": ["weapon"],
  "enabledBy": "wtEnabled",
  "simulator": { "module": "weapon_tech_sim.dll" },
  "sections": [
    {
      "title": "Kick",
      "visibleWhen": "wtRecoil",
      "fields": [
        { "key": "wtRecoil", "kind": "toggle", "label": "Recoil", "default": 1 },
        { "key": "wtFireTimeMs", "kind": "number", "label": "Fire time (ms)", "default": 100, "integer": true }
      ],
      "records": [
        {
          "key": "wtKick#", "label": "Kick", "max": 24,
          "columns": [
            { "name": "pitch", "kind": "number" },
            { "name": "yaw", "kind": "number", "default": 0, "optional": true }
          ]
        }
      ]
    }
  ]
}
```

### Top level

| Member | Type | Meaning |
|---|---|---|
| `apexSchema` | number, required | `1`. Anything else leaves the extension out. |
| `id` | string, required | `[A-Za-z0-9][A-Za-z0-9_.-]{0,63}`. Written into every `.gdtx` block, so it is permanent: never rename it. |
| `version` | string | Shown in tooltips and in the module prompt. |
| `targets` | string[], required | Asset types as the GDTs name them (`bulletweapon`). `weapon` means every weapon type (bulletweapon, projectileweapon, dualwieldweapon, grenadeweapon…), but not weaponcamo or sharedweaponsounds. |
| `enabledBy` | string | A key that turns the extension on per asset (see below). |
| `title` | string | The extension's name in Apex's own sentences ("Turn on Weapon tech for 25 assets"). Default: the id. |
| `offNotice` | string | One line at the top of a form whose asset has the extension off, with Turn on beside it. Needs `enabledBy`. |
| `notice` | string | One line under the extension's first section header while it is on (and, with a `consumer`, while that tool isn't installed). |
| `export` | object | Copy an asset's values out as text (see "Export"). |
| `consumer` | object | A file in the install that carries a marker while the tool reading the values is installed; the `notice` goes while it does (see "Notices"). |
| `simulator` | object | The preview module (see "The preview module"). |
| `sections` | object[], required | In order, after the deffile's sections and before "Other". The rail lists them first. |

### Sections and fields

| Member | Type | Meaning |
|---|---|---|
| section `title` | string, required | The header and rail entry. |
| section `visibleWhen` | string | A rule; false hides the section's rows. |
| section `fields` | object[] | Flat fields, one key each. |
| section `records` | object[] | Tables over numbered keys (see "Record lists"). They follow the section's fields unless one names a field to follow (`after`). |
| section `collapsedUnlessSet` | bool | Starts folded to its header unless one of its rows holds a value (see "Folding sections"). |
| section `notice` | string | One line under the section's header while the extension is on. |
| field `key` | string, required | `[A-Za-z_][A-Za-z0-9_]{0,127}`, unique in the manifest. A key the asset type's deffile declares is refused: that value belongs in the GDT. |
| field `kind` | string, required | `number`, `toggle` (0/1), `choice`, `text`, `assetRef` (needs `refType`), `anim` (an xanim). |
| field `label`, `description` | string | Row label (default: the key) and tooltip. |
| field `default` | string, number, true/false | What a missing key means. Never written. |
| field `min`, `max` | number | Both or neither; out of range shows a warning, never clamps. |
| field `step` | number | The scrub and arrow-key step. |
| field `integer` | bool | Whole numbers only. |
| field `choices` | array | For `choice`: values, or `{ "value", "label" }` objects (see "Labelled choices"). |
| field `refType` | string | For `assetRef`: an asset type, or `weapon` for any weapon type (see "Weapon references"). |
| field `visibleWhen` | string | A rule; false hides the row. |
| field `parts` | object[] | For `text`: the named parts of a comma-joined value, a row each (see "Values in parts"). |

### Record lists

A list is a table whose rows are numbered keys: `wtKick#` is `wtKick1`, `wtKick2`… Each row is a record of
comma-separated fields; nothing is escaped and values are raw (backslashes literal).

| Member | Meaning |
|---|---|
| `key` | The pattern, ending in `#`. |
| `label`, `description`, `visibleWhen` | As a field's. |
| `max` | Most rows. |
| `after` | The key of a field in the same section: the table sits right after that field in the form (and the export). Without it, or naming no field of the section (noted), the table follows the section's fields. |
| `columns` | In record order. Each has `name` (what rules read), `kind` (`number`, `choice`, `toggle`, `text`, `anim`) and a field's `label`, `description`, `default`, `min`/`max`, `step`, `integer`, `choices`; plus `prefix` (written before the value, `slot:`), `named` (found by its prefix anywhere after the positional fields, written last, left out when empty), `optional` (may be left out at the end) and `hidden` (never shown; see "Hidden columns"). |
| `checks` | `{ "when": rule, "message": text }`: a rule over the row's columns; true puts the message on the row. |
| `unique` | Columns no two rows may share all of. |
| `combine` | Groups of columns shown as one labelled choice (see "Combined columns"). |

**Row order.** A key is row *n* of a list when it is the list's stem (any case) followed only by digits:
`wtKick0`, `wtKick7`, `wtKick007` are rows 0, 7 and 7; `wtKick1x` is no row. Rows are ordered by their number;
one number spelled twice (`wtKick1` and `wtKick01`) puts the spelling with fewer digits first (`wtKick1`,
`wtKick01`, `wtKick001`). Where a key sits in the file never matters. Gaps close up when read: rows 0, 1, 3 are three
rows, shown as 1–3. Reading never rewrites anything; an edit of the table writes its rows back as 1..N in row order
and removes the list's other keys (a gap, `wtKick01`), and leaves every other table and key of the block as it is.
A table is inherited whole: an asset with any row of its own has exactly its own rows. Your tool must read a list
the same way.

```jsonc
{ "title": "Kick",
  "fields": [ { "key": "wtFireTimeMs", "kind": "number" }, { "key": "wtKickReturn", "kind": "toggle" } ],
  "records": [ { "key": "wtKick#", "label": "Kick patterns", "after": "wtFireTimeMs", "columns": [ /* … */ ] } ] }
// The form: Fire time, Kick patterns, Kick return.
```

### Hidden columns

A column the record format needs but nobody edits (weapon-tech's pitch scale, parsed and unused) can be
`"hidden": true`. It needs a `default`, which is what it holds in a new row.

```jsonc
{ "name": "pitchScale", "kind": "number", "label": "Pitch scale", "default": 1, "hidden": true }
```

- The table has no cell for it and the filter doesn't find the table by its name; it is still stored, read by rules,
  `checks` and `unique`, copied by Ctrl+C, and exported.
- **New rows** (+ Add row, Ctrl+Enter's copy of the row above, the first row) hold its default, whatever the row they
  copy holds.
- **Stored and pasted rows keep theirs** as written, byte for byte, through any edit of another cell. A pasted row
  with another value keeps that value.
- **A row without it** (a field the record format needs, left out): a stored row says so on its ⚠ ("Pitch scale is
  empty. The table doesn't show it; changing any cell of the row writes 1.") and gets the default with its next edit;
  a pasted row gets it as it is pasted. An `optional` hidden column left out stays out.
- Without a `default` the column is noted and shown, and a `combine` can't name a hidden column (the group is left
  out): a mistake never hides a stored value.

In a table, Ctrl+V pastes rows and Ctrl+C copies them (see "Pasting and copying rows").

### Values in parts

A text field whose one value is several numbers or words joined by commas (`accel,retAccelScale,retSpeedCurveScale,
maxPitch,maxYaw`) can name them. The editor then shows a row per part under the field's label, each with the form's own
editor for its kind, while the key and its stored value stay one: undo, ↶ (this session's change) and ↑ (the parent's
value) on the label line act on the whole value, the journal and the `.gdtx` hold the one key, and a derived asset
inherits the whole value or holds its own.

```jsonc
{ "key": "wtSpringViewHip", "kind": "text", "label": "Spring: view, hip",
  "description": "How the view springs back from kick while firing from the hip.",
  "parts": [
    { "name": "accel", "label": "Stiffness", "kind": "number", "min": 0, "max": 1000 },
    { "name": "retAccelScale", "label": "Return speed", "kind": "number" },
    { "name": "retSpeedCurveScale", "label": "Return curve", "kind": "number" },
    { "name": "maxPitch", "label": "Max climb", "kind": "number" },
    { "name": "maxYaw", "label": "Max drift", "kind": "number" }
  ] }
```

| Part member | Type | Meaning |
|---|---|---|
| `name` | string, required | `[A-Za-z_][A-Za-z0-9_]{0,127}`, unique in the field. Apex shows it beside the label and the filter finds it. |
| `kind` | string, required | `number`, `choice`, `toggle` (0/1) or `text`. |
| `label`, `description` | string | The part's row label (default: the name) and its tooltip. |
| `default` | string, number, true/false | What a missing or empty part means. Never written, except to fill a gap (below). No commas. |
| `min`/`max`, `step`, `integer` | | As a number field's (`number` only). |
| `choices` | array | As a choice field's (`choice` only), labels included. |

- **The field's `kind` must be `text`** (an Apex from before parts shows the value in one text box, as stored). Parts
  on another kind are noted and ignored.
- **One bad part leaves out all of them**: a missing name, a name twice, a kind Apex doesn't know. The field is then
  one text box, so no part can ever be shown under another's name. Unknown members of a part are noted and ignored.
- **The value as stored is the truth.** It is split on every comma (nothing is escaped). Part *n* is field *n*,
  trimmed for its editor (`" 1"` is a switch that is on). A part with no field is shown empty.
- **An untouched value is written back byte for byte**: spaces, empty fields, a trailing comma, fields past the last
  part. Only an edit rewrites, and only the part edited: `" 30, 1.5 ,,4"` with Return speed set to 2 is
  `" 30,2,,4"`.
- **Edits at the end.** A part past the value's end fills the parts between with their defaults, else empty (`"30,1"`,
  Max climb 9: `"30,1,,9"`). A part emptied with nothing set after it ends the value there (`"1,2,3"`, the last part
  emptied: `"1,2"`); one emptied in the middle keeps its place (`"1,,3"`). The same number spelled differently is no
  edit. A part can't hold a comma.
- **Pasting the value.** A comma-separated list typed or pasted into any part fills the parts (it is how a modder pastes
  the spring line from a cfg). A list with as many fields as there are parts is the whole value and lands from the first
  part, whichever part it went into; a shorter list fills on from the part it went into (`"7,8"` into Return speed sets
  Return speed and Return curve). It is one undo step, and each field is trimmed and checked like a typed part (a value
  outside its range is flagged, not refused). A list that does not fit (more fields than parts, or running past the
  last part) changes nothing and says so on that part. In a number part a comma list is read as a list, not as one
  number with its commas dropped; a number written with thousands separators (`1,234`) is still one number.
- **Problems.** An unset value (empty) has none. Otherwise each part is checked as its kind (a number, its range, a
  whole number, one of its choices), a part that is missing or empty with no `default` is "Stiffness is empty.", and
  non-empty fields past the last part are said and kept. The row's ⚠ shows the first and how many more; each part shows
  its own; the Explorer counts the field as one problem.
- Undo, the status line and the Inspector's changes say which part changed ("Undid Spring: view, hip: Max drift",
  "Stiffness: 30 → 31"), never the stored text. ↑↓ walk the parts, then on to the next row.
- A record column can't have parts: a record's fields are already split on commas, so a column holds none.

### Labelled choices

A choice (a field, a part or a column) can show words while storing a value:

```jsonc
"choices": [ { "value": "0", "label": "Kick" }, { "value": "1", "label": "Hold, slow" }, "2" ]
```

- `value` is a string or a number (written as the manifest spells it); `label` is shown. A plain entry in a labelled
  list is its own label. Values are compared without case; a value listed twice is noted, the first kept.
- A stored value the list lacks is kept, shown as `custom: <value>`, and is a ⚠ as before; picking from the list
  replaces it.
- The dropdown's placeholder (an empty optional column) shows the default's label.
- An Apex from before labels notes the objects and offers the list without them: list every value plainly if your
  extension must work there.

### Combined columns

Two or more stored columns that together mean one thing (an `ads` and a `gun` switch that make four modes) can show as
one labelled choice. They are stored exactly as before; only the table changes.

```jsonc
{ "key": "wtKick#", "label": "Kick patterns", "max": 24,
  "columns": [
    { "name": "ads", "kind": "toggle" }, { "name": "gun", "kind": "toggle" },
    { "name": "bullet", "kind": "number", "integer": true }
  ],
  "combine": [
    { "label": "Applies to", "description": "Which kick this set drives.", "columns": ["ads", "gun"],
      "choices": [
        { "value": ["0", "0"], "label": "Hip view" }, { "value": ["0", "1"], "label": "Hip gun" },
        { "value": ["1", "0"], "label": "ADS view" }, { "value": ["1", "1"], "label": "ADS gun" }
      ] }
  ] }
```

| Member | Meaning |
|---|---|
| `columns` | Stored column names, at least one, none in another group. The choice shows where the first is; the others don't show. |
| `label`, `description` | The column's header and tooltip. |
| `choices` | `{ "value": [one per column, strings or numbers, no commas], "label": text }`. |

- **Reading a row:** each column's field, trimmed (an empty one read as its column's `default`), is compared with each
  choice's values (numbers by value, text without case). The first choice that matches is shown; with none, the
  fields as written show as `custom: 2,0` and stay as they are. Nothing is rewritten by reading.
- **Picking a choice** writes each of its columns' fields and formats the row as any cell edit does; picking the
  choice a row already matches writes nothing (`" 1 , 0 "` stays).
- Rules, `checks` and `unique` read the stored columns by name, as before.
- Anything wrong (an unknown column, a choice with the wrong count, no label, no choices) leaves the group out with a
  note, and its columns show one by one: a mistake never hides a stored field. An Apex from before combine notes the
  member and shows the columns one by one.

### Weapon references

`"kind": "assetRef", "refType": "weapon"` picks any weapon (every type whose name ends in `weapon`, as `targets` reads
`weapon`): the picker suggests them all, ⚠ says when a name is no weapon (`No weapon named ‘x’`), → goes to it. While a
type literally named `weapon` is loaded (Apex's mock data) it means that type.

### Pasting and copying rows

In a table, keyboard first:

- **Ctrl+V** with rows on the clipboard (more than one line, or fields separated by commas or tabs) pastes them from
  the row the keyboard is in (or the first marked row) down, replacing rows and adding any past the last, as a
  spreadsheet does. On **+ Add row** it adds them at the end. One undo step; the pasted rows are marked; each is
  checked like any row; rows past `max` are left out and counted. The status line says what was replaced and added
  and how many rows have a problem. A single bare value pastes into the text field the keyboard is in, as any paste.
- **A line** is a record as stored (`1,0,3,93,1.5,0.35,1.3,1`), a spreadsheet's row (cells separated by tabs, each
  trimmed, joined by commas), or one of the list's keys with its record as a `.gdtx` or a cfg writes it
  (`"wtKick1" "1,0,3"`, `wtKick1 = 1,0,3`; the number is ignored, rows go in the order pasted). Blank lines are
  skipped. Values are raw, as stored.
- **Shift+↑ / Shift+↓** mark rows; **Ctrl+C** copies the marked rows, or the row the keyboard is in, as stored, one
  per line (a text field's own selected text is copied as text). **Esc**, a plain ↑↓ or a click unmarks them.

### Folding sections

`"collapsedUnlessSet": true` folds a section to its header while none of its rows holds a value, the asset's own or
inherited (a row with only its default holds nothing; a table holds a value with any row). The header has ▸ / ▾ and
says how many rows hold one ("2 set", "none set").

- It opens by itself when a value appears (an edit, a paste, an undo, a parent's edit), and stays open when the values
  go (nothing folds away under the hand). The header (a click, or Space or Enter on it) folds and opens it.
- A filter or the Changed, Problems or Overrides view shows the rows it finds whether folded or not; picking the
  section in the rail or going to one of its rows (Go to property, an undo) opens it.
- Folding is the tab's, for the session: never saved, never a setting. Nothing you set is ever hidden by a default.

### Notices

`offNotice`, `notice` and a section's `notice` are one line each of your words, shown as you write them (control
characters dropped), quietly, in the form:

- `offNotice` leads the form while the extension is off for the asset (own, inherited or default), with **Turn on**,
  which turns the switch on as a click on it would (one undo step) and puts the keyboard on it.
- `notice` sits under the extension's first section header, a section's `notice` under its own header, while the
  extension is on.

Say only what is true for your extension: these are the only words Apex shows on your behalf, and Apex never says
anything about your tools or the game itself.

#### `consumer`: a notice that goes when your tool is installed

A `notice` is usually about the tool that reads the extension's values (a linker pass, a game DLL). A `consumer` lets
Apex drop the notice once that tool is there: it names a file in the BO3 install that carries a marker text while the
tool is installed.

```jsonc
"notice": "Your linker doesn't read these yet. Copy this weapon's section into weapon_tech.cfg.",
"consumer": { "file": "bin/jansson64r.dll", "contains": "generated:apex-gdtx" }
```

- `file` is relative to the install root (`/` or `\`): no drive, no `..`, no stream (`:`), no trailing dot or space.
- `contains` is a short plain ASCII text (up to 200 characters, no control characters) searched for as bytes.
- The notice shows unless the file is there and holds the text. When Apex can't tell (no install loaded, a file it won't
  read: over 16 MB, or a link), it shows the notice, as for a manifest with no `consumer`.
- Apex only reads the file, once per change to it (size and write time), and says nothing about whether the tool is
  switched on for a map or a mod: it can tell that a tool is missing, not that it is unused. The notice is evaluated when
  an asset's form is built, so a tool installed while Apex runs is noticed the next time one is opened.
- A mistake (not an object, a path that leaves the install, no text) leaves the consumer out with a note, and the notice
  is then always shown.

### Export

```jsonc
"export": { "format": "ini-section", "header": "[weapon:{asset}]", "command": "Copy as weapon_tech.cfg" }
```

| Member | Meaning |
|---|---|
| `format` | `ini-section`, the only one. Anything else leaves the export out. |
| `header` | The first line; `{asset}` is replaced by the asset's name. One line, raw. |
| `command` | What the palette and the button on the extension's first header call it. |

The command copies to the clipboard the header, then `key = value` for each key with a value, the asset's own or
inherited (what a tool that follows parents reads for it: a hand copy into a cfg needs the effective values), in the
form's order (sections, then each section's fields with its lists among them as `after` places them), a list as its
numbered keys 1..N in row
order. Left out: keys with only their default, empty values, and the `enabledBy` key (the section itself says the
asset has the extension on). Values are raw, as the `.gdtx` holds them. Lines end with CRLF, the last one too. It is
offered only while the extension is on for the asset. Example, a derived weapon with its own fire time:

```
[weapon:base_gun_up]
wtFireTimeMs = 70
wtSpringViewHip = 30,1,1,4,2
wtKick1 = 1,0,3,93,1.5,0.35,1.3,1
wtIk = 1
```

### Rules (`visibleWhen`, `checks`)

```
or      := and ('||' and)*
and     := compare ('&&' compare)*
compare := unary (('==' | '!=' | '<' | '<=' | '>' | '>=') unary)?
unary   := '!' unary | primary
primary := number | "string" | 'string' | true | false | key | '(' or ')'
```

A key reads the row's value (own, inherited or default) of the same extension; a bare value is true unless empty, 0,
false, off or no. `==` compares as switches, numbers or case-insensitive text; orderings need two numbers. No
functions, no assignment, no reading GDT keys. A rule that doesn't parse is reported and hides nothing.

### What Apex does with a manifest it can't fully read

Apex never refuses to start over an extension. A broken manifest (not JSON, wrong `apexSchema`, bad id, no targets)
leaves that extension out; a bad field, section or list leaves that part out; an unknown member is noted and ignored.
What was left out is said once, in one banner after startup, with the detail in its tooltip. Notes appear in the
section header's tooltip only.

## Where values live: the `.gdtx`

APE rewrites a GDT with only the keys its deffile declares, so extension values can't live in the GDT. They live in
a sidecar beside it, `<name>.gdtx`, in GDT syntax, one block per asset and extension:

```
{
	"ar_foo_zm" ( "weapon-tech" )
	{
		"wtEnabled" "1"
		"wtKick1" "0.5,0.1"
	}
}
```

- Apex creates the file with the first value and removes it when the last block goes (backed up first, like any save).
- Only values that differ from what the key would read without them are written; a default is never written.
- Keys Apex writes go in row order (case-insensitive, digit runs compared as numbers, `wtKick1` before `wtKick01`);
  a reader must not depend on it (hand edits and older files aren't sorted), and Apex reads rows by number either way
  ("Record lists").
- **Comments.** `//` outside a quoted string, wherever a space could be (between blocks, before a block's `(` or `{`,
  inside a block between keys, after a value, between a key and its value), starts a comment that runs to the end
  of its line (its LF): it is
  skipped, quotes and braces in it included. A `//` inside a value is part of the value. Nothing else is a comment
  (no `/* */`, `#` or `;`): other text inside a block still ends what Apex reads of that block, and Apex refuses to
  save into such a block, saying so. GDTs are read the same way; APE never writes a comment.
- A save keeps every comment byte for byte: values change in place, a removed key's line goes but a comment after
  it on that line stays, and a block holding comments is never rewritten in key order. Removing a key with a comment
  between it and its value is refused with a plain message (the comment would go with it); move the comment first.
- Apex writes it only through its save path, when the user saves. APE leaves `.gdtx` files alone.
- Renaming, moving, duplicating or deleting an asset in Apex takes its blocks with it. Doing that in APE doesn't:
  Apex keeps the orphaned block and lists it in the startup banner.
- A save writes the `.gdtx` before its GDT. If it stops between the two, the `.gdtx` is ahead (a renamed block, say,
  under its new name) until the next save writes the GDT; your tools may briefly see that.
- A GDT deleted on disk while Apex is open leaves Apex with its extension data, unsaved edits to it included, just as
  its unsaved GDT edits go.

Your build tools read the `.gdtx`: the asset's own block, else its parent chain's (by asset name, across GDTs), else
the manifest default.

## On and off: `enabledBy`

`enabledBy` names a key (with or without a field of its own; without one, Apex adds an "Enabled" switch at the top of
the first section). Its effective value is the asset's own, else the nearest ancestor's, else the default. Off hides
every row of the extension except the switch and never touches stored values. A derived weapon follows its parent
until it sets its own.

**Many assets at once.** The palette offers "Turn on <title> for N assets" and "Turn off <title> for N assets" over
the open table's checked rows, else the Explorer's selection (Ctrl+click, Shift+click). N counts the assets whose
switch in effect changes. Parents are done before the assets based on them; an asset that would then read the wanted
value from its parent (or the default) isn't written, and one holding the opposite value of its own drops it to follow
its parent rather than hold a copy. One undo step (the table's while it is open).

## The preview module

An extension may ship one native module that computes motion for the weapon preview (kick, sway, springs). It is the
only code an extension can ship.

```jsonc
"simulator": { "module": "bin\\weapon_tech_sim.dll" }
```

`module` is a path relative to the extension's folder, ending in `.dll`. Apex refuses (and says so) a path with `..`,
an absolute path, a drive, a stream (`:`), a wildcard, or one that goes through a junction or symbolic link; an
extension folder that is itself a junction or symbolic link is left out whole (copy it in instead). A module that
isn't there yet is only noted; the preview says so when it asks.

### The interface

`docs/plugin-abi/apex_sim.h` is the whole contract, version 1. In short:

- Plain C exports, `__cdecl`, 64-bit Windows: `apex_sim_abi_version`, `apex_sim_info`, `apex_sim_create`,
  `apex_sim_reset`, `apex_sim_step`, `apex_sim_destroy`. A missing export means the module isn't used.
- Flat structs, each starting with `size` set by Apex. Read and write only within the size you were given; Apex
  zero-fills every struct it passes out. Structs grow at the end, so an old module keeps working with a newer Apex.
  The size is never below version 1's, so every version 1 member is always there.
- No callbacks and no pointers kept after a call returns: the key/value strings passed to create are freed when it
  returns. Strings are UTF-8, NUL-terminated.
- `apex_sim_abi_version` is called first; Apex uses nothing else from a module whose version it doesn't know.
- `apex_sim_info` returns 0, sets `abi` to what `apex_sim_abi_version` returned and fills `id` with your manifest
  `id`, byte for byte (no trimming, no case folding). Any difference and the module isn't used.
- `apex_sim_create` gets every key of your extension for the weapon, in row order, with the value the editor shows
  (own, else inherited, else your default), raw; a list arrives as its numbered keys, rows 1..N. Return NULL with a
  short plain message in `err` when you can't simulate this weapon; that is shown beside the preview and nothing else
  happens. On success `err` is ignored.
- `apex_sim_step` advances by `dt` (always above 0) and fills the output. Set `supported` to the parts you computed
  and put a short reason in `note` for the rest: Apex draws only supported parts. Return non-zero with a `note` when a
  step fails.
- One `apex_sim` per previewed weapon, called from one thread (Apex's UI thread); several may be alive at once, so
  keep no per-weapon state in globals. `apex_sim_destroy` is called once per create.
- Your module runs inside Apex: let no C++ or SEH exception out of a call; return with the floating-point control
  state as you found it (MXCSR and the x87 control word; Apex's renderer depends on it, so Apex checks MXCSR after
  every call, puts it back and counts the call as failed); start no thread that outlives a call (Apex unloads the
  module when it closes); load no DLL at run time.
- Every call runs on the thread that draws Apex's window, and Apex can't interrupt one: a module that hangs, hangs
  Apex. A step must return in about a millisecond; create (run 150 ms after the weapon's extension values change) and
  destroy must return well under a second. Never block: no waiting on locks, files, the network or other threads.

Apex's own checks guard the boundary: a write past the end of a buffer Apex gave you, or ten failed steps in a row,
turns your module off until Apex restarts; motion that isn't a finite number is a failed step, and so, for counting,
is a step that took over 50 ms or a create that took over a second (its result is still used, and the user is told
once which module was slow). There is no watchdog: these checks run after a call returns.

### Loading and consent

Nothing about a module happens at startup. The first time a preview needs it (a weapon your extension is on for),
Apex:

1. checks the path again,
2. computes the file's SHA-256,
3. asks the user, unless they already answered for that hash, in the weapon's preview itself (a card on the render,
   not a dialog): "Load weapon-tech's preview module?", then that the module runs inside Apex with their permissions,
   then the extension, its version and the file's name (the full path and SHA-256 behind **Details**). For a new build
   of a module they answered for, it says what changed: the SHA-256's first 8 hex digits, old and new, the size and
   how much it changed, and the file's write time, old and new (answers kept by Apex 0.2.0 have no size or time, so
   those are left out). The card never takes the keyboard from where the user is working; with the keyboard already
   in the preview it lands on **Don't load**, and Esc or Enter in the preview choose Don't load too,
4. loads that exact file: held open (with its extension's folder) so it can't change or move in between, hashed
   again, and loaded by the path Windows reports for the held file, which must be inside the extension's folder,
5. checks the exports, the ABI version and the id.

Consent is for one file, so a module must be one self-contained DLL: link the C runtime statically (`/MT`) and ship
nothing beside it. Before asking, Apex reads the module's import table, delay-loaded imports included, and refuses a
module that imports any DLL Windows doesn't supply. An import is allowed when it is a bare file name and either an API
set (a name starting `api-` or `ext-`), one of Windows' KnownDLLs, or a file of that name in System32; Windows looks
for imports in System32 only, never in the module's folder, on PATH or in the current directory. A module carrying a
side-by-side manifest that names files or assemblies (`<file>`, `<dependency>`) is refused too, since Windows would
look for those beside it. This can't catch a DLL your module loads itself at run time (`LoadLibrary`): that is code the
user never saw a hash for, and consent is the only gate on it. Don't do it.

The answer, either way, is remembered per extension id and hash in `%LocalAppData%\Apex\extension-modules.dat`, sealed
for the Windows user (DPAPI) and kept away from `%AppData%\Apex`, where extensions are unpacked: a file Apex didn't
write reads as no answers, so the user is asked. (Development builds before 0.2.0 kept answers in
`%AppData%\Apex\extension-modules.json`; those are not read, so their users are asked once more.) To forget every
answer, close Apex and delete the file. A new build of your module has a new hash, so users are asked again
("…changed preview module?"). "Don't load" leaves everything else working (editing, saving) and the preview without
your motion; the preview's notice offers "Load module…" to ask again.

A module stays loaded until Apex closes; Apex destroys every simulation and frees the module on exit. While Apex is
open, Windows won't let the DLL be overwritten: close Apex before installing a new build.

### What the preview does with it

A weapon your extension is on for gets a preview of its own (Preview, "This asset"), with the BO3 install only; a
weapon it is off for has none, as before. The module is asked for when that preview first opens. Docked, the preview
takes the right column's full height (the Inspector steps aside until it goes or pops out, as the editor gives an
xanim's preview its room), so a degree of kick is a dozen pixels, not two; the viewport is never under 160 px tall.

- **The viewmodel**, in first person through APE's renderer: `gunModel` at `viewmodelTag` (default `tag_weapon`), each
  `attachViewModelN` at its `attachViewModelTagN` (the first per tag), and the viewhands `handModel`, else the hands
  last typed into an xanim preview (none: the gun on the anim's skeleton). Values are the weapon's own, else its
  parent's, else the deffile's, as they are in the editor (edits show at once). The camera is the rig's `tag_camera`,
  else `tag_view`, with the field of view last set in an xanim preview (65 by default); ADS doesn't zoom.
- **Hip** is `idleAnim`'s first frame. **ADS** is `adsUpAnim`'s last frame layered on it per bone, as the game layers
  anims: a bone the ADS anim keys moves from the idle pose to its own, a bone it doesn't key keeps the idle pose (an
  ADS-up anim that keys only `tag_torso` carries the idle arms to the eye). The `ads` you are given is the same
  fraction that mixes the two. No `adsUpAnim` and ADS keeps the hip pose (said under the preview).
- **Fire** (hold the button or Space): a round at once, then one per fire time while held; a tap is one round, and
  rounds never come faster than the fire time. The fire time is weapon-tech's `wtFireTimeMs` when the weapon (or a
  parent) sets it, else the weapon's `fireTime`. **Hip / ADS** (A) moves `ads` over `adsTransInTime` /
  `adsTransOutTime`. **Reset** (R) calls `apex_sim_reset`. Space fires after a click on any of them; the trigger is
  let go when the window loses focus, and a minimised window steps nothing.
- **Steps** run on the preview's display frames with the real time since the last one (at most 0.1 s; shorter than
  0.5 ms is added to the next), with that frame's rounds as `shots`. Only while something moves: the trigger is held,
  `ads` is moving, or your output changed by more than 0.0001 within the last 0.25 s, and never more than 10 s after
  the last input. At rest no step is sent.
- **Drawing.** `viewAngles` / `viewOrigin` move the camera and the viewmodel together, so on screen the world moves and
  the gun holds; `gunAngles` / `gunOrigin` move the viewmodel about its own origin (the eye) within the view. Signs and
  axes as the header says: pitch -5 lifts the muzzle (or the view), yaw +5 turns it left, origins along x forward,
  y left, z up.
- **Unsupported parts** are not drawn. Your `note` is shown under the preview; with no note, the parts you left out are
  named ("Not simulated: view origin."). Your keys in a note (or in `err`) are said as the form labels them, quoted,
  with the note as written in its tooltip: a field's key is its label (`wtRecoil` → “Kick and offsets”), a list's key
  or pattern its table (`wtKick3` → “Kick sets row 3”), and a stem `X*` the table whose stem is X, else the field whose
  key is X, else the words the labels of every key starting with X share when they are in one section
  (`wtSpringGun*` → “Spring: gun”), else that section's title. A stem across sections, or naming nothing, stays as
  written. So write keys as keys and let your labels do the talking.
- **The spray overlay**, drawn over the render as UI (APE's frame under it is unchanged, byte for byte): a reticle with
  a ruler (a tick a degree, longer at 5° and 10°) at the centre, the view's path through the last burst, and a dot a
  round where its kick peaked; the latest round's dot is in the accent colour. Angles land where the first-person
  camera puts them (`cg_fov`, Hor+), so the pattern scales with the pane. Only `viewAngles` is measured, and only
  while you say you computed them.
- **The readout** under Fire, for the last burst. A burst starts with a round fired while none is open (the first, or
  after the last settled or came to rest); rounds fired before then join it, so quick taps are one burst.
  - *Climb*: the most the view went up (−pitch, never below 0).
  - *Drift*: the yaw farthest from 0, and which way (positive yaw is left).
  - *Settles in*: from the start of the step that fired the last round to the first sample from which the view stays
    within 0.05° of rest for 0.1 s, or until the motion comes to rest (rounds that never move the view settle at
    once); "doesn't return" when the motion comes to rest away from rest, or stops being stepped, first.
  - *Shots*: the rounds in the burst.
  - A dot is the sample farthest from rest (√(pitch² + yaw²)) from the step that fired its round until the step before
    the next round (or the burst's end); rounds fired in one step share it.

  Time is the sum of the steps' `dt`, so a burst measures the same however the display frames fell. Without
  `viewAngles` the readout says "The module doesn't simulate view kick for this weapon, so there's nothing to measure."
  and the rounds are still counted. Reset (R) clears it, and so does a new simulation (an edit).
- **No gun to draw** (no `gunModel` or `idleAnim`, or one that isn't loaded): the preview says so on the render, and the
  module still runs: Fire, the overlay and the readout work as with a gun.
- **Edits.** The values you were created with are a copy: 150 ms after the weapon's extension values stop changing,
  the simulation is destroyed and created again. A create still waiting on the question when a newer one starts is
  destroyed unused. Close every preview waiting on the question and it is withdrawn, with nothing remembered.
  Every preview of a weapon your extension is on for shows the same question, and an answer in one is the answer.

Under the preview, one line says why it isn't moving: waiting for the question, "you chose not to load it" or a
module that couldn't be used (both with **Load module…**, which asks again), your create's `err`, or Apex having
turned the module off.

### What a module can do

It runs inside Apex's process with the user's permissions: it can do anything the user can, and a crash in it ends
Apex. Apex writes the session journal to disk before it loads a module and before every `apex_sim_create`, so unsaved
edits, including the value your create is about to see, survive such a crash as they survive a power cut. A module
must never write GDTs or `.gdtx` files: only Apex's save path does, when the user saves.

## Hardening (host and UI)

What Apex does about extensions and modules that are careless or hostile, and what it can't do. With no extension
installed, every editor shows the same content and behaves the same as before extensions existed; two fixes made
along the way ship to every editor regardless: the form's scroll padding (the last row of a form scrolls fully into
view) and the row focus on Undo (Undo of a row the keyboard is already in leaves it there).

**The module and its consent.**
- The user agrees to one file, by SHA-256. A module that imports, or delay-loads, any DLL Windows doesn't supply, or
  carries a side-by-side manifest naming files or assemblies, is refused before the question (see "Loading and
  consent"); its imports are searched for in System32 only. A DLL the module loads itself at run time can't be seen:
  consent is the only gate on it.
- What loads is the file that was hashed: the module and its extension's folder are held open from the second hash
  until the load, and Windows is given the held file's own resolved path (`GetFinalPathNameByHandle`), which must lie
  inside the extension's folder. An extension folder that is itself a junction or symbolic link is left out; a module
  path through one is refused.
- Answers live in `%LocalAppData%\Apex\extension-modules.dat`, DPAPI-sealed for the Windows user, never under
  `%AppData%\Apex` where extensions are unpacked; a file Apex didn't write is no answer. Answers from development
  builds (`extension-modules.json`) aren't migrated.
- The question shows manifest text only on lines of its own: the version is cut to 32 characters, and control and
  format characters (bidi overrides included) are stripped from the id, the version, titles, labels, descriptions,
  check messages and the file's name and path, and refused in module paths. It is asked in the weapon preview; only a
  question no preview is there to show (nothing in Apex asks that way) falls back to the confirmation dialog, with
  the same words. A question exists only once Apex asks it, and Load on a question that has gone (answered or
  withdrawn) does nothing. A manifest member named twice is noted (the last is read), a non-finite
  number default is refused, and a flood of notes is listed as the first 100 (the banner: 40) and how many more.
- When every preview waiting on the question has closed, the question is withdrawn and nothing is remembered.

**Calls into a module.** There is no watchdog: a call that doesn't return hangs Apex, which is why the header asks for
a step in about a millisecond and a quick create and destroy. After each call Apex checks what it can: guard bytes
after every buffer (a write past one turns the module off), finite output, the time it took (a step over 50 ms or a
create over a second counts as a failed call and is named to the user once), and the floating-point control state
(MXCSR; a change is put back and counted, since the renderer's output must stay bit-exact). Ten failed or slow calls in
a row turn the module off until Apex restarts. The journal is on disk before every load and create. `apex_sim_info`
must report the same ABI as `apex_sim_abi_version` and the manifest id byte for byte.

**The recoil preview.** A click on Hip, ADS or Reset hands the keyboard back to the preview, so Space fires next (Tab
still reaches them, and Space on a focused one is theirs); so does answering the question. The trigger is let go when the window loses focus (Alt+Tab,
a minimise) as well as when keyboard focus leaves the preview, and a minimised window steps nothing. The palette offers
Fire and Reset only when something can fire.

**Record tables.** When the editor is narrower than a table's cells (below about 1600 px for a kick table), the cells
scroll sideways between the row number and ⚠ ↑ ↓ ✕, which never move: the bar beside "+ Add row" (its thumb never
under 24 px), Shift+wheel, or Tab into a hidden cell. A focused switch rings its track only, never cut by the cell's
edge or covering On/Off. Each cell is named "{column} row {n}" for screen readers.

## Compatibility

- `apexSchema` stays 1 while manifests only gain members. An older Apex notes and ignores a member it doesn't know
  (for example `simulator`) and loads the rest, so add members freely; only a change an older Apex would misread
  bumps it. Every editor-pass member (`title`, `offNotice`, `notice`, `export`, `collapsedUnlessSet`, `parts`,
  `combine`, labelled `choices`) degrades that way: a 0.2.0 Apex shows the value in parts as one text box, the
  combined columns one by one, and drops the labelled entries of a choice list (so list a value plainly too if that
  matters), the notices and the export. So do a column's `hidden` (an older Apex shows the column, and a new row there copies
  the row above's value) and a list's `after` (the table follows the section's fields).
- The ABI version is a major number. Additions go at the end of structs (old modules and old Apex keep working
  through `size`); anything else is a new version, and Apex refuses versions it doesn't know.
- Your `id` and your key names are a permanent contract with users' `.gdtx` files. Add keys; never rename or reuse
  one. Plan a migration before changing what a key means.

## Troubleshooting

| You see | Why | What to do |
|---|---|---|
| Banner: "Apex left out parts of the X extension" | The manifest has problems. | Hover the banner for each one. |
| Your section is missing on a type | Not in `targets`, or every field is a deffile key. | Check `targets`; rename keys the deffile declares. |
| Only "Enabled" shows | The extension is off for this asset (or its parent). | Turn it on. |
| A value in parts shows as one text box | Its field isn't `text`, or a part is wrong. | Hover the section header for the note. |
| A combined column shows as two | The group has a mistake. | Hover the section header for the note. |
| "its preview module … was left out" | The `module` path was refused. | Use a plain relative path inside the folder. |
| "…isn't a 64-bit Windows DLL" | A 32-bit build, or not a DLL. | Build for x64. |
| "…needs X, which isn't part of Windows" | The module imports a DLL of its own, or the VC++ runtime (`/MD`) on a computer without it. | Link the runtime statically (`/MT`) and build one self-contained DLL. |
| "…is built for version N of Apex's preview interface" | ABI mismatch. | Build against the header this Apex ships. |
| "…says it belongs to Y, not X" | `apex_sim_info` id differs from the manifest id. | Make them match exactly. |
| "Apex turned off its preview module until it restarts" | Ten failed, slow or floating-point-changing calls in a row, or a write past a buffer. | Read the banner's tooltip; fix and restart Apex. |
| Never asked, preview off | The user chose Don't load for this build. | "Load module…" beside the preview asks again. |
| Can't overwrite the DLL | Apex has it loaded. | Close Apex first. |
