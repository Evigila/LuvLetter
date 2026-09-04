# Changelog

This document records user-visible additions, behavior changes, and intentionally
reserved product capabilities. Architectural ownership and dependency rules remain in
`architecture.md`.

## Unreleased

### Added

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
  one-minute per-path trigger cooldown. Added `index.refresh` to force a full scan.
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
  existing five-second lifetime of ordinary messages.
- Added explicit index lifecycle feedback. Initial construction reports
  `正在生成索引表`, background maintenance reports `正在更新索引`, and successful
  publication completes the activity with `索引已就绪`.
- Added command-name candidates from the registered command snapshot. `Gen` gives file
  results priority and uses commands as remaining direct matches; `Cmd` shows commands
  only; `Ask` shows no candidates.
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

### Changed

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
- `Cmd` mode preserves strict command behavior and reports unknown commands through the
  message queue; `Ask` bypasses command matching entirely.
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

### Reserved

- The final candidate row reserves Global Search. Activating it currently reports that
  the feature is not implemented and keeps the input open.
- Filesystem search currently covers case-insensitive exact-name, exact-stem, and prefix
  matching. Fuzzy matching, pinyin matching, durable offline incremental recovery,
  NTFS MFT/USN acceleration, and configurable UI settings are reserved for later
  iterations.
- Echo is the current natural-language fallback for `Gen` and `Ask`; this response path
  is reserved for a future AI search integration.
