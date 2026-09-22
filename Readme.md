# DiscordChatExporter — Best-of-All Fork

[![Build](https://img.shields.io/github/actions/workflow/status/wefalltomorrow/DiscordChatExporter/main.yml?branch=prime)](https://github.com/wefalltomorrow/DiscordChatExporter/actions)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](License.txt)

This fork combines the current upstream **Tyrrrz/DiscordChatExporter** codebase with the strongest
reliability, continuation, search, conversion, and user-request transport work from several community
forks, while preserving newer upstream fixes such as poll rendering, thread-starter handling, safer
HTML links, and current media/output-path fixes.

**Current fork status:** `prime` is the finished integration branch. The latest validated build passes
formatting, CLI tests, GUI tests, Docker, and all 18 Windows/Linux/macOS CLI+GUI packaging targets.

<p align="center">
    <img src="favicon.png" alt="Icon" />
</p>

**DiscordChatExporter** exports Discord channel history from direct messages, group messages, and
servers to portable files, with support for Discord markdown and rich media.

> [!WARNING]
> DiscordChatExporter can authenticate with user or bot tokens. Automating a user account may be
> restricted by Discord's terms or anti-abuse systems. A bot is preferable where it has access to
> the channels you need.

## What this fork adds

### Native resilient exporting

- **Continue/resume exports** for JSON, HTML, CSV, and SQLite.
- **Whole-server continuation** using an export catalog/manifest instead of restarting completed work.
- **Per-channel checkpointing** so interrupted multi-channel jobs can skip completed channels.
- **Retry failed channels** without rerunning successful channels.
- **Request-level malformed/truncated JSON retry**: a bad Discord response retries the exact API request
  up to five times instead of immediately restarting the channel.
- **Per-channel fault isolation** so a recoverable failure in one channel does not tear down an entire
  multi-channel run.
- **Cancellation controls, completion summaries, rate-limit status, count-backed progress, and ETA**.
- **Shared client-wide rate-limit coordination** so concurrent export workers honor the same Discord pause.
- **Serialized user-token Discord API traffic**: only 1 user-token API request may be in flight per client (bot tokens retain their separate 16-request ceiling).
- **750 ms minimum spacing between user-token API request starts**, intentionally favoring low-impact archival traffic over maximum export speed.
- **Aggressive adaptive post-429 cooldowns**: Discord's own retry interval is always honored, then an additional 5s / 15s / 30s / 60s safety cushion is added as hard rate limits repeat within a 10-minute window.
- **Earlier invalid-response circuit breaker**: 25 HTTP 401/403/429 responses within 10 minutes stops the run before a broken token/permission state can generate a large volume of failed requests.
- **Per-run unavailable-endpoint suppression**: once an exact request returns 403/404, repeated attempts at that same resource are served from the local failure cache rather than hitting Discord again.
- **Shared per-run guild/member/channel/role metadata caching** across every channel in a server export, including collapse of concurrent duplicate lookups.
- **429 retry timing from Discord's JSON `retry_after` response**, with header/exponential fallbacks.
- **Invalid-request safety circuit breaker** that stops a run after an abnormal burst of HTTP 401/403/429 responses.
- User-token requests **always respect Discord's advisory rate-limit headers**, even if advisory limits are disabled for bot-token testing.
- Export completion summaries report API request count, 429s, advisory pauses, suppressed unavailable requests, and metadata cache hits without issuing additional API calls.

### Better archives

- **SQLite (\.db) export format** with full-text search.
- **Searchable Library** across previous SQLite exports.
- **Offline conversion** from DiscordChatExporter JSON to HTML Dark, HTML Light, TXT, CSV, or SQLite
  without contacting Discord again.
- Conversion metadata preserves roles, display names, colors, channels, custom emoji, and other
  formatting information where the source export contains it.
- Streaming/atomic continuation merges, path/manifest hardening, CSV formula neutralization, and
  asset-download deduplication.

### User-token request compatibility

For user-token requests, this fork combines:

- **HttpCloak Chrome TLS/browser transport on Windows** adapted from DiscordChatExporterPlus.
- A locally generated **official-web-style X-Super-Properties** profile based on upstream PR #1582.
- Matching browser User-Agent, locale/timezone headers, session-scoped launch identifiers, and a
  best-effort current Discord web build-number lookup.
- No dependency on a third-party client-properties API.
- Bot-token requests remain on the normal HttpClient path.

These changes improve request consistency; they are not a guarantee against account restrictions. On Linux, this fork deliberately falls back to the normal managed HTTP transport because HttpCloak's in-process .NET native binding has a currently open host-crash issue under some workloads. On Windows, set `DISCORDCHATEXPORTER_DISABLE_BROWSER_TRANSPORT=1` to force the managed HTTP path for troubleshooting or compatibility.

### Optional resilient PowerShell wrapper

`scripts/Export-Guild-Resilient.ps1` wraps the native `exportguild --resume` workflow. It preserves the
CLI's normal in-place progress display, uses `manifest.json` checkpoints to skip verified completed
channels, automatically retries unfinished channels, and avoids the old per-channel `/channels/{id}`
resolution problem. Automatic partial-failure retries are intentionally bounded, and clearly permanent
permission/not-found failures are not hammered repeatedly. The wrapper passes the token to the CLI through
`DISCORD_TOKEN` rather than placing it in the child process command line.

### Recommended whole-server archival workflow

For a single-server export, use the environment variable for the token and native resume/checkpointing:

```powershell
$env:DISCORD_TOKEN = "your-token"
.\DiscordChatExporter.Cli.exe exportguild -g 123456789012345678 --resume -f Json -o "C:\Discord Exports"
```

User-token Discord API traffic is serialized internally, so increasing channel-level `--parallel`
does not create simultaneous user-token API requests. The exporter intentionally favors quiet,
low-impact archival traffic over maximum throughput.

For especially long exports, `scripts/Export-Guild-Resilient.ps1` adds bounded process-level retries
around the same native `exportguild --resume` workflow.

### Keeping tokens out of command arguments

The CLI supports the `DISCORD_TOKEN` environment variable. This keeps the token out of the process
command line, and the resilient PowerShell wrapper uses the same mechanism for child processes.

The `-t|--token` option remains supported for compatibility.

## Formats

- HTML Dark
- HTML Light
- Plain Text (TXT)
- CSV
- JSON
- SQLite (DB)

## Download

### Latest validated builds

Every successful `prime` build publishes self-contained CLI and GUI artifacts for:

- Windows: x64, x86, ARM64
- Linux: x64, musl-x64, ARM, ARM64
- macOS: Intel x64, Apple Silicon ARM64

Get the newest validated artifacts from the latest successful **main** workflow run:

**https://github.com/wefalltomorrow/DiscordChatExporter/actions/workflows/main.yml?query=branch%3Aprime**

GitHub Actions artifacts are retained by GitHub for a limited period.

### Tagged releases

Formal tagged releases, when published, appear at:

**https://github.com/wefalltomorrow/DiscordChatExporter/releases**

Community package-manager packages that use the `Tyrrrz` package IDs still track upstream, not this fork.

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

See [the documentation](.docs/Readme.md) for GUI, CLI, filtering, scheduling, continuation, library,
and conversion usage.

## Credits

The original application is **DiscordChatExporter** by
[Oleksii Holub (Tyrrrz)](https://github.com/Tyrrrz/DiscordChatExporter) and its contributors.

This fork also incorporates/adapts work from:

- [arandomhooman/DiscordChatExporter](https://github.com/arandomhooman/DiscordChatExporter) —
  continuation/resume, SQLite, library/search, conversion, resilient batch exporting, progress/ETA,
  and extensive reliability/hardening work.
- [nulldg/DiscordChatExporterPlus](https://github.com/nulldg/DiscordChatExporterPlus) — HttpCloak
  browser/TLS transport work.
- [edelkas/DiscordChatExporter](https://github.com/edelkas/DiscordChatExporter) — inspiration/reference
  for shared export metadata caching and additional archival/export improvements selectively reviewed
  for this fork.
- [Tyrrrz/DiscordChatExporter PR #1582](https://github.com/Tyrrrz/DiscordChatExporter/pull/1582) and
  the earlier client-properties work it builds on.

See [NOTICE](NOTICE) and [License.txt](License.txt).
