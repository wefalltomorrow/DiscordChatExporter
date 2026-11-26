# Offline-only archive conversion

This release converts DiscordChatExporter JSON archives and extracted Discord Data Package message transcripts already stored on your device. It does not sign in to Discord, discover servers or channels, request message history, or download Discord-hosted assets. The GUI supports batch conversion and local archive browsing; the CLI exposes only `convert` and `guide`.

## What is included

- Convert local transcripts to HTML Dark, HTML Light, Plain Text, CSV, or SQLite.
- Batch-convert files from the GUI and browse or search local SQLite archives in the Library.
- Block Discord API/CDN requests from this build and remove the automatic updater and its network dependency.
- Block automatic remote-resource loading in generated HTML; visible external links can still be opened deliberately.

## Data Package limitation

Discord Data Package transcripts contain only messages authored by the account that requested the package. They cannot reconstruct a complete two-sided conversation or provide full-history export from a server where a bot cannot be added. This release is for local conversion of existing files; it does not provide a replacement live collection method.

## Account-risk status

This build removes Discord requests initiated by the application. It cannot guarantee zero account-enforcement risk, change the behavior of earlier binaries, or make collection or reuse of Discord data permitted. The latest previously published [2.48.0-wft.4 release](https://github.com/wefalltomorrow/DiscordChatExporter/releases/tag/2.48.0-wft.4) retains live Discord access; these changes apply only to binaries built from this offline-only source revision.

Follow Discord's [self-bot guidance](https://support.discord.com/hc/en-us/articles/115002192352-Automated-User-Accounts-Self-Bots), [Developer Policy](https://support-dev.discord.com/hc/en-us/articles/8563934450327-Discord-Developer-Policy), and [Terms of Service](https://discord.com/terms).
