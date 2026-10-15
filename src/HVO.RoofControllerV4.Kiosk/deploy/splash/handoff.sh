#!/bin/sh
# Bound all waits so a splash failure cannot hold boot indefinitely.
# Finish Plymouth's VT restoration before Avalonia selects its DRM mode. Retaining
# KD_GRAPHICS lets getty's later terminal reset restore the old console resolution,
# cropping a kiosk framebuffer whose dimensions differ from the boot console.
if /usr/bin/timeout 2 /usr/bin/plymouth --ping; then
    echo "HVO splash active; restoring console and releasing DRM before kiosk."
    if /usr/bin/timeout 8 /usr/bin/plymouth quit; then
        echo "HVO splash handoff complete."
    else
        echo "HVO splash handoff failed; retrying bounded quit."
        /usr/bin/timeout 3 /usr/bin/plymouth quit || true
    fi
else
    echo "HVO splash absent; continuing kiosk startup."
fi
exit 0
