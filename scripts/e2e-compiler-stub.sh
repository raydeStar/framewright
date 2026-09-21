#!/usr/bin/env bash
set -euo pipefail

# Keep one controlled compiler implementation for both CI and Windows. The
# shell wrapper exists because ProcessStartInfo launches an executable directly
# and cannot ask Linux to interpret the Windows .cmd companion. A modest bit of
# diplomacy between operating systems; the secret order approves.
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
exec pwsh -NoProfile -File "$script_dir/e2e-compiler-stub.ps1" "$@"
