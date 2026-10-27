# Contributing to Achievement Labs

Create a feature branch in your fork based on the current upstream branch. Push your changes to your fork, then open a pull request against `Achievement-Labs/achievement-labs:main`. In GitHub's compare screen choose **compare across forks**, set the base repository to `Achievement-Labs/achievement-labs`, and select your fork and feature branch as the head. You do not need write access to this upstream repository.

Keep app features and event-catalogue corrections in separate PRs. Prefer one feature or one title-specific mapping correction per PR, with a clear explanation of the behavior, source evidence and tests. State whether a recipe has only passed structural checks or has confirmed live achievement credit. Do not mix speculative edition remaps into a UI PR.

Preserve the author-maintained catalogue and unrelated recipes. Use standalone `Events/<titleId>.json` templates and ordinary replacement operations. `SupportedTitleIDs` must remain the final property of every Data.json file. Never include account tokens, private credentials or account-specific exports in a PR.

For desktop changes run `pwsh -File tools/Test-Enhancements.ps1`. For automation/token changes also run `dotnet run --project tests/AutomationChecks -- <temporary-output-directory>`. Use synthetic/offline fixtures by default; describe any separate live validation explicitly. Existing upstream update URLs and profile functionality must remain intact.

Publishing a fork release does not open a PR against upstream. See [GitHub's instructions for fork pull requests](https://docs.github.com/en/pull-requests/how-tos/create-pull-requests/creating-a-pull-request-from-a-fork).
