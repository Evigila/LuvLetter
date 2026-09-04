# Indexing Capability Overview

This document describes implemented behavior reviewed against commit `4c1b940`. It
separates code capabilities from proposed work and does not certify every manual test
or the performance targets in the roadmaps. Ownership is documented in `architecture.md`;
future work is tracked in `roadmap.indexing.md` and `roadmap.applications.md`.

## Implemented behavior

| Area | Current behavior | Boundary |
| --- | --- | --- |
| Entities | Files and directory names, independent of extension. | No file contents, modification-date fields, or size filters. |
| Default scope | Current user profile, Downloads, and Desktop/Documents/Pictures/Music/Videos Known Folders; redirected locations are retained and overlaps deduplicated. | No default whole-disk or installed-application scan. Included roots are internal options, not user-facing JSON/Settings controls. |
| Hidden and system entries | Readable entries can be indexed; there is no blanket hidden/system exclusion. | Ordinary access permissions still apply. |
| Links and junctions | Encountered reparse directories appear as folder candidates. | Their descendants are not traversed by default. Explicitly configured reparse roots can be traversed. |
| Matching | Case-insensitive Unicode exact name, exact file stem, then name prefix; outer input whitespace is trimmed. | No substring, full-path, fuzzy, pinyin, wildcard, token-reordering, or internal-whitespace normalization. |
| Ranking | Deterministic name-based tiers, folder-first ties for otherwise equal names, and stable identity ordering. | No recency, usage history, favorites, or personalized ranking. |
| Candidates | `Gen` provides up to five filesystem/command results plus one reserved Global Search row by default; files take precedence. | `Cmd` only offers commands; `Ask` has no filesystem candidates. Limits are internal options rather than Settings controls. |
| Keyboard behavior | Up/Down selects, Enter opens, Shift+Enter reveals in Explorer; same-input refresh preserves selection where possible. | No preview, multi-select, copy-path action, or implemented Global Search page. |
| Executables and shortcuts | In-scope `.exe` and `.lnk` files have an executable glyph and can be opened through the Windows Shell. | No application catalog, friendly-name discovery, App Paths enumeration, or packaged-app activation. |
| Type icons | Built-in glyphs distinguish folders, documents, images, archives, audio, video, executables, and generic files. | No real executable icons or thumbnails. |
| Live maintenance | Name-change notifications maintain an in-memory Delta of creates, renames, and deletions in coalesced 250 ms batches. | This is a scheduling interval, not a measured latency guarantee. In-place content writes are not subscribed to. |
| Reconciliation | Startup scan, independent six-minute periodic deadlines, event-triggered reconciliation, watcher recovery, and `index.refresh`. | One scan at a time; automatic scans respect a one-minute global gap. Accepted work may be queued or coalesced. |
| Cache | Validated local v3 snapshot, backup fallback, atomic replacement, and background startup loading before rescan; failed scans retain the previous usable snapshot. | Cache loading takes time. Missing/incompatible caches require construction; live Delta and offline changes are not durably replayed. |
| Ignore and cooldown | Ordinary directory/path ignores precede cooldown; full ignores exclude exact files/subtrees. Per-path cooldown defaults to 60 seconds. | Ordinary ignored paths remain scanned and searchable. No globs or `.gitignore` parsing. Unattributed overflow can still request recovery. |
| Configuration | `maintenance.json` controls refresh interval, cooldown, ordinary ignores, and full ignores; unmodified legacy defaults upgrade in memory. | Read once per launch. `index.refresh` does not reload settings; invalid configuration pauses indexing until corrected and restarted. |
| Reliability and feedback | Background companion, supervised restart, stale-query rejection, lifecycle messages, and event-colored debug logs. | A failed configured root prevents the entire new snapshot from publishing; healthy roots are not published independently. |

## Practical search examples

These examples assume an item is in scope and not full-ignored. They describe matching
eligibility, not a guarantee that the item wins a slot among the top five results.

| Indexed name | Input | Current result |
| --- | --- | --- |
| `report.pdf` | `report.pdf` | Exact-name match. |
| `report.pdf` | `report` | Exact-stem match. |
| `report.pdf` | `rep` or `REP` | Prefix match. |
| `annual-report.pdf` | `report` | No match; substring search is absent. |
| `项目计划.docx` | `项目` | Unicode prefix match. |
| `项目计划.docx` | `计划` or `xiangmu` | No match; substring and pinyin search are absent. |
| `report.pdf` | `*.pdf` or `type:pdf` | No extension-filter syntax. |
| `report.pdf` | Its complete directory path plus filename | No full-path query routing. |
| `Microsoft To Do.lnk` | `Microsoft todo` | No match; internal spaces are significant. |
| Installed Microsoft To Do | `Microsoft To Do` | Installation alone creates no filesystem candidate; application discovery is planned. |

## Maintenance distinctions

A live query merges the last complete snapshot with changes observed during the current
session. Most ordinary changes do not need a full scan and do not wait six minutes.
Directory moves/imports can require reconciliation: old descendants disappear through
prefix filtering, while new descendants may wait for the next permitted scan.

Periodic deadlines start with scope configuration and are not reset by event-driven or
manual scans. Per-path cooldown suppresses repeated requests by one path; the global
automatic gap spaces complete scans even when many different paths change. Force
refresh bypasses ordinary ignores and cooldowns, honors full exclusions, and never
overlaps an active scan.

Ordinary ignore lowers trigger frequency, but does not reduce periodic enumeration or
complete snapshot size. Dependency trees, caches, and agent state can still contribute
substantial scanning work. Full ignore reduces scanning by removing search coverage.
Ordinary ignored events also retain Delta processing, so they can still contribute to
unattributed watcher or pending-buffer overflow.

Delta changes are memory-only. After restart, queries may temporarily show the last
saved full snapshot until reconciliation catches up. With no compatible cache, complete
results require an initial scan. Changed full exclusions invalidate old-scope caches.
At the retained-Delta safety limit, queries fall back to the complete baseline, which
can also temporarily be stale. Failed root scans preserve the old snapshot as a whole.

Candidates are checked for existence/kind before activation. The current launcher still
bases success on receiving a process handle; more accurate Windows Shell success
reporting is part of the application roadmap.

## Proposed next capabilities

All items below are proposals. This order reflects the current application-search use
case and can change with user priorities.

1. **Application discovery and activation:** Start Menu shortcuts, App Paths, portable
   roots, and Windows Shell packaged entries; friendly names, aliases, AUMID activation,
   independent cache, and launch-identity deduplication. Microsoft To Do is a first-release
   manual acceptance case.
2. **Scope and diagnostics controls:** included roots, ignores, pause/resume, last
   successful build, entry counts, cache state, and per-root errors in Settings. Explain
   why an expected item is outside scope or excluded.
3. **Richer name retrieval:** path/word matching and application whitespace aliases,
   followed by bounded fuzzy and optional pinyin matching. Preserve exact-match priority
   and measure the effect on query cost and ranking.
4. **A real Global Search view:** pagination, filters, preview, copy-path, recent items,
   and favorites. Date/size filters require metadata absent from the current records.
5. **Durable incremental recovery:** persist Delta checkpoints, recover changes across
   restarts, and isolate unavailable roots so healthy roots can continue updating.
6. **Measured performance work:** establish large-corpus benchmarks before considering
   mmap snapshots, lower-memory construction, and conditional NTFS USN/MFT acceleration.
   Retain ordinary watcher/scanning fallbacks.
7. **Optional content and semantic search:** separate document text/OCR extraction from
   the filename index, with explicit scope, resource, and privacy controls. Define
   storage and execution behavior before adding semantic/AI retrieval.

Performance figures in the indexing roadmap are targets, not measured results. Use its
manual acceptance checklist to validate representative files, scopes, and configurations;
use the application roadmap checklist once that module is implemented.
