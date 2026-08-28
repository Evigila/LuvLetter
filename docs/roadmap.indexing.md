# File Indexing Roadmap

This document records the intended evolution of filename indexing, candidate production,
and file activation. It is a planning document: Phases 1 and 2 are complete. User-visible
work is recorded in `changelog.md` only after it is implemented, while ownership and
dependency rules remain in `architecture.md`.

## Product invariants

The following rules apply to every phase:

- Indexing runs as an ordinary-user companion process. A Windows Service, elevation,
  and machine-wide data collection are not default requirements.
- Application startup and command input never wait for an initial scan. A missing,
  incompatible, or restarting indexer degrades to command and natural-language behavior.
- Queries read an immutable published generation while maintenance builds the next one.
  Publishing a generation is atomic from the query path's perspective.
- Editor revisions remain authoritative. A result for an older text or mode revision
  cannot replace or activate the current candidate list.
- `Gen` gives indexed filesystem candidates priority, then uses command candidates for
  remaining direct-result capacity. `Cmd` uses command candidates only, and `Ask` never
  queries the filesystem index.
- A non-empty new editor revision selects and highlights its first candidate. A
  same-revision index refresh preserves selection when the same stable candidate token
  remains present and falls back to the first candidate otherwise. When no candidates
  exist, Enter submits input and does not close the input.
- Candidate capacity is policy owned by managed options, not a rendering constant. The
  current default is five direct results and one reserved Global Search row.
- Full paths are reconstructed only for bounded results. Per-entry runtime storage must
  remain compact and contiguous instead of retaining one complete path allocation per
  file or directory.
- Index data remains local. Content extraction, semantic processing, and network access
  require separate, explicit product decisions.

## Phase 1 — Prefix-index baseline `[Complete]`

Phase 1 establishes an end-to-end, recoverable filename search path:

- `LuvLetter.IndexKernel` owns a compact C++20 directory table, file records, shared
  UTF-16 string pool, stable path identifiers, and case-insensitive Unicode prefix
  lookup.
- `LuvLetter.Indexer.exe` is a hidden companion connected through a current-user Named
  Pipe. It exits when its parent exits or the pipe disconnects.
- A validated v2 snapshot is loaded before maintenance begins. The companion rebuilds
  in Windows background-processing mode, publishes immutable generations, and performs
  a six-hour full reconciliation.
- The `LLIX` v1 protocol provides handshake, root configuration, revisioned queries,
  generation status, shutdown, and bounded error frames under a 1 MiB payload ceiling.
- Managed supervision restarts failed sessions without blocking Host startup. Generation
  changes requeue the latest unchanged input so the first completed build appears
  without another keystroke.
- Native ABI v5 provides revisioned editor changes, atomic candidate snapshots, and
  candidate activation while keeping keyboard focus in InputWindow.
- `Gen` displays file-prefix results before matching commands and appends the reserved
  Global Search row. Up and Down select results; Enter opens a selected file and
  Shift+Enter reveals it in Explorer.

Phase 1 deliberately does not provide folder results, filesystem-event maintenance,
fuzzy ranking, pinyin matching, content indexing, or an implemented Global Search view.

## Phase 2 — Filesystem candidate completeness `[Complete]`

Phase 2 completes the expected file-and-folder candidate behavior and makes names created
during the current session searchable without waiting for the six-hour reconciliation.
Its Delta state is memory-only; durable change replay remains a later phase.

### Win32 enumeration and error isolation

- Replace recursive `std::filesystem` traversal with explicit Win32 directory
  enumeration and an iterative work queue.
- Treat access denial, disappearing paths, long paths, and malformed individual entries
  as directory-local failures. One unreadable directory must not discard results already
  collected from its siblings or abort the complete root.
- Do not follow reparse-point directories by default. This prevents junction and symbolic
  link cycles while preserving ordinary entries that can be activated safely.
- Continue honoring cancellation between directories and bounded enumeration batches so
  reconfiguration and shutdown remain responsive.

### Folder candidates and lightweight type icons

- Index directory names as first-class candidates with stable identifiers and lazily
  reconstructed paths. Root containers themselves are configuration scope, not ordinary
  search results unless explicitly requested later.
- Carry a small candidate-kind and icon-category value across the index protocol and
  Native ABI. Native renders lightweight built-in glyphs for folders and common file
  categories; candidate production must not synchronously extract shell icons.
- Keep icon metadata bounded and deterministic. Shell thumbnails, executable icon
  extraction, and per-path image caches are outside this phase.

### Deterministic ranking

Direct filesystem results use the following match tiers:

1. exact display-name match;
2. exact file-stem match;
3. display-name prefix match.

Within one tier, results sort by case-insensitive ordinal display name and its
case-sensitive spelling. When names tie, folders precede files, followed by stable
identifier and a reconstructed-path collision fallback. The same input and published
generation must therefore produce the same top results independently of filesystem
enumeration order.

No edit distance, token score, usage history, or pinyin score participates in Phase 2.

### Real-time name maintenance

- Maintain one cancellable `ReadDirectoryChangesW` subscription per eligible configured
  root and observe file and directory create, delete, and rename events.
- Coalesce bursts for 250–500 ms before publishing them. Filesystem callbacks append
  bounded change descriptions and never rebuild paths, mutate the base snapshot, or
  block candidate queries directly.
- Apply coalesced changes to an in-memory Delta containing upserts and tombstones. Query
  results merge the immutable base generation with one atomically published Delta
  generation under the same ranking rules.
- Represent a deleted or renamed directory with prefix filtering so descendants from the
  base snapshot disappear immediately without materializing one tombstone per child.
- Treat notification-buffer overflow, watcher loss, ambiguous rename pairs, root
  disconnect, or Delta count and memory thresholds as unsafe state. Discard uncertain
  incremental assumptions and schedule a complete background rebuild.
- Retain the six-hour full reconciliation as a correctness safety net. Phase 2 does not
  persist Delta state or claim to recover changes that occurred while LuvLetter was not
  running.

### Snapshot provenance and compatibility

- Persist a canonical root-set fingerprint and the index-affecting enumeration policy in
  the next snapshot schema. A valid snapshot may be served immediately only when its
  provenance matches the configured scope.
- Upgrade the snapshot layout for file and directory candidates, match metadata, and a
  payload checksum. Invalid, truncated, incompatible, checksum-failed, or
  scope-mismatched snapshots are discarded and rebuilt without failing application
  startup.
- Upgrade `LLIX` and the Native ABI when folder kind and icon category cross their
  boundaries. All new layouts remain fixed-width, size-checked, and version-gated.
  Mixed versions fail the session explicitly instead of interpreting old memory or wire
  data as a new structure.

### Index lifecycle feedback

- Report `Ready`, `InitialBuild`, and `Updating` explicitly in LLIX status responses.
  Generation changes alone do not distinguish the first scan from background maintenance.
- Present initial construction as the persistent `正在生成索引表` activity and later
  rebuilds, including the six-hour reconciliation, as `正在更新索引`.
- Keep the activity visible with a rotating indicator until a generation is successfully
  published, then convert it in place to the ordinary five-second `索引已就绪` message.
  Disconnects and shutdown dismiss the activity without reporting a false completion.

### Activation behavior and verification

- Validate the candidate path and expected filesystem kind immediately before activation.
  A successful file open, file reveal, folder open, or folder reveal closes InputWindow.
- A stale, missing, inaccessible, or kind-mismatched candidate reports a message and
  keeps the input open. Shift+Enter reveals a file or folder in its containing location;
  Enter opens the selected item using the Windows shell.
- The kernel suite covers file and directory records, Unicode prefix lookup,
  deterministic ranking, overlapping roots, v3 persistence, provenance mismatch,
  checksum corruption, Delta ordering, ancestor tombstones, rebuild-cutoff pruning, and
  live create/rename/delete notifications.
- Core and Native suites cover LLIX v3 activity decoding, Native ABI v7, icon categories,
  default and same-revision selection, persistent message timelines, stale revision
  rejection, and file/folder activation success or failure.

Phase 2 is implemented. The manual acceptance checklist remains the release-validation
surface for focus, Shell activation, redirected Known Folders, notification overflow,
and perceived performance on representative machines.

## Phase 3 — Persistent incremental recovery `[Planned]`

Phase 3 makes bounded incremental maintenance survive companion restarts and reduces the
frequency of complete construction:

- Persist an integrity-checked incremental log or Delta checkpoint only after its base
  generation and root fingerprint have been durably identified.
- Replay complete persisted batches on restart, reject partial tails, and never apply a
  Delta to a different base generation or configuration scope.
- Compact durable upserts, tombstones, and deleted-directory prefixes into a new immutable
  base after time, count, or memory thresholds, then atomically retire the old log.
- Record per-root maintenance state so one disconnected or invalid root does not prevent
  healthy roots from recovering.
- Reconcile after incomplete shutdown, watcher gaps, checkpoint mismatch, or log
  corruption. Recovery prefers a safe full rebuild over silently incomplete results.

Network shares, removable media, and filesystems without dependable change notifications
continue to use bounded reconciliation rather than receiving weaker correctness claims.

## Phase 4 — User-controlled scope and operations `[Planned]`

Phase 4 exposes already-established indexing policy through configuration and Settings:

- Configure included roots, excluded subtrees or patterns, candidate capacity, and the
  treatment of hidden, system, and reparse-point entries.
- Normalize, validate, migrate, and transactionally persist indexing settings through
  Core configuration. Native rendering does not read settings files directly.
- Display index status, active roots, last successful publication, entry counts, current
  generation, rebuild state, and recoverable diagnostics.
- Provide explicit rebuild, pause, and resume actions. Rebuild reuses the supervised
  companion lifecycle and never launches a second competing indexer.
- Make scope changes invalidate mismatched snapshots and watchers predictably, while
  preserving command and `Ask` behavior during replacement.

## Phase 5 — Measured scale optimization `[Planned]`

Phase 5 changes storage only in response to repeatable measurements:

- Add benchmark fixtures at approximately 10 thousand, 100 thousand, and 1 million
  entries, including long Unicode names, many identical basenames, deep directories,
  cold snapshot load, warm query, rebuild cancellation, and concurrent queries.
- Record steady-state working set, persisted bytes per entry, peak build memory, scan
  throughput, snapshot load time, and p50/p95/p99 query latency.
- Introduce memory-mapped, sectioned snapshots when mapping materially lowers startup or
  steady-state cost. Every section carries checked offsets, lengths, alignment, and an
  integrity boundary before it becomes queryable.
- Reduce construction peak memory with bounded batches, compact temporary identifiers,
  and external or segmented sorting only when the benchmark corpus demonstrates need.
- Keep the simpler in-memory path for small indexes if mmap setup or paging would cost
  more than it saves.

## Phase 6 — NTFS offline catch-up `[Planned, conditional]`

Phase 6 may use the NTFS USN Change Journal to catch up changes that occurred while
LuvLetter was not running:

- Store per-volume journal identity and checkpoint data, validate them at startup, and
  replay only a complete, continuous change range.
- Detect journal deletion, wrap, volume replacement, unsupported filesystems, and access
  denial. Any uncertain checkpoint falls back to the Phase 3 reconciliation path.
- Keep watcher and full-scan implementations as supported fallbacks for non-NTFS,
  network, removable, and policy-restricted roots.
- Add direct MFT enumeration for initial construction only if Phase 5 benchmarks prove
  that ordinary Win32 enumeration cannot meet the agreed startup or rebuild budget.

USN and MFT acceleration do not imply a Windows Service or administrator requirement.
If a target volume cannot be accessed as an ordinary user, LuvLetter uses the safe
fallback rather than requesting elevation silently.

## Phase 7 — Optional retrieval layers `[Planned, optional]`

These capabilities build on the stable filename index but remain independently optional:

- Implement the reserved Global Search action as a cancellable, pageable search surface
  rather than increasing the keyboard candidate list without bound.
- Add path and word-boundary matching, then evaluate a bounded fuzzy or trigram index
  against the Phase 5 memory and latency budgets.
- Add pinyin matching as a language-specific strategy with its own compact data and an
  explicit enablement policy.
- Run content extraction in a separate constrained worker or plugin with file-type,
  privacy, size, battery, and CPU limits. Content data does not enter the filename
  snapshot format.
- Connect `Gen` and `Ask` to optional semantic or AI search only through Core contracts.
  Local filename and command behavior remains available when that integration is absent
  or offline.

## Compatibility strategy

Three version boundaries evolve independently:

- Native ABI versions protect in-process struct layouts and callbacks. Managed and Native
  must use exactly the same supported ABI version.
- `LLIX` major versions protect companion framing and payload layouts. The handshake
  rejects incompatible peers before root configuration or queries are accepted.
- Snapshot schema versions protect persisted sections and provenance. An unsupported or
  corrupt snapshot is cache loss, not application failure; the indexer rebuilds it.

Configuration schema migration remains a Core responsibility and is not coupled to any
of these binary versions. A release that changes more than one boundary documents each
change separately and keeps the no-index degraded path operational.

## Performance targets

Targets are measured on the Phase 5 benchmark corpora and on representative supported
Windows hardware. They are budgets, not reasons to return stale revisions or skip
validation:

- A valid persisted generation should become queryable within 250 ms of a completed pipe
  handshake on a warm local disk.
- Warm top-five filename queries over one million entries should remain below 10 ms at
  p95 and below 25 ms at p99 in the companion.
- End-to-end candidate refresh should remain below 50 ms at p95 when the companion is
  connected and no rebuild I/O is contending with the query.
- The idle companion should consume no continuous scan CPU. Status polling and blocked
  filesystem watches should remain effectively idle between events.
- The one-million-entry steady-state filename index should target at most 128 MiB. Phase
  5 should reduce peak construction memory toward twice the published snapshot size
  without moving unbounded allocation into the main process.
- Scanning, reconciliation, and compaction remain in Windows background-processing mode
  and must not cause observable input, caret, or candidate-navigation stalls.

If a target is missed, the benchmark result and chosen tradeoff are recorded before a
more complex index, journal, or persistence design is adopted.

## Manual acceptance checklist

The following scenarios should be repeated for every phase that changes indexing,
candidates, persistence, or activation:

1. Start with no snapshot, invoke InputWindow immediately, and confirm typing, commands,
   `Ask`, and the reserved Global Search row remain responsive while indexing runs.
2. Leave non-empty `Gen` input unchanged through the first build and confirm matching
   candidates refresh automatically when a new generation is published.
3. Start with a valid snapshot and confirm candidates are available before the background
   reconciliation completes.
4. Create, rename, and delete files and folders while LuvLetter is running; confirm the
   candidate index reflects each coalesced change within the documented 250–500 ms
   window without requiring another full scan.
5. Delete or rename a directory containing indexed descendants and confirm prefix
   filtering immediately hides all stale children.
6. Force notification overflow or exceed the configured Delta threshold and confirm a
   safe background rebuild replaces uncertain incremental state.
7. Include an unreadable or disappearing subdirectory beside readable siblings and
   confirm sibling file and folder results remain indexed.
8. Include deep Unicode names, case variants, duplicate basenames, extensionless files,
   hidden entries, and reparse-point cycles; confirm the documented policy and stable
   ordering.
9. Confirm exact display-name results precede exact stem results, which precede prefix
   results, and repeat the query after restart to confirm deterministic ordering.
10. Confirm file and folder rows have lightweight type glyphs without focus loss, shell
   icon extraction pauses, or candidate-window activation.
11. Confirm a non-empty candidate list starts with its first row visibly selected, Down
    moves to the second row, Up returns to the first, and Enter activates the highlighted
    row. With no candidates, confirm Enter submits normally and Escape follows the
    ordinary input-hide path.
12. Confirm Enter opens a selected file or folder and closes InputWindow only on success;
   confirm Shift+Enter reveals the selected item in its containing location.
13. Delete or replace a selected item before activation and confirm validation reports a
    message, does not open the wrong item, and keeps InputWindow open.
14. Change configured roots and confirm a snapshot from the previous scope is not exposed
    as if it belonged to the new scope.
15. Corrupt or truncate the snapshot and confirm startup degrades to an empty index,
    rebuilds in the background, and leaves the main application operational.
16. Terminate the companion during a query and confirm supervision restarts it, stale
    results are rejected, and the current input refreshes after readiness returns.
17. Exercise `Gen`, `Ask`, and `Cmd` with identical text and confirm their candidate and
    submission rules remain isolated.
18. Observe a large rebuild on battery and AC power and confirm Windows reports background
    processing behavior while input animation and keyboard navigation remain smooth.
19. Start without a compatible snapshot and confirm `正在生成索引表` remains visible with a
    rotating indicator until publication, then becomes `索引已就绪` for five seconds.
20. Start with a compatible snapshot or trigger scheduled maintenance and confirm the
    persistent activity reads `正在更新索引`; hiding the queue must not consume continuous
    animation CPU, and showing it again must resume the spinner.

Automated suites cover deterministic logic and protocol boundaries, but they do not
replace these user-driven focus, shell-activation, and perceived-performance checks.
