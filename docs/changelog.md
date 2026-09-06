# Changelog

This document records user-visible additions, behavior changes, and intentionally
reserved product capabilities. Architectural ownership and dependency rules remain in
`architecture.md`.

## Unreleased

### Fixed

- Ordinary runtime logging is now an explicit console-session capability. Visual Studio
  and packaged GUI launches no longer keep indexer stdout/stderr pipes, asynchronous line
  readers, console formatting, or watcher-event message construction active by default;
  the one-click console launcher opts in without changing persistent diagnostic logging.
- Control Center now constructs its shared WPF font family as a typed object before
  loading XAML. Opening Quick Actions and pressing `1` no longer fails while assigning
  the shared typography string to `TextElement.FontFamily`.
- InputBox and Quick Actions now keep independent visibility. Invoking either popup only
  transfers keyboard focus, while Escape continues to dismiss both together.
- The candidate list now inherits the input window's resolved width and left edge. The
  combined input surface keeps one DIP-based layout across monitor DPI and work-area
  changes, while narrow displays constrain both windows together.
- Input text, placeholder text, caret, and IME placement now share metrics derived from
  the active font, keeping their vertical alignment consistent. The mode tag uses regular
  text, a transparent interior, and a finer outline.
- Downloads discovery and partition classification now use the current Windows Known
  Folder location, including redirection to another drive, instead of assuming a
  Downloads directory beneath the user profile. Failed discovery does not invent a
  fallback path. Explicitly configured unavailable roots still retain their previous
  index and retry; ordinary logs now identify unavailable roots by path and Win32 error.
- Candidate action labels now compile with an explicit UTF-8 source encoding and
  code-page-independent Unicode literals. The right-side action group measures its current
  label, so `打开到文件夹` and `复制路径` expand left while preserving a common right edge.
- Successful file and application activation now dismisses the input without restoring its
  previous foreground window. File reveal uses `SHOpenFolderAndSelectItems`, then waits for
  event-driven Shell and UI Automation signals after dismissal. The focus broker matches the
  active folder by PIDL, scopes structure observation to the target Explorer window and file
  view, validates the active tab before each focus write, and focuses the unique selected file
  item rather than the `Name` column header. Event and focus budgets plus a single final timeout
  correction replace periodic retries. Native ABI v12 adds the focus-safe dismissal operation.
  Copying a path leaves the input and candidates visible.

### Added

- Gen candidates now identify their location as `应用`, `文件`, or `文件夹`. The selected
  row alone reserves a right-side Enter hint; holding Shift changes it to `打开到文件夹`,
  while holding Ctrl changes it to `复制路径`. Enter opens the item, Shift+Enter reveals
  files and applications but opens a selected folder directly, and Ctrl+Enter copies the
  complete untruncated path. Native ABI v11 adds the copy-path action capability.
- Command domains and command-path candidates no longer repeat generic descriptions.
  Commands can register optional argument metadata; `/luv index refresh` exposes `-f` and
  `--force` with the `强制全量刷新` description. Argument candidates remain Tab-only so
  Enter always submits the arguments already entered by the user.
- Added explicit command domains and multi-segment command paths. Empty `Cmd` lists
  domains and each completed segment reveals only its immediate children. Built-in
  commands are now `/luv settings` and `/luv index refresh`.
- Added command aliases and prefix links. Aliases share one command definition; links
  rewrite a source prefix before resolving the remaining path. `/luv refreshindex` links
  to `/luv index refresh`.
- Tab now completes the selected command segment without executing it. Enter executes
  eligible candidates or submits the current text when the selected domain or branch is
  not executable.
- Split manual index maintenance into normal and forced modes. `/luv index refresh`
  observes partition gaps, while `-f` and `--force` bypass them across file and application
  partitions. LLIX v8 distinguishes normal `Reconcile` from forced `Refresh` frames.
- Added a bounded serial Windows command runner for Cmd input whose first token does not
  match a registered domain. It invokes hidden `cmd.exe`, captures bounded stdout and
  stderr for the message queue, enforces a two-minute timeout, and preserves standalone
  `cd` working-directory changes.
- Typing or pasting a leading `/` in `Gen` now switches directly to `Cmd`. The visible
  slash is treated as a mode prefix for command candidates and submission, and the edit
  bypasses application and filesystem index queries from its first published revision.
- Candidate rows now use Windows stock icons for ordinary files and folders and the
  resolved Shell icon for classic, shortcut, AppsFolder, and indexed executable
  applications. Resolution runs on a bounded STA worker with DPI-aware caching, stale
  result rejection, and the existing vector glyph as an immediate or failure fallback.
- Added a performance and resident-memory audit, a two-process working-set/private-bytes
  sampler, and a synthetic index-kernel benchmark that reports warm Top-K query
  percentiles and compact-snapshot memory changes.
- Added non-overlapping filesystem partitions for Desktop, Downloads, the user-profile
  remainder, and other configured roots. Each partition owns its baseline, live Delta,
  rebuild policy, cache/backup, generation, and refresh schedule; parent scans exclude
  every delegated child root.
- Added longest-root filesystem event routing, per-partition reconciliation/cooldown,
  one bounded priority worker, and stable cross-partition Top-K queries under a shared
  publication view. Startup-critical partitions default to six-minute reconciliation;
  normal partitions default to 30 minutes.
- Split application discovery into independently cached and published Start Menu,
  App Paths, AppsFolder, curated-system, and portable partitions. A slow or failed source
  retains its last valid data and cannot delay healthy-source publication.
- Added curated Windows Settings, Control Panel, MMC, and common system-tool entries,
  non-package AppsFolder items, localized Shell labels, and trusted Start Menu Shell/PIDL,
  `.msc`, and `.cpl` activation while rejecting arbitrary URI and Shell targets.
- Added fixed bounded STA discovery/activation workers and per-source application retry
  backoff. Portable-root count no longer creates a matching number of permanent threads.
- Added application discovery for Start Menu executable shortcuts, App Paths, packaged
  AppsFolder entries, and configured portable roots. Display-name and executable aliases
  include whitespace-compacted matching for names such as `Microsoft To Do`.
- Added application Enter activation, classic application reveal, and explicit packaged
  reveal/failure feedback. Launches revalidate cached targets, preserve shortcut semantics,
  reject stale activation, and keep newer input open after late completion.
- Added an independent application cache and backup, source-specific failure retention,
  startup reuse, Start Menu monitoring, periodic reconciliation, and full-ignore filtering.
- Added configurable application ranking bias and an injectable priority provider.
  Applications and in-scope `.exe` files precede ordinary files by default; an additional
  file priority can override the bias. Usage-history collection remains future work.
- Added `FullIgnorePaths` for exact file and directory-subtree exclusions from both
  live updates and complete scans. Scope-compatible caches include these exclusions;
  forced refreshes preserve them.
- Added distinct console events for file-triggered, periodic, cooldown-refused, and forced
  rebuild requests, with queue/coalescing and scan lifecycle diagnostics. The debug
  launcher uses gray, green, red, and green respectively; all other output defaults to
  gray, independently of stdout/stderr.
- Added root-level `start.bat` and `scripts/start.ps1` for one-click build and launch on
  Windows, with automatic Visual Studio MSBuild discovery, Debug/Release selection,
  required-output checks, and a persistent debug console with process stop controls and
  stdout/stderr logs under `%LocalAppData%\LuvLetter\Logs`.
- Added editable index-maintenance settings with a six-minute periodic refresh,
  directory scopes that suppress rebuild triggers while retaining search, and a bounded
  one-minute per-path trigger cooldown. Added the index refresh command to force a full scan.
- Added default rebuild-ignore rules for version-control metadata, developer dependencies,
  build output, virtual environments, and package caches. Exact directory-name matching
  works across workspace locations; older configuration files receive defaults for new
  fields while explicit empty lists remain respected.
- Expanded ordinary rebuild-ignore defaults for .NET/NuGet, native and web build output,
  Python/notebook caches, VS Code, and Codex/Cursor/Copilot/Claude directories. These
  rules retain search coverage and do not populate full-ignore exclusions.
- Added the `Gen`, `Ask`, and `Cmd` input modes with a persistent status tag inside the
  input surface.
- Added a clipped vertical transition for mode changes: the outgoing tag label exits
  upward while the incoming label enters from below. The tag border transitions between
  green (`Gen`), orange (`Ask`), and purple (`Cmd`) without changing the label color.
  Rapid changes are presented in order without resetting an in-flight transition.
- Added empty-input Space switching in the order `Gen` -> `Ask` -> `Cmd` -> `Gen`.
  Leading ordinary spaces are not inserted, and holding Space does not repeatedly
  cycle modes.
- Added Echo handling for natural-language input. `Ask` always echoes; `Gen` echoes only
  after registered commands and General matchers decline the input.
- Added an `IGeneralInputMatcher` extension boundary for built-in General-mode matchers.
- Added the first built-in file-index implementation as a hidden C++ companion process.
  It loads a persistent snapshot immediately, rebuilds the current user-profile index in
  the background, performs a low-priority periodic reconciliation, and keeps queries on
  an immutable previous snapshot while rebuilding.
- Added a compact C++ filename index with case-insensitive Unicode prefix matching,
  stable ordering, bounded top-result path reconstruction, validated persistence, and
  atomic snapshot replacement.
- Added a non-activating candidate list above the input box. Its default structure is up
  to five direct matches followed by one Global Search entry, and the counts are exposed
  through candidate options for future configuration.
- Added keyboard candidate selection. Up and Down select or move through results; Enter
  opens a selected file, while Shift+Enter opens its containing location in Explorer.
- Added persistent message activities that update one bubble in place, remain visible
  until completion, and display a rotating progress indicator without changing the
  ordinary transient-message lifetime.
- Added explicit index lifecycle feedback. Initial construction reports
  `正在生成索引表`, background maintenance reports `正在更新索引`, and successful
  publication completes the activity with `索引已就绪`.
- Added hierarchical command candidates from registered domain and command snapshots.
  `Cmd` shows domains first and commands after a valid domain; `Gen` remains application
  and file search with Global Search, while `Ask` shows no candidates.
- Added editor revisions and latest-wins query delivery so slow results from older input
  cannot replace or activate the current candidate list.
- Added index-generation status refresh. The current input is queried again automatically
  when the first background build completes or the companion reconnects, without requiring
  another keystroke.
- Added folder-name indexing and activation. Folder candidates open with Enter and are
  selected in their parent location with Shift+Enter.
- Added lightweight candidate glyphs for folders, images, documents, archives, audio,
  video, executables, commands, searches, and generic files. Rendering performs no Shell
  icon extraction or thumbnail I/O.
- Added coalesced `ReadDirectoryChangesW` maintenance with a bounded in-memory Delta,
  exact tombstones, directory-prefix tombstones, and safe rebuild fallback after watcher
  uncertainty or Delta thresholds.
- Added snapshot v3 provenance and integrity checks. Cached entities carry their type,
  roots are fingerprinted, and a payload checksum rejects silent corruption.
- Added persistent incremental recovery. Snapshot v4 carries an immutable base identity
  and applied Delta sequence; complete, checksummed write-ahead batches replay only over
  the matching base, roots, and enumeration policy.
- Added crash-safe Delta compaction through identity-named snapshot/journal pairs and an
  atomically replaced active-generation manifest per filesystem partition. Healthy
  partitions remain recoverable when another configured root is unavailable.
- Added estimated initial-scan progress with discovered-entry counts and exact packing,
  compaction, and persistence stages. The existing persistent activity now updates an
  in-place percentage alongside its indefinite spinner.
- Added explicit opt-in, bounded JSONL indexer diagnostics through
  `LUVLETTER_INDEXER_LOG`, including throttled scan progress, filesystem errors,
  no-progress detection, recovery, persistence, and publication events.

### Changed

- Native popup surfaces now use a 90%-opaque cool-white background so desktop color can
  show through subtly, with a shared low-opacity rounded shadow rendered behind InputBox,
  candidates, Quick Actions cards, and message bubbles. Schema 12 upgrades the previous
  opaque default while preserving customized themes.
- Message bubbles now size to their measured content up to the existing 440-DIP maximum.
  Long messages wrap and grow vertically, and ordinary transient messages remain for
  three seconds instead of five.
- Index activities retain their percentage and discovered-entry count without rendering
  a text-based segmented progress bar.
- Reduced ready-state companion status polling from four round trips per second to one
  every five seconds while preserving immediate forced refresh through an explicit wake.
- Reworked application lookup to precompute compact display/alias keys and retain only a
  bounded Top-K heap per query. Semantically unchanged source refreshes skip cache and
  publication work, and application/file publications reuse the unaffected query half.
- Removed retained application-cache JSON buffers, packed candidate interop strings into
  pooled contiguous storage, and reused candidate-window rendering resources across
  ordinary result updates. Explicit Settings close now releases the window visual tree.
- Reduced filesystem indexing peaks by building compact records directly, decoding cache
  records into final vectors, and streaming snapshot writes and checksums. Partition
  publication now requires a complete persisted snapshot/journal pair; failed persistence
  retains the previous query view.
- Batched watcher changes by owning partition and bounded Delta and cross-partition result
  merges to the requested Top-K. Full-ignore checks now use an empty-list fast path and
  sorted path-boundary lookup without repeated normalization on hot scanner paths.
- Upgraded LLIX to v7, combining partition IDs, roots, delegated subtrees, maintenance
  tiers, maximum ages, and automatic gaps with work-stage, percentage, estimate, and
  discovered-entry status fields. Persisted snapshots use schema v4; Native ABI remains v7.
- Extended the index refresh command to request both file and application catalogs. Existing ignore,
  cooldown, full-exclusion, and console-color semantics also apply to application events.
- Rank a bounded pool of up to 64 matches per source before taking visible results;
  keep the default five direct results and one Global Search row. Application publication
  refreshes unchanged input and preserves surviving candidate tokens.
- Corrected file and classic application Shell success detection so opening an existing
  process does not require a new process handle. App Paths launches use verified absolute
  targets and child-only private PATH values; private-environment launches that require
  elevation report that the original shortcut is needed.
- Upgrade unchanged legacy default name/cache lists in memory while preserving customized
  lists, explicit empty lists, full ignores, and timing settings. Existing configuration
  files are not rewritten.
- Made the ignore-before-cooldown contract explicit and added regression coverage:
  ignored events consume no cooldown slots and remain ignored even at full capacity.
- Kept six-minute periodic deadlines independent of file changes and manual refreshes.
  Busy ticks merge into one request, with the existing automatic minimum gap preserved.
- Upgraded LLIX to v5 to carry full-ignore paths; snapshot schema v3 and Native ABI v7
  remain unchanged. An empty exclusion list retains previous v3 cache compatibility.
- Invalid maintenance configuration now pauses indexing until corrected and restarted,
  preventing fallback defaults from exposing full-ignored results. Other application
  functions remain available, and user configuration files are never overwritten.
- Coalesced watcher-triggered full index rebuilds with a one-minute minimum interval
  after each scan. Directory churn no longer cancels active scans; ordinary incremental
  updates remain enabled. The periodic reconciliation now defaults to six minutes.
- Moved index-cache loading off the companion handshake thread and added a validated
  `.bak` fallback. Cache publication refreshes unchanged input before scanning completes.
- Retained the previous index after failed scans or Delta overflow, bounded deletion
  bursts, and delayed failed-build retries. Root access and unexpected enumeration
  failures no longer overwrite a usable cache with an incomplete replacement.
- Upgraded LLIX to v4 with an explicit failed-build state so failed maintenance does not
  announce successful completion. Snapshot schema v3 and Native ABI v7 are unchanged.
- Fixed application startup with file indexing enabled. The companion client now exposes
  a constructor that the default dependency-injection container can activate.
- Changed double-Ctrl input activation from a visibility toggle to a three-state focus
  transition: show and focus when hidden, refocus without clearing when visible but
  unfocused, and hide when visible and focused.
- The focus indicator now synchronizes immediately after activation instead of waiting
  for the next text or caret update.
- `Cmd` keeps matched registered domains inside the application dispatcher and reports
  unknown child commands through the message queue; unmatched domains run as Windows
  commands. `Ask` bypasses command matching entirely.
- Native ABI version 10 adds command-editor text replacement and explicit candidate action
  capabilities so Tab completion and Enter execution remain independent.
- Native ABI version 7 adds token-based persistent message activity operations while
  retaining the candidate icon categories from version 6 and the revisioned input,
  activation, and selected-mode contracts introduced in version 5.
- Enter now closes the input only after a selected file or command is successfully
  activated. A non-empty candidate list now selects and visibly highlights its first row
  by default; when no candidates exist, Enter remains ordinary input submission.
- Replaced one recursive filesystem iterator with an explicit Win32 per-directory work
  queue. An unreadable, disappearing, or long-path directory no longer truncates all
  later sibling directories in the user profile.
- The default indexing scope covers the complete current user profile, including its
  Downloads and development subtrees, and retains redirected Windows Known Folders that
  resolve outside that profile.
- Changed filesystem ranking to exact display name, then exact stem, then prefix, with
  deterministic folder-first ties. Same-revision refreshes reuse stable tokens and retain
  the selected candidate when it still exists.
- Upgraded the companion wire protocol to LLIX v3. It retains the explicit file or
  directory result type introduced in v2 and adds `Ready`, `InitialBuild`, and `Updating`
  status values so presentation does not infer work type from generation numbers.
- Hidden persistent-only message queues no longer run a continuous animation timer.
- Candidate publication now treats Native `S_FALSE` as a stale revision instead of
  committing managed activation tokens. Long secondary labels are truncated on a safe
  UTF-16 boundary while activation retains the complete path.
- Removed the second full result extraction, sort, and repack from normal filesystem
  construction. The scan now writes its captured Delta sequence into the packed snapshot
  directly, reducing initial-build CPU time and peak allocations.
- Excluded index-owned snapshot, journal, manifest, and diagnostic-log paths from scanning
  and filesystem notifications, preventing maintenance writes from scheduling themselves.
- An unavailable configured root no longer blocks publication for every healthy root.
  Existing results under the unavailable root are retained while healthy roots reconcile.

### Reserved

- The final candidate row reserves Global Search. Activating it currently reports that
  the feature is not implemented and keeps the input open.
- Filesystem search currently covers case-insensitive exact-name, exact-stem, and prefix
  matching. Fuzzy matching, pinyin matching, NTFS USN-based offline catch-up,
  MFT acceleration, and configurable UI settings are reserved for later iterations.
- Echo is the current natural-language fallback for `Gen` and `Ask`; this response path
  is reserved for a future AI search integration.
