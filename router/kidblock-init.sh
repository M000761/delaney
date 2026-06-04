#!/bin/sh
# Runs on every router boot via /config/scripts/post-config.d/
# DM9: `kidblock.sh init` calls cmd_reapply, which runs migrate_legacy_override
# (one-shot promote of any unexpired /var/run/kidblock.override into the new
# per-MAC kidblock-overrides.conf) then prune_expired_overrides + apply_time_chain
# + apply_domain_rules + apply_whitelist_rules. Net effect: unexpired per-MAC
# overrides survive reboots; expired ones are dropped at first boot tick.

/bin/bash /config/scripts/kidblock.sh init >/dev/null 2>&1 || true
exit 0
