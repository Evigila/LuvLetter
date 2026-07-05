# LuvLetter Architecture

LuvLetter is currently reduced to a small Windows-only command input shell.
The codebase is intentionally minimal while the new foundation is being built.

## Projects

- `src/LuvLetter`: WPF control center. It owns only the startup window and the
  hotkey configuration page.
- `src/LuvLetter.Core`: C# core library. It owns the hotkey data model,
  persisted hotkey settings, and managed interop for the native input box.
- `src/LuvLetter.Native`: C++ native library. It owns the Win32/D2D input box
  module and all keyboard input captured by that box.

## Runtime

- The default activation hotkey is `Alt+F1`.
- The WPF control center can record and apply input box configuration.
- The native input box is shown by the registered hotkey and takes focus so
  typed content is handled by the native window.

## Configuration

Configuration is stored in `%AppData%\LuvLetter\settings.json` through
`LuvLetterConfigurationStore`.

The current input box configuration groups are:

- `InputBox.Hotkeys`: activation, submit, cancel, and backspace hotkeys.
- `InputBox.Placement`: position mode, offsets, bottom margin, and custom
  coordinates.
- `InputBox.Colors`: border, background, text, and caret colors using
  `#RRGGBB` or `#AARRGGBB`.
- `InputBox.Size`: box width, box height, corner radius, border thickness,
  font size, and horizontal text padding.

## Native Input Box Module

The input box is implemented as `input/InputBoxHost` in `LuvLetter.Native`.
It renders one rounded rectangle with:

- corner radius `8`;
- white border with thickness `2`;
- translucent light-gray fill;
- blur/acrylic-style composition behind the window where the OS supports it.
