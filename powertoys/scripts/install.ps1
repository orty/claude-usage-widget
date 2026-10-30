<#
.SYNOPSIS
    Builds the Claude Usage Dock extension and installs it, without Visual Studio.

.DESCRIPTION
    Needs only the .NET 10 SDK (winget install Microsoft.DotNet.SDK.10).

    1. Creates, once, a self-signed code-signing certificate for
       CN=ClaudeUsageDock (the Publisher in Package.appxmanifest) in your own
       certificate store. Its private key cannot be exported.
    2. Trusts that certificate for app packages (Local Machine > Trusted
       People). This is the one step that needs administrator rights: Windows
       asks once, the first time.
    3. Builds and signs the package with a version taken from the clock, so
       every build is an update of the one installed: the accounts and
       settings the extension keeps in its package folder survive it.
    4. Installs it.
    5. Reloads Command Palette once the new build has started, when its
       "Enable external reload" setting is on; otherwise run "Reload" yourself.

    Run it again after every merge of upstream.

.PARAMETER Platform
    x64 or ARM64. Defaults to this PC's.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File powertoys\scripts\install.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'x64' })
)

$ErrorActionPreference = 'Stop'
$Publisher = 'CN=ClaudeUsageDock'   # must equal Identity/@Publisher in Package.appxmanifest
$PackageName = 'ClaudeUsageDock'    # Identity/@Name

$project = Join-Path $PSScriptRoot '..\ClaudeUsageDock' | Resolve-Path
$csproj = Join-Path $project 'ClaudeUsageDock.csproj'
$manifest = Join-Path $project 'Package.appxmanifest'
$out = Join-Path $env:TEMP "ClaudeUsageDock-build\$Platform"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK is not installed. Install it with: winget install Microsoft.DotNet.SDK.10'
}

# 1. The signing certificate, made once and kept in the user's store.
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(7) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    Write-Host 'Creating a code-signing certificate for local builds...'
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher `
        -KeyUsage DigitalSignature -KeyExportPolicy NonExportable `
        -FriendlyName 'Claude Usage for the Dock (local builds)' `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}

# 2. Trusted for app packages: the only step that needs elevation.
$trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $cert.Thumbprint
if (-not $trusted) {
    $cer = Join-Path $env:TEMP 'ClaudeUsageDock-signing.cer'
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    $import = "Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($isAdmin) {
        Invoke-Expression $import
    } else {
        Write-Host 'Windows will ask once to trust the certificate for app packages...'
        Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', $import
    }
    if (-not (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $cert.Thumbprint)) {
        throw 'The certificate was not trusted (the elevation was declined?). Nothing was installed.'
    }
}

# 3. Build and sign, with a version that is higher at every build. The
#    manifest is put back as it was, so the working tree stays clean.
$now = Get-Date
# Major.Minor.Build.Revision, each at most 65535: days since 2026, then
# two-second steps of the day.
$version = '1.0.{0}.{1}' -f ($now.Date - [datetime]'2026-01-01').Days, [int][math]::Floor($now.TimeOfDay.TotalSeconds / 2)
$original = [IO.File]::ReadAllText($manifest)
try {
    [xml]$xml = $original
    $xml.Package.Identity.Version = $version
    $xml.Save($manifest)
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    Write-Host "Building $PackageName $version ($Platform)..."
    & dotnet build $csproj -c Release "-p:Platform=$Platform" `
        -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=true `
        "-p:PackageCertificateThumbprint=$($cert.Thumbprint)" "-p:AppxPackageDir=$out\" -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "The build failed (exit code $LASTEXITCODE)." }
} finally {
    [IO.File]::WriteAllText($manifest, $original)
}

$msix = Get-ChildItem $out -Recurse -Filter '*.msix' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msix) { throw "No package was produced under $out." }

# 4. Install, as an update when an older build is there.
Write-Host "Installing $($msix.Name)..."
Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown
$installed = Get-AppxPackage -Name $PackageName
if (-not $installed -or $installed.Version -ne $version) {
    throw "The package did not install as $version."
}
Write-Host ''
Write-Host "Installed $PackageName $version."

# 5. Command Palette starts a freshly installed extension late (its first
#    launch takes 15-30 s, before any of its code runs) and then leaves its
#    pinned Dock bands out, and on the main screen the Memory half of the
#    performance band too, until a Reload. Reload it once the extension is up,
#    through x-cmdpal://reload, which Command Palette honours only with
#    Settings > General > For Developers > "Enable external reload" on.
$reloaded = $false
$cmdpal = Get-AppxPackage -Name 'Microsoft.CommandPalette' -ErrorAction SilentlyContinue
if ($cmdpal) {
    $cmdpalSettings = Join-Path $env:LOCALAPPDATA "Packages\$($cmdpal.PackageFamilyName)\LocalState\settings.json"
    $allowed = $false
    try { $allowed = [bool](Get-Content $cmdpalSettings -Raw | ConvertFrom-Json).AllowExternalReload } catch { }
    if ($allowed) {
        Write-Host 'Waiting for Command Palette to start the new build, then reloading it...'
        $log = Join-Path $env:LOCALAPPDATA "Packages\$($installed.PackageFamilyName)\LocalState\extension.log"
        $since = Get-Date
        $deadline = $since.AddSeconds(90)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 2
            if ((Test-Path $log) -and (Get-Item $log).LastWriteTime -gt $since -and
                (Select-String -Path $log -Pattern 'COM server started' -SimpleMatch | Select-Object -Last 1)) {
                break
            }
        }
        Start-Sleep -Seconds 3   # let Command Palette finish its late start first
        Start-Process 'x-cmdpal://reload'
        $reloaded = $true
    }
}

if ($reloaded) {
    Write-Host 'Command Palette was reloaded.'
} else {
    Write-Host 'Next: in Command Palette run "Reload". (Turn on Settings > General > For Developers >'
    Write-Host '"Enable external reload" and this script reloads it for you next time.)'
}
Write-Host 'To add accounts, type "Claude usage" in Command Palette; to show them, right-click'
Write-Host 'the Dock > Edit Dock > + and add the "Claude usage" bands.'
