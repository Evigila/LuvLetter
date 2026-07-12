# LuvLetter Architecture

LuvLetter is a Windows-only command shell whose hot path stays in Win32 and
Direct2D. The repository intentionally avoids third-party runtime frameworks.

## Projects

- `src/LuvLetter`: the WPF control center and process composition root. It owns
  the tray icon, global Ctrl gesture hook, configuration window, and user-facing
  error reporting. WPF does not participate in native drawing or text input.
- `src/LuvLetter.Core`: configuration, feature registration, bounded command
  dispatch, and the managed/native interop adapter. A successfully loaded plug-in
  registers its features through `FeatureRegistry`; registry changes are copied
  to Native as immutable snapshots.
- `src/LuvLetter.Native`: the C++ Win32/D2D shell. One dedicated UI thread owns
  both native windows, all HWND state, D2D/DWrite resources, and input state.
- `tests/LuvLetter.Core.Tests`: a zero-NuGet console smoke suite for Core and
  the pure Ctrl gesture state machine.

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
schema version. `LuvLetterConfigurationStore` repairs missing/null sections,
normalizes finite ranges, migrates the legacy top-level hotkey format, writes to
a same-directory temporary file, flushes it, and atomically replaces the old
settings file before publishing the new in-memory snapshot.

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

Run the Core smoke suite with:

```powershell
dotnet run --project tests/LuvLetter.Core.Tests/LuvLetter.Core.Tests.csproj --configuration Release
```
