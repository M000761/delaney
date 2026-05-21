#!/bin/sh
# Runs on every router boot via /config/scripts/post-config.d/
# Re-applies the iptables chain so blocking survives reboots.

/bin/bash /config/scripts/kidblock.sh init >/dev/null 2>&1 || true
exit 0
