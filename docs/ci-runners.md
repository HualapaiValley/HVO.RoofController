# CI Runners and Workflow Security

This repository is public. Anything that can start a workflow can run code on the machine that
executes it, so the self-hosted runner needs settings that the workflow files cannot enforce by
themselves. This page lists the rules in the workflows and the GitHub settings the repository
owner must confirm.

## Workflows

| Workflow | Runner | Triggers | Purpose |
|---|---|---|---|
| `ci.yml` | GitHub-hosted `ubuntu-latest` | push to `main`, pull requests, nightly | Build and test the server graph (Debug with coverage, then Release with warnings as errors) |
| `ios.yml` | GitHub-hosted `macos-26` | push and pull requests touching the iPad or shared code | Build the unsigned iPad simulator app with the pinned Xcode and workload set |
| `pi-image.yml` | GitHub-hosted `ubuntu-latest` | push and pull requests touching the Pi image inputs, weekly, manual | Build the `linux/arm64` Pi image from a clean builder; nothing is pushed or deployed |
| `m5-ios-validation.yml` | Self-hosted M5 Mac (`m5-roof` label) | `repository_dispatch` (`m5-roof-validation`) and manual `workflow_dispatch` only | Build the iPad simulator app on the operations Mac with its installed Xcode |

All workflows:

- declare `permissions: contents: read`;
- pin every action to a full commit SHA with a `# vX.Y.Z` comment (Dependabot updates the SHA
  and the comment together);
- run `dotnet` from `src/` so `src/global.json` selects the SDK.

## Rules for the self-hosted M5 workflow

- **Trusted triggers only.** `repository_dispatch` needs a token with write access to the
  repository and always runs the workflow file from the default branch. `workflow_dispatch`
  needs write access. Never add `pull_request`, `pull_request_target`, `push` for other refs,
  `issue_comment` or `workflow_run` triggers to a workflow that targets the M5 runner.
- **Main only.** The job's `if` refuses any repository other than
  `HualapaiValley/HVO.RoofController` and any ref other than `refs/heads/main`, so a manual
  dispatch of another branch does not run.
- **No stored credentials.** Checkout uses `persist-credentials: false`, and the job token is
  read-only.
- **Clean workspace.** The job deletes the previous workspace, including `.git/hooks`, before
  checkout.
- **Pinned toolchain.** The SDK comes from `src/global.json`, the workload set is pinned
  (`WORKLOAD_SET_VERSION`), and Xcode is selected per job through `DEVELOPER_DIR` (set the
  repository variable `M5_XCODE_DEVELOPER_DIR` if Xcode is not at the default path). The job
  never changes the machine-wide `xcode-select` setting and does not need an iOS simulator
  runtime.
- **Ownership checks are diagnostics.** The job fails if it is not running as the dedicated
  runner account or if the tool cache, `~/.dotnet` or the NuGet cache contains entries owned by
  another user or writable by others. These checks cannot detect changes made by an earlier job
  running as the same account; only ephemeral runners give per-job isolation.
- **Queued, not dropped.** Each run has its own concurrency group, so a second dispatch waits
  for the runner instead of cancelling a pending run.
- **Unsigned output.** The job fails unless the app has no bundle seal and no signing identity.

To run it:

```bash
gh workflow run m5-ios-validation.yml --repo HualapaiValley/HVO.RoofController --ref main
```

## GitHub settings the owner must confirm

The workflow file cannot stop another workflow, branch or fork from targeting the runner's
labels. These settings do. Record the confirmed values in the private operations record, not
here.

1. **Runner group scope.** The M5 runner's group is available only to repositories that need it,
   and is restricted to selected workflows: `m5-ios-validation.yml` at `refs/heads/main` for this
   repository (and the equivalent pinned workflow and ref for any other repository that shares
   the runner). Without that restriction, any workflow in an allowed repository, on any branch,
   can target the runner.
2. **Public repositories.** If the group allows public repositories, the restriction in item 1
   is what keeps pull-request code off the runner. Confirm it before leaving public access on.
3. **Fork pull request approval.** Set "Require approval for all outside collaborators" (or
   stricter) in the repository's Actions settings. The default only covers first-time
   contributors.
4. **Action pinning.** "Require actions to be pinned to a full-length commit SHA" stays enabled,
   and allowed actions are limited to GitHub-owned, verified creators or an explicit list.
5. **Default workflow permissions.** The default `GITHUB_TOKEN` permission stays read-only and
   workflows cannot approve pull requests.
6. **Dispatch tokens.** Tokens that send `repository_dispatch` are fine-grained, limited to this
   repository, and stored only where needed.
7. **Runner isolation.** Prefer an ephemeral (single-job) runner, or a dedicated macOS account
   per repository. If one persistent account serves several repositories, a job from any of them
   can change the caches and tools the next job uses; treat those repositories as one trust
   domain.
8. **Network.** The runner's network cannot reach the roof controller or other observatory
   control systems, and the runner holds no deployment keys or roof API keys.
