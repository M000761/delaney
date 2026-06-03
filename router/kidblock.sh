#!/bin/bash
# kidblock.sh — schedule + per-device domain blocking for an EdgeRouter (EdgeOS).
#
# Schedule file format (kidblock-schedule.conf):
#   [days] HH:MM-HH:MM    one rule per line. # comments OK.
#   days:  *   (every day)
#          mon, tue, wed, thu, fri, sat, sun
#          range:  mon-thu, fri-sun, sat-mon (wraps)
#          list:   sat,sun  or  mon,wed,fri
#          omitted = applies to all days (legacy format)
#
# MAC file format (kidblock-macs.conf):
#   aa:bb:cc:dd:ee:ff   Label or hostname              # default mode = blocklist
#   aa:bb:cc:dd:ee:ff   Label or hostname  mode:whitelist
#
# Per-device mode (DM6):
#   blocklist (default): device reaches everything EXCEPT IPs resolved from
#                        domains in kidblock-domains.conf.
#   whitelist:           device reaches NOTHING except IPs resolved from
#                        domains in kidblock-allowlist.conf (homework-hour use case).
#   Both modes still honour the schedule's full-block windows (KIDBLOCK_TIME).

set -u

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
MAC_CONF="${SCRIPT_DIR}/kidblock-macs.conf"
DOMAIN_CONF="${SCRIPT_DIR}/kidblock-domains.conf"
ALLOW_CONF="${SCRIPT_DIR}/kidblock-allowlist.conf"
SCHED_CONF="${SCRIPT_DIR}/kidblock-schedule.conf"
STATE_FILE="/var/run/kidblock.state"
OVERRIDE_FILE="/var/run/kidblock.override"
LOG_FILE="/var/log/kidblock.log"
CHAIN_TIME="KIDBLOCK_TIME"
CHAIN_DOMAINS="KIDBLOCK_DOMAINS"
CHAIN_WHITELIST="KIDBLOCK_WHITELIST"
IPSET_V4="kidblock_domains_v4"
IPSET_V6="kidblock_domains_v6"
IPSET_ALLOW_V4="kidblock_allow_v4"
IPSET_ALLOW_V6="kidblock_allow_v6"
DNSMASQ_CONF="/etc/dnsmasq.d/kidblock-domains.conf"
DNSMASQ_ALLOW_CONF="/etc/dnsmasq.d/kidblock-allowlist.conf"

log() { echo "$(date '+%Y-%m-%d %H:%M:%S') $*" >> "$LOG_FILE" 2>/dev/null || true; }

# All controlled MACs regardless of mode -- KIDBLOCK_TIME blocks everyone during
# schedule windows. Mode-specific helpers below filter for the domain / whitelist chains.
get_macs() {
  [ -f "$MAC_CONF" ] || return 0
  awk 'NF && $1 !~ /^#/ { print tolower($1) }' "$MAC_CONF"
}

# Returns MACs whose row has NO mode:xxx token, OR mode:blocklist.
# Trailing-token-tolerant: scans from $NF backwards for the first mode:xxx field.
get_macs_blocklist() {
  [ -f "$MAC_CONF" ] || return 0
  awk 'NF && $1 !~ /^#/ {
    mode = "blocklist"
    for (i = NF; i > 1; i--) {
      if ($i ~ /^mode:/) { mode = substr($i, 6); break }
    }
    if (mode == "blocklist") print tolower($1)
  }' "$MAC_CONF"
}

get_macs_whitelist() {
  [ -f "$MAC_CONF" ] || return 0
  awk 'NF && $1 !~ /^#/ {
    mode = "blocklist"
    for (i = NF; i > 1; i--) {
      if ($i ~ /^mode:/) { mode = substr($i, 6); break }
    }
    if (mode == "whitelist") print tolower($1)
  }' "$MAC_CONF"
}

ensure_chains() {
  for c in "$CHAIN_TIME" "$CHAIN_DOMAINS" "$CHAIN_WHITELIST"; do
    iptables  -nL "$c" >/dev/null 2>&1 || iptables  -N "$c"
    ip6tables -nL "$c" >/dev/null 2>&1 || ip6tables -N "$c"
    iptables  -C FORWARD -j "$c" 2>/dev/null || iptables  -I FORWARD 1 -j "$c"
    ip6tables -C FORWARD -j "$c" 2>/dev/null || ip6tables -I FORWARD 1 -j "$c"
  done
}

ensure_ipsets() {
  ipset list "$IPSET_V4" >/dev/null 2>&1 || \
    ipset create "$IPSET_V4" hash:ip family inet  timeout 0 maxelem 65536 2>/dev/null || true
  ipset list "$IPSET_V6" >/dev/null 2>&1 || \
    ipset create "$IPSET_V6" hash:ip family inet6 timeout 0 maxelem 65536 2>/dev/null || true
  ipset list "$IPSET_ALLOW_V4" >/dev/null 2>&1 || \
    ipset create "$IPSET_ALLOW_V4" hash:ip family inet  timeout 0 maxelem 65536 2>/dev/null || true
  ipset list "$IPSET_ALLOW_V6" >/dev/null 2>&1 || \
    ipset create "$IPSET_ALLOW_V6" hash:ip family inet6 timeout 0 maxelem 65536 2>/dev/null || true
}

flush_time_chain() {
  iptables  -F "$CHAIN_TIME" 2>/dev/null || true
  ip6tables -F "$CHAIN_TIME" 2>/dev/null || true
}

flush_domain_chain() {
  iptables  -F "$CHAIN_DOMAINS" 2>/dev/null || true
  ip6tables -F "$CHAIN_DOMAINS" 2>/dev/null || true
}

flush_whitelist_chain() {
  iptables  -F "$CHAIN_WHITELIST" 2>/dev/null || true
  ip6tables -F "$CHAIN_WHITELIST" 2>/dev/null || true
}

apply_block() {
  ensure_chains
  flush_time_chain
  iptables  -A "$CHAIN_TIME" -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN
  ip6tables -A "$CHAIN_TIME" -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN
  local count=0
  while IFS= read -r mac; do
    [ -z "$mac" ] && continue
    iptables  -A "$CHAIN_TIME" -m mac --mac-source "$mac" -j DROP
    ip6tables -A "$CHAIN_TIME" -m mac --mac-source "$mac" -j DROP
    count=$((count+1))
  done < <(get_macs)
  echo block > "$STATE_FILE"
  log "applied block ($count MACs, with ESTABLISHED bypass)"
}

apply_allow() {
  ensure_chains
  flush_time_chain
  echo allow > "$STATE_FILE"
  log "applied allow"
}

apply_domain_rules() {
  if [ ! -f "$DNSMASQ_CONF" ]; then
    flush_domain_chain
    return
  fi
  ensure_chains
  ensure_ipsets
  flush_domain_chain
  iptables  -A "$CHAIN_DOMAINS" -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN
  ip6tables -A "$CHAIN_DOMAINS" -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN
  # Blocklist MACs only -- whitelist MACs are filtered via KIDBLOCK_WHITELIST instead.
  while IFS= read -r mac; do
    [ -z "$mac" ] && continue
    iptables  -A "$CHAIN_DOMAINS" -m mac --mac-source "$mac" -m set --match-set "$IPSET_V4" dst -j DROP
    ip6tables -A "$CHAIN_DOMAINS" -m mac --mac-source "$mac" -m set --match-set "$IPSET_V6" dst -j DROP
  done < <(get_macs_blocklist)
}

# Default-DROP for each whitelist MAC, with RETURN-if-in-allow-set as the carve-out.
# Order inside the chain (per MAC): ESTABLISHED bypass -> RETURN if dst in allow set -> DROP.
# RETURN exits this chain only -- the packet still passes KIDBLOCK_TIME / KIDBLOCK_DOMAINS,
# but those won't match a whitelist MAC since both are scoped to other MAC sets.
apply_whitelist_rules() {
  ensure_chains
  ensure_ipsets
  flush_whitelist_chain
  iptables  -A "$CHAIN_WHITELIST" -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN
  ip6tables -A "$CHAIN_WHITELIST" -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN
  local count=0
  while IFS= read -r mac; do
    [ -z "$mac" ] && continue
    iptables  -A "$CHAIN_WHITELIST" -m mac --mac-source "$mac" -m set --match-set "$IPSET_ALLOW_V4" dst -j RETURN
    ip6tables -A "$CHAIN_WHITELIST" -m mac --mac-source "$mac" -m set --match-set "$IPSET_ALLOW_V6" dst -j RETURN
    iptables  -A "$CHAIN_WHITELIST" -m mac --mac-source "$mac" -j DROP
    ip6tables -A "$CHAIN_WHITELIST" -m mac --mac-source "$mac" -j DROP
    count=$((count+1))
  done < <(get_macs_whitelist)
  [ "$count" -gt 0 ] && log "applied whitelist ($count MACs, default-DROP + allow-set carve-out)"
}

current_state() {
  [ -f "$STATE_FILE" ] && cat "$STATE_FILE" || echo unknown
}

now_minutes() {
  local h m
  h=$(date +%H); m=$(date +%M)
  echo $(( 10#$h * 60 + 10#$m ))
}

now_dow() { date +%w; }   # 0=Sun..6=Sat

dow_name() {
  case "$1" in
    0) echo sun ;; 1) echo mon ;; 2) echo tue ;; 3) echo wed ;;
    4) echo thu ;; 5) echo fri ;; 6) echo sat ;;
  esac
}

dow_num() {
  case "$1" in
    sun) echo 0 ;; mon) echo 1 ;; tue) echo 2 ;; wed) echo 3 ;;
    thu) echo 4 ;; fri) echo 5 ;; sat) echo 6 ;; *) echo -1 ;;
  esac
}

days_match() {
  local spec="$1" dow="$2"
  local today; today=$(dow_name "$dow")
  case "$spec" in
    \*) return 0 ;;
    *,*)
      local p
      IFS=, read -ra parts <<< "$spec"
      for p in "${parts[@]}"; do
        [ "$p" = "$today" ] && return 0
      done
      return 1
      ;;
    *-*)
      local from="${spec%-*}" to="${spec#*-}"
      local fi; fi=$(dow_num "$from")
      local ti; ti=$(dow_num "$to")
      { [ "$fi" -lt 0 ] || [ "$ti" -lt 0 ]; } && return 1
      if [ "$fi" -le "$ti" ]; then
        [ "$dow" -ge "$fi" ] && [ "$dow" -le "$ti" ] && return 0
        return 1
      else
        { [ "$dow" -ge "$fi" ] || [ "$dow" -le "$ti" ]; } && return 0
        return 1
      fi
      ;;
    *)
      [ "$spec" = "$today" ] && return 0
      return 1
      ;;
  esac
}

schedule_state_at() {
  local now_min="$1" now_dow="$2"
  [ -f "$SCHED_CONF" ] || { echo allow; return; }
  local line days range
  while IFS= read -r line; do
    line="${line%%#*}"
    # trim leading/trailing whitespace
    line="${line#"${line%%[![:space:]]*}"}"
    line="${line%"${line##*[![:space:]]}"}"
    [ -z "$line" ] && continue

    # Try "DAYS HH:MM-HH:MM" then "HH:MM-HH:MM" (legacy)
    if [[ "$line" =~ ^([a-z*,-]+)[[:space:]]+([0-9]{2}:[0-9]{2}-[0-9]{2}:[0-9]{2})$ ]]; then
      days="${BASH_REMATCH[1]}"
      range="${BASH_REMATCH[2]}"
    elif [[ "$line" =~ ^([0-9]{2}:[0-9]{2}-[0-9]{2}:[0-9]{2})$ ]]; then
      days="*"
      range="${BASH_REMATCH[1]}"
    else
      continue
    fi

    days_match "$days" "$now_dow" || continue

    local start="${range%-*}" end="${range#*-}"
    local sh="${start%:*}" sm="${start#*:}"
    local eh="${end%:*}"   em="${end#*:}"
    local s_min=$(( 10#$sh * 60 + 10#$sm ))
    local e_min
    if [ "$end" = "24:00" ]; then
      e_min=1440
    else
      e_min=$(( 10#$eh * 60 + 10#$em ))
    fi
    if [ "$now_min" -ge "$s_min" ] && [ "$now_min" -lt "$e_min" ]; then
      echo block
      return
    fi
  done < "$SCHED_CONF"
  echo allow
}

desired_state() {
  if [ -f "$OVERRIDE_FILE" ]; then
    local expiry mode
    read -r expiry mode < "$OVERRIDE_FILE" || true
    local now_epoch; now_epoch=$(date +%s)
    if [ -n "${expiry:-}" ] && [ "$now_epoch" -lt "$expiry" ]; then
      echo "$mode"
      return
    else
      rm -f "$OVERRIDE_FILE"
    fi
  fi
  schedule_state_at "$(now_minutes)" "$(now_dow)"
}

cmd_block()   { apply_block; }
cmd_allow()   { apply_allow; }

cmd_reapply() {
  ensure_chains
  apply_domain_rules
  apply_whitelist_rules
  local want; want=$(desired_state)
  local cur;  cur=$(current_state)
  if [ "$want" != "$cur" ]; then
    if [ "$want" = block ]; then apply_block; else apply_allow; fi
    log "tick: $cur -> $want"
  fi
}

cmd_override() {
  local mode="$1" minutes="${2:-}"
  if [ -z "$minutes" ] || ! echo "$minutes" | grep -qE '^[0-9]+$' || [ "$minutes" -lt 1 ]; then
    echo "Usage: $0 override-${mode} <minutes>" >&2
    exit 1
  fi
  local until=$(( $(date +%s) + minutes * 60 ))
  echo "$until $mode" > "$OVERRIDE_FILE"
  if [ "$mode" = block ]; then apply_block; else apply_allow; fi
  log "override $mode for ${minutes}m"
  echo "Override: $mode for $minutes min (until $(date -d @"$until" '+%H:%M' 2>/dev/null || echo "$until"))"
}

cmd_clear_override() {
  rm -f "$OVERRIDE_FILE"
  cmd_reapply
  echo "Override cleared, reverted to schedule."
}

cmd_status() {
  local cur sched_now want
  cur=$(current_state)
  sched_now=$(schedule_state_at "$(now_minutes)" "$(now_dow)")
  want=$(desired_state)
  echo "=== kidblock status ==="
  printf "Time now            : %s (%s)\n" "$(date '+%Y-%m-%d %H:%M:%S %Z')" "$(dow_name "$(now_dow)")"
  printf "Current applied     : %s\n" "$cur"
  printf "Schedule says now   : %s\n" "$sched_now"
  printf "Effective desired   : %s\n" "$want"
  echo
  if [ -f "$OVERRIDE_FILE" ]; then
    local expiry mode now_epoch
    read -r expiry mode < "$OVERRIDE_FILE" || true
    now_epoch=$(date +%s)
    if [ -n "${expiry:-}" ] && [ "$now_epoch" -lt "$expiry" ]; then
      printf "Override active     : %s until %s (%d min remaining)\n" "$mode" \
        "$(date -d @"$expiry" '+%H:%M' 2>/dev/null || echo "$expiry")" \
        "$(( (expiry - now_epoch + 59) / 60 ))"
    else
      echo "Override            : expired (will be cleaned up on next tick)"
    fi
  else
    echo "Override            : none"
  fi
  echo
  echo "Schedule rules:"
  if [ -f "$SCHED_CONF" ]; then
    awk 'NF && $1 !~ /^#/ {print "  " $0}' "$SCHED_CONF"
  else
    echo "  (no schedule file)"
  fi
  echo
  echo "Controlled devices (MACs):"
  if [ -f "$MAC_CONF" ]; then
    awk 'NF && $1 !~ /^#/ {print "  " $0}' "$MAC_CONF"
  else
    echo "  (no MAC file)"
  fi
  echo
  echo "Per-device domain blocking:"
  if [ -f "$DNSMASQ_CONF" ]; then
    local dom_count v4 v6
    dom_count=$(awk 'NF && $1 !~ /^#/' "$DOMAIN_CONF" 2>/dev/null | wc -l)
    v4=$(ipset list "$IPSET_V4" 2>/dev/null | awk '/Number of entries/{print $4}')
    v6=$(ipset list "$IPSET_V6" 2>/dev/null | awk '/Number of entries/{print $4}')
    echo "  ENABLED: $dom_count domains in conf"
    echo "  ipset entries currently: v4=${v4:-0}  v6=${v6:-0}"
  else
    echo "  disabled (run: sudo $0 install-domains)"
  fi
  echo
  echo "Per-device whitelist (allowlist):"
  if [ -f "$DNSMASQ_ALLOW_CONF" ]; then
    local allow_count av4 av6 wl_macs
    allow_count=$(awk 'NF && $1 !~ /^#/' "$ALLOW_CONF" 2>/dev/null | wc -l)
    av4=$(ipset list "$IPSET_ALLOW_V4" 2>/dev/null | awk '/Number of entries/{print $4}')
    av6=$(ipset list "$IPSET_ALLOW_V6" 2>/dev/null | awk '/Number of entries/{print $4}')
    wl_macs=$(get_macs_whitelist | wc -l)
    echo "  ENABLED: $allow_count domains in conf, $wl_macs whitelist MACs"
    echo "  ipset entries currently: v4=${av4:-0}  v6=${av6:-0}"
  else
    echo "  disabled (run: sudo $0 install-allowlist)"
  fi
  echo
  echo "iptables KIDBLOCK_TIME rules:"
  iptables -nvL "$CHAIN_TIME" 2>/dev/null | sed 's/^/  /' || echo "  (chain missing)"
  echo
  echo "iptables KIDBLOCK_DOMAINS rules:"
  iptables -nvL "$CHAIN_DOMAINS" 2>/dev/null | sed 's/^/  /' || echo "  (chain missing)"
  echo
  echo "iptables KIDBLOCK_WHITELIST rules:"
  iptables -nvL "$CHAIN_WHITELIST" 2>/dev/null | sed 's/^/  /' || echo "  (chain missing)"
}

cmd_install_domains() {
  if [ ! -f "$DOMAIN_CONF" ]; then
    echo "No $DOMAIN_CONF — nothing to install." >&2
    exit 1
  fi
  ensure_ipsets
  : > "$DNSMASQ_CONF"
  local count=0
  while IFS= read -r line; do
    line="${line%%#*}"
    line="${line//[[:space:]]/}"
    [ -z "$line" ] && continue
    echo "ipset=/${line}/${IPSET_V4},${IPSET_V6}" >> "$DNSMASQ_CONF"
    count=$((count+1))
  done < "$DOMAIN_CONF"
  # Hard restart, not SIGHUP — dnsmasq's ipset directives don't re-evaluate on SIGHUP.
  killall dnsmasq 2>/dev/null; sleep 1; /etc/init.d/dnsmasq start >/dev/null 2>&1 || true
  apply_domain_rules
  log "installed per-device domain blocklist ($count domains)"
  echo "Per-device domain blocklist installed ($count domains)."
  echo "Blocked only for MACs in $MAC_CONF (mode != whitelist); other devices unaffected."
}

cmd_uninstall_domains() {
  rm -f "$DNSMASQ_CONF"
  killall dnsmasq 2>/dev/null; sleep 1; /etc/init.d/dnsmasq start >/dev/null 2>&1 || true
  flush_domain_chain
  ipset flush "$IPSET_V4" 2>/dev/null || true
  ipset flush "$IPSET_V6" 2>/dev/null || true
  ipset destroy "$IPSET_V4" 2>/dev/null || true
  ipset destroy "$IPSET_V6" 2>/dev/null || true
  log "removed per-device domain blocklist"
  echo "Per-device domain blocklist removed."
}

cmd_install_allowlist() {
  if [ ! -f "$ALLOW_CONF" ]; then
    echo "No $ALLOW_CONF — nothing to install." >&2
    exit 1
  fi
  ensure_ipsets
  : > "$DNSMASQ_ALLOW_CONF"
  local count=0
  while IFS= read -r line; do
    line="${line%%#*}"
    line="${line//[[:space:]]/}"
    [ -z "$line" ] && continue
    echo "ipset=/${line}/${IPSET_ALLOW_V4},${IPSET_ALLOW_V6}" >> "$DNSMASQ_ALLOW_CONF"
    count=$((count+1))
  done < "$ALLOW_CONF"
  # Hard restart, not SIGHUP — dnsmasq's ipset directives don't re-evaluate on SIGHUP.
  killall dnsmasq 2>/dev/null; sleep 1; /etc/init.d/dnsmasq start >/dev/null 2>&1 || true
  apply_whitelist_rules
  log "installed per-device whitelist ($count domains)"
  echo "Per-device whitelist installed ($count allowed domains)."
  echo "Applied only to MACs in $MAC_CONF marked mode:whitelist; all other dest IPs DROPPED for those MACs."
}

cmd_uninstall_allowlist() {
  rm -f "$DNSMASQ_ALLOW_CONF"
  killall dnsmasq 2>/dev/null; sleep 1; /etc/init.d/dnsmasq start >/dev/null 2>&1 || true
  flush_whitelist_chain
  ipset flush "$IPSET_ALLOW_V4" 2>/dev/null || true
  ipset flush "$IPSET_ALLOW_V6" 2>/dev/null || true
  ipset destroy "$IPSET_ALLOW_V4" 2>/dev/null || true
  ipset destroy "$IPSET_ALLOW_V6" 2>/dev/null || true
  log "removed per-device whitelist"
  echo "Per-device whitelist removed. Whitelist MACs now unrestricted (subject to schedule + blocklist)."
}

case "${1:-}" in
  block)               cmd_block ;;
  allow)               cmd_allow ;;
  reapply|tick)        cmd_reapply ;;
  status)              cmd_status ;;
  override-allow)      cmd_override allow "${2:-}" ;;
  override-block)      cmd_override block "${2:-}" ;;
  clear-override)      cmd_clear_override ;;
  install-domains)     cmd_install_domains ;;
  uninstall-domains)   cmd_uninstall_domains ;;
  install-allowlist)   cmd_install_allowlist ;;
  uninstall-allowlist) cmd_uninstall_allowlist ;;
  init)                ensure_chains; apply_domain_rules; apply_whitelist_rules; cmd_reapply ;;
  *)
    echo "Usage: $0 {block|allow|reapply|status|override-allow N|override-block N|clear-override|install-domains|uninstall-domains|install-allowlist|uninstall-allowlist|init}"
    exit 1
    ;;
esac
