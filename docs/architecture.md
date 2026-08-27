# LuvLetter Architecture

LuvLetter is a Windows command shell with a WPF presentation shell, a WPF-independent
Core application layer, and a native Win32/Direct2D renderer. `Microsoft.Extensions.Hosting`
owns dependency composition and service lifecycle.

## Dependency direction

```text
LuvLetter (WPF views + Windows adapters + composition)
    -> LuvLetter.Core (application coordination + business modules + contracts)
    -> LuvLetter.Native (only through the versioned C ABI at runtime)
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
- `Platform/Tray`: notification-area UI and settings-view lifetime.
- `Hosting`: Generic Host registrations and the WPF-specific `IHostLifetime`.
- `Program`: the STA/single-instance entry point. It builds and starts the Host, delegates
  the WPF dispatcher loop to `WpfHostLifetime`, then stops and disposes the Host.

### `src/LuvLetter.Core`

- `Application/ApplicationCoordinator`: the single application `IHostedService`. It applies the
  initial configuration, loads built-in and external plugins, synchronizes Quick
  Actions, subscribes runtime events, starts gestures, and performs idempotent shutdown.
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
- `windows/QuickActionsWindow`: top-aligned Quick Action paging, hotkeys, animation,
  geometry, and rendering.
- `rendering`: shared animation and layered-window surface primitives.

The internal Native vocabulary is `QuickActions`. ABI v2 deliberately retains its
historic `Feature*` struct names and layouts; those names are compatibility wire
identifiers, not domain modules. The v2 version gate advertises the required atomic
`HidePopups` export so a new Managed assembly cannot silently pair with an older DLL.

## Host lifecycle

The Generic Host is started synchronously on the WPF STA thread before
`WpfHostLifetime.Run()` enters the WPF dispatcher loop. This ensures the tray, settings
factory, native adapter, and global hook are first created on the UI thread.
`WpfHostLifetime` bridges shutdown in both directions: WPF exit requests Host shutdown,
while Host shutdown requests WPF dispatcher shutdown. If Host startup fails before the
dispatcher starts, shutdown completes without waiting for an `Application.Exit` event
that cannot occur. The Host owns singleton disposal and `IHostedService` start/stop.

`ApplicationCoordinator` is the only hosted business coordinator. Startup order is:

1. Apply current InputBox and QuickActions configuration to Native.
2. Load the mandatory built-in plugins, then discover external plugins from `plugins`.
3. Synchronize the Quick Action snapshot.
4. Subscribe command, Native, registry, and gesture events.
5. Start activation gestures; the lazily created settings window remains closed.

Plugin and initial-load diagnostics are recoverable warnings. A gesture-hook failure
opens Settings as a degraded mode. Fatal partial startup executes compensating cleanup.
Shutdown can be requested by WPF, the tray, or the Host. It stops the hook, unsubscribes
events, hides both Native windows, and releases the plugin session; every step is
idempotent and container-owned singletons are disposed by the Host afterward.

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
and Escape globally dismisses both popups through one serialized Native request. The
two Native popups have independent visibility: command input is bottom-centered and
enters upward, while Quick Actions is top-centered and enters downward.

Writes use a same-directory temporary file, flush it, atomically replace the old file,
and only then publish the new in-memory snapshot.

## Build and tests

The C++ project requires full Visual Studio MSBuild:

```powershell
MSBuild.exe LuvLetter.slnx /m /p:Configuration=Release
```

Run the 17 Core smoke scenarios with:

```powershell
dotnet run --project tests/LuvLetter.Core.Tests/LuvLetter.Core.Tests.csproj -c Release
```

Run the Native suite with:

```powershell
MSBuild.exe tests/LuvLetter.Native.Tests/LuvLetter.Native.Tests.vcxproj /m /p:Configuration=Release /p:Platform=x64
tests/LuvLetter.Native.Tests/bin/x64/Release/LuvLetter.Native.Tests.exe
```
