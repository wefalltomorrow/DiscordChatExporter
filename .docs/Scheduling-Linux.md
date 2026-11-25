# Scheduling local conversions on Linux

A local scheduler such as cron can run the CLI against a transcript already on your device. For example:

~~~cron
0 2 * * * /path/to/DiscordChatExporter.Cli convert --input /data/messages.json --output /data/converted.html --format HtmlDark
~~~

Ensure the scheduled user can read the input and write the output. This task converts a local file; it does not sign in to Discord or fetch messages.
