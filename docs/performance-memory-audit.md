# Performance and Resident-Memory Audit

This audit originally reviewed commit `3d0e56e` with interactive latency, idle efficiency,
and combined resident memory of `LuvLetter.exe` and `LuvLetter.Indexer.exe` as the
primary constraints. It is a static code review, not a benchmark report. A cost marked
**confirmed** follows directly from object ownership, loop bounds, or scheduling in the
code. Its real duration, allocation volume, and benefit still require measurement on a
representative machine.

## Implementation status

The first optimization pass implements the structural changes that preserve current
search coverage and cache compatibility. Debug and Release builds pass, but no runtime
numbers have been collected yet; the findings and targets below therefore remain the
baseline for manual measurement.

| Finding | Current status |
| --- | --- |
| Ready-state polling | Implemented as a five-second stable wait with an immediate semaphore wake for force refresh. The longer-term pushed protocol remains optional. |
| Application query | Implemented with publication-time compact-name keys, one query context, and a bounded 256-result heap. |
| Application cache retention | Raw manifest/snapshot byte arrays and the duplicate old entry graph are no longer retained. A catalog-wide byte budget remains deferred until source eviction policy is defined. |
| Candidate rendering | Ordinary result changes retain the DIB, render target, brushes, and text formats. Shell icons load on one STA worker into a bounded CPU cache; configuration, DPI, and device-loss paths recreate device resources without blocking candidate rendering. |
| Full-ignore matching | Implemented with an empty-list fast path, pre-normalized calls, and sorted boundary-aware prefix lookup. |
| Repeatable measurement | Added a two-process PowerShell sampler and a synthetic Release index-kernel query benchmark. Baseline capture remains a manual step. |
| Filename build and cache I/O | The builder writes final compact records directly. Cache records are decoded directly and saved with a 48 KiB streaming buffer and incremental checksum. |
| Delta and cross-partition merge | Both final merges are bounded to K. Delta still scans its upsert map and has count limits rather than a byte budget. |
| Watcher batches | Events are normalized once, grouped by owning partition, applied in one Delta batch, and logged as aggregate counts without reordering rename events. |
| Application publication | Semantically unchanged sources skip generation, cache write, merged publication, and `Changed`. Core caches application and file query results against independent revisions. Incremental deduplication-key publication remains deferred. |
| Candidate transfer | Candidate metadata and UTF-16 strings use pooled arrays and one pinned contiguous text region. Native pending-request replacement remains deferred. |
| Hidden UI memory | Explicit Settings close releases its visual tree. Native idle surface retirement remains measurement-dependent. |

The current architecture already has several useful performance properties:

- The filename snapshot stores compact directory and entity records plus one UTF-16
  string pool instead of retaining a full path per entity.
- Filename prefix lookup starts with binary search and materializes only a bounded Top K.
- One filesystem worker prevents multiple large scans or loads from peaking together.
- Application sources publish independently, and their Shell work is bounded by fixed
  STA workers.
- Input revisions, the one-item change channel, and bounded candidate counts limit stale
  work and UI state.
- Hidden windows do not continuously animate. Layered-window DIBs are created on first
  render rather than with the initial HWNDs.
- Candidate icon extraction does not run in the query or UI-render path. One STA worker
  coalesces identical requests, caps pending/completed work and cached images, and rejects
  results from stale candidate generations or DPI sizes before creating a D2D bitmap.

These strengths should be retained while the following costs are removed.

## Priority summary

| Priority | Work | Primary effect | Evidence |
| --- | --- | --- | --- |
| P0 | Replace 250 ms ready-state status polling with pushed state changes or a long idle wait plus an explicit wake signal. | Idle CPU, wakeups, IPC allocations | Confirmed |
| P0 | Precompute application search keys and use a bounded Top-K query rather than full-catalog LINQ sorting. | Keystroke latency, Gen 0 allocation | Confirmed |
| P0 | Stop retaining serialized application cache bytes and impose catalog-wide entry/byte budgets. | Resident memory, worst-case safety | Confirmed |
| P0 | Preserve candidate-window rendering resources across ordinary result updates. | Keystroke rendering latency, GDI/D2D churn | Confirmed |
| P0 | Add an empty-exclusion fast path and a compiled prefix matcher for full ignores. | File/application scan throughput | Confirmed; magnitude depends on scope |
| P0 | Add repeatable allocation, query, build, and working-set benchmarks before changing storage formats. | Prevent regressions and rank the remaining work | Missing evidence today |
| P1 | Remove temporary filename build/load/save copies; then evaluate memory-mapped cold snapshots. | Build peak, cache load peak, steady-state companion memory | Confirmed copies; mmap benefit must be measured |
| P1 | Give Delta a process-wide byte budget and a name-ordered query view. | Worst-case memory, changed-name query latency | Confirmed |
| P1 | Batch watcher routing/publication by owning partition. | Change-storm CPU, locks, allocations | Confirmed |
| P1 | Suppress semantically unchanged application publications and update merged keys incrementally. | Periodic/startup CPU and candidate requery | Confirmed |
| P1 | Pack managed/native candidate transfer into one buffer and make latest revision replace pending native work. | Keystroke allocation and stale work | Confirmed; magnitude must be measured |
| P2 | Release large hidden DIBs after an idle interval and destroy the hidden Settings visual tree on close. | Post-use resident memory | Confirmed retained resources; typical size must be measured |
| P2 | Consider IOCP watchers, lightweight tray/hosting replacements, PGO, and NTFS-specific enumeration only after profiles justify them. | Scale or framework baseline | Conditional |

## P0 findings

### 1. Ready-state polling prevents a quiet idle process

`FileIndexCompanionClient` sets both rebuilding and stable status intervals to 250 ms and
creates request/cancellation state for every poll. The ready application therefore sends
four named-pipe status round trips per second even when neither process has changed.
This contradicts the roadmap goal that idle status handling remain effectively idle.

Implement one of these designs:

1. Prefer an unsolicited `StateChanged`/`GenerationChanged` frame from the companion,
   handled by a single managed reader loop. Queries and commands receive responses by
   request ID.
2. As a smaller transition, retain 250 ms while building, wait 5–30 seconds while ready,
   and let `RequestRefresh` release a semaphore so force refresh does not wait for the
   stable interval.

The second design reduces ready-state polls by at least 95% at a five-second interval;
the push design removes the periodic idle work. Relevant code:

- `src/LuvLetter/Platform/Indexing/FileIndexCompanionClient.cs:16-17`
- `src/LuvLetter/Platform/Indexing/FileIndexCompanionClient.cs:289-347`
- `src/LuvLetter/Platform/Indexing/FileIndexCompanionClient.cs:506-557`

### 2. Application query work scales with the entire published catalog

Every keystroke maps every published entry to an anonymous object, filters it, fully
sorts the matches, and finally takes at most 256 results. `ApplicationNameMatcher` trims
and compacts the same query once per entry, then allocates a compact string for every
display name and alias that it examines. At the published limit this is work over
100,000 entries for one input revision, and it has no cancellation boundary.

Publish an immutable search representation containing the original entry plus
precomputed display and alias keys. Build one query context per revision. Query a
case-insensitive exact/prefix structure, or initially perform one allocation-light scan
with a fixed-size Top-K heap. Preserve the current ordering with a final sort over only
K results. Check cancellation periodically until the indexed design lands.

Localized aliases such as `Power Options` -> `电源选项` belong in this published search
representation. They should be normalized when a source publishes, not on every query.

Relevant code:

- `src/LuvLetter/Platform/Applications/WindowsApplicationCatalog.cs:148-159`
- `src/LuvLetter.Core/Application/ApplicationNameMatcher.cs:8-40`
- `src/LuvLetter/Platform/Applications/WindowsApplicationDiscovery.cs:344-373`

### 3. Application cache bytes duplicate the live object graph

Every successful cache load retains three forms of the same partition: deserialized
entries, manifest bytes, and the complete JSON snapshot bytes. `TrustedCache` keeps this
`ApplicationPartitionCacheHit` for the partition lifetime so the old bytes can later be
rewritten as a backup.

The limits are per source rather than global. Up to 64 portable roots plus six built-in
sources are allowed. Each partition permits 20,000 entries and a 16 MiB JSON snapshot,
while the 100,000 published-entry limit does not remove entries held by source
partitions. The contractual upper bound for retained raw snapshot bytes alone is roughly
70 x 16 MiB, over 1 GiB, before managed strings, records, dictionaries, and the merged
view. Normal installations will be much smaller, but the ownership is still wasteful and
the worst-case boundary is unsafe.

Use generation-named files or an atomic file rotation so the previous on-disk generation
becomes the backup without being held in memory or rewritten. Retain only generation,
checksum, and file identity after validation. Add process-wide entry and serialized-byte
budgets with deterministic source priority. A later compact binary format can reduce
load allocation, but removing the retained byte arrays gives the first direct resident
memory win without changing search semantics.

Relevant code:

- `src/LuvLetter/Platform/Applications/ApplicationCatalogOptions.cs:16-21`
- `src/LuvLetter/Platform/Applications/WindowsApplicationCatalog.cs:13-16`
- `src/LuvLetter/Platform/Applications/WindowsApplicationCatalog.cs:345-355`
- `src/LuvLetter/Platform/Applications/WindowsApplicationCatalog.cs:919-950`
- `src/LuvLetter/Platform/Applications/ApplicationPartitionCache.cs:39-53`
- `src/LuvLetter/Platform/Applications/ApplicationPartitionCache.cs:65-86`

### 4. Candidate updates recreate rendering resources

`InputCandidatesWindow::SetItems` calls `DiscardResources(true)` for every accepted
candidate revision. That releases the DIB, D2D render target, brushes, and text formats;
`Show` immediately recreates them to render the new rows. This runs on the result path
after a typical keystroke.

Keep the render target, text formats, and brushes across content changes. Let
`LayeredWindowSurface::Ensure` resize the DIB only when pixel dimensions actually change.
Continue discarding resources for configuration/DPI changes and
`D2DERR_RECREATE_TARGET`. This preserves rendering behavior while eliminating repeated
GDI/COM allocation when the result count and DPI are stable.

Relevant code:

- `src/LuvLetter.Native/windows/InputCandidatesWindow.cpp:313-336`
- `src/LuvLetter.Native/windows/InputCandidatesWindow.cpp:381-399`
- `src/LuvLetter.Native/windows/InputCandidatesWindow.cpp:561-566`
- `src/LuvLetter.Native/rendering/LayeredWindowSurface.cpp:117-183`

### 5. Full-ignore matching sits inside enumeration

Filename construction calls `PathExclusions::Contains` for every encountered item.
`Contains` normalizes the candidate path and linearly checks every exclusion. Its worst
case is O(entries x exclusions), with path work in the inner loop. Application discovery
also normalizes each enumerated path even when the default full-ignore list is empty; a
watcher path can be normalized twice.

Return immediately when there are no exclusions. Compile normalized directory scopes
and exact files into an immutable boundary-aware trie or sorted-prefix matcher. Expose a
`ContainsNormalized` path for enumeration and watcher routes that already produced a
canonical path. Preserve drive, UNC, case-insensitive, and directory-boundary semantics.

Relevant code:

- `src/LuvLetter.IndexKernel/src/FileIndex.cpp:415-439`
- `src/LuvLetter.IndexKernel/src/FileIndex.cpp:945-955`
- `src/LuvLetter/Platform/Applications/WindowsApplicationCatalog.cs:174-184`
- `src/LuvLetter/Platform/Applications/WindowsApplicationDiscovery.cs:404-445`

## P1 findings

### 6. Filename construction and persistence have high transient peaks

The steady-state snapshot is compact. Ignoring allocator slack, one partition occupies
approximately:

```text
12 * directory_count + 24 * entity_count + 2 * pooled_utf16_char_count bytes
```

It does not store a full path for every entity. Construction, however, first retains
`TemporaryDirectory` and `TemporaryEntity` records with independent `std::wstring`
allocations and a pending stack containing complete directory paths. It then creates the
packed records and string pool while the temporary representation is still alive. The
old published snapshot must also remain queryable until the new generation commits.

Loading allocates serialized record bytes and then decoded record vectors concurrently.
Saving serializes all directory/entity records into another byte vector. The store saves
a backup and primary separately, so it hashes, flushes, and can write the same first
generation twice. If persistence repeatedly fails, `snapshot` can retain the new
generation while `cached` retains the old generation.

Use one arena/string pool and compact offsets during enumeration, retaining sortable
entity metadata rather than one heap string per entry. Store only a directory index in
the pending traversal stack and reconstruct that directory path when it is popped.
Stream encoded records to disk while hashing, and rotate the previous disk file to the
backup before installing the new generation. Decode directly into final vectors on the
current format as a first step.

After those changes are benchmarked, add a sectioned, validated memory-mapped reader for
broad cold partitions. Mapping should remain optional for small hot partitions where an
in-memory vector can be faster. The map must validate header, bounds, alignment,
checksum, record parent ordering, kind, and sort order before publication.

Relevant code:

- `src/LuvLetter.IndexKernel/src/FileIndex.cpp:37-52`
- `src/LuvLetter.IndexKernel/src/FileIndex.cpp:680-728`
- `src/LuvLetter.IndexKernel/src/FileIndex.cpp:742-863`
- `src/LuvLetter.IndexKernel/src/FileIndex.cpp:865-1057`
- `src/LuvLetter.Indexer/PartitionedIndexStore.h:306-339`

### 7. Cross-partition and Delta query bounds do not scale to configured maxima

For every partition, the store asks for a complete K-result vector, reconstructing names
and paths before the global merge. It then deduplicates each candidate with a linear
scan of the growing merged vector and sorts the result. With the protocol maxima of 1024
partitions and K=256, this can materialize 262,144 string-owning candidates and perform
quadratic path comparisons on the synchronous pipe request. The default partition count
is small, so the practical priority depends on future scope configuration.

Capture lightweight per-partition iterators/cursors and perform a K-way merge into a
bounded heap. Use stable path identity plus collision-safe equality for deduplication.
Construct the final display/path strings only for surviving results. Keep exact-name,
exact-stem, prefix, application/file ranking, and deterministic tie-break semantics.

The Delta adds a second scale issue: every query scans all upserts, reserves capacity for
all of them, and creates a path dictionary even when few changed names match. Maintain a
case-folded name-ordered side index and bound the final merge to K. Add a process-wide
byte budget in addition to per-partition entry counts; count-only limits do not constrain
long path strings, and multiplying per-partition limits by 1024 is not safe.

Relevant code:

- `src/LuvLetter.Indexer/PartitionedIndexStore.h:111-129`
- `src/LuvLetter.Indexer/Main.cpp:329-374`
- `src/LuvLetter.Indexer/IndexMaintenance.cpp:241-330`
- `src/LuvLetter.Indexer/IndexMaintenance.cpp:18-20`
- `src/LuvLetter.Indexer/IndexRebuildPolicy.h:128`

### 8. Watcher batches are processed again as individual events

The watcher publishes a coalesced batch, but `PartitionedIndexStore::ApplyChanges`
routes and applies each path separately. Each event repeats longest-root resolution,
path normalization, Delta locking, state locking, aggregate status scans, notification,
and logging. A batch can contain thousands of entries, and the worker can observe a
partially processed batch.

Normalize and deduplicate a batch once, resolve owners once, retain rename ordering, and
group operations by partition. Apply one Delta batch and one state/status update per
owner. Aggregate high-frequency accepted/refused log counts while keeping sampled paths
for diagnosis. Cache normalized roots and lengths in the ownership map.

Relevant code:

- `src/LuvLetter.Indexer/PartitionedIndexStore.h:191-239`
- `src/LuvLetter.Core/Application/IndexPartitions/OwnershipMap.cs:24-45`
- `src/LuvLetter.Indexer/IndexMaintenance.cpp:630-682`

### 9. Application publication repeatedly rebuilds the whole merged view

Every successful cache load or source discovery flattens all usable sources, filters,
groups, sorts each duplicate group, rebuilds alias arrays, sorts the global result, and
recreates the ID dictionary. It raises `Changed` even when the discovered source is
semantically identical. The candidate coordinator then re-queries both applications and
the filename companion for non-empty input. Startup can publish twice per source: cache
first and discovery later.

Persist a semantic content hash per source and update freshness without publishing when
content is unchanged. Maintain a deduplication-key map whose winners and merged aliases
are recalculated only for keys touched by the changed source. Include source and revision
in the change notification so Core can reuse the unaffected half of a query. Coalesce a
short startup burst while still publishing the first usable cached source immediately.

Relevant code:

- `src/LuvLetter/Platform/Applications/WindowsApplicationCatalog.cs:410-470`
- `src/LuvLetter/Platform/Applications/WindowsApplicationCatalog.cs:508-546`
- `src/LuvLetter.Core/Application/InputCandidateCoordinator.cs:192-209`
- `src/LuvLetter.Core/Application/InputCandidateCoordinator.cs:477-515`

### 10. Application discovery allocates large shortcut buffers and serializes unrelated work

Each `.lnk` read creates three `StringBuilder` instances with 32,768-character capacity;
target resolution can create another buffer of that size. A thousand shortcuts therefore
request roughly 250 MiB of UTF-16 character buffers over the discovery pass before COM
wrappers and returned strings are counted. Localized display-name discovery also creates
Shell automation objects repeatedly, and executable sources read `FileVersionInfo` for
each candidate.

Use small initial or pooled buffers and grow only on truncation while retaining long-path
support. Reuse Shell folder objects per directory, cache executable descriptions by
path/size/last-write, and make localized enrichment independently measurable.

Catalog category limits also do not produce the intended concurrency for every source.
App Paths, portable roots, and curated definitions all pass through one general STA
queue. Keep COM ShellLink/AppsFolder operations on STA, but move registry reads and plain
filesystem enumeration to bounded non-STA lanes. A slow portable root must not block App
Paths or curated entries.

Relevant code:

- `src/LuvLetter/Platform/Applications/WindowsShortcut.cs:18-38`
- `src/LuvLetter/Platform/Applications/WindowsShell.cs:15-29`
- `src/LuvLetter/Platform/Applications/WindowsShell.cs:108-178`
- `src/LuvLetter/Platform/Applications/WindowsApplicationDiscovery.cs:98-128`
- `src/LuvLetter/Platform/Applications/WindowsApplicationDiscovery.cs:269-289`

### 11. Candidate publication crosses several allocation boundaries

The managed coordinator builds records, arrays, and identity dictionaries for each
publication. `NativeShellService` then allocates two unmanaged strings for every row.
Native copies those strings into `std::wstring` members, allocates a `HostRequest`, creates
a completion event, posts the request to the native UI thread, waits synchronously, and
then managed frees all temporary buffers. Application candidates can cause an immediate
publication followed by a second publication when filename results arrive.

Pack all candidate metadata and UTF-16 text into one bounded buffer with offsets. Because
the count and text limits are already bounded, one native request can own that buffer and
replace an older pending candidate revision before it renders. Reuse candidate target
maps when identities are unchanged. Preserve synchronous lifetime guarantees for the
existing ABI until the packed request owns all memory.

The current named-pipe query also holds a single I/O lock. Once a stale query frame has
been sent, caller cancellation cannot interrupt its read without breaking framing, so the
newest query waits for the old response. The long-term push/reader-loop protocol from
finding 1 should dispatch responses by request ID and support latest-revision
cancellation. A small typing debounce before filesystem IPC is a safe interim option;
application matches can remain immediate.

Relevant code:

- `src/LuvLetter.Core/Application/InputCandidateCoordinator.cs:518-690`
- `src/LuvLetter.Core/NativeShell/NativeShellService.cs:195-274`
- `src/LuvLetter.Native/host/NativeShellHost.cpp:66-119`
- `src/LuvLetter.Native/host/NativeShellHost.cpp:422-490`
- `src/LuvLetter.Native/host/NativeShellHost.cpp:614-716`
- `src/LuvLetter/Platform/Indexing/FileIndexCompanionClient.cs:431-503`

## P2 and conditional findings

### 12. Hidden UI state remains resident after first use

The native host creates four HWNDs at startup, but their DIB surfaces are lazy. Once a
surface renders, hiding the window does not normally reset it. The DIB cost is exactly
`pixel_width * pixel_height * 4`. Defaults are modest; high DPI, 32 candidate rows, or
maximum user dimensions can make retained surfaces material. Release candidate,
Quick Actions, and message surfaces after a measured idle interval, keeping small text
state so recreation is correct. Avoid releasing the input surface immediately if that
hurts invocation latency.

Each surface also creates a click-through shadow HWND, but its DIB is allocated only when
the corresponding content first renders and is released immediately when that content
hides. The shadow adds an 8-DIP perimeter while visible and caches pixels until its shape,
opacity, size, or DPI changes; caret blinking and a stable message spinner therefore do
not rebuild it.

The coded caps are also material at the supported extremes: Input, candidates, and
Quick Actions each permit up to 16 million pixels (64 MiB at 32 bits per pixel), while
the message queue permits four million pixels (16 MiB). Display and geometry constraints
normally keep real allocations below those individual caps, but hidden surfaces can
theoretically retain a combined 208 MiB after use.

The WPF Settings window is created lazily, which is good, but closing/minimizing only
hides it and `TrayIconService` retains the instance. Its visual tree remains resident for
the process lifetime after first use. Detach handlers and dispose/recreate Settings on
close if profiling shows a meaningful post-use increase.

Relevant code:

- `src/LuvLetter.Native/host/NativeShellHost.cpp:1035-1052`
- `src/LuvLetter.Native/host/NativeShellHost.cpp:1157-1185`
- `src/LuvLetter.Native/rendering/LayeredWindowSurface.cpp:117-217`
- `src/LuvLetter/Platform/Tray/TrayIconService.cs:135-198`

### 13. Framework baseline and watcher topology need evidence before redesign

The host enables both WPF and Windows Forms and uses Generic Host. Windows Forms supplies
the tray icon, while WPF supplies Settings and message boxes. Replacing the tray with
Win32 may allow Windows Forms assemblies to remain unloaded; replacing WPF or the host
container is much broader. Measure loaded modules, managed heap, and private working set
before accepting that complexity.

Filesystem monitoring uses one thread, 32 KiB event buffer, directory handle, and
overlapped wait state per outer watch root, plus one publisher thread. Nested default
partitions generally collapse to one profile watch, so this is reasonable today. Many
disjoint, network, or removable roots should use IOCP/thread-pool waits and exponential
reopen backoff rather than a permanent thread and 250 ms failed-root retry each.

Relevant code:

- `src/LuvLetter/LuvLetter.csproj:7-8`
- `src/LuvLetter/Program.cs:22-30`
- `src/LuvLetter/Platform/Tray/TrayIconService.cs:18-40`
- `src/LuvLetter.Indexer/IndexMaintenance.cpp:441-458`
- `src/LuvLetter.Indexer/IndexMaintenance.cpp:503-595`

### 14. Defer micro-optimizations until the structural costs are gone

There are smaller allocation opportunities in filename path reconstruction, protocol
encoding, ranking records, command LINQ, and native request objects. Examples include
calculating a result path length before one allocation, using an `ArrayBufferWriter` for
frames, making ranking context a value type, and reusing an overlapped event. Their work
is already bounded by small K values. They should follow polling, full-catalog queries,
cache duplication, build peaks, and render-resource churn.

Native Release builds already enable whole-program optimization, intrinsic functions,
function-level linking, reference optimization, and COMDAT folding. Profile-guided
optimization should be evaluated against a captured hot workload rather than enabled as
a substitute for algorithmic changes. Direct MFT enumeration and USN catch-up remain
conditional because they add filesystem and recovery complexity without reducing every
steady-state memory cost.

## Measurement plan

Measure the two-process total and each process separately. A working-set figure alone is
insufficient because mapped clean pages can be reclaimed. Record private working set,
private bytes/commit, total working set, managed GC heap, allocation rate, Gen 0/1/2
collections, CPU time, context switches/wakeups, thread count, and handle count.

With LuvLetter already running, `scripts/measure-performance.ps1` records working set,
private bytes, cumulative CPU, threads, and handles for both processes plus a combined
row. Its default ten-minute capture writes under `artifacts/performance`. Allocation,
GC, context-switch, and query-percentile data still require an ETW/.NET trace and the
instrumented benchmark fixtures described below.

`LuvLetter.IndexKernel.Benchmarks` constructs a compact synthetic filename snapshot and
reports construction time, private/working-set change, and warm Top-5 p50/p95/p99 query
latency. Its positional arguments are entity count, iteration count, and query; defaults
are 100,000, 2,000, and `item0000000`. Run its Release build for comparisons. This
isolates the kernel data structure; it does not replace end-to-end or real-disk scans.

Use repeatable corpora near 10,000, 100,000, and 1,000,000 filename entries. Include deep
paths, long Unicode names, duplicate basenames, large ignored dependency trees, 0/64/1024
full exclusions, and controlled Delta sizes. Record:

- cold build scan rate and peak combined commit;
- warm cache time to first queryable critical partition and background completion;
- snapshot bytes per entity and steady companion memory per loaded partition;
- warm query p50/p95/p99 and allocation for exact, stem, common prefix, missing prefix,
  many duplicate names, large Delta, and removed-subtree filtering;
- watcher change-storm throughput, queue high-water mark, overflow, lock wait, and rebuild
  count;
- cache write bytes and duration for primary plus backup.

For applications, record per-source entries, aliases, serialized bytes, discovery time,
allocated bytes, and publish time. Query catalogs near 1,000, 20,000, and 100,000 entries
with exact, localized alias, compacted whitespace, common prefix, and miss cases. Record
candidate time to first application row, final merged row, allocations per keystroke,
stale-query count, and native render duration.

Run these lifecycle scenarios:

1. Warm startup, then ten idle minutes with no input or filesystem changes.
2. Cold startup without caches while opening input immediately.
3. Type and erase at 10 revisions per second for 30 seconds.
4. Leave a non-empty query open while each application source and file partition
   publishes.
5. Produce 4,096 file changes in one watcher batch and a large Delta.
6. Open and close Input, candidates, Quick Actions, message queue, and Settings; wait five
   minutes and compare memory with the pre-use baseline.
7. Rebuild one broad partition successfully, then repeat with cache persistence failure.
8. Add slow/offline disjoint roots and observe threads, retries, and healthy-source
   latency.

The existing roadmap targets remain the acceptance floor: a warm one-million-entry
filename Top 5 below 10 ms p95, end-to-end candidate refresh below 50 ms p95 without I/O
contention, at most 128 MiB for the one-million-entry steady filename index, and build
peak trending toward twice the published snapshot. Add host and combined-process budgets
only after the first repeatable baseline; otherwise a guessed number could reward moving
memory from one process or category to another.

## Recommended implementation order

1. Add the benchmark corpora, internal timers/counters, and an idle ten-minute capture.
2. Remove ready-state 250 ms polling, retained application JSON, and candidate render
   resource recreation.
3. Publish precomputed application search keys and replace the full LINQ sort with
   bounded Top K.
4. Compile full-ignore matchers and batch watcher events by owner.
5. Stop unchanged application publications and separate application/file change
   revisions in Core.
6. Reduce filename construction and cache I/O peaks on the existing v3 schema.
7. Add Delta byte budgets/name indexing and a bounded cross-partition merge.
8. Benchmark memory-mapped cold snapshots, packed native candidate transfer, hidden
   surface retirement, and framework replacement independently.

Each step should land with before/after numbers for its target scenario. Search quality,
localized aliases, deterministic ranking, previous-generation fallback, partition
isolation, and launch revalidation are correctness gates and must not be traded for a
faster average.
