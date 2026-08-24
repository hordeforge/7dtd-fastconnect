#!/usr/bin/env bash
# Monotonic seconds since boot (/proc/uptime, CLOCK_BOOTTIME), shared by every
# lifecycle script with a bounded wait. Bash's SECONDS is wall-clock derived:
# an NTP step or manual correction mid-wait would extend or truncate timeouts
# (killing a client that was about to spawn, or hanging past its budget).
# Source this file; do not execute it.

# Fallback keeps the old behaviour off-Linux.
mono_sec() {
  local up
  if read -r up _ < /proc/uptime 2>/dev/null && [[ "$up" =~ ^[0-9]+([.][0-9]+)?$ ]]; then
    printf '%s\n' "${up%%.*}"
  else
    printf '%s\n' "$SECONDS"
  fi
}
