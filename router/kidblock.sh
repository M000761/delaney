#!/bin/bash
# kidblock.sh — schedule + per-device domain blocking + per-MAC overrides for an
# EdgeRouter (EdgeOS).
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
#
# Per-MAC overrides (DM9):
#   kidblock-overrides.conf holds one row per active override:
#     <MAC>  <verb>  <minutes>  <expiry-epoch>     # comments OK
#   verb is "block" or "allow". Expired rows are pruned at every reapply.
#   A per-MAC override takes precedence over both the schedule (KIDBLOCK_TIME)
#   AND any blocklist/whitelist filtering (KIDBLOCK_DOMAINS / KIDBLOCK_WHITELIST)
#   because the override rule lives at the top of KIDBLOCK_TIME, which ensure_chains()
#   places FIRST in the FORWARD chain (per its REVERSE iteration; see
#   router/tests/test_chain_order.sh), so its ACCEPT (allow) / DROP (block)
#   terminates FORWARD traversal before KIDBLOCK_DOMAINS / KIDBLOCK_WHITELIST run.

set -u

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
MAC_CONF="${SCRIPT_DIR}/kidblock-macs.conf"
DOMAIN_CONF="${SCRIPT_DIR}/kidblock-domains.conf"
ALLOW_CONF="${SCRIPT_DIR}/kidblock-allowlist.conf"
SCHED_CONF="${SCRIPT_DIR}/kidblock-schedule.conf"
OVERRIDE_CONF="${SCRIPT_DIR}/kidblock-overrides.conf"
LEGACY_OVERRIDE_FILE="/var/run/kidblock.override"
STATE_SIG_FILE="/var/run/kidblock.state.sig"
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

# Lowercased MAC (canonicalize so MAC_CONF + OVERRIDE_CONF keying matches regardless
# of upper/lower-case input on the CLI / from the UI).
norm_mac() { echo "$1" | tr '[:upper:]' '[:lower:]'; }

is_mac() {
  echo "$1" | grep -qE '^[0-9a-fA-F]{2}(:[0-9a-fA-F]{2}){5}$'
}

is_number() {
  echo "$1" | grep -qE '^[0-9]+$'
}

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
  # REVERSED iteration: -I FORWARD 1 stacks insertions, so the LAST iterated chain
  # lands FIRST in FORWARD. TIME goes LAST so its override-row visits FIRST. Do not
  # re-order without re-thinking; reds the override precedence invariant. (DM21; see
  # router/tests/test_chain_order.sh.)
  for c in "$CHAIN_WHITELIST" "$CHAIN_DOMAINS" "$CHAIN_TIME"; do
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

# ==========================================================================
# Per-MAC override state (DM9)
# ==========================================================================

# Print sorted unexpired override rows: "<mac> <verb> <minutes> <expiry-epoch>".
# Comments + blank lines + expired entries silently dropped.
read_overrides() {
  [ -f "$OVERRIDE_CONF" ] || return 0
  local now_epoch; now_epoch=$(date +%s)
  awk -v now="$now_epoch" '
    {
      sub(/#.*/, "")
    }
    NF >= 4 {
      mac=tolower($1); verb=$2; mins=$3; expiry=$4
      if (verb != "block" && verb != "allow") next
      if (expiry+0 <= now) next
      print mac, verb, mins, expiry
    }
  ' "$OVERRIDE_CONF" | sort
}

# Atomically rewrite OVERRIDE_CONF without expired entries. If no rows survive,
# remove the file (cleaner than an empty file).
prune_expired_overrides() {
  [ -f "$OVERRIDE_CONF" ] || return 0
  local now_epoch; now_epoch=$(date +%s)
  local tmp="${OVERRIDE_CONF}.tmp.$$"
  {
    echo "# kidblock per-MAC overrides (DM9)"
    echo "# <MAC>  <verb>  <minutes>  <expiry-epoch>"
    awk -v now="$now_epoch" '
      {
        sub(/#.*/, "")
      }
      NF >= 4 {
        mac=tolower($1); verb=$2; mins=$3; expiry=$4
        if (verb != "block" && verb != "allow") next
        if (expiry+0 <= now) next
        printf "%-19s %-6s %-7s %s\n", mac, verb, mins, expiry
      }
    ' "$OVERRIDE_CONF"
  } > "$tmp"
  # If no data rows survived, the file is just the header + comment. Keep it
  # (so the script + UI can read consistently). Atomic mv either way.
  mv "$tmp" "$OVERRIDE_CONF"
}

# Replace any existing row for $mac in OVERRIDE_CONF with the new row.
# If $mac is missing, append. Atomic via temp + mv.
write_override_row() {
  local mac="$1" verb="$2" minutes="$3"
  mac=$(norm_mac "$mac")
  local now_epoch; now_epoch=$(date +%s)
  local expiry=$(( now_epoch + minutes * 60 ))
  local tmp="${OVERRIDE_CONF}.tmp.$$"
  {
    if [ -f "$OVERRIDE_CONF" ]; then
      # Keep header / comments, drop any old row for this mac
      awk -v target="$mac" '
        /^[[:space:]]*#/ || NF == 0 { print; next }
        { if (tolower($1) != target) print }
      ' "$OVERRIDE_CONF"
    else
      echo "# kidblock per-MAC overrides (DM9)"
      echo "# <MAC>  <verb>  <minutes>  <expiry-epoch>"
    fi
    printf "%-19s %-6s %-7s %s\n" "$mac" "$verb" "$minutes" "$expiry"
  } > "$tmp"
  mv "$tmp" "$OVERRIDE_CONF"
}

# Remove any row for $mac from OVERRIDE_CONF. Atomic.
remove_override_row() {
  local mac; mac=$(norm_mac "$1")
  [ -f "$OVERRIDE_CONF" ] || return 0
  local tmp="${OVERRIDE_CONF}.tmp.$$"
  awk -v target="$mac" '
    /^[[:space:]]*#/ || NF == 0 { print; next }
    { if (tolower($1) != target) print }
  ' "$OVERRIDE_CONF" > "$tmp"
  mv "$tmp" "$OVERRIDE_CONF"
}

# Truncate to header-only.
clear_all_override_rows() {
  local tmp="${OVERRIDE_CONF}.tmp.$$"
  {
    echo "# kidblock per-MAC overrides (DM9)"
    echo "# <MAC>  <verb>  <minutes>  <expiry-epoch>"
  } > "$tmp"
  mv "$tmp" "$OVERRIDE_CONF"
}

# Pre-DM9 single-global-override migration: if /var/run/kidblock.override exists
# with an unexpired entry, promote it to per-MAC entries for every controlled MAC
# then drop the legacy file. Idempotent.
migrate_legacy_override() {
  [ -f "$LEGACY_OVERRIDE_FILE" ] || return 0
  local expiry mode now_epoch
  read -r expiry mode < "$LEGACY_OVERRIDE_FILE" || true
  rm -f "$LEGACY_OVERRIDE_FILE"
  [ -z "${expiry:-}" ] && return 0
  now_epoch=$(date +%s)
  [ "$now_epoch" -ge "$expiry" ] && return 0
  [ "$mode" != "block" ] && [ "$mode" != "allow" ] && return 0
  local remaining=$(( (expiry - now_epoch + 59) / 60 ))
  [ "$remaining" -lt 1 ] && return 0
  local mac
  while IFS= read -r mac; do
    [ -z "$mac" ] && continue
    write_override_row "$mac" "$mode" "$remaining"
  done < <(get_macs)
  log "migrated legacy global override ($mode, ${remaining}m remaining) to per-MAC entries"
}

# ==========================================================================
# Time-chain builder (DM9)
# ==========================================================================

# Build KIDBLOCK_TIME deterministically from {schedule_at_now} + {overrides.conf}.
# Replaces the pre-DM9 apply_block / apply_allow pair for the schedule axis;
# the per-MAC override entries live at the top of the chain so they preempt
# both the schedule's full-block (added below them when active) AND the
# downstream KIDBLOCK_DOMAINS / KIDBLOCK_WHITELIST chains (ACCEPT/DROP terminates
# FORWARD traversal).
apply_time_chain() {
  ensure_chains
  flush_time_chain
  iptables  -A "$CHAIN_TIME" -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN
  ip6tables -A "$CHAIN_TIME" -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN

  # Per-MAC overrides at the top: allow=ACCEPT (bypass everything),
  # block=DROP (preempt schedule).
  local override_macs=""
  local row mac verb mins exp
  while IFS= read -r row; do
    [ -z "$row" ] && continue
    set -- $row
    mac="$1"; verb="$2"; mins="$3"; exp="$4"
    override_macs="$override_macs $mac"
    if [ "$verb" = "allow" ]; then
      iptables  -A "$CHAIN_TIME" -m mac --mac-source "$mac" -j ACCEPT
      ip6tables -A "$CHAIN_TIME" -m mac --mac-source "$mac" -j ACCEPT
    else
      iptables  -A "$CHAIN_TIME" -m mac --mac-source "$mac" -j DROP
      ip6tables -A "$CHAIN_TIME" -m mac --mac-source "$mac" -j DROP
    fi
  done < <(read_overrides)

  # Schedule-block DROP rules for controlled MACs NOT in overrides.
  local sched; sched=$(schedule_state_at "$(now_minutes)" "$(now_dow)")
  if [ "$sched" = "block" ]; then
    local m
    while IFS= read -r m; do
      [ -z "$m" ] && continue
      case " $override_macs " in *" $m "*) continue ;; esac
      iptables  -A "$CHAIN_TIME" -m mac --mac-source "$m" -j DROP
      ip6tables -A "$CHAIN_TIME" -m mac --mac-source "$m" -j DROP
    done < <(get_macs)
  fi
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
  local mac
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
  local mac
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

# Signature of the effective time-chain inputs: schedule_state + sorted active
# overrides + macs.conf snapshot. Used to suppress redundant log lines on
# no-change reapply ticks (per the DM9 NO clause: don't log-spam).
state_signature() {
  local sched; sched=$(schedule_state_at "$(now_minutes)" "$(now_dow)")
  echo "sched=$sched"
  read_overrides
  echo "---"
  get_macs | sort
}

# One-line human summary of the effective state (count of block/allow overrides
# + schedule). Used in the reapply log entry on state changes.
human_summary() {
  local sched; sched=$(schedule_state_at "$(now_minutes)" "$(now_dow)")
  local n_block=0 n_allow=0
  local row verb
  while IFS= read -r row; do
    [ -z "$row" ] && continue
    set -- $row
    verb="$2"
    if [ "$verb" = "block" ]; then n_block=$((n_block+1))
    else n_allow=$((n_allow+1))
    fi
  done < <(read_overrides)
  echo "schedule=$sched; per-MAC overrides: ${n_allow} allow / ${n_block} block"
}

cmd_reapply() {
  ensure_chains
  ensure_ipsets
  migrate_legacy_override
  prune_expired_overrides
  apply_time_chain
  apply_domain_rules
  apply_whitelist_rules
  local sig; sig=$(state_signature)
  local prev=""
  [ -f "$STATE_SIG_FILE" ] && prev=$(cat "$STATE_SIG_FILE" 2>/dev/null || true)
  if [ "$sig" != "$prev" ]; then
    echo "$sig" > "$STATE_SIG_FILE"
    log "reapply: $(human_summary)"
  fi
}

# Resolve the override verb args into (target, minutes):
#   <MAC> <minutes>      -> target=MAC
#   --all <minutes>      -> target=__all__
#   <minutes>            -> target=__all__ (back-compat, pre-DM9 form)
# On parse failure prints usage to stderr + returns 1.
parse_override_args() {
  local verb="$1"
  shift
  if [ "$#" -lt 1 ]; then
    echo "Usage: $0 override-${verb} {<MAC>|--all} <minutes>" >&2
    echo "       $0 override-${verb} <minutes>   (back-compat alias for --all)" >&2
    return 1
  fi
  if [ "$1" = "--all" ]; then
    shift
    if [ "$#" -ne 1 ] || ! is_number "$1" || [ "$1" -lt 1 ]; then
      echo "Usage: $0 override-${verb} --all <minutes>" >&2
      return 1
    fi
    echo "__all__ $1"
    return 0
  fi
  if is_mac "$1"; then
    local mac; mac=$(norm_mac "$1")
    shift
    if [ "$#" -ne 1 ] || ! is_number "$1" || [ "$1" -lt 1 ]; then
      echo "Usage: $0 override-${verb} $mac <minutes>" >&2
      return 1
    fi
    echo "$mac $1"
    return 0
  fi
  if is_number "$1" && [ "$#" -eq 1 ] && [ "$1" -ge 1 ]; then
    echo "__all__ $1"
    return 0
  fi
  echo "Usage: $0 override-${verb} {<MAC>|--all} <minutes>" >&2
  return 1
}

cmd_override() {
  local verb="$1"; shift
  local parsed; parsed=$(parse_override_args "$verb" "$@") || exit 1
  local target="${parsed% *}"
  local minutes="${parsed#* }"
  if [ "$target" = "__all__" ]; then
    local count=0
    local mac
    while IFS= read -r mac; do
      [ -z "$mac" ] && continue
      write_override_row "$mac" "$verb" "$minutes"
      count=$((count+1))
    done < <(get_macs)
    cmd_reapply
    log "override $verb --all $minutes (${count} MACs)"
    echo "Override: $verb on all $count controlled devices for $minutes min."
  else
    write_override_row "$target" "$verb" "$minutes"
    cmd_reapply
    log "override $verb $target $minutes"
    local until_h; until_h=$(date -d "+$minutes minutes" '+%H:%M' 2>/dev/null || echo "")
    if [ -n "$until_h" ]; then
      echo "Override: $verb on $target for $minutes min (until $until_h)."
    else
      echo "Override: $verb on $target for $minutes min."
    fi
  fi
}

cmd_clear_override() {
  local target="${1:-}"
  if [ -z "$target" ] || [ "$target" = "--all" ]; then
    clear_all_override_rows
    rm -f "$LEGACY_OVERRIDE_FILE"
    cmd_reapply
    log "cleared all per-MAC overrides"
    echo "Cleared all overrides; reverted to schedule."
    return
  fi
  if ! is_mac "$target"; then
    echo "Usage: $0 clear-override {<MAC>|--all}" >&2
    exit 1
  fi
  local mac; mac=$(norm_mac "$target")
  remove_override_row "$mac"
  cmd_reapply
  log "cleared override for $mac"
  echo "Cleared override for $mac; device returns to schedule."
}

# Manual "force everything to block/allow until expiry" -- kept for back-compat
# with any external scripts that called the pre-DM9 cmd_block / cmd_allow. Now
# routed through the per-MAC primitive (1440-min = 24h ceiling, same as KILL),
# so the next reapply tick sees consistent per-MAC state and doesn't revert.
cmd_block() { cmd_override block --all 1440; }
cmd_allow() { cmd_override allow --all 1440; }

# Print one-line override state for a single MAC, or "none" if no row.
override_state_for_mac() {
  local target; target=$(norm_mac "$1")
  local row mac verb mins exp
  while IFS= read -r row; do
    [ -z "$row" ] && continue
    set -- $row
    mac="$1"; verb="$2"; mins="$3"; exp="$4"
    if [ "$mac" = "$target" ]; then
      local until_h; until_h=$(date -d @"$exp" '+%H:%M' 2>/dev/null || echo "$exp")
      local rem_min=$(( (exp - $(date +%s) + 59) / 60 ))
      echo "$mac: $verb until $until_h ($rem_min min remaining)"
      return
    fi
  done < <(read_overrides)
  echo "$target: none"
}

cmd_status() {
  # Single-MAC status sub-mode: `status <MAC>` -> just that MAC's override state.
  if [ -n "${1:-}" ]; then
    if ! is_mac "$1"; then
      echo "Usage: $0 status [<MAC>]" >&2
      exit 1
    fi
    prune_expired_overrides
    override_state_for_mac "$1"
    return
  fi

  prune_expired_overrides
  local sched_now
  sched_now=$(schedule_state_at "$(now_minutes)" "$(now_dow)")
  echo "=== kidblock status ==="
  printf "Time now            : %s (%s)\n" "$(date '+%Y-%m-%d %H:%M:%S %Z')" "$(dow_name "$(now_dow)")"
  printf "Schedule says now   : %s\n" "$sched_now"
  printf "Summary             : %s\n" "$(human_summary)"
  echo
  echo "Per-MAC overrides:"
  local any=0
  local row mac verb mins exp
  while IFS= read -r row; do
    [ -z "$row" ] && continue
    set -- $row
    mac="$1"; verb="$2"; mins="$3"; exp="$4"
    local until_h; until_h=$(date -d @"$exp" '+%H:%M' 2>/dev/null || echo "$exp")
    local rem_min=$(( (exp - $(date +%s) + 59) / 60 ))
    printf "  %-19s %-6s until %s (%d min remaining)\n" "$mac" "$verb" "$until_h" "$rem_min"
    any=1
  done < <(read_overrides)
  [ "$any" = "0" ] && echo "  (none)"
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
  status)              shift; cmd_status "${1:-}" ;;
  override-allow)      shift; cmd_override allow "$@" ;;
  override-block)      shift; cmd_override block "$@" ;;
  clear-override)      shift; cmd_clear_override "${1:-}" ;;
  install-domains)     cmd_install_domains ;;
  uninstall-domains)   cmd_uninstall_domains ;;
  install-allowlist)   cmd_install_allowlist ;;
  uninstall-allowlist) cmd_uninstall_allowlist ;;
  init)                ensure_chains; apply_domain_rules; apply_whitelist_rules; cmd_reapply ;;
  *)
    cat >&2 <<EOF
Usage: $0 <subcommand> [args]

Subcommands:
  reapply | tick               Re-evaluate KIDBLOCK_TIME against schedule + overrides
  status [<MAC>]               Print status (or single-MAC override state)
  override-block <MAC> <min>   Block <MAC> only for <min> minutes
  override-allow <MAC> <min>   Allow <MAC> only for <min> minutes
  override-block --all <min>   Block ALL controlled MACs for <min> minutes (bulk)
  override-allow --all <min>   Allow ALL controlled MACs for <min> minutes (bulk)
  override-block <min>         Back-compat alias for --all
  override-allow <min>         Back-compat alias for --all
  clear-override <MAC>         Remove override for <MAC>
  clear-override --all         Remove all per-MAC overrides
  clear-override               Back-compat alias for --all
  block                        Block all controlled devices (24h --all, DM9-compat)
  allow                        Allow all controlled devices (24h --all, DM9-compat)
  install-domains              Push dnsmasq + iptables blocklist rules
  uninstall-domains            Tear down blocklist
  install-allowlist            Push dnsmasq + iptables whitelist rules
  uninstall-allowlist          Tear down whitelist
  init                         Rebuild all chains (called from boot hook)
EOF
    exit 1
    ;;
esac
