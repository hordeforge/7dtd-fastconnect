#!/usr/bin/env bash
# Monotonic seconds since boot (/proc/uptime, CLOCK_BOOTTIME), shared by every
# lifecycle script with a bounded wait. The bash fallback is $SECONDS, which
# counts from shell start and is immune to clock steps, so a wait budget is
# never shortened or extended by an NTP correction mid-wait.
#
# A reading is a delta within one shell only. $SECONDS starts at 0 in every
# shell and /proc/uptime counts from boot, so neither a reading nor a deadline
# built from one can be compared with a value from another process or another
# machine; a difference of two readings from the same shell is a duration, and
# nothing else here is.
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
