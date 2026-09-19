#Requires -Version 5.1

<#
.SYNOPSIS
    Builds self-contained DiscordChatExporter binaries.

.DESCRIPTION
    Wraps the `dotnet publish` invocations that the project's CI uses, so that
    producing runnable binaries from a local clone is a single command.

    With no arguments it publishes both the GUI and the CLI as self-contained,
    trimmed win-x64 executables into .\bin\, ready to run on a machine with no
    .NET runtime installed.

.PARAMETER Target
    Which app(s) to build: Cli, Gui, or Both (default).

.PARAMETER Runtime
    One or more .NET RIDs to build for. Defaults to win-x64. Others used
    upstream: win-x86, win-arm64, linux-x64, linux-arm, linux-arm64,
    linux-musl-x64, osx-x64, osx-arm64.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER Version
    Version stamped into the assemblies. Defaults to 999.9.9-local.

.PARAMETER Output
    Root output directory. Defaults to .\bin\ next to this script. Each build
    lands in <Output>\<AssetName>.<rid>\.

.PARAMETER Portable
    Build framework-dependent instead of self-contained. Far smaller output,
    but requires the .NET 10 runtime on the target machine.

.PARAMETER NoTrim
    Disable IL trimming. Larger output, but removes any risk of trimming away
    something only reached via reflection. Worth trying first if a
    self-contained build misbehaves at runtime.

.PARAMETER Format
    Let CSharpier reformat the source files in place before building. Off by
    default so that building never touches source. Note that CSharpier runs in
    check mode in Release and fails the build on unformatted source, which is
    how CI enforces formatting; this switch puts it into write mode instead.

.PARAMETER Test
    Run the test suite before publishing. Most tests hit the live Discord API
    and need a DISCORD_TOKEN environment variable.

.PARAMETER StripSymbols
    Delete .pdb files from the output. The GUI drags in ~100 MB of native
    debug symbols from the SkiaSharp and HarfBuzz packages, which the official
    releases also ship; dropping them cuts the GUI build from ~156 MB to
    ~56 MB and only costs you native stack traces.

.PARAMETER Zip
    Also produce <AssetName>.<rid>.zip alongside each output directory.

.PARAMETER Clean
    Delete all bin\ and obj\ directories first.

.PARAMETER Pause
    Wait for a keypress before exiting. Used by build.cmd so that double-click
    runs stay readable.

.EXAMPLE
    .\build.ps1
    Both apps, self-contained win-x64, into .\bin\.

.EXAMPLE
    .\build.ps1 -Target Cli -Version 2.44.0 -Zip
    Just the CLI, version-stamped, and zipped for distribution.

.EXAMPLE
    .\build.ps1 -Runtime win-x64, linux-x64 -Clean
    A clean build of both apps for two platforms.
#>

[CmdletBinding()]
param(
    [ValidateSet('Cli', 'Gui', 'Both')]
    [string] $Target = 'Both',

    [string[]] $Runtime = @('win-x64'),

    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [string] $Version = '999.9.9-local',

    [string] $Output = (Join-Path $PSScriptRoot 'bin'),

    [switch] $Portable,
    [switch] $NoTrim,
    [switch] $Format,
    [switch] $Test,
    [switch] $StripSymbols,
    [switch] $Zip,
    [switch] $Clean,
    [switch] $Pause
)

$ErrorActionPreference = 'Stop'

# Keep the build quiet and non-phoning-home, same as CI
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 'true'
$env:DOTNET_NOLOGO = 'true'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = 'true'

$root = $PSScriptRoot
$solution = Join-Path $root 'DiscordChatExporter.slnx'

# GUI assets aren't suffixed, unlike the CLI assets
$apps = @{
    Cli = @{ Project = 'DiscordChatExporter.Cli'; Asset = 'DiscordChatExporter.Cli' }
    Gui = @{ Project = 'DiscordChatExporter.Gui'; Asset = 'DiscordChatExporter' }
}

function Write-Step([string] $Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Note([string] $Message) {
    Write-Host "    $Message" -ForegroundColor DarkGray
}

function Invoke-Dotnet {
    param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $Arguments)

    Write-Note "dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE"
    }
}

# global.json pins a minimum SDK feature band and rolls forward to the latest
# one installed, so accept any same-major.minor SDK at or above that band.
function Assert-DotnetSdk {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "The .NET SDK was not found on PATH. Install it with 'winget install Microsoft.DotNet.SDK.10', or from https://dotnet.microsoft.com/download/dotnet/10.0, then open a new terminal."
    }

    $required = (Get-Content (Join-Path $root 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $requiredParts = $required.Split('.')
    $requiredMajorMinor = "$($requiredParts[0]).$($requiredParts[1])"
    $requiredBand = [int] $requiredParts[2]

    $installed = @(& dotnet --list-sdks | ForEach-Object { ($_ -split ' ')[0] })

    $usable = @($installed | Where-Object {
            $parts = $_.Split('.')
            $parts.Count -ge 3 -and
            "$($parts[0]).$($parts[1])" -eq $requiredMajorMinor -and
            [int] $parts[2] -ge $requiredBand
        })

    if ($usable.Count -eq 0) {
        throw "global.json requires .NET SDK $required or a later $requiredMajorMinor feature band. Installed: $($installed -join ', '). Install it with 'winget install Microsoft.DotNet.SDK.10'."
    }

    Write-Note ".NET SDK $($usable[-1]) (global.json requires $required)"
}

function Invoke-Clean {
    Write-Step 'Cleaning'

    $stale = @(
        Get-ChildItem $root -Recurse -Directory -Include 'bin', 'obj' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\\.git\\' }
    )

    foreach ($dir in $stale) {
        if (Test-Path $dir.FullName) {
            Write-Note "remove $($dir.FullName.Substring($root.Length + 1))"
            Remove-Item $dir.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    if (Test-Path $Output) {
        Remove-Item $Output -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-Publish {
    param([hashtable] $App, [string] $Rid, [string] $Destination)

    if (Test-Path $Destination) {
        Remove-Item $Destination -Recurse -Force
    }

    $arguments = @(
        'publish', (Join-Path $root $App.Project),
        "-p:Version=$Version",
        '--configuration', $Configuration,
        '--runtime', $Rid,
        '--output', $Destination
    )

    # CSharpier hooks into the build: it rewrites source files in Debug, but only checks them
    # in Release and fails the build if any are unformatted. Skip it unless asked to format.
    if ($Format) {
        $arguments += '-p:CSharpier_Check=false'
    }
    else {
        $arguments += '-p:CSharpier_Bypass=true'
    }

    if ($Portable) {
        $arguments += '--no-self-contained'
    }
    else {
        $arguments += '--self-contained'
    }

    if ($NoTrim) {
        $arguments += '-p:PublishTrimmed=false'
    }

    Invoke-Dotnet @arguments

    # The macOS GUI bundle is named after MacOSBundleName; match the asset name
    $bundle = Join-Path $Destination 'DCE.app'
    if ($Rid.StartsWith('osx-') -and (Test-Path $bundle)) {
        Move-Item $bundle (Join-Path $Destination "$($App.Asset).app") -Force
    }

    # Native symbols arrive as package content, so the project's
    # CopyOutputSymbolsToPublishDirectory=false doesn't cover them
    if ($StripSymbols) {
        $symbols = @(Get-ChildItem $Destination -Recurse -File -Filter '*.pdb')
        if ($symbols.Count -gt 0) {
            $saved = ($symbols | Measure-Object Length -Sum).Sum / 1MB
            Write-Note ("stripping {0} symbol file(s), {1:N0} MB" -f $symbols.Count, $saved)
            $symbols | Remove-Item -Force
        }
    }

    if ($Zip) {
        $archive = Join-Path $Output "$($App.Asset).$Rid.zip"
        if (Test-Path $archive) {
            Remove-Item $archive -Force
        }
        Write-Note "packing $(Split-Path $archive -Leaf)"
        Compress-Archive -Path (Join-Path $Destination '*') -DestinationPath $archive
    }
}

$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$exitCode = 0

try {
    if (-not (Test-Path $solution)) {
        throw "Could not find DiscordChatExporter.slnx in '$root'. Keep this script in the root of the cloned repository."
    }

    Write-Step 'Checking prerequisites'
    Assert-DotnetSdk

    if ($Clean) {
        Invoke-Clean
    }

    Write-Step 'Restoring packages'
    Invoke-Dotnet 'restore' $solution

    if ($Test) {
        Write-Step 'Running tests'
        if (-not $env:DISCORD_TOKEN) {
            Write-Warning 'DISCORD_TOKEN is not set; most tests hit the live Discord API and will fail without it.'
        }
        Invoke-Dotnet 'test' $solution '--configuration' $Configuration '-p:CSharpier_Bypass=true'
    }

    if ($Target -eq 'Both') {
        $targets = @('Gui', 'Cli')
    }
    else {
        $targets = @($Target)
    }

    $results = @()

    foreach ($rid in $Runtime) {
        foreach ($name in $targets) {
            $app = $apps[$name]
            $destination = Join-Path $Output "$($app.Asset).$rid"

            Write-Step "Publishing $($app.Project) for $rid"
            Invoke-Publish -App $app -Rid $rid -Destination $destination

            $results += [pscustomobject]@{
                App     = $app.Project
                Runtime = $rid
                Output  = $destination
            }
        }
    }

    Write-Step "Done in $([int] $stopwatch.Elapsed.TotalSeconds)s"

    foreach ($result in $results) {
        Write-Host "    $($result.App) [$($result.Runtime)]" -ForegroundColor Green
        Write-Host "      $($result.Output)"

        # Point at the executables, not just the folder, for Windows builds
        if ($result.Runtime.StartsWith('win-')) {
            Get-ChildItem $result.Output -Filter '*.exe' |
            Where-Object { $_.Name -ne 'createdump.exe' } |
            ForEach-Object { Write-Host "      -> $($_.Name)" -ForegroundColor DarkGreen }
        }
    }
}
catch {
    Write-Host ''
    Write-Host "BUILD FAILED: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
}
finally {
    if ($Pause) {
        Write-Host ''
        Write-Host 'Press any key to close...' -ForegroundColor DarkGray
        $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    }
}

exit $exitCode
