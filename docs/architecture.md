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
  only the latest editor revision, combines file and command candidates according to the
  active mode, owns activation tokens, and rejects stale results before Native display.
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
  positioned above InputWindow. It stores copied display data, applies only the
  candidate snapshot matching the current editor revision, and draws lightweight
  Direct2D type glyphs without Shell icon or thumbnail I/O.
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
with an older DLL.

### `src/LuvLetter.IndexKernel`

- A C++20 static library containing the compact immutable filesystem-name index.
- Directory components plus searchable file and folder entities are stored in continuous
  tables and a shared UTF-16 string pool instead of one full path allocation per entry.
- Prefix queries use the sorted filename records and reconstruct full paths only for the
  bounded result set.
- The v3 persisted snapshot validates its magic, schema, roots fingerprint, payload
  checksum, sizes, references, and ordering before it is accepted.

### `src/LuvLetter.Indexer`

- A hidden, ordinary-user companion process owned by the main application.
- It connects to a per-run random Named Pipe, exits when the pipe closes or its parent
  process ends, serves queries from the current immutable snapshot plus a bounded live
  Delta, and rebuilds on a Windows background-processing thread.
- `ReadDirectoryChangesW` watchers coalesce file and folder name changes for 250 ms.
  Upserts and tombstones are merged with the base generation; directory-prefix
  tombstones hide stale descendants. Overflow, ambiguous directory rename recovery, and
  Delta thresholds request a safe background rebuild. Requests are coalesced and never
  cancel an active scan; automatic retries and event-driven scans wait at least one
  minute after the preceding scan finishes. If Delta exceeds its retained capacity,
  queries fall back to the last complete snapshot until reconciliation succeeds.
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
2. `InputCandidateCoordinator` subscribes the revisioned Native input stream.
3. `ApplicationCoordinator` applies Native configuration, loads built-in and external
   plugins, synchronizes Quick Actions, subscribes runtime events, and starts activation
   gestures. The lazily created settings window remains closed.

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

InputBox and QuickActions share the same default surface tokens: an opaque silver
background (`#FFC0C0C0`), white border (`#FFFFFFFF`), dark-gray content
(`#FF3F3F3F`), 8-pixel corner radius, and 1-pixel border. Core owns the canonical
configuration defaults, while Native mirrors the same values for ABI fallback and
defensive sanitization. Schema migration upgrades fields that still match the previous
default theme and preserves customized values. Schema 9 also recognizes the historical
640-by-44 dark InputBox preset as one atomic theme, replacing its black surface and
foreground together so it cannot become a low-contrast hybrid after migration.

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
and enters a capacity-one latest-wins pipeline. `Gen` queries files first, fills any
remaining direct-result capacity with command prefixes, then appends the reserved Global
Search row. `Ask` publishes no candidates. `Cmd` queries commands only. The default
configuration allows five direct results and one Global Search row, while the limit is
owned by `InputCandidateOptions` rather than Native rendering code.

A non-empty new editor revision selects and visibly highlights its first candidate. A
same-revision index refresh reuses stable candidate tokens and preserves the selected
token when it still exists; if that token disappears, selection falls back to the first
candidate. Up or Down moves the selection. Enter opens the selected file or folder;
Shift+Enter reveals it in Explorer. A successful filesystem or command activation closes
InputWindow, while Enter when no candidates exist follows ordinary submission and does
not close it. Global Search currently reports its reserved status through the message
queue and keeps the input open.

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

Snapshot writes use a same-directory temporary file, flush it, and atomically replace the
old file. Query publication is independent: a completed in-memory generation can continue
serving when persistence fails. A `.bak` file retains the preceding scope-compatible
snapshot (or the first completed snapshot when no predecessor exists). Startup tries
this backup if the primary is missing, invalid, or incompatible. Both files use the same
validation and atomic-save rules. Failed root access or unexpected enumeration errors
do not publish a replacement snapshot; ordinary inaccessible or disappearing descendants
remain skippable. A failed rebuild keeps the previous query view and retries after
one minute instead of entering a tight loop.

## File-index lifecycle and protocol

The default scope is the current user profile plus redirected Windows Known Folders that
resolve outside it; an explicitly configured reparse root is retained even when its path
is textually below another root. The last scope-compatible snapshot is loaded from
`%LocalAppData%\LuvLetter\Index\v1\file-index-v3.bin`. The `v1` directory is the cache
namespace and is independent of snapshot schema v3. Loading and validating the primary
or its `.bak` fallback runs on the worker, outside the pipe handshake and request loop.
Publishing a compatible cache increments the generation so unchanged input refreshes
before the startup rescan finishes. Queries may briefly have no file results while the
cache itself is loading; they do not wait for a full directory scan. Cache-directory
write events are ignored by live maintenance to avoid feeding persistence back into it.
A periodic reconciliation becomes due every six minutes from scope configuration,
independent of file-triggered and manual scans. Busy deadlines coalesce into one pending
scan; the automatic minimum gap still applies. MFT/USN integration, fuzzy matching, pinyin matching,
and privileged services remain outside this phase.

The `LLIX` v5 protocol uses a fixed 20-byte little-endian header, UTF-8 length-prefixed
strings, request IDs, editor revisions, and a 1 MiB payload ceiling. Managed owns the
single pipe server and starts `LuvLetter.Indexer.exe` with the pipe name, parent process
ID, and data directory. The pipe is restricted to the current user. Protocol, timeout,
or process failures invalidate the whole session and trigger bounded background restart;
the command and Echo paths remain available. A compact status request reports the index
generation and an explicit `Ready`, `InitialBuild`, `Updating`, or `Failed` activity. Core maps an
initial build to the persistent `正在生成索引表` activity, maintenance rebuilds including
the six-minute reconciliation to `正在更新索引`, and a successfully published generation to
the five-second `索引已就绪` completion. Failed scans dismiss the spinner and report a
delayed retry without announcing success; existing snapshots remain queryable. Session
loss maps locally to `Unavailable`,
dismisses the spinner without a false completion, and rejects stale session state. Session
readiness and completed generations requeue the latest unchanged editor revision, so a
user does not need to type another character after the initial background build, a live
Delta publication, or a companion restart.

### Maintenance policy

`%LocalAppData%\LuvLetter\Index\maintenance.json` is created on first startup and read
once per application launch. Invalid or unreadable configuration pauses indexing with
a console diagnostic until the file is corrected and the app restarted. Commands and
Echo remain available; an existing file is never overwritten. This prevents a malformed
full-ignore configuration from silently restoring unrestricted search. The default values
are a 360-second periodic refresh, a 60-second trigger cooldown, and ignored rebuild
scopes covering temporary data, developer-generated directories, and package caches.
For example, an editable configuration can use environment variables:

```json
{
  "RefreshIntervalSeconds": 360,
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
therefore require an initial scan before results appear. Snapshot schema v3 is retained;
an empty full-ignore list preserves compatibility with previous v3 root fingerprints.
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
overflow has no trustworthy source path and uses one shared cooldown entry, so it cannot
be attributed to an ignored directory. Such uncertainty can still queue a recovery scan.
Ordinary ignore retains Delta processing and therefore cannot prevent all watcher or
pending-buffer overflow. Recovery is logged separately from known-path file triggers.
The watcher observes file/directory name changes; an in-place content-only write does
not itself request a full filename-index scan.

Accepted requests are coalesced, with at most one scan running and a one-minute global
gap between automatic scans. Thus six minutes is the periodic interval, not a guarantee
that event-driven maintenance waits six minutes. A new temporary filename can bypass a
different filename's cooldown; noisy directory scopes belong in the ignore list.

The built-in `index.refresh` command in `Gen` or `Cmd` queues a full scan regardless of
rebuild-ignore and cooldown rules, including the global automatic gap. Full-ignore
exclusions still apply. If a scan is already running, one follow-up scan is queued
rather than cancelling or overlapping it. LLIX v5
carries maintenance settings with root configuration and acknowledges `Refresh` with a
status response. Disconnected manual requests wait for the next companion connection.

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
