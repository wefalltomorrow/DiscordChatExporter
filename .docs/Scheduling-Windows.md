# Scheduling local conversions on Windows

Use Windows Task Scheduler to run the CLI against a transcript already on your device. Set the program to the full path of DiscordChatExporter.Cli.exe and use arguments like:

~~~text
convert --input "C:/Archives/messages.json" --output "C:/Archives/converted.html" --format HtmlDark
~~~

Set the working directory to the folder containing the CLI. This task converts a local file; it does not sign in to Discord or fetch messages.
