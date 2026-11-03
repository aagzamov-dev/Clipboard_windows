$ErrorActionPreference = 'Stop'
$installation = Join-Path $env:LOCALAPPDATA 'Programs/Clipboard'
$executable = Join-Path $installation 'Clipboard.exe'
if ([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') -ne [IO.Path]::GetFullPath($installation).TrimEnd('\')) { throw 'Run this script from the installed Clipboard folder.' }
$running = Get-Process Clipboard -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable }
if ($running) { throw 'Quit Clipboard from its tray menu before uninstalling.' }
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startup = Get-ItemProperty -Path $runKey -Name 'Clipboard.Windows' -ErrorAction SilentlyContinue
if ($startup -and $startup.'Clipboard.Windows' -eq ('"' + $executable + '" --background')) { Remove-ItemProperty -Path $runKey -Name 'Clipboard.Windows' }
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Clipboard (Windows).lnk'
if (Test-Path $shortcutPath) {
    $shell = New-Object -ComObject WScript.Shell
    if ($shell.CreateShortcut($shortcutPath).TargetPath -eq $executable) { Remove-Item $shortcutPath }
}
Set-Location $env:TEMP
Remove-Item $installation -Recurse -Force
Write-Output 'Clipboard was uninstalled. History and preferences remain in %LOCALAPPDATA%\Clipboard.'
