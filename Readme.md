# DiscordChatExporter

[![Build](https://img.shields.io/github/actions/workflow/status/wefalltomorrow/DiscordChatExporter/main.yml?branch=prime)](https://github.com/wefalltomorrow/DiscordChatExporter/actions)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](License.txt)

This fork is being changed to an offline-only archive converter. The candidate build does not sign in to Discord, discover servers or channels, fetch message history, or download Discord assets. It converts files already on your device.

<p align="center"><img src="favicon.png" alt="DiscordChatExporter" /></p>

## Current candidate capabilities

- Convert DiscordChatExporter JSON exports and extracted Discord Data Package message transcripts to HTML Dark, HTML Light, Plain Text, CSV, or SQLite.
- Batch-convert files from the GUI and browse or search local SQLite archives in the Library.
- Use the CLI for single-file conversion.
- Discord API and CDN requests are disabled, the updater and its network dependency are removed, and the CLI exposes only local convert and guide commands.

Discord Data Package transcripts include only messages authored by the account that requested the package. They do not provide a complete two-sided conversation history. This candidate therefore does not meet the full-history use case for channels where a bot cannot be added.

**Account-risk milestone status: unresolved.** This change removes Discord requests initiated by this build. It does not prove that suspension risk is zero, address earlier use of other builds, or provide full-history retrieval for the stated use case. Existing published binaries keep their original behavior; these changes apply only to a build made from this source revision.

Discord's [self-bot guidance](https://support.discord.com/hc/en-us/articles/115002192352-Automated-User-Accounts-Self-Bots) prohibits automating normal user accounts, and its [Developer Policy](https://support-dev.discord.com/hc/en-us/articles/8563934450327-Discord-Developer-Policy) restricts scraping through Discord services. The offline converter does not provide a way to collect full history or make earlier live collection compliant.

The latest published release, [2.48.0-wft.4](https://github.com/wefalltomorrow/DiscordChatExporter/releases/tag/2.48.0-wft.4), predates these changes and still contains live Discord access. This source candidate disables automatic updates so it cannot replace itself with that older behavior.

## GUI

Open the **Conversion** screen, select one or more DiscordChatExporter JSON files or an extracted Data Package folder, choose an output folder and target formats, then select **Convert**. The **Library** screen works with local export folders and SQLite files.

## CLI

Convert a local file with:

~~~powershell
.\DiscordChatExporter.Cli.exe convert --input .\messages.json --output .\conversation.html --format HtmlDark
~~~

Supported formats are **HtmlDark**, **HtmlLight**, **PlainText**, **Csv**, and **Db**. The **guide** command prints the local conversion instructions. No token or Discord ID is accepted by the CLI.

## Data Package limitation

Discord's [Data Package guide](https://support.discord.com/hc/en-us/articles/360004957991-Your-Discord-Data-Package) explains how to request and download your package. After extracting it, select the package folder or its **Messages** folder in the GUI and batch-convert its messages.json transcripts. Those transcripts contain only messages sent by the requesting account, so this import cannot reconstruct all messages in a conversation.

Conversion does not fetch remote assets. Generated HTML blocks automatic external resource loading. Visible external links remain in the archive and open only if you deliberately select them.

## Downloads and validation

Do not assume that an existing release binary contains these changes. Check the release notes and source revision for the offline-only build before using it. No account-safety claim is made for earlier releases.

See the [offline-only candidate release notes](docs/Offline-Only-Release-Notes.md) for the full behavior and limitations.

The project targets the .NET 10 SDK. See [release readiness](docs/ReleaseReadiness.md) for required validation and the unresolved account-risk milestone. User documentation is in [.docs](.docs/Readme.md).

## Credits

The original **DiscordChatExporter** is developed by [Oleksii Holub (Tyrrrz)](https://github.com/Tyrrrz/DiscordChatExporter) and its contributors.

This fork also incorporates or adapts work from:

- [arandomhooman/DiscordChatExporter](https://github.com/arandomhooman/DiscordChatExporter) — continuation/resume, SQLite, library/search, conversion, and reliability improvements.
- [edelkas/DiscordChatExporter](https://github.com/edelkas/DiscordChatExporter) — caching and export improvements selectively reviewed and ported.

See [NOTICE](NOTICE) and [License.txt](License.txt).
