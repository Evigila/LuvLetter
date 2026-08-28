# Changelog

This document records user-visible additions, behavior changes, and intentionally
reserved product capabilities. Architectural ownership and dependency rules remain in
`architecture.md`.

## Unreleased

### Added

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
  the background, performs a low-priority six-hour reconciliation, and keeps queries on
  an immutable previous snapshot while rebuilding.
- Added a compact C++ filename index with case-insensitive Unicode prefix matching,
  stable ordering, bounded top-result path reconstruction, validated persistence, and
  atomic snapshot replacement.
- Added a non-activating candidate list above the input box. Its default structure is up
  to five direct matches followed by one Global Search entry, and the counts are exposed
  through candidate options for future configuration.
- Added keyboard candidate selection. Up and Down select or move through results; Enter
  opens a selected file, while Shift+Enter opens its containing location in Explorer.
- Added command-name candidates from the registered command snapshot. `Gen` gives file
  results priority and uses commands as remaining direct matches; `Cmd` shows commands
  only; `Ask` shows no candidates.
- Added editor revisions and latest-wins query delivery so slow results from older input
  cannot replace or activate the current candidate list.
- Added index-generation status refresh. The current input is queried again automatically
  when the first background build completes or the companion reconnects, without requiring
  another keystroke.

### Changed

- Fixed application startup with file indexing enabled. The companion client now exposes
  a constructor that the default dependency-injection container can activate.
- Changed double-Ctrl input activation from a visibility toggle to a three-state focus
  transition: show and focus when hidden, refocus without clearing when visible but
  unfocused, and hide when visible and focused.
- The focus indicator now synchronizes immediately after activation instead of waiting
  for the next text or caret update.
- `Cmd` mode preserves strict command behavior and reports unknown commands through the
  message queue; `Ask` bypasses command matching entirely.
- Native ABI version 5 adds revisioned input-change events, candidate snapshots, and
  candidate-activation events while retaining the selected mode on final submission.
- Enter now closes the input only after a selected file or command is successfully
  activated. With no candidate selected, Enter remains ordinary input submission.

### Reserved

- The final candidate row reserves Global Search. Activating it currently reports that
  the feature is not implemented and keeps the input open.
- File search currently covers case-insensitive filename/stem prefixes. Fuzzy matching,
  pinyin matching, NTFS MFT/USN incremental maintenance, and configurable UI settings
  are reserved for later iterations.
- Echo is the current natural-language fallback for `Gen` and `Ask`; this response path
  is reserved for a future AI search integration.
