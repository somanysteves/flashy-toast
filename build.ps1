<#
.SYNOPSIS
  Builds the flashy-toast single-file exe.

.DESCRIPTION
  Publishes a self-contained, single-file Win64 exe to dist/flashy-toast.exe.
  The exe embeds requireAdministrator in its application manifest, so a
  double-click triggers a UAC prompt and runs elevated.

  For auto-start at logon, run install.ps1 after building.
#>

#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcProj    = Join-Path $repoRoot 'src/FlashyToast/FlashyToast.csproj'
$publishDir = Join-Path $repoRoot "src/FlashyToast/bin/$Configuration/net8.0-windows10.0.19041.0/$Runtime/publish"
$distDir    = Join-Path $repoRoot 'dist'
$distExe    = Join-Path $distDir 'flashy-toast.exe'

Write-Host "==> Cleaning dist/" -ForegroundColor Cyan
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -ItemType Directory -Path $distDir | Out-Null

Write-Host "==> dotnet publish" -ForegroundColor Cyan
& dotnet publish $srcProj -c $Configuration -r $Runtime -p:PublishSingleFile=true --self-contained true | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

$exePath = Join-Path $publishDir 'flashy-toast.exe'
if (-not (Test-Path $exePath)) { throw "Published exe not found at $exePath" }

Copy-Item $exePath $distExe -Force

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  exe: $distExe"

if ($env:GITHUB_OUTPUT) {
    "exe=$distExe" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
