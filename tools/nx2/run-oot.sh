#!/usr/bin/env bash
# Build and run a Switch 2 (.nup) title with the NX2 profile used for OoT bring-up.
#
# Usage: tools/nx2/run-oot.sh /path/to/game.nup [timeout-seconds]
#   NX2_ROM        alternative to the first argument
#   NX2_SKIP_BUILD=1  reuse the existing Debug build
#
# Logs, profile and shader cache go to .nx2-logs/run-<timestamp>/ (git-ignored).
set -euo pipefail

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
rom=${1:-${NX2_ROM:-}}
secs=${2:-${NX2_SECONDS:-240}}

if [ -z "$rom" ] || [ ! -f "$rom" ]; then
    echo "usage: $0 /path/to/game.nup [timeout-seconds]" >&2
    echo "       the ROM is not part of this repository" >&2
    exit 2
fi

run="$repo/.nx2-logs/run-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$run/data" "$run/config"
cd "$repo"

if [ "${NX2_SKIP_BUILD:-0}" != "1" ]; then
    dotnet build src/Ryujinx/Ryujinx.csproj -c Debug > "$run/build.log" 2>&1 || {
        echo "build failed, see $run/build.log" >&2
        tail -20 "$run/build.log" >&2
        exit 1
    }
fi

# NX2_PATCH skips three guest movie-module instructions that otherwise abort with
# 2243-0128 before the first decoder buffer is read. Keep it unless a run proves
# the patch is no longer needed.
env -u NX2_ARB_LOG -u NX2_THREAD_DUMP_AT_PRESENT \
    XDG_CONFIG_HOME="$run/config" \
    NX2_PATCH="${NX2_PATCH:-0xD3CF86C:0x2A1F03E0,0xD3CF8A4:0x2A1F03E0,0xD3CF8DC:0x2A1F03E0}" \
    timeout -s INT "$secs" dotnet src/Ryujinx/bin/Debug/net11.0/Ryujinx.dll \
        -r "$run/data" \
        "$rom" \
        --dram-size MemoryConfiguration12GiB \
        --memory-manager-mode SoftwarePageTable \
        --ignore-missing-services \
        > "$run/ocarina.log" 2>&1 || rc=$?

echo "rc=${rc:-0}" > "$run/invocation.txt"
echo "run: $run"
echo "log: $run/ocarina.log"
