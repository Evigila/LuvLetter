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
  positioned above InputWindow. It stores copied display data and applies only the
  candidate snapshot matching the current editor revision.
- `windows/QuickActionsWindow`: top-aligned Quick Action paging, hotkeys, animation,
  geometry, and rendering.
- `windows/MessageQueueWindow`: a read-only, non-activating bottom-left notification stack.
  It renders up to six compact, independent notification bubbles without taking focus.
- `rendering`: shared animation and layered-window surface primitives.

The internal Native vocabulary is `QuickActions`. ABI v5 deliberately retains its
historic `Feature*` struct names and layouts; those names are compatibility wire
identifiers, not domain modules. The v5 version gate adds revisioned input-change and
candidate-activation callbacks plus atomic candidate snapshots while retaining the
submitted input mode, message queue, and `HidePopups` exports. A new Managed assembly
therefore cannot silently pair with an older DLL.

### `src/LuvLetter.IndexKernel`

- A C++20 static library containing the compact immutable filename index.
- Directory components and file names are stored in continuous tables and a shared
  UTF-16 string pool instead of one full path allocation per entry.
- Prefix queries use the sorted filename records and reconstruct full paths only for the
  bounded result set.
- The persisted snapshot validates its magic, schema, sizes, references, and ordering
  before it is accepted.

### `src/LuvLetter.Indexer`

- A hidden, ordinary-user companion process owned by the main application.
- It connects to a per-run random Named Pipe, exits when the pipe closes or its parent
  process ends, serves queries from the current immutable snapshot, and rebuilds on a
  below-normal-priority maintenance thread.
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

A newly published candidate list has no selection. Up or Down creates and moves the
selection. Enter activates the selection; Shift+Enter reveals a selected file in
Explorer. A successful file or command activation closes InputWindow, while Enter with
no selected candidate follows ordinary submission and does not close it. Global Search
currently reports its reserved status through the message queue and keeps the input open.

The built-in settings plugin is always Quick Action slot 1 and is displayed as
`Control Center`. Quick Actions exposes the numeric slots 1 through 9. Selecting an
unassigned slot closes the Quick Actions window and reports a diagnostic through the
message queue. All coordinator status reports are mirrored to that queue and retain
their existing WPF/tray status fallback.

The message queue starts empty and hidden. Enqueuing a non-empty message shows it
without activating it. Each bubble has its own five-second lifetime; expiry continues
while the window is manually hidden, and the window hides automatically when the last
bubble expires. Alt+Backspace hides a visible queue or shows the unexpired bubbles; it
is a no-op when none remain. A subsequent message shows the queue again. Escape
deliberately does not hide this read-only status surface. The active stack is bounded
at six bubbles, discarding the oldest on overflow; long text is kept to one line and
trimmed with an ellipsis.

Each bubble owns an independent monotonic timeline: it enters from the left over 180 ms,
starts its reverse leftward exit five seconds after enqueue, and is removed after the
140 ms exit completes. One adaptive timer renders 16 ms frames only while the queue is
visible and a transition is active; otherwise it sleeps until the next lifecycle
boundary. Manually hiding the surface therefore pauses rendering, not message lifetime.

Writes use a same-directory temporary file, flush it, atomically replace the old file,
and only then publish the new in-memory snapshot.

## File-index lifecycle and protocol

The default root is the current user profile. The last valid snapshot is loaded from
`%LocalAppData%\LuvLetter\Index\v1\file-index-v2.bin`, so queries can use the previous
generation while a startup rebuild runs. A complete low-priority background rescan
reconciles the index every six hours; the first version intentionally omits MFT/USN
integration, fuzzy matching, pinyin matching, and privileged services.

The `LLIX` protocol uses a fixed 20-byte little-endian header, UTF-8 length-prefixed
strings, request IDs, editor revisions, and a 1 MiB payload ceiling. Managed owns the
single pipe server and starts `LuvLetter.Indexer.exe` with the pipe name, parent process
ID, and data directory. The pipe is restricted to the current user. Protocol, timeout,
or process failures invalidate the whole session and trigger bounded background restart;
the command and Echo paths remain available. A compact status request reports the index
generation and rebuild state. Session readiness and completed generations requeue the
latest unchanged editor revision, so a user does not need to type another character
after the initial background build or a companion restart.

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

Do not use `dotnet build LuvLetter.slnx` for a complete build. The solution contains
two `.vcxproj` projects, while the .NET SDK version of MSBuild does not include
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
