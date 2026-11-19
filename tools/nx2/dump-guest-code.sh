#!/usr/bin/env bash
# Extract a 16-byte-aligned code slice at a guest VA from a module in a Switch 2 NUP.
# Usage: tools/nx2/dump-guest-code.sh <nup> <module> <guest-va> <length> <out.bin>
set -euo pipefail

if [ "$#" -ne 5 ]; then
    echo "usage: $0 <nup> <module> <guest-va> <length> <out.bin>" >&2
    exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
exec dotnet run --project "$repo_root/tools/nx2/Nx2DumpGuestCode/Nx2DumpGuestCode.csproj" -c Debug -- "$@"
