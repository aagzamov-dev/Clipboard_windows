# Verification — 2026-11-03

This app was developed and cross-compiled on an Apple Silicon Mac with .NET SDK 10.0.403 and PowerShell 7.6.9. No Windows VM, remote Windows session, or Wine was available. Native Windows runtime behavior is not claimed as tested.

## Completed checks

- Release solution compilation with warnings treated as errors: passed, zero warnings/errors.
- Core regression suite: 76 checks passed. Covers settings validation/atomic writes, text/PNG/file storage, duplicate rules, indexed Unicode search, date/type/pin filters, stable capture cutoff, editing/search consistency, deletion/undo, retention and draft protection, clear-history behavior, draft budgets, per-monitor layout persistence, and gate-serialized shutdown.
- Backup checks: encrypted/plain round trips, wrong passwords, modified ciphertext, truncated final records, archive traversal, malicious SQLite views, conflicting identities, rollback of earlier rows on a later conflict, idempotent merge, and absence of deleted plaintext in raw exported database pages. Export rejects live database destinations.
- Placement calculations: mouse positions at both corners of monitors left of/above primary, small work areas, and 100%, 150%, 200%, 300% DPI.
- Performance on this Mac: 10,000 durable small-text inserts approximately 1.2 seconds; indexed final-entry lookup approximately 0.9 ms. This is a core storage benchmark, not Windows UI latency, memory, or clipboard throughput measurement.
- PowerShell parser: all four build/install/uninstall scripts passed syntax checks. The complete packaging workflow ran on macOS using PowerShell, producing self-contained x64 and ARM64 ZIPs.
- Package inspection: ZIP integrity, every manifest file hash, complete package inventory, architecture of apphost/native SQLite, required notices, and SHA-256 sidecars checked.
- Astra static review: findings addressed in paste cancellation/target validation, asynchronous selection and draft saves, backup privacy/import validation, STA write ordering, database shutdown, and installer preservation of unrelated files/startup entries. Static review is not a substitute for a Windows runtime check.
- The sibling macOS repository remains unchanged and clean. No Git remote was configured for Windows; nothing was committed, pushed, or released.

## Windows 11 runtime checklist

Run these on a disposable Windows test account before publishing a release:

1. First run requests local-history consent; starting/resuming does not capture the previous clipboard. Restart remembers consent and preferences. A second instance cannot duplicate capture/shortcuts.
2. Copy empty/Unicode/large text, RTF, PNG/bitmap screenshots, multiple files, and directories from Notepad, browsers, Office, Explorer and remote-desktop applications. Test busy/delayed clipboard providers and size-limit notices. Excluded apps and supported confidential markers must not be captured.
3. Open/close rapidly by shortcut/tray. Test shortcut conflicts/replacement, normal key editing, list multiselection, selection changes during slow reads, text-save while continuing to edit, combined drafts, undo deletion, clear while drafts exist, and quit/cancel with drafts.
4. Open beside the mouse on every monitor, including monitors left/above primary, mismatched DPI, taskbars on each edge, display removal/reconnection, and list/preview resizing. Check keyboard focus, disabled animations, high contrast, and light/dark appearance.
5. Paste once into an editor, browser and Explorer. Test released modifiers, target closure, focus changes, rapid reopens/cancellation, concurrent clipboard owners, UAC-elevated apps and secure desktops. Blocked paste must not redirect to another app or repeatedly inject input.
6. Test pause durations, lock/unlock, resume, exclusions, retention/count limits, daily/weekly schedule across restarts, pinned items and draft-protected entries.
7. Import/export real text and image histories, encrypted and plain backups. Test an incorrect password, full disk, denied access, corrupted databases, cancellation of clear, and unavailable original file references.
8. Extract each architecture’s full ZIP; run directly on the matching Windows architecture without .NET installed. Run Install.ps1, enable launch at login, update the installed copy, and uninstall. Existing history, preferences, other apps’ startup registrations, unrelated shortcuts/files must remain intact.
9. If release checks are configured, test no update, new version, no network, rate limits and invalid tags. Updates open the configured Windows release page; unattended installation and signing are not configured.

## Release prerequisites

Windows runtime verification, a dedicated Windows release repository, and Windows code-signing configuration remain outside the validated development environment. The default builds do not contact an update server. Signed automatic installation is not implemented; packages are unsigned development builds with checksums.
