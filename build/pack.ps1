# Builds the RemoteMan installer (RemoteMan-win-Setup.exe) and update packages with Velopack.
# Used by both CI workflows; also works locally on Windows:  ./build/pack.ps1 -Version 0.1.0
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [string] $PublishDir = 'publish',
    [string] $OutputDir = 'releases',
    [string] $ReleaseNotes = ''
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

vpk @vpkArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }
