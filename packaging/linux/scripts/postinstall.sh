#!/bin/sh
# StreamZH .deb/.rpm: after the files are unpacked.
#   deb: postinst configure [<old version>]   (old version empty on a new installation)
#   rpm: %post <count>                          (1 = new installation, 2 or more = upgrade)
# New installation: create the data folder, enable and start the service. Upgrade: restart it if it was running.
set -e

user=streamzh
data=/var/lib/streamzh
unit=streamzh.service

case "$1" in
    configure)
        if [ -n "$2" ]; then action=upgrade; else action=install; fi ;;
    1)
        action=install ;;
    [0-9]*)
        action=upgrade ;;
    *)
        # abort-upgrade, abort-remove, ...: nothing to do.
        exit 0 ;;
esac

if [ ! -d "$data" ]; then
    install -d -m 0750 -o "$user" -g "$user" "$data"
fi
chmod 0755 /opt/streamzh/StreamZH

if [ ! -d /run/systemd/system ]; then
    echo "StreamZH: systemd is not running here; start it with: sudo -H -u $user env STREAMZH_DATA=$data /opt/streamzh/StreamZH"
    exit 0
fi

systemctl daemon-reload >/dev/null 2>&1 || true
if [ "$action" = install ]; then
    systemctl enable "$unit" >/dev/null 2>&1 || true
    if systemctl start "$unit"; then
        echo "StreamZH runs: open http://$(hostname 2>/dev/null || echo localhost):9797 and create the first user."
        echo "From another device the setup page asks for a one-time setup code: sudo journalctl -u streamzh | grep -i 'setup code'"
        echo "  (also in $data/setup-code)."
    else
        echo "StreamZH did not start: sudo journalctl -u streamzh -n 50"
    fi
    echo "Root folders and download folders must be writable for the user $user, e.g.:"
    echo "  sudo setfacl -R -m u:$user:rwX -m d:u:$user:rwX /data/media"
else
    systemctl try-restart "$unit" >/dev/null 2>&1 || true
fi

exit 0
