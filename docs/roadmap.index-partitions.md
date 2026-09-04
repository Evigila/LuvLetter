# Partitioned Index Architecture `[Implementation Baseline]`

This document is the implementation specification for expanding searchable coverage while
keeping startup and maintenance bounded. It replaces the earlier evaluation. The first
runtime baseline now implements independent application-source partitions and
non-overlapping filesystem partitions with per-partition cache, Delta, scheduling, and
publication. Later sections distinguish that baseline from staged live ownership changes,
durable Delta recovery, optional broad-volume coverage, and Settings controls that remain
planned.

## Goals and boundaries

The design has two independent responsibilities:

- **Coverage** selects discovery providers and configured filesystem roots. It determines
  whether LuvLetter can represent an object at all.
- **Maintenance** divides represented objects into independently cached, refreshed, and
  published units. It determines availability, freshness, and failure isolation.

Partitioning does not turn an unsupported Shell entry into an application and does not
implicitly enable whole-disk scope. Application coverage therefore grows through explicit
providers. Filesystem coverage grows through configured roots/volumes. Windows and
Program Files executables may be optional file results, but raw recursive scans are not
the primary application-discovery mechanism.

The partition system optimizes startup publication, rebuild peak cost, cache writes, and
failure isolation. It does not inherently reduce the number of indexed entities or final
memory. Hot application/file indexes stay resident; broad cold file snapshots should use
memory mapping and demand paging once measured memory requires it.

## Vocabulary

The implementation uses four separate concepts:

| Concept | Responsibility | Example |
| --- | --- | --- |
| `IndexSource` | How entities are discovered | AppsFolder, Start Menu, App Paths, filesystem traversal |
| `IndexPartition` | Authoritative cache, refresh, failure, and publication boundary | `apps.start-menu.user`, `files.user-remainder` |
| `MaintenanceTier` | Startup importance and freshness policy | StartupCritical, Active, Normal, Background |
| `IndexView` | Immutable query aggregation over committed partition snapshots | Current application/file search view |

A tier is scheduling metadata. It is neither an index nor a search-ranking boost. Cache
availability, freshness, and maintenance priority never enter candidate scoring.

A `PartitionDescriptor` contains the stable partition ID, source kind, entity kind,
scope, exclusions, tier, resource lane, and maintenance policy. A
`PartitionSnapshot` contains its generation, scope and exclusion fingerprints,
ownership epoch, immutable search structure, and source checkpoint when supported.

Availability and freshness are independent:

```text
Availability: Unavailable | Cached | Ready
Freshness:    Current | Stale | PossiblyIncomplete
Refresh:      Idle | Queued | Building | Publishing | Failed | Backoff
```

Cached/Stale is searchable while a refresh runs. A source failure must not make every
other partition appear unavailable.

## Default partition set

Start with a small, stable set. Do not create one partition for every ordinary directory.

### Application partitions `[Implemented]`

- `start-menu:user`
- `start-menu:common`
- `apps-folder`
- `app-paths:user`
- `app-paths:machine`
- `system:curated`
- one hashed-cache `portable:<normalized-root>` partition per configured portable root

Each provider publishes independently. Shell sources use stable Shell identities and
retain their activation semantics. App Paths user/machine partitions preserve precedence
in the query view rather than deleting source records during discovery.

The system-entry provider covers locally available, curated Windows launch identities:
supported `ms-settings:` pages, Control Panel canonical entries, management-console
entries, and a bounded known-system-tool catalog. It validates OS availability and keeps
localized display names/aliases. It never treats arbitrary URI text as executable data.

The Shell provider retains both package AUMIDs and non-package launchable AppsFolder
items. Start Menu discovery retains trusted entries from the user/common Programs roots
when they use executable, Shell/PIDL, supported URI, `.msc`, or `.cpl` activation.
Original link/Shell identity is the launch target. Known resolved paths still participate
in full-ignore checks.

An execution-alias provider can be added after these sources if coverage measurements
show a remaining gap. Recursive Windows/Program Files scans are optional file coverage
and must not promote every helper executable to an application.

### Filesystem partitions `[Implemented for configured roots]`

- `filesystem:desktop` and `filesystem:downloads`: startup-critical roots when configured.
- `filesystem:user-profile`: the user-profile remainder, excluding delegated roots.
- `filesystem:root-<scope-hash>`: each other configured root, including redirected Known
  Folders and explicitly configured project or reparse roots.
- `files.volume.<volume-id>`: optional remainder of an enabled fixed volume.
- `files.network.<scope-hash>`: one bounded partition per enabled network root.
- `files.removable.<volume-id>`: one partition per enabled removable volume.

A fixed volume, network share, or removable volume beyond current user scope is an
explicit coverage choice. Removable identity uses volume identity rather than drive letter.
Reparse directories are entries but are not traversed by default. Explicit reparse-root
behavior must preserve one-owner rules and prevent cycles.

An explicit reparse root is the one ownership exception that needs fan-out semantics:
the parent owns the link directory entity, while the explicit child partition owns the
target tree. Rename/delete of the link updates the parent Delta and marks the child dirty;
events below the target route to the most specific root. Ordinary nested non-reparse
roots are delegated or folded and never scanned twice.

## Filesystem ownership

Within one `OwnershipEpoch`, every normalized filesystem path has exactly one
authoritative filesystem partition. The most specific configured scope owns the path.
Parent partitions exclude delegated subtrees from traversal and watcher publication.
The complete file view is the union of these non-overlapping owners.

Application launch records and filesystem records are different entity types. An
application may refer to an executable without transferring filesystem ownership.
Application equivalence requires the complete launch identity: provider identity,
activation kind/target, arguments, working directory, and relevant Shell semantics.
A matching executable path alone is insufficient.

The current file index retains its existing stable record identifier and normalized path
fallback. Volume ID plus file ID identity remains planned for stronger cross-path move
tracking and later usage ranking. Launch identities remain independent.

### Ownership changes

A scope/configuration change creates `OwnershipEpoch + 1`:

1. Normalize and validate the proposed ownership map and all global exclusions.
2. Capture an event sequence checkpoint and keep the old SnapshotSet queryable.
3. Build affected new-owner partitions in staging. Route or journal changes affecting
   both old and staged ownership after the checkpoint.
4. Under a short coordinator gate, drain staged events through a cutover checkpoint.
5. Write and validate all new partition generations and the new SnapshotSet manifest.
6. Atomically replace the manifest and swap the in-memory SnapshotSet to the new epoch.
7. Route buffered later events to the new owners, then asynchronously retire old files.

A crash before manifest replacement restores the old epoch. A crash after replacement
loads only complete referenced generations from the new epoch. Queries never combine
incompatible ownership epochs. Unaffected snapshot data may be reused only when its
scope/exclusion fingerprint is compatible with the new manifest.

A first implementation may require restart to apply scope changes, but the manifest,
epoch, and staging format must follow this protocol so live migration can be added
without changing cache correctness.

`OwnershipEpoch` fences ownership-map changes only. Ordinary refreshes use independent
per-partition generations and never wait for a global generation or two-phase commit.
The coordinated SnapshotSet cutover is required only when affected path ownership moves.

Do not implement an overlapping full baseline plus hot overlays in the first version.
That model requires tombstones, authoritative overrides, generation frontiers, and
cross-layer move recovery. Non-overlapping ownership provides full configured coverage
without resurrecting deleted baseline records.

## Cache and atomic publication

The application baseline uses independent manifests and snapshots. The file baseline uses
one validated atomic snapshot and backup per partition:

```text
%LocalAppData%\LuvLetter\Index\v1\partitions\
  partition-<id-hash>.bin
  partition-<id-hash>.bin.bak

%LocalAppData%\LuvLetter\Applications\v2\partitions\
  <source-hash>.manifest.json
  <source-hash>.snapshot.json
  <source-hash>.manifest.json.bak
  <source-hash>.snapshot.json.bak
```

Application caches validate schema, source/scope and full-exclusion fingerprint,
generation, bounds, payload length, and checksum. File caches validate snapshot schema,
root plus delegated/full-exclusion fingerprint, bounds, ordering, and checksum. File
ownership epoch is currently an in-memory stale-work fence; a durable SnapshotSet manifest
and immutable numbered file generations remain part of the live-migration work.

The durable live-migration format will commit in this order:

1. Write `snapshot-<generation>.tmp`.
2. Flush data and validate header, bounds, and checksum.
3. Atomically rename it to the immutable generation filename.
4. Write and atomically replace `partition.json`.
5. Atomically publish a new in-memory SnapshotSet referencing that generation.
6. Keep at least the preceding valid generation and remove older unreferenced files later.

The current baseline instead publishes a validated in-memory generation immediately and
then atomically replaces that partition's backup/primary cache. A cache-write failure does
not withdraw the queryable generation.

An ownership change additionally writes all staged generations before atomically replacing
`snapshot-set.json`. A failed write or refresh leaves the previous generation referenced.
An invalid primary manifest may use a fully compatible backup. A partition cannot load a
cache excluded by current global rules. Deleting or changing one partition configuration
does not invalidate unrelated partition caches.

Durable Delta logs are not required for the first partition release. Cache data may be
stale after a crash and must reconcile in the background. Add a journal/checkpoint only
when measurements justify faster recovery; its commit boundary must reference a committed
snapshot generation.

The file MVP keeps the existing immutable baseline plus bounded in-memory Delta. A build
captures `(PartitionId, OwnershipEpoch, ScopeFingerprint, DeltaRevision)` outside the
query lock. Commit verifies the active owner/epoch/scope, swaps the baseline under the
short publication gate, and prunes only Delta changes at or below the captured revision.
Later events remain. A late build or watcher callback carrying an obsolete epoch is
discarded. Application partitions initially need a last-known-good baseline and dirty
bit rather than filesystem Delta; only an explicit successful empty result clears one.

## Scheduler

Queue due jobs together through bounded resource lanes rather than creating one thread
per partition:

| Lane | Initial concurrency | Work |
| --- | --- | --- |
| Cache I/O | 2 | Validate manifests, open/map snapshots |
| Shell STA | 1 | AppsFolder and Shell/link metadata |
| Registry | 1 | App Paths and related registrations |
| Local disk | 1 per physical volume, capped globally | Directory traversal and file cache writes |
| Network/removable | 1 | Slow or disconnectable roots |
| CPU | `min(max(CPU - 1, 1), 4)` | Normalize, build lookup structures, merge metadata |

These are conservative initial limits and must be configurable internally. Broad jobs
enumerate in bounded batches, observe cancellation between batches, and yield when a
StartupCritical job needs the same lane. One partition has at most one active build and
one coalesced follow-up request.

Scheduling score includes startup importance, overdue deadline, dirty age, starvation
age, and estimated cost. Aging must eventually run a background partition despite
continuous hot-source activity. Source backoff affects only the failed partition.

At startup:

1. Queue every compatible cache load; publish each independently when validated.
2. Queue each due refresh. StartupCritical work takes lane priority.
3. Publish a successful partition immediately; never wait for an all-source batch.
4. Continue background/wide reconciliation after the core search view is usable.

“All jobs are queued” does not imply simultaneous disk scanning. This model permits useful
parallelism across independent resources while avoiding same-disk contention. A small
scope is expected to finish early but is not assumed fast; provider isolation prevents a
slow small source from blocking other partitions.

## Maintenance policy

Intervals are maximum reconciliation ages, not unconditional full-scan timers:

| Tier | Default maximum age | Automatic scan gap | Normal update |
| --- | --- | --- | --- |
| StartupCritical applications | 6 minutes | 1 minute | Provider events, then targeted reconciliation |
| Active files | 6 minutes | 1 minute | Delta from watcher events |
| Normal user files | 30 minutes | 1 minute in the baseline | Delta from watcher events |
| Background volume | 6 hours | 15 minutes | Delta when reliable, otherwise bounded reconciliation |
| Network/removable | 30 minutes while available | exponential backoff after failure | Delta when available |

Keep current six-minute behavior for existing application/active scopes during migration.
These values become user-facing only after scan duration and reliability telemetry support
safe defaults.

Known-path processing order is:

```text
global FullIgnore
-> resolve authoritative partition
-> apply/remove the live Delta entry
-> ordinary rebuild-ignore decision
-> per-path 60-second cooldown
-> partition automatic-scan gap
-> queue/coalesce targeted reconciliation when required
```

Ordinary ignore blocks an automatic rebuild request but leaves searchable Delta and
periodic reconciliation intact. Full ignore blocks event admission, scanning, cached
publication, query results, and activation across all partitions. Local rules may narrow
one partition but cannot weaken global full exclusions.

Ordinary watcher events update Delta and do not normally rebuild. Overflow or watcher
loss marks only affected partitions `PossiblyIncomplete`, retains the current snapshot,
and queues targeted reconciliation with elevated recovery priority. Directory reconnect
does the same. File watchers are low-latency hints, not proof of completeness.

For capable local NTFS volumes, USN Change Journal recovery is an optional later
optimization. FileSystemWatcher plus targeted periodic reconciliation remains available
for other filesystems, network roots, removable media, and unavailable journals.

Internal `RefreshPartition(PartitionId, cause, force)` is required in the first
partition release. The public `index.refresh` fans out a force request to every enabled
partition. Force bypasses ordinary ignore, cooldown, and automatic gap; it still honors
full ignore, ownership, lane limits, and one-active-build per partition. A targeted source
retry never becomes a refresh of all healthy partitions.

## Query view

The coordinator atomically exposes one immutable `SnapshotSet`. A query captures it once
and does not mix generations published during that query. Every available partition uses
the same matching and ranking policy.

For the MVP, a short shared publication lease covers the bounded cross-partition query;
baseline/Delta publication takes the exclusive lease, while every scan/build runs outside
it. This closes mixed-generation reads without requiring lock-free RCU/MVCC or copying the
entire index on every event. Replace it only if measured query/publication contention
justifies a more complex read-view implementation.

Each partition exposes a score-ordered cursor using the global scorer. The aggregator
performs a k-way merge, stable identity/launch-equivalence grouping, and final Top K
selection. It requests additional cursor items when deduplication consumes results;
partition/tier order cannot create a hidden result quota. Stable ties use entity identity.
A later query sees later published generations.

Application bias, match score, and future usage priority remain the candidate-ranking
inputs. Tier, partition, cache/stale state, and publication time are excluded. A high-use
file can outrank an application regardless of its maintenance tier.

Queries continue while refreshes run. Removing a partition from a new ownership epoch is
visible only with the atomic SnapshotSet cutover. Activation revalidates the current
entity/launch descriptor and global full exclusions, so a stale token cannot launch a
removed or changed target.

## Status and diagnostics

Expose both aggregate and per-partition state:

- cache load/publish duration, build duration, entry count, generation, and bytes;
- availability, freshness, refresh state, last success, next deadline, and backoff;
- watcher health, overflow count, event backlog, cooldown and coalescing counts;
- current scope/source fingerprints and ownership epoch;
- first cached result, first StartupCritical-ready, and background completion times;
- query fan-out, per-partition matches, merge/dedup counts, and P50/P95 latency.

The user-visible ready state means all StartupCritical partitions have usable snapshots.
It does not wait for background volume refresh. Diagnostics must distinguish unsupported
source/activation kind, outside scope, full ignored, source unavailable, name unmatched,
and ranked below visible Top K.

Measure before changing concurrency and deadlines. Persist local aggregate metrics only
when a product decision introduces telemetry storage; console timings are sufficient for
the first implementation.

## Required invariants

1. One filesystem path has at most one authoritative owner per OwnershipEpoch.
2. Queries read only committed immutable snapshots from one captured SnapshotSet.
3. Refresh failure cannot remove the failed partition's last valid generation.
4. Maintenance priority and cache freshness never affect search rank.
5. Watcher events are hints; recovery must have targeted reconciliation.
6. Global full exclusions apply to events, builds, caches, queries, and activation.
7. Ownership/configuration changes publish old or new compatible state, never a mixture.
8. Application and file entities are not merged solely by executable path.
9. One partition never has overlapping builds; repeated work becomes one follow-up.
10. A failed/offline partition cannot block healthy partition publication or persistence.
11. Crash recovery selects a fully committed manifest/generation set.
12. A force-all refresh respects resource limits and does not create unbounded workers.

These invariants require focused tests before broad coverage is enabled.

## Delivery plan

### Phase 0 — Baseline and contracts `[Partially implemented]`

Record first cached/result-ready times, provider/root duration, entry counts, cache
load/write duration, query P50/P95, watcher overflow, and event backlog. Introduce
`IndexSource`, `IndexPartition`, `MaintenanceTier`, `PartitionSnapshot`,
`SnapshotSet`, resource lane, and targeted-refresh contracts without changing coverage.

### Phase 1 — Application coverage and partitions `[Implemented]`

Add non-package Shell entries, trusted non-executable Start Menu activation kinds,
localized aliases, curated system settings/control-panel/tool entries, and diagnostics.
Split current application sources into independent partitions with cache generations,
deadlines, retries, and immediate publication. Preserve existing launch revalidation and
ranking. This directly addresses missing Windows built-in software and current serial
batch publication.

### Phase 2 — Filesystem partitions `[Implemented baseline]`

Upgrade the companion protocol and snapshot layout to non-overlapping active, interactive,
user-remainder, and optional volume/root partitions. Add ownership maps/fingerprints and
capture an immutable SnapshotSet per query. Initially apply scope changes on restart while
using in-memory ownership epochs and scope-compatible per-partition caches. A durable
SnapshotSet manifest is deferred with live ownership migration.

### Phase 3 — Targeted Delta and recovery `[Implemented baseline]`

Route watcher events to owners, maintain per-partition Delta/cooldown/ignore state, mark
overflow per partition, and implement targeted reconciliation/retry. Cross-partition
rename sides already route to their respective owners. A staged runtime OwnershipEpoch
cutover remains planned; configuration changes currently take effect after restart. Keep
global force refresh as fan-out.

### Phase 4 — Scheduler and adaptive policy `[Partially implemented]`

The file runtime has one shared disk worker with tier, overdue age, starvation age, and
estimated-cost priority. Application discovery uses fixed STA workers and bounded
source-category dispatch with per-source exponential backoff. Per-volume lanes, recorded
deadline adaptation, broad-scan batching, cold snapshot memory mapping, and checkpoint/USN
recovery remain measurement-driven work.

### Phase 5 — User controls

Expose included roots/volumes, active-root assignment, tier/maximum age, partition state,
targeted refresh, and diagnostics in Settings. Preserve safe defaults and validate overlap
before applying a new ownership epoch.

## Manual acceptance matrix

The user performs runtime testing. The matrix spans the implemented baseline and later
planned phases; cases requiring a durable SnapshotSet manifest, precise overflow source,
or optional network/removable coverage are future acceptance gates:

- one slow/offline application provider beside healthy sources;
- corrupted primary and backup cache for only one partition;
- crash simulation before/after snapshot and manifest commit boundaries;
- querying while one partition publishes a new generation;
- repeated changes, cooldown, ordinary ignore, and full ignore in one owner;
- watcher overflow/loss (all-file recovery in the baseline, source-targeted after watcher
  attribution is added);
- delete, rename, and move within and across partition boundaries;
- adding/removing a nested active scope without duplicate or missing results;
- junction/reparse cycles and an explicit reparse root;
- network disconnect/reconnect and removable drive-letter change;
- force one partition internally and force all through `index.refresh`;
- broad reconciliation while StartupCritical search and activation remain responsive;
- equal matches across tiers using the same ranking, including a usage-boosted file;
- application/file records sharing an executable but retaining distinct launch semantics;
- non-package Shell, Settings URI, Control Panel, MMC, packaged, and executable launches;
- cached/stale/current status transitions and per-partition diagnostics.

Do not enable optional whole-volume defaults until coverage quality, index size, rebuild
cost, memory, and query latency meet measured acceptance thresholds.
