$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$destination = Join-Path $env:LOCALAPPDATA 'Programs/Clipboard'
$executable = Join-Path $destination 'Clipboard.exe'
if ($source -eq [IO.Path]::GetFullPath($destination).TrimEnd('\')) { throw 'This package is already installed here.' }
$manifestPath = Join-Path $source 'Clipboard.package.json'
if (!(Test-Path $manifestPath)) { throw 'Extract a complete published Clipboard package before installing.' }
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($manifest.Product -ne 'Clipboard.Windows' -or $manifest.Files.Count -lt 2) { throw 'Invalid package manifest.' }
if (Test-Path $destination) {
    $existingManifest = Join-Path $destination 'Clipboard.package.json'
    if (!(Test-Path $existingManifest) -or (Get-Content $existingManifest -Raw | ConvertFrom-Json).Product -ne 'Clipboard.Windows') { throw 'The destination is not a recognized Clipboard installation. It was preserved.' }
}
$running = Get-Process Clipboard -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable }
if ($running) { throw 'Quit the installed Clipboard from its tray menu, then try again. History will be preserved.' }
$stage = "$destination-stage-$([Guid]::NewGuid().ToString('N'))"
$old = "$destination-previous-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    foreach ($file in $manifest.Files) {
        $path = [IO.Path]::GetFullPath((Join-Path $source $file.Path))
        if (!$path.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path $path -PathType Leaf)) { throw 'Invalid package file path.' }
        if ((Get-FileHash $path -Algorithm SHA256).Hash -ne $file.SHA256) { throw "Package checksum failed: $($file.Path)" }
        $target = [IO.Path]::GetFullPath((Join-Path $stage $file.Path))
        if (!$target.StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid installation path.' }
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $path -Destination $target
    }
    if (!(Test-Path (Join-Path $stage 'Clipboard.exe'))) { throw 'The package is missing Clipboard.exe.' }
    Copy-Item -LiteralPath $manifestPath -Destination $stage
    if (Test-Path $destination) { Move-Item $destination $old }
    try { Move-Item $stage $destination }
    catch { if (Test-Path $old) { Move-Item $old $destination }; throw }
    $shell = New-Object -ComObject WScript.Shell
    $shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Clipboard (Windows).lnk'
    $shortcut = $shell.CreateShortcut($shortcutPath)
    if (!(Test-Path $shortcutPath) -or $shortcut.TargetPath -eq $executable) {
        $shortcut.TargetPath = $executable; $shortcut.WorkingDirectory = $destination; $shortcut.Save()
    } else { Write-Warning 'An unrelated Start shortcut was preserved.' }
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $startup = Get-ItemProperty -Path $runKey -Name 'Clipboard.Windows' -ErrorAction SilentlyContinue
    $owned = !$startup -or $startup.'Clipboard.Windows' -eq ('"' + $executable + '" --background')
    $settingsPath = Join-Path $env:LOCALAPPDATA 'Clipboard/settings.json'
    $login = $false
    if (Test-Path $settingsPath) {
        try { $login = (Get-Content $settingsPath -Raw | ConvertFrom-Json).StartAtLogin -eq $true }
        catch { Write-Warning 'Unreadable preferences preserved; login registration was unchanged.'; $owned = $false }
    }
    if ($owned) {
        if ($login) { if (!(Test-Path -LiteralPath $runKey)) { New-Item -Path $runKey | Out-Null }; Set-ItemProperty -Path $runKey -Name 'Clipboard.Windows' -Value ('"' + $executable + '" --background') }
        elseif ($startup) { Remove-ItemProperty -Path $runKey -Name 'Clipboard.Windows' }
    }
    Write-Output "Installed to $destination. Launch Clipboard from Start. History and preferences were preserved."
} finally {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    if (Test-Path $old) { Write-Output "Previous binaries retained for recovery at $old" }
}
