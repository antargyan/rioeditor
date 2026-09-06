<#
.SYNOPSIS
    Publishes the RioEditor Windows head and packs it into an MSIX bundle for the
    Microsoft Store.

.DESCRIPTION
    Produces two things in the output folder:

      RioEditor_<version>.msixbundle              upload this to Partner Center (unsigned;
                                                  the Store strips and re-signs anyway)
      RioEditor_<version>_test-signed.msixbundle  signed with a local self-signed cert,
                                                  for installing and testing on this machine

    The identity parameters must match Partner Center exactly (Product -> Product identity).
    Their defaults are obvious placeholders, so an accidental Store upload fails loudly
    rather than shipping under the wrong identity.

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File packaging\windows\build-msix.ps1 -IdentityName '12345YourCompany.RioEditor' -Publisher 'CN=ABCDEF12-3456-7890-ABCD-EF1234567890' -PublisherDisplayName 'Your Company' -DisplayName 'Your Reserved App Name'

    -DisplayName must be a name reserved for the product in Partner Center.

    The package version is major.minor.RioBuild.0, taken from Directory.Build.props, so bumping
    RioBuild with packaging/bump-build.ps1 is what makes a re-upload possible. -Version overrides.
#>
[CmdletBinding()]
param(
    [string]   $IdentityName         = 'REPLACE.WITH.PartnerCenter.PackageIdentityName',
    [string]   $Publisher            = 'CN=REPLACE-WITH-PARTNER-CENTER-PUBLISHER-ID',
    [string]   $PublisherDisplayName = 'REPLACE WITH PUBLISHER DISPLAY NAME',
    # Must be a name RESERVED for the product in Partner Center, not a friendly label.
    [string]   $DisplayName          = 'RioEditor : MarkDown Editor',
    # Defaults to major.minor.RioBuild.0 from Directory.Build.props; see Resolve-Version.
    [string]   $Version,
    # Overrides RioBuild when composing the version, without editing Directory.Build.props.
    # CI passes the run number here so every pipeline upload increments on its own.
    [int]      $BuildNumber,
    [string[]] $Architectures        = @('x64', 'arm64'),
    [string]   $Configuration        = 'Release',
    [string]   $OutputDirectory,
    [switch]   $SkipTestSigning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packagingRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot      = (Resolve-Path (Join-Path $packagingRoot '..\..')).Path
$project       = Join-Path $repoRoot 'src\RioEditor.Desktop\RioEditor.Desktop.csproj'
$imagesDir     = Join-Path $packagingRoot 'Images'
$template      = Join-Path $packagingRoot 'AppxManifest.template.xml'

# Captured here, at script scope, on purpose: inside a function $PSBoundParameters describes
# that function's own parameters, so testing it there silently reports 'not supplied'.
$hasBuildNumber = $PSBoundParameters.ContainsKey('BuildNumber')

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'publish\store' }
$stagingRoot = Join-Path $OutputDirectory '_staging'

# Directory.Build.props is the single source of truth for the product version across every
# platform, so the package takes its version from there rather than keeping a second copy.
#
# The package version is major.minor.RioBuild.0, which makes RioBuild the upload counter here
# exactly as it already is for Google Play's versionCode and Apple's CFBundleVersion — one knob
# for all three stores, bumped by packaging\bump-build.ps1.
#
# RioBuild cannot be the fourth field: the Store reserves the revision and rejects anything
# non-zero there. It goes in the third instead, which costs RioVersion's patch component in the
# package version. That is a fair trade — the patch is still the user-facing version everywhere
# it is actually read, and without this a re-upload of the same release is impossible.
function Resolve-Version {
    $props = Join-Path $repoRoot 'Directory.Build.props'
    if (-not (Test-Path $props)) { throw "Directory.Build.props not found at $props." }

    # XPath rather than property access: Directory.Build.props has several PropertyGroup
    # elements and only one carries these, which Set-StrictMode turns into an error.
    $xml     = [xml](Get-Content $props)
    $version = $xml.SelectSingleNode('/Project/PropertyGroup/RioVersion')
    $build   = $xml.SelectSingleNode('/Project/PropertyGroup/RioBuild')
    if (-not $version) { throw "RioVersion not found in $props; pass -Version explicitly." }
    if (-not $build)   { throw "RioBuild not found in $props; pass -Version explicitly." }

    $parts = $version.InnerText.Trim().Split('.')
    if ($parts.Count -lt 2) { throw "RioVersion '$($version.InnerText)' is not major.minor.patch." }

    $buildField = if ($hasBuildNumber) { $BuildNumber } else { $build.InnerText.Trim() }
    return "{0}.{1}.{2}.0" -f $parts[0], $parts[1], $buildField
}

if (-not $Version) {
    $Version = Resolve-Version
    $source = if ($hasBuildNumber) { "RioVersion from Directory.Build.props + BuildNumber $BuildNumber" }
              else { 'RioVersion + RioBuild from Directory.Build.props' }
    Write-Host "version  : $Version ($source)"
}

# The Store owns the revision field and rejects a package that sets it.
if ($Version -notmatch '^\d+\.\d+\.\d+\.0$') {
    throw "Version must be four parts with a revision of 0 (e.g. 1.0.0.0); got '$Version'."
}
if (-not (Test-Path $imagesDir)) {
    throw "Missing $imagesDir. Run generate-icons.ps1 first."
}

# ---- locate the Windows SDK tools ----------------------------------------
function Find-SdkTool {
    param([string]$Name)

    $binRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path $binRoot)) { throw "Windows SDK not found at $binRoot." }

    # Newest SDK first, and prefer the host architecture's build of the tool.
    $hostArch = 'x64'
    if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { $hostArch = 'arm64' }

    $tool = Get-ChildItem $binRoot -Directory |
        Where-Object { $_.Name -match '^10\.' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "$hostArch\$Name" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1

    if (-not $tool) { throw "$Name not found under $binRoot. Install the Windows SDK." }
    return $tool
}

$makeappx = Find-SdkTool 'makeappx.exe'
$signtool = Find-SdkTool 'signtool.exe'
Write-Host "makeappx : $makeappx"
Write-Host "signtool : $signtool"
Write-Host ''

if (Test-Path $OutputDirectory) { Remove-Item $OutputDirectory -Recurse -Force }
$packagesDir = Join-Path $OutputDirectory 'packages'
New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null
New-Item -ItemType Directory -Force -Path $packagesDir | Out-Null

$manifestTemplate = Get-Content $template -Raw

foreach ($arch in $Architectures) {
    Write-Host "=== $arch ===" -ForegroundColor Cyan
    $rid     = "win-$arch"
    $staging = Join-Path $stagingRoot $arch

    # Self-contained: an MSIX cannot run a prerequisite installer, so the .NET 10
    # runtime has to travel inside the package.
    & dotnet publish $project -c $Configuration -r $rid --self-contained true -p:PublishSingleFile=false -o $staging
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid." }

    # Symbols bloat the package and the Store has no use for them.
    Get-ChildItem $staging -Filter *.pdb -Recurse | Remove-Item -Force

    Copy-Item $imagesDir (Join-Path $staging 'Images') -Recurse -Force
    # A build input, not package payload: the listing artwork is for Partner Center's web form.
    Remove-Item (Join-Path $staging 'Images\StoreListing-300x300.png') -Force -ErrorAction SilentlyContinue

    $manifest = $manifestTemplate.
        Replace('{IdentityName}',          $IdentityName).
        Replace('{Publisher}',             $Publisher).
        Replace('{PublisherDisplayName}',  $PublisherDisplayName).
        Replace('{DisplayName}',           $DisplayName).
        Replace('{Version}',               $Version).
        Replace('{ProcessorArchitecture}', $arch)
    Set-Content -Path (Join-Path $staging 'AppxManifest.xml') -Value $manifest -Encoding UTF8

    $msix = Join-Path $packagesDir "RioEditor_${Version}_$arch.msix"
    & $makeappx pack /d $staging /p $msix /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed for $arch." }
    Write-Host ''
}

# ---- bundle --------------------------------------------------------------
$bundle = Join-Path $OutputDirectory "RioEditor_$Version.msixbundle"
& $makeappx bundle /d $packagesDir /p $bundle /bv $Version /o
if ($LASTEXITCODE -ne 0) { throw 'makeappx bundle failed.' }

Write-Host ''
Write-Host "Store upload package: $bundle" -ForegroundColor Green

# ---- test signing --------------------------------------------------------
# Only so the bundle can be installed on this machine. Partner Center discards this
# signature and re-signs with the Store's own certificate.
if (-not $SkipTestSigning) {
    $pfxPassword = 'rioeditor-test'
    $pfx = Join-Path $OutputDirectory 'RioEditorTest.pfx'
    $cer = Join-Path $OutputDirectory 'RioEditorTest.cer'

    # The certificate subject must equal the manifest's Publisher exactly, or
    # signtool rejects the package with "publisher name does not match".
    # Reuse a matching cert if one is already here, so repeated builds do not
    # pile up near-identical certificates in the personal store.
    $cert = Get-ChildItem 'Cert:\CurrentUser\My' |
        Where-Object { $_.Subject -eq $Publisher -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if ($cert) {
        Write-Host "Reusing existing test certificate $($cert.Thumbprint)"
    } else {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature -FriendlyName 'RioEditor MSIX test signing' -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
        Write-Host "Created test certificate $($cert.Thumbprint)"
    }

    $securePassword = ConvertTo-SecureString -String $pfxPassword -Force -AsPlainText
    Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $pfx -Password $securePassword | Out-Null
    Export-Certificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $cer -Type CERT | Out-Null

    $signedBundle = Join-Path $OutputDirectory "RioEditor_${Version}_test-signed.msixbundle"
    Copy-Item $bundle $signedBundle -Force
    & $signtool sign /fd SHA256 /f $pfx /p $pfxPassword $signedBundle
    if ($LASTEXITCODE -ne 0) { throw 'signtool sign failed.' }

    Write-Host ''
    Write-Host "Test-signed package : $signedBundle" -ForegroundColor Green
    Write-Host "Test certificate    : $cer"
    Write-Host ''
    Write-Host 'To install locally, in an elevated shell (the import is a one-time step):'
    Write-Host "  Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    Write-Host "  Add-AppxPackage -Path '$signedBundle'"
}

Write-Host ''
Write-Host "Staging folders kept for inspection at: $stagingRoot"
