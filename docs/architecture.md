# LuvLetter Architecture

LuvLetter is a Windows-only command shell whose hot path stays in Win32 and
Direct2D. The repository intentionally avoids third-party runtime frameworks.

## Projects

- `src/LuvLetter`: the WPF configuration center and Windows process shell. It owns
  views, control binding, the tray icon, the low-level keyboard-hook adapter, the
  built-in UI module, process composition, and user-facing error reporting. WPF
  does not contain gesture recognition, configuration persistence, plug-in loading,
  native drawing, or text-input logic.
- `src/LuvLetter.Core`: application and core logic. It owns activation gesture
  recognition, configuration application and rollback, configuration persistence,
  feature registration, bounded command dispatch, module discovery/registration,
  and the managed/native interop adapter. Registry changes are copied to Native as
  immutable snapshots.
- `src/LuvLetter.Native`: the C++ Win32/D2D shell. One dedicated UI thread owns
  both native windows, all HWND state, D2D/DWrite resources, and input state. It is
  the only project that renders the command and feature windows.
- `tests/LuvLetter.Core.Tests`: a zero-NuGet console smoke suite for Core and
  its application/core modules.
- `tests/LuvLetter.Native.Tests`: a zero-dependency C++ smoke suite for the deterministic
  input animation module; it tests the source directly without expanding the public C ABI.

The compile-time dependency direction is `LuvLetter -> LuvLetter.Core`. The Native
DLL is reached only through the versioned C ABI; Native never references a managed
assembly.

## Module boundaries

### LuvLetter

- `Settings`: WPF control mapping, editor input models, value parsing, hotkey capture,
  and one parser per configuration section. `MainWindow` only coordinates view events
  and the Core application service.
- `Hotkeys`: the Windows low-level keyboard hook and WPF Dispatcher adapter. Gesture
  recognition itself lives in Core.
- `Tray`: notification-area UI and settings-window lifetime.
- `Modules`: UI-specific built-in modules.
- `Program`: the composition root. It creates services and connects events, but does
  not implement configuration, command, feature, or gesture policy.

### LuvLetter.Core

- `Activation`: the deterministic Ctrl gesture state machine and the activation-service
  port implemented by the Windows shell.
- `Application`: use-case orchestration such as applying a configuration across Native,
  gesture recognition, and persistent storage with compensating rollback.
- `Commands`: command registration plus bounded, serial asynchronous dispatch.
- `Configuration`: immutable models plus separate normalizer, schema migrator, JSON
  repository, and current-snapshot store.
- `Features`: feature definitions, registration, snapshots, and activation.
- `Modules`: public module contract, assembly discovery, deferred registration context,
  per-module failure isolation, and successful `IDisposable` module lifetime ownership.
  Modules receive only minimal command/feature registrar capabilities.
- `Native`: managed ABI declarations, configuration mapping, feature token mapping,
  bounded callback delivery, and Native-session lifecycle. No rendering occurs here.

### LuvLetter.Native

- `api`: the stable exported C ABI and exception boundary.
- `input/FeaturePager`: Win32-independent feature paging and index resolution.
- `input/InputHistory`: Win32-independent bounded command history, de-duplication,
  draft preservation, and navigation.
- `input/InputBoxAnimator`: Win32-independent, reversible presentation state for the
  command input. It owns timing progress, easing, opacity, horizontal expansion, and
  vertical offset, but does not own clocks, timers, HWNDs, or D2D resources.
- `input/NativeConfigurationSanitizer`: Native-side defaults and defensive range/
  enum validation for both window configuration structs.
- `input/InputBoxHost`: ownership of the single Native UI thread and both HWNDs. Input,
  IME, D2D resource, and window-controller responsibilities should continue to move
  into focused collaborators without changing the ABI or thread-ownership model.

Modules may depend inward on public Core contracts. Core must not reference WPF,
Windows Forms, or application views. Native must not know module implementations or
managed delegates; it receives only configuration structs, display snapshots, and
opaque feature tokens.

## Activation

The default gestures are:

- tap and release Ctrl twice to toggle the command input;
- tap and release Ctrl once, then press and hold the same Ctrl key to toggle the
  feature window.

The tap duration, second-press timeout, hold threshold, permitted Ctrl sides,
and gesture-to-window mapping are configurable. The low-level hook observes but
does not suppress keyboard input.

## Native windows

The command input is a rounded, translucent layered window with IME support,
bounded input history, horizontal text scrolling, and a managed submit callback.
On entry it rises from below, fades in, and expands from the center; on exit it
reverses the same path and the HWND is hidden only after the transparent final frame.
The fixed-size layered surface is retained during animation: Native changes the
visual bounds inside the bitmap instead of reallocating the HWND and DIB every frame.
Switching directly to the feature window bypasses the input exit animation so the
two popup windows remain exclusive and the feature window can receive focus at once.

The feature window is a row of rounded square cells drawn by Native. It displays
at most seven registered features per page. The default controls are:

- `1` through `7`: activate the corresponding cell on the current page;
- `-` / `=`: previous or next page, wrapping at either end;
- `Escape`: close the feature window.

Cell count, size, gap, colors, font, placement, paging keys, cancel key, and the
first numeric activation key are configurable. Showing either native window
hides the other one.

Both layered windows are Per-Monitor-V2 aware. Configuration geometry is stored
as 96-DPI device-independent units; Native scales the window, cached DIB,
regions, placement, mouse hit testing, and IME position for the target monitor.

The current translucent style uses a per-pixel-alpha layered window. Acrylic or
blur is not currently implemented.

## Managed/native boundary

The native ABI is versioned. Every configuration structure carries `structSize`
and `abiVersion`, and both sides validate their expected layout before use.
Configuration, feature snapshots, and callback registration are synchronously
marshalled onto the Native UI thread. Display commands are posted asynchronously.

Native callbacks expose borrowed memory only for the duration of the callback.
Managed code immediately copies input text or feature tokens and queues business
work away from the Native UI thread. Exceptions are contained on both sides of
the ABI.

## Configuration

Configuration is stored in `%AppData%\LuvLetter\settings.json` with an explicit
schema version. The configuration module repairs missing/null sections, normalizes
finite ranges, and migrates supported historical visual defaults. The obsolete
top-level activation-hotkey format is rejected and replaced with the default Ctrl
gestures. The JSON repository writes to a same-directory temporary file, flushes it,
and atomically replaces the old settings file before the Store publishes the new
in-memory snapshot.

The top-level groups are `InputBox`, `ActivationGestures`, and `FeatureWindow`.

## Build and publish

The C++ project requires the full Visual Studio MSBuild; the .NET CLI MSBuild
cannot build a `.vcxproj` directly. From a Visual Studio Developer PowerShell,
build the complete solution with:

```powershell
MSBuild.exe LuvLetter.slnx /m /p:Configuration=Release
```

Publishing `src/LuvLetter/LuvLetter.csproj` with the same MSBuild automatically
builds the x64 Native project and includes `LuvLetter.Native.dll`. Native uses the
static MSVC runtime, so the published DLL has no VC++ Redistributable dependency.

Run the Core smoke suite (currently 15 scenarios) with:

```powershell
dotnet run --project tests/LuvLetter.Core.Tests/LuvLetter.Core.Tests.csproj --configuration Release
```

Run the Native smoke suite with:

```powershell
MSBuild.exe tests/LuvLetter.Native.Tests/LuvLetter.Native.Tests.vcxproj /m /p:Configuration=Release /p:Platform=x64
tests/LuvLetter.Native.Tests/bin/x64/Release/LuvLetter.Native.Tests.exe
```
