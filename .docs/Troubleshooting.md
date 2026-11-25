# Troubleshooting

## The app has no server list or export button

That is expected in this offline-only candidate. Live Discord access and server/channel export commands are disabled. Use **Conversion** for local JSON files and the **Library** for local archives.

## A JSON file is rejected

The input must be a DiscordChatExporter JSON export or a supported Data Package message transcript. Select an individual transcript file in the CLI. In the GUI, select JSON files or the extracted Data Package folder. Confirm the ZIP has been extracted before importing.

## The Data Package import finds no transcripts

Select the extracted package root or its **Messages** folder. The importer needs the package's message transcript files and channel metadata (or channel IDs in the transcript folder names).

## Images or attachments do not appear

Conversion does not download assets. Generated HTML blocks automatic external resource loading, so remote images and media appear only when local files are available. Visible external links can be opened by deliberately selecting them.

## The output cannot be written

Choose an output directory where the app can write and ensure the target file is not locked by another application.

## Account-risk milestone

This source revision blocks Discord requests initiated by the exporter. It does not establish zero suspension risk, change earlier published binaries, or meet the full-history use case for channels the app cannot access. See [release readiness](../docs/ReleaseReadiness.md).
