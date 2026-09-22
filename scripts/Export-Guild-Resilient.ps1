# Export-Guild-Resilient.ps1
#
# Native-resume wrapper for this DiscordChatExporter fork.
#
# Unlike the original per-channel workaround, this script runs "exportguild" so channels that
# cannot be resolved through /channels/{id} can still be exported from the guild channel list.
# The fork's native --resume support checkpoints each successful channel to manifest.json.
# If the process fails or only some channels fail, the next pass skips verified completed
# channels and retries only the unfinished work.
#
# Example:
#   .\Export-Guild-Resilient.ps1 -Token $env:DCE_TOKEN -GuildId 123456789012345678 -OutputDirectory "C:\Discord Exports\My Server" -Format Csv
#
# Extra DCE options can be supplied with -DceArguments, for example:
#   .\Export-Guild-Resilient.ps1 ... -DceArguments @('--media', '--reuse-media', '--include-threads', 'all')

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Token,

    [Parameter(Mandatory)]
    [string]$GuildId,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [string]$DceExe,

    [ValidateSet('HtmlDark', 'HtmlLight', 'PlainText', 'Csv', 'Json', 'Db')]
    [string]$Format = 'Csv',

    [ValidateRange(1, 128)]
    [int]$Parallel = 1,

    [ValidateRange(1, 100)]
    [int]$MaxPartialFailurePasses = 3,

    [ValidateRange(1, 100)]
    [int]$MaxProcessFailureAttempts = 10,

    [ValidateRange(1, 300)]
    [int]$MaxRetryDelaySeconds = 30,

    [switch]$ResetState,

    [string[]]$DceArguments = @()
)

$ErrorActionPreference = 'Stop'


# ============================================================================
# RESOLVE CLI
# ============================================================================

if ([string]::IsNullOrWhiteSpace($DceExe)) {
    $LocalCandidates = @(
        (Join-Path (Get-Location) 'DiscordChatExporter.Cli.exe'),
        (Join-Path $PSScriptRoot 'DiscordChatExporter.Cli.exe'),
        (Join-Path (Split-Path $PSScriptRoot -Parent) 'DiscordChatExporter.Cli.exe')
    )

    $DceExe = $LocalCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1

    if (-not $DceExe) {
        $Command = Get-Command 'DiscordChatExporter.Cli.exe' -ErrorAction SilentlyContinue
        if ($Command) {
            $DceExe = $Command.Source
        }
    }
}

if (-not $DceExe -or -not (Test-Path -LiteralPath $DceExe -PathType Leaf)) {
    throw 'DiscordChatExporter.Cli.exe was not found. Pass its path with -DceExe.'
}


# ============================================================================
# PATHS / STATE
# ============================================================================

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$ManifestFile = Join-Path $OutputDirectory 'manifest.json'
$LogFile = Join-Path $OutputDirectory "_resilient_export_$GuildId.log"

if ($ResetState) {
    Remove-Item -LiteralPath $ManifestFile -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath "$ManifestFile.bak" -Force -ErrorAction SilentlyContinue

    Remove-Item -LiteralPath (Join-Path $OutputDirectory "_completed_channels_$GuildId.txt") -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $OutputDirectory "_skipped_channels_$GuildId.txt") -Force -ErrorAction SilentlyContinue
}


function Write-Log {
    param(
        [Parameter(Mandatory)]
        [string]$Message
    )

    $Timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $Line = "[$Timestamp] $Message"

    Write-Host $Line
    Add-Content -LiteralPath $LogFile -Value $Line -Encoding UTF8
}


function Get-RetryDelay {
    param(
        [Parameter(Mandatory)]
        [int]$Attempt
    )

    $Exponent = [Math]::Min($Attempt, 5)
    $Delay = [Math]::Pow(2, $Exponent)

    return [int][Math]::Min($MaxRetryDelaySeconds, $Delay)
}


function Test-AuthenticationFailure {
    param([string]$Text)

    return (
        $Text -match '(?i)authentication token is invalid' -or
        $Text -match '(?i)\bunauthorized\b'
    )
}


function Test-PartialChannelFailure {
    param([string]$Text)

    return (
        $Text -match '(?i)Failed to export the following channel\(s\):' -or
        $Text -match '(?i)Export failed\.'
    )
}


function Test-PermanentOnlyChannelFailure {
    param([string]$Text)

    $HasPermanentFailure = (
        $Text -match '(?i)\bforbidden\b' -or
        $Text -match '(?i)\bnot found\b' -or
        $Text -match '(?i)missing access' -or
        $Text -match '(?i)unknown channel'
    )

    $HasTransientFailure = (
        $Text -match '(?i)malformed or truncated JSON' -or
        $Text -match '(?i)too many requests' -or
        $Text -match '(?i)rate.?limit' -or
        $Text -match '(?i)timed? out' -or
        $Text -match '(?i)temporar' -or
        $Text -match '(?i)bad gateway' -or
        $Text -match '(?i)service unavailable' -or
        $Text -match '(?i)gateway timeout' -or
        $Text -match '(?i)connection' -or
        $Text -match '(?i)socket' -or
        $Text -match '(?i)TLS'
    )

    return $HasPermanentFailure -and -not $HasTransientFailure
}


# ============================================================================
# BUILD COMMAND
# ============================================================================

$OutputPath = [IO.Path]::TrimEndingDirectorySeparator($OutputDirectory) +
    [IO.Path]::DirectorySeparatorChar

$ExportArgs = @(
    'exportguild',
    '-g', $GuildId,
    '--parallel', [string]$Parallel,
    '-f', $Format,
    '-o', $OutputPath,
    '--resume',
    '--fuck-russia'
)

if ($DceArguments) {
    $ExportArgs += $DceArguments
}


# ============================================================================
# RUN / RETRY
# ============================================================================

$Pass = 0
$ProcessFailures = 0
$PartialFailurePasses = 0

Write-Host ''
Write-Host '========================================================================'
Write-Host 'DiscordChatExporter resilient guild export'
Write-Host '========================================================================'
Write-Host "Guild:       $GuildId"
Write-Host "Output:      $OutputDirectory"
Write-Host "Format:      $Format"
Write-Host "Parallel:    $Parallel"
Write-Host "Manifest:    $ManifestFile"
Write-Host ''

while ($true) {
    $Pass++

    Write-Log "Starting export pass $Pass."

    $ErrorFile = Join-Path $env:TEMP "DCE_Resilient_$([Guid]::NewGuid().ToString('N')).stderr.txt"

    try {
        # Keep the token out of the child process command line. The CLI natively
        # accepts DISCORD_TOKEN, so expose it only for the duration of this launch.
        $PreviousDiscordToken = $env:DISCORD_TOKEN
        try {
            $env:DISCORD_TOKEN = $Token

            # stdout stays attached directly to the terminal, preserving the native
            # Spectre.Console in-place progress display.
            & $DceExe @ExportArgs 2> $ErrorFile

            $ExitCode = $LASTEXITCODE
        }
        finally {
            if ($null -eq $PreviousDiscordToken) {
                Remove-Item Env:DISCORD_TOKEN -ErrorAction SilentlyContinue
            }
            else {
                $env:DISCORD_TOKEN = $PreviousDiscordToken
            }
        }

        $ErrorText = ''
        if (Test-Path -LiteralPath $ErrorFile) {
            $ErrorText = Get-Content -LiteralPath $ErrorFile -Raw -ErrorAction SilentlyContinue
        }

        if (-not [string]::IsNullOrWhiteSpace($ErrorText)) {
            Write-Host ''
            Write-Host $ErrorText.TrimEnd()
            Add-Content -LiteralPath $LogFile -Value $ErrorText.TrimEnd() -Encoding UTF8
        }

        if (Test-AuthenticationFailure -Text $ErrorText) {
            Write-Log 'Authentication failed. Stopping immediately.'
            throw 'Discord authentication failed.'
        }

        $HasPartialChannelFailures = Test-PartialChannelFailure -Text $ErrorText

        if ($ExitCode -eq 0 -and -not $HasPartialChannelFailures) {
            Write-Log "Export completed successfully after $Pass pass(es)."

            Write-Host ''
            Write-Host '========================================================================'
            Write-Host 'DONE'
            Write-Host '========================================================================'
            Write-Host "Manifest: $ManifestFile"
            Write-Host "Log:      $LogFile"
            Write-Host ''

            break
        }

        if ($ExitCode -eq 0 -and $HasPartialChannelFailures) {
            $PartialFailurePasses++

            if (Test-PermanentOnlyChannelFailure -Text $ErrorText) {
                Write-Log 'Remaining channel failures appear permanent; not retrying them automatically.'

                Write-Host ''
                Write-Host 'Completed channels are safely checkpointed in manifest.json.'
                Write-Host 'The remaining failures look like permission/not-found errors, so this wrapper will not repeatedly request them.'
                Write-Host ''

                break
            }

            if ($PartialFailurePasses -ge $MaxPartialFailurePasses) {
                Write-Log "Some channels are still failing after $PartialFailurePasses retry pass(es)."

                Write-Host ''
                Write-Host 'Completed channels are safely checkpointed in manifest.json.'
                Write-Host 'Run this script again later to retry only the unfinished channels.'
                Write-Host ''

                break
            }

            $Delay = Get-RetryDelay -Attempt $PartialFailurePasses

            Write-Log "Pass $Pass completed with channel-level failures; retrying unfinished channels only."
            Write-Host "Retrying unfinished channels in $Delay seconds..."
            Start-Sleep -Seconds $Delay
            continue
        }

        $ProcessFailures++

        if ($ProcessFailures -ge $MaxProcessFailureAttempts) {
            Write-Log "Process-level failure persisted for $ProcessFailures attempt(s); stopping."
            throw "DiscordChatExporter exited with code $ExitCode after repeated failures."
        }

        $Delay = Get-RetryDelay -Attempt $ProcessFailures

        Write-Log "DiscordChatExporter exited with code $ExitCode."
        Write-Host "Retrying from the manifest checkpoint in $Delay seconds..."
        Start-Sleep -Seconds $Delay
    }
    finally {
        Remove-Item -LiteralPath $ErrorFile -Force -ErrorAction SilentlyContinue
    }
}
