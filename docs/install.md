# Installing: hvo-roof-install

`hvo-roof-install` is the installer (#61). It sets up the roof controller's pieces on a machine:

- the controller;
- a test rig;
- the touchscreen kiosk;
- `hvo-roof`, the command-line client;
- the Mac app.

It records what it installed, so a later run can check, repair and upgrade what is there. It never moves the roof.

It runs as a wizard in the terminal, in the same HVO Dark look as the other interfaces. It can also run without asking
anything, from an answers file that the wizard saves.

> **What it does today.** This release of the installer:
>
> - looks at the machine;
> - offers only the roles the machine can have, and refuses the rest;
> - works out and shows the plan;
> - saves answers files;
> - makes the controller's folders, its keys, its certificate authority and its HTTPS certificate
>   ([Certificates](#certificates));
> - deploys the controller, or a test rig with its HAT emulator, from the release's images by their digests, through
>   the deploy script ([Deploying](deployment.md#deploying-with-the-script)), and adds the first admin;
> - sets up the touchscreen kiosk on the controller's Pi ([The kiosk](#the-kiosk));
> - installs `hvo-roof` and the Mac app on other machines, connected to the controller and trusting its CA once you
>   have checked its fingerprint ([hvo-roof and the Mac app](#hvo-roof-and-the-mac-app));
> - writes the install record.
>
> It adopts a controller that the deploy script already runs.

## Getting it

`hvo-roof-install` is one self-contained file, so the machine needs no .NET runtime. It is built for linux-arm64 (the
Pi), linux-x64 and osx-arm64 (Apple silicon). There is no build for an Intel Mac or for Windows.

Every run of the `CI` workflow publishes the three builds as the `hvo-roof-install-<run id>` artifact. From #73, each
release carries them as assets, with `install.sh` to download the right one.

To build one yourself, run this from `src/`, so that `src/global.json` picks the SDK:

```bash
cd src
dotnet publish HVO.RoofControllerV4.Installer -c Release -r linux-arm64 -o ../out/hvo-roof-install-arm64
# or -r linux-x64, or -r osx-arm64 for a Mac
```

The wizard needs a terminal of at least 80 × 24 that sends function keys: the Linux console, SSH from any common
terminal, tmux, or Terminal on a Mac.

## Running it

| Command | What it does |
|---------|--------------|
| `hvo-roof-install` | Opens the wizard. |
| `hvo-roof-install --answers FILE` | Installs from an answers file without asking anything, printing the plan and each step. |
| `hvo-roof-install --plan --answers FILE` | Prints every folder, file, container, service and port the install would make or change, and changes nothing. |
| `hvo-roof-install --plan` | The same for what is installed here, from its install record: yours, or with `sudo` (or without a record of yours) the machine's. |
| `hvo-roof-install --version` | Prints the installer's version and the commit it was built from, such as `4.0.0+0123abcd…`. |
| `hvo-roof-install cert` | Checks the controller's certificate and its CA, makes or renews what needs it, and redeploys the controller to serve it once the roof is idle and you agree. `--plan` shows what it would do. |
| `hvo-roof-install cert show` | Shows the controller's certificate and CA: what they are for, until when, and their fingerprints. |
| `hvo-roof-install cert import FILE` | Puts your own certificate in place for the controller. |

The installer runs as root for the machine's roles, and as you for your own ([Roles](#roles)).

- **The controller, a rig on Linux or the kiosk:** run it with `sudo`.
- **`hvo-roof` or the Mac app:** run it as yourself, without `sudo`.
- **A rig on a Mac:** run it as yourself too, and on its own: `hvo-roof` and the Mac app are installed in a later run,
  once the rig is running ([The guards](#the-guards)).

It refuses to mix the two in one run, and refuses the wrong one for a role. `--plan` does not need root, but planning
the controller or a rig needs Docker access (on Linux, root or the `docker` group).

Keys are made on the machine, straight into files that only their user can read, and never shown. The only secrets a
person gives are the first admin's password and PIN, the camera's password, the password of the admin who makes the
Mac app's device key, and the password of a certificate you import ([Passwords and PINs](#passwords-and-pins)). The installer never shows, keeps or logs one. An answers file, the
plan, the record and the log never hold a secret.

### The release it installs

Each installer installs its own release: `--version` names it. It reads that release's `release.json` from GitHub,
which names the controller's and the HAT emulator's images on GHCR, each by its digest. Docker pulls those images by
their digests, and no others: the deploy script deploys the controller's image only when Docker reports that digest.

For a machine that cannot reach GitHub, download `release.json` from the release's page on a machine that can, and give
its folder with `--release DIR`. Docker still pulls the images from `ghcr.io`.

### Exit codes

`install.sh` and scripts rely on these.

| Code | Meaning |
|------|---------|
| 0 | Installed. With `--plan`, the plan can be carried out. |
| 1 | A step failed. The log says which step, and what the installer did before it. Nothing after that step changed; running the installer again carries on. |
| 2 | The command line or the answers file was not valid. |
| 3 | Refused, and nothing was changed. The reason is one of these: a role this machine cannot have, or a choice the guards refuse ([The guards](#the-guards)); a missing prerequisite (`sudo`, Docker); something the installer will not replace; a certificate the controller could not serve; a controller CA that could not be fetched, or whose SHA-256 is not the one given; a folder you cannot write to; the keychain over SSH; or, for `--plan` from a record an earlier release wrote, an answer the record does not give. |
| 130 | You quit before installing, or the installer was interrupted. Ctrl+C stops between steps, and never kills a deploy script that has started. It stops that script too, which puts the old controller back, unless the new controller has already passed its checks: the script then runs to its end, and the installer exits 130 only when steps were left ([Running it again](#running-it-again)). |

## Roles

| Role | In an answers file | Where it runs | Runs as | Record |
|------|--------------------|---------------|---------|--------|
| The controller, driving the real HAT | `controller` | Only the observatory's Raspberry Pi: 64-bit Raspberry Pi OS with `/dev/i2c-1`, `/dev/gpiomem` and the thermal sensor | root | `/etc/hvo-roof/install.json` |
| A test rig: the controller against the HAT emulator | `rig` | Linux (amd64 or arm64) with Docker, or an Apple silicon Mac with Docker Desktop | root on Linux; you on a Mac | the machine's on Linux; yours on a Mac |
| The kiosk | `kiosk` | The controller's Pi | root | `/etc/hvo-roof/install.json` |
| `hvo-roof` | `cli` | Linux or an Apple silicon Mac | you | yours |
| The Mac app | `mac-app` | An Apple silicon Mac | you | yours |

Your record is `$XDG_CONFIG_HOME/hvo-roof/install.json`, or `~/.config/hvo-roof/install.json`. `hvo-roof` keeps its
credentials in the same folder.

A rig on Linux keeps its files where the controller does, in the deploy script's folders:

- `/etc/hvo-roof`, with `secrets`, `https` and `config` inside it;
- `/var/lib/hvo-roof`, with `identity` and `settings-secrets` inside it.

A rig on a Mac keeps them in `~/Library/Application Support/HVO Roof Rig`, which Docker Desktop shares with its
containers.

### The guards

The installer refuses a choice that would put the roof at risk, or that cannot work, before it changes anything:

- **The real HAT only on its Pi.** The controller is offered only on a 64-bit Linux Pi with the HAT's three devices.
  On a Pi where I2C is not enabled, the installer names the missing devices and the command that enables I2C
  (`sudo raspi-config nonint do_i2c 0`, then a reboot).
- **Never a rig on the real HAT's machine.** A test rig is refused in either of these cases:
  - the machine's record says the machine drives the real HAT;
  - a `roof-controller` container there drives the real HAT.
- **A typed confirmation for a rig near a HAT.** Before a rig is installed on a machine with `/dev/i2c-1`, for the
  first time, a person types the machine's host name, to confirm that it is not the observatory's Pi. An answers
  file can give it as `rigConfirmation`. The wizard never saves the confirmation, so each new rig is confirmed on its
  own machine.
- **One controller container.** The controller and a rig cannot share a machine: both run as the `roof-controller`
  container.
- **The kiosk with its controller.** The kiosk needs the controller on the same Pi: choose both, or install the
  controller first.
- **The kiosk's screen.** The kiosk draws on the display itself, so it needs a connected screen, and no desktop
  (`display-manager.service`) holding it, now or at the next boot: use Raspberry Pi OS Lite, or boot to the console.
  Once the kiosk is installed, a screen that is off or unplugged does not stop an update.
- **Docker.** The controller and a rig need Docker running, and the right to use it.
- **Root rules.** See [Running it](#running-it).
- **The rig before its clients on a Mac.** A rig on a Mac is refused with `hvo-roof` or the Mac app in the same run:
  they trust the rig's CA by its SHA-256, which you can compare only once the rig has made it. Install the rig, then
  run the installer again for them.
- **The keychain on a Mac.** `trustInKeychain` is refused anywhere but a Mac. On Linux, add the CA to a browser by
  hand ([Trusting the controller in a browser](#trusting-the-controller-in-a-browser)).
- **One controller for `hvo-roof` and the Mac app.** They share one controller, and your record keeps it once for
  both. A run for one of them, with another controller address or another CA or certificate SHA-256 than the other
  one was installed with, is refused: give the same controller, or choose both to move them together. The wizard
  checks it on its Client page, where the controller is given.
- **Compose.** A container that Docker Compose made is described in the plan but never replaced. Move it first: see
  [Moving between Compose and the deploy script](deployment.md#moving-between-compose-and-the-deploy-script).
- **Busy ports.** A port that something else already listens on blocks the plan.
- **An API key the controller refuses.** The controller refuses to start with an entry in `/etc/hvo-roof/secrets`
  that has no name, a role other than `RoofViewer`, `RoofOperator` or `RoofAdmin`, both a key and a key hash or
  neither, a key shorter than 24 or longer than 512 characters, a hash that is not 64 hexadecimal digits, or a kiosk
  key without the `RoofViewer` role. The deploy script's pre-flight check fails on such an entry, so it blocks a
  deploy: the plan names the entry and what is wrong with it, never its key. A controller that is left as it is is
  not blocked.
- **A typed confirmation for plain HTTP.** Before the controller serves plain HTTP, when it does not already, a person
  types `http`, to confirm that keys, session tokens and PINs may cross this network unencrypted. An answers file can
  give it as `httpConfirmation`. The wizard never saves it.

## What it looks for

Before asking anything, and without changing anything, the installer looks at the following:

- **The machine:** the platform, the host name, who runs the installer, the operating system and the Pi's model.
- **The controller's certificate and CA:** who issued the certificate, the names it is for, until when, and whether
  its password opens it. Each run warns when either expires soon, or when the certificate is not for a name clients
  use ([Renewing](#renewing)).
- **The HAT's devices:** `/dev/i2c-1`, `/dev/gpiomem` and `/sys/class/thermal/thermal_zone0/temp`.
- **Docker:** its version, and Compose v2's version.
- **The install records:** the machine's, and yours. A record the installer cannot read is reported and replaced, and
  until then a test rig is refused, since the record may say the machine drives the real HAT. A record from a newer
  installer is never replaced: install with that installer, or a newer one.
- **`/etc/hvo-roof`,** when it was set up without the installer.
- **The containers:** `roof-controller` and `hat-emulator`. For each, the installer finds:
  - its state and version;
  - whether it drives the real HAT or an emulator;
  - whether the deploy script or Docker Compose made it.
- **The kiosk's service:** `hvo-roof-kiosk.service`.
- **The display:** the display outputs in `/sys/class/drm` and whether a screen is connected to one, and whether a
  desktop (`display-manager.service`) runs, or starts at boot (it is enabled, and the Pi boots to `graphical.target`).
- **The programs:** `hvo-roof` on the `PATH` (or in `~/.local/bin` or `/usr/local/bin`), and `HVO Roof.app` in
  `/Applications` or `~/Applications`.

The wizard's first page shows what it found, and the log keeps it. A later run starts from the recorded roles and
choices.

## The wizard

The wizard has up to eight steps. A step with nothing to ask for the roles chosen is passed over, and not counted:
step 4 asks about the controller for the controller or a rig, and about the connection to it for `hvo-roof` and the
Mac app; step 6 comes only when the plan needs a password. The key bar says
what the keys do:

- **Enter** does what the highlighted button says, even from a list of options (**Space** chooses an option).
- **Esc** goes back a step. It never closes the installer.
- **F10** quits, unless the install is running: then the installer finishes (or stops) first.

**1. This machine.** What the installer found. Nothing has changed yet.

![Step 1: This machine, showing the Pi, its HAT devices, Docker, the records and a running roof-controller container](images/install/1-machine.svg)

**2. What to install.** Each role this machine can have. A role it cannot have is shown with the reason, and cannot be
chosen.

![Step 2: What to install, with the controller chosen and the test rig, the kiosk and the Mac app unavailable, each with its reason](images/install/2-roles.svg)

On a machine with the HAT's I2C bus, choosing a test rig asks for the host name:

![Step 2 with a test rig chosen on a machine with /dev/i2c-1, asking for the host name to confirm](images/install/2-rig-confirmation.svg)

**3. Choices.** The questions of the roles chosen, filled in from the record, or with the defaults:

- **The controller:** how clients connect (private CA, your own certificate, self-signed, or HTTP), the ports, and
  the other names and domains clients use for it ([Certificates](#certificates)). No domain is listed unless you
  list it: the page names those this machine's resolver searches, and you add one only if clients use names in it.
- **The kiosk:** whether to hide the console's cursor behind it, and who to give a PIN for signing in at it
  ([The kiosk](#the-kiosk)). The page names the operators and admins on the controller who have no PIN yet.
- **`hvo-roof`:** its folder.
- **The Mac app:** its folder.

![Step 3: Choices for the controller: the connection, the HTTPS, HTTP and web UI ports, and the names clients use for it](images/install/3-settings.svg)

With the kiosk chosen too:

![Step 3 with the kiosk: the console's cursor hidden, the people to give a PIN, and olga named as an operator without one](images/install/3-settings-kiosk.svg)

Choosing HTTP asks you to type `http`, to confirm that keys, session tokens and PINs may cross the network unencrypted:

![Step 3 with HTTP chosen: a warning, and the field where http is typed to confirm](images/install/3-settings-http.svg)

**4. The controller.** For the controller or a rig:

- **The first admin:** the name of the first person, who signs in to the web UI and adds everyone else, and whether
  they have a PIN for the kiosk. They are added only while the controller has no admin. Leave the name empty to add
  nobody.
- **The camera** (the controller only): the Blue Iris server and a user that may only view. Leave the server empty
  to leave the camera as it is. The page reminds you that earlier versions held a Blue Iris credential in their
  source, and that the old user's password must change ([security.md](security.md), "Rotate the Blue Iris credential").
  The installer writes the camera's settings to the secrets folder, so they take precedence over the camera settings
  in the web UI, which cannot change them then. Run the installer again to change the camera.
- **Telemetry:** the OTLP/HTTP endpoint the controller exports to. Empty turns the export off.
- **Settings from a backup:** the full path of an `appsettings.Local.json`, for a controller that has no settings yet
  ([Settings from a backup](#settings-from-a-backup)).

![Step 4: The controller, with the first admin roy and a PIN, the Blue Iris server and its view-only user with the reminder, a telemetry endpoint and a backup's settings](images/install/4-controller.svg)

A rig has the HAT emulator in place of the camera: how many times as fast as real time it runs, its camera's frame
rate, and whether other machines may use it (over HTTPS only).

![Step 4 for a test rig: the first admin, and the HAT emulator's time scale, camera frame rate and whether it is open to the network](images/install/4-rig.svg)

**4. Connecting to the controller.** For `hvo-roof` and the Mac app:

- **The address:** the controller as this machine reaches it, with its port, such as `https://roof.local:8443`.
- **How they trust it,** as the controller serves it ([Certificates](#certificates)):
  - **Its private CA.** **Fetch its CA** gets the CA from the controller and shows its name and SHA-256 fingerprint.
    Compare the fingerprint with the one on the Done page of the controller's installer, or from
    `sudo hvo-roof-install cert show` on the controller, and tick that it is the same. Next stays on the page until
    you do, and a changed address needs its CA fetched again.
  - **Its self-signed certificate:** give the certificate's SHA-256 fingerprint, from the same places. Each
    connection is pinned to it.
  - **As this machine trusts any website:** a certificate of your own from a CA this machine already trusts, or
    plain HTTP.
- **The keychain** (on a Mac, with the CA): trust the CA in your login keychain too, so Safari and Chrome open the web
  UI without a warning.
- **The admin who makes the Mac app's device key** (the Mac app only), by the name they sign in with
  ([The Mac app's device key](#the-mac-apps-device-key)).

![Step 4 for hvo-roof: the controller's address, its private CA fetched from it with the CA's name and fingerprint, and the box ticked to say it is the fingerprint the controller shows](images/install/4-client.svg)

For the Mac app, the page also offers the keychain and asks for the admin:

![Step 4 for the Mac app: the controller's CA and its fingerprint checked, the login keychain chosen, and ada as the admin who makes the Mac's device key](images/install/4-client-mac.svg)

**5. Review the plan.** Every change the install would make, checked against the machine, as `--plan` prints it
([The plan](#the-plan)). Install goes ahead only when nothing blocks the plan. **Save answers** writes the answers
file, which installs the same way elsewhere with `--answers`. When the plan needs a password, the button says Next.

![Step 5: Review the plan, listing the folders, the record, the adopted container and the ports, with Save answers](images/install/5-review.svg)

A blocked step says why, and Install stays off:

![Step 5 with a blocked plan: a container made by Docker Compose, with the reason and a pointer to the docs](images/install/5-review-blocked.svg)

**6. Passwords.** Only the passwords and PINs the plan needs ([Passwords and PINs](#passwords-and-pins)): the first
admin's, when the controller has no admin yet, the camera's, when its files are made or its user changes, and the
password of the admin who makes the Mac app's device key, when the Mac needs one. A new password or PIN is typed
twice; the admin's, which they already have, once. Each is shown only as dots. None is saved, in the answers or anywhere else, or logged, and the fields are
cleared once the install has them.

![Step 6: Passwords, with the first admin's password and PIN and the camera's password each typed twice and shown as dots, and a message that the two PINs differ](images/install/6-passwords.svg)

**7. Installing.** Each step as it runs. The installer cannot be left until the install has finished or stopped.

![Step 7: Installing, part way through, with each folder created and the record being written](images/install/7-installing.svg)

**8. Done.** What was installed, where to reach it and its log, that the roof has not moved, what to back up, how
clients trust its certificate, and where the record and the install's log are. The same text stays in the terminal
after the wizard closes.

![Step 8: Done, with the controller's API and web UI addresses, its log, the backup reminder, its CA with the CA's fingerprint, the record and the log](images/install/8-done.svg)

When a step fails, the Done page says why, and where the log is. Nothing after that step was changed, and running
the installer again carries on from there ([Running it again](#running-it-again)). An install refused before it
started says that nothing was changed.

![Step 8 after a step failed: hvo-roof's connection was not written because the controller's CA could not be fetched, nothing after that step was changed, the log, and that running the installer again carries on](images/install/8-stopped.svg)

## Answers files

An answers file holds everything the wizard asks, as JSON. It has no secret. Members are camelCase, and names are
kebab-case. A member the installer does not know is an error, so a misspelt one is not silently ignored.

```json
{
  "schema": 1,
  "roles": ["controller"],
  "controller": {
    "connection": "private-ca",
    "httpsPort": 8443,
    "httpPort": 8080,
    "webPort": 8088,
    "hostNames": ["roof"],
    "domains": ["observatory.example"],
    "firstAdmin": { "name": "observer", "pin": true },
    "camera": { "baseUrl": "http://192.168.0.4:81", "userName": "roof-viewer" },
    "telemetryEndpoint": "http://collector:4318"
  }
}
```

A rig's `controller` section has no `camera`. It has a `rig` section instead:

```json
{
  "roles": ["rig"],
  "controller": {
    "firstAdmin": { "name": "tester" },
    "rig": { "timeScale": 10, "cameraFramesPerSecond": 5, "openToLan": false }
  }
}
```

With the kiosk, a `kiosk` section:

```json
{
  "roles": ["controller", "kiosk"],
  "controller": { "firstAdmin": { "name": "observer", "pin": true } },
  "kiosk": { "hideCursor": true, "pins": ["olga"] }
}
```

For `hvo-roof` and the Mac app on a Mac, a `client` section, and the admin who makes the Mac app's device key:

```json
{
  "roles": ["cli", "mac-app"],
  "client": {
    "controller": "https://roofpi.local:8443",
    "caSha256": "FB:26:8B:E2:34:20:52:AF:FB:D8:05:14:87:EB:B5:D6:4A:D8:6D:4E:65:BC:35:26:C7:D2:31:11:2A:D1:C0:76",
    "trustInKeychain": true
  },
  "macApp": { "admin": "ada" }
}
```

| Member | Values | Default |
|--------|--------|---------|
| `schema` | `1` | `1` |
| `roles` | Any of `controller`, `rig`, `kiosk`, `cli`, `mac-app` | Required |
| `controller.connection` | `private-ca`, `own-certificate`, `self-signed`, `http` | `private-ca` |
| `controller.httpsPort` | The controller's API over HTTPS (the deploy script's `HTTPS_HOST_PORT`) | `8443` |
| `controller.httpPort` | The controller's API over HTTP, with `http` (`HOST_PORT`) | `8080` |
| `controller.webPort` | The web UI (`WEB_HOST_PORT`) | `8088` |
| `controller.hostNames` | Other short names clients use for the controller, such as `roof` ([Certificates](#certificates)) | None |
| `controller.domains` | The domains clients reach it under, such as `observatory.example` | None. The wizard names the search domains in `/etc/resolv.conf`, but lists none unasked. |
| `controller.firstAdmin.name` | The first person, an admin, added when the controller has no admin yet. Their password, and their PIN with `"pin": true`, are typed, or given in files ([Passwords and PINs](#passwords-and-pins)). | None. The wizard asks. |
| `controller.firstAdmin.pin` | `true` to give the first admin a PIN for the kiosk | `false` |
| `controller.camera.baseUrl` | The Blue Iris server the controller shows the camera from, such as `http://192.168.0.4:81`. It must have no path, and no user name or password. | None: the camera is left as it is. |
| `controller.camera.userName` | A Blue Iris user that may only view, when the server asks for one. Its password is typed, or given in a file. | None |
| `controller.telemetryEndpoint` | An OTLP/HTTP endpoint for the controller's telemetry, such as `http://collector:4318` | None: export is off |
| `controller.importSettingsFrom` | The full path of an `appsettings.Local.json` to start a controller that has no settings with ([Settings from a backup](#settings-from-a-backup)). Not recorded. | None |
| `controller.rig.timeScale` | A rig only: how many times as fast as real time the emulated roof runs, from `0.1` to `100` | `1` (the wizard offers the running emulator's) |
| `controller.rig.cameraFramesPerSecond` | A rig only: the emulated camera's frame rate, from `0.1` to `30` | `5` (the wizard offers the running emulator's) |
| `controller.rig.openToLan` | A rig only: `true` to publish its API and web UI on every address, not only on this machine's loopback address. Needs HTTPS. | `false` |
| `kiosk.hideCursor` | `true` to hide the console's cursor behind the kiosk, from the next reboot (`vt.global_cursor_default=0` in `/boot/firmware/cmdline.txt`). `false` leaves `cmdline.txt` as it is. | `true` |
| `kiosk.pins` | People on the controller, operators or admins, by the name they sign in with, to give a PIN for signing in at the kiosk. Someone who has one keeps it. Not recorded. | None |
| `cli.folder` | `~/.local/bin` or `/usr/local/bin` | `~/.local/bin` |
| `macApp.folder` | `/Applications` or `~/Applications` | `/Applications` |
| `macApp.admin` | An admin on the controller, by the name they sign in with, who signs in once so the installer can make the Mac app's device key. Their password is typed, or given in a file. | Required for the Mac app |
| `client.controller` | The controller `hvo-roof` and the Mac app connect to, as this machine reaches it, with its port, such as `https://roof.local:8443` | Required for either |
| `client.caSha256` | The SHA-256 fingerprint of the controller's private CA, as its installer's Done page or `cert show` gives it: the CA is fetched from the controller and trusted only when it matches. Colons between the pairs are optional. | None |
| `client.certificateSha256` | The SHA-256 fingerprint of the controller's self-signed certificate, which each connection is pinned to | None |
| `client.trustInKeychain` | On a Mac, `true` to trust the CA in your login keychain too, for Safari and Chrome ([Trusting the controller in a browser](#trusting-the-controller-in-a-browser)). Needs `client.caSha256`. | `false` |
| `rigConfirmation` | This machine's host name, for a rig on a machine with `/dev/i2c-1` | None |
| `httpConfirmation` | `http`, to serve plain HTTP where the controller does not already | None |

The `controller` section applies to a rig too. A section for a role that is not chosen is dropped. An answers file
has no place for a password, a PIN or a key: a member such as `password` is an error. The wizard never
saves `rigConfirmation` or `httpConfirmation`, so each machine is confirmed on its own. The wizard saves answers to `hvo-roof-answers.json` in the folder where the installer was started, unless you give another path.

### Passwords and PINs

A person types each password and PIN; none is saved in an answers file, the record or the log. The installer asks
only when a step needs one:

- the first admin's password, typed twice, when the controller has no admin yet;
- the first admin's PIN, typed twice, with `"pin": true`;
- the camera's password, when its files are made or its user changes;
- the kiosk's PIN for each person in `kiosk.pins` who has none yet, typed twice;
- the password of `macApp.admin`, typed once, when the Mac needs a new device key.

Without a terminal (`--answers` in a script), give each in a file that only you can read, and the installer reads
its first line: `--admin-password-file FILE`, `--admin-pin-file FILE`, `--camera-password-file FILE`,
`--sign-in-password-file FILE` for the Mac app's admin, and `--pin-file NAME=FILE` for each person's PIN
(`--pin-file olga=/root/olga-pin`). When a
step needs one that was not given, the installer stops with exit code 2 and changes nothing.

### Settings from a backup

`controller.importSettingsFrom`, the wizard's **Settings from a backup**, starts a controller with the settings saved
from another one. Give the full path of the saved `appsettings.Local.json`, such as a copy from a backup of
`/etc/hvo-roof/config` ([commissioning.md](commissioning.md#the-settings-file)).

- The installer copies it to `/etc/hvo-roof/config/appsettings.Local.json` as it is, and only while the controller has
  no settings file there. It never changes the settings of a controller that has them: change those in the web UI, or
  with `hvo-roof settings`.
- A controller that runs is redeployed to read them.
- The installer checks that the file holds a JSON object that sets no secret, and refuses the install before anything
  changes when it does not. A secret is a setting named `Key` or `Password`, or the camera's `BlueIris:UserName`: the
  settings file is one anyone on the machine can read, and the controller refuses one that sets a secret. The message
  names the setting, never its value. Take it out of the backup and put it in a file in `/etc/hvo-roof/secrets`
  instead; the installer asks for the camera's user and password itself. The deploy script's [deployment check](deployment.md#the-deployment-check) then reads it as the
  controller will, before the controller is replaced.
- The path is not recorded in the install record, since an import is done once. Running the same answers again
  changes nothing.

## The plan

`--plan`, and the wizard's review page, list each step under what it makes, with what it would do here:

- **create:** the step makes something new.
- **change:** the step changes something that is there.
- **unchanged:** it is already as it should be.
- **info:** for example, a port the controller will listen on.
- **blocked:** the step cannot go ahead. The line under it says why, and nothing is installed.

For example, for a test rig on a Linux workstation:

```text
$ hvo-roof-install --plan --answers rig.json
The plan for a test rig on rig1, 4.0.0:

Folders
  create     /etc/hvo-roof                                 the controller's configuration (0755)
  create     /etc/hvo-roof/secrets                         secrets the controller reads, one file per setting (0700)
  create     /etc/hvo-roof/https                           the controller's HTTPS certificate (0700)
  create     /etc/hvo-roof/ca                              the certificate authority's key (0700)
  create     /etc/hvo-roof/config                          settings files the controller reads (0755)
  create     /var/lib/hvo-roof                             the controller's data (0755)
  create     /var/lib/hvo-roof/identity                    people, sessions and API keys (0700)
  create     /var/lib/hvo-roof/settings-secrets            secrets set through the API (0700)
Files
  create     /etc/hvo-roof/ca.crt                          the certificate authority clients trust (its key stays in /etc/hvo-roof/ca) (a new CA, which may issue only for names in local, localhost, rig1, and private addresses)
  create     /etc/hvo-roof/secrets/Kestrel__Certificates__Default__Password  the certificate file's password (random, never shown) (0600)
  create     /etc/hvo-roof/https/roof-controller.pfx       the controller's certificate, from its CA, for rig1 (for 3 names and 3 addresses, until 2027-11-02)
  create     /etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__0__Key  the installer's operator key: the deploy script's Status and verified Stop (never shown) (installer-operator (RoofOperator): a new random key, never shown)
  create     /etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__1__Key  the installer's admin key: adds the first admin (never shown) (installer-admin (RoofAdmin): a new random key, never shown)
  create     /etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__2__Key  the web UI's own key for Stop (never shown) (web-ui-stop (RoofViewer): a new random key, never shown)
  create     /etc/hvo-roof/secrets/BlueIris__BaseUrl       the camera the controller shows: the HAT emulator's (http://hat-emulator:5290)
  create     /etc/hvo-roof/secrets/BlueIris__Password      the camera's password: none (empty)
  create     /etc/hvo-roof/secrets/BlueIris__UserName      the camera's user: none, the emulator asks for none (empty)
  create     /etc/hvo-roof/install.json                    the install record: the roles, choices and versions (no secrets) (0644)
Users
  create     tester                                        the first admin: signs in to the web UI and adds everyone else (added through the controller's API as RoofAdmin, with a password)
Packages
  info       bash, curl, setsid or perl, jq or python3     what the deploy script runs (found bash, curl, setsid, jq)
Containers
  create     hat-emulator                                  the HAT emulator: the roof, drive and limit switches the rig drives (release 4.0.0's image by digest, on the hvo-emulator network, its control API on 127.0.0.1:5290)
  create     roof-controller                               the controller, against the HAT emulator (release 4.0.0 by digest, through the deploy script)
Ports
  info       8443                                          the controller's API (HTTPS) (free; roof-controller will listen on it)
  info       8088                                          the web UI (free; roof-controller will listen on it)

21 to create, 0 to change, 0 unchanged.

The install asks for the first admin's password (or give it with --admin-password-file FILE).
Installing a test rig needs root: run the installer with sudo.
```

### Running it again

Each step checks the machine just before it runs, and changes only what differs. A folder with the wrong mode has its
mode set, and its contents are never touched. The record is rewritten only when it would say something new. A second
run of the same answers changes nothing: "Nothing to change: this machine is already as the answers describe."

After a failed step, running the installer again carries on from where it stopped. So does it after Ctrl+C, which
stops the installer between steps. The installer never kills a deploy script that has started. A Ctrl+C at the terminal
(outside the wizard, which reads its own keys) reaches the script too, which stops and puts the old controller back if
it was replacing it; the installer then says what runs as the controller, and exits 130. Once the new controller has
passed the script's checks, the script ignores Ctrl+C and runs to its end, as it does for an interrupt only the
installer gets (`kill -INT`): the installer then stops before its next step and exits 130, or finishes when no step
was left. An API key entry that a stopped install left with its name but no key is finished by the next run.

The deploy script runs with a clean environment. Only `PATH`, `HOME`, `USER`, `LOGNAME`, `LANG`, `TERM`, `TMPDIR`
and Docker's own `DOCKER_HOST`, `DOCKER_CONFIG`, `DOCKER_CERT_PATH` and `DOCKER_TLS_VERIFY` reach it from the
installer's; the installer gives it the rest. A setting of the deploy script's in your shell never changes what the
installer deploys. The installer also sets `REQUIRE_IDLE_ROOF`: the script then stops before it replaces the
controller when it cannot read the roof's status, as well as when the roof moves
([Deploying](deployment.md#deploying-with-the-script)).

## The kiosk

On the controller's Pi, the installer sets up the touchscreen kiosk ([kiosk.md](kiosk.md)) as the steps in
[Install](kiosk.md#by-hand) do by hand:

- **Packages:** the display, touch and font libraries, with apt, when any is missing.
- **The user:** `hvo-kiosk`, a system user in `video`, `input` and `render`.
- **The program:** `/opt/hvo-roof-kiosk/hvo-roof-kiosk`, from the release's `hvo-roof-kiosk` tarball, checked against
  the size and SHA-256 in `release.json`, and the program inside it against its SHA-256 too. The program it replaces
  is kept as `hvo-roof-kiosk.previous`, to go back to ([kiosk.md](kiosk.md#by-hand)).
- **The device key:** the controller's own kiosk key (`kiosk`, a `RoofViewer` key marked `Kiosk` and `Local`), made
  like the installer's other keys, and redeployed to the controller once the roof is idle. The kiosk's copy is
  `/etc/hvo-roof-kiosk/device-key`, `0400`, which only `hvo-kiosk` reads.
- **The settings:** `/opt/hvo-roof-kiosk/appsettings.Local.json`, with the controller on `localhost`:
  `https://localhost:8443/` trusting the installer's CA (`/etc/hvo-roof/ca.crt`), pinning the controller's certificate
  when it is self-signed or your own, or `http://localhost:8080/` over plain HTTP. Settings changed by hand there are
  kept.
- **The service:** `hvo-roof-kiosk.service` and the backlight's udev rule, applied, then the service enabled and
  started. It is started again when its program, key, settings, unit or rule change.
- **The cursor:** with `kiosk.hideCursor`, `vt.global_cursor_default=0` in `/boot/firmware/cmdline.txt`. It takes
  effect at the next reboot, which the Done page reminds you of.
- **PINs:** for each person in `kiosk.pins`, a PIN typed twice, or read from `--pin-file NAME=FILE`, set through the
  controller's API. A viewer, or a name that is not on the controller, blocks the plan; someone with a PIN keeps it.

`hvo-roof-install cert` keeps the kiosk's settings with the certificate: renewing a self-signed certificate re-pins
it, and restarts the kiosk. A certificate from the installer's CA needs no change.

## hvo-roof and the Mac app

On your own Linux machine or Apple silicon Mac, the installer sets up `hvo-roof` ([cli.md](cli.md)) and the Mac app
([mac.md](mac.md)), connected to a controller that already runs. Run it as yourself, without `sudo`. For example, for
both on a Mac, with the answers above:

```text
$ hvo-roof-install --plan --answers mac.json
The plan for hvo-roof and the Mac app on studio, 4.0.0:

Folders
  unchanged  /Users/roy/.local/bin                         your programs (there already, and kept as it is)
  create     /Users/roy/Library/Application Support/HVO Roof  the Mac app's settings and device key: yours alone (0700)
Files
  create     /Users/roy/.local/bin/hvo-roof                hvo-roof: the roof from the command line, and its terminal UI (release 4.0.0's, 0755)
  create     /Users/roy/.config/hvo-roof/credentials.json  hvo-roof's connection: the controller and how it is trusted (you sign in with hvo-roof login) (https://roofpi.local:8443, trusting its CA (FB:26:8B:E2…))
  create     /Applications/HVO Roof.app                    the Mac app (release 4.0.0's, without macOS's quarantine mark)
  create     /Users/roy/Library/Application Support/HVO Roof/ca.crt  the controller's CA: the Mac app trusts the controller's certificate by it (https://roofpi.local:8443, trusting its CA (FB:26:8B:E2…))
  create     /Users/roy/Library/Application Support/HVO Roof/device-key  the Mac app's device key: a Viewer's, which shows the roof and offers Stop when no one is signed in (a Viewer key (mac-studio-roy) made by ada, who signs in once; 0600, yours alone)
  create     /Users/roy/Library/Application Support/HVO Roof/appsettings.Local.json  the Mac app's settings: the controller's address, how it trusts it, and its key (https://roofpi.local:8443, trusting its CA (FB:26:8B:E2…))
  change     /Users/roy/Library/Keychains/login.keychain-db  trusts the controller's CA in your login keychain: Safari and Chrome open the web UI without a warning (HVO Roof CA (roofpi, 2026-10-01), trusted for websites: macOS asks for your password)
  create     /Users/roy/.config/hvo-roof/install.json      the install record: the roles, choices and versions (no secrets) (0600)
Checks
  run        HVO Roof.app --check                          opens the app's window once, and closes it: it starts with these settings (after the changes above)

8 to create, 1 to change, 1 unchanged, 1 check to run.

The install asks for the Mac app's admin's password (or give it with --sign-in-password-file FILE).
```

### Trusting the controller

With a private CA, the installer fetches the CA from the controller's `/ca.crt`, which anyone may read, and checks
that the certificate the controller presents was issued by it. It keeps the CA only when the CA's SHA-256 fingerprint
is the one you gave: the one on the Done page of the controller's installer, or from `sudo hvo-roof-install cert show`
on the controller. A CA with another fingerprint blocks the plan, and nothing is changed. Check the fingerprint on
the controller itself, not in a message that could have been changed on its way.

With a self-signed certificate, `hvo-roof` and the Mac app pin it instead. With a certificate of your own, or plain
HTTP, they trust the controller as this machine trusts any website.

### hvo-roof

- **The program:** the release's `hvo-roof` for this machine (linux-x64, linux-arm64 or osx-arm64), checked against
  the SHA-256 in `release.json`, in `~/.local/bin` or `/usr/local/bin`. `/usr/local/bin` is offered only when you may
  write to it without `sudo`. The program it replaces is kept as `hvo-roof.previous`.
- **The connection:** `~/.config/hvo-roof/credentials.json` (or under `$XDG_CONFIG_HOME`), `0600`, with the
  controller's address and its CA or pin. A sign-in saved there for the same controller is kept; one for another
  controller is removed.

Then sign in with `hvo-roof login`. Put `~/.local/bin` on your `PATH` if it is not.

### The Mac app

- **The app:** `HVO Roof.app` from the release's zip, checked against the SHA-256 in `release.json`, in
  `/Applications` or `~/Applications`. The app is signed but not notarised, so the installer unpacks it without macOS's
  quarantine mark (`ditto --noqtn`), and removes a mark that is there: macOS then opens it without a warning. The app
  it replaces is kept as `HVO Roof.app.previous` in the settings folder.
- **Its settings folder:** `~/Library/Application Support/HVO Roof`, `0700`, with:
  - `ca.crt`, the controller's CA, with a private CA;
  - `device-key`, `0600` ([The Mac app's device key](#the-mac-apps-device-key));
  - `appsettings.Local.json`, with the controller's address, the CA's file or the certificate's pin, and the key's
    file. Settings you change there by hand are kept.
- **The check:** once the rest is in place, the installer opens the app with `--check`, which opens its window with
  these settings and closes it. It stops the install when the app cannot start. Over SSH, where the app cannot open a
  window, the plan says so, and you open HVO Roof on the Mac to check it.
- **The keychain,** with `client.trustInKeychain`: the CA is trusted for websites in your login keychain. macOS asks
  for your password, on the Mac's own screen: over SSH, the plan is blocked. The installer looks for the CA there by
  its SHA-256, not its name, and reads its trust settings: once it is trusted for websites, a later run changes
  nothing, even over SSH or with the controller out of reach. A certificate of the same name, or the CA with its trust
  set to deny, does not count.

### The Mac app's device key

The Mac app shows the roof, and offers Stop, with a device key of its own: an API key with the Viewer role, named
`mac-HOST-USER` after the Mac and you. People sign in in the app to open or close the roof.

To make the key, the admin in `macApp.admin` signs in to the controller's API once, with their password typed in the
wizard or given with `--sign-in-password-file FILE`. The installer adds the key, writes it straight to `device-key`
(`0600`, yours alone), and ends the admin's session. The key is never shown, logged, or put on the clipboard, and the
password is not kept.

A later run checks the key with the controller, and keeps one it takes: it needs no password then. A key the
controller refuses is replaced, and a Mac that had a key of its name gets a new secret for it. A key of that name that
is not a Mac's (a kiosk key, another role's, or one from the secrets folder) is left alone: the install stops there,
with no key written, until you remove it on the web UI's People page.

### Trusting the controller in a browser

The web UI is served with the same certificate as the API, so a browser warns until it trusts the controller's CA
too. Get the CA, and check its fingerprint before you trust it:

```bash
curl -fsSk -o hvo-roof-ca.crt https://roof.local:8443/ca.crt   # -k: the fingerprint is checked next
openssl x509 -in hvo-roof-ca.crt -noout -subject -fingerprint -sha256
```

On a Mac, the installer's settings folder already holds it, checked: `~/Library/Application Support/HVO Roof/ca.crt`.

- **Safari and Chrome on a Mac:** tick the keychain in the wizard, or give `client.trustInKeychain`. By hand:
  `security add-trusted-cert -r trustRoot -p ssl -k ~/Library/Keychains/login.keychain-db hvo-roof-ca.crt`, which asks
  for your password.
- **Chrome and Chromium on Linux** read the NSS database in your home folder:
  `certutil -d sql:$HOME/.pki/nssdb -A -t "C,," -n "HVO Roof CA" -i hvo-roof-ca.crt` (from `libnss3-tools`). Restart
  the browser.
- **Command-line tools on Linux** (`curl`, `wget`) read the system's store: on Debian, Ubuntu and Raspberry Pi OS,
  `sudo cp hvo-roof-ca.crt /usr/local/share/ca-certificates/hvo-roof-ca.crt && sudo update-ca-certificates`.
- **Firefox** may keep its own list: if it warns, open Settings → Privacy & Security → View Certificates →
  Authorities, import `hvo-roof-ca.crt`, and tick "Trust this CA to identify websites".

If the controller makes a new CA (`cert --new-ca`), remove the old one and trust the new one the same way. Running
the installer again with the new fingerprint replaces the CA in `hvo-roof`'s connection and the Mac app's settings,
and trusts the new one in the keychain; remove the old one there with Keychain Access.

## Certificates

The controller serves its API and the web UI over HTTPS, unless you choose plain HTTP. `controller.connection` says
how clients trust it:

| Connection | The certificate | What each client does |
|------------|-----------------|-----------------------|
| `private-ca` (the default) | Issued by the installer's own certificate authority (CA), made on the controller's machine. | Trusts the CA once. A renewed certificate then needs nothing more. |
| `own-certificate` | Yours, from your own CA, put in place with `cert import`. | Nothing, when it already trusts your CA. |
| `self-signed` | Signed by itself. | Pins it, and pins it again after each renewal. |
| `http` | None. Keys, session tokens and PINs cross the network unencrypted. | Nothing. Use it only on a network you trust: a person types `http` to confirm. |

### The installer's CA

The CA and the certificates it issues use ECDSA P-256 keys and SHA-256. They meet the rules that browsers and Apple's
platforms set for a server certificate.

- **The CA** lasts 10 years. Its key is made on the machine, straight into a file only root can read, and never leaves
  it. The CA may issue only for these, so a client that trusts it refuses anything else it signs:
  - `local`, `localhost`, and the short names and domains of the controller;
  - the private networks (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `fc00::/7`) and loopback.

  So a stolen Pi, or the CA's key, cannot pass for a public site to the clients. It can still sign for any `.local`
  name, any name in a listed domain, and any private address, so keep the key safe, list only the domains clients use,
  and if a Pi or its key is lost, remove its CA from each client's trust.
- **The certificate** lasts 397 days, the most browsers accept. It is for:
  - the machine's short host name and each name in `controller.hostNames`, each alone, under `.local` and under each
    domain in `controller.domains`;
  - `localhost`;
  - the machine's private addresses, `127.0.0.1` and `::1`. Addresses on Docker and other virtual networks,
    link-local and public addresses, and IPv6 temporary and deprecated addresses, which do not last, are left out.

  The controller answers to the same names (its `AllowedHosts`).
- **A self-signed certificate** is for the same names, and lasts 825 days, the most Apple's platforms accept.

| File | Mode | Holds |
|------|------|-------|
| `/etc/hvo-roof/ca/ca.key` | `0600`, in a `0700` folder | The CA's private key |
| `/etc/hvo-roof/ca.crt` | `0644` | The CA's certificate, for clients |
| `/etc/hvo-roof/https/roof-controller.pfx` | `0600` | The certificate, its key, and its CA's certificate |
| `/etc/hvo-roof/secrets/Kestrel__Certificates__Default__Password` | `0600` | The file's password: random, never shown |

These are the paths [deployment.md](deployment.md#2-tls-certificate) uses: the deploy script serves the certificate
with `HTTPS_CERT_DIR=/etc/hvo-roof/https` and the default `HTTPS_CERT_FILE`.

### Trusting the CA

The Done page, `cert` and `cert show` print the CA's SHA-256 fingerprint. A client gets the CA from the controller at
`https://<host>.local:<HTTPS port>/ca.crt`, or as a copy of `/etc/hvo-roof/ca.crt`. Check the fingerprint the client shows
against the one the installer printed before trusting it. The TLS handshake cannot give the CA: the server leaves a
self-signed root out of it. The installer does this for `hvo-roof` and the Mac app
([Trusting the controller](#trusting-the-controller)); for a browser, see
[Trusting the controller in a browser](#trusting-the-controller-in-a-browser).

### Renewing

Every run of the installer, `--plan` included, warns when one of these applies:

- the certificate expires within 30 days, or has expired;
- the CA expires within 400 days, so that its last certificate still lasts its full 397 days;
- the certificate is not for a name or address clients use;
- the certificate's password does not open it.

It says none of these when the controller is recorded as serving plain HTTP, since a certificate left in place is not
served then.

`sudo hvo-roof-install cert` then makes what needs making, and changes only what differs:

- **A new CA** when the CA is missing or cannot be read, expires within 400 days, may not issue for a name the
  controller now has, or does not limit the names it may issue for. `--new-ca` makes one even when none of these apply.
  Every client must then trust the new CA.
- **The certificate again** when the CA is new, the names or addresses have changed, it expires within 30 days,
  another CA issued it, or its password does not open it. `--renew` issues it again even when none of these apply.
- **The modes** of the files, when they differ.

With nothing recorded yet, such as a controller the deploy script runs, it uses the defaults: the installer's CA,
unless a certificate that this machine's CA did not issue is in place, which it keeps as yours. It then says which
connection to choose when you install the controller, so the certificate is kept. Without root it cannot read the
certificate to tell, so with nothing recorded even `cert --plan` needs `sudo`. When a record is there but cannot be
read, it refuses until the record is fixed or removed, since the record may say to keep the certificate.

It refuses to issue while this machine's clock is before the CA's start, and `--plan` shows it as blocked. If the clock
is wrong, set the time (NTP) and run it again. If the clock is right, the CA was made while it was ahead: make a new one
with `cert --new-ca`.

It prints the plan, each step, and the certificate as `cert show` does. It never moves the roof.

The controller serves a new certificate once it is deployed again. When the record says the installer runs the
controller, `cert` then redeploys it, through the deploy script as an install does:

- **Only while the roof is idle.** It reads the controller's status first, and again just before the deploy script
  runs. While the roof moves, it leaves the controller as it is and says to run `sudo hvo-roof-install cert --redeploy`
  once the roof is idle.
- **Only when the certificate is all that would change.** When more would, it leaves the controller as it is and says
  to run `sudo hvo-roof-install`, which redeploys it with the new certificate. More would change with a new release,
  for example, or with camera settings or API keys that an install which stopped before its redeploy wrote. It checks
  this again just before the deploy script runs, after you agree.
- **Only once you agree.** It asks `Redeploy the controller now? [y/N]`. With no one at a terminal, such as in a
  script, it does not ask and does not redeploy. `--redeploy` redeploys without asking. `--no-redeploy` puts the
  certificate in place and stops there.

The deploy script stops the roof with a verified Stop before it replaces the controller, and puts the old one back if
the new one fails a check. A controller that is stopped serves the new certificate when it starts again. One that is not
deployed yet serves it once `hvo-roof-install` deploys it. When something else stands in the way, such as a controller
that Docker Compose made, `cert` says what it is: once that is dealt with, run `cert --redeploy`. After a Ctrl+C that
stopped the deploy script, it says what runs as the controller, and that `cert --redeploy` finishes. When the script ran
to its end anyway (see [Running it again](#running-it-again)), the controller serves the new certificate.

`cert --redeploy` also redeploys a controller that serves another certificate than the one in place, such as after a
renewal that was not redeployed. On a machine that cannot reach GitHub, give the release's folder with `--release DIR`,
as for an install ([The release it installs](#the-release-it-installs)). It exits with code 3 when it could not
redeploy: the roof moves, more than the certificate would change, or no controller is deployed. So does `cert` when you
agreed to the redeploy and the check just before the deploy script refuses it. The certificate is in place either way.

With nothing recorded, such as a controller the deploy script runs, it never redeploys, and `--redeploy` is refused
before anything changes. When the roof is idle, run the deploy script again
([Deploying](deployment.md#deploying-with-the-script)).

`cert --plan` shows what `cert` would do, the redeploy included, and changes nothing. It runs without root, but then
cannot check the files that only root can read.

### Your own certificate

`sudo hvo-roof-install cert import FILE` puts your certificate in place and records that the controller serves your
own. The file is one of these:

- a PKCS#12 file (`.pfx`, `.p12`) with its key;
- PEM (`.crt`, `.pem`) with its chain and its key, or with `--key FILE` for the key.

When the file or the key has a password, the installer asks for it, or reads it from `--password-file FILE`. The
password opens the file only: the installer writes the controller's own file with a new random password. It refuses a
certificate that has expired, is not yet valid, is a CA's, or is not for a server, and a key whose password needs more
than 300,000 iterations to derive its key, the most that .NET opens in a PKCS#12 file (export it again with fewer). It
warns when the certificate is not for a name or address clients use, and when it lasts longer than Apple's platforms
accept. Importing the same certificate again changes nothing, unless its chain has changed. `--plan` checks the file and
shows what would change. It then redeploys the controller to serve it, as `cert` does ([Renewing](#renewing)), and takes
`--redeploy` and `--no-redeploy` too.

The installer never renews your certificate. Before it expires, import the new one.

## The record and the log

The record says which roles are installed, with their choices and the release. Every later run reads it, for these
purposes:

- to offer the same choices;
- to refuse a test rig on the real HAT's machine;
- from #72, to upgrade, roll back or uninstall.

It is written last, once everything before it is in place, and it holds no secret.

| File | Mode | Holds |
|------|------|-------|
| `/etc/hvo-roof/install.json` | `0644` | The machine's roles: the controller, a rig on Linux, the kiosk. |
| `~/.config/hvo-roof/install.json` | `0600`, in a `0700` folder | Your roles: `hvo-roof`, the Mac app, a rig on a Mac. |

The log records the following, each with the time:

- what the installer found;
- each command it ran, and how the command ended (the last 40 lines of its output);
- each change it made.

A secret is written as `[secret]`, and a command that handles one is logged without its input or output. `--plan`
writes no log.

| Run as | Log | Mode |
|--------|-----|------|
| root | `/var/log/hvo-roof-install.log` | `0640`, owned by root |
| you, on Linux | `$XDG_STATE_HOME/hvo-roof/install.log`, or `~/.local/state/hvo-roof/install.log` | `0600` |
| you, on a Mac | `~/Library/Logs/hvo-roof-install.log` | `0600` |

## Tests

Nothing here needs the Pi, the HAT or the roof. The tests (`tests/HVO.RoofControllerV4.RPi.Tests/Installer`) run
the installer on a fake machine: a Pi with or without the HAT's devices, a Linux workstation, or a Mac. The fake
machine has files, modes, containers and ports, and its programs are stubbed. The tests check:

- the plan for each role on each machine;
- the guards and their messages;
- that a second run changes nothing;
- that answers files round-trip;
- that no secret reaches an answers file, the record or the log;
- the certificates: the CA's name constraints and the names on the certificate it issues, that the key files are made
  owner-only and never printed, renewal before expiry, a changed name or a new CA, importing a PFX or PEM with its
  chain, and refusing a certificate the controller could not serve;
- the kiosk's steps (`InstallerKioskTests`): its files, modes and owners, its settings for each way the controller is
  reached, its PINs, a rollback copy on update, and its settings following a renewed certificate;
- `hvo-roof` and the Mac app (`InstallerClientTests`): the release's program for each platform, the controller's CA
  saved only when its fingerprint is the one given, a connection to another controller replacing the old sign-in, the
  Mac app without its quarantine mark, its device key made through the controller's API (a real one, in process), its
  settings kept when you changed them, `--check`, and the login keychain;
- the exit codes.

The controller's side, `GET /ca.crt` and the `https_certificate` health check, has its own tests
(`Security/CaCertificateEndpointTests` and `HealthChecks/RoofCertificateHealthCheckTests`).

The wizard is drawn on Terminal.Gui's in-memory driver, with the same pages, keys, colours and work off its thread as
in a terminal.

CI publishes the three builds and checks that each is the right platform. It also checks what the linux-x64 build
prints for `--version` and `--plan`.

### The rig end to end

`tests/installer/rig-scenario.sh`, the Scenarios workflow's `installer-rig` job, installs a test rig for real. It uses
the published installer, real Docker and the HAT emulator. The release is built as the release workflow builds one:
the controller's and the emulator's images, each an index of this machine's platform, built from its Dockerfile, and
an empty image for the other platform. They go in a registry of the run's own on loopback, with the `release.json`
that names them by digest. The script checks:

1. **Install.** `--plan` changes nothing. The install as root with `--answers`, `--release` and
   `--admin-password-file` then runs the release's images by digest, published on loopback only. The folders and files
   have their modes. The controller answers over HTTPS with the installer's CA and reports the emulated HAT, the web UI
   answers, and the first admin signs in with the password from the file. No key or password is in the output, the
   log, the record or the containers' configuration.
2. **Again.** The same answers change nothing and replace nothing.
3. **hvo-roof.** The installer, run as you and not as root (in a home of the run's own, so yours is untouched),
   installs the release's `hvo-roof` with the rig as its controller, trusting the rig's CA by its fingerprint. A wrong
   fingerprint is refused (exit 3) with nothing installed. Then `hvo-roof` is in `~/.local/bin` (`0755`) and is the
   release's, and its connection holds the rig's CA (`0600`). `hvo-roof login` signs in with the password on standard
   input, and `hvo-roof status` reports the emulated HAT. The same answers again change nothing and keep the session. No
   password, key or session is in the output, the log or the record.
4. **Change.** A new time scale replaces the emulator and redeploys the controller against it.
5. **Certificate.** `cert --renew --redeploy` serves a new certificate that the CA from before the renewal verifies,
   and leaves that CA unchanged. `hvo-roof`, which trusts the CA rather than the certificate, still signs in.

Throughout, the roof does not move: the relay register stays 0, and the emulator records no direction relay closing
and no violation.

It needs Docker with buildx, the .NET SDK, `curl`, `jq`, `openssl`, `ss`, and `sudo` without a password that reaches
the same Docker daemon. It refuses to run on a Raspberry Pi. It also refuses on a machine that has a controller or a
rig: `/etc/hvo-roof`, `/var/lib/hvo-roof`, the installer's log, or its containers or network. It removes all of them
when it ends. The ports it uses must be free: the controller's 8443 and 8088, the emulator's 5290, and its registry's
15001. `RIG_HTTPS_PORT` and `RIG_WEB_PORT` move the controller off 8443 and 8088, and `RIG_REGISTRY_PORT` moves the
registry. `RIG_RESULTS_DIR` writes each check's result and timing to `rig-scenario.md`.

### The kiosk end to end

The CI workflow's `installer-kiosk` job runs the installer's kiosk steps for real, as root on GitHub's arm64 runner
with systemd (`InstallerKioskSystemTests`). They install the kiosk program the build published for linux-arm64, from a
release folder, with a kiosk key and a CA made for the test, and the test checks the apt packages, the `hvo-kiosk`
user and its groups, the files with their modes and owners, that `hvo-roof-kiosk.service` runs as `hvo-kiosk` and logs
where it draws, and that a second run changes nothing; then it removes what it made. The controller's side (its
container, and the redeploy that gives it the kiosk's key) needs the Pi's HAT, so the fake machine's tests cover it. The
runner has no screen or touchscreen, so the kiosk's display stays one of its assumptions
([kiosk.md](kiosk.md#assumptions)). Anywhere without `HVO_KIOSK_INSTALL=1` and `HVO_KIOSK_PROGRAM` the test is skipped.

### The Mac app end to end

The CI workflow's `installer-mac` job installs the Mac app for real on GitHub's macOS runner, as the person
(`InstallerMacSystemTests`). It takes the app the build zipped, marks the zip as a browser marks a download, and
installs it from a release folder into `~/Applications` in a home of the test's own. The controller is its API in
process, so the device key is really made through it by an admin who signs in once. The test checks that the app
carries no quarantine mark and is still signed, the settings folder is `0700`, the device key is `0600` and is a
viewer's key the controller takes, the CA's fingerprint is the one given, the settings are valid, `--check` opens the
app's window with them, and a second run changes nothing. No key or password is in the output or the log. Anywhere
without `HVO_MAC_INSTALL=1` and `HVO_MAC_APP_ZIP` the test is skipped.

A second test reads a keychain as macOS writes it: it adds a CA to a keychain of its own, and checks that the
installer finds it by its SHA-256 in `security find-certificate -Z`'s listing. With `HVO_MAC_TRUST_SETTINGS=1` it
also trusts the CA for websites in the admin trust settings and checks that the installer reads that from their
export, then removes the trust and the keychain. macOS asks in a dialog before `sudo` may change those settings, and a
runner has no one to answer it, so the job first allows the change with `sudo security authorizationdb write
com.apple.trust-settings.admin allow`; every command the test runs fails it after two minutes rather than waiting. It
runs with `HVO_MAC_INSTALL=1`.

### Refreshing the screenshots

The wizard's tests save each page as ANSI. CI draws the pages as SVG and keeps them as the `installer-renders-<run id>`
artifact. To refresh the pictures above, download that artifact from a green run and copy its `.svg` files into
`docs/images/install/`. To draw them locally, run this from `src/`:

```bash
HVO_INSTALLER_RENDERS_DIR=/tmp/installer-renders \
  dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests --filter "FullyQualifiedName~.Installer.InstallerWizard"
for ans in /tmp/installer-renders/*.ans; do
  python3 ../tests/cli/ansi-to-svg.py "$ans" "../docs/images/install/$(basename "${ans%.ans}").svg" --title hvo-roof-install
done
```
