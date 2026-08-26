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
  initial configuration, registers built-in modules, loads plugins, synchronizes Quick
  Actions, subscribes runtime events, starts gestures, and performs idempotent shutdown.
- `Modules/Settings`: one cohesive settings module containing the public service port,
  editor DTOs, validation/mapping, transactional apply/rollback, and built-in settings
  command/Quick Action registration.
- `Modules/QuickActions`: Quick Action definitions, snapshots, registrar capability,
  registry, and activation results.
- `Plugins`: dynamic assembly discovery and lifetime ownership. External extensions
  implement `ILuvLetterPlugin`; the default directory is `plugins`.
- `Commands`: bounded, serial command registration and dispatch.
- `Activation`: the deterministic Ctrl-gesture state machine and platform port.
- `Configuration`: immutable models, schema migration, normalization, JSON persistence,
  and current-snapshot ownership.
- `NativeShell`: the `INativeShell` port plus the managed ABI adapter, token mapping, and
  bounded callback delivery. Rendering remains in the Native project.

`Modules` means built-in product functionality, with one folder per capability.
`Plugins` exclusively means dynamically discovered external assemblies. There is no
generic `Features` layer and no dynamic `Modules` loader.

### `src/LuvLetter.Native`

- `api`: the stable C ABI and exception boundary.
- `configuration`: native defaults and defensive ABI validation.
- `host/NativeShellHost`: the Native UI-thread owner and request serializer.
- `windows/InputWindow`: input editing, history, IME, animation driving, and rendering.
- `windows/QuickActionsWindow`: Quick Action paging, hotkeys, geometry, and rendering.
- `rendering`: shared animation and layered-window surface primitives.

The internal Native vocabulary is `QuickActions`. ABI v1 deliberately retains its
historic `Feature*` struct and export names so existing managed/native pairs remain
binary compatible; those names are compatibility wire identifiers, not domain modules.

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
2. Register every built-in `IApplicationModule`.
3. Discover and load external plugins from `plugins`.
4. Synchronize the Quick Action snapshot.
5. Subscribe command, Native, registry, and gesture events.
6. Start activation gestures; the lazily created settings window remains closed.

Plugin and initial-load diagnostics are recoverable warnings. A gesture-hook failure
opens Settings as a degraded mode. Fatal partial startup executes compensating cleanup.
Shutdown can be requested by WPF, the tray, or the Host. It stops the hook, unsubscribes
events, hides both Native windows, and releases the plugin session; every step is
idempotent and container-owned singletons are disposed by the Host afterward.

## Configuration compatibility

Configuration is stored in `%AppData%\LuvLetter\settings.json`. Schema 6 writes the
canonical groups `InputBox`, `ActivationGestures`, and `QuickActions`. Older settings
using `FeatureWindow` at the root or inside `ActivationGestures` are migrated before
deserialization. A document containing both legacy and canonical names is rejected as
ambiguous instead of silently choosing one value.

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
