# Versions and releases

Every program and image of the roof controller carries one product version: the controller and its web UI, the HAT
emulator, `hvo-roof`, the kiosk and the Mac app. Versions follow [Semantic Versioning](https://semver.org/), starting at
4.0.0.

## The version

The version is set in one place, the `VersionPrefix` in [`Directory.Build.props`](../Directory.Build.props). It is the
version the next release will carry. What a build adds to it depends on where it is built:

| Build | Version | Set by |
|-------|---------|--------|
| A workstation, or the deploy script | `4.0.0-dev` | the default in `Directory.Build.props` |
| CI | `4.0.0-ci.<run number>` | the `Version` environment variable, which `ci.yml` sets for its builds; `ROOF_VERSION` in the image workflows |
| A release | `4.0.0`, or `4.0.0-rc.<n>` for a release candidate | the tag: the release workflow runs CI with the version the tag names, and builds the images with it |
| A dry run of the release workflow | `4.0.0-dryrun.<run number>` | the release workflow, run by hand ([A dry run](#a-dry-run)) |

Every build also carries the commit it was built from, after a `+`: `4.0.0-ci.123+0123abcd…`. The SDK reads it from the
checkout. An image is built without the checkout, so its builds pass the commit in with `-p:SourceRevisionId=`.

The run number counts per workflow: one commit can be `ci.412` in `CI` and `ci.57` in `Pi image`, and `ci.57` means a
different commit in each. It orders the builds of one workflow; the commit identifies a build.

[`Directory.Build.targets`](../Directory.Build.targets) stops any build whose version is neither the `VersionPrefix`
nor a prerelease of it, so a `-p:Version=4.1.0` on a tree that says 4.0.0 fails with a message naming both.

To move to the next version, change the `VersionPrefix` in a pull request of its own.

## Where it shows

| Where | What it shows |
|-------|---------------|
| `hvo-roof --version` | `4.0.0-dev+<commit>` |
| `hvo-roof info`, and the terminal interface's System page | `hvo-roof`'s own version, then the controller's |
| The web UI's System page | The web UI's version, then the controller's |
| The kiosk's and the Mac app's System page | `This kiosk` or `This app`, then the controller's (for an admin) |
| The controller's log | `HVO Roof Controller 4.0.0-dev+<commit> is starting.` |
| `GET /api/v1.0/System/info` | `applicationVersion`: `4.0.0-dev+<commit>` |
| The Mac app's `Info.plist` | `CFBundleShortVersionString` 4.0.0, and the build number in `CFBundleVersion` |
| Both images' labels | `org.opencontainers.image.version`, `.revision`, `.created` and `.source` |

The pages show the commit shortened to 12 characters: `4.0.0-dev (commit 0123abcd4567)`. Everyone sees the version of
the program they use; the controller's version, host and resource use need the Admin role.

## The scripts

[`build/version.sh`](../build/version.sh) reads the version from `Directory.Build.props`. The workflows and the deploy
script use it, so no version is written anywhere else:

```bash
build/version.sh                 # 4.0.0
build/version.sh --dev           # 4.0.0-dev
build/version.sh --ci 123        # 4.0.0-ci.123
build/version.sh --tag v4.0.0    # 4.0.0
build/version.sh --tag v4.0.0-rc.1
build/version.sh --dry-run 7     # 4.0.0-dryrun.7
```

With `--tag` it checks that the tag names this version, and fails for any other tag:

```text
version.sh: the tag 'v4.1.0' is for 4.1.0, but Directory.Build.props holds 4.0.0. Change the VersionPrefix first, in its own pull request.
```

[`build/check-image-labels.sh`](../build/check-image-labels.sh) checks the labels of every platform image in an OCI
archive, as `docker buildx build --output type=oci,dest=<file>` writes it. The version and commit must be the ones
given, the source must be this repository, and the title and creation time must be set. Given a list of platforms, the
archive must hold an image for exactly those. A blob it cannot read, or a manifest of a type it does not know, fails
the check:

```bash
build/check-image-labels.sh image.tar 4.0.0-ci.123 "$(git rev-parse HEAD)"
build/check-image-labels.sh image.tar 4.0.0-ci.123 "$(git rev-parse HEAD)" linux/amd64,linux/arm64
```

[`build/release-compose.py`](../build/release-compose.py) makes a release's compose file
([Deployment](deployment.md#deploying-with-compose)) from `src/HVO.RoofControllerV4.RPi/docker-compose.yaml`: the same
profiles, services, settings and mounts, with the release's images on GHCR, each pinned to its digest when one is
given, in place of the images that file builds, and nothing built. A line of the source it does not recognise, such as
a new image or a `build:` it cannot remove, fails it:

```bash
build/release-compose.py --version 4.0.0 -o docker-compose.yaml
build/release-compose.py --version 4.0.0 --controller-digest sha256:<hex> --emulator-digest sha256:<hex> -o docker-compose.yaml
```

[`build/release-assets.py`](../build/release-assets.py) gathers a release's assets ([The assets](#the-assets)) into
one new folder, from what CI built: `hvo-roof` and `hvo-roof-install` for each platform, the kiosk's folder and the Mac
app's zip. It checks that each is exactly what it expects, built for the right processor, and makes the kiosk's
tarball, the compose file (with `release-compose.py`), `release.json` and `SHA256SUMS`. `--upgrade-notes DIR` puts
every release's upgrade notes in the folder, up to this one's, in `release.json`. `--registry` names another registry
than GHCR, for a test:

```bash
build/release-assets.py --version 4.0.0 --commit "$(git rev-parse HEAD)" --created 2026-10-01T12:00:00Z \
  --controller-digest sha256:<hex> --emulator-digest sha256:<hex> \
  --cli ci/cli --installer ci/installer --kiosk ci/kiosk --mac-zip ci/mac/hvo-roof-mac-<run id>.zip \
  --upgrade-notes docs/upgrade-notes -o dist
```

[`build/push-image.sh`](../build/push-image.sh) pushes an image from an OCI archive to a registry with
[skopeo](https://github.com/containers/skopeo): every platform's image and the index over them, each with the digest
it has in the archive, so the digest pushed is the digest `check-image-labels.sh` checked. It reads the tag back and
prints the digest. With `--tag` it gives an image already pushed another tag, once it has checked that the image's
version tag still names the digest the release names; the workflow that moves `latest` uses it
([Publishing](#publishing)):

```bash
build/push-image.sh roof-controller.tar ghcr.io/hualapaivalley/roof-controller:4.0.0
build/push-image.sh --tag ghcr.io/hualapaivalley/roof-controller:4.0.0@sha256:<hex> latest
```

A tag that has been pushed again since, with another image, fails it and tags nothing:

```text
push-image.sh: ghcr.io/hualapaivalley/roof-controller:4.0.0 is sha256:<other> in the registry, not sha256:<hex>: it was pushed again after the release. Nothing is tagged.
```

## Images

Both Dockerfiles take three build arguments, and set the labels and the programs' version from them:

| Argument | Label | Also |
|----------|-------|------|
| `ROOF_VERSION` | `org.opencontainers.image.version` | `-p:Version=` for every program in the image |
| `ROOF_REVISION` | `org.opencontainers.image.revision` | `-p:SourceRevisionId=`, the commit after the `+` |
| `ROOF_CREATED` | `org.opencontainers.image.created` | |

An image built without them carries `4.0.0-dev` with no commit and empty labels. Each Dockerfile declares them just
before the step that uses them (the publish, and the labels after the last `RUN`): every `RUN` after an `ARG` sees it,
so a new version or build time would otherwise restore the packages and install the image's own packages again. The deploy script passes all three
([Deployment](deployment.md)); Compose passes `HVO_ROOF_VERSION`, `HVO_ROOF_REVISION` and `HVO_ROOF_CREATED` from the
environment. The `Pi image` and `Emulator image` workflows build each image for `linux/amd64` and `linux/arm64` with its CI
version, and check its labels and its platforms with `check-image-labels.sh`.

## Cutting a release

1. **The version.** The `VersionPrefix` must be the version to release. If it is not, change it first, in a pull
   request of its own ([The version](#the-version)).
2. **The release pull request.** In one pull request, merged before the tag:
   - In `CHANGELOG.md`, rename `## [Unreleased]` to the version and the day, `## [4.0.0] - 2026-10-01`, and start a new,
     empty `## [Unreleased]` above it.
   - Write the upgrade notes, `docs/upgrade-notes/4.0.0.md`: what someone running the last release must do to move to
     this one, such as a setting to change or a step to take before upgrading, in a few lines of Markdown with no
     heading of their own. When there is nothing to do, say so: the file must not be empty. The release's notes start
     with them, under "Upgrade notes".
3. **A release candidate**, if the release needs trying on the Pi first. Tag main's commit `v4.0.0-rc.1` (then
   `-rc.2`, and so on) as in step 4. A candidate is released as a prerelease: it goes through the same checks and gives
   the same assets and images, but it does not move the images' `latest` tag, and it needs neither upgrade notes nor
   a CHANGELOG section.
4. **The tag.** Tag the merged commit on main with an annotated tag, and push the tag:

   ```bash
   git switch main
   git pull --ff-only
   git tag -a v4.0.0 -m "HVO Roof Controller 4.0.0"
   git push origin v4.0.0
   ```

   The tag starts the `Release` workflow. It stops before building anything when the tag does not name the
   `VersionPrefix`, is not on main, or, for a final release, has no upgrade notes or CHANGELOG section. Nothing was
   released then: delete the tag (`git push origin :refs/tags/v4.0.0`, then `git tag -d v4.0.0`), fix what the run
   named, and tag again.
5. **The draft.** The run makes a draft release, linked from the run's summary, then installs a rig from it end to
   end. When that has passed too, check the draft ([Before publishing](#before-publishing)), then publish it
   ([Publishing](#publishing)).

## What the release workflow does

[`.github/workflows/release.yml`](../.github/workflows/release.yml) runs for every tag that starts with `v`:

1. **The version.** `version.sh --tag` gives the version, and the checks of step 4 above run.
2. **The gates.** CI ([`ci.yml`](../.github/workflows/ci.yml)) and the scenarios
   ([`scenarios.yml`](../.github/workflows/scenarios.yml), without the soaks) run for the tagged commit, CI building
   every program with the release's version. Both must pass. They run within the release's own run, so their results
   are on its page, and the programs CI tested are the ones released.
3. **The images.** Both images are built again for `linux/amd64` and `linux/arm64`, with no cache and the base images
   pulled afresh, into OCI archives. `check-image-labels.sh` checks each, and `push-image.sh` pushes each to GHCR with
   the version as its tag: `ghcr.io/hualapaivalley/roof-controller:4.0.0` and
   `ghcr.io/hualapaivalley/roof-hat-emulator:4.0.0`. Each image gets a build provenance attestation, which GHCR keeps
   beside it.
4. **The assets.** `release-assets.py` gathers what CI built and tested in this run into the release's assets
   ([The assets](#the-assets)), with the images' digests and the upgrade notes. Before logging in to GHCR, the run
   checks that CI's `hvo-roof` and `hvo-roof-install` for linux-x64 print the version and commit, running each with no
   token and an empty environment; here it checks that `hvo-roof-linux-x64` and `hvo-roof-install-linux-x64` are those
   files, and `SHA256SUMS` against the files. One build provenance attestation
   covers every file in `SHA256SUMS`.
5. **The draft.** A draft release of the assets, titled `HVO Roof Controller 4.0.0` and marked a prerelease for a
   release candidate. Its notes are the upgrade notes, the images, how to check the files, and then what GitHub writes
   from the pull requests merged since the last release. The same assets go to the run as an artifact.
6. **The end to end.** The `e2e` job, on x64 and on arm64, installs a test rig from that artifact as a person would
   from the release: the release's `install.sh` checks `hvo-roof-install` against `SHA256SUMS` and its attestation
   and starts it, and the installer runs the release's images from GHCR against the HAT emulator. The rig scenario
   then installs `hvo-roof` by `install.sh`, opens, stops part way and closes the emulated roof through it, renews the
   certificate, changes the rig's address and name, and backs the rig up and restores it
   ([The rig end to end](install.md#the-rig-end-to-end)). It writes nothing, and signs in to GHCR only to read the
   images while they are private.

Only the release job can write: the draft (`contents: write`), the images (`packages: write`) and the attestations
(`id-token: write` for the signing certificate, and `attestations: write`). Only the two steps that call `gh` have the
token in their environment. Every other job, CI's and the scenarios' included, can only read. Every action is pinned
to a commit. A second run for the same tag waits for the first rather than cancelling it.

The release job runs in the `release` environment. The check that a tag is on main runs from the tagged commit's own
workflow, so a commit could leave it out; the environment's protection rules are the repository's, and no commit can
change them ([The first release](#the-first-release)).

The workflow publishes nothing. Only people who can write to the repository see a draft, and no published release
names the images until someone publishes the draft.

## The assets

| Asset | What it is |
|-------|------------|
| `hvo-roof-linux-arm64`, `hvo-roof-linux-x64`, `hvo-roof-osx-arm64` | `hvo-roof`, for the Pi, a Linux workstation and an Apple silicon Mac (signed ad hoc) ([Install](cli.md#install)) |
| `hvo-roof-install-linux-arm64`, `hvo-roof-install-linux-x64`, `hvo-roof-install-osx-arm64` | The installer, for the same three (signed ad hoc) ([Installing](install.md#getting-it)) |
| `hvo-roof-kiosk-<version>-linux-arm64.tar.gz` | The kiosk's program, unit, udev rule and example settings, in a folder of that name ([Install](kiosk.md#install)) |
| `HVO-Roof-<version>.zip` | The Mac app, signed ad hoc ([Install](mac.md#install)) |
| `docker-compose.yaml` | The release compose file, each image pinned to its digest ([Deploying with Compose](deployment.md#deploying-with-compose)) |
| `deploy-roofcontroller-rpi.sh` | The deploy script, for a released image ([Deploying a released image](deployment.md#deploying-a-released-image)) |
| `install.sh` | The script that checks a machine, then downloads the release's installer for it, checks it and starts it ([install.sh](install.md#installsh)) |
| `release.json` | The release, its images and its files, for programs to read ([release.json](#releasejson)) |
| `SHA256SUMS` | The SHA-256 of every other file, as `sha256sum` writes it |

The images are not assets: they are on GHCR, and `release.json` and the release's notes name them.

Anyone can check a downloaded file, and where it was built, with the [GitHub CLI](https://cli.github.com/):

```bash
sha256sum --check --ignore-missing SHA256SUMS
gh attestation verify hvo-roof-linux-arm64 --repo HualapaiValley/HVO.RoofController \
  --signer-workflow HualapaiValley/HVO.RoofController/.github/workflows/release.yml --source-ref refs/tags/v4.0.0
gh attestation verify oci://ghcr.io/hualapaivalley/roof-controller:4.0.0 --repo HualapaiValley/HVO.RoofController \
  --signer-workflow HualapaiValley/HVO.RoofController/.github/workflows/release.yml --source-ref refs/tags/v4.0.0
```

`--repo` alone accepts an attestation from any of the repository's workflows, on any branch, such as a dry run's of a
file with the same name. `--signer-workflow` and `--source-ref` accept only the release workflow's, for that tag.

## release.json

What a program needs to know about a release, such as the installer and `install.sh`:

```json
{
  "schemaVersion": 1,
  "product": "HVO Roof Controller",
  "version": "4.0.0",
  "tag": "v4.0.0",
  "prerelease": false,
  "commit": "<the commit, 40 hex digits>",
  "created": "2026-10-01T12:00:00Z",
  "repository": "https://github.com/HualapaiValley/HVO.RoofController",
  "images": {
    "controller": {
      "repository": "ghcr.io/hualapaivalley/roof-controller",
      "tag": "4.0.0",
      "digest": "sha256:<hex>",
      "reference": "ghcr.io/hualapaivalley/roof-controller:4.0.0@sha256:<hex>",
      "platforms": ["linux/amd64", "linux/arm64"]
    },
    "hatEmulator": { "...": "the same, for ghcr.io/hualapaivalley/roof-hat-emulator" }
  },
  "upgradeNotes": [
    { "version": "4.0.0", "text": "Nothing to do by hand." }
  ],
  "assets": [
    { "name": "hvo-roof-install-linux-arm64", "kind": "installer", "platform": "linux-arm64", "size": 45234567, "sha256": "<hex>" },
    { "name": "hvo-roof-linux-arm64", "kind": "cli", "platform": "linux-arm64", "size": 41234567, "sha256": "<hex>" }
  ]
}
```

- `schemaVersion` changes only when a field is removed or changes its meaning. A reader refuses a schema version it
  does not know.
- `created` is when the release was built: the time in both images' labels, and the kiosk tarball's file times.
- `images.<image>.reference` is what to pull or deploy (`IMAGE_REF`): the tag, for people, and the digest, which is
  what is pulled.
- `upgradeNotes` is every release's upgrade notes up to this one, oldest first: each `docs/upgrade-notes/X.Y.Z.md`
  whose version is not newer than this release's (a release candidate's is its final release's), as its `version` and
  its `text`, at most 16 KiB each and 128 KiB together. Before an upgrade, the installer prints those of each release
  after the one installed ([Upgrading](install.md#upgrading)), so an upgrade across several releases shows them all.
  The folder holds `X.Y.Z.md` files alone. It is left out when there are none, which only a release candidate before
  any final release's notes may have.
- `assets` lists every asset but `release.json` and `SHA256SUMS`, sorted by name. `kind` is `cli`, `installer`, `kiosk`, `mac-app`,
  `compose`, `deploy-script` or `install-script`. `platform` is the .NET runtime the file is for, or `null` for a file for any.

## Before publishing

Check the draft, on its page and with its files:

- **The run.** Every job passed, the `e2e` job on both runners included, and the summary names the draft. Never
  publish a draft whose `e2e` job failed or did not run.
- **The assets.** Every file of [The assets](#the-assets) is there, with the version in its name where it has one.
  Download them into an empty folder and check them:

  ```bash
  gh release download v4.0.0 --repo HualapaiValley/HVO.RoofController --dir release-check
  cd release-check
  sha256sum --check SHA256SUMS
  gh attestation verify hvo-roof-linux-x64 --repo HualapaiValley/HVO.RoofController \
    --signer-workflow HualapaiValley/HVO.RoofController/.github/workflows/release.yml --source-ref refs/tags/v4.0.0
  chmod +x hvo-roof-linux-x64 && ./hvo-roof-linux-x64 --version    # 4.0.0+<the tagged commit>
  jq '{version, tag, prerelease, commit, images: [.images[].reference]}' release.json
  ```

- **`release.json`.** The version, the tag, `prerelease` (true only for a candidate), the tagged commit, and both
  images at the version, each with a digest.
- **The images.** Both platforms, the digest `release.json` names, and the attestation:

  ```bash
  docker buildx imagetools inspect ghcr.io/hualapaivalley/roof-controller:4.0.0
  gh attestation verify oci://ghcr.io/hualapaivalley/roof-controller:4.0.0 --repo HualapaiValley/HVO.RoofController \
    --signer-workflow HualapaiValley/HVO.RoofController/.github/workflows/release.yml --source-ref refs/tags/v4.0.0
  ```

- **The notes.** The upgrade notes are there and read well, and the pull requests GitHub lists are the release's.
  Edit the notes on the draft's page if they need it. Never replace a file there: `SHA256SUMS`, `release.json` and the
  attestation name the files the run made.
- **On the Pi**, for a release candidate at least: deploy the controller by the reference in `release.json`
  ([Deploying a released image](deployment.md#deploying-a-released-image)), and check it as the deployment guide says.

## Publishing

Publish the draft on its page (Edit, then Publish release), or:

```bash
gh release edit v4.0.0 --repo HualapaiValley/HVO.RoofController --draft=false
```

GitHub marks a newly published release the repository's latest unless told otherwise. Publish a fix to an older
version, such as 4.0.1 after 4.1.0, with `--latest=false`, so that GitHub's "Latest" stays on the newest:

```bash
gh release edit v4.0.1 --repo HualapaiValley/HVO.RoofController --draft=false --latest=false
```

Publishing a release runs the [`Release latest`](../.github/workflows/release-latest.yml) workflow.
[`build/release-latest.sh`](../build/release-latest.sh) decides whether `latest` moves: only for the newest final
release, the highest `vX.Y.Z` of the published releases that are not prereleases, whatever GitHub marks latest. A
release candidate, or a fix to an older version, leaves `latest` where it is, and the run's summary says why. For the
newest, the workflow gives each image named in its `release.json` the `latest` tag, once it has checked that the
image's version tag still names the digest in `release.json`. It refuses a release whose tag is a candidate's but
which is not marked a prerelease, and a `release.json` that is not the release's.

A release published with a workflow's token starts no workflow, so a workflow that publishes calls `Release latest`
itself. To run it by hand for a published release:

```bash
gh workflow run release-latest.yml --repo HualapaiValley/HVO.RoofController -f tag=v4.0.0
```

A published release is final: never delete it, move its tag, or push its images' version tags again. A mistake found
after publishing is fixed in the next version.

## The first release

A package that a workflow pushes to the organization for the first time is private. So after the first run of the
release workflow (a dry run is enough), and before the first release is published, an owner of the HualapaiValley
organization makes both packages public: on the organization's Packages page, for `roof-controller` and then
`roof-hat-emulator`, Package settings, Change visibility, Public. Anyone can then pull the images without logging in,
as the Pi and the release compose file do. A public package cannot be made private again.

The release workflow's last job runs in the `release` environment, which GitHub makes, unprotected, on the first run.
Before the first release, an admin of the repository protects it and the tags, in the repository's settings:

- **Environments, `release`.** Required reviewers: the release managers, one of whom approves each run's release
  job; tick "Prevent self-review" if there are two or more. Deployment branches and tags: selected ones, the tag rule
  `v*` and the branch `main` (for [dry runs](#a-dry-run)).
- **Rules, Rulesets, New tag ruleset.** Target the tags `v*`, and restrict creations, updates and deletions to the
  release managers (the bypass list), so that nobody else can tag a release or move one's tag.

Optionally, turn on immutable releases in the repository's settings (General, Releases), so that a published
release's tag and assets cannot be changed, as this page already treats them.

## A dry run

To try the whole workflow without releasing anything, run it by hand, on main:

```bash
gh workflow run release.yml --repo HualapaiValley/HVO.RoofController --ref main
```

A dry run releases the commit it runs on as version `4.0.0-dryrun.<run number>` (`version.sh --dry-run`), with the tag
`v4.0.0-dryrun.<run number>`. Every check runs but the tag's: CI, the scenarios, the images, the assets and the
attestations, and the end to end from the draft's assets, where `install.sh` checks the attestation without a tag (a dry
run has none). Its draft is a prerelease whose notes say it is a dry run, and it makes no git tag. Its images are on
GHCR with the dry run's version as their tag, and it never moves `latest`. Its release job waits for a reviewer as a
release's does. The `release` environment lets only main and `v*` tags run that job, so to dry-run another branch, add
it to the environment's deployment branches first, and remove it afterwards.

Delete what it made afterwards, the draft and the images:

```bash
gh release delete v4.0.0-dryrun.<run number> --repo HualapaiValley/HVO.RoofController --yes
```

On GHCR, each package holds the dry run's image as several versions: the one tagged with the dry run's version, an
untagged one for each platform, and the attestation. Delete them on the package's page (the version's menu, Delete),
or with `gh api`, which lists a package's versions with their IDs and tags, newest first, and deletes one by its ID.
Its token needs the `read:packages` and `delete:packages` scopes, which `gh auth login` does not give:

```bash
gh auth refresh -h github.com -s read:packages,delete:packages
gh api "orgs/HualapaiValley/packages/container/roof-controller/versions" \
  --jq '.[] | {id, name, tags: .metadata.container.tags, created_at}'
gh api -X DELETE "orgs/HualapaiValley/packages/container/roof-controller/versions/<id>"
```

A dry run's versions were all created within the minutes of its release job, and the tagged one names its dry run.
Never delete a version of a published release: its tagged image, an image of one of its platforms, or its attestation.

Before the first release, keep the first dry run's versions. They are each package's only ones, GHCR does not delete a
package's last tagged version (only the whole package), and each package must exist for an owner to make it public
([The first release](#the-first-release)). Delete them once the first release has pushed its own.

## A failed release

When a run fails, nothing is published. It may still have pushed the images with the version's tag, before the draft;
running it again pushes them again over that tag, which is harmless until the release is published. Fix the cause,
then:

- **A check of the tag** (the version, main, the upgrade notes or the CHANGELOG): delete the tag, fix it on main, and
  tag again (step 4 of [Cutting a release](#cutting-a-release)).
- **CI or the scenarios**: when the runner failed rather than the code, re-run the failed jobs on the run's page.
  When the code needs a fix, delete the tag, merge the fix, and tag the new commit.
- **The release job**: delete its draft if it made one, which keeps the tag, then re-run the failed jobs. The job
  refuses to run while a release for the tag exists, even a draft:

  ```bash
  gh release delete v4.0.0 --repo HualapaiValley/HVO.RoofController --yes
  ```

- **The `e2e` job**: the draft is there, and must not be published. Its results (`release-e2e-results-<runner>-<run>`)
  say which check failed. When the runner failed rather than the release, re-run the failed jobs: they take the
  assets from the run's artifact, and the draft stays. When the code needs a fix, delete the draft and the tag, merge
  the fix, and tag the new commit.

Once a release is published, its tag never moves.

## Tests

- `tests/versioning/version-tests.sh` runs both scripts against made-up `Directory.Build.props` files and OCI
  archives. CI runs it with the deploy-script tests.
- `ProductVersionTests` checks that every program carries the version in `Directory.Build.props`, or a prerelease of
  it, and `RoofControllerApiTests` that the controller's `System/info` reports it. The tests of each System page read
  the versions back.
- `tests/mac/test_bundle.py` checks `bundle.py`'s `--version` ([Mac app](mac.md#build-it-yourself)).
- `tests/releasing/test_release_compose.py` checks `release-compose.py` against the real compose file and damaged copies
  of it, and CI's "Compose profiles" step checks, profile by profile with Compose, that the file it makes differs from
  the source only in its images.
- `tests/releasing/test_release_assets.py` checks `release-assets.py` with made-up CI artifacts: the assets, their
  names and modes, the kiosk tarball's contents, `install.sh` with the release's version written in, `release.json`,
  `SHA256SUMS`, and every artifact it must refuse. `tests/install/install-sh-tests.sh` runs that `install.sh`
  ([install.sh's tests](install.md#installshs-tests)).
- `tests/releasing/push-image-tests.sh` pushes two-platform OCI archives with `push-image.sh` to a registry of its own
  in Docker, checks every digest the registry then holds, and tags them `latest`, refusing a version tag pushed again.
- `tests/releasing/release-latest-tests.sh` runs `release-latest.sh` against a stand-in for `gh` that serves made-up
  releases, two to a page: the first final release, a candidate, a fix to an older version, versions `sort -V`
  orders, drafts and other tags, and each release and `release.json` it refuses. CI runs these with the
  `push-image.sh` tests.
- The release workflow runs CI and the scenarios for every release, then installs a rig from its draft on x64 and
  arm64 (`tests/installer/rig-scenario.sh` with `RIG_RELEASE_DIR`, [The rig end to end](install.md#the-rig-end-to-end)),
  and a [dry run](#a-dry-run) runs the whole workflow without releasing.
