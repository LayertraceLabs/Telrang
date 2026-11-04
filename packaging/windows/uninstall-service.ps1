#Requires -RunAsAdministrator
<#
.SYNOPSIS
Removes the Telrang Windows service (DESIGN.md section 29; docs/deploy/windows.md).

.DESCRIPTION
Stops and deletes the service "Telrang" and the firewall rule install-service.ps1 may have added. Keeps the
configuration and the data (C:\ProgramData\Telrang) and the Event Log source; -RemoveProgram also deletes the
program folder. To remove the data too, afterwards: Remove-Item -Recurse -Force C:\ProgramData\Telrang

.EXAMPLE
.\uninstall-service.ps1 -RemoveProgram
#>
[CmdletBinding()]
param(
    [switch]$RemoveProgram,
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'Telrang')
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

$Name = 'Telrang'
$service = Get-Service -Name $Name -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Write-Host "Stopping $Name"
        Stop-Service -Name $Name
        $service.WaitForStatus('Stopped', [TimeSpan]::FromMinutes(1))
    }
    & sc.exe delete $Name | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete $Name failed with exit code $LASTEXITCODE" }
    Write-Host "Removed the service $Name"
}
else {
    Write-Host "No service $Name"
}

Get-NetFirewallRule -Name 'Telrang-TCP' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

if ($RemoveProgram) {
    $InstallDir = [IO.Path]::GetFullPath($InstallDir)
    if (Test-Path -LiteralPath (Join-Path $InstallDir 'Telrang.Web.exe')) {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force
        Write-Host "Removed $InstallDir"
    }
    elseif (Test-Path -LiteralPath $InstallDir) {
        throw "$InstallDir holds no Telrang.Web.exe; not removed."
    }
}

Write-Host "Kept configuration and data: $(Join-Path $env:ProgramData 'Telrang')"
