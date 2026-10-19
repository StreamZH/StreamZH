#!/bin/sh
# StreamZH - one app for your whole media pipeline.
# Copyright (c) 2026 The StreamZH Authors.
# SPDX-License-Identifier: GPL-3.0-only
#
# Container start: runs StreamZH as PUID:PGID (like the linuxserver.io images) with UMASK, data in /config.
# Started with --user instead, StreamZH runs as that user and these variables are ignored.

set -eu

# 027: new files are readable for the group (Plex or Jellyfin in the same group can read imported media), not for
# others. UMASK=002 makes them group-writable, UMASK=022 readable for everyone.
umask "${UMASK:-027}"

if [ "$(id -u)" -ne 0 ]; then
    exec dotnet /app/StreamZH.dll "$@"
fi

uid=${PUID:-$APP_UID}
gid=${PGID:-$APP_UID}
case "$uid:$gid" in
    *[!0-9:]*|:*|*:) echo "PUID and PGID must be numbers (got PUID=$uid PGID=$gid)" >&2; exit 1 ;;
esac
if [ "$uid" -eq 0 ]; then
    echo "Warning: StreamZH runs as root (PUID=0)." >&2
fi

# /config belongs to the user; the whole tree only when the owner changed (first start, new PUID), so big
# download folders inside /config are not walked on every start.
mkdir -p /config
if [ "$(stat -c %u:%g /config)" != "$uid:$gid" ]; then
    echo "Giving /config to $uid:$gid"
    chown -R "$uid:$gid" /config
fi
# /config holds the database (API key, password hashes, indexer keys), the sign-in keys and backups: nobody else may
# read it (the group may look, as with the Linux installer). StreamZH tightens the files inside at every start.
chmod u+rwx,g-w,o-rwx /config

export HOME=/config
exec setpriv --reuid="$uid" --regid="$gid" --clear-groups -- dotnet /app/StreamZH.dll "$@"
