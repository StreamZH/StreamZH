#!/bin/sh
# StreamZH .deb/.rpm: before the files are removed.
#   deb: prerm remove | upgrade <new version> | deconfigure ...
#   rpm: %preun <count>   (0 = removal, 1 or more = upgrade)
# Removal: stop and disable the service. Upgrade: nothing (the new package restarts it).
set -e

case "$1" in
    remove|0) ;;
    *) exit 0 ;;
esac

if [ -d /run/systemd/system ]; then
    systemctl disable --now streamzh.service >/dev/null 2>&1 || true
fi

exit 0
