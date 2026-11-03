# Clipboard for Windows 11

A compact native clipboard manager in C# / .NET 10 and WPF. This is an independent Git repository; the macOS app remains in the sibling `../MacOS` repository.

## Use

Run `Clipboard.exe` from a complete extracted Windows package. Choose **Start local history** to begin recording; existing clipboard contents are not captured. History stays on your PC in `%LOCALAPPDATA%\Clipboard`.

Press **Ctrl+Alt+V** or click the tray icon to open beside the mouse, on the mouse’s monitor. The popup respects taskbars, negative monitor coordinates, and mixed DPI. Its list and preview sizes are remembered per monitor. Opening uses a 65 ms fade when system animations are enabled; dismissal is immediate.

- Click an entry to paste into the previous app. Its **Preview** button opens the editor; **Copy** copies without pasting. Hover the icons for names and shortcuts.
- Turn on **Keep open** in the header to keep Clipboard above other apps while you work or paste. Turn it off to restore automatic dismissal; closing the picker resets the mode. Opening Settings or Clear history closes the picker and resets the mode too. Returning to the kept-open picker uses the previous app identified by Windows as its paste destination; an unavailable or invalid destination clears the target. Each row has its own **Pin/Unpin** button; highlighted pins indicate saved pinned entries and do not trigger paste.
- The compact picker defaults to 440 × 520 DIP. Drag anywhere in its title area to move it; drag an edge to resize. Existing per-monitor sizes are kept. Image entries show thumbnails and a larger preview. Local PNG, JPEG, BMP, GIF, TIFF, and ICO file references can also be previewed without changing file Copy/Paste behavior. Image files on shares, redirected/linked paths, and unsupported locations stay as references. Previews are limited to 20 MB and 64 million source pixels; thumbnails decode to a 160-pixel edge and larger previews to 1024 pixels. Hidden or recycled rows cancel their pending previews.
- Opening or closing Preview keeps the current window position. Growth is limited to the space remaining on that monitor; near an edge, Preview uses the compact single-pane layout. Temporary screen-edge fits do not replace your preferred list/preview sizes. New popup openings still appear beside the mouse.
- Preview preserves the list's opening width, item widths, and visible height. Extra window space goes to Preview; a wider or taller list also sets the minimum requested preview window size. Screen limits can temporarily use the compact fallback or reduce visible height, without replacing the captured list size.
- Search and filter by type, pin, or date. All history is the default. Day arrows browse older dates. History loads in pages of 100 with virtualized rows.
- Edit text in Preview. **Save** updates history; Copy/Paste use the draft immediately. Text actions trim whitespace, remove blank lines, and change case.
- Ctrl-click or Shift-click text entries to select several. Space opens their combined editable preview. Original entries stay unchanged; combined drafts are retained per selection until copied/reverted/discarded.
- New captures wait behind **Refresh** while you browse or edit. New copies never overwrite your active draft or selection.
- History stays until you delete it, without automatic expiry or an item limit, including when older settings are imported. **Clear history** offers copies older than 7, 30, or 90 days, all unpinned history, or everything. Age-based cleanup preserves pinned entries, unsaved drafts, and the newest copy; it removes older copies, never the selected recent date range. Permanent cleanup requires confirmation.
- The tray menu pauses recording manually or for 5, 15, or 60 minutes. Recording also suspends while Windows is locked.
- Settings includes System/Light/Dark appearance, app exclusions, launch at login, backup/restore, and settings import/export.
- Windows 11 acrylic, rounded corners, and native window shadows frame the popup and settings. Transparency follows Windows accessibility and transparency preferences; unsupported systems use a solid background.

## Shortcuts

| Shortcut | Action |
| --- | --- |
| Ctrl+Alt+V | Toggle popup; configurable in Settings |
| Ctrl+, | Settings |
| Ctrl+F | Search |
| Down in search | Move to history list |
| Up/Down, Shift+Up/Down | Navigate / extend selection |
| Space in list | Toggle preview |
| Enter | Paste selected entries |
| Ctrl+Enter | Paste current selection/draft from the editor |
| Shift+Enter / Ctrl+Shift+Enter | Paste plain text from list / editor |
| Ctrl+C / Ctrl+Shift+C | Copy selection / current draft |
| Ctrl+S | Save individual text draft |
| Ctrl+P | Pin/unpin list selection |
| Ctrl+1–Ctrl+9 | Paste a corresponding result from the current page |
| Delete | Delete list selection; Undo restores it before cleanup |
| Escape | Clear bulk selection, close preview, then dismiss |

Text fields retain normal editing shortcuts. Unsaved drafts stay in memory across popup dismissal; normal Quit asks to save or discard. As with ordinary unsaved documents, a crash, forced termination, or OS shutdown can lose unsaved drafts.

## Privacy and limits

No accounts, analytics, clipboard uploads, or background network traffic are included unless update checks have been configured for a Windows release repository. The local SQLite database is **not encrypted**; use the app only for content you intend to keep. Supported confidential markers (including Windows history exclusion and common password-manager markers) are skipped; this is not secret detection. App exclusions check both clipboard owner and foreground executable names.

Text/RTF: 2 MB each. PNG images: 20 MB and 64 million pixels. File references: 100 per copy. Total edited draft text: 8 MB. Image previews decode to at most a 1024-pixel edge. Busy clipboard reads retry briefly on a dedicated STA thread; capture has a bounded queue, and a skipped/failed capture is reported.

Automatic paste restores the remembered destination, waits for released modifier keys, verifies foreground ownership and the app’s own clipboard write, then sends Ctrl+V once. Windows integrity levels, secure desktops, or destination behavior can block it; Copy and manual Ctrl+V remain available. The app never elevates itself to bypass this restriction.

Backups are either plain ZIP databases or password-encrypted, streaming AES-256-GCM archives with PBKDF2-SHA256 (310,000 iterations). Deleted entries are scrubbed from exported snapshots. Restore validates schema, limits, identities and content hashes, then merges in one transaction; conflicts or authentication failures leave live history intact. File backups include references, not original files. Backups are limited to 200,000 active entries and a 2 GB database/archive. Backups are Windows-specific and do not import the Mac archive format.

## Build, check, package

Install the **.NET 10 SDK** and **PowerShell 7** (`pwsh`). From this folder:

```powershell
./Scripts/check.ps1
# Run from source on Windows:
dotnet run --project src/Clipboard.Windows
# Create a self-contained x64 ZIP:
./Scripts/publish.ps1 -Runtime win-x64
# Or ARM64:
./Scripts/publish.ps1 -Runtime win-arm64
```

On Windows, `check.ps1` also runs isolated WPF image-preview component checks. They use temporary sample history and hidden composition targets, never the live clipboard or visible app windows. Optional layout renders: `dotnet run --project tests/Clipboard.Windows.Tests -c Release -- --render .build/image-review`.

No third-party UI framework or web view is used. SQLite is the only application package dependency. Core regression tests also run on macOS/Linux:

```sh
dotnet run --project tests/Clipboard.Core.Tests -c Release
```

Packages include their .NET runtime, native SQLite library, licenses, an installation manifest with per-file SHA-256 checksums, and Install/Uninstall scripts. Windows does not need a separately installed .NET runtime. Extract the whole ZIP; optionally run **Install.ps1** to install for the current user and create a Start shortcut. Quit the installed copy before replacing it. Install/uninstall preserve `%LOCALAPPDATA%\Clipboard`; previous binaries are retained for recovery. Packages are unsigned development builds; code signing is not configured.

## Updates and verification

A Windows release repository is not configured, and nothing is published. To enable daily/manual GitHub release checks in a release build:

```powershell
./Scripts/publish.ps1 -Runtime win-x64 -UpdateRepository owner/Clipboard_windows
```

Use a dedicated Windows repository with `vMAJOR.MINOR.PATCH` release tags. Update checks send only a standard request to GitHub’s latest-release endpoint. Available updates open the release page after confirmation; this implementation does **not** silently download or replace running binaries. Signed automatic installation needs a future release/signing setup. Mac Sparkle updates are not reused.

Compilation, core tests, package creation, and Windows runtime verification are distinct. See [VERIFICATION.md](VERIFICATION.md) for checks actually run and the remaining native Windows checklist.

[MIT License](LICENSE) · [Third-party notices](THIRD_PARTY_NOTICES.md)
# Clipboard_windows
