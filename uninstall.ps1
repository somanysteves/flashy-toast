<#
.SYNOPSIS
  Removes the flashy-toast scheduled task and installed exe.

.DESCRIPTION
  Unregisters the scheduled task, stops the running process, and deletes
  %LOCALAPPDATA%\Programs\flashy-toast\.

  Leaves the log directory (%LOCALAPPDATA%\flashy-toast\) intact — pass
  -PurgeLogs to remove that too.
#>

#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$TaskName = 'flashy-toast',
    [switch]$PurgeLogs
)

$ErrorActionPreference = 'Stop'

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Write-Host "==> Unregistering task '$TaskName'" -ForegroundColor Cyan
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

Get-Process -Name 'flashy-toast' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "==> Stopping pid $($_.Id)" -ForegroundColor Cyan
    Stop-Process -Id $_.Id -Force
}

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\flashy-toast'
if (Test-Path $installDir) {
    Write-Host "==> Removing $installDir" -ForegroundColor Cyan
    Remove-Item $installDir -Recurse -Force
}

if ($PurgeLogs) {
    $logDir = Join-Path $env:LOCALAPPDATA 'flashy-toast'
    if (Test-Path $logDir) {
        Write-Host "==> Removing $logDir" -ForegroundColor Cyan
        Remove-Item $logDir -Recurse -Force
    }
}

Write-Host "Done." -ForegroundColor Green
