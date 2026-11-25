# Using the GUI

The GUI in this candidate build is for local conversion and archive search. Server discovery, live export, and Discord asset downloads are disabled.

## Convert local files

1. Start DiscordChatExporter and select **Conversion**.
2. Select **Pick JSON files…** to add DiscordChatExporter JSON exports.
3. Or select **Pick Data Package folder…** and choose the extracted package folder or its **Messages** folder. The app finds the package's message transcript files for batch conversion.
4. Select an output folder and the formats to create.
5. Select **Convert**. Per-file results appear on screen; existing files are protected by the overwrite confirmation.

Supported output formats are HTML Dark, HTML Light, TXT, CSV, and SQLite. Conversion reads the selected local files; it does not sign in or fetch messages from Discord. Source attachment and image URLs may be preserved in output. The generated HTML blocks automatic remote loading; a browser follows visible external links only when deliberately selected.

## Library

Select **Library** to browse export folders already in the local catalog or scan another folder. SQLite exports can be searched across the catalog. This search uses local database files.

## Settings and updates

Settings apply to the local app and output formatting. Automatic update checks are disabled in this candidate, and the update control is hidden. This build does not check for or download application updates.

## Unsupported in this build

The GUI does not accept a Discord token or retrieve server/channel history. A Discord Data Package transcript contains only messages sent by the account that requested it and is not a full conversation archive. The account-risk milestone remains unresolved; this build must not be described as a zero-risk full-history exporter.
