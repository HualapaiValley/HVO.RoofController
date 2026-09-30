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
| CI | `4.0.0-ci.<run number>` | the `VersionSuffix` environment variable in `ci.yml`; `ROOF_VERSION` in the image workflows |
| A release | `4.0.0`, or `4.0.0-rc.<n>` for a release candidate | `-p:Version=` with the version the tag names |

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
environment. The `Pi image` and `Emulator image` workflows build each image with its CI version and check its labels,
and its platforms, with `check-image-labels.sh`.

## Tests

- `tests/versioning/version-tests.sh` runs both scripts against made-up `Directory.Build.props` files and OCI
  archives. CI runs it with the deploy-script tests.
- `ProductVersionTests` checks that every program carries the version in `Directory.Build.props`, or a prerelease of
  it, and `RoofControllerApiTests` that the controller's `System/info` reports it. The tests of each System page read
  the versions back.
- `tests/mac/test_bundle.py` checks `bundle.py`'s `--version` ([Mac app](mac.md#build-it-yourself)).
