# LuvLetter Architecture

LuvLetter is a Windows command shell with a WPF presentation shell, a WPF-independent
Core application layer, a native Win32/Direct2D renderer, and an out-of-process C++ file
indexer. `Microsoft.Extensions.Hosting` owns dependency composition and service lifecycle.

## Dependency direction

```text
LuvLetter (WPF views + Windows adapters + composition)
    -> LuvLetter.Core (application coordination + business modules + contracts)
    -> LuvLetter.Native (only through the versioned C ABI at runtime)
    -> LuvLetter.Indexer (only through the versioned Named Pipe protocol at runtime)
           -> LuvLetter.IndexKernel
```

Core targets `net10.0` and does not reference WPF or Windows Forms. WPF page code calls
Core services through interfaces such as `ISettingsService`; Core coordination calls
Windows implementations through `IApplicationShell`, `IActivationGestureService`, and
`INativeShell`.

## Project layout

### `src/LuvLetter`

- `View/Settings`: the settings page, control binding, and keyboard-event capture only.
  Parsing, validation, immutable mapping, apply, persistence, and rollback live in Core.
- `Platform/Activation`: the low-level Windows keyboard hook and Dispatcher adapter.
- `Platform/Indexing`: companion-process supervision, the Named Pipe client and protocol
  validation, default index roots, and Windows file open/reveal behavior.
- `Platform/Applications`: background STA discovery and activation for Start Menu,
  App Paths, packaged applications, and configured portable roots; independently
  persisted application catalog and maintenance.
- `Platform/Tray`: notification-area UI and settings-view lifetime.
- `Hosting`: Generic Host registrations and the WPF-specific `IHostLifetime`.
- `Program`: the STA/single-instance entry point. It builds and starts the Host, delegates
  the WPF dispatcher loop to `WpfHostLifetime`, then stops and disposes the Host.

### `src/LuvLetter.Core`

- `Application/ApplicationCoordinator`: the primary business coordinator. It applies the
  initial configuration, loads built-in and external plugins, synchronizes Quick
  Actions, subscribes runtime events, starts gestures, routes General/Ask/Command input,
  and performs idempotent shutdown. General mode first recognizes registered commands,
  then offers the text to ordered `IGeneralInputMatcher` implementations before falling
  back to an Echo response.
- `Application/InputCandidateCoordinator`: a separate hosted input pipeline. It consumes
  only the latest editor revision, ranks application and file candidates, fills command
  candidates according to the active mode, owns activation tokens, and rejects stale
  results before Native display. Application and file publication revisions are tracked
  independently, so a source-only refresh can reuse the unaffected half of the current
  query. Application contracts and the injectable ranking policy live alongside it;
  Windows metadata and launching stay behind these ports.
- `Modules/Settings`: one cohesive settings capability containing the public service port,
  editor DTOs, validation/mapping, transactional apply/rollback, and the non-removable
  built-in settings plugin.
- `Modules/QuickActions`: Quick Action definitions, snapshots, registrar capability,
  registry, and activation results.
- `Plugins`: dynamic assembly discovery and lifetime ownership. External extensions
  implement `ILuvLetterPlugin`; the default directory is `plugins`.
- `Commands`: bounded, serial command registration and dispatch.
- `Activation`: the deterministic global-shortcut state machine and platform port.
- `Configuration`: immutable models, schema migration, normalization, JSON persistence,
  and current-snapshot ownership.
- `NativeShell`: the `INativeShell` port plus the managed ABI adapter, token mapping, and
  bounded callback delivery. Rendering remains in the Native project.

Built-in product functionality implements the same plugin contract as dynamically
discovered extensions. The built-in settings plugin is always supplied by dependency
composition and cannot be removed; optional assemblies are discovered from `plugins`.

### `src/LuvLetter.Native`

- `api`: the stable C ABI and exception boundary.
- `configuration`: native defaults and defensive ABI validation.
- `host/NativeShellHost`: the Native UI-thread owner and request serializer.
- `windows/InputWindow`: input editing, history, IME, animation driving, and rendering.
- `windows/InputCandidatesWindow`: the non-activating, keyboard-driven candidate list
  positioned beside InputWindow, preferring the space above it and flipping below when
  necessary. InputWindow is the geometry root for the combined input surface: it resolves
  the configured DIP width against the active monitor work area, while the candidate list
  inherits its exact pixel width and left edge. It stores copied display data, applies only the
  candidate snapshot matching the current editor revision, and draws lightweight
  Direct2D type glyphs without Shell icon or thumbnail I/O. Ordinary result updates
  retain its layered DIB and Direct2D resources when geometry and device state permit.
- `windows/QuickActionsWindow`: top-aligned Quick Action paging, hotkeys, animation,
  geometry, and rendering.
- `windows/MessageQueueWindow`: a read-only, non-activating bottom-left notification stack.
  It renders up to six compact, independent notification bubbles without taking focus.
- `rendering`: shared animation and layered-window surface primitives.

The internal Native vocabulary is `QuickActions`. ABI v7 deliberately retains its
historic `Feature*` struct names and layouts; those names are compatibility wire
identifiers, not domain modules. The version gate includes revisioned input-change and
candidate-activation callbacks, atomic candidate snapshots, and a bounded candidate icon
category. It also provides token-based begin, update, and complete operations for
persistent message activities while retaining the submitted input mode, ordinary message
queue, and `HidePopups` exports. A new Managed assembly therefore cannot silently pair
with an older DLL. Candidate synchronization validates all rows first, then uses pooled
metadata and one pinned contiguous UTF-16 region for the synchronous ABI call; Native
copies the bounded strings before the region is returned to the pool.

### `src/LuvLetter.IndexKernel`

- A C++20 static library containing the compact immutable filesystem-name index.
- Directory components plus searchable file and folder entities are stored in continuous
  tables and a shared UTF-16 string pool instead of one full path allocation per entry.
- Construction writes those final compact records and the shared string pool directly;
  it does not retain a second per-entity temporary name graph.
- Prefix queries use the sorted filename records and reconstruct full paths only for the
  bounded result set.
- The v4 persisted snapshot validates its magic, schema, roots fingerprint, immutable
  base identity, applied Delta sequence, payload checksum, sizes, references, and
  ordering before it is accepted. Record sections are decoded directly into final vectors
  and saved through a fixed-size streaming buffer.

### `src/LuvLetter.Indexer`

- A hidden, ordinary-user companion process owned by the main application.
- It connects to a per-run random Named Pipe, exits when the pipe closes or its parent
  process ends, and serves one captured read view across independent filesystem
  partitions. Each partition owns an immutable baseline, bounded live Delta, policy,
  snapshot/journal pair, active-generation manifest, generation, and refresh state.
- Filesystem ownership uses the most specific configured root. Every ancestor scan
  excludes all delegated child roots, so logical nested scopes have one physical owner.
  Desktop and Downloads are startup-critical; the user-profile remainder and other
  configured roots use normal maintenance by default. One shared worker schedules builds
  by tier, overdue/dirty/starvation age, and measured prior cost.
- `ReadDirectoryChangesW` watchers coalesce file and folder name changes for 250 ms.
  Each batch is normalized once, grouped by longest-root owner, and applied per partition.
  Resolved, root-owned upserts and tombstones are flushed to an integrity-checked
  per-partition recovery journal before they enter the live Delta; directory-prefix tombstones hide
  stale descendants. Complete batches replay only over their exact base, roots, policy,
  and applied sequence. Partial tails, corruption, watcher uncertainty, and unavailable
  roots retain the last complete generation and request safe reconciliation.
- Full scans omit the index data directory and any external diagnostic-log files
  from both enumeration and change notifications. This prevents snapshot, journal, and
  log writes from feeding back into the indexer's own Delta.
- Initial scans report directory progress while constructing the packed snapshot in one
  pass. The scan percentage is explicitly estimated because recursive enumeration does
  not know the final directory count before discovery; packing and persistence use fixed
  stage percentages.
  Overflow without a trustworthy source requests safe recovery for all file partitions.
  Requests coalesce, never overlap a partition's active build, and respect its automatic
  gap. A Delta exceeding its retained capacity falls back to that partition's last complete
  baseline until reconciliation succeeds.
- It is deliberately not a Windows Service and does not require administrator access.

## Host lifecycle

The Generic Host is started synchronously on the WPF STA thread before
`WpfHostLifetime.Run()` enters the WPF dispatcher loop. This ensures the tray, settings
factory, native adapter, and global hook are first created on the UI thread.
`WpfHostLifetime` bridges shutdown in both directions: WPF exit requests Host shutdown,
while Host shutdown requests WPF dispatcher shutdown. If Host startup fails before the
dispatcher starts, shutdown completes without waiting for an `Application.Exit` event
that cannot occur. The Host owns singleton disposal and `IHostedService` start/stop.

Hosted services have a fixed dependency order:

1. `FileIndexCompanionClient` starts its supervisor in the background; a missing or
   incompatible companion degrades to no file candidates instead of blocking startup.
   Rebuilding state is polled every 250 ms, stable state every five seconds, and a forced
   refresh wakes the stable wait immediately.
2. `WindowsApplicationCatalog` begins independent cache loading and source discovery.
3. `InputCandidateCoordinator` subscribes the revisioned Native input stream and both
   index publication streams.
4. `ApplicationCoordinator` applies Native configuration, loads built-in and external
   plugins, synchronizes Quick Actions, subscribes runtime events, and starts activation
   gestures. The lazily created settings window remains closed. Minimizing Settings keeps
   it available from the tray; explicitly closing it detaches handlers and releases the
   visual tree so the next tray activation creates a fresh window.

Plugin and initial-load diagnostics are recoverable warnings. A gesture-hook failure
opens Settings as a degraded mode. Fatal partial startup executes compensating cleanup.
Shutdown can be requested by WPF, the tray, or the Host. It stops the hook, unsubscribes
events, hides the interactive Native windows, and releases the plugin session; every
step is idempotent and container-owned singletons are disposed by the Host afterward.

## Configuration compatibility

Configuration is stored in `%AppData%\LuvLetter\settings.json`. Schema 9 writes the
canonical groups `InputBox`, `ActivationGestures`, and `QuickActions`. Older settings
using `FeatureWindow` at the root or inside `ActivationGestures` are migrated before
deserialization. A document containing both legacy and canonical names is rejected as
ambiguous instead of silently choosing one value.

InputBox, its candidate dataset, QuickActions, and the message queue share the same
default surface tokens: an opaque cool-white background (`#FFF0F3F9`), white border
(`#FFFFFFFF`), dark-gray content
(`#FF3F3F3F`), 8-pixel corner radius, and 1-pixel border. Core owns the canonical
configuration defaults, while Native mirrors the same values for ABI fallback and
defensive sanitization. Schema migration upgrades fields that still match the previous
default theme and preserves customized values. Schema 10 replaces the previous opaque
silver background while preserving custom themes. Schema 9 also recognizes the historical
640-by-44 dark InputBox preset as one atomic theme, replacing its black surface and
foreground together so it cannot become a low-contrast hybrid after migration.
Schema 11 standardizes typography on Microsoft YaHei UI at 14 DIPs. The legacy per-window font-size fields
remain in serialized/native layouts for compatibility, but normalization and Native
sanitization enforce the shared value. Input adornments derive their geometry from that
shared type scale, and Direct2D applies the active monitor DPI to the complete surface.
Candidate rows use the same size for both lines, distinguish file names with bold weight,
and reserve a wider, taller layout for full-size paths. Text overflow uses ellipses; when
neither side of InputBox can fit every row, the viewport keeps the selected row visible
without scaling the text. Device DPI converts this logical layout to pixels once. Display,
work-area, and DPI changes reflow the input surface as a unit so its internal proportions
remain stable across monitors.

The command input shortcut is fixed to double Ctrl, Quick Actions is fixed to Alt+F1,
the message queue is fixed to Alt+Backspace, and Escape dismisses the two interactive
popups through one serialized Native request. Command input is bottom-centered and
enters upward, Quick Actions is top-centered and enters downward, and the read-only
message queue is positioned at the bottom-left of the selected monitor's work area.

InputBox and Quick Actions show/toggle requests complete synchronously on the Native UI
thread so the next keystroke cannot overtake window activation. The Host verifies that
the requested popup is both the foreground window and the keyboard-focus owner. If the
normal foreground request is rejected, it briefly joins the previous foreground
thread's input queue, retries activation, and always detaches afterward. Recognized
Alt+F1 and Alt+Backspace keystrokes are consumed by the global hook so the previously
focused application does not handle the same command concurrently. If Windows still
denies activation, Native returns an explicit failure instead of silently presenting a
popup that cannot receive keyboard input.

Command-input toggling has three states based on actual keyboard ownership rather than
visibility alone. A hidden input is shown and activated; a visible but unfocused input
is reactivated without clearing its text; a visible and focused input is hidden. After
each activation attempt the Host explicitly refreshes the caret, IME position, and
focus-indicator target from the final foreground/focus state. Escape always follows the
ordinary hide path.

`InputWindow` remains a custom DirectWrite control, but implements standard single-line
editor semantics: Shift navigation and mouse dragging maintain a visible selection;
Ctrl navigation moves by word; Ctrl+A/C/X/V use the normal selection and clipboard
rules; Backspace, Delete, text input, paste, and IME results replace or remove the
selection first. Up and Down navigate candidates when a candidate snapshot is present,
and otherwise retain command-history navigation.
The caret timer starts only while InputWindow is both foreground and focused; focus or
application deactivation stops the timer and removes the caret immediately. Window
visibility alone is never treated as proof of keyboard ownership.

Keyboard focus also drives a reversible green leading indicator. The indicator slides
in from outside the input surface instead of toggling visibility in place. Its animated
reservation is part of the DirectWrite content geometry, so text, selection, caret,
mouse hit testing, responsive wrapping, and the IME composition position move together.
The indicator shares the input window's frame timer but keeps an independent animation
state, allowing focus and popup transitions to reverse without discontinuities.

The input surface also owns a fixed-width mode tag immediately after the focus
indicator. A leading Space on an empty editor cycles `Gen`, `Ask`, and `Cmd` without
inserting text; key repeat cannot cycle more than once per physical press. The tag and
animated indicator share one leading reservation used by DirectWrite layout, caret and
selection painting, mouse hit testing, responsive wrapping, and IME placement.
Mode changes keep that reservation fixed while the outgoing label slides upward and the
incoming label slides in from below, clipped to the tag bounds. The tag owns an
independent animation state on the input window's shared frame timer. Its border color
follows the active mode (green for `Gen`, orange for `Ask`, and purple for `Cmd`) and is
interpolated during the label transition; the label continues to use the configured
input text color. Rapid mode changes are queued in input order instead of restarting the
visible transition. Popup dismissal finishes only the current Tag transition and drops
queued presentation work so hiding remains bounded.

Submitted text crosses the Native boundary as an `InputSubmission` containing both the
text and its mode. `Ask` always produces an Echo response. `Cmd` always uses strict
command dispatch, including the existing unknown-command diagnostic. `Gen` recognizes
registered commands first, then invokes `IGeneralInputMatcher` extensions, and finally
produces Echo when no matcher accepts the text.

Real-time candidate production is separate from final submission. Each actual text or
mode change increments an editor revision, immediately clears the old Native snapshot,
and enters a capacity-one latest-wins pipeline. `Gen` merges application and file matches,
fills remaining direct-result capacity with command prefixes, then appends the reserved Global
Search row. `Ask` publishes no candidates. `Cmd` queries commands only. The default
configuration allows five direct results and one Global Search row, while the limit is
owned by `InputCandidateOptions` rather than Native rendering code.

Each source retrieves up to 64 matches before `ICandidateRankingPolicy` orders the merged
pool. The default combines a 1,000-point application bias, name-match score, and an
optional `ICandidatePriorityProvider` boost. Standalone indexed `.exe` files receive the
same bias. An ordinary file can outrank an application through additional priority;
usage collection and a persisted history are not implemented yet. Application caching
does not affect rank. Future history-based retrieval must also account for results
outside the bounded retrieval pool.

`IApplicationCatalog` publishes application names, executable aliases, and explicit
launch descriptors. Application matching adds a secondary whitespace-compacted key, so
`Microsoft todo` can match `Microsoft To Do`; the filename kernel remains unchanged.
Available applications can publish before a new revision's file query completes.
Catalog changes requery unchanged input without discarding surviving selection tokens.

A non-empty new editor revision selects and visibly highlights its first candidate. A
same-revision index refresh reuses stable candidate tokens and preserves the selected
token when it still exists; if that token disappears, selection falls back to the first
candidate. Up or Down moves the selection. Enter opens the selected application, file, or folder;
Shift+Enter reveals it in Explorer. A successful filesystem or command activation closes
InputWindow, while Enter when no candidates exist follows ordinary submission and does
not close it. Global Search currently reports its reserved status through the message
queue and keeps the input open.

Catalog application activation is asynchronous and single-flight. A successful late
completion cannot hide input from a newer editor revision. Classic applications retain
their shortcut or executable launch semantics; packaged entries activate by AUMID and
report ordinary file reveal as unavailable. Shell acceptance uses `ShellExecuteExW`
rather than requiring a new process handle. Native receives only bounded display text,
an executable glyph, and the managed activation token; no ABI or file protocol change
is required. Source coverage and launch limitations are in `roadmap.applications.md`.

The built-in settings plugin is always Quick Action slot 1 and is displayed as
`Control Center`. Quick Actions exposes the numeric slots 1 through 9. Selecting an
unassigned slot closes the Quick Actions window and reports a diagnostic through the
message queue. All coordinator status reports are mirrored to that queue and retain
their existing WPF/tray status fallback.

The message queue starts empty and hidden. Enqueuing a non-empty ordinary message shows
it without activating it. Each ordinary bubble has its own five-second lifetime; expiry
continues while the window is manually hidden, and the window hides automatically when
the last bubble expires. Alt+Backspace hides a visible queue or shows the remaining
bubbles; it is a no-op when none remain. A subsequent message shows the queue again.
Escape deliberately does not hide this read-only status surface. The active stack is
bounded at six bubbles. Overflow evicts transient bubbles before active persistent
activities, and long text is kept to one line and trimmed with an ellipsis.

Each bubble owns an independent monotonic timeline: it enters from the left over 180 ms,
starts its reverse leftward exit five seconds after ordinary enqueue, and is removed
after the 140 ms exit completes. A message activity instead has a stable token, remains
until completion, can update its text in place, and displays an eight-dot rotating
spinner. Completing it without text begins its exit; completing it with final text turns
the same bubble into an ordinary five-second notification. One adaptive timer renders
16 ms frames only while the queue is visible and animation is required; otherwise it
sleeps until the next lifecycle boundary. Manually hiding the surface therefore pauses
rendering rather than activity lifetime, and a hidden persistent-only queue does not run
a background animation timer.

Snapshot writes stream encoded records and the checksum through a fixed-size buffer.
Each partition's snapshot and recovery-journal generations use one immutable base identity.
Compaction writes and flushes a new snapshot and its retained journal tail before atomically
replacing the small per-partition active-generation manifest. The old pair remains authoritative
until that final switch, so interruption cannot join a snapshot to the wrong log. Query publication follows
the same switch; persistence failure keeps the previous complete generation serving.

## File-index lifecycle and protocol

The default scope is divided into startup-critical Desktop/Downloads, the user-profile
remainder, and other configured or redirected Known Folder roots. Every parent descriptor
delegates its nested roots. An explicitly configured reparse root is retained as an
independent owner even when its path is textually below another root. Scope-compatible
snapshot/journal pairs load independently through
`%LocalAppData%\LuvLetter\Index\v1\partitions\partition-<id-hash>\file-index-v4.active`.
The manifest selects identity-named `file-index-v4-<identity>.bin` and `.delta` files.
Compatible legacy `partition-<id-hash>.bin`/`.bak` caches are considered during migration.
The `v1` directory is the cache namespace and is independent of snapshot schema v4.
Loading and validation run on the file worker outside the pipe handshake and request
loop. Publishing a compatible cache increments aggregate status so unchanged input
refreshes before rescans finish. Queries may briefly omit a partition while its cache is
loading; they do not wait for a full directory scan. Cache-directory write events are
ignored by live maintenance to avoid feeding persistence back into it.

Startup-critical file partitions have a six-minute maximum reconciliation age; normal
partitions use 30 minutes. Periodic deadlines start at scope configuration and remain
independent of event-triggered and manual scans. Busy deadlines coalesce into one pending
request; the per-partition one-minute automatic gap still applies. A shared publication
lease gives each query one stable cross-partition read view while baseline/Delta swaps are
brief exclusive operations. Count and memory thresholds compact each journal into a new
immutable base without scanning. Each partition queues compaction at 8 MiB of retained
operation storage or 4096 batches. At a 16 MiB retained-operation budget, maintenance
keeps the complete existing overlay, stops accepting further incremental batches, and
requires reconciliation before resuming. This prevents a persistence failure from growing
the recovery backlog indefinitely; query results can remain stale until reconciliation
succeeds. MFT/USN integration, a persisted SnapshotSet manifest for live ownership
migration, fuzzy matching, pinyin matching, and privileged services remain outside this
baseline.

The `LLIX` v7 protocol uses a fixed 20-byte little-endian header, UTF-8 length-prefixed
strings, request IDs, editor revisions, and a 1 MiB payload ceiling. Managed owns the
single pipe server and starts `LuvLetter.Indexer.exe` with the pipe name, parent process
ID, and data directory. Scope configuration carries stable partition IDs, roots,
delegated subtrees, tiers, maximum ages, automatic gaps, and global ignore policies. The
pipe is restricted to the current user. Protocol, timeout,
or process failures invalidate the whole session and trigger bounded background restart;
the command and Echo paths remain available. A compact status request reports generation,
an explicit `Ready`, `InitialBuild`, `Updating`, or `Failed` activity, work stage, optional
percentage, whether that percentage is estimated, and the number of entries discovered
so far. Core maps initial construction to `正在生成索引表`
and maintenance to `正在更新索引`, with an in-place ten-segment
progress bar. Initial scanning is labelled with `约` rather than presenting a discovered
work queue as an exact total; packing, compaction, and persistence are exact stages. A
successfully published generation becomes the five-second `索引已就绪` completion. Failed
scans report a delayed retry without announcing success; existing partition snapshots
remain queryable. Session loss maps locally to `Unavailable`,
dismisses the spinner without a false completion, and rejects stale session state. Session
readiness and completed generations requeue the latest unchanged editor revision, so a
user does not need to type another character after the initial background build, a live
Delta publication, or a companion restart. Cross-partition and Delta merges retain only
the requested Top-K heap while preserving full-path collision checks and deterministic
final ordering.

### Maintenance policy

`%LocalAppData%\LuvLetter\Index\maintenance.json` is created on first startup and read
once per application launch. Invalid or unreadable configuration pauses indexing with
a console diagnostic until the file is corrected and the app restarted. Commands and
Echo remain available; an existing file is never overwritten. This prevents a malformed
full-ignore configuration from silently restoring unrestricted search. The default values
are a 360-second startup-critical maximum age, a 1,800-second normal-partition maximum
age, a 60-second automatic gap, a 60-second trigger cooldown, and ignored rebuild scopes
covering temporary data, developer-generated directories, and package caches.
For example, an editable configuration can use environment variables:

```json
{
  "RefreshIntervalSeconds": 360,
  "NormalPartitionRefreshIntervalSeconds": 1800,
  "AutomaticRebuildGapSeconds": 60,
  "TriggerCooldownSeconds": 60,
  "IgnoreRebuildDirectories": [
    "%TEMP%",
    "%LOCALAPPDATA%\\LuvLetter\\Index"
  ],
  "FullIgnorePaths": [
    "%USERPROFILE%\\Private\\example.txt",
    "%USERPROFILE%\\Private\\ExcludedFolder"
  ]
}
```

Ignore entries must be absolute directory paths after environment expansion. They match
case-insensitively at directory boundaries, so ignoring `C:\work\cache` does not ignore
`C:\work\cache-other`. These scopes suppress change-triggered full rebuilds only:
ordinary incremental changes, scheduled scans, and search remain enabled. Moving a
populated directory into scope can leave its descendants waiting for the next full scan.

Three independently editable lists control rebuild triggers:

| Setting | Default scope |
| --- | --- |
| `IgnoreRebuildDirectories` | The user's temporary directory and LuvLetter's index data directory. Add specific noisy workspace paths here. |
| `IgnoreRebuildDirectoryNames` | Complete directory components at any depth, covering the developer directories listed below. |
| `IgnoreRebuildCacheDirectories` | Absolute package caches and editor state paths; includes NuGet, Maven, Cargo, npm, Python, Go, Gradle, sbt, VS Code, and Cursor. |

The complete directory-name defaults are maintained in `FileIndexIgnoreDefaults.cs`:

| Category | Exact directory names |
| --- | --- |
| Version control and editors | `.git`, `.hg`, `.svn`, `.vs`, `.idea`, `.vscode`, `.vscode-insiders`, `.vscode-server`, `.vscode-server-insiders` |
| Coding agents | `.cursor`, `.cursor-server`, `.codex`, `.claude`, `.copilot`, `.agents` |
| Dependencies and package managers | `node_modules`, `.npm`, `.pnpm`, `.pnpm-store`, `.yarn`, `.bun`, `.nuget`, `nuget`, `packages`, `.paket`, `vendor`, `.bundle`, `.gradle`, `.pub-cache`, `.pub` |
| Python environments | `.venv`, `venv`, `.ven`, `__pypackages__`, `.conda`, `.pixi` |
| Build and test output | `bin`, `obj`, `build`, `out`, `dist`, `target`, `artifacts`, `.build`, `_build`, `coverage`, `TestResults` |
| Native, JVM, and mobile build data | `CMakeFiles`, `cmake-build-debug`, `cmake-build-release`, `.cxx`, `.kotlin`, `.bloop`, `.bsp`, `.metals`, `.dart_tool`, `Pods`, `DerivedData`, `Carthage`, `.swiftpm` |
| Python and shared caches | `.cache`, `__pycache__`, `.pycache`, `pycache`, `.pytest_cache`, `.mypy_cache`, `.ruff_cache`, `.tox`, `.nox`, `.hypothesis`, `.ipynb_checkpoints`, `.pdm-build` |
| Web and test caches | `.next`, `.nuxt`, `.output`, `.svelte-kit`, `.angular`, `.turbo`, `.parcel-cache`, `.sass-cache`, `.nyc_output`, `.vite`, `.vitest`, `.astro`, `.docusaurus`, `.vercel`, `.netlify`, `.serverless` |

Absolute cache/state defaults cover the following subtrees:

| Base directory | Relative paths |
| --- | --- |
| `%USERPROFILE%` | `.nuget\packages`, `.m2\repository`, `.cargo\registry`, `.cargo\git`, `go\pkg\mod`, `.gradle`, `.sbt\boot`, `.ivy2\cache` |
| `%LOCALAPPDATA%` | `npm-cache`, `pip\Cache`, `Yarn\Cache`, `pnpm\store`, `uv\cache`, `NuGet\v3-cache`, `NuGet\plugins-cache`, `NuGet\Cache`, `pypoetry\Cache`, `go-build` |
| `%APPDATA%` | `Code`, `Code - Insiders`, `Cursor` |

The editor data roots cover logs, local history, workspace databases, and extension
storage, including Copilot. Their ordinary folder names such as `logs`, `History`, or
`workspaceStorage` are not global name rules. Custom data homes, including overridden
`CODEX_HOME`, `CLAUDE_CONFIG_DIR`, or `COPILOT_HOME`, can be added as absolute scopes;
the indexer does not read another tool's configuration to discover them.

Directory choices follow [NuGet's documented cache locations](https://learn.microsoft.com/en-us/nuget/consume-packages/managing-the-global-packages-and-cache-folders),
[Gradle's directory layout](https://docs.gradle.org/current/userguide/directory_layout.html),
[Python's bytecode cache convention](https://peps.python.org/pep-3147/),
[Cursor rules](https://docs.cursor.com/context/rules),
[Claude's configuration and state directories](https://code.claude.com/docs/en/claude-directory),
and [Copilot CLI's configuration directory](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-config-dir-reference).
Codex's `.codex` scope was also checked against the local session/cache/database layout.

Names match case-insensitively as whole path components: `.git` does not match `.github`
or `.gitignore`, and `bin` does not match `binoculars` or a `.bin` extension. General
source/workspace roots such as `src`, `source`, `repos`, `projects`, and `lib` are not
defaults. Neither are entire `.github` trees, model/dataset folders, or all hidden
directories. Names like `packages` and `vendor` can also contain authored source in a
monorepo: they suppress complete rebuild requests but retain live updates, periodic
scanning, and search. All three lists retain that same search coverage.
No wildcard `*pycache*` rule is introduced; the table lists exact supported variants.
Individual configuration files such as `AGENTS.md`, `CLAUDE.md`, `.cursorrules`, and
`copilot-instructions.md` are not matched by a file-extension or filename ignore rule.

Older JSON files that omit a setting receive its defaults in memory without
rewriting the file. A supplied name or cache list that still exactly matches the previous
shipped default set is upgraded in memory, ignoring order and case. Lists with custom
additions or removals remain unchanged; an explicit `[]` disables that list. Other settings,
including full ignores and intervals, are preserved. A gray configuration message reports
the upgrade. Customized lists can copy selected new entries from the tables above.
The example above omits the directory-name and
package-cache fields and therefore uses their defaults. Restart the app after editing. The two
absolute-path lists together allow at most 1024 entries; the name list allows 128 single
components of at most 255 characters, with no wildcards, separators, or trailing dots
or spaces.

`FullIgnorePaths` is a separate list, empty by default, of at most 1024 absolute file or
directory paths after environment expansion. Entries are exact paths, not wildcard
patterns. A directory entry also excludes its descendants; matching is case-insensitive
and respects path boundaries. Full ignore takes precedence over all rebuild-trigger
settings and remains effective during startup, periodic, and forced scans.

| Policy | Change-triggered rebuild | Incremental and full-scan coverage | Search results |
| --- | --- | --- | --- |
| Rebuild ignore | Suppressed for known paths | Retained | Retained |
| Full ignore | Suppressed for known paths | Excluded | Excluded |

Full-ignore entries are filtered before directory traversal and before watcher events
enter the Delta. Fully excluded roots do not open directory watches. Root provenance
also fingerprints the normalized exclusions, so an older cache containing newly excluded
paths cannot be published, including through backup fallback. Changing exclusions can
therefore require an initial scan before results appear. Snapshot schema v4 and recovery
journal bindings validate the current scope and enumeration policy together.
These are path-scope rules, not file-identity rules across junction aliases, and they do
not erase already-existing cache files from disk.

For known-path events, full ignore is checked before live updates. Ordinary changes
then update the Delta; only changes requiring full reconciliation reach the rebuild
policy. That policy checks absolute ignore scopes and directory-name ignores before
any cooldown lookup, expiry cleanup, insertion, or capacity check. A match returns
`Ignored` immediately: it cannot consume a cooldown slot, extend a deadline, or produce
a red per-path cooldown refusal, even when the cooldown map is already full.

The in-memory cooldown map records paths whose reconciliation requests were accepted.
Repeated events for the same normalized path cannot request another rebuild until its
deadline, and suppressed events do not extend that deadline. The map expires entries
and is capped at 4096; when full it suppresses new keys until room is available. Watcher
overflow has no trustworthy source path and uses one cooldown entry per partition, so it
cannot be attributed to an ignored directory. Such uncertainty currently queues recovery
across all file partitions.
Ordinary ignore retains Delta processing and therefore cannot prevent all watcher or
pending-buffer overflow. Recovery is logged separately from known-path file triggers.
The watcher observes file/directory name changes; an in-place content-only write does
not itself request a full filename-index scan.

Accepted requests are coalesced, with at most one file scan running and a one-minute
per-partition gap between automatic scans. Thus six or 30 minutes is a maximum
reconciliation age, not a guarantee that event-driven maintenance waits that long. A new
temporary filename can bypass a different filename's cooldown; noisy directory scopes
belong in the ignore list.

The built-in `index.refresh` command in `Gen` or `Cmd` requests both filesystem and
application catalogs. Filesystem refresh fans out across enabled partitions regardless of
rebuild-ignore, cooldown, and automatic-gap rules. Full-ignore exclusions still apply.
If a partition scan is already running, one follow-up request is coalesced rather than
cancelling or overlapping it. LLIX v7
carries maintenance settings with root configuration and acknowledges `Refresh` with a
status response. Disconnected manual requests wait for the next companion connection.

### Application catalog maintenance

`WindowsApplicationCatalog` loads compatible source caches before background discovery.
Settings are at `%LocalAppData%\LuvLetter\Applications\settings.json` with an initially
empty `PortableRoots` array. Each source uses a hashed manifest/snapshot pair and matching
backups under `Applications\v2\partitions`.
Settings are read once per launch; invalid application settings pause only this catalog.
Invalid shared maintenance settings pause both file and application indexing.

Source discovery covers trusted user/common Start Menu shortcuts, App Paths in both
registry views, packaged and non-package AppsFolder entries, a curated Windows system
catalog, and bounded portable directories. Queries read an immutable merged view with
publication-time compact display and alias keys. Each query prepares its normalized text
once, scans without per-entry objects, and sorts only a bounded Top-K heap. Cache
validation checks format, size, checksum, source/entry shape, scope, and full-ignore
provenance. Failed sources retain their own previous entries while each successful source
publishes and persists immediately. Successful empty sources remove stale entries. Atomic
writes preserve that source's previous valid backup, and save failure retains in-memory
results. Cache trust retains only source, scope, and generation metadata rather than the
serialized JSON bytes. A source refresh whose sorted entries are unchanged updates
freshness without increasing generation, rewriting cache, or publishing `Changed`.

Every source shares the six-minute maximum age and 60-second cooldown defaults. Start Menu
watchers coalesce changes into their owning source; other sources refresh periodically or
on `index.refresh`. Failures retry locally after 1, 2, 4, then at most 6 minutes.
Known full/ordinary ignores run before cooldown; full ignores also filter cached entries
and activation targets. Watcher errors request recovery and recreate the watcher with
backoff. Two fixed discovery STA workers and one activation STA worker bound thread count;
adding portable roots does not add permanent threads. Shutdown cancels pending work and
bounds the wait for in-flight discovery to two seconds.

### Index console events

The debug launcher colors stable `[Index][event]` tags, independently of whether a line
arrives on stdout or stderr. Native log lines are synchronized, redirected text uses
UTF-8, and persisted logs contain plain text. Untagged diagnostics also default to gray.

| Event | Message | Console color |
| --- | --- | --- |
| `file-change` | `File changed triggered` | Gray |
| `periodic` | `Automatic index rebuild` | Green |
| `cooldown-refused` | `File changed but cooldown refused` | Red |
| `force` | `Force rebuild queued` | Green |
| Other events | Startup, ignored changes, watcher recovery, cache operations, rebuild lifecycle, configuration errors | Gray |

The four primary messages describe causes and admission decisions, not the complete
lifecycle. Queued and coalesced requests are distinguished; file-trigger logs include a
sample path, event count, active-scan flag, and minimum wait. Cooldown refusals include
remaining seconds without extending the deadline. Capacity refusal is reported separately
from cooldown. Force requests distinguish waiting for the companion from acceptance.
Start, completion, cancellation, and failure/retry messages carry the actual scan state;
a scan can combine several causes. Ordinary changes handled entirely by the Delta do
not claim to rebuild the index. Ignored paths are not logged individually on every write.
Watcher overflow cannot identify its source path and is reported as recovery, not as a
known file change; it can still request reconciliation even when exclusions are present.

Diagnostic logging is off by default and does not create or touch a log file. Set
`LUVLETTER_INDEXER_LOG=debug` before starting the application to write bounded UTF-8 JSONL
diagnostics to `%LocalAppData%\LuvLetter\Index\v1\logs\indexer.log`; an absolute value may
select another file. The active file rotates at 4 MiB to one `.previous` archive. Events
cover process and pipe lifetime, scan progress and filesystem errors, 30-second no-progress
detection, journal recovery, compaction, snapshot persistence, activation, cancellation,
and publication. Logging failure never makes indexing unavailable.

## Build and tests

LuvLetter is a Windows x64 application and requires both managed and native build tools:

- the .NET 10 SDK;
- Visual Studio Build Tools 2026 with the **Desktop development with C++** workload;
- the MSVC `v145` x64/x86 tools and a Windows 10 or Windows 11 SDK;
- the Build Tools **.NET SDK** component, which supplies the .NET SDK resolver to full
  MSBuild.

The Visual Studio IDE is not required. The standalone Build Tools installation provides
the full-framework `MSBuild.exe` and C++ targets that can be invoked from a VS Code
terminal.

### One-click startup

Double-click the root-level `start.bat` to restore and build the application in Debug mode,
including its native renderer and indexer, then launch it. The launcher locates full
Visual Studio MSBuild through `vswhere`; both Visual Studio 2026 and standalone Build
Tools installations are supported. The prerequisites above must already be installed,
and the first restore needs access to the configured NuGet feeds.

From a PowerShell terminal at the repository root:

```powershell
.\scripts\start.ps1
.\scripts\start.ps1 -Configuration Release
```

The BAT entry point also forwards arguments, for example
`.\start.bat -Configuration Release`. It uses Windows PowerShell with a
process-local execution-policy bypass and pauses after the debug session so diagnostics remain
visible. Both entry points resolve paths from the script directory and can be called
from another working directory, including when the repository path contains spaces.

Exit any running LuvLetter instance from the system tray before launching again. The
script refuses to rebuild while a process named `LuvLetter` is running, stops if the
build fails or a required native output is missing, and starts the executable with its
output directory as the working directory. The console stays open, displays the launched
PID, and streams standard output and error to both the console and per-run files under
`%LocalAppData%\LuvLetter\Logs`. Companion diagnostics include cache readiness, accepted
rebuild causes, scan duration, and persistence failures. OutputDebugString/Trace output
is not captured. Press Ctrl twice to show the input box, or use the system tray.

Press `Q`, Enter, or Ctrl+C in the debug console to stop only the process launched by
that session. The script first tries a window close and then forces termination if
necessary; exit from the system tray for normal application shutdown. The companion
observes its parent exit. Closing the console window forcibly can bypass script cleanup,
so use the stop keys or tray exit. The BAT launcher pauses after exit to retain logs;
direct PowerShell invocation returns to the existing terminal prompt.

### Manual build

Do not use `dotnet build LuvLetter.slnx` for a complete build. The solution contains
C++ `.vcxproj` projects, while the .NET SDK version of MSBuild does not include
`Microsoft.Cpp.Default.props` or the MSVC toolchain. It consequently reports `MSB4278`.

From a PowerShell terminal at the repository root, locate full MSBuild without assuming
that Visual Studio or Build Tools was added to `PATH`:

```powershell
$vswhereCandidates = @(
    "$env:ProgramFiles\Microsoft Visual Studio\Installer\vswhere.exe"
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
)
$vswhere = $vswhereCandidates |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1
if (-not $vswhere) {
    throw "Visual Studio Build Tools was not found."
}

$vsPath = & $vswhere -latest `
    -products Microsoft.VisualStudio.Product.BuildTools `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
              Microsoft.VisualStudio.Component.Windows11SDK.26100 `
              Microsoft.NetCore.Component.SDK `
    -property installationPath
if (-not $vsPath) {
    throw "The required C++, Windows SDK, and .NET SDK Build Tools were not found."
}

$msbuild = Join-Path $vsPath "MSBuild\Current\Bin\amd64\MSBuild.exe"
```

Build the complete Debug application, including `LuvLetter.Native.dll`, with:

```powershell
& $msbuild LuvLetter.slnx /restore /m /p:Configuration=Debug
```

The full build places `LuvLetter.Native.dll` and `LuvLetter.Indexer.exe` beside the
application. Run it directly from that shared output directory; the application starts
and supervises the indexer automatically:

```powershell
& .\src\LuvLetter\bin\Debug\net10.0-windows\LuvLetter.exe
```

Alternatively, after the complete build has succeeded, let the .NET CLI launch the
already-built executable without invoking its incompatible solution build path:

```powershell
dotnet run --project src/LuvLetter/LuvLetter.csproj -c Debug --no-build
```

Do not use a bare `dotnet build; dotnet run` at the repository root. The first command
tries to build the C++ projects with .NET SDK MSBuild and fails; PowerShell's semicolon
then starts the second command even though the build failed.

For a Release build, replace `Debug` with `Release` in both commands. After at least one
complete build, managed-only iterations may use the following command, which deliberately
reuses the existing native DLL for the same configuration:

```powershell
dotnet build src/LuvLetter/LuvLetter.csproj -c Debug -p:SkipNativeBuild=true
```

The same managed-only build can be combined with launching the application:

```powershell
dotnet run --project src/LuvLetter/LuvLetter.csproj -c Debug -p:SkipNativeBuild=true
```

`SkipNativeBuild=true` is not a first-build workaround. A managed-only iteration reuses
both native outputs from the preceding complete build. Starting without a current
ABI-compatible `LuvLetter.Native.dll` fails Host startup; starting without the matching
`LuvLetter.Indexer.exe` safely disables file candidates and retries in the background.

Run the Core smoke scenarios with:

```powershell
dotnet run --project tests/LuvLetter.Core.Tests/LuvLetter.Core.Tests.csproj -c Release
```

Run the Native suite with:

```powershell
& $msbuild tests/LuvLetter.Native.Tests/LuvLetter.Native.Tests.vcxproj /m /p:Configuration=Release /p:Platform=x64
& .\tests\LuvLetter.Native.Tests\bin\x64\Release\LuvLetter.Native.Tests.exe
```

Run the C++ index-kernel suite with:

```powershell
& $msbuild tests/LuvLetter.IndexKernel.Tests/LuvLetter.IndexKernel.Tests.vcxproj /m /p:Configuration=Release /p:Platform=x64
& .\tests\LuvLetter.IndexKernel.Tests\bin\x64\Release\LuvLetter.IndexKernel.Tests.exe
```

### Performance measurement

With both processes running, collect a ten-minute per-process and combined working-set,
private-bytes, CPU, thread, and handle series with:

```powershell
.\scripts\measure-performance.ps1
```

The CSV is written below `artifacts\performance` by default. Build the synthetic compact
snapshot benchmark with full MSBuild, then run its Release executable with optional
entity-count, iteration-count, and query arguments:

```powershell
& $msbuild benchmarks/LuvLetter.IndexKernel.Benchmarks/LuvLetter.IndexKernel.Benchmarks.vcxproj /m /p:Configuration=Release /p:Platform=x64
& .\benchmarks\LuvLetter.IndexKernel.Benchmarks\bin\x64\Release\LuvLetter.IndexKernel.Benchmarks.exe 100000 2000 item0000000
```

The benchmark reports construction time, private/working-set deltas, and warm Top-5
query p50/p95/p99. It uses a synthetic in-memory snapshot and does not measure disk scan,
pipe, managed ranking, or UI latency; use the process sampler and the manual scenarios in
`performance-memory-audit.md` for the end-to-end view.
