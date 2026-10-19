#!/usr/bin/env bash
# StreamZH - one app for your whole media pipeline.
# Copyright (c) 2026 The StreamZH Authors.
# SPDX-License-Identifier: GPL-3.0-only
#
# Checks that every place naming StreamZH's GitHub repository agrees with AppInfo.Repository
# (src/StreamZH.Core/AppInfo.cs), the repository the update check trusts (security review SV4 X-01):
#   - the Docker Compose example runs ghcr.io/<AppInfo.Repository>;
#   - every github.com/<owner>/streamzh, raw.githubusercontent.com/<owner>/streamzh and ghcr.io/<owner>/streamzh in the
#     README, the docs, docker/ and packaging/ names the same owner;
#   - with --repository <owner/name> (the release workflow passes $GITHUB_REPOSITORY): the repository that builds the
#     release is that one too, so its image ghcr.io/<owner/name> is the image the app and the docs point to.
#
#   packaging/check-repository.sh [--repository owner/name]
#
# Prints the image name (ghcr.io/<owner/name>, lower case) on success; exits 1 with an error per mismatch.
set -euo pipefail

cd "$(dirname "$0")/.."

actual=""
while [ $# -gt 0 ]; do
    case "$1" in
        --repository) actual="${2:-}"; shift 2 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

expected=$(sed -n 's/.*const string Repository = "\([^"]*\)";.*/\1/p' src/StreamZH.Core/AppInfo.cs | head -n 1)
if [ -z "$expected" ]; then
    echo "::error file=src/StreamZH.Core/AppInfo.cs::No 'const string Repository = \"owner/name\";' found"
    exit 1
fi
expected="${expected,,}"
image="ghcr.io/$expected"
failed=0

if [ -n "$actual" ] && [ "${actual,,}" != "$expected" ]; then
    echo "::error file=src/StreamZH.Core/AppInfo.cs::This workflow runs in $actual, but AppInfo.Repository is $expected: the update check, the docs and the image name would point to another repository. Release from $expected, or change AppInfo.Repository and the docs."
    failed=1
fi

compose=docker/docker-compose.example.yml
if ! grep -Eq "^[[:space:]]*image:[[:space:]]*${image//./\\.}:" "$compose"; then
    echo "::error file=$compose::The example must run image: $image:<tag>"
    failed=1
fi

# owner/streamzh links in the files a user reads or runs.
files=(README.md SECURITY.md CONTRIBUTING.md CHANGELOG.md docs docker packaging .github/ISSUE_TEMPLATE)
while IFS= read -r hit; do
    file=${hit%%:*}
    rest=${hit#*:}
    line=${rest%%:*}
    reference=${rest#*:}
    # Host, owner and name -> owner/name in lower case.
    named=$(printf '%s' "$reference" | sed -E 's#^[^/]+/##' | tr '[:upper:]' '[:lower:]')
    if [ "$named" != "$expected" ]; then
        echo "::error file=$file,line=$line::$reference does not name $expected (AppInfo.Repository)"
        failed=1
    fi
done < <(grep -rnoiE '(github\.com|raw\.githubusercontent\.com|ghcr\.io)/[A-Za-z0-9._-]+/streamzh([^A-Za-z0-9._-]|$)' "${files[@]}" 2>/dev/null \
    | sed -E 's/[^A-Za-z0-9._-]$//' || true)

if [ "$failed" -ne 0 ]; then
    exit 1
fi
echo "$image"
