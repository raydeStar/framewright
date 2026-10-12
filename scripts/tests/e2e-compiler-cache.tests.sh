#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
wrapper="${1:-"$script_dir/../e2e-compiler-stub.sh"}"
test_parent="$(cd -- "${TMPDIR:-/tmp}" && pwd -P)"
test_root="$(mktemp -d "$test_parent/framewright-compiler-cache-test.XXXXXXXX")"
pids=()
cleanup() {
    # Let only our controlled children finish before deleting their fixture.
    if [[ -d "$test_root/observations" ]]; then touch "$test_root/observations/release"; fi
    for pid in "${pids[@]}"; do wait "$pid" || true; done
    if [[ "$test_root" == "$test_parent"/framewright-compiler-cache-test.* ]]; then rm -rf -- "$test_root"; fi
}
trap cleanup EXIT

fail() { printf 'Compiler cache contract failed: %s\n' "$*" >&2; exit 1; }

mkdir -p "$test_root/bin" "$test_root/observations" "$test_root/temp with spaces" "$test_root/caller-cache/powershell"
printf 'caller cache must survive\n' > "$test_root/caller-cache/powershell/StartupProfileData-NonInteractive"

# Observe the real shell wrapper at its process boundary without requiring a
# particular installed PowerShell or deliberately corrupting a shared cache.
cat > "$test_root/bin/pwsh" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$XDG_CACHE_HOME" > "$FW_CACHE_OBSERVATIONS/$FW_CACHE_TEST_ID.cache"
printf '%s\0' "$@" > "$FW_CACHE_OBSERVATIONS/$FW_CACHE_TEST_ID.args"
[[ -d "$XDG_CACHE_HOME" ]] || exit 91
mkdir -p "$XDG_CACHE_HOME/powershell"
printf '%s\n' "$FW_CACHE_TEST_ID" > "$XDG_CACHE_HOME/powershell/StartupProfileData-NonInteractive"
touch "$FW_CACHE_OBSERVATIONS/$FW_CACHE_TEST_ID.ready"
if [[ "${FW_CACHE_WAIT:-0}" == 1 ]]; then
    for ((attempt = 0; attempt < 250; attempt++)); do
        [[ -f "$FW_CACHE_OBSERVATIONS/release" ]] && break
        sleep 0.02
    done
    [[ -f "$FW_CACHE_OBSERVATIONS/release" ]] || exit 92
fi
printf 'controlled stdout\n'
printf 'controlled stderr\n' >&2
exit "${FW_CACHE_EXIT:-0}"
STUB
chmod +x "$test_root/bin/pwsh"
export PATH="$test_root/bin:$PATH"
export TMPDIR="$test_root/temp with spaces"
export XDG_CACHE_HOME="$test_root/caller-cache"
export FW_CACHE_OBSERVATIONS="$test_root/observations"

arguments=(budget '--name=prop with spaces' --dims 2 1 0.75)
for id in first second third fourth; do
    FW_CACHE_TEST_ID="$id" FW_CACHE_WAIT=1 bash "$wrapper" "${arguments[@]}" > "$test_root/$id.stdout" 2> "$test_root/$id.stderr" &
    pids+=("$!")
done

for ((attempt = 0; attempt < 250; attempt++)); do
    ready=("$FW_CACHE_OBSERVATIONS/"*.ready)
    [[ ${#ready[@]} == 4 ]] && break
    sleep 0.02
done
[[ ${#ready[@]} == 4 ]] || fail 'concurrent child processes did not all become ready'

caches=()
for id in first second third fourth; do
    IFS= read -r cache < "$FW_CACHE_OBSERVATIONS/$id.cache"
    [[ "$cache" == "$TMPDIR"/framewright-e2e-pwsh-cache.* ]] || fail 'child inherited or escaped the caller cache'
    [[ -d "$cache" ]] || fail 'a live child lost its cache'
    [[ "$(cat "$cache/powershell/StartupProfileData-NonInteractive")" == "$id" ]] || fail 'concurrent children shared a startup profile'
    for previous in "${caches[@]}"; do
        [[ "$cache" != "$previous" ]] || fail 'concurrent children shared a cache directory'
    done
    caches+=("$cache")

    mapfile -d '' -t received < "$FW_CACHE_OBSERVATIONS/$id.args"
    [[ ${#received[@]} == 9 && "${received[0]}" == -NoProfile && "${received[1]}" == -File ]] || fail 'PowerShell invocation changed'
    [[ "${received[2]}" == */e2e-compiler-stub.ps1 ]] || fail 'controlled compiler script changed'
    for index in "${!arguments[@]}"; do
        [[ "${received[index + 3]}" == "${arguments[index]}" ]] || fail 'compiler arguments changed'
    done
done
touch "$FW_CACHE_OBSERVATIONS/release"
for pid in "${pids[@]}"; do wait "$pid" || fail 'a successful child exit was changed'; done
for cache in "${caches[@]}"; do [[ ! -e "$cache" ]] || fail 'success left its cache behind'; done

if FW_CACHE_TEST_ID=failure FW_CACHE_EXIT=37 bash "$wrapper" "${arguments[@]}" > "$test_root/failure.stdout" 2> "$test_root/failure.stderr"; then
    fail 'a failed child was reported as successful'
else
    [[ $? == 37 ]] || fail 'the child failure exit code changed'
fi
IFS= read -r failure_cache < "$FW_CACHE_OBSERVATIONS/failure.cache"
[[ ! -e "$failure_cache" ]] || fail 'failure left its cache behind'
for id in first second third fourth failure; do
    [[ "$(cat "$test_root/$id.stdout")" == 'controlled stdout' ]] || fail 'stdout changed'
    [[ "$(cat "$test_root/$id.stderr")" == 'controlled stderr' ]] || fail 'stderr changed'
done
[[ "$XDG_CACHE_HOME" == "$test_root/caller-cache" ]] || fail 'caller environment changed'
[[ "$(cat "$XDG_CACHE_HOME/powershell/StartupProfileData-NonInteractive")" == 'caller cache must survive' ]] || fail 'caller cache changed'
[[ -z "$(find "$TMPDIR" -mindepth 1 -print -quit)" ]] || fail 'an invocation left temporary files behind'
printf 'Compiler cache contracts passed: concurrent isolation, arguments, streams, exit codes, and owned cleanup.\n'
