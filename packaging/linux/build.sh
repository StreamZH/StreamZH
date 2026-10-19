#!/usr/bin/env bash
# StreamZH - one app for your whole media pipeline.
# Copyright (c) 2026 The StreamZH Authors.
# SPDX-License-Identifier: GPL-3.0-only
#
# Packs a published linux-x64 or linux-arm64 folder into the release files:
#   StreamZH-<version>-linux-<arch>.tar.gz   (with install-linux.sh)
#   streamzh_<version>_<amd64|arm64>.deb
#   streamzh-<version>.<x86_64|aarch64>.rpm
#
#   packaging/linux/build.sh --rid linux-x64 --input out/linux-x64 --output dist [--version 1.0.0] [--nfpm path]
#
# The version defaults to <Version> in Directory.Build.props. nfpm (https://nfpm.goreleaser.com) must be on PATH or
# given with --nfpm; without it only the .tar.gz is built (--no-packages skips the .deb and .rpm on purpose).
# See docs/PACKAGING.md.

set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$here/../.." && pwd)
rid=
input=
output=
version=
nfpm=${NFPM:-nfpm}
packages=1

fail() { printf 'build.sh: %s\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        --rid) rid=${2:?}; shift 2 ;;
        --input) input=${2:?}; shift 2 ;;
        --output) output=${2:?}; shift 2 ;;
        --version) version=${2:?}; shift 2 ;;
        --nfpm) nfpm=${2:?}; shift 2 ;;
        --no-packages) packages=0; shift ;;
        -h|--help) sed -n '6,17p' "$0"; exit 0 ;;
        *) fail "unknown option: $1" ;;
    esac
done

case "$rid" in
    linux-x64) deb_arch=amd64; rpm_arch=x86_64 ;;
    linux-arm64) deb_arch=arm64; rpm_arch=aarch64 ;;
    *) fail "--rid must be linux-x64 or linux-arm64" ;;
esac
if [ -z "$input" ] || [ ! -f "$input/StreamZH" ]; then
    fail "--input must be the published folder with StreamZH: $input"
fi
[ -d "$input/wwwroot" ] || fail "the web UI (wwwroot) is missing in $input"
[ -n "$output" ] || fail "--output is missing"
if [ -z "$version" ]; then
    version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$repo/Directory.Build.props" | head -n 1)
fi
case "$version" in
    [0-9]*.[0-9]*.[0-9]*) ;;
    *) fail "version must look like 1.2.3 or 1.2.3-rc.1: $version" ;;
esac

input=$(cd "$input" && pwd)
mkdir -p "$output"
output=$(cd "$output" && pwd)
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT

# Program files: root-owned in the packages, readable for everyone, the program executable.
chmod -R u=rwX,go=rX "$input"
chmod 0755 "$input/StreamZH"

# ---- .tar.gz: the published folder plus the install script, in a folder named like the file ----
name="StreamZH-$version-$rid"
mkdir -p "$work/$name"
cp -R "$input/." "$work/$name/"
cp "$repo/tools/install-linux.sh" "$work/$name/install-linux.sh"
chmod 0755 "$work/$name/install-linux.sh"
cp "$repo/LICENSE" "$repo/NOTICE.md" "$work/$name/"
tar -C "$work" --owner=0 --group=0 --numeric-owner -czf "$output/$name.tar.gz" "$name"
echo "$output/$name.tar.gz"

if [ "$packages" -eq 0 ]; then
    exit 0
fi
command -v "$nfpm" >/dev/null 2>&1 || fail "nfpm not found (install it or pass --nfpm, or use --no-packages)"

# ---- .deb and .rpm ----
# nfpm expands variables only in some fields, so the config is filled in here (paths must not contain "|").
sed -e "s|\${STREAMZH_VERSION}|$version|g" \
    -e "s|\${STREAMZH_ARCH}|$deb_arch|g" \
    -e "s|\${STREAMZH_INPUT}|$input|g" \
    -e "s|\${STREAMZH_PACKAGING}|$here|g" \
    -e "s|\${STREAMZH_REPO}|$repo|g" \
    "$here/nfpm.yaml" > "$work/nfpm.yaml"
"$nfpm" package --config "$work/nfpm.yaml" --packager deb --target "$output/streamzh_${version}_${deb_arch}.deb"
"$nfpm" package --config "$work/nfpm.yaml" --packager rpm --target "$output/streamzh-${version}.${rpm_arch}.rpm"
