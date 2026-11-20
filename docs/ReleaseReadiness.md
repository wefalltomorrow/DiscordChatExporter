# Release validation

The `main` workflow validates PRs targeting `prime` or `integration/**`. Its offline test matrix covers Windows, Linux and macOS. The fork release workflow uses the same CLI test selection and also runs the GUI and resilient-wrapper tests on all three systems. CLI packages from both workflows include `Export-Guild-Resilient.ps1`.

For validation of a branch, dispatch `main.yml` with `run_live_tests: false` (the default). This runs formatting, offline tests and the packaging matrix. Manual dispatch does not publish a release, including when a tag is selected. The separate `live-test` job can access Discord only when explicitly requested through `run_live_tests: true`; merely configuring a token does not enable it. Live tests are not required for release validation.

Before publishing:

1. Obtain successful formatting, offline tests and all 18 packaging jobs for the exact candidate commit. Draft PRs skip packaging, so use an offline manual dispatch for the complete matrix.
2. Review changes and verify package contents, including License.txt, NOTICE and the CLI wrapper.
3. Verify the release version and notes. Publish only after the validation gate passes. The existing tag-push and `release/**` branch workflows can publish automatically; use an integration or fix branch for validation work.

For every published fork release, audit the description for the policy warning and the explicit statement that no credible evidence establishes suspension prevention. Editing a release description does not rebuild or replace its assets; verify the tag and asset provenance separately.

Local Windows checks are useful evidence but do not replace native Linux/macOS validation. Offline success does not establish prevention of Discord detection or suspension. Do not label a candidate as safe or undetectable based on these tests.

Discord prohibits automating normal user accounts (self-bots), warns that accounts found doing so may be terminated, and bars scraping in its Developer Policy. There is no credible evidence that this project prevents account suspension or other enforcement, and no such protection is claimed. A first-429 stop is only a fail-closed response to a rate-limit signal; it does not make user-token automation permitted or establish account safety. Do not claim that this project or any release prevents or minimizes suspension risk while user-token automation remains available.

If GitHub rejects workflow dispatch with `Actions has been disabled for this repository`, preserve the response and keep the candidate unmerged and unreleased until execution is restored. An API response showing repository Actions permissions enabled does not establish that jobs can run.
