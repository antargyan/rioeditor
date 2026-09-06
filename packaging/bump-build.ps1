<#
.SYNOPSIS
    Increments RioBuild — the store upload counter — and optionally sets RioVersion.

.DESCRIPTION
    One knob for all three stores. Every store refuses an upload whose build number it has
    already seen, and each has its own name for the same thing:

      Google Play       android:versionCode        <- ApplicationVersion  <- RioBuild
      App Store         CFBundleVersion            <- ApplicationVersion  <- RioBuild
      Microsoft Store   third field of the MSIX version                   <- RioBuild

    RioBuild must only ever increase, and must never reset when RioVersion changes: the Microsoft
    Store compares whole package versions, so a reset would produce a version lower than one
    already published and the upload would be rejected.

    RioVersion stays the human-facing version and moves on its own schedule.

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File packaging\bump-build.ps1
    Bumps the build number only — the normal case for re-uploading the same release.

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File packaging\bump-build.ps1 -SetVersion 1.1.0
    Bumps the build number and moves the display version at the same time.

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File packaging\bump-build.ps1 -WhatIf
    Shows what would change without writing.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $SetVersion,
    [int]    $SetBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..')).Path
$props    = Join-Path $repoRoot 'Directory.Build.props'
if (-not (Test-Path $props)) { throw "Directory.Build.props not found at $props." }

if ($SetVersion -and $SetVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "SetVersion must be three parts (e.g. 1.1.0); got '$SetVersion'."
}

# Edited as text, not as XML: round-tripping through XmlDocument reflows the whole file and
# throws away the comments that explain what these properties are for.
$text = Get-Content $props -Raw

function Read-Prop {
    param([string]$Name)
    $m = [regex]::Match($text, "<$Name>\s*([^<]+?)\s*</$Name>")
    if (-not $m.Success) { throw "$Name not found in $props." }
    return $m.Groups[1].Value
}

$currentVersion = Read-Prop 'RioVersion'
$currentBuild   = [int](Read-Prop 'RioBuild')

$newBuild = if ($PSBoundParameters.ContainsKey('SetBuild')) { $SetBuild } else { $currentBuild + 1 }
if ($newBuild -le $currentBuild -and -not $PSBoundParameters.ContainsKey('SetBuild')) {
    throw "Refusing to produce a build number that does not increase ($currentBuild -> $newBuild)."
}
if ($newBuild -le $currentBuild) {
    Write-Warning "Build number is going backwards ($currentBuild -> $newBuild). Every store will reject an upload it has already seen."
}

$newVersion = if ($SetVersion) { $SetVersion } else { $currentVersion }

Write-Host "RioVersion : $currentVersion -> $newVersion"
Write-Host "RioBuild   : $currentBuild -> $newBuild"
Write-Host ''
Write-Host 'Resulting store identifiers:'
$parts = $newVersion.Split('.')
Write-Host ("  Google Play  versionCode      {0}" -f $newBuild)
Write-Host ("  App Store    CFBundleVersion  {0}" -f $newBuild)
Write-Host ("  MS Store     package version  {0}.{1}.{2}.0" -f $parts[0], $parts[1], $newBuild)

if (-not $PSCmdlet.ShouldProcess($props, "set RioVersion=$newVersion, RioBuild=$newBuild")) {
    return
}

$text = [regex]::Replace($text, '<RioVersion>\s*[^<]+?\s*</RioVersion>', "<RioVersion>$newVersion</RioVersion>", 1)
$text = [regex]::Replace($text, '<RioBuild>\s*[^<]+?\s*</RioBuild>',     "<RioBuild>$newBuild</RioBuild>",     1)
Set-Content -Path $props -Value $text -NoNewline

Write-Host ''
Write-Host "Updated $props" -ForegroundColor Green
