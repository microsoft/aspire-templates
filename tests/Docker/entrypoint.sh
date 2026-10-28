#!/usr/bin/env bash
set -euo pipefail

# Each privileged test container owns its daemon; never use the host socket.
mkdir -p /workspace/diagnostics
dockerd --host=unix:///var/run/docker.sock --storage-driver=vfs > /workspace/diagnostics/dockerd.log 2>&1 &
daemon_pid=$!
trap 'kill "$daemon_pid"; wait "$daemon_pid" || true' EXIT
for ((attempt=0; attempt<60; attempt++)); do
    if docker info > /dev/null 2>&1; then
        export PS1='TEMPLATE_SHELL_READY $ '
        "$@"
        exit $?
    fi
    if ! kill -0 "$daemon_pid" 2>/dev/null; then
        cat /workspace/diagnostics/dockerd.log >&2
        exit 1
    fi
    sleep 1
done
cat /workspace/diagnostics/dockerd.log >&2
echo "Docker daemon did not become ready within 60 seconds." >&2
exit 1
