# DiscordChatExporter (arandomhooman fork)

[![Build](https://img.shields.io/github/actions/workflow/status/arandomhooman/DiscordChatExporter/main.yml?branch=prime)](https://github.com/arandomhooman/DiscordChatExporter/actions)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](License.txt)

> This is a **fork** of [**Tyrrrz/DiscordChatExporter**](https://github.com/Tyrrrz/DiscordChatExporter)
> with extra features (see [Additions in this fork](#additions-in-this-fork)). All credit for the original
> application goes to [Oleksii Holub (Tyrrrz)](https://github.com/Tyrrrz) and its contributors.
> Licensed under MIT, the same as upstream.

<p align="center">
    <img src="favicon.png" alt="Icon" />
</p>

**DiscordChatExporter** can export message history from any [Discord](https://discord.com) channel to a file.
It works with direct messages, group messages, and server channels, and supports Discord's dialect of
markdown as well as most other rich media features.

> [!WARNING]
> While **DiscordChatExporter** allows it, automating user accounts is against Discord TOS and may result in
> you getting banned. If possible, use a bot to export chat logs from accessible channels.

## Additions in this fork

On top of upstream **DiscordChatExporter**, this fork adds:

- **Continue / resume exports** — re-open an existing export and fetch only the messages added since,
  merging them into the existing file in place. Works for **JSON, HTML, CSV, and SQLite**, and can
  **continue an entire server in one click** (select-all across channels).
- **SQLite (`.db`) export format** — export a channel directly to a queryable SQLite database with
  built-in full-text search. Available in both the GUI and the CLI (`-f Db`).
- **Library with full-text search** — catalog your past exports and search message content across all of
  them at once (powered by the SQLite format).
- **Offline format conversion** — convert an existing **JSON** export into **HTML (dark/light), TXT, CSV,
  or SQLite** without re-downloading anything. Conversions stay high-fidelity: roles, colors, members,
  channels, and custom emoji are preserved via data embedded in the JSON export.
- **Count-backed progress & ETA** — progress and time-remaining estimates based on actual message counts
  rather than timestamp guesses.

## Download

This fork publishes its own builds on its [**Releases**](https://github.com/arandomhooman/DiscordChatExporter/releases) page:

- **GUI** (desktop app): look for `DiscordChatExporter.*.zip`
- **CLI** (terminal app): look for `DiscordChatExporter.Cli.*.zip`

Or [build it from source](#building-from-source).

> [!IMPORTANT]
> To launch the GUI on macOS you may need to remove the download from quarantine:
> `xattr -rd com.apple.quarantine DiscordChatExporter.app`.

> [!NOTE]
> Community packages (Scoop, WinGet, AUR, Nix, Docker) track the **upstream** release, not this fork.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/arandomhooman/DiscordChatExporter.git
cd DiscordChatExporter

# Run the GUI
dotnet run --project DiscordChatExporter.Gui -c Release

# Run the CLI
dotnet run --project DiscordChatExporter.Cli -c Release -- --help

# Or produce a self-contained build (e.g. Windows x64)
dotnet publish DiscordChatExporter.Gui -c Release -r win-x64 --self-contained -o ./publish
```

## Features

- Cross-platform graphical and command-line interfaces
- Authentication via either a user or a bot token
- Multiple output formats: HTML (dark/light), TXT, CSV, JSON, **SQLite**
- Support for markdown, attachments, embeds, emoji, and other rich media features
- File partitioning, date ranges, message filtering, and other export options
- Self-contained exports that can be viewed offline
- **Resume/continue exports, a searchable export library, and offline conversion of JSON exports into any
  other format** (this fork)

## Screenshots

![channel list](.assets/list.png)
![rendered output](.assets/output.png)

## Credits

A fork of [**Tyrrrz/DiscordChatExporter**](https://github.com/Tyrrrz/DiscordChatExporter) by
[Oleksii Holub](https://github.com/Tyrrrz) and
[contributors](https://github.com/Tyrrrz/DiscordChatExporter/graphs/contributors), used under the MIT
license (see [License.txt](License.txt)). Please consider supporting the original author:
<https://tyrrrz.me/donate>.

## See also

- [**Chat Analytics**](https://github.com/mlomb/chat-analytics) — analyze chat patterns using DiscordChatExporter exports.
- [**DiscordChatExporter-frontend**](https://github.com/slatinsky/DiscordChatExporter-frontend) — a convenient viewer for exports.
