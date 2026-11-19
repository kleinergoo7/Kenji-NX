#!/usr/bin/env bash
# A/B oracle harness: run Lichii (reference) and Kenji-NX on the same NUP and
# compare captured-frame mean brightness plus the oracle's shader/pipeline totals.
#
# Usage: tools/nx2/oracle-ab.sh [seconds]          (default 60)
#   NUP / NX2_ROM  title to run (default: Fast Fusion NUP)
#   EDEN_CLI       oracle binary
# DISPLAY=:0.0 is required: Xvfb has no present queue.
set -uo pipefail

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
secs=${1:-60}
nup=${NX2_ROM:-${NUP:-/home/reng/Downloads/Fast_Fusion_Update_1.4.nup}}
oracle=${EDEN_CLI:-/tmp/opencode/lichii/build/bin/eden-cli}

if [ ! -f "$nup" ]; then
    echo "NUP not found: $nup (override with NUP=...)" >&2
    exit 2
fi

stamp=$(date +%Y%m%d-%H%M%S)
tmp=/tmp/opencode/oracle-ab-$stamp-$$
mkdir -p "$tmp"/{cfg,data,cache,oracle-frames}
oracle_log=$tmp/oracle.log
ours_log=$tmp/ours.log
ours_start=$tmp/ours-start   # mtime marker: only count frames dumped by this run

printf 'A/B oracle harness: %s s\nNUP: %s\n' "$secs" "$nup"

# Oracle: fresh XDG dirs keep the shader cache cold so "lowered" is counted here.
oracle_rc=0
DISPLAY=:0.0 \
XDG_CONFIG_HOME="$tmp/cfg" XDG_DATA_HOME="$tmp/data" XDG_CACHE_HOME="$tmp/cache" \
EDEN_NX2_AUTO_STUB=1 EDEN_NX2_FRAME_CAPTURE="$tmp/oracle-frames" \
    timeout -s INT "$secs" "$oracle" -g "$nup" >"$oracle_log" 2>&1 || oracle_rc=$?

# Ours: run-oot.sh owns the build and the NX2 flags. Frame dumps always go to
# .nx2-logs/ root, so the marker separates this run's frames from older ones.
ours_rc=0
touch "$ours_start"
NX2_PATCH="" NX2_FRAME_DUMP=1 \
    timeout -s INT "$((secs + 180))" \
    bash "$repo/tools/nx2/run-oot.sh" "$nup" "$secs" >"$ours_log" 2>&1 || ours_rc=$?

# Last oracle frame (first ones are captured before the title screen renders),
# newest our frame. Missing frames become N/A instead of failing the run.
oracle_frame=$(find "$tmp/oracle-frames" -maxdepth 1 -name 'nx2-frame-*.tga' -print 2>/dev/null | sort -V | tail -1)
ours_frame=$(find "$repo/.nx2-logs" -maxdepth 1 -name 'frame-*.rgba' -newer "$ours_start" -printf '%T@ %p\n' 2>/dev/null | sort -nr | head -1 | cut -d' ' -f2-)

oracle_mean=N/A
if [ -n "$oracle_frame" ] && convert -size 1280x720 "$oracle_frame" "$tmp/oracle.png" >/dev/null 2>&1; then
    oracle_mean=$(identify -format '%[fx:mean]' "$tmp/oracle.png" 2>/dev/null || echo N/A)
fi

ours_mean=N/A
if [ -n "$ours_frame" ] && convert -size 3840x2160 -depth 8 "rgba:$ours_frame" "$tmp/ours.png" >/dev/null 2>&1; then
    ours_mean=$(identify -format '%[fx:mean]' "$tmp/ours.png" 2>/dev/null || echo N/A)
fi

# Oracle totals are printed on shutdown; our fork has no equivalent logging (0).
# -a on every grep: the log contains NUL bytes and would otherwise be "binary".
oracle_shaders=$(grep -a 'Shader cache v2 programs:' "$oracle_log" 2>/dev/null | tail -1 | grep -aoE '[0-9]+ lowered' | grep -aoE '[0-9]+' || true)
oracle_pipelines=$(grep -a 'Runtime pipelines this session:' "$oracle_log" 2>/dev/null | tail -1 | grep -aoE 'Runtime pipelines this session: [0-9]+' | grep -aoE '[0-9]+$' || true)
ours_shaders=0
ours_pipelines=0

# run-oot.sh returns 0 even when its inner timeout fires; report the emulator rc it saved.
ours_run_dir=$(sed -n 's/^run: //p' "$ours_log" | tail -1)
if [ -n "$ours_run_dir" ] && [ -f "$ours_run_dir/invocation.txt" ]; then
    ours_rc=$(sed -n 's/^rc=//p' "$ours_run_dir/invocation.txt" | tail -1)
fi

printf '\n| Side   | Mean brightness | Shaders lowered | Pipelines built | rc  |\n'
printf '|--------|-----------------|-----------------|-----------------|-----|\n'
printf '| Oracle | %-15s | %-15s | %-15s | %-3s |\n' "${oracle_mean:-N/A}" "${oracle_shaders:-N/A}" "${oracle_pipelines:-N/A}" "$oracle_rc"
printf '| Ours   | %-15s | %-15s | %-15s | %-3s |\n' "${ours_mean:-N/A}" "${ours_shaders:-0}" "${ours_pipelines:-0}" "$ours_rc"

printf '\noracle log: %s\nours log:   %s\nscratch:    %s\nour frames: %s (newer than %s)\n' \
    "$oracle_log" "$ours_log" "$tmp" "$repo/.nx2-logs/frame-*.rgba" "$ours_start"
