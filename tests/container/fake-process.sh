#!/usr/bin/env bash
# A stand-in for the controller or the web UI, for supervisor-tests.sh. Its role is its file name (controller or ui, a
# symbolic link to this script). It is steered by files in FAKE_DIR:
#   <role>.exit-at-start   exit at once with the code in the file (a crash at start, or 75 for a requested restart)
#   <role>.term-delay      seconds to take over SIGTERM before exiting (the controller's roof stop), default 0
#   <role>.ignore-term     ignore SIGTERM altogether (only SIGKILL ends it)
#   <role>.exit-now        exit with the code in the file within 0.1 s, once (the file is removed)
# and writes:
#   events.log             "<role> start <pid>", "<role> term", "<role> exit <code>", one line each, in order
#   <role>.pid             its pid
#   <role>.env             its environment (NUL-separated)
#   <role>.args            its arguments, one per line
set -uo pipefail

role=$(basename "$0")
dir=${FAKE_DIR:-${RoofWeb__FakeDir:?}}
event() {
  printf '%s %s\n' "${role}" "$*" >>"${dir}/events.log"
}

printf '%s\n' "$$" >"${dir}/${role}.pid"
env -0 >"${dir}/${role}.env"
printf '%s\n' "$@" >"${dir}/${role}.args"
event "start $$"

if [[ -f "${dir}/${role}.exit-at-start" ]]; then
  code=$(<"${dir}/${role}.exit-at-start")
  event "exit ${code}"
  exit "${code}"
fi

on_term() {
  event term
  if [[ -f "${dir}/${role}.ignore-term" ]]; then
    return
  fi
  sleep "$(cat "${dir}/${role}.term-delay" 2>/dev/null || echo 0)"
  event "exit 0"
  exit 0
}
trap on_term TERM

while :; do
  if [[ -f "${dir}/${role}.exit-now" ]]; then
    code=$(<"${dir}/${role}.exit-now")
    rm -f "${dir}/${role}.exit-now"
    event "exit ${code}"
    exit "${code}"
  fi
  sleep 0.1 &
  wait $!
done
