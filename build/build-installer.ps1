# Builds the RemoteMan installer on this PC, the same way CI does (test, publish, Velopack pack).
#
#   git clone -b test/all-features https://github.com/Langeboi/RemoteMan.git
#   cd RemoteMan
#   .\build\build-installer.ps1            # builds releases\RemoteMan-*-Setup.exe
#   .\build\build-installer.ps1 -Install   # ...and runs it
#
# Needs Windows 10/11, Git and the .NET 10 SDK. Installs the Velopack CLI (vpk) as a .NET tool for
# your user if it isn't there. Works in Windows PowerShell 5.1 and PowerShell 7.
param(
    # Installer version. Default: <app version>-local.<date>.<time>, e.g. 0.1.0-local.20261122.1634.
    # That sorts above the CI test builds of the same app version and below the real release, so an
    # installed local build updates to the next release or the next test build after a version bump.
    [string] $Version = '',
    # Update channel baked into the installer: 'test' follows the test builds, '' the normal releases.
    [string] $Channel = 'test',
    [switch] $SkipTests,
    # Run the installer when it's built.
    [switch] $Install
)
$ErrorActionPreference = 'Stop'
$VpkVersion = '1.2.161'   # keep in step with the Velopack package in RemoteMan.App.csproj and the workflows
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Push-Location $root
try {
    function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
    function Run {
        & $args[0] @($args | Select-Object -Skip 1)
        if ($LASTEXITCODE -ne 0) { throw "$($args -join ' ') failed (exit code $LASTEXITCODE)" }
    }

    if ($env:OS -ne 'Windows_NT') { throw 'The installer can only be built on Windows.' }

    Step 'Checking the .NET SDK'
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw ".NET SDK not found. Install the .NET 10 SDK:`n  winget install Microsoft.DotNet.SDK.10`nthen open a new PowerShell window and run this script again."
    }
    $sdks = @(dotnet --list-sdks | ForEach-Object { [int](($_ -split '\.')[0]) })
    if (-not ($sdks | Where-Object { $_ -ge 10 })) {
        throw ".NET 10 SDK not found (installed: $((dotnet --list-sdks) -join ', ')). Install it:`n  winget install Microsoft.DotNet.SDK.10"
    }
    Write-Host "dotnet $(dotnet --version)"

    if (-not $Version) {
        $base = (dotnet msbuild src/RemoteMan.App -getProperty:Version).Trim()
        if (-not $base) { throw 'Could not read the app version from RemoteMan.App.csproj.' }
        # No leading zeros: SemVer numeric parts like 0905 are invalid, so the time is written as 905.
        $now = Get-Date
        $Version = "$base-local.$($now.ToString('yyyyMMdd')).$([int]$now.ToString('HHmm'))"
    }
    Write-Host "Version $Version, channel '$(if ($Channel) { $Channel } else { 'default' })'"

    if (-not $SkipTests) {
        Step 'Running the tests'
        Run dotnet test tests/RemoteMan.Tests -c Release
    }

    Step 'Publishing the app'
    if (Test-Path publish) { Remove-Item publish -Recurse -Force }
    Run dotnet publish src/RemoteMan.App -c Release -r win-x64 --self-contained false "-p:Version=$Version" -o publish

    Step "Checking the Velopack CLI (vpk $VpkVersion)"
    $toolsDir = Join-Path $env:USERPROFILE '.dotnet\tools'
    if (($env:PATH -split ';') -notcontains $toolsDir) { $env:PATH = "$env:PATH;$toolsDir" }
    $installed = $null
    foreach ($line in (dotnet tool list -g)) { if ($line -match '^\s*vpk\s+(\S+)') { $installed = $Matches[1] } }
    if (-not $installed) {
        Run dotnet tool install -g vpk --version $VpkVersion
    } elseif ($installed -ne $VpkVersion) {
        Run dotnet tool update -g vpk --version $VpkVersion --allow-downgrade
    } else {
        Write-Host "vpk $installed already installed"
    }

    Step 'Building the installer'
    & (Join-Path $PSScriptRoot 'pack.ps1') -Version $Version -Channel $Channel -PublishDir publish -OutputDir releases

    $setup = Get-ChildItem releases -Filter '*-Setup.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $setup) { throw 'vpk finished but no *-Setup.exe was found in releases\.' }
    Write-Host "`nInstaller: $($setup.FullName)" -ForegroundColor Green

    if ($Install) {
        Step 'Running the installer'
        Start-Process $setup.FullName -Wait
    }
}
finally {
    Pop-Location
}
