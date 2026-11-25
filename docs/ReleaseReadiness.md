# Release validation

This checkout is a local archive conversion candidate. Its Discord API/CDN client rejects outbound requests, automatic updates are disabled, the GUI hides Discord connection and export actions, and the CLI registers only local convert and guide commands. HTML conversion adds a Content Security Policy that blocks automatic external resource loads while allowing local archive assets. A browser can still open a visible external link if the user deliberately selects it.

The main workflow runs on pull requests to prime or integration branches, pushes to prime, and tag pushes. Non-draft PRs and direct branch or tag builds run formatting, token-free CLI tests, GUI tests, and the cross-platform package matrix. Draft PRs skip packaging. The release workflow runs on pushes to release branches. Both workflows create GitHub releases automatically for their configured publishing refs; neither has a manual dispatch trigger.

Before publishing a candidate:

1. Obtain successful formatting, CLI and GUI tests, and all 18 package jobs for the exact commit. Do not treat local checks as a substitute for Linux and macOS CI.
2. Inspect the packaged CLI and GUI contents and verify License.txt and NOTICE are present.
3. Verify the tag, release notes, source revision, and assets together. The release workflows read docs/Offline-Only-Release-Notes.md; editing release text alone does not rebuild existing assets.
4. Keep the account-risk milestone unresolved unless a permitted solution also satisfies the requested full-history use case.

## Account-risk milestone status

As of November 26, 2026, the milestone is unresolved. The candidate removes Discord API and CDN requests initiated by the application, disables automatic updates, and prevents automatic remote resource loading in generated HTML. It does not provide live history retrieval, and it cannot guarantee that an account will never face enforcement for other activity or for earlier builds. Existing published binaries keep their original behavior; specifically, the latest published 2.48.0-wft.4 release still includes live Discord access.

The required use case is full-history export from servers where a bot cannot be added. Discord Data Package transcripts contain only messages authored by the requesting account, so they cannot meet that requirement. Discord's self-bot guidance prohibits automating normal user accounts; its Developer Policy and Terms of Service also restrict scraping through Discord services. The candidate therefore offers local conversion of data already obtained through a permitted source; it does not claim to make unauthorized collection safe or to prevent suspension. See Discord's [self-bot guidance](https://support.discord.com/hc/en-us/articles/115002192352-Automated-User-Accounts-Self-Bots), [Developer Policy](https://support-dev.discord.com/hc/en-us/articles/8563934450327-Discord-Developer-Policy), [Data Package guide](https://support.discord.com/hc/en-us/articles/360004957991-Your-Discord-Data-Package), and [Terms of Service](https://discord.com/terms).
