Upgrade from 4.4.0 while the emulated or installed controller is idle, through the normal installer workflow.
The existing identity and settings schemas do not change. Default installations keep Alpaca off and publish no new
port. `RoofController:Alpaca:AllowCommands` remains `Off`; enabling the server never enables Open or Close.

To opt in, use the installer's **Alpaca server** option with the host's private IPv4 LAN address and TCP port
(default `11111`), or follow [Alpaca container deployment](../alpaca.md#container-deployment). Compose users must
explicitly add the new `docker-compose.alpaca.yaml` overlay; the base file alone keeps the old behavior.

If migrating a server previously run from source, preserve its existing `alpaca-devices.json` in the new persistent
Alpaca data directory before first start. Never replace it with freshly generated IDs: astronomy clients recognise
these devices by their GUIDs. The default host directory is `/var/lib/hvo-roof-alpaca`, owned by UID/GID `1655`, mode
`0755`; the public ID file is `0644`. Preserve the separate server account key through the normal secrets workflow.
The installer now includes these IDs in backups. Old backups without them remain valid.

Rollback keeps the dedicated Alpaca data directory; older images and installers simply ignore it. It is a sibling
of `/var/lib/hvo-roof`, so the old installer's root-owned backup walk still works after rollback and can hand over to
a newer installer on the next upgrade. Keep the new version's backup when rolling back: an older installer's
backup does not include the separate Alpaca IDs. The deploy script restores the discovery responder to match the rolled-back controller's settings and image. Backups are unchanged apart from the validated
Alpaca identity file; backup encryption remains a separate follow-up.
