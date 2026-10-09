# Saving to GDTs — design notes

Status: built on branch wt-save (2026-09-27); manual APE/linker checks pending (see the end).

The bar: a modder must be able to trust a save more than they trust APE's. A save that corrupts, reorders or
silently drops anything once is enough to lose that user for good.

## Decided (2026-09-27)

- **Apex writes GDTs in place, only on an explicit user save.** The never-write rule is now a *development* rule
  (for tests): automated tests and Apex.Shots never write under the install; they work on a temp copy.
- **Stock GDTs are editable, like in APE** (APE parity). They get no special block; the backups below make every
  save undoable, so no extra confirmation is needed.
- **The user's model for a save:** check for changes, take an exclusive lock, write, unlock. The sequence below
  implements that, plus atomic replace and verification so interference is detected and nothing is corrupted.

## Save sequence per GDT

1. **Preflight, no lock.** Compare the file's size, last-write time and content hash with the stamp taken when
   Apex read it. Different → conflict flow (see Write mechanics 5) before anything else.
2. **Lock.** Open the target with `FileShare.Read` (others may read, nobody else may write). A sharing violation
   means another program has it open for writing → plain message ("zm_weapons.gdt is open in another program")
   with Retry; never a partial write.
3. **Re-check under the lock.** Read the file's bytes through the locked handle and hash them. This closes the
   gap between preflight and lock; a mismatch goes to the conflict flow.
4. **Build.** Splice the edits into the bytes read under the lock (never into an older copy).
5. **Stage.** Write the backup of the original bytes (`%LOCALAPPDATA%\Apex\backups\`), then write
   `<file>.apex-tmp` in the same directory, `Flush(true)`, re-read and re-parse it and check it against the model.
6. **Swap.** Release the lock and immediately `File.Replace(tmp, target, …)` (atomic on NTFS, keeps attributes and
   ACLs). NTFS won't rename over a file with open handles, so the lock can't be held across the swap; the window
   is microseconds. Transient failures (antivirus or indexer holding a handle) retry with a short backoff (~1 s
   total), then report.
7. **Verify.** Re-open the target and hash it. Must equal the staged bytes. If not, something wrote in the swap
   window: report it and keep the backup, never overwrite blindly.
8. **Commit.** Only now: the watcher ignores the event whose hash matches what Apex wrote, the new stamp becomes the
   baseline, and the journal entries for those assets are cleared.

A save across several GDTs: preflight, lock, re-check, build **and stage** every file first; if any of that fails,
write none (temp files are deleted, backups are harmless). Only then swap, file by file. Each swap is atomic on its
own (there is no cross-file transaction), so if one fails after others succeeded, the report says exactly which
files were saved and the rest are not attempted; unsaved edits stay in the session and the journal.
(Changed while building: staging every file before the first swap, instead of stage-then-swap per file, moves every
likely failure — disk full, a verify mismatch, a value a GDT can't hold — before anything is replaced.)
(Changed 2026-10-06: extension data (`.gdtx`) is swapped before any GDT, and a file whose check after its swap fails
stops the rest as a failed swap does, so a partial save never leaves a GDT ahead of its `.gdtx`. Why:
`docs/schema-extensions-design.md`, "Hardening (save path)".)

A leftover `*.apex-tmp` next to a GDT (crash between stage and swap) is deleted on the next save of that file; the
original was never touched. One exception: a `.gdtx` whose removal stopped after it was moved aside leaves only
`x.gdtx.apex-tmp`, and loading puts it back (`GdtSaveService.RecoverRemovedSidecar`).

## Write mechanics (non-negotiable once saving exists)

1. **Splice, don't re-serialize.** The indexer already records each asset's body offset and length. A save
   rewrites only the byte ranges of assets that changed, and appends new ones; every other byte in the file
   stays identical. Why: modders keep GDTs in git, and a save that reformats 4,000 untouched assets makes
   every diff unreadable and hides real mistakes. `Services/GdtWriter.cs` is the syntax reference for new or
   fully rewritten bodies only.
2. **Match the file's existing conventions** per file, detected at read time: line endings (CRLF vs LF),
   indentation (tabs), quoting, trailing newline, key order within an asset (keep existing order; new keys go
   where APE would put them — verify APE's rule), encoding (bytes in, bytes out; never round-trip through a
   lossy UTF-8 decode, and never add a BOM). Values are raw: backslashes are literal.
3. **Atomic replace.** Write to `<file>.apex-tmp` in the same directory, flush with `FileStream.Flush(true)`,
   then `File.Replace(tmp, target, …)` (same volume, so it is atomic on NTFS). Never write in place. The full
   order, including the lock, is in "Save sequence per GDT" above.
   Keep the previous version as a backup outside the install (e.g. `%LOCALAPPDATA%\Apex\backups\`, bounded,
   newest N per file) so any save can be undone even after closing.
4. **Verify after write.** Re-parse the written file and compare against the in-memory model: same asset set,
   same values for every asset, untouched ranges byte-identical to the original. As built, the check runs on
   the spliced bytes *before* anything is staged (same assets in the same order, every untouched asset's block
   byte-identical, every written asset reading back as the original plus exactly its changes, every byte between
   assets unchanged), then the temp file is read back byte-for-byte, then the target is hashed after the swap.
   A failure before the swap means the file was never replaced. A mismatch after the swap means another program
   wrote in the swap window: Apex reports it and keeps the backup but does **not** restore it automatically —
   that would overwrite the other program's write, which is the thing this rule exists to prevent.
5. **Conflict detection.** Record size + last-write time + a content hash (XxHash128, computed while indexing, so
   it costs nothing extra) per GDT at load. Before writing, if the file changed on disk (APE, git checkout,
   another editor), do not overwrite: show which assets differ and let the user choose theirs/mine per asset.
   The existing `GdtWatcher` already sees these changes; the save path must not rely on the watcher having fired.
   As built, a save writes each asset's *delta* (the keys the session changed against the asset's session
   baseline), not the whole in-memory asset, so an external change to a key the session didn't touch is kept.
   Per asset, a key conflicts when the file's value is neither what Apex read nor the session's new value. A
   changed stamp is always a conflict (dialog), even when nothing overlaps; "Keep mine" then saves against the
   new stamp, merging.
6. **Locks and attributes.** Handle read-only files (stock GDTs, source control) and sharing violations (APE
   or the linker holding the file) with a plain message and a retry, never a raw exception.
7. **Structural edits** (new, duplicate, rename, delete, new GDT) go through the same splice path. Rename must
   update references the same way the in-memory refactor does, and the review screen must list every file
   that a rename touches.

## What APE's files show (corpus survey, 2026-09-27: 2,047 GDTs, 116,389 assets)

Confirmed from the corpus:
- **Keys within an asset are sorted by APE, case-insensitively, lowercasing** (so `_` sorts before letters):
  68,910 assets are sorted that way; in 5,431 of them lowercasing and uppercasing disagree and lowercasing is the
  one that holds. The 47,478 unsorted assets are in files written by porting tools, not APE. A new key goes in its
  sorted place in a sorted body; in an unsorted body it goes after the last key (APE would re-sort the whole
  asset; Apex doesn't reorder what it didn't change).
- **APE writes every gdf field of a root asset, defaults included** (key counts are constant per gdf: image 42,
  bulletweapon 1,287/1,288, material 909/910, …), and **only the overrides of a derived asset**. Apex's new asset
  already starts with every schema default; a duplicate copies all values.
- **APE doesn't sort assets**: 353 of the 1,068 APE-written files have assets out of name order, so it keeps file
  order and (assumed, consistent with that) appends new ones. Apex appends new assets at the end.
- **No escaping**: a string runs from `"` to the next `"`; backslashes are literal (81,803 assets hold them).
  No GDT contains a quote or a line break inside a value. Apex refuses to save such a value (plain message, file
  untouched) rather than altering it (the old `GdtWriter` turned quotes into apostrophes).
- Files are CRLF (2,001) or LF (46); 45 have no final newline; no BOM anywhere. Apex keeps each file's own.
- **Encoding**: one file holds non-ASCII (`skye_t8_hitchcock_m9.gdt`, cp1252 `é` = 0xE9), so whatever wrote it
  used the ANSI code page. Apex reads UTF-8 and writes edited values as UTF-8 (so it reads back what it wrote);
  untouched bytes are never re-encoded, and a value holding U+FFFD (bytes Apex couldn't decode) is refused.
  Assumed, not verified in APE: how APE shows a UTF-8 `é`. ASCII is identical either way.
- Empty GDT: `{\r\n}\r\n` (dozens of stubs). New GDTs are written that way.
- 11 assets have duplicate keys (last one wins in Apex's parser). An edit sets every occurrence; a removal removes
  all, so every reader sees the same value.

## Development guard

`APEX_WRITE_ROOT` (one or more folders, `;`-separated): when set, every write, rename, replace or delete the save
path makes must sit inside one of them, checked before the first byte (`WriteGuard`). Apex.Shots sets it to its
own temp folder at startup in every mode, and `APEX_BACKUP_DIR` beside it. Unset, as users run Apex, it allows all.

## UX

- One explicit save: Ctrl+S ("Save all", APE's shortcut) saves every changed GDT with no dialog (the
  common path is one action; backups make it undoable). A dialog appears only for a conflict, a locked or
  read-only file, or a failure. No autosave to GDTs: the linker reads them, and a half-finished edit must not
  reach a build.
- The review screen is a diff per asset (key, old → new), grouped by file, like a code-review diff view. It
  reuses "Your changes" data so the two can never disagree.
- After saving, the status line says what was written ("Saved 3 assets in 2 GDTs"), and undo still works.
- The session journal (built before this) is what makes unsaved work survive a crash; after a successful save
  the journal entries for those assets are cleared.

### As built (2026-09-27)

- **Ctrl+S / Save all** (catalog `file.saveAll`) commits typing still in the focused field, plans on the UI thread
  (every record created, read into memory, renamed or re-parented, plus deletes and new GDTs; the planner keeps only
  real differences), runs the save sequence on the thread pool, and commits on the UI thread. "Saving…" appears only
  if it takes over 150 ms. Status: "Saved 3 assets in 2 GDTs". Written assets rebase ("changed" is now measured
  against the file), their change marks and journal entries clear, the chip and start page count only what is
  left; undo history stays, so Ctrl+Z after a save makes the value unsaved again.
- **Edits made while a save runs** are not lost: the plan snapshots each written asset, and on commit a key edited
  since the snapshot keeps its newer value (and shows as changed).
- **Conflict dialog** ("Changed on disk"): per asset, the keys with yours · the file's · what Apex read, and two
  pills, Keep mine / Take the file's. Save re-reads the changed GDTs (the session keeps its edits), applies the
  choices and saves again; Cancel writes nothing. A rename/new-asset name clash or an ambiguous asset can't be
  chosen away: the dialog says what to fix and Save stays disabled. A new GDT whose file already exists is a banner.
- **Locked, read-only, failed**: a banner with the file and what to do, and Retry. Invalid values: a banner, no Retry.
- **Watcher**: a changed file whose hash is the one Apex just wrote is ignored; a real external change still
  reloads. Reloads now match records by their name in the file (a session rename no longer drops the record, and
  unsaved new assets are left alone), keep the file's stamp, and rebase records that were viewed but not edited
  (another program's change is not shown as the user's change). A file the watcher can't read yet (another
  program still writing it) is looked at again instead of being read as empty — before, that dropped every asset of
  the GDT from the session, edits included — and an asset with unsaved edits is never dropped by a reload (if the
  file no longer has it, saving asks).
- **Backups**: newest 10 per GDT in `%LOCALAPPDATA%\Apex\backups\<name>-<path hash>\`. The palette command
  "Restore previous version of this GDT" (catalog `gdt.restorePrevious`, active asset's GDT) writes the newest
  backup back through the same sequence, so the version it replaces is backed up too (restoring again goes forward).
  It refuses while that GDT has unsaved changes.
- **Mock mode**: saving is disabled with a status line ("This is sample data … no GDT files to save to"). Writing
  sample data to a temp tree would pretend to save something the user can't use; the tests instead run Apex live
  against a temp install (real deffiles and GDTs copied under the write root).
- **New GDTs** created live are named `source_data/<name>.gdt`, where their file is written.
- **Placement (2026-10-02)**: Copy/Paste and "Duplicate to…" add assets like Duplicate (appended, all values; a
  derived copy keeps its parent and only its own values). Cut/Paste and "Move to…" keep the record and its name: it
  becomes new to its GDT (written whole, appended) and a *ghost* of its old place deletes it from the old file, so a
  save changes two files, each through the same sequence (preflight, lock, stage all, swap), each backed up. The
  ghost carries what Apex read, so a change to that asset on disk is a conflict ("changed before delete"); a reload
  of the old file leaves the moved asset moved. Moving it back to the file that still holds it cancels the move.
  Underive writes the asset's effective values (own, else nearest ancestor's, else the schema default) and swaps the
  header in place from `[ "parent" ]` to `( "type.gdf" )`; new keys land in APE's order. Each is one Ctrl+Z step
  until the save that writes it.

## Tests that must exist before it ships

- Round-trip: for every GDT in the real corpus, copied to a temp dir: load, save with no changes, byte-compare
  = identical. Then one change per file: only that asset's range differs.
- Fuzz edits (random keys/values incl. backslashes, quotes, empty values, non-ASCII bytes) → save → reparse →
  model matches.
- Kill during write (simulate by throwing between tmp write and replace): original file intact.
- External change between load and save → conflict shown, file not overwritten.
- Read-only / locked file → message, no partial write.
- APE opens every file Apex saved (manual check on a sample, recorded in the change).

Where they live (`Apex.Shots/SaveChecks.cs`, all on temp copies under the `APEX_WRITE_ROOT` guard):
- Every Shots run: golden byte-exact splices for each kind of edit, a 37-GDT subset of the real corpus (the
  largest files and every oddity the survey found) for layout-equals-index, no-op round trip and one edit per
  file, a real-file structural test, 400 fuzz files × 3 saves, every interference case, bounded backups and
  restore. About 25 s. `-- --save` runs only these.
- Every Shots run also drives saving in the live app with real input (`SaveUiChecks.cs`, alone with `-- --save-ui`):
  Apex runs against a temp install (real deffiles and two real GDTs copied under the write root); Ctrl+S, undo after
  save, the watcher ignoring Apex's own write but not another program's, the conflict dialog (Keep mine and Take the
  file's, by real clicks), a locked file's banner and Retry, and Restore previous version.
- `-- --perf-live` gates planning a save over the real corpus at 16 ms (measured 0.4 ms).
- `-- --save-corpus` (opt-in, ~1 min): every GDT in the install (2,047 files, 594 MB) copied to temp for the same
  checks, plus 3,000 fuzz files.

## Manual checks only the user can do

- Open a GDT Apex saved in APE (one value edit, one new asset, one rename): APE lists it, values match.
- Save it again from APE and diff: APE rewrites whole files (sorted keys), so expect only APE's own reformatting.
- Run a linker build of a map/mod that uses a saved GDT.
- Non-ASCII: type an accented character in Apex, save, open in APE (Apex writes UTF-8; APE's own files look ANSI).
