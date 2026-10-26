#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
  One-time, explicit administrator-managed Defender exclusions for HomeWatch.
.DESCRIPTION
  Adds/removes precise per-user HomeWatch package staging folders and explicitly
  supplied local game library folders. Never called by the HomeWatch agent,
  updater service or web UI. Changes remain under Windows administrator control.
.EXAMPLE
  .\Manage-DefenderExclusions.ps1 -Action Add -GameFolders 'D:\Games'
.EXAMPLE
  .\Manage-DefenderExclusions.ps1 -Action Remove -GameFolders 'D:\Games'
.EXAMPLE
  .\Manage-DefenderExclusions.ps1 -Action Add -GameFolders 'D:\Games','C:\Games' -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Add', 'Remove')]
    [string]$Action,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$GameFolders
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command Get-MpPreference -ErrorAction SilentlyContinue) -or
    -not (Get-Command Add-MpPreference -ErrorAction SilentlyContinue) -or
    -not (Get-Command Remove-MpPreference -ErrorAction SilentlyContinue)) {
    throw 'Microsoft Defender antivirus management commands are not available on this computer.'
}

# Validate user-supplied game paths: never exclude an entire drive, UNC share,
# Windows/Program Files tree, or a wildcard/expression.
function Get-SafeGamePath([string]$Value) {
    if ($Value -match '[*?%]' -or $Value -match '^\\\\' -or $Value -notmatch '^[A-Za-z]:\\') {
        throw "Not a supported local absolute game directory: $Value"
    }
    $full = [IO.Path]::GetFullPath($Value).TrimEnd('\')
    $drive = [IO.Path]::GetPathRoot($full).TrimEnd('\')
    if ($full -eq $drive -or $full.Length -le 4) {
        throw "Refusing to exclude a drive root: $Value"
    }
    $systemRoots = @($env:windir, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData) |
        Where-Object { $_ } | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') }
    foreach ($root in $systemRoots) {
        if ($full.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
            $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to exclude an operating-system/application directory: $Value"
        }
    }
    return $full
}

$paths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($game in $GameFolders) {
    [void]$paths.Add((Get-SafeGamePath $game))
}

# HomeWatch stages as the logged-in user in their own LOCALAPPDATA. Include
# existing non-special local profiles so an administrator can configure child
# accounts without signing in as each child or exposing credentials in HomeWatch.
$profiles = @(Get-CimInstance Win32_UserProfile |
    Where-Object { -not $_.Special -and $_.LocalPath -and $_.LocalPath -match '^[A-Za-z]:\\' })
foreach ($profile in $profiles) {
    $local = [IO.Path]::GetFullPath($profile.LocalPath)
    if (-not (Test-Path -LiteralPath $local -PathType Container)) { continue }
    foreach ($suffix in @('Packages', 'installers')) {
        [void]$paths.Add((Join-Path $local ("AppData\Local\HomeWatch\" + $suffix)))
    }
}
if ($paths.Count -eq 0) { throw 'No eligible local folders were found.' }

Write-Warning 'Excluded folders and their contents will not be checked by Defender real-time scanning. A user who can write to these folders can place other unscanned files there. Use only on systems you administer.'
Write-Host "Requested action: $Action. Review the exact directories below:"
$paths | Sort-Object | ForEach-Object { Write-Host "  $_" }

$existing = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in @((Get-MpPreference).ExclusionPath)) {
    if ($entry) { [void]$existing.Add($entry.TrimEnd('\')) }
}
foreach ($path in ($paths | Sort-Object)) {
    $known = $existing.Contains($path.TrimEnd('\'))
    if ($Action -eq 'Add' -and $known) { Write-Host "Already excluded: $path"; continue }
    if ($Action -eq 'Remove' -and -not $known) { Write-Host "No existing exclusion: $path"; continue }
    if ($PSCmdlet.ShouldProcess($path, "$Action Microsoft Defender real-time exclusion")) {
        if ($Action -eq 'Add') { Add-MpPreference -ExclusionPath $path -ErrorAction Stop }
        else { Remove-MpPreference -ExclusionPath $path -ErrorAction Stop }
        Write-Host "$Action completed: $path"
    }
}
Write-Host 'Review Windows Security > Virus & threat protection > Exclusions to confirm the final settings.'
