# Indexing Capability Overview

This document describes the current filesystem and application search implementation. It
separates code capabilities from proposed work and does not certify every manual test
or the performance targets in the roadmaps. Ownership is documented in `architecture.md`;
future work is tracked in `roadmap.indexing.md` and `roadmap.applications.md`.

## Implemented behavior

| Area | Current behavior | Boundary |
| --- | --- | --- |
| Entities | Files, directory names, and launchable application entries. | No file contents, modification-date fields, or size filters. |
| Default scope | Files: startup-critical Desktop/Downloads partitions, the user-profile remainder, and configured/redirected Known Folder partitions. Applications: user/common Start Menu, App Paths, packaged and non-package AppsFolder entries, and curated Windows system entries. | No whole-disk scan or recursive Program Files/Windows scan. File roots remain internal options; portable application roots have JSON configuration. |
| Hidden and system entries | Readable entries can be indexed; there is no blanket hidden/system exclusion. | Ordinary access permissions still apply. |
| Links and junctions | Encountered reparse directories appear as folder candidates. | Their descendants are not traversed by default. Explicitly configured reparse roots can be traversed. |
| Matching | Case-insensitive exact name/stem and prefix. Applications also match executable aliases and whitespace-compacted names. | No substring, full-path, fuzzy, pinyin, wildcard, or token-reordering. Internal whitespace stays significant for ordinary filenames. |
| Ranking | Configurable application bias plus name score and optional additional priority; rank up to 64 retrieved matches per source before truncating. | Usage history is not recorded. The injected priority provider can promote files above applications; history-aware retrieval beyond the pool is future work. |
| Candidates | `Gen` merges applications and files with five direct results plus Global Search by default and labels their locations as `应用`, `文件`, or `文件夹`. | `Cmd` offers command domains and one immediate path segment at a time, including aliases and links; optional arguments alone carry descriptions. `Ask` has no candidates. Limits are internal options rather than Settings controls. |
| Keyboard behavior | Up/Down selects, Tab completes a command segment or optional argument, Enter opens/executes or submits when the selection is not executable, Shift+Enter reveals files/applications or opens a selected folder, and Ctrl+Enter copies the full selected path while keeping the input visible. Only the selected Gen row displays the current Enter action. | No preview, multi-select, or implemented Global Search page. Same-input refresh preserves selection where possible. |
| Executables and shortcuts | Trusted Start Menu links, registered programs, packaged AUMIDs, non-package AppsFolder items, curated Settings/Control Panel/MMC/system tools, portable roots, and ordinary in-scope executables can launch. Original shortcut and Shell activation semantics are retained. Packaged applications reveal their executable or installation directory when Windows exposes one. | Some virtual Shell entries have no filesystem location and cannot be revealed. Generic document links remain files. Arbitrary URI/Shell targets are rejected. Private-PATH App Paths launches requiring elevation report an original-shortcut fallback. |
| Type icons | Windows stock icons represent ordinary files and folders, while application rows load their Shell icon with built-in glyphs as an immediate fallback. | No thumbnails. |
| Live maintenance | Name-change notifications maintain a Delta of creates, renames, and deletions in coalesced 250 ms batches, flushed to each owning partition's recovery journal before live publication. | This is a scheduling interval, not a measured latency guarantee. In-place content writes are not subscribed to. |
| Reconciliation | Per-partition startup scan, six-minute startup-critical and 30-minute normal file deadlines, event-targeted reconciliation, watcher recovery, normal `/luv index refresh`, and force-all `--force`. | One file build runs at a time; ordinary work respects each partition's one-minute gap. Accepted work may be queued or coalesced. |
| File cache | One validated v4 snapshot/recovery-journal pair and atomic active-generation manifest per filesystem partition. Complete journal batches replay over their matching base; compaction persists both files before publication. | Cache loading takes time. Missing/incompatible partitions require construction. Offline changes need reconciliation; a durable SnapshotSet manifest for live ownership migration remains planned. |
| Application cache | One v2 manifest/snapshot/backup pair per source, with source/scope validation, checksum, independent startup publication, and source-local failure retention. | No first-launch results before discovery when a compatible source cache is absent. Removed-source cache garbage collection is pending. |
| Ignore and cooldown | Ordinary directory/path ignores precede cooldown; full ignores exclude exact files/subtrees. Per-path cooldown defaults to 60 seconds. | Ordinary ignored paths remain scanned and searchable. No globs or `.gitignore` parsing. Unattributed overflow can still request recovery. |
| Configuration | Shared `maintenance.json` controls maintenance and exclusions; `Applications/settings.json` provides `PortableRoots`. | Read once per launch. `/luv index refresh` does not reload settings. Invalid shared configuration pauses both indexes; invalid application configuration pauses only its catalog. |
| Reliability and feedback | Background companion, supervised restart, stale-query rejection, estimated scan progress, persistent activity updates, opt-in bounded JSONL diagnostics, fixed application STA workers, and source-local application retry/backoff. | A failed filesystem partition retains its own prior snapshot and retries; aggregate file activity/progress cannot yet expose every partition field to Settings. |

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
| Ordinary `Microsoft To Do.lnk` file | `Microsoft todo` | Filename matching alone still preserves internal spaces. |
| Discovered Microsoft To Do application | `Microsoft To Do` or `Microsoft todo` | Display-name or whitespace-compacted application match; Enter activates its AUMID. |

## Maintenance distinctions

A live query merges the last complete snapshot with recovered journal batches and changes
observed during the current session. Most ordinary changes do not need a full scan and do not wait six minutes.
Directory moves/imports can require reconciliation: old descendants disappear through
prefix filtering, while new descendants may wait for the next permitted scan.

Periodic deadlines start with scope configuration and are not reset by event-driven or
manual scans. Per-path cooldown suppresses repeated requests by one path; each partition's
automatic gap spaces its complete scans even when many different paths change. Force
refresh bypasses ordinary ignores and cooldowns, honors full exclusions, and never
overlaps an active scan in the same partition.

Ordinary ignore lowers trigger frequency, but does not reduce periodic enumeration or
complete snapshot size. Dependency trees, caches, and agent state can still contribute
substantial scanning work. Full ignore reduces scanning by removing search coverage.
Ordinary ignored events also retain Delta processing, so they can still contribute to
unattributed watcher or pending-buffer overflow.

Complete journal batches survive restart and replay only over the matching snapshot,
scope, policy, and applied sequence. Partial tails, corruption, and offline changes
require reconciliation. With no compatible cache, complete results require an initial
scan. Changed full exclusions invalidate old-scope caches.
At the retained-Delta safety limit, queries fall back to that partition's complete
baseline, which can also temporarily be stale. Failed root scans preserve only the
affected partition's old snapshot.

Applications have a separate persistent catalog and per-source refresh state. Healthy
sources publish and persist while an unavailable source retains its old entries and
backs off independently. Start Menu notifications target the owning partition;
registrations, AppsFolder, system entries, and portable roots are periodically checked.
`/luv index refresh` fans out normal work across both services; `-f` or `--force` bypasses
the partition gap. Cache affects startup availability, not
ranking. Full-ignore rules also filter known application paths.

File candidates are checked for existence/kind before activation, while catalog entries
revalidate their launch descriptors and registrations. Windows Shell acceptance no longer
requires a new process handle. A late application launch cannot close a newer input query.

## Proposed next capabilities

All items below are proposals and can change with user priorities.

1. **Usage-aware ranking:** collect successful activations, persist stable identities,
   and define frequency/recency/pinning boosts through the existing priority interface.
   Extend retrieval so frequently used items outside the current pool remain discoverable.
2. **Scope and diagnostics controls:** included roots, ignores, pause/resume, last
   successful build, entry counts, cache state, and per-root errors in Settings. Explain
   why an expected item is outside scope or excluded.
3. **Richer name retrieval:** path/word matching and execution-alias coverage,
   followed by bounded fuzzy and optional pinyin matching. Preserve exact-match priority
   and measure the effect on query cost and ranking.
4. **A real Global Search view:** pagination, filters, preview, copy-path, recent items,
   and favorites. Date/size filters require metadata absent from the current records.
5. **Live ownership migration:** extend existing per-partition durable recovery with a
   SnapshotSet manifest and staged ownership-map changes at runtime.
6. **Measured performance work:** establish large-corpus benchmarks before considering
   mmap snapshots, lower-memory construction, and conditional NTFS USN/MFT acceleration.
   Retain ordinary watcher/scanning fallbacks.
7. **Optional content and semantic search:** separate document text/OCR extraction from
   the filename index, with explicit scope, resource, and privacy controls. Define
   storage and execution behavior before adding semantic/AI retrieval.

Performance figures in the indexing roadmap are targets, not measured results. Use its
manual acceptance checklist to validate representative files, scopes, and configurations;
use the application-search checklist for application discovery, ranking, and activation.
