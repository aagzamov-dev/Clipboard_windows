$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet run --project tests/Clipboard.Core.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
    dotnet build Clipboard.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
} finally { Pop-Location }
