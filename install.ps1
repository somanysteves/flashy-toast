<#
.SYNOPSIS
  Installs flashy-toast for the current user with auto-start at logon.

.DESCRIPTION
  Copies dist/flashy-toast.exe to %LOCALAPPDATA%\Programs\flashy-toast\ and
  registers a Scheduled Task that runs the exe at logon with highest
  privileges (auto-elevated, no UAC prompt at logon).

  Re-running this script is safe — it overwrites the installed exe and
  re-registers the task.

  Requires an elevated PowerShell (Register-ScheduledTask with -RunLevel
  Highest needs admin).

.PARAMETER Source
  Path to the built exe. Defaults to dist/flashy-toast.exe next to this script.

.PARAMETER TaskName
  Name of the scheduled task. Defaults to 'flashy-toast'.
#>

#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot 'dist/flashy-toast.exe'),
    [string]$TaskName = 'flashy-toast'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Source)) {
    throw "flashy-toast.exe not found at $Source. Run build.ps1 first."
}

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\flashy-toast'
$installExe = Join-Path $installDir 'flashy-toast.exe'

Write-Host "==> Stopping any running instance" -ForegroundColor Cyan
Get-Process -Name 'flashy-toast' -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "==> Installing to $installDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item $Source $installExe -Force

Write-Host "==> Registering scheduled task '$TaskName'" -ForegroundColor Cyan
$action    = New-ScheduledTaskAction -Execute $installExe
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -StartWhenAvailable
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  installed: $installExe"
Write-Host "  task:      $TaskName (runs at logon as $env:USERNAME, elevated)"
Write-Host ""
Write-Host "To start now without rebooting:" -ForegroundColor Yellow
Write-Host "  Start-ScheduledTask -TaskName $TaskName" -ForegroundColor Yellow
