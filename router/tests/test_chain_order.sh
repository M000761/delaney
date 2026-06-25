#!/bin/bash
# test_chain_order.sh (DM21) -- regression guard for the kidblock FORWARD jump order.
#
# WHY: ensure_chains() inserts each kidblock chain with `iptables -I FORWARD 1`, which
# STACKS insertions -- the chain iterated LAST lands FIRST in FORWARD. The per-MAC
# override row lives at the top of KIDBLOCK_TIME, so KIDBLOCK_TIME MUST be visited
# first or the override's ACCEPT/DROP can't preempt the KIDBLOCK_DOMAINS /
# KIDBLOCK_WHITELIST filtering downstream (the 2026-06-22 NUC-M google.com bug).
# This test asserts a clean `kidblock.sh init` yields FORWARD order TIME -> DOMAINS
# -> WHITELIST for both iptables (v4) and ip6tables (v6).
#
# Router-side test: needs root (iptables CAP_NET_ADMIN). Re-inits the kidblock chains,
# so the live override state in kidblock-overrides.conf is re-applied by init (NUC-M's
# Allow override survives). Run before deploying a kidblock.sh change:
#   sudo router/tests/test_chain_order.sh
#
# Override the script under test with KIDBLOCK_SH=/path/to/kidblock.sh if needed.

set -u

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
KIDBLOCK="${KIDBLOCK_SH:-$SCRIPT_DIR/../kidblock.sh}"
EXPECTED="KIDBLOCK_TIME KIDBLOCK_DOMAINS KIDBLOCK_WHITELIST"

if [ ! -f "$KIDBLOCK" ]; then
  echo "FAIL: kidblock.sh not found at $KIDBLOCK (set KIDBLOCK_SH to override)"; exit 2
fi
if [ "$(id -u)" -ne 0 ]; then
  echo "FAIL: must run as root -- iptables needs CAP_NET_ADMIN. Try: sudo $0"; exit 2
fi

# First 3 KIDBLOCK_* jump targets in FORWARD, in visit order, space-joined.
forward_kidblock_order() {
  local ipt="$1"
  "$ipt" -S FORWARD 2>/dev/null \
    | grep -E '^-A FORWARD -j KIDBLOCK_' \
    | head -3 \
    | awk '{print $NF}' \
    | tr '\n' ' ' \
    | sed 's/ *$//'
}

# Delete every existing kidblock jump from FORWARD (v4 + v6) so init starts clean.
# A chain may carry more than one stale jump -- loop until -C reports it gone.
reset_jumps() {
  local ipt="$1" ch
  for ch in KIDBLOCK_TIME KIDBLOCK_DOMAINS KIDBLOCK_WHITELIST; do
    while "$ipt" -C FORWARD -j "$ch" 2>/dev/null; do
      "$ipt" -D FORWARD -j "$ch" 2>/dev/null || break
    done
  done
}

reset_jumps iptables
reset_jumps ip6tables

"$KIDBLOCK" init >/dev/null 2>&1 || { echo "FAIL: '$KIDBLOCK init' exited non-zero"; exit 1; }

got4="$(forward_kidblock_order iptables)"
[ "$got4" = "$EXPECTED" ] || { echo "FAIL (v4): expected [$EXPECTED] got [$got4]"; exit 1; }

got6="$(forward_kidblock_order ip6tables)"
[ "$got6" = "$EXPECTED" ] || { echo "FAIL (v6): expected [$EXPECTED] got [$got6]"; exit 1; }

echo "PASS: FORWARD visits KIDBLOCK_TIME -> KIDBLOCK_DOMAINS -> KIDBLOCK_WHITELIST (v4 + v6)"
echo "      -> per-MAC override row at top of KIDBLOCK_TIME preempts DOMAINS / WHITELIST."
exit 0
