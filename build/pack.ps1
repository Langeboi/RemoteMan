# Builds the RemoteMan installer (RemoteMan-win-Setup.exe) and update packages with Velopack.
# Used by both CI workflows; also works locally on Windows:  ./build/pack.ps1 -Version 0.1.0
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [string] $PublishDir = 'publish',
    [string] $OutputDir = 'releases',
    [string] $ReleaseNotes = '',
    # 'test' for test builds; empty = the default release channel.
    [string] $Channel = ''
)
$ErrorActionPreference = 'Stop'
$assets = Join-Path $PSScriptRoot '..\src\RemoteMan.App\Assets'

$vpkArgs = @(
    'pack',
    '--packId', 'RemoteMan',
    '--packTitle', 'RemoteMan',
    '--packAuthors', 'RemoteMan',
    '--packVersion', $Version,
    '--packDir', $PublishDir,
    '--mainExe', 'RemoteMan.exe',
    '--outputDir', $OutputDir,
    # Installs the .NET 8 desktop runtime first if the PC doesn't have it.
    '--framework', 'net8.0-x64-desktop',
    '--icon', (Join-Path $assets 'remoteman.ico'),
    '--splashImage', (Join-Path $assets 'setup-splash.png'),
    '--shortcuts', 'Desktop,StartMenuRoot'
)
if ($ReleaseNotes) { $vpkArgs += @('--releaseNotes', $ReleaseNotes) }
if ($Channel) { $vpkArgs += @('--channel', $Channel) }

# Code signing, only when a certificate is configured (CI secrets); otherwise the build is unsigned.
#  - Azure Trusted Signing: AZURE_SIGNING_ENDPOINT, AZURE_SIGNING_ACCOUNT, AZURE_SIGNING_PROFILE, plus
#    AZURE_TENANT_ID / AZURE_CLIENT_ID / AZURE_CLIENT_SECRET of an app registration with the
#    "Trusted Signing Certificate Profile Signer" role.
#  - Or a .pfx certificate: SIGN_PFX_BASE64 (the file, base64) and SIGN_PFX_PASSWORD.
if ($env:AZURE_SIGNING_ENDPOINT -and $env:AZURE_SIGNING_ACCOUNT -and $env:AZURE_SIGNING_PROFILE) {
    $meta = Join-Path ([IO.Path]::GetTempPath()) 'trusted-signing.json'
    @{ Endpoint = $env:AZURE_SIGNING_ENDPOINT; CodeSigningAccountName = $env:AZURE_SIGNING_ACCOUNT; CertificateProfileName = $env:AZURE_SIGNING_PROFILE } |
        ConvertTo-Json | Set-Content -Encoding utf8 $meta
    $vpkArgs += @('--azureTrustedSignFile', $meta)
    Write-Host 'Signing with Azure Trusted Signing'
}
elseif ($env:SIGN_PFX_BASE64) {
    $pfx = Join-Path ([IO.Path]::GetTempPath()) 'remoteman-signing.pfx'
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:SIGN_PFX_BASE64))
    $vpkArgs += @('--signParams', "/f `"$pfx`" /p `"$($env:SIGN_PFX_PASSWORD)`" /fd sha256 /tr http://timestamp.digicert.com /td sha256")
    Write-Host 'Signing with the .pfx certificate'
}
else {
    Write-Host 'No signing certificate configured: building an unsigned installer.'
}

vpk @vpkArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }
