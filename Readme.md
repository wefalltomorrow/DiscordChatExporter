# DiscordChatExporter

[![Build](https://img.shields.io/github/actions/workflow/status/wefalltomorrow/DiscordChatExporter/main.yml?branch=prime)](https://github.com/wefalltomorrow/DiscordChatExporter/actions)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](License.txt)

Maintained fork of **DiscordChatExporter** focused on resilient long-running exports, conservative Discord API usage, and improved archive/search tooling.

The fork remains close to upstream while selectively incorporating useful changes from other community forks.

<p align="center">
    <img src="favicon.png" alt="DiscordChatExporter" />
</p>

> [!WARNING]
> DiscordChatExporter supports both user and bot tokens. Automating regular user accounts may be restricted by Discord's terms or enforcement systems. Bot authentication is preferable where available. The request controls in this fork reduce API load, bursts, and unnecessary retries, but do not guarantee against account restrictions.

## Key improvements

### Resilient exporting

- Resume JSON, HTML, CSV, and SQLite exports.
- Whole-server checkpointing through `manifest.json`.
- Skip channels that were already completed on a previous run.
- Isolate per-channel failures so one recoverable error does not stop the full server export.
- Retry malformed or truncated Discord JSON at the request level instead of restarting an entire channel.
- Bound transient retries to avoid uncontrolled retry loops.
- Optional resilient PowerShell wrapper for long-running exports.

### Conservative Discord API behavior

User-token exports are intentionally paced more conservatively than upstream:

- One user-token Discord API request in flight at a time.
- Minimum 750 ms interval between user-token request starts.
- Advisory rate-limit headers are always respected for user tokens.
- HTTP 429 handling prefers Discord's `retry_after` value and applies progressively larger local cooldowns when rate limits repeat.
- Rate-limit pauses are coordinated across the client.
- Exact 403/404 requests are cached for the remainder of the run to avoid repeated unavailable lookups.
- Guild channels, roles, members, and channel/thread metadata are cached across the export.
- Concurrent duplicate metadata lookups are collapsed into a single request.
- A local circuit breaker stops the run after an abnormal burst of HTTP 401/403/429 responses.
- Export summaries report API request counts, 429s, advisory pauses, avoided unavailable requests, and metadata cache hits.

### Archive and search tools

- SQLite (`.db`) export format with full-text search.
- Searchable library across previous SQLite exports.
- Offline conversion from DiscordChatExporter JSON to HTML Dark, HTML Light, TXT, CSV, or SQLite.
- Streaming/atomic continuation merges.
- CSV formula neutralization.
- Asset/media reuse and download deduplication.

### Windows user-token compatibility

On Windows, user-token requests can use the HttpCloak browser/TLS transport together with a Discord-web-style request profile. This includes compatible browser headers, locale/timezone data, session identifiers, and a best-effort current Discord web build number.

Bot tokens use the normal managed HTTP path. Linux and macOS also use managed HTTP.

To disable the Windows browser transport for troubleshooting:

```powershell
$env:DISCORDCHATEXPORTER_DISABLE_BROWSER_TRANSPORT = '1'
```

## CLI usage

For long whole-server exports, use `DISCORD_TOKEN` to keep the token out of the process command line and enable `--resume` for checkpointing:

```powershell
$SecureToken = Read-Host "Discord token" -AsSecureString
$env:DISCORD_TOKEN = [System.Net.NetworkCredential]::new('', $SecureToken).Password

& '.\DiscordChatExporter.Cli.exe' `
    exportguild `
    -g 123456789012345678 `
    -f Csv `
    -o 'C:\Discord Export\' `
    --resume `
    --parallel 1

Remove-Item Env:DISCORD_TOKEN -ErrorAction SilentlyContinue
```

`--parallel 1` is the recommended setting for a conservative single-server archival run. User-token Discord requests are serialized internally, so higher channel-level parallelism does not create simultaneous user-token API requests.

For very long exports, `scripts/Export-Guild-Resilient.ps1` wraps the same `exportguild --resume` workflow with bounded process-level retries.

## Formats

- HTML Dark
- HTML Light
- Plain Text (TXT)
- CSV
- JSON
- SQLite (DB)

## Downloads

The latest release is available from:

**https://github.com/wefalltomorrow/DiscordChatExporter/releases/latest**

Releases include self-contained CLI and GUI builds for:

- Windows: x64, x86, ARM64
- Linux: x64, musl-x64, ARM, ARM64
- macOS: Intel x64, Apple Silicon ARM64

Development builds from `prime` are also available as GitHub Actions artifacts:

**https://github.com/wefalltomorrow/DiscordChatExporter/actions/workflows/main.yml?query=branch%3Aprime**

## Build from source

Requires the **.NET 10 SDK**.

```bash
git clone https://github.com/wefalltomorrow/DiscordChatExporter.git
cd DiscordChatExporter

# GUI
dotnet run --project DiscordChatExporter.Gui -c Release

# CLI
dotnet run --project DiscordChatExporter.Cli -c Release -- --help

# Self-contained Windows x64 GUI
dotnet publish DiscordChatExporter.Gui -c Release -r win-x64 --self-contained -o ./publish
```

## Documentation

See [the documentation](.docs/Readme.md) for GUI, CLI, filtering, scheduling, continuation, library, and conversion usage.

## Credits

The original **DiscordChatExporter** is developed by [Oleksii Holub (Tyrrrz)](https://github.com/Tyrrrz/DiscordChatExporter) and its contributors.

This fork also incorporates or adapts work from:

- [arandomhooman/DiscordChatExporter](https://github.com/arandomhooman/DiscordChatExporter) — continuation/resume, SQLite, library/search, conversion, and reliability improvements.
- [nulldg/DiscordChatExporterPlus](https://github.com/nulldg/DiscordChatExporterPlus) — HttpCloak browser/TLS transport.
- [edelkas/DiscordChatExporter](https://github.com/edelkas/DiscordChatExporter) — caching and export improvements selectively reviewed and ported.
- [Tyrrrz/DiscordChatExporter PR #1582](https://github.com/Tyrrrz/DiscordChatExporter/pull/1582) and the earlier client-properties work it builds on.

See [NOTICE](NOTICE) and [License.txt](License.txt).
