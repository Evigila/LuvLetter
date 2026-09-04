# Application Search

Application search is implemented in `Gen`. This document records source coverage,
ranking, Windows activation, and manual acceptance cases. Compilation is verified;
runtime acceptance remains a manual task.

## Discovery sources

| Source | Implemented behavior | Boundary |
| --- | --- | --- |
| User and common Start Menu Programs folders | Read trusted `.lnk` entries, retain the original shortcut and localized label, and support executable, `.msc`, `.cpl`, and Shell/PIDL targets. | Document links and untrusted or unresolved targets are not promoted to applications. |
| User and machine App Paths | Read both registry views per hive and retain registered paths, aliases, and private search directories. User/machine precedence is applied when equivalent results merge. | This is registration discovery, not a scan of all installed executables. Registry changes depend on periodic or forced refresh. |
| Windows Shell AppsFolder | Discover packaged AUMIDs and non-package launchable Shell items with localized display names. | Covers entries exposed by AppsFolder, including Microsoft To Do; does not recursively scan WindowsApps. |
| Curated Windows system entries | Publish a strict local whitelist of Settings pages, Control Panel canonical entries, MMC tools, and common system executables. | Arbitrary URI, `shell:` text, and unlisted Control Panel targets are rejected. Availability is checked locally. |
| Configured portable roots | Recursively discover `.exe` entries, using file descriptions and filename aliases. | Empty by default; reparse points are not traversed. |
| Existing file index | In-scope `.exe` results receive application ranking bias and an executable glyph. | Other `.lnk` files remain ordinary file candidates unless discovered as applications. |

These sources do not promise every installed program. Programs without registrations
need explicit portable roots or existing file-index coverage. Program Files and Windows
are not recursively scanned by default. Traversal is bounded to 100,000 visited entries,
20,000 matches per source, and 32 directory levels; the catalog permits 20,000 entries
before deduplication.

Configure portable roots in
`%LocalAppData%\LuvLetter\Applications\settings.json`:

```json
{
  "PortableRoots": ["D:\\PortableApps"]
}
```

Use absolute paths; environment variables are expanded. Settings are read once per
launch. Invalid application settings pause this catalog with a console diagnostic.
Correct the file and restart; `index.refresh` does not reload configuration.

## Matching and ranking

Applications use case-insensitive exact and prefix name/alias matching. Executable
aliases include filenames with and without `.exe`. A lower-priority whitespace-compacted
key allows `Microsoft todo` and `Microsoft To Do` to identify the same discovered entry.
Filename matching is unchanged. Queries containing directory separators are not treated
as application names. Substring, fuzzy, pinyin, reordered-word, and full-path matching
remain future work.

Direct matches pass through `ICandidateRankingPolicy` before visible truncation:

```text
application bias + name-match score + additional priority
```

`CandidateRankingOptions.ApplicationBias` defaults to 1,000 for catalog applications
and standalone executables, and zero for ordinary files/folders. Match scores range
from 80 to 300: exact display name, exact alias, compacted exact name, literal prefix,
then compacted prefix. File scores retain exact name, exact stem, and prefix tiers.
Without additional priority, a matching application precedes even an exact ordinary
file match. Ties retain the source's stable order.

`ICandidatePriorityProvider` can add priority to application or file identities, allowing
a file to outrank an application. This release does not record usage, learn from launches,
or persist priority history. The catalog cache accelerates loading and has no ranking
advantage. Each source currently retrieves up to 64 matches before ranking; future
history-aware retrieval must also address items outside that pool.

The default remains five direct results plus one reserved Global Search row. `Gen`
merges applications and files, then fills unused direct slots with commands. `Cmd`
remains commands-only; `Ask` has no candidates. Application publication refreshes
unchanged input. On a new revision, available applications can appear while the file
query completes; same-revision refresh preserves surviving activation tokens.

## Identity and activation

Core owns application contracts, name matching, ranking, and activation decisions.
Windows owns discovery, persistence, and launch adapters. Native renders its existing
file row with an executable glyph and opaque token; file snapshot v3 and Native ABI v7
remain unchanged. LLIX v6 carries filesystem partition descriptors independently of
application activation.

Entries have stable source IDs and explicit launch descriptors. Display text is never
parsed as a command line. Identical shortcut bytes can merge, packaged entries use AUMID,
and executable registrations retain their launch metadata. Equivalent names become
aliases. Matching labels alone never establish equivalence. Different shortcut arguments,
show states, or elevation metadata remain distinct. Ambiguous cross-source duplicates
remain separate. A file row yields only to a known equivalent catalog launch target.

Enter submits one catalog activation at a time on a dedicated background STA worker.
Shortcuts launch through their original `.lnk`; portable executables use their containing
directory as the working directory. Packaged applications use
`IApplicationActivationManager` with their AUMID. Cached targets, registrations, and
known full-ignore paths are rechecked before activation. Removed or changed entries
report a refresh/retry diagnostic.

App Paths launches use the verified absolute executable, preventing same-name search
collisions. Registrations with private search directories use a child-only PATH
environment and `UseShellExecute=false`; the host PATH stays unchanged. If this route
requires elevation, it reports that the original shortcut is needed instead of silently
dropping the environment. Other classic launches use `ShellExecuteExW`.
[App Paths registration](https://learn.microsoft.com/en-us/windows/win32/shell/app-registration).

Shell success no longer depends on receiving a new process handle; the generic file
launcher uses the same corrected acceptance rule. Failure or cancelled elevation keeps
input visible. Success closes it only while the originating editor revision is current.
Acceptance does not certify completed application initialization.

Shift+Enter reveals a classic application's resolved executable. Packaged entries report
that ordinary file reveal is unavailable and keep input open. Real executable icons,
thumbnails, and user-defined launch arguments remain future work.

## Cache and maintenance

Each discovery source owns an independent cache pair under:

```text
%LocalAppData%\LuvLetter\Applications\v2\partitions\<source-hash>.manifest.json
%LocalAppData%\LuvLetter\Applications\v2\partitions\<source-hash>.snapshot.json
%LocalAppData%\LuvLetter\Applications\v2\partitions\<source-hash>.manifest.json.bak
%LocalAppData%\LuvLetter\Applications\v2\partitions\<source-hash>.snapshot.json.bak
```

Startup loads and publishes each compatible source cache independently before discovery.
Validation checks schema, size, checksum, source identity, entry shape, source scope, and
full-ignore provenance. An invalid primary pair falls back to its compatible backup pair.
Each completed source immediately rebuilds the immutable merged query view and notifies
the candidate pipeline. A first launch without a usable source cache still requires that
source's discovery.

A failed source retains its previous entries; a successful empty source removes them.
Healthy sources publish and persist without waiting for slow or failed sources. Atomic
saves retain the preceding valid generation for that source as backup; persistence
failure does not discard usable in-memory results. A disconnected portable root is
unavailable, not empty. Old merged v1 application caches are not migrated.

Maintenance shares the file-index configuration: each application source has a six-minute
periodic deadline and a 60-second automatic gap by default, with bounded per-path cooldown
for Start Menu events. Failed sources retain dirty state and retry after 1, 2, 4, then at
most 6 minutes. Start Menu changes target only their owning source; registrations,
AppsFolder, system entries, and portable roots refresh periodically. `index.refresh`
requests all application and file partitions and bypasses automatic cooldown.

Dispatch is bounded globally and by source category. Shell metadata and all other
discovery operations use two fixed STA workers, while activation owns a third fixed STA
worker. Adding portable roots therefore does not add permanent threads. One source has at
most one active discovery plus one coalesced follow-up request.

For known events, full ignore and ordinary rebuild ignore precede cooldown. Ordinary
ignore retains periodic discovery and search. `FullIgnorePaths` excludes known shortcut
targets, executables, working directories, and package installation paths at discovery,
cache publication, and activation. App Paths search directories are environment metadata,
not indexed targets. Unattributed watcher errors request recovery. Debug events reuse
the existing cause colors and identify `catalog=applications`.

## Manual acceptance checklist

1. Start with `start.bat`. In `Gen`, search `Microsoft To Do` and `Microsoft todo`;
   confirm the packaged application appears and Enter opens it.
2. Find a Start Menu application outside the profile using its label and executable
   alias. Confirm Enter launches it and Shift+Enter selects its executable in Explorer.
3. Use a query shared by an application and an ordinary file; confirm the application
   appears first. Check standalone executable priority and an ordinary document shortcut.
4. Configure a narrow portable root, restart, and check space/Unicode paths, aliases,
   and working-directory-sensitive launch.
5. Check two shortcuts with different arguments to the same executable remain available;
   identical copied links should merge. Matching labels alone must not collapse entries.
6. Restart with a cache and observe results before discovery completes. Keep the same
   query open through refresh and confirm surviving selection remains stable.
7. With LuvLetter stopped, back up the cache and damage its primary; restart and confirm
   backup recovery. Repeat with neither usable file and confirm discovery rebuilds it.
   Restore saved files after the check.
8. Disconnect a portable root, add a healthy Start Menu application, refresh, and restart.
   Confirm the healthy addition is cached and the unavailable source retains old entries.
   Activating an unavailable target must report failure without closing input.
9. Repeatedly change a disposable Start Menu shortcut. Confirm ignored changes never
   enter cooldown; other events provide trigger/cooldown context. Run `index.refresh`
   in `Cmd` and confirm both catalogs report forced work in green.
10. Full-ignore a disposable shortcut or application path, restart, and verify cached and
    fresh results exclude it. Ordinary ignore must retain search coverage.
11. Cancel elevation, retry an already running program, and remove a disposable target
    after discovery. Verify launch feedback and repeated-Enter suppression.
12. Change input/mode while an activation is pending; completion must not close the new
    query. Check `Cmd`, `Ask`, file/folder reveal, and reserved Global Search behavior.
13. Manually run the Core test executable after building to check matcher tiers, distinct
    IDs, injected file priority, ranking before truncation, source failure isolation,
    same-revision tokens, and cancelled/stale application activation.

## Remaining work

Usage-history collection and persistence, pinning/recency, history-aware retrieval,
execution aliases, richer matching, real icons, source diagnostics in Settings, and
the full Global Search page are separate follow-up capabilities. App Paths registry,
AppsFolder, and curated-system change notifications are not subscribed yet and rely on
periodic or forced refresh. Removed portable-source cache files are harmless and are not
garbage-collected yet.
