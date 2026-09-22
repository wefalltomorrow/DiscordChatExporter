# DiscordChatExporter

[![Build](https://img.shields.io/github/actions/workflow/status/wefalltomorrow/DiscordChatExporter/main.yml?branch=prime)](https://github.com/wefalltomorrow/DiscordChatExporter/actions)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](License.txt)

My fork of **DiscordChatExporter**. I mainly use it for long server exports, so most of my changes are around making exports easier to resume, cutting out unnecessary Discord API requests, and adding better archive/search tools.

It stays close to upstream where it makes sense, with selected fixes and features pulled in from other community forks.

<p align="center">
    <img src="favicon.png" alt="DiscordChatExporter" />
</p>

> [!WARNING]
> DiscordChatExporter can use either a user token or a bot token. Discord does not officially support automating normal user accounts, so use a bot where that works for your server. The changes in this fork make user-token exports slower and much less aggressive with the API, but they cannot guarantee an account will never be restricted.

## Main changes

### More reliable long exports

- Resume JSON, HTML, CSV and SQLite exports instead of starting over.
- Whole-server checkpointing with `manifest.json`.
- Completed channels are skipped on the next run.
- A failed channel does not kill the rest of the server export.
- Malformed/truncated Discord JSON retries the exact request instead of restarting the whole channel.
- Retry handling is bounded so a broken request cannot loop forever.
- Optional resilient PowerShell wrapper for very long exports.

### Conservative Discord API usage

User-token exports are deliberately slower than upstream:

- One user-token Discord API request in flight at a time.
- At least 750 ms between user-token request starts.
- Discord advisory rate-limit headers are always respected.
- HTTP 429 responses use Discord's own `retry_after`, then add extra cooldown time if rate limits keep happening.
- Rate-limit pauses apply across the whole export, not just one channel worker.
- 403/404 results are remembered for the rest of the run so the same unavailable resource is not requested again.
- Guild channels, roles, members and channel/thread metadata are cached across the export.
- Duplicate metadata lookups are collapsed into one request.
- The run stops if it hits an abnormal burst of 401/403/429 responses instead of blindly continuing.
- The final summary shows API request count, 429s, advisory pauses, avoided requests and cache hits.

For my use case, I would rather an export take longer than send a pile of unnecessary requests.

### Better archive/search tools

- SQLite (`.db`) export format with full-text search.
- Searchable library across previous SQLite exports.
- Offline conversion from DiscordChatExporter JSON to HTML Dark, HTML Light, TXT, CSV or SQLite.
- Streaming/atomic continuation merges.
- CSV formula neutralization.
- Asset/media reuse and download deduplication.

### Windows user-token compatibility

On Windows, user-token requests can use the HttpCloak browser/TLS transport together with a Discord-web-style request profile. This includes matching browser headers, locale/timezone data, session identifiers and a current Discord web build number where available.

Bot tokens stay on the normal managed HTTP path. Linux/macOS also use managed HTTP.

If the Windows browser transport causes trouble, disable it with:

```powershell
$env:DISCORDCHATEXPORTER_DISABLE_BROWSER_TRANSPORT = '1'
```

## CLI quick start

For a long whole-server export, I recommend keeping the token out of the command line and using `--resume`:

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

`--parallel 1` is a good default for a single-server archival run. User-token Discord requests are serialized internally anyway, so raising channel parallelism does not create simultaneous user-token API requests.

For very long exports, `scripts/Export-Guild-Resilient.ps1` wraps the same `exportguild --resume` flow with bounded process-level retries.

## Formats

- HTML Dark
- HTML Light
- Plain Text (TXT)
- CSV
- JSON
- SQLite (DB)

## Downloads

Grab the latest normal build from:

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

See [the documentation](.docs/Readme.md) for GUI, CLI, filtering, scheduling, continuation, library and conversion usage.

## Credits

The original **DiscordChatExporter** is by [Oleksii Holub (Tyrrrz)](https://github.com/Tyrrrz/DiscordChatExporter) and its contributors.

This fork also uses or adapts work from:

- [arandomhooman/DiscordChatExporter](https://github.com/arandomhooman/DiscordChatExporter) — resume/continuation, SQLite, library/search, conversion and reliability work.
- [nulldg/DiscordChatExporterPlus](https://github.com/nulldg/DiscordChatExporterPlus) — HttpCloak browser/TLS transport work.
- [edelkas/DiscordChatExporter](https://github.com/edelkas/DiscordChatExporter) — caching ideas and other export improvements that were selectively reviewed/ported.
- [Tyrrrz/DiscordChatExporter PR #1582](https://github.com/Tyrrrz/DiscordChatExporter/pull/1582) and the earlier client-properties work it builds on.

See [NOTICE](NOTICE) and [License.txt](License.txt).
