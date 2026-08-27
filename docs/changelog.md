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

### Changed

- Changed double-Ctrl input activation from a visibility toggle to a three-state focus
  transition: show and focus when hidden, refocus without clearing when visible but
  unfocused, and hide when visible and focused.
- The focus indicator now synchronizes immediately after activation instead of waiting
  for the next text or caret update.
- `Cmd` mode preserves strict command behavior and reports unknown commands through the
  message queue; `Ask` bypasses command matching entirely.
- Native ABI version 4 carries the selected input mode with each submitted value.

### Reserved

- `Gen` reserves its matcher pipeline for the built-in file-name and path index. The
  indexer and search-result surface are not implemented in this change.
- Echo is the current natural-language fallback for `Gen` and `Ask`; this response path
  is reserved for a future AI search integration.
