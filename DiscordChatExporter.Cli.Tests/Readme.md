# DiscordChatExporter tests

The current fork build is offline-only for Discord. Unit and conversion tests use local fixtures or in-memory HTTP handlers; tests that require a live Discord server are skipped. No Discord account token or test-server access is required.

Run the suite with `dotnet test`.
