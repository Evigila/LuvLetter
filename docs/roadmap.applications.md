# Application Search Roadmap

This document proposes application discovery, matching, and activation. All additions
below are planned. Implemented file indexing remains documented in `architecture.md`
and `roadmap.indexing.md`; application features enter `changelog.md` after delivery.

## Current behavior

The filesystem index already includes `.exe` files within its configured roots.
`CandidateIconClassifier` gives them an executable glyph, and
`WindowsFileCandidateLauncher.Open` delegates Enter activation to the Windows Shell.
Default roots cover the user profile and redirected user folders, so installed programs
outside those roots are not systematically discovered. Application display names and
aliases are not represented separately from filenames.

The next module should add an application catalog alongside the existing file index.
Its purpose is to find launchable application entries by recognizable names while
preserving ordinary file and folder search.

### Microsoft To Do discovery gap

A local read-only check confirmed Microsoft To Do is installed and registered as
`Microsoft.Todos_8wekyb3d8bbwe!App`. Its package manifest names `Todo.exe` inside
`Program Files\WindowsApps`, outside the default file-index roots. The current code
does not enumerate Windows application registrations, so this launchable application
is not discovered by its Start Menu display name. The checked full-ignore list was
empty; ordinary rebuild-ignore rules do not remove search results.

There is also a separate matching gap: the filename matcher ignores case but preserves
internal spaces. `Microsoft todo` is not a prefix of `Microsoft To Do`; changing the
spelling alone still cannot supply the missing application entry. `Gen` currently
queries files, `Cmd` only offers commands, and `Ask` offers no candidates.

The proposed first release should therefore include packaged-app discovery and
activation, with Microsoft To Do as a manual acceptance case. These remain planned
capabilities; this diagnosis does not implement or launch an application.

## Discovery sources

| Source | Planned behavior |
| --- | --- |
| Current-user and common Start Menu Programs folders | Discover application shortcuts, retain their display names and original launch entries, and resolve metadata on a background STA worker. |
| Current-user and machine App Paths registrations | Read both registry views where available, collect executable aliases and registered paths, and honor per-user precedence. |
| Windows Shell application folder | Discover packaged application display names and stable AppUserModelIDs (AUMIDs), including Microsoft To Do, without recursively scanning package installation directories. |
| Explicit portable-application directories | Enumerate `.exe` files in configured roots with bounded background traversal. Keep these roots separate from general file-index scope. |
| Existing filesystem candidates | Continue to expose matching `.exe` files under current file roots even when they have no application registration. |

App Paths registers executable locations and may provide a per-application search path;
its launch semantics must be retained by the adapter. The discovery sources are
complementary rather than a claim to enumerate every installed program.
[Application registration](https://learn.microsoft.com/en-us/windows/win32/shell/app-registration).

Do not add recursive scans of every Program Files or Windows directory by default.
That would expose many support binaries and repeat expensive work. User-selected
portable roots cover programs without a shortcut or registration. A generic `.lnk`
that opens a document is still a file candidate, not automatically an application.
Packaged application entries belong in the first release. The Shell applications folder
is virtual, so a filesystem traversal cannot substitute for application discovery.
[FOLDERID_AppsFolder](https://learn.microsoft.com/en-us/windows/win32/shell/knownfolderid#FOLDERID_AppsFolder).
Other non-file launch targets can be considered separately.

## Ownership and candidate integration

- Core owns application entry contracts, matching, ranking, and candidate activation
  decisions through `IApplicationCatalog` and `IApplicationLauncher` boundaries.
- The Windows layer owns Start Menu and registry discovery, shortcut metadata, cache
  persistence, and Shell activation. It publishes an immutable catalog generation.
- The C++ filesystem index remains focused on files and directories. Application
  metadata does not expand its compact records, snapshot schema, or LLIX protocol.
- `InputCandidateCoordinator` combines bounded application and file results for the
  latest editor revision. A missing or rebuilding source cannot erase the other source's
  usable candidates. Catalog publication requeries unchanged input.
- Use a distinct managed application activation target and stable identity. Native can
  initially render the existing generic file row with its executable glyph; keep the
  application descriptor behind the token. Adding an application-specific native enum
  later would require coordinated ABI versioning.

An application entry stores a stable ID, display name, aliases, source, launch kind,
launch descriptor, optional resolved executable path, working directory, and generation.
The display string is never parsed as a command line. Native receives only bounded row
text, icon category, and an opaque activation token.

## Matching and result policy

Use case-insensitive exact name/alias matches first, then exact filename stems, then
prefix matches. Executable aliases include the filename with and without `.exe`, so
`Code`, `code.exe`, and `Visual Studio Code` can identify the same entry when discovered
metadata supplies those names. Application entries win equal-strength ties against
ordinary filesystem rows; a weak application prefix must not outrank an exact file match.

Add a lower-priority whitespace-compacted key for application display names and aliases,
so `Microsoft todo` can identify `Microsoft To Do` after discovery. Preserve the original
label and prioritize its exact spelling. Apply this normalization only to application
matching, preserve distinct identities when keys collide, and leave filename matching
unchanged.

Retain the default five direct results plus one Global Search row. `Gen` merges
applications and filesystem matches, then fills remaining capacity with commands;
`Cmd` remains commands-only and `Ask` retains its current behavior. Query text with a
directory separator favors filesystem paths. Full-path text search is not implied by
the current filename-only kernel and needs separate routing if added later.

Deduplicate equivalent launch descriptors across shortcuts, registrations, and files.
Use executable target, arguments, working directory, and relevant launch semantics;
do not merge solely by display name or executable path. Two shortcuts selecting different
profiles or modes remain distinct and receive disambiguating secondary text. When
equivalence cannot be established, preserve both entries. Exact duplicate filesystem
rows can yield their slot to the catalog entry with its friendly name.

For packaged entries, use AUMID as the primary identity. Merge an equivalent Start Menu
entry only when it resolves to that same launch identity, never just a matching label.

## Launch behavior

Enter submits one launch request for the selected entry. Shift+Enter reveals the
resolved executable when available, otherwise the original shortcut. A stale token,
removed target, denied launch, or user-cancelled elevation keeps input visible and
reports the corresponding result. Shell acceptance closes input; it does not guarantee
that the target application has completed initialization.

Launch shortcuts through their original `.lnk` entry. Shortcut metadata can include
arguments, working directory, and show state; replacing it with a bare executable can
change application behavior. Direct portable executables use their containing directory
as the default working directory.
[Shell links](https://learn.microsoft.com/en-us/windows/win32/shell/links).

Packaged applications use their AUMID through `IApplicationActivationManager`, rather
than directly starting an executable from `WindowsApps`. Report its activation result
and retain input on failure. A packaged entry without a meaningful file/shortcut target
reports Shift+Enter reveal as unavailable instead of inventing a filesystem location.
[ActivateApplication](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationactivationmanager-activateapplication).

Use a dedicated Windows STA activation path with explicit success, cancellation, and
failure results. The current `Process.Start(...) != null` check should be replaced:
Shell success does not require a new process handle. Use the Shell operation result,
close returned handles where applicable, and keep launches outside query locks.
[ShellExecuteExW](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shellexecuteexw),
[SHELLEXECUTEINFOW](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-shellexecuteinfow).

## Cache and maintenance

Maintain an independent versioned application cache under
`%LocalAppData%\LuvLetter\Applications`. Load the last validated catalog before a
background source refresh, publish atomically, and preserve a backup. Failed reads of
one discovery source retain that source's last valid entries; a confirmed uninstall
removes entries. Revalidate the launch target when the user activates a cached result.

Start Menu changes request a coalesced refresh. Recheck registrations periodically,
defaulting to six minutes with the established one-minute automatic cooldown. Portable
roots use bounded maintenance rather than running scans on each keystroke. Extend
`index.refresh` to refresh both catalogs. Application maintenance does not launch programs
or read executable icons on the query path. Begin with the existing executable glyph;
real icons, fuzzy matching, pinyin, and usage-based ranking can follow separately.

## Delivery sequence

1. **Classic and packaged applications:** Start Menu shortcuts, App Paths, Windows Shell
   application entries, configured portable roots, executable/display-name aliases,
   bounded merged candidates, deduplication, and explicit launch/reveal outcomes.
   Include Microsoft To Do and AUMID activation; correct generic launch-result handling.
2. **Persistent application catalog:** independent snapshot and backup, startup reuse,
   source-specific failure retention, periodic/event maintenance, and `index.refresh`
   integration. Deliver this with the first user-facing release to preserve fast startup.
3. **Optional coverage:** execution aliases, real icons, and richer matching after the
   first application-discovery and activation paths pass manual acceptance.

## Manual acceptance checklist

1. Search a known executable under the current user profile and confirm Enter starts it.
2. Find a Start Menu application installed outside the profile by its display name and
   executable alias, with and without `.exe`; confirm one equivalent primary entry.
3. Launch a portable executable whose path contains spaces or Unicode and which expects
   its own directory as the working directory.
4. Launch two shortcuts to the same executable with different arguments and confirm their
   modes remain distinct. Reopen an already-running single-instance application without
   reporting failure merely because a new process handle is absent.
5. Delete an indexed target, cancel an elevation prompt, and simulate a stale candidate
   revision; confirm input stays usable and nothing else is launched.
6. Confirm Shift+Enter reveals the intended executable or shortcut, and ordinary file,
   folder, command, and Ask-mode behavior remains unchanged.
7. Restart with a valid application cache, disconnect the filesystem companion, and
   simulate a failed application source refresh; verify available catalog results remain
   usable and refresh automatically after recovery.
8. With Microsoft To Do installed, search `Microsoft To Do` and `Microsoft todo` in
   `Gen`; confirm one equivalent application entry, then verify Enter activation both
   when closed and already running. No package installation directory scan is required.
9. Restart with cached packaged entries, then uninstall a disposable test app and verify
   a stale activation reports failure and the next successful catalog refresh removes it.
   An entry without a filesystem target must handle Shift+Enter explicitly.

These are proposed acceptance scenarios. No application feature or runtime test is
introduced by this planning document.
