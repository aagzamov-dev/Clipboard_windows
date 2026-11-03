# Windows development

- This is an independent native Windows 11 repository. Use C# and WPF; keep the compact macOS Clipboard layout and behavior as the reference.
- Keep changes minimal, use native Windows APIs, and avoid web views and background polling where Windows notifications are available.
- Run `Scripts/check.ps1` on Windows for core tests and a Release build. The core tests also run on macOS with .NET 10.
- Snapshot the foreground window for paste, but open beside the mouse on its monitor. Respect taskbars, negative coordinates, and per-monitor DPI.
- Register shortcut replacements before releasing the previous registration. A conflict or failed settings write must preserve the previous shortcut and saved settings.
- Keep user data in `%LOCALAPPDATA%\Clipboard`; never clean preferences or clipboard history as build artifacts.
- Keep clipboard capture on its dedicated STA thread. Never capture existing contents on start/resume, secrets with supported exclusion markers, or the app’s own writes.
- Test core behavior cross-platform and cross-compile the Windows application. Distinguish that from native runtime verification; never claim Windows UI behavior was tested on macOS.
- Do not publish releases or configure a remote without the user’s instruction.
