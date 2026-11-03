param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$UpdateRepository = ''
)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    & ./Scripts/check.ps1
    $output = Join-Path $PWD ".build/package-$Runtime-$([Guid]::NewGuid().ToString('N'))"
    dotnet publish src/Clipboard.Windows -c Release -r $Runtime --self-contained true -o $output "-p:UpdateRepository=$UpdateRepository"
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $assets = Get-Content src/Clipboard.Windows/obj/project.assets.json -Raw | ConvertFrom-Json -AsHashtable
    $framework = $assets.project.frameworks.Values | Select-Object -First 1
    $runtimePacks = @($framework.downloadDependencies | Where-Object { $_.name -match '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.' })
    if ($runtimePacks.Count -lt 2) { throw 'Runtime license sources could not be determined.' }
    foreach ($pack in $runtimePacks) {
        $version = [regex]::Match($pack.version, '\d+\.\d+\.\d+').Value
        $packDirectory = Join-Path $assets.project.restore.packagesPath ($pack.name.ToLowerInvariant() + '/' + $version)
        $licenseDirectory = Join-Path $output ('Licenses/' + $pack.name)
        New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
        $notices = @(Get-ChildItem $packDirectory -File | Where-Object { $_.Name -match 'LICENSE|NOTICE' })
        if ($notices.Count -eq 0) { throw "Missing runtime licenses for $($pack.name)" }
        $notices | Copy-Item -Destination $licenseDirectory
    }
    Copy-Item LICENSE, THIRD_PARTY_NOTICES.md, APACHE-2.0.txt, README.md, VERIFICATION.md -Destination $output
    Copy-Item Scripts/Install.ps1, Scripts/Uninstall.ps1 -Destination $output
    $files = Get-ChildItem $output -File -Recurse | Where-Object { $_.Name -ne 'Clipboard.package.json' } | ForEach-Object {
        @{ Path = [IO.Path]::GetRelativePath($output, $_.FullName); SHA256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    }
    @{ Product = 'Clipboard.Windows'; Runtime = $Runtime; Files = @($files) } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $output 'Clipboard.package.json') -Encoding utf8
    $archive = Join-Path $PWD ".build/Clipboard-Windows-$Runtime.zip"
    Compress-Archive -Path "$output/*" -DestinationPath $archive -Force
    $hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content "$archive.sha256" -Encoding ascii
    Write-Output "Prepared $archive (self-contained; no system .NET runtime required)."
} finally {
    if ($output -and (Test-Path $output)) { Remove-Item $output -Recurse -Force }
    Pop-Location
}
