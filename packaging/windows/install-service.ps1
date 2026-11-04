#Requires -RunAsAdministrator
<#
.SYNOPSIS
Installs or upgrades Telrang as a Windows service (DESIGN.md section 29; docs/deploy/windows.md).

.DESCRIPTION
Run from the extracted release folder in an elevated PowerShell (Windows PowerShell 5.1 or PowerShell 7):
  - copies the program to -InstallDir (default C:\Program Files\Telrang), replacing an earlier version;
  - creates the service "Telrang" under the virtual account NT SERVICE\Telrang, start Automatic (Delayed),
    restarts after a failure, with ASPNETCORE_ENVIRONMENT=Production and DOTNET_EnableDiagnostics=0;
  - creates C:\ProgramData\Telrang (configuration, the service may read) and C:\ProgramData\Telrang\data (database and
    key ring, the service may change), both for SYSTEM, Administrators and the service account only. Folders that exist
    already (made by another account before the install, pentest R3-DEP-4) are taken over: owner Administrators, and the
    ACL of the folder and everything in it reset to the above;
  - puts the example telrang.json there unless one exists. An existing telrang.json that Administrators or SYSTEM do
    not own was not written by this script or an administrator: it is refused unless -KeepConfig is given (it could name
    another data folder);
  - refuses an -InstallDir that other accounts may write to (they could replace the program, pentest R3-DEP-11); a new
    one is created for SYSTEM and Administrators (full) and Users (read and run) only;
  - on an upgrade, backs up the data folder to C:\ProgramData\Telrang\backups (SYSTEM and Administrators only) first;
  - registers the Event Log source "Telrang", optionally opens the port in Windows Firewall (-OpenFirewall), then
    starts the service.
Running it again from a newer release folder upgrades: the service is stopped, the data backed up, the program
replaced, the service started; configuration and data are left as they are.

.EXAMPLE
.\install-service.ps1
.EXAMPLE
.\install-service.ps1 -OpenFirewall -Port 8443
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'Telrang'),
    [switch]$OpenFirewall,
    [ValidateRange(1, 65535)][int]$Port = 8080,
    [switch]$NoStart,
    [switch]$KeepConfig
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

$Name = 'Telrang'
$Account = "NT SERVICE\$Name"
$Source = $PSScriptRoot
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$Exe = Join-Path $InstallDir 'Telrang.Web.exe'
$ConfigDir = Join-Path $env:ProgramData 'Telrang'
$DataDir = Join-Path $ConfigDir 'data'
$BackupDir = Join-Path $ConfigDir 'backups'
$System = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
$Admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
$Users = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-545')
$TrustedInstaller = 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
$CreatorOwner = 'S-1-3-0'

function Invoke-Sc {
    & sc.exe @args | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

# True when SYSTEM, Administrators or TrustedInstaller owns the path (an owner may always change the ACL).
function Test-AdminOwned([string]$Path) {
    $owner = (Get-Acl -LiteralPath $Path).GetOwner([System.Security.Principal.SecurityIdentifier]).Value
    return @($System.Value, $Admins.Value, $TrustedInstaller) -contains $owner
}

# The identities other than SYSTEM, Administrators and TrustedInstaller that may write to, delete in or re-permission
# a folder (CREATOR OWNER rules apply to children only and are judged by their owner). Empty = admin-only.
function Get-OtherWriters([string]$Path) {
    $write = [System.Security.AccessControl.FileSystemRights]'WriteData, AppendData, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership, WriteAttributes, WriteExtendedAttributes'
    $others = @()
    foreach ($rule in (Get-Acl -LiteralPath $Path).GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne 'Allow' -or ($rule.FileSystemRights -band $write) -eq 0) { continue }
        $sid = $rule.IdentityReference.Value
        if (@($System.Value, $Admins.Value, $TrustedInstaller, $CreatorOwner) -contains $sid) { continue }
        $others += $sid
    }
    return $others
}

# Owner Administrators, for this folder and everything in it (takeown /A), so an account that created it before the
# install keeps no right to reopen it (pentest R3-DEP-4).
function Set-AdminOwner([string]$Path) {
    & takeown.exe /F $Path /A /R /D Y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "takeown.exe $Path failed with exit code $LASTEXITCODE" }
}

# Only SYSTEM, Administrators and the service account; nothing inherited from C:\ProgramData (where Users may read).
# Children lose their own entries and inherit this ACL.
function Set-PrivateAcl([string]$Path, [System.Security.AccessControl.FileSystemRights]$ServiceRights) {
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetOwner($Admins)
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $none = [System.Security.AccessControl.PropagationFlags]::None
    $full = [System.Security.AccessControl.FileSystemRights]::FullControl
    $allow = [System.Security.AccessControl.AccessControlType]::Allow
    $rules = @(
        (New-Object System.Security.AccessControl.FileSystemAccessRule($System, $full, $inherit, $none, $allow)),
        (New-Object System.Security.AccessControl.FileSystemAccessRule($Admins, $full, $inherit, $none, $allow)))
    if ($ServiceRights -ne 0) {
        $service = New-Object System.Security.Principal.NTAccount($Account)
        $rules += New-Object System.Security.AccessControl.FileSystemAccessRule($service, $ServiceRights, $inherit, $none, $allow)
    }
    foreach ($rule in $rules) { $acl.AddAccessRule($rule) }
    Set-Acl -LiteralPath $Path -AclObject $acl
    if (@(Get-ChildItem -LiteralPath $Path -Force).Count -gt 0) {
        & icacls.exe (Join-Path $Path '*') /reset /T /C /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "icacls.exe /reset on $Path failed with exit code $LASTEXITCODE" }
    }
}

# A folder only SYSTEM and Administrators may change, others may read and run (a custom -InstallDir, pentest R3-DEP-11).
function Set-ProgramAcl([string]$Path) {
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetOwner($Admins)
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $none = [System.Security.AccessControl.PropagationFlags]::None
    $allow = [System.Security.AccessControl.AccessControlType]::Allow
    $full = [System.Security.AccessControl.FileSystemRights]::FullControl
    $read = [System.Security.AccessControl.FileSystemRights]::ReadAndExecute
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($System, $full, $inherit, $none, $allow)))
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($Admins, $full, $inherit, $none, $allow)))
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($Users, $read, $inherit, $none, $allow)))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

if (-not (Test-Path -LiteralPath (Join-Path $Source 'Telrang.Web.exe'))) {
    throw "Telrang.Web.exe is not next to this script ($Source). Run it from the extracted release folder."
}

# ---- checks before anything changes ----
$configFile = Join-Path $ConfigDir 'telrang.json'
if ((Test-Path -LiteralPath $configFile) -and -not (Test-AdminOwned $configFile) -and -not $KeepConfig) {
    throw "$configFile exists and is not owned by Administrators or SYSTEM: another account wrote it, and it could point the " +
        "service at its own data folder. Read it, then remove it, or run again with -KeepConfig to use it as it is."
}
if ((Test-Path -LiteralPath $InstallDir) -and [IO.Path]::GetFullPath($Source).TrimEnd('\') -ne $InstallDir.TrimEnd('\')) {
    $writers = @(Get-OtherWriters $InstallDir)
    if (-not (Test-AdminOwned $InstallDir) -or $writers.Count -gt 0) {
        throw "$InstallDir may be changed by accounts other than SYSTEM and Administrators ($($writers -join ', ')): they " +
            "could replace the program the service runs. Choose a folder only Administrators may change, or a new one."
    }
}

# ---- stop a running version ----
$existing = Get-Service -Name $Name -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Host "Stopping $Name"
    Stop-Service -Name $Name
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromMinutes(1))
}

# ---- program ----
if ([IO.Path]::GetFullPath($Source).TrimEnd('\') -ne $InstallDir.TrimEnd('\')) {
    if (Test-Path -LiteralPath $InstallDir) {
        # Replace the whole folder (files of an older version could still be loaded), but never a folder that is not Telrang.
        $content = @(Get-ChildItem -LiteralPath $InstallDir -Force)
        if ($content.Count -gt 0 -and -not (Test-Path -LiteralPath $Exe)) {
            throw "$InstallDir is not empty and holds no Telrang.Web.exe. Choose another -InstallDir."
        }
        $content | Remove-Item -Recurse -Force
    }
    else {
        # A new folder inherits its parent's ACL (under C:\ Authenticated Users may modify): replaced at once.
        New-Item -ItemType Directory -Path $InstallDir | Out-Null
        Set-ProgramAcl $InstallDir
    }
    Write-Host "Copying the program to $InstallDir"
    Copy-Item -Path (Join-Path $Source '*') -Destination $InstallDir -Recurse -Force
}

# ---- service ----
$quotedExe = '"' + $Exe + '"' # quoted: a path with spaces must never be read as several candidates
if (-not $existing) {
    Write-Host "Creating the service $Name"
    New-Service -Name $Name -BinaryPathName $quotedExe -DisplayName 'Telrang' -StartupType Automatic `
        -Description 'Telrang voting app (docs/deploy/windows.md)' | Out-Null
}
else {
    $wmi = Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'"
    if ($wmi.PathName -ne $quotedExe) {
        $result = Invoke-CimMethod -InputObject $wmi -MethodName Change -Arguments @{ PathName = $quotedExe }
        if ($result.ReturnValue -ne 0) { throw "Changing the service's program path failed ($($result.ReturnValue))." }
    }
}
Invoke-Sc config $Name obj= $Account start= delayed-auto
# Restart after 5 s, 30 s, then every 60 s; the failure count resets after a day. Also for a non-zero exit code.
Invoke-Sc failure $Name reset= 86400 actions= restart/5000/restart/30000/restart/60000
Invoke-Sc failureflag $Name 1

$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
$wanted = @('ASPNETCORE_ENVIRONMENT=Production', 'DOTNET_EnableDiagnostics=0', 'DOTNET_NOLOGO=1')
$environment = @()
$property = Get-ItemProperty -LiteralPath $serviceKey -ErrorAction SilentlyContinue
if ($property -and ($property.PSObject.Properties.Name -contains 'Environment')) { $environment = @($property.Environment) }
foreach ($entry in $wanted) {
    $key = $entry.Split('=')[0] + '='
    if (-not ($environment | Where-Object { $_.StartsWith($key, [StringComparison]::OrdinalIgnoreCase) })) { $environment += $entry }
}
New-ItemProperty -LiteralPath $serviceKey -Name Environment -PropertyType MultiString -Value $environment -Force | Out-Null

# ---- configuration and data ----
foreach ($dir in @($ConfigDir, $DataDir, $BackupDir)) {
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
}
# Taken over whoever created them (pentest R3-DEP-4): owner Administrators, ACLs reset down the tree.
Set-AdminOwner $ConfigDir
Set-PrivateAcl $ConfigDir ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute)
Set-PrivateAcl $DataDir ([System.Security.AccessControl.FileSystemRights]::Modify)
Set-PrivateAcl $BackupDir 0 # SYSTEM and Administrators only: not even the service reads its backups (pentest R3-DEP-9)
if ($existing -and @(Get-ChildItem -LiteralPath $DataDir -Force).Count -gt 0) {
    $backup = Join-Path $BackupDir ("telrang-data-" + (Get-Date -Format 'yyyy-MM-dd-HHmmss') + ".zip")
    Write-Host "Backing up $DataDir to $backup"
    Compress-Archive -Path (Join-Path $DataDir '*') -DestinationPath $backup
}
if (-not (Test-Path -LiteralPath $configFile)) {
    Copy-Item -LiteralPath (Join-Path $Source 'telrang.json') -Destination $configFile
    Write-Host "Wrote the example configuration $configFile"
}

if (-not [System.Diagnostics.EventLog]::SourceExists($Name)) {
    [System.Diagnostics.EventLog]::CreateEventSource($Name, 'Application')
}

if ($OpenFirewall) {
    Get-NetFirewallRule -Name 'Telrang-TCP' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -Name 'Telrang-TCP' -DisplayName "Telrang (TCP $Port)" -Direction Inbound -Protocol TCP `
        -LocalPort $Port -Program $Exe -Action Allow | Out-Null
    Write-Host "Firewall: inbound TCP $Port allowed for $Exe"
}

if ($NoStart) {
    Write-Host "Installed. Start with: Start-Service $Name"
    return
}

Write-Host "Starting $Name"
Start-Service -Name $Name
$keyFile = Join-Path $DataDir 'admin-key.txt'
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath $keyFile)) {
    if ((Get-Service -Name $Name).Status -eq 'Stopped') { throw "$Name stopped. See Event Viewer > Windows Logs > Application, source Telrang." }
    Start-Sleep -Seconds 1
}
if (Test-Path -LiteralPath $keyFile) {
    Write-Host ''
    Get-Content -LiteralPath $keyFile | Write-Host
    Write-Host "(also in $keyFile; it is deleted at the next start)"
}
Write-Host "Running. Configuration: $configFile  Data: $DataDir  Logs: Event Viewer, source Telrang"
