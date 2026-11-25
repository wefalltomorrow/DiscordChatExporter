# Scheduling local conversions on macOS

Use launchd or another local scheduler to run the CLI against a transcript already on your device. Configure the executable path and pass these arguments:

~~~text
convert
--input
/Users/you/Archives/messages.json
--output
/Users/you/Archives/converted.html
--format
HtmlDark
~~~

Ensure the scheduled user can read the input and write the output. This task converts a local file; it does not sign in to Discord or fetch messages.
