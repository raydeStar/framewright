#!/usr/bin/env bash
set -euo pipefail

# Keep one controlled compiler implementation for both CI and Windows. The
# shell wrapper exists because ProcessStartInfo launches an executable directly
# and cannot ask Linux to interpret the Windows .cmd companion. A modest bit of
# diplomacy between operating systems; the secret order approves.
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

# Parallel readiness probes must not share PowerShell's startup JIT cache.
# A corrupt shared profile can crash pwsh before the controlled compiler runs:
# https://github.com/PowerShell/PowerShell/issues/26528
compiler_cache_parent="$(cd -- "${TMPDIR:-/tmp}" && pwd -P)"
compiler_cache_dir="$(mktemp -d "$compiler_cache_parent/framewright-e2e-pwsh-cache.XXXXXXXX")"
cleanup() {
    if [[ "$compiler_cache_dir" == "$compiler_cache_parent"/framewright-e2e-pwsh-cache.* ]]; then
        rm -rf -- "$compiler_cache_dir"
    fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# Keep the shell alive for its EXIT cleanup, preserving the child's streams
# and exit status. Only this invocation sees the temporary cache location.
XDG_CACHE_HOME="$compiler_cache_dir" pwsh -NoProfile -File "$script_dir/e2e-compiler-stub.ps1" "$@"
