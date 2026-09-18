# Export-Guild-Resilient.ps1
#
# Resilient whole-guild wrapper for DiscordChatExporter.Cli.
#
# Exports channels one at a time, keeps persistent completion state,
# retries transient/truncated-response failures, remembers 403/404 skips,
# and redraws percentage updates on one console line.
#
# Example:
#   .\Export-Guild-Resilient.ps1 -Token $env:DCE_TOKEN -GuildId 123456789012345678 -OutputDirectory "C:\Discord Exports\My Server" -Format Csv

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Token,

    [Parameter(Mandatory)]
    [string]$GuildId,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [string]$DceExe,

    [ValidateSet('HtmlDark', 'HtmlLight', 'PlainText', 'Csv', 'Json')]
    [string]$Format = 'Csv',

    [int]$MaxRetryDelaySeconds = 30,

    [int]$MaxUnknownErrorAttempts = 5,

    [ConsoleColor]$ProgressColor = [ConsoleColor]::Cyan,

    [switch]$ResetState
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
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1

    if (-not $DceExe) {
        $Command = Get-Command 'DiscordChatExporter.Cli.exe' -ErrorAction SilentlyContinue
        if ($Command) {
            $DceExe = $Command.Source
        }
    }
}

if (-not $DceExe -or -not (Test-Path -LiteralPath $DceExe)) {
    throw 'DiscordChatExporter.Cli.exe was not found. Pass its path with -DceExe.'
}


# ============================================================================
# STATE
# ============================================================================

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$StateFile = Join-Path $OutputDirectory "_completed_channels_$GuildId.txt"
$SkippedFile = Join-Path $OutputDirectory "_skipped_channels_$GuildId.txt"
$LogFile = Join-Path $OutputDirectory "_export_retry_log_$GuildId.txt"

if ($ResetState) {
    Remove-Item -LiteralPath $StateFile, $SkippedFile -Force -ErrorAction SilentlyContinue
}

$script:ProgressLineActive = $false
$script:ProgressLineWidth = 0


function Finish-LiveProgressLine {
    if ($script:ProgressLineActive) {
        Write-Host ''
        $script:ProgressLineActive = $false
        $script:ProgressLineWidth = 0
    }
}


function Write-NormalLine {
    param([Parameter(Mandatory)][string]$Text)

    Finish-LiveProgressLine
    Write-Host $Text
}


function Write-LiveProgressLine {
    param([Parameter(Mandatory)][string]$Text)

    $PaddingLength = [Math]::Max(0, $script:ProgressLineWidth - $Text.Length)
    $Padding = if ($PaddingLength -gt 0) { ' ' * $PaddingLength } else { '' }

    $CarriageReturn = [char]13
    Write-Host -NoNewline -ForegroundColor $ProgressColor "$CarriageReturn$Text$Padding"

    $script:ProgressLineWidth = $Text.Length
    $script:ProgressLineActive = $true
}


function Write-Log {
    param([Parameter(Mandatory)][string]$Message)

    Finish-LiveProgressLine

    $Timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $Line = "[$Timestamp] $Message"

    Write-Host $Line
    Add-Content -LiteralPath $LogFile -Value $Line -Encoding UTF8
}


function Get-RetryDelay {
    param([Parameter(Mandatory)][int]$Attempt)

    $Exponent = [Math]::Min($Attempt, 5)
    $Delay = [Math]::Pow(2, $Exponent)

    return [int][Math]::Min($MaxRetryDelaySeconds, $Delay)
}


function Add-SkippedChannel {
    param(
        [Parameter(Mandatory)][string]$ChannelId,
        [Parameter(Mandatory)][string]$ChannelName,
        [Parameter(Mandatory)][string]$Reason
    )

    $Timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $Tab = [char]9
    $Line = "$ChannelId$Tab$ChannelName$Tab$Reason$Tab$Timestamp"
    Add-Content -LiteralPath $SkippedFile -Value $Line -Encoding UTF8
}


$Completed = [System.Collections.Generic.HashSet[string]]::new()

if (Test-Path -LiteralPath $StateFile) {
    foreach ($Line in Get-Content -LiteralPath $StateFile) {
        $Id = $Line.Trim()
        if ($Id) {
            [void]$Completed.Add($Id)
        }
    }
}


$PermanentSkipped = [System.Collections.Generic.HashSet[string]]::new()

if (Test-Path -LiteralPath $SkippedFile) {
    foreach ($Line in Get-Content -LiteralPath $SkippedFile) {
        if ([string]::IsNullOrWhiteSpace($Line)) {
            continue
        }

        $Parts = $Line -split ([char]9), 4
        if ($Parts.Count -lt 3) {
            continue
        }

        if ($Parts[2] -in @('403 Forbidden', '404 Not Found')) {
            [void]$PermanentSkipped.Add($Parts[0].Trim())
        }
    }
}


# ============================================================================
# CHANNEL LIST
# ============================================================================

Write-Host ''
Write-Host 'Fetching channel list...'
Write-Host ''

$ChannelOutput = & $DceExe channels -t $Token -g $GuildId 2>&1
$ChannelListExitCode = $LASTEXITCODE

if ($ChannelListExitCode -ne 0) {
    $ChannelOutput | ForEach-Object { Write-Host ([string]$_) }
    throw "Failed to retrieve channel list. Exit code: $ChannelListExitCode"
}

$Channels = foreach ($Line in $ChannelOutput) {
    $Text = [string]$Line

    if ($Text -match '^\s*(\d+)\s+\|\s+(.+?)\s*$') {
        [PSCustomObject]@{
            Id = $Matches[1]
            Name = $Matches[2].Trim()
        }
    }
}

if (-not $Channels) {
    throw 'No channels could be parsed from DiscordChatExporter output.'
}

Write-Host "Found $($Channels.Count) channels."
Write-Host "Already completed: $($Completed.Count)"
Write-Host "Permanent skips: $($PermanentSkipped.Count)"
Write-Host ''


# ============================================================================
# EXPORT
# ============================================================================

$ChannelNumber = 0
$SuccessfulThisRun = 0
$SkippedThisRun = 0

foreach ($Channel in $Channels) {
    $ChannelNumber++

    $ChannelId = $Channel.Id
    $ChannelName = $Channel.Name

    Finish-LiveProgressLine

    Write-Host ''
    Write-Host '========================================================================'
    Write-Host "[$ChannelNumber/$($Channels.Count)] $ChannelName"
    Write-Host "Channel ID: $ChannelId"
    Write-Host '========================================================================'

    if ($Completed.Contains($ChannelId)) {
        Write-Host 'Already completed - skipping.'
        continue
    }

    if ($PermanentSkipped.Contains($ChannelId)) {
        Write-Host 'Previously marked 403/404 - skipping.'
        continue
    }

    $Attempt = 0

    while ($true) {
        $Attempt++

        Write-Log "Starting '$ChannelName' [$ChannelId] - attempt $Attempt"

        $ExportArgs = @(
            'export',
            '-t', $Token,
            '-c', $ChannelId,
            '-f', $Format,
            '-o', "$OutputDirectory\",
            '--fuck-russia'
        )

        $OutputLines = [System.Collections.Generic.List[string]]::new()

        & $DceExe @ExportArgs 2>&1 |
            ForEach-Object {
                $Text = [string]$_
                [void]$OutputLines.Add($Text)

                if ($Text -match ':\s*(\d{1,3})%\s*$') {
                    Write-LiveProgressLine -Text $Text
                }
                else {
                    Write-NormalLine -Text $Text
                    Add-Content -LiteralPath $LogFile -Value $Text -Encoding UTF8
                }
            }

        $ExitCode = $LASTEXITCODE
        Finish-LiveProgressLine

        $OutputText = $OutputLines -join [Environment]::NewLine

        if ($ExitCode -eq 0) {
            Write-Log "SUCCESS: '$ChannelName' [$ChannelId]"

            [void]$Completed.Add($ChannelId)
            Add-Content -LiteralPath $StateFile -Value $ChannelId -Encoding ASCII

            $SuccessfulThisRun++
            break
        }

        if (
            $OutputText -match '(?i)authentication token is invalid' -or
            $OutputText -match '(?i)unauthorized'
        ) {
            Write-Log "FATAL: Authentication failed while exporting '$ChannelName' [$ChannelId]."
            throw 'Discord authentication failed. Stopping the export.'
        }

        if (
            $OutputText -match '(?i)failed:\s*forbidden' -or
            $OutputText -match '(?i)403\s+forbidden'
        ) {
            Write-Log "SKIPPED: '$ChannelName' [$ChannelId] - 403 Forbidden"
            Add-SkippedChannel -ChannelId $ChannelId -ChannelName $ChannelName -Reason '403 Forbidden'
            [void]$PermanentSkipped.Add($ChannelId)
            $SkippedThisRun++
            break
        }

        if (
            $OutputText -match '(?i)failed:\s*not found' -or
            $OutputText -match '(?i)404\s+not found'
        ) {
            Write-Log "SKIPPED: '$ChannelName' [$ChannelId] - 404 Not Found"
            Add-SkippedChannel -ChannelId $ChannelId -ChannelName $ChannelName -Reason '404 Not Found'
            [void]$PermanentSkipped.Add($ChannelId)
            $SkippedThisRun++
            break
        }

        $IsJsonTruncation = (
            $OutputText -match '(?i)JsonReaderException' -or
            $OutputText -match '(?i)JsonException' -or
            $OutputText -match '(?i)reached end of data' -or
            $OutputText -match '(?i)expected end of string' -or
            $OutputText -match '(?i)malformed or truncated JSON'
        )

        if ($IsJsonTruncation) {
            Write-Log "TRANSIENT JSON FAILURE: '$ChannelName' [$ChannelId]"

            $RetryDelay = Get-RetryDelay -Attempt $Attempt
            Write-Host "Retrying this channel in $RetryDelay seconds..."
            Start-Sleep -Seconds $RetryDelay
            continue
        }

        $IsTransientNetworkFailure = (
            $OutputText -match '(?i)too many requests' -or
            $OutputText -match '(?i)rate.?limit' -or
            $OutputText -match '(?i)request timeout' -or
            $OutputText -match '(?i)timed out' -or
            $OutputText -match '(?i)connection.*(?:closed|reset|aborted)' -or
            $OutputText -match '(?i)socketexception' -or
            $OutputText -match '(?i)httprequestexception' -or
            $OutputText -match '(?i)httpcloakexception' -or
            $OutputText -match '(?i)bad gateway' -or
            $OutputText -match '(?i)service unavailable' -or
            $OutputText -match '(?i)gateway timeout' -or
            $OutputText -match '(?i)cloudflare'
        )

        if ($IsTransientNetworkFailure) {
            Write-Log "TRANSIENT NETWORK FAILURE: '$ChannelName' [$ChannelId]"

            $RetryDelay = Get-RetryDelay -Attempt $Attempt
            Write-Host "Retrying this channel in $RetryDelay seconds..."
            Start-Sleep -Seconds $RetryDelay
            continue
        }

        Write-Log "UNKNOWN FAILURE: '$ChannelName' [$ChannelId] - exit code $ExitCode"

        if ($Attempt -ge $MaxUnknownErrorAttempts) {
            Write-Log "SKIPPED: '$ChannelName' [$ChannelId] after $Attempt unknown failures"
            Add-SkippedChannel -ChannelId $ChannelId -ChannelName $ChannelName -Reason "Unknown failure after $Attempt attempts"

            $SkippedThisRun++
            break
        }

        $RetryDelay = Get-RetryDelay -Attempt $Attempt
        Write-Host "Unknown failure. Retrying in $RetryDelay seconds..."
        Start-Sleep -Seconds $RetryDelay
    }
}


# ============================================================================
# SUMMARY
# ============================================================================

Finish-LiveProgressLine

Write-Host ''
Write-Host '========================================================================'
Write-Host 'DONE'
Write-Host '========================================================================'
Write-Host ''
Write-Host "Total channels:          $($Channels.Count)"
Write-Host "Completed total:         $($Completed.Count)"
Write-Host "Completed this run:      $SuccessfulThisRun"
Write-Host "Permanent skips:         $($PermanentSkipped.Count)"
Write-Host "Skipped this run:        $SkippedThisRun"
Write-Host ''
Write-Host "Exports:                 $OutputDirectory"
Write-Host "Completed state:         $StateFile"
Write-Host "Skipped channels:        $SkippedFile"
Write-Host "Full log:                $LogFile"
Write-Host ''
