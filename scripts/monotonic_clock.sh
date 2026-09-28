#!/usr/bin/env bash
# Monotonic seconds since boot (/proc/uptime, CLOCK_BOOTTIME), shared by every
# lifecycle script with a bounded wait. The bash fallback is $SECONDS, which
# counts from shell start and is immune to clock steps, so a wait budget on
# one machine is comparable with a budget on another and with a log line
# written by a different process.
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
