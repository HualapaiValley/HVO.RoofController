#!/usr/bin/env bash
# The published hvo-roof command against a running controller (#45), in a real terminal: a few commands, then
# 'hvo-roof ui' in a tmux pane, where every page is drawn, F9 stops a roof that another client started, Esc leaves the
# interface open, and F10 closes it with exit code 0. Then the terminal closing during 'hvo-roof open' (a tmux window
# killed under its shell, which delivers SIGHUP twice) and SIGTERM during 'hvo-roof close': each sends Stop, the roof
# stops short of the limit, and 'close' exits 130 (a closed terminal leaves no one to read the exit code of 'open'). The
# screens are saved as text, and drawn as SVG images (tests/cli/ansi-to-svg.py), for the CI artifacts and the
# screenshots in docs/cli.md.
#
# It needs a controller whose roof may move (the emulated one: tests/emulator/compose-smoke-test.sh runs this script
# against the compose emulator profile), tmux and an admin API key. It touches no hardware, and it keeps its credentials
# in a directory of its own that it removes on exit. tmux runs as a server of its own (not the one you may be working
# in), so the interface sees only this script's environment: never your credentials or your HVO_ROOF_URL.
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
session="hvo-roof-smoke"
socket="hvo-roof-smoke-$$"
export HOME="${home}" XDG_CONFIG_HOME="${home}/.config" HVO_ROOF_URL HVO_ROOF_API_KEY

# The screens are drawn in colour, as a terminal that supports 24-bit colour shows them.
export COLORTERM=truecolor
unset NO_COLOR TMUX

# tmux on a server of its own, with no configuration: it starts with this script's environment.
t() {
    tmux -L "${socket}" -f /dev/null "$@"
}

fail() {
    echo "[terminal] FAIL: $*" >&2
    exit 1
}

# The motion command that signal_during runs in the background, until it ends.
background_pid=""

cleanup() {
    local status=$?
    # A failed check can leave it following the roof: SIGTERM makes it send Stop and end.
    if [ -n "${background_pid}" ]; then
        kill "${background_pid}" 2>/dev/null || true
        wait "${background_pid}" 2>/dev/null || true
    fi
    if t has-session -t "${session}" 2>/dev/null; then
        t capture-pane -p -t "${session}" >"${out}/last-screen.txt" 2>/dev/null || true
    fi
    t kill-server 2>/dev/null || true
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
    t capture-pane -p -t "${session}"
}

# stopped_short <name> <limit>: checks with 'status --json' that the roof is still, and not at the limit.
stopped_short() {
    local name=$1 limit=$2
    run "${name}" 0 status --json
    python3 - "${out}/${name}.txt" "${limit}" <<'PY' || fail "the roof did not stop short of ${limit}: $(cat "${out}/${name}.txt")"
import json, sys
status = json.load(open(sys.argv[1]))
sys.exit(0 if not status["isMoving"] and status["status"] != sys.argv[2] else 1)
PY
    echo "[terminal] the roof stopped short of the ${limit,,} limit"
}

# signal_during <name> <signal> <args...>: starts a motion command, sends it the signal once it follows the motion, and
# checks that it sent Stop, that the controller verified the stop, and that the command exited 130.
signal_during() {
    local name=$1 signal=$2 code=0 pid deadline
    shift 2
    "${cli}" "$@" >"${out}/${name}.txt" 2>&1 &
    pid=$!
    background_pid=${pid}
    deadline=$((SECONDS + 15))
    until grep -qF "Following the motion" "${out}/${name}.txt"; do
        kill -0 "${pid}" 2>/dev/null || fail "'hvo-roof $*' ended before it followed the motion: $(cat "${out}/${name}.txt")"
        [ "${SECONDS}" -lt "${deadline}" ] || fail "'hvo-roof $*' did not follow the motion within 15 s: $(cat "${out}/${name}.txt")"
        sleep 0.1
    done
    kill -s "${signal}" "${pid}"
    wait "${pid}" || code=$?
    background_pid=""
    [ "${code}" -eq 130 ] || fail "'hvo-roof $*' exited ${code} on SIG${signal}, not 130: $(cat "${out}/${name}.txt")"
    grep -qF "Interrupted: Stop sent." "${out}/${name}.txt" \
        || fail "'hvo-roof $*' did not say it sent Stop on SIG${signal}: $(cat "${out}/${name}.txt")"
    grep -qF "Stop acknowledged. Relay register verified de-energized." "${out}/${name}.txt" \
        || fail "the Stop that 'hvo-roof $*' sent on SIG${signal} was not verified: $(cat "${out}/${name}.txt")"
    echo "[terminal] SIG${signal} during 'hvo-roof $*': Stop sent and verified, exit 130"
}

# hangup_during <name> <args...>: runs a motion command at an interactive shell in a tmux window of its own, and closes
# the window once the command follows the motion, as closing a terminal or dropping an SSH session does: the kernel and
# then the shell each send it SIGHUP, well under a millisecond apart. Checks that it sent Stop and that the controller
# verified the stop. The shell goes with the window, so the exit code cannot be read.
hangup_during() {
    local name=$1 deadline
    shift
    t new-window -d -t "${session}" -n "${name}" "bash --norc --noprofile -i"
    t send-keys -t "${session}:${name}" "'${cli}' $* >'${out}/${name}.txt' 2>&1" Enter
    deadline=$((SECONDS + 15))
    until grep -qF "Following the motion" "${out}/${name}.txt" 2>/dev/null; do
        [ "${SECONDS}" -lt "${deadline}" ] || fail "'hvo-roof $*' did not follow the motion within 15 s: $(cat "${out}/${name}.txt" 2>&1)"
        sleep 0.1
    done
    t kill-window -t "${session}:${name}"
    deadline=$((SECONDS + 20))
    until grep -qF "Stop acknowledged. Relay register verified de-energized." "${out}/${name}.txt"; do
        [ "${SECONDS}" -lt "${deadline}" ] \
            || fail "'hvo-roof $*' did not send a verified Stop within 20 s of its terminal closing: $(cat "${out}/${name}.txt")"
        sleep 0.25
    done
    grep -qF "Interrupted: Stop sent." "${out}/${name}.txt" \
        || fail "'hvo-roof $*' did not say it sent Stop when its terminal closed: $(cat "${out}/${name}.txt")"
    echo "[terminal] the terminal closed during 'hvo-roof $*': Stop sent and verified"
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
    t capture-pane -e -N -p -t "${session}" >"${out}/$1.ans"
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

# The interface in a 120 x 36 pane. The pane stays after the command ends, so its exit code can be read. It first
# writes its HOME, to prove it runs with this script's environment (the variables are not written: one is a key).
t new-session -d -s "${session}" -x 120 -y 36 \
    "printf '%s\\n' \"\$HOME\" >'${out}/pane-home.txt'; '${cli}' ui; echo \"hvo-roof exited \$?\"; sleep 600"
wait_screen "the interface shows the live status" 30 "status live"
[ "$(cat "${out}/pane-home.txt")" = "${home}" ] || fail "the interface did not run with this script's environment"
rm -f "${out}/pane-home.txt"
wait_screen "Stop is on the screen" 5 "Stop roof (F9)"
save 01-roof

# Each page, by its frame title (the key bar names every page, so it proves nothing), then what the page reads from
# the controller.
for page in F2:Settings:"Read the settings (version" F3:People:"API keys" F4:System:"Health: " F5:Setup:"Credentials file:"; do
    IFS=: read -r key title text <<<"${page}"
    t send-keys -t "${session}" "${key}"
    wait_screen "the ${title} page" 15 "┤${key} ${title}├"
    wait_screen "the ${title} page's content" 15 "${text}"
    save "0${key#F}-${title,,}"
done

t send-keys -t "${session}" F1
wait_screen "the roof page again" 5 "┤F1 Roof├"

# Another client starts the roof; F9 in the interface stops it.
run open 0 open --no-wait
wait_screen "the interface shows the roof moving" 15 "Opening"
save 06-opening
t send-keys -t "${session}" F9
wait_screen "Stop is acknowledged and verified" 15 "Stop acknowledged. Relay register verified de-energized."
save 07-stopped

# Stop ended the motion: the roof is still, short of the open limit.
stopped_short stopped Open

t send-keys -t "${session}" Escape
sleep 1
screen | grep -qF "Stop roof (F9)" || fail "Esc closed the interface"

t send-keys -t "${session}" F10
wait_screen "F10 closes the interface with exit code 0" 15 "hvo-roof exited 0"
save 08-closed

# A command that has the roof moving loses its terminal, or gets SIGTERM from a service manager or 'kill'. Each sends
# Stop before it ends.
hangup_during hangup open
stopped_short hangup-stopped Open
signal_during term TERM close
stopped_short term-stopped Closed

run close 0 close
grep -q "Done. Roof: Closed" "${out}/close.txt" || fail "'hvo-roof close' did not end at the closed limit: $(cat "${out}/close.txt")"
echo "[terminal] PASS: the screens are in ${out}"
