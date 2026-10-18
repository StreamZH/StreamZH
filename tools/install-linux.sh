#!/usr/bin/env bash
# StreamZH - one app for your whole media pipeline.
# Copyright (c) 2026 The StreamZH Authors.
# SPDX-License-Identifier: GPL-3.0-only
#
# Installs or updates StreamZH as a systemd service on Linux (x64 or arm64).
#
#   sudo bash install-linux.sh                          # from the unpacked package this script is in
#   sudo bash install-linux.sh --from StreamZH-linux-x64.zip
#   sudo bash install-linux.sh --from streamzh.tar.gz --port 9797
#   sudo bash install-linux.sh --uninstall              # keeps the data in /var/lib/streamzh
#
# Program files go to /opt/streamzh, data to /var/lib/streamzh, the service runs as the system user "streamzh".
# Running it again with a newer package updates StreamZH; the data stays.

set -euo pipefail

prefix=/opt/streamzh
data=/var/lib/streamzh
user=streamzh
unit_name=streamzh.service
unit_file=/etc/systemd/system/$unit_name
port=9797
source_path=
start=1
action=install
purge=0
arguments="$*"

usage() {
    cat <<'EOF'
Usage: sudo bash install-linux.sh [options]

  --from <path>      Unpacked package folder, .tar.gz or .zip (default: the folder of this script)
  --prefix <dir>     Program folder (default /opt/streamzh)
  --data <dir>       Data folder (default /var/lib/streamzh)
  --user <name>      Service user, created when missing (default streamzh)
  --port <port>      Port to check after the start (default 9797; the port itself is set in Settings -> General)
  --no-start         Install, but do not start or restart the service
  --uninstall        Stop and remove the service and the program folder; data and user stay
  --purge            With --uninstall: also delete the data folder and the user
  -h, --help         This help

Root folders and download folders must be readable and writable for the service user, for example:
  sudo setfacl -R -m u:streamzh:rwX -m d:u:streamzh:rwX /srv/media
or add the user to the group that owns them:  sudo usermod -aG media streamzh
EOF
}

say() { printf '\n==> %s\n' "$*"; }
fail() { printf 'install-linux.sh: %s\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        --from) source_path=${2:?--from needs a path}; shift 2 ;;
        --prefix) prefix=${2:?--prefix needs a folder}; shift 2 ;;
        --data) data=${2:?--data needs a folder}; shift 2 ;;
        --user) user=${2:?--user needs a name}; shift 2 ;;
        --port) port=${2:?--port needs a number}; shift 2 ;;
        --no-start) start=0; shift ;;
        --uninstall) action=uninstall; shift ;;
        --purge) purge=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) usage >&2; fail "unknown option: $1" ;;
    esac
done

# Never let a typo turn a folder option into "/" or a relative path before anything is replaced or deleted.
check_folder() {
    case "$2" in
        /*) ;;
        *) fail "$1 must be an absolute path: $2" ;;
    esac
    case "${2%/}" in
        ''|/|/bin|/boot|/dev|/etc|/home|/lib|/lib64|/opt|/proc|/root|/run|/sbin|/srv|/sys|/tmp|/usr|/var|/var/lib|/mnt|/media)
            fail "$1 must not be $2" ;;
    esac
}
check_folder --prefix "$prefix"
check_folder --data "$data"
prefix=${prefix%/}
data=${data%/}
case "$port" in
    ''|*[!0-9]*) fail "--port must be a number: $port" ;;
esac

[ "$(id -u)" -eq 0 ] || fail "needs root: sudo bash $0 $arguments"
if ! command -v systemctl >/dev/null 2>&1 || [ ! -d /run/systemd/system ]; then
    fail "systemd is not running on this system (in Docker use the image instead: docker/Dockerfile)"
fi

if [ "$action" = uninstall ]; then
    say "Remove the StreamZH service"
    if [ -f "$unit_file" ]; then
        systemctl disable --now "$unit_name" || true
        rm -f "$unit_file"
        systemctl daemon-reload
        echo "service removed"
    else
        echo "no service $unit_name"
    fi
    if [ -x "$prefix/StreamZH" ]; then
        rm -rf -- "$prefix"
        echo "removed $prefix"
    fi
    if [ "$purge" -eq 1 ]; then
        rm -rf -- "$data"
        echo "removed $data"
        if id "$user" >/dev/null 2>&1; then
            userdel "$user" || true
            echo "removed user $user"
        fi
    else
        echo "kept $data (your database, settings and logs); --purge deletes it"
    fi
    exit 0
fi

# ---- Find the package ----

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
[ -n "$source_path" ] || source_path=$script_dir
[ -e "$source_path" ] || fail "not found: $source_path"

work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT

if [ -d "$source_path" ]; then
    package=$(cd "$source_path" && pwd)
else
    say "Unpack $(basename "$source_path")"
    case "$source_path" in
        *.tar.gz|*.tgz) tar -xzf "$source_path" -C "$work" ;;
        *.zip)
            command -v unzip >/dev/null 2>&1 || fail "unzip is missing (sudo apt install unzip)"
            unzip -q "$source_path" -d "$work" ;;
        *) fail "--from must be a folder, a .tar.gz or a .zip: $source_path" ;;
    esac
    package=$(dirname "$(find "$work" -type f -name StreamZH -print -quit)")
    [ "$package" != . ] || package=
fi

if [ -z "$package" ] || [ ! -f "$package/StreamZH" ]; then
    fail "no StreamZH program in $source_path (expected the unpacked linux-x64 or linux-arm64 package)"
fi
[ -d "$package/wwwroot" ] || fail "the web UI (wwwroot) is missing next to $package/StreamZH"

case "$(uname -m)" in
    x86_64|amd64) arch=x64 ;;
    aarch64|arm64) arch=arm64 ;;
    *) arch=$(uname -m) ;;
esac
if command -v file >/dev/null 2>&1; then
    kind=$(file -b "$package/StreamZH")
    case "$arch:$kind" in
        x64:*x86-64*|arm64:*aarch64*) ;;
        *:*ELF*) fail "the package does not match this computer ($arch): $kind" ;;
    esac
fi

# .NET needs ICU unless globalization is invariant; say so before the service fails to start.
if ! ldconfig -p 2>/dev/null | grep 'libicuuc\.so' >/dev/null; then
    echo "Warning: ICU (libicu) not found. StreamZH needs it: sudo apt install libicu-dev   (Fedora: sudo dnf install libicu)" >&2
fi

# ---- User, folders, program files ----

say "User and folders"
if ! id "$user" >/dev/null 2>&1; then
    nologin=$(command -v nologin || echo /usr/sbin/nologin)
    useradd --system --user-group --home-dir "$data" --no-create-home --shell "$nologin" "$user"
    echo "created system user $user"
fi
group=$(id -gn "$user")
install -d -m 0750 -o "$user" -g "$group" "$data"
echo "data:    $data (owner $user)"

was_active=0
if systemctl is-active --quiet "$unit_name"; then
    was_active=1
fi

say "Install program files to $prefix"
staging=$prefix.new
rm -rf -- "$staging"
mkdir -p "$staging"
cp -R "$package/." "$staging/"
# Owned by root, readable for everybody, writable for nobody else (zip packages lose the modes).
chown -R root:root "$staging"
chmod -R u=rwX,go=rX "$staging"
chmod 0755 "$staging/StreamZH"
[ ! -f "$staging/install-linux.sh" ] || chmod 0755 "$staging/install-linux.sh"

if [ "$was_active" -eq 1 ]; then
    say "Stop StreamZH"
    systemctl stop "$unit_name"
fi
if [ -d "$prefix" ]; then
    rm -rf -- "$prefix.old"
    mv -- "$prefix" "$prefix.old"
fi
mv -- "$staging" "$prefix"
rm -rf -- "$prefix.old"
echo "program: $prefix"

# ---- systemd unit (the same as docs/streamzh.service, with the folders and user from the options) ----

say "Service $unit_name"
cat > "$unit_file" <<EOF
# StreamZH systemd service, written by install-linux.sh. Changes: sudo systemctl edit streamzh
[Unit]
Description=StreamZH - one app for your whole media pipeline
After=network-online.target
Wants=network-online.target

[Service]
Type=exec
User=$user
Group=$group
Environment=STREAMZH_DATA=$data
WorkingDirectory=$prefix
ExecStart=$prefix/StreamZH
# Restart after a crash; "Shutdown" in the web UI stops it for good.
Restart=on-failure
RestartSec=5
TimeoutStopSec=60
# New files: group may read and write (media folders shared with Plex/Jellyfin).
UMask=0002
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable "$unit_name" >/dev/null 2>&1
echo "enabled (starts with the system)"

if [ "$start" -eq 0 ]; then
    echo
    echo "Installed, not started. Start with: sudo systemctl start streamzh"
    exit 0
fi

say "Start StreamZH"
# A failed start is reported below with the journal.
systemctl restart "$unit_name" || true

ready=0
for _ in $(seq 1 60); do
    if ! systemctl is-active --quiet "$unit_name"; then
        sleep 1
        if ! systemctl is-active --quiet "$unit_name"; then
            break
        fi
    fi
    if command -v curl >/dev/null 2>&1; then
        if curl -fs -o /dev/null --max-time 2 "http://127.0.0.1:$port/health"; then
            ready=1
            break
        fi
    elif command -v wget >/dev/null 2>&1; then
        if wget -q -O /dev/null --timeout=2 "http://127.0.0.1:$port/health"; then
            ready=1
            break
        fi
    fi
    sleep 1
done

if [ "$ready" -eq 1 ]; then
    host=$(hostname -f 2>/dev/null || hostname)
    echo "running: http://$host:$port"
elif systemctl is-active --quiet "$unit_name"; then
    echo "running (no answer on port $port yet; another port or HTTPS set in Settings -> General?)"
else
    echo "StreamZH did not start. Last log lines:" >&2
    journalctl -u "$unit_name" -n 30 --no-pager >&2 || true
    exit 1
fi

cat <<EOF

Done. StreamZH runs as the systemd service streamzh (user $user) and starts with the system.
  Status:  systemctl status streamzh
  Logs:    journalctl -u streamzh -f      (log files: $data/logs)
  Update:  sudo bash install-linux.sh --from <new package>
Root folders and download folders must be writable for the user $user.
EOF
