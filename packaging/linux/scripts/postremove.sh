#!/bin/sh
# StreamZH .deb/.rpm: after the files are removed.
#   deb: postrm remove | purge | upgrade ...
#   rpm: %postun <count>   (0 = removal, 1 or more = upgrade)
# Removal keeps the data folder /var/lib/streamzh (database, settings, logs, backups) and the user streamzh.
# "apt purge streamzh" deletes both. rpm has no purge: delete them by hand (printed below).
set -e

user=streamzh
data=/var/lib/streamzh

if [ -d /run/systemd/system ]; then
    systemctl daemon-reload >/dev/null 2>&1 || true
fi

case "$1" in
    purge)
        rm -rf -- /var/lib/streamzh
        if getent passwd "$user" >/dev/null 2>&1; then
            userdel "$user" >/dev/null 2>&1 || true
        fi
        if getent group "$user" >/dev/null 2>&1; then
            groupdel "$user" >/dev/null 2>&1 || true
        fi
        ;;
    remove)
        echo "StreamZH: kept $data (your database, settings and logs); 'sudo apt purge streamzh' deletes it."
        ;;
    0)
        echo "StreamZH: kept $data (your database, settings and logs). To delete it: sudo rm -rf $data && sudo userdel $user"
        ;;
esac

exit 0
