# Using the CLI

The CLI in this candidate build exposes local conversion and help only. It does not accept Discord tokens or provide live export, server discovery, or channel lookup commands.

## Show available commands

~~~console
./DiscordChatExporter.Cli --help
~~~

On Windows Command Prompt, omit the leading ./ and use DiscordChatExporter.Cli.exe.

## Convert a transcript

~~~console
./DiscordChatExporter.Cli convert --input ./messages.json --output ./conversation.html --format HtmlDark
~~~

Options:

- **--input / -i**: local DiscordChatExporter JSON export or Data Package messages.json transcript.
- **--output / -o**: local path for the converted file.
- **--format / -f**: **HtmlDark**, **HtmlLight**, **PlainText**, **Csv**, or **Db**. The default is **HtmlDark**.

Example for CSV:

~~~console
./DiscordChatExporter.Cli convert -i ./messages.json -o ./conversation.csv -f Csv
~~~

The CLI processes one input file per command. The GUI supports batch conversion of Data Package transcripts. The **guide** command prints a short offline usage guide.

Conversion reads local files and does not connect to Discord. It does not download media; source URLs may remain in output and can load when opened in a browser.
