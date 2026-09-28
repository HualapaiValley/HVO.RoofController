# CI Runners and Workflow Security

This repository is public. CI uses GitHub-hosted runners for the server tests, the Pi
image build and the HAT emulator image. The former iOS/M5 workflows were removed with the native application.

## Workflows

| Workflow | Runner | Triggers | Purpose |
|---|---|---|---|
| `ci.yml` | GitHub-hosted `ubuntu-latest` | push to `main`, pull requests, nightly | Build and test the server graph (Debug with coverage, then Release with warnings as errors) |
| `pi-image.yml` | GitHub-hosted `ubuntu-latest` | push and pull requests touching the Pi image inputs, weekly, manual | Build the `linux/arm64` Pi image from a clean builder; nothing is pushed or deployed |
| `emulator-image.yml` | GitHub-hosted `ubuntu-latest` | push and pull requests touching the emulator or controller image inputs, weekly, manual | Build the HAT emulator image for `linux/amd64` and `linux/arm64`, and open and close the emulated roof through the containerized controller (`tests/emulator/compose-smoke-test.sh`); no hardware, nothing pushed or deployed |

All workflows:

- declare `permissions: contents: read`;
- pin every action to a full commit SHA with a `# vX.Y.Z` comment (Dependabot updates the SHA
  and the comment together);
- run `dotnet` from `src/` so `src/global.json` selects the SDK.

## Retired M5 runner

Deleting the workflow does not unregister a runner or revoke its access. The repository
owner must check whether the M5 runner serves other projects, then remove this repository
from its runner group (or unregister the runner if no longer used). Revoke any dispatch
tokens and credentials that were dedicated to this repository. Until then, confirm the
runner cannot be targeted by other workflows or reach the roof controller network.

## GitHub settings the owner must confirm

Record the confirmed values in the private operations record, not here:

1. **Fork pull request approval.** Require approval for outside collaborators before running
   untrusted changes, even though retained workflows use hosted runners.
2. **Action pinning.** Keep the full-length SHA requirement enabled and limit allowed actions.
3. **Default workflow permissions.** Keep the `GITHUB_TOKEN` read-only by default and prevent
   workflows from approving pull requests.
4. **Runner retirement.** Confirm this repository no longer has access to the M5 runner,
   including through a shared organization runner group, and that any dedicated dispatch
   tokens and credentials were revoked.
5. **Network.** No untrusted runner can reach the roof controller or other observatory controls.
