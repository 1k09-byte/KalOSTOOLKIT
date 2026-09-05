# collect-nuget-licenses.ps1
#
# Copies the license file of every NuGet package referenced by a project into
#   $(OutDir)\Licenses\<PackageId>.<Version>\
#
# MIT and MPL redistribution require the license notice to accompany the
# distributed copies, so the release output (which is zipped verbatim by
# publish-consumer.ps1) must carry the actual license texts, not just links.
#
# License files are looked up in the package's own folder inside the NuGet
# global-packages cache (and the fallback folder); if the folder carries no
# license text the .nupkg archive is inspected for a top-level license file.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File collect-nuget-licenses.ps1 `
#       -AssetsFile <path-to-project.assets.json> `
#       -OutDir <path-to-output> `
#       -PackageFolders "C:\Users\me\.nuget\packages;C:\...\NuGetFallbackFolder"

param(
    [Parameter(Mandatory = $true)][string]$AssetsFile,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [Parameter(Mandatory = $true)][string]$PackageFolders
)

$ErrorActionPreference = "SilentlyContinue"

$roots = $PackageFolders -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ -and (Test-Path $_) }
if (-not $roots) { exit 0 }
if (-not (Test-Path $AssetsFile)) { exit 0 }

# The MSBuild Exec appends a space inside the quotes to keep a trailing
# backslash from escaping the closing quote (see Licenses.targets).
$destRoot = Join-Path ($OutDir.Trim()) "Licenses"
New-Item -ItemType Directory -Path $destRoot -Force | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Find-PackageDir {
    param([string]$Id, [string]$Version, [string[]]$Roots)
    # NuGet's global cache lowercases the package-id folder.
    foreach ($root in $Roots) {
        foreach ($name in @($Id.ToLowerInvariant(), $Id)) {
            $dir = Join-Path $root (Join-Path $name $Version)
            if (Test-Path $dir) { return $dir }
        }
    }
    return $null
}

function Get-LicenseFile {
    param([string]$PackageDir)
    if (-not (Test-Path $PackageDir)) { return @() }
    # Top-level files whose name looks like a license/EULA/copying/notice text
    # (also Microsoft's 'THIRD-PARTY-NOTICES.TXT' convention).
    $matches = Get-ChildItem -Path $PackageDir -File | Where-Object {
        $_.Name -match '(?i)^(license|licence|copying|eula|copyright|notice|notices|third-party)' -and
        $_.Extension -notin @('.nupkg', '.snupkg', '.nuspec', '.xml')
    }
    return @($matches)
}

function Get-LicenseFromNupkg {
    param([string]$PackageDir, [string]$Id, [string]$Version)
    $nupkg = Join-Path $PackageDir "$Id.$Version.nupkg"
    if (-not (Test-Path $nupkg)) { return $null }
    try {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
        try {
            $entry = $zip.Entries | Where-Object {
                $_.FullName -notmatch '/' -and
                $_.Name -match '(?i)^(license|licence|copying|eula|copyright|notice|notices|third-party)'
            } | Select-Object -First 1
            if ($entry -eq $null) { return $null }
            $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("kk-lic-" + [guid]::NewGuid().ToString("N") + "-" + $entry.Name)
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $tmp, $true)
            return $tmp
        }
        finally { $zip.Dispose() }
    }
    catch { return $null }
}

$assets = Get-Content -Raw -Path $AssetsFile | ConvertFrom-Json
$copied = 0
$skipped = @()

foreach ($prop in $assets.libraries.PSObject.Properties) {
    $lib = $prop.Value
    if ($lib.type -ne 'package') { continue }
    $parts = $prop.Name -split '/', 2
    if ($parts.Count -ne 2) { continue }
    $id = $parts[0]
    $ver = $parts[1]

    $pkgDir = Find-PackageDir -Id $id -Version $ver -Roots $roots
    if (-not $pkgDir) { continue }

    $licenseFiles = @(Get-LicenseFile -PackageDir $pkgDir)
    $fromNupkg = $false
    if ($licenseFiles.Count -eq 0) {
        $tmp = Get-LicenseFromNupkg -PackageDir $pkgDir -Id $id -Version $ver
        if ($tmp) { $licenseFiles = @($tmp); $fromNupkg = $true }
    }
    if ($licenseFiles.Count -eq 0) {
        $skipped += "$id/$ver"
        continue
    }

    $destDir = Join-Path $destRoot "$id.$ver"
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    foreach ($file in $licenseFiles) {
        $dest = Join-Path $destDir $file.Name
        Copy-Item -Path $file.FullName -Destination $dest -Force
        $copied++
    }
    if ($fromNupkg) {
        Remove-Item -Path $licenseFiles[0].FullName -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "CollectNuGetLicenses: copied $copied license/notice file(s) into $(Join-Path $destRoot '')"
if ($skipped.Count -gt 0) {
    Write-Host "CollectNuGetLicenses: no license text in package archive for: $($skipped -join ', ')"
}
