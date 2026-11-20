# DiscordChatExporter

[![Build](https://img.shields.io/github/actions/workflow/status/wefalltomorrow/DiscordChatExporter/main.yml?branch=prime)](https://github.com/wefalltomorrow/DiscordChatExporter/actions)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](License.txt)

Maintained fork of **DiscordChatExporter** focused on resilient long-running exports, explicit Discord API request handling, and improved archive/search tooling.

The fork remains close to upstream while selectively incorporating useful changes from other community forks.

<p align="center">
    <img src="favicon.png" alt="DiscordChatExporter" />
</p>

> [!WARNING]
> Discord prohibits automating normal user accounts ("self-bots") and warns that accounts found doing so may be terminated. Its Developer Policy also bars scraping data through Discord services. This fork's user-token export mode automates a normal account. There is no credible evidence that this fork prevents account suspension or other enforcement, so no such protection is claimed. Request scheduling, rate-limit handling, retries, and transport choices do not make this use permitted or establish account safety. Do not use user-token mode if avoiding account enforcement is important. See Discord's [self-bot policy](https://support.discord.com/hc/en-us/articles/115002192352-Automated-User-Accounts-Self-Bots) and [Developer Policy](https://support-dev.discord.com/hc/en-us/articles/8563934450327-Discord-Developer-Policy).

## Key improvements

### Resilient exporting

- Resume JSON, HTML, CSV, and SQLite exports.
- Whole-server checkpointing through `manifest.json`.
- Manifest v2 stores the original export settings required for safe resume and continuation.
- Resume restores recorded settings before verifying or rebuilding existing channel archives.
- Skip channels that were already completed on a previous run.
- Incrementally update completed exports with messages posted since the last export.
- Combine `--incremental --resume` to repair incomplete archives and fetch newer messages in one pass.
- Isolate per-channel failures so one recoverable error does not stop the full server export.
- Retry malformed or truncated Discord JSON at the request level instead of restarting an entire channel.
- Bound transient retries to avoid uncontrolled retry loops.
- Optional resilient PowerShell wrapper for long-running exports.

### Discord API request handling

For user-token requests, this client applies the scheduling and stop behavior below. These are request-handling controls; they do not make user-token automation permitted or establish account safety:

- One user-token Discord API request in flight at a time.
- Minimum 750 ms interval between user-token request starts.
- Advisory rate-limit headers are always respected for user tokens.
- The first HTTP 429 from a confirmed user-token request immediately stops this client; it is not retried, and queued requests are rejected. A local marker keyed by a one-way fingerprint of that token blocks future runs of this fork using the same token under the same OS user profile. Bot-token 429 responses use the retry and cooldown policy.
- The marker is stored in that OS user's application-data directory. It is local stop state, not an account-wide enforcement signal, and does not establish account safety. If a 429 occurs while the client is still determining whether a token is a user or bot token, only the current client is stopped because the token type is not confirmed.
- On Windows, the marker directory is %LOCALAPPDATA%\DiscordChatExporter\UserTokenRequestStops. To manually clear all locally stored 429 stops, close every running instance and remove that directory; this allows those tokens to be used again.
- Bot-token HTTP 429 handling prefers Discord's `retry_after` value and applies progressively larger local cooldowns when rate limits repeat.
- Rate-limit pauses are coordinated across the client.
- Refreshing the GUI with the same token preserves request stops, cooldowns, and cached request history. Rate-limit preference changes apply without resetting the client. Switching credentials starts a new client; a 429 stop for the same token remains in local application data across restarts.
- Exact 403/404 requests are cached for the remainder of the run to avoid repeated unavailable lookups.
- Guild channels, roles, members, and channel/thread metadata are cached across the export.
- Concurrent duplicate metadata lookups are collapsed into a single request.
- A local circuit breaker stops further requests from the client after an abnormal burst of HTTP 401/403/429 responses.
- User-token requests retain their request slot through retries and cooldowns so queued requests cannot bypass a newly received limit. Server-requested advisory waits are honored in full, including waits longer than 60 seconds.
- Exhausting bot-token HTTP 429 retries stops subsequent requests from that client. Retry delays also support HTTP-date headers and non-object JSON error bodies.
- The CLI exits with code **3** when requests have been stopped. Automation must not restart it automatically after this code. The resilient PowerShell wrapper respects this exit code and recognizes restriction/rate-limit errors from older builds, while retaining retries for ordinary connection failures.
- An explicitly unusable rate-limit duration stops the client instead of falling back to a short retry. This includes negative/nonfinite values, missing reset timing when an advisory bucket is exhausted, and waits beyond the supported timer range. Existing waiters honor cooldown extensions from other in-flight requests.
- Account verification required (40002), Cloudflare blocking (40333), or lost authentication also stop further requests from the client, including queued channel requests. Review the account or access issue before starting another export. Ordinary missing channel permissions remain scoped to that channel. These stops respond to errors; they do not guarantee prevention of account detection or restrictions.
- Export summaries report API request counts, 429s, advisory pauses, avoided unavailable requests, and metadata cache hits.

### Archive and search tools

- SQLite (`.db`) export format with full-text search.
- Searchable library across previous SQLite exports.
- Offline conversion from DiscordChatExporter JSON to HTML Dark, HTML Light, TXT, CSV, or SQLite.
- Streaming/atomic continuation merges.
- CSV formula neutralization.
- Asset/media reuse and download deduplication.

### Windows user-token compatibility

On Windows, user-token requests can use an alternate HTTP/TLS transport for compatibility. It does not make automated user-token requests compliant or protect accounts from Discord enforcement.

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
    --resume

Remove-Item Env:DISCORD_TOKEN -ErrorAction SilentlyContinue
```

`--parallel` controls channel-level work and local resource use. User-token Discord API requests are serialized internally and spaced apart regardless of this setting; changing parallelism does not make user-token automation permitted or establish lower account-enforcement risk.

For manifest v2 archives, `--resume` restores the recorded format and continuation-sensitive settings
for existing channel outputs before verifying or rebuilding them. This prevents a later resume command
from silently changing media, filter, markdown, date-range, partition, locale, or UTC behavior.

To update the completed archive later without re-exporting its full history, run the same command
with `--incremental` instead of `--resume`:

```powershell
& '.\DiscordChatExporter.Cli.exe' `
    exportguild `
    -g 123456789012345678 `
    -f Csv `
    -o 'C:\Discord Export\' `
    --incremental `
    --parallel 1
```

Incremental mode restores the original continuation-sensitive settings from `manifest.json` and
appends only newer messages. See [Using the CLI](.docs/Using-the-CLI.md) for details.

To repair interrupted channel archives while also fetching messages posted since the last export, use
both flags together. Incomplete archives continue from the last message actually stored; completed
archives receive newer messages, and previously unexported channels are exported normally:

```powershell
& ".\DiscordChatExporter.Cli.exe" `
    exportguild `
    -g 123456789012345678 `
    -f Csv `
    -o "C:\Discord Export\" `
    --incremental `
    --resume `
    --parallel 1
```

New exports record media directories inside the export folder as relative paths. Move the archive,
its media folder, and `manifest.json` together to keep resume and incremental exports portable.
Incremental media links are relative to the final archive, including when temporary files are used.
External media directories and absolute paths recorded by older versions retain their original locations.

For very long exports, `scripts/Export-Guild-Resilient.ps1` wraps the same `exportguild --resume` workflow with bounded process-level retries.

## Reporting bugs

Use [GitHub Issues](https://github.com/wefalltomorrow/DiscordChatExporter/issues) for bug reports and
feature requests. Include the application version, CLI/GUI mode, operating system, export format,
relevant command/options, and any error output that does not contain credentials.

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
