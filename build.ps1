<#
.SYNOPSIS
  Builds and signs a flashy-toast .msix package.

.DESCRIPTION
  - Publishes the single-file self-contained Win64 exe.
  - Generates a self-signed code-signing cert if one doesn't already exist
    in CurrentUser\My matching CertSubject; exports the .cer alongside the
    .msix for end-user trust import.
  - Stages exe + AppxManifest + Assets into dist/staging.
  - MakeAppx packs the staging dir into dist/flashy-toast.msix.
  - signtool signs the .msix with the cert.

.PARAMETER Version
  Override the package version. Format: w.x.y.z (four integers). When
  omitted, the version baked into Package.appxmanifest is used as-is.

.PARAMETER Sign
  Whether to sign the .msix. Default $true. CI passes -Sign:$false on PR
  builds where no signing cert is available.

.PARAMETER CertSubject
  Subject for the self-signed cert. Must match the Publisher attribute in
  Package.appxmanifest exactly.
#>

#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Version,
    [bool]$Sign = $true,
    [string]$CertSubject = "CN=flashy-toast",
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$repoRoot    = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcProj     = Join-Path $repoRoot 'src/FlashyToast/FlashyToast.csproj'
$srcDir      = Join-Path $repoRoot 'src/FlashyToast'
$manifestSrc = Join-Path $srcDir 'Package.appxmanifest'
$assetsSrc   = Join-Path $srcDir 'Assets'
$publishDir  = Join-Path $srcDir "bin/$Configuration/net8.0-windows10.0.19041.0/$Runtime/publish"
$distDir     = Join-Path $repoRoot 'dist'
$stageDir    = Join-Path $distDir 'staging'
$msixPath    = Join-Path $distDir 'flashy-toast.msix'
$cerPath     = Join-Path $distDir 'flashy-toast.cer'

function Find-SdkTool {
    param([string]$ToolName)
    $base = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (-not (Test-Path $base)) { throw "Windows SDK not found at $base. Install the Windows 10/11 SDK." }
    $versions = Get-ChildItem $base -Directory | Where-Object Name -Match '^\d+\.\d+\.\d+\.\d+$' | Sort-Object Name -Descending
    foreach ($v in $versions) {
        $candidate = Join-Path $v.FullName "x64\$ToolName"
        if (Test-Path $candidate) { return $candidate }
    }
    throw "Could not find $ToolName under $base."
}

function Update-ManifestVersion {
    param([string]$ManifestPath, [string]$NewVersion)
    if (-not $NewVersion) { return }
    [xml]$xml = Get-Content $ManifestPath
    $xml.Package.Identity.Version = $NewVersion
    $xml.Save($ManifestPath)
    Write-Host "Set manifest version to $NewVersion"
}

function Get-OrCreate-SigningCert {
    param([string]$Subject)
    $existing = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
    if ($existing) {
        Write-Host "Using existing cert: $($existing.Thumbprint) (expires $($existing.NotAfter.ToString('yyyy-MM-dd')))"
        return $existing
    }
    Write-Host "Creating self-signed code-signing cert with subject $Subject..."
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $Subject `
        -KeyUsage DigitalSignature `
        -FriendlyName "flashy-toast self-signed" `
        -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
        -NotAfter (Get-Date).AddYears(3)
    Write-Host "Created cert: $($cert.Thumbprint)"
    return $cert
}

# ---- Build ----

Write-Host "==> Cleaning dist/" -ForegroundColor Cyan
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -ItemType Directory -Path $distDir, $stageDir | Out-Null

if ($Version) {
    Write-Host "==> Updating manifest version" -ForegroundColor Cyan
    Update-ManifestVersion -ManifestPath $manifestSrc -NewVersion $Version
}

Write-Host "==> dotnet publish" -ForegroundColor Cyan
& dotnet publish $srcProj -c $Configuration -r $Runtime -p:PublishSingleFile=true --self-contained true | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

$exePath = Join-Path $publishDir 'flashy-toast.exe'
if (-not (Test-Path $exePath)) { throw "Published exe not found at $exePath" }

Write-Host "==> Staging" -ForegroundColor Cyan
Copy-Item $exePath $stageDir
# AppxManifest.xml (not Package.appxmanifest) is the file name MakeAppx expects
# inside the package root.
Copy-Item $manifestSrc (Join-Path $stageDir 'AppxManifest.xml')
Copy-Item $assetsSrc (Join-Path $stageDir 'Assets') -Recurse

Write-Host "==> MakeAppx pack" -ForegroundColor Cyan
$makeappx = Find-SdkTool -ToolName 'makeappx.exe'
& $makeappx pack /d $stageDir /p $msixPath /o | Out-Host
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed (exit $LASTEXITCODE)" }

if ($Sign) {
    Write-Host "==> Signing" -ForegroundColor Cyan
    $cert = Get-OrCreate-SigningCert -Subject $CertSubject
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
    Write-Host "Exported cert (public): $cerPath"

    $signtool = Find-SdkTool -ToolName 'signtool.exe'
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /a $msixPath | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed (exit $LASTEXITCODE)" }
} else {
    Write-Host "==> Skipping signing (-Sign:`$false)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  msix: $msixPath"
if ($Sign) { Write-Host "  cer:  $cerPath" }

# Emit paths for CI consumption.
if ($env:GITHUB_OUTPUT) {
    "msix=$msixPath" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    if ($Sign) { "cer=$cerPath" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
}
