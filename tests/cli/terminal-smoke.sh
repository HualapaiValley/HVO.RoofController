#!/usr/bin/env bash
# The published hvo-roof command against a running controller (#45), in a real terminal: a few commands, then
# 'hvo-roof ui' in a tmux pane, where every page is drawn, F9 stops a roof that another client started, Esc leaves the
# interface open, and F10 closes it with exit code 0. The screens are saved as text, and drawn as SVG images
# (tests/cli/ansi-to-svg.py), for the CI artifacts and the screenshots in docs/cli.md.
#
# It needs a controller whose roof may move (the emulated one: tests/emulator/compose-smoke-test.sh runs this script
# against the compose emulator profile), tmux and an admin API key. It touches no hardware, and it keeps its credentials
# in a directory of its own that it removes on exit.
#
#   HVO_ROOF_CLI=path/to/hvo-roof HVO_ROOF_URL=http://127.0.0.1:15195/ HVO_ROOF_API_KEY=... tests/cli/terminal-smoke.sh
#
# Settings (environment): TERMINAL_SMOKE_OUT, the directory for the saved screens (default: a new temporary one).
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

cli=${HVO_ROOF_CLI:?HVO_ROOF_CLI must name the hvo-roof executable}
: "${HVO_ROOF_URL:?HVO_ROOF_URL must be the controller address}"
: "${HVO_ROOF_API_KEY:?HVO_ROOF_API_KEY must be an admin API key}"
out=${TERMINAL_SMOKE_OUT:-$(mktemp -d)}
mkdir -p "${out}"

home=$(mktemp -d)
session="hvo-roof-smoke-$$"
export HOME="${home}" XDG_CONFIG_HOME="${home}/.config" HVO_ROOF_URL HVO_ROOF_API_KEY

fail() {
    echo "[terminal] FAIL: $*" >&2
    exit 1
}

cleanup() {
    local status=$?
    if tmux has-session -t "${session}" 2>/dev/null; then
        tmux capture-pane -p -t "${session}" >"${out}/last-screen.txt" 2>/dev/null || true
        tmux kill-session -t "${session}" 2>/dev/null || true
    fi
    rm -rf "${home}"
    exit "${status}"
}
trap cleanup EXIT

command -v tmux >/dev/null || fail "tmux is required"
command -v python3 >/dev/null || fail "python3 is required"

# run <name> <expected exit code> <args...>: runs a command, saves what it wrote, and checks its exit code.
run() {
    local name=$1 expected=$2 code=0
    shift 2
    "${cli}" "$@" >"${out}/${name}.txt" 2>&1 || code=$?
    [ "${code}" -eq "${expected}" ] || fail "'hvo-roof $*' exited ${code}, not ${expected}: $(cat "${out}/${name}.txt")"
    echo "[terminal] hvo-roof $* (exit ${code})"
}

screen() {
    tmux capture-pane -p -t "${session}"
}

# wait_screen <description> <timeout seconds> <text>: waits for the pane to show the text.
wait_screen() {
    local description=$1 timeout=$2 text=$3
    local deadline=$((SECONDS + timeout))
    until screen | grep -qF -- "${text}"; do
        if [ "${SECONDS}" -ge "${deadline}" ]; then
            screen >"${out}/timeout.txt" || true
            fail "timed out after ${timeout} s waiting for ${description}; the screen is in timeout.txt"
        fi
        sleep 0.25
    done
    echo "[terminal] ${description}"
}

# save <name>: saves the screen as text, with its colours (name.ans), and as an image (name.svg).
save() {
    screen >"${out}/$1.txt"
    tmux capture-pane -e -N -p -t "${session}" >"${out}/$1.ans"
    python3 "${here}/ansi-to-svg.py" "${out}/$1.ans" "${out}/$1.svg" --title "hvo-roof ui"
}

run version 0 --version
run help 0 --help
grep -q "ui" "${out}/help.txt" || fail "--help does not list 'ui'"
run whoami 0 whoami
run status 0 status
grep -q "Closed" "${out}/status.txt" || fail "the status does not say Closed: $(cat "${out}/status.txt")"
run usage 2 status --no-such-option
run health 8 health

# The interface in a 120 x 36 pane. The pane stays after the command ends, so its exit code can be read.
tmux new-session -d -s "${session}" -x 120 -y 36 \
    "'${cli}' ui; echo \"hvo-roof exited \$?\"; sleep 600"
wait_screen "the interface shows the live status" 30 "status live"
wait_screen "Stop is on the screen" 5 "Stop roof (F9)"
save 01-roof

# Each page, by its frame title (the key bar names every page, so it proves nothing), then what the page reads from
# the controller.
for page in F2:Settings:"Read the settings (version" F3:People:"API keys" F4:System:"Health: " F5:Setup:"Credentials file:"; do
    IFS=: read -r key title text <<<"${page}"
    tmux send-keys -t "${session}" "${key}"
    wait_screen "the ${title} page" 15 "┤${key} ${title}├"
    wait_screen "the ${title} page's content" 15 "${text}"
    save "0${key#F}-${title,,}"
done

tmux send-keys -t "${session}" F1
wait_screen "the roof page again" 5 "┤F1 Roof├"

# Another client starts the roof; F9 in the interface stops it.
run open 0 open --no-wait
wait_screen "the interface shows the roof moving" 15 "Opening"
save 06-opening
tmux send-keys -t "${session}" F9
wait_screen "Stop is acknowledged and verified" 15 "Stop acknowledged. Relay register verified de-energized."
save 07-stopped

tmux send-keys -t "${session}" Escape
sleep 1
screen | grep -qF "Stop roof (F9)" || fail "Esc closed the interface"

tmux send-keys -t "${session}" F10
wait_screen "F10 closes the interface with exit code 0" 15 "hvo-roof exited 0"
save 08-closed

run close 0 close
grep -q "Done. Roof: Closed" "${out}/close.txt" || fail "'hvo-roof close' did not end at the closed limit: $(cat "${out}/close.txt")"
echo "[terminal] PASS: the screens are in ${out}"
