# Offline process-level checks. The fake CLI never makes network requests.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$Wrapper = Join-Path $PSScriptRoot '../Export-Guild-Resilient.ps1'
$TestRoot = Join-Path ([IO.Path]::GetTempPath()) ('DceWrapperTests_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $TestRoot | Out-Null
$Shell = (Get-Process -Id $PID).Path
$Cases = @(
    @{ Name = 'stop-exit-code'; Code = 3; Text = ''; Calls = 1; Fails = $true },
    @{ Name = 'verification'; Code = 1; Text = 'Discord requires account verification (code 40002).'; Calls = 1; Fails = $true },
    @{ Name = 'blocked'; Code = 1; Text = 'Discord reports that Cloudflare blocked the request (code 40333).'; Calls = 1; Fails = $true },
    @{ Name = 'circuit'; Code = 1; Text = 'Safety circuit breaker stopped Discord requests.'; Calls = 1; Fails = $true },
    @{ Name = 'exhausted'; Code = 1; Text = 'Discord rate-limit retries were exhausted. Further requests from this client have been stopped.'; Calls = 1; Fails = $true },
    @{ Name = 'authentication'; Code = 1; Text = 'Authentication token is invalid.'; Calls = 1; Fails = $true },
    @{ Name = 'legacy-rate-limit'; Code = 0; Text = 'Export failed. Too many requests.'; Calls = 1; Fails = $true },
    @{ Name = 'invalid-timing'; Code = 3; Text = 'Discord returned an unusable rate-limit duration. Further requests from this client have been stopped.'; Calls = 1; Fails = $true },
    @{ Name = 'success'; Code = 0; Text = ''; Calls = 1; Fails = $false },
    @{ Name = 'transient'; Code = 1; Text = 'Connection timed out.'; Calls = 2; Fails = $false },
    @{ Name = 'partial'; Code = 0; Text = 'Export failed. Connection timed out.'; Calls = 2; Fails = $false }
)
$SavedEnvironment = @{}
foreach ($Name in @('DCE_WRAPPER_TEST_LOG', 'DCE_WRAPPER_TEST_CODE', 'DCE_WRAPPER_TEST_TEXT')) {
    $SavedEnvironment[$Name] = [Environment]::GetEnvironmentVariable($Name)
}
try {
    if ($IsWindows) {
        $FakeCli = Join-Path $TestRoot 'fake-cli.cmd'
        @'
@echo off
if exist "%DCE_WRAPPER_TEST_LOG%" (
  echo call>>"%DCE_WRAPPER_TEST_LOG%"
  exit /b 0
)
echo call>>"%DCE_WRAPPER_TEST_LOG%"
if defined DCE_WRAPPER_TEST_TEXT echo %DCE_WRAPPER_TEST_TEXT% 1>&2
exit /b %DCE_WRAPPER_TEST_CODE%
'@ | Set-Content -LiteralPath $FakeCli -Encoding ascii
    }
    else {
        $FakeCli = Join-Path $TestRoot 'fake-cli'
        @'
#!/bin/sh
if [ -f "$DCE_WRAPPER_TEST_LOG" ]; then
  echo call >> "$DCE_WRAPPER_TEST_LOG"
  exit 0
fi
echo call >> "$DCE_WRAPPER_TEST_LOG"
printf '%s\n' "$DCE_WRAPPER_TEST_TEXT" >&2
exit "$DCE_WRAPPER_TEST_CODE"
'@ | Set-Content -LiteralPath $FakeCli -Encoding utf8NoBOM
        & chmod +x $FakeCli
        if ($LASTEXITCODE -ne 0) { throw 'Could not prepare fake CLI.' }
    }
    $Failures = @()
    foreach ($Case in $Cases) {
        $env:DCE_WRAPPER_TEST_LOG = Join-Path $TestRoot ($Case.Name + '.calls')
        $env:DCE_WRAPPER_TEST_CODE = [string]$Case.Code
        $env:DCE_WRAPPER_TEST_TEXT = $Case.Text
        $Arguments = @('-NoProfile', '-File', $Wrapper, '-Token', 'offline-placeholder', '-GuildId', '1',
            '-OutputDirectory', (Join-Path $TestRoot $Case.Name), '-DceExe', $FakeCli,
            '-MaxProcessFailureAttempts', '2', '-MaxPartialFailurePasses', '2', '-MaxRetryDelaySeconds', '1')
        $Output = & $Shell @Arguments 2>&1
        $Failed = $LASTEXITCODE -ne 0
        $Calls = @(Get-Content -LiteralPath $env:DCE_WRAPPER_TEST_LOG).Count
        if ($Calls -ne $Case.Calls -or $Failed -ne $Case.Fails) {
            $Failures += $Case.Name
            Write-Host "FAIL $($Case.Name): calls=$Calls failed=$Failed"
            Write-Host ($Output -join [Environment]::NewLine)
        }
        else { Write-Host "PASS $($Case.Name)" }
    }
    if ($Failures.Count) { throw "Failed wrapper cases: $($Failures -join ', ')" }
    Write-Host "$($Cases.Count) offline wrapper cases passed."
}
finally {
    foreach ($Name in $SavedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($Name, $SavedEnvironment[$Name])
    }
    $ResolvedTestRoot = [IO.Path]::GetFullPath($TestRoot)
    $TempPrefix = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) + [IO.Path]::DirectorySeparatorChar
    if ($ResolvedTestRoot.StartsWith($TempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($ResolvedTestRoot).StartsWith('DceWrapperTests_')) {
        Remove-Item -LiteralPath $ResolvedTestRoot -Recurse -Force
    }
}
