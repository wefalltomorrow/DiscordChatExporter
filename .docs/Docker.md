# Docker conversion

The CLI container supports local file conversion. It does not connect to Discord or accept a token.

Build the image from the repository root:

~~~console
docker build -f DiscordChatExporter.Cli.dockerfile -t dce-offline .
~~~

Mount the input directory read-only and an output directory, then run convert:

~~~console
docker run --rm \
  -v /absolute/path/input:/in:ro \
  -v /absolute/path/output:/out \
  dce-offline convert --input /in/messages.json --output /out/conversation.html --format HtmlDark
~~~

Replace the host paths with directories available to Docker Desktop on Windows or Docker on macOS/Linux. Supported formats are **HtmlDark**, **HtmlLight**, **PlainText**, **Csv**, and **Db**.
