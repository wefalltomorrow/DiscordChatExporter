# Getting started

This candidate build converts local chat archive files. It does not sign in to Discord or retrieve new messages.

## Choose GUI or CLI

- The GUI supports batch conversion and local archive browsing/search.
- The CLI converts one JSON transcript at a time.

## Convert with the GUI

1. Open the app and select **Conversion**.
2. Select **Pick JSON files…** for DiscordChatExporter exports, or **Pick Data Package folder…** for an extracted Discord Data Package.
3. Choose an output folder and one or more target formats.
4. Select **Convert** and review the results list.

## Convert with the CLI

~~~powershell
.\DiscordChatExporter.Cli.exe convert --input .\messages.json --output .\conversation.html --format HtmlDark
~~~

The output formats are **HtmlDark**, **HtmlLight**, **PlainText**, **Csv**, and **Db**. Run the CLI with **guide** or **--help** for its available commands. The CLI accepts no Discord token, channel ID, or live-export command.

## Data Package limits

The GUI reads message transcripts from an extracted Data Package. These records contain messages sent by the account that requested the package; they do not include other participants' messages. They cannot be used to recreate a complete conversation transcript.

See Discord's [Data Package guide](https://support.discord.com/hc/en-us/articles/360004957991-Your-Discord-Data-Package) for requesting and extracting the package. Conversion reads local files and does not fetch remote assets. Generated HTML blocks automatic external resource loading; visible links open only when deliberately selected.

## Library

The Library lists local exports in folders you add. SQLite (.db) files can be searched locally; no Discord connection is used.

## Account-risk milestone

This build removes live Discord requests initiated by the application. That does not establish zero account risk or provide full-history retrieval from channels where a bot cannot be added. Existing release binaries are unchanged; verify that a binary was built from the offline-only source before using it.

Automatic update checks are disabled in this candidate so it cannot replace itself with an earlier release that still has live Discord access. The latest published release, 2.48.0-wft.4, is unchanged and retains its original behavior.
