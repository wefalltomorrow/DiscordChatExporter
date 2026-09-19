# DiscordChatExporter — Best-of-All Fork

[![Build](https://img.shields.io/github/actions/workflow/status/wefalltomorrow/DiscordChatExporter/main.yml?branch=prime)](https://github.com/wefalltomorrow/DiscordChatExporter/actions)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](License.txt)

This fork combines the current upstream **Tyrrrz/DiscordChatExporter** codebase with the strongest
reliability, continuation, search, conversion, and user-request transport work from several community
forks, while preserving newer upstream fixes such as poll rendering, thread-starter handling, safer
HTML links, and current media/output-path fixes.

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
resolution problem.

## Formats

- HTML Dark
- HTML Light
- Plain Text (TXT)
- CSV
- JSON
- SQLite (DB)

## Download

Builds and releases for this fork are published under:

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
- [Tyrrrz/DiscordChatExporter PR #1582](https://github.com/Tyrrrz/DiscordChatExporter/pull/1582) and
  the earlier client-properties work it builds on.

See [NOTICE](NOTICE) and [License.txt](License.txt).
