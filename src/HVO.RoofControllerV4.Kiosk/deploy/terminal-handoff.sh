#!/bin/sh
# Run only by systemd ExecStopPost, after the kiosk process and its native descriptors have gone.
# This stays independent of the app binary so program rollback still works with the installed unit.
set -eu
if [ "${SERVICE_RESULT:-}" != success ] || [ "${EXIT_CODE:-}" != exited ] || [ "${EXIT_STATUS:-}" != 80 ]; then
    exit 0
fi
# Queue the conflicting login unit: waiting here would deadlock its ordering after the kiosk.
# These fixed commands accept no UI input. The login unit always authenticates through /bin/login.
if /usr/bin/systemctl --no-block restart hvo-roof-terminal.service && /usr/bin/chvt 1; then
    exit 0
fi
echo 'Kiosk terminal handoff failed. From another Linux login, use sudo systemctl start hvo-roof-terminal.service or sudo systemctl start hvo-roof-kiosk.service.' >&2
exit 1
