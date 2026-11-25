# Supported input files

This candidate build does not use Discord tokens, server IDs, or channel IDs. The GUI and CLI work with files already on your device.

## DiscordChatExporter JSON

Select an existing DiscordChatExporter JSON export in the GUI, or pass its local path to the CLI:

~~~console
./DiscordChatExporter.Cli convert --input ./channel.json --output ./channel.html --format HtmlDark
~~~

## Discord Data Package

Request and download your package through Discord's **User Settings → Data & Privacy → Request your data** flow. See Discord's [Data Package guide](https://support.discord.com/hc/en-us/articles/360004957991-Your-Discord-Data-Package). Extract the ZIP, then select the package folder or its **Messages** folder in the GUI and choose **Pick Data Package folder…**.

The importer reads local messages.json transcripts. Data Package transcripts contain messages sent by the account that requested the package, not all messages sent by everyone in each channel. They cannot provide the full conversation history needed when the app has no authorized access to a channel.

The CLI can convert an individual messages.json transcript. It does not batch-scan a package folder.
