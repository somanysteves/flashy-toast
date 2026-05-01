#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project  = Join-Path $repoRoot 'src/FlashyToast/FlashyToast.csproj'

dotnet publish $project -c $Configuration -r $Runtime -p:PublishSingleFile=true --self-contained true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

$exe = Join-Path $repoRoot "src/FlashyToast/bin/$Configuration/net8.0-windows10.0.19041.0/$Runtime/publish/flashy-toast.exe"
if (-not (Test-Path $exe)) { throw "publish output missing: $exe" }

Write-Host "Built: $exe"

# Emit path for CI consumption (e.g. $env:GITHUB_OUTPUT).
if ($env:GITHUB_OUTPUT) {
    "exe=$exe" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
