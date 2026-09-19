#!/bin/sh
set -eu

app_uid="${APP_UID:-1654}"
app_gid="$(id -g "$app_uid")"

# Docker creates missing bind-mount children as root. Repair only Framewright's
# application-owned boundary, then prove the real application identity can use
# it before the API advertises itself as healthy.
install -d -m 0770 -o "$app_uid" -g "$app_gid" \
    /data \
    /data/assets \
    /data/assets/.staging \
    /data/backups
chown "$app_uid:$app_gid" /data /data/assets /data/assets/.staging /data/backups
find /data -maxdepth 1 -type f -exec chown "$app_uid:$app_gid" {} +

# Host credentials and skills stay read-only. Codex receives private writable
# runtime copies so token refresh and system-skill installation cannot mutate the
# host bind mounts or fail halfway through an image job.
if [ -f /codex-seed/auth.json ]; then
    cp /codex-seed/auth.json /codex-home/auth.json
    chmod 600 /codex-home/auth.json
    chown "$app_uid:$app_gid" /codex-home/auth.json
fi

if [ -d /codex-seed/skills/imagegen ]; then
    mkdir -p /codex-home/skills/.system/imagegen
    cp -R /codex-seed/skills/imagegen/. /codex-home/skills/.system/imagegen/
    chown -R "$app_uid:$app_gid" /codex-home/skills
fi

for writable_path in /data /data/assets/.staging /data/backups /codex-home; do
    if ! gosu "$app_uid:$app_gid" test -w "$writable_path"; then
        echo "Framewright startup refused: app user cannot write $writable_path" >&2
        exit 70
    fi
done

exec gosu "$app_uid:$app_gid" dotnet Framewright.dll
