#!/bin/sh
# StreamZH .deb/.rpm: before the files are unpacked (install and upgrade).
# Creates the system user "streamzh" (no shell, home = the data folder). Hands an installation made with
# tools/install-linux.sh over to the package: its unit in /etc would hide the package's unit.
set -e

user=streamzh
data=/var/lib/streamzh

if ! getent passwd "$user" >/dev/null 2>&1; then
    nologin=$(command -v nologin 2>/dev/null || echo /usr/sbin/nologin)
    if ! getent group "$user" >/dev/null 2>&1; then
        groupadd --system "$user"
    fi
    useradd --system --gid "$user" --home-dir "$data" --no-create-home --shell "$nologin" \
        --comment "StreamZH service" "$user"
fi

old_unit=/etc/systemd/system/streamzh.service
if [ -f "$old_unit" ] && grep -q 'written by install-linux.sh' "$old_unit"; then
    if [ -d /run/systemd/system ]; then
        systemctl stop streamzh.service >/dev/null 2>&1 || true
    fi
    mv -f "$old_unit" "$old_unit.install-linux.bak"
    echo "StreamZH: moved the unit of install-linux.sh to $old_unit.install-linux.bak; the package's unit is used now."
    echo "StreamZH: the package uses /var/lib/streamzh and the user streamzh. If install-linux.sh used other ones (--data,"
    echo "          --user), add them with: sudo systemctl edit streamzh"
fi

exit 0
