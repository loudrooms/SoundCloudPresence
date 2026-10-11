#Requires -Version 5.1
<#
    Builds SoundCloud Presence: compiles the GUI with the .NET Framework
    csc that ships with Windows, assembles dist\, renders the README
    screenshots, zips the result.
      .\build.ps1
      .\build.ps1 -SkipScreenshot
#>

param(
    [string]$Version = '2.0.0',
    [switch]$SkipScreenshot
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$csc = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw 'csc.exe not found. This build needs the .NET Framework 4.x SDK that ships with Windows.' }

$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item (Join-Path $dist '*') -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Write-Host 'Compiling SoundCloudPresence.exe ...'
& $csc /nologo /target:winexe `
    /out:"$dist\SoundCloudPresence.exe" `
    /reference:System.dll /reference:System.Windows.Forms.dll `
    /reference:System.Drawing.dll /reference:Microsoft.CSharp.dll `
    (Join-Path $root 'src\SoundCloudPresence.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }

Copy-Item (Join-Path $root 'src\SoundCloudPresence.ps1') $dist -Force
Copy-Item (Join-Path $root 'config.txt')                 $dist -Force
Copy-Item (Join-Path $root 'README.md')                  $dist -Force
if (Test-Path (Join-Path $root 'LICENSE')) { Copy-Item (Join-Path $root 'LICENSE') $dist -Force }

if (-not $SkipScreenshot) {
    $assets = Join-Path $root 'assets'
    New-Item -ItemType Directory -Force -Path $assets | Out-Null
    $shot = Join-Path $assets 'screenshot.png'
    Write-Host 'Rendering screenshot ...'
    Start-Process -FilePath "$dist\SoundCloudPresence.exe" -ArgumentList '--shot', $shot -Wait
    Start-Process -FilePath "$dist\SoundCloudPresence.exe" -ArgumentList '--shot-settings', (Join-Path $assets 'settings.png') -Wait
    # copy images into dist so the README inside the zip renders
    New-Item -ItemType Directory -Force -Path (Join-Path $dist 'assets') | Out-Null
    Copy-Item $shot (Join-Path $dist 'assets\screenshot.png') -Force
    Copy-Item (Join-Path $assets 'settings.png') (Join-Path $dist 'assets\settings.png') -Force
}

$zip = Join-Path $root "SoundCloudPresence-$Version-windows.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zip
Write-Host ''
Write-Host "Done."
Write-Host "  app      : $dist\SoundCloudPresence.exe"
Write-Host "  release  : $zip"
