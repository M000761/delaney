#!/bin/bash
# test_explain_mac.sh (DM22) -- hermetic, root-free guard for `kidblock.sh explain-mac`.
#
# WHY: explain-mac MUST be read-only (the UI Why? dialog ssh-execs it on a 5s timer;
# it must never mutate iptables / ipset / dnsmasq state). It must also resolve the
# right verdict in actual FORWARD visit order -- the 2026-06-22 NUC-M bug was a
# chain-order desync that made an active Allow override LOOK active while
# KIDBLOCK_DOMAINS silently dropped her traffic (the regression DM21 fixed).
#
# Unlike test_chain_order.sh, this test needs NO root and NO live router: it copies
# kidblock.sh into a temp dir (so SCRIPT_DIR + the *.conf reads point at fixtures),
# shims iptables / ip6tables / ipset on PATH, and points EXPLAIN_* at fixture logs.
# Every shim RECORDS any state-mutating verb; the read-only assertion is that the
# recording stays empty across every scenario. Run before deploying a kidblock.sh
# change:   router/tests/test_explain_mac.sh
#
# Override the script under test with KIDBLOCK_SH=/path/to/kidblock.sh if needed.

set -u

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
KIDBLOCK="${KIDBLOCK_SH:-$SCRIPT_DIR/../kidblock.sh}"
MAC="7c:50:79:f7:db:13"
DST="142.250.4.102"

if [ ! -f "$KIDBLOCK" ]; then
  echo "FAIL: kidblock.sh not found at $KIDBLOCK (set KIDBLOCK_SH to override)"; exit 2
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
BIN="$TMP/bin"
mkdir -p "$BIN"
MUTATIONS="$TMP/mutations.log"
: > "$MUTATIONS"

# ---- fixtures next to the copied script (SCRIPT_DIR=$TMP) -------------------
cp "$KIDBLOCK" "$TMP/kidblock.sh"

cat > "$TMP/kidblock-macs.conf" <<EOF
# fixture
$MAC   NUC-M
aa:bb:cc:dd:ee:ff   WL-Device   mode:whitelist
EOF

# Active allow override for the MAC, expiry 1h out (so it never looks expired).
EXP="$(( $(date +%s) + 3600 ))"
cat > "$TMP/kidblock-overrides.conf" <<EOF
# fixture per-MAC overrides
$MAC   allow   60   $EXP
EOF

# Empty schedule -> schedule_state_at returns "allow" (no schedule-block path).
cat > "$TMP/kidblock-schedule.conf" <<EOF
# no windows
EOF

# Domain conf with the YouTube category so the dst-ip hint resolves a category.
cat > "$TMP/kidblock-domains.conf" <<EOF
# fixture domains
# --- YouTube ---
youtube.com
googlevideo.com
EOF

# Live dnsmasq log: a reply mapping googlevideo.com -> the dst-ip.
cat > "$TMP/messages.log" <<EOF
Jun 24 19:14:32 EdgeRouter dnsmasq[1234]: query[A] googlevideo.com from 192.168.200.176
Jun 24 19:14:32 EdgeRouter dnsmasq[1234]: reply googlevideo.com is $DST
EOF

# kidblock log with a couple of lines mentioning the MAC.
cat > "$TMP/kidblock.log" <<EOF
2026-06-25 01:10:00 override allow $MAC 60
2026-06-25 01:09:00 reapply: schedule=allow; per-MAC overrides: 1 allow / 0 block
2026-06-25 01:08:00 unrelated line for bb:bb:bb:bb:bb:bb
EOF

# ---- shims -----------------------------------------------------------------
# iptables: -S FORWARD prints the kidblock jumps in KB_TEST_ORDER; -nvL <chain>
# prints that chain's fixture rules. Any mutating verb is recorded (and the test
# then fails on a non-empty mutation log).
cat > "$BIN/iptables" <<SHIM
#!/bin/bash
echo "iptables \$*" >> "$MUTATIONS.all"
case "\$1" in
  -S)
    if [ "\$2" = "FORWARD" ]; then
      for ch in \${KB_TEST_ORDER:-KIDBLOCK_TIME KIDBLOCK_DOMAINS KIDBLOCK_WHITELIST}; do
        echo "-A FORWARD -j \$ch"
      done
    fi
    exit 0 ;;
  -nvL|-nL)
    chain="\$2"
    echo "Chain \$chain (1 references)"
    echo " pkts bytes target     prot opt in     out     source               destination"
    case "\$chain" in
      KIDBLOCK_TIME)
        echo "   42  9000 ACCEPT     all  --  *      *       0.0.0.0/0            0.0.0.0/0            MAC 7C:50:79:F7:DB:13" ;;
      KIDBLOCK_DOMAINS)
        echo "    3   180 DROP       all  --  *      *       0.0.0.0/0            0.0.0.0/0            MAC 7C:50:79:F7:DB:13 match-set kidblock_domains_v4 dst" ;;
      KIDBLOCK_WHITELIST)
        : ;;  # no rule for this MAC
    esac
    exit 0 ;;
  -A|-I|-D|-F|-N|-X|-P|-R|-Z)
    echo "iptables \$*" >> "$MUTATIONS"; exit 0 ;;
esac
exit 0
SHIM

cat > "$BIN/ip6tables" <<SHIM
#!/bin/bash
echo "ip6tables \$*" >> "$MUTATIONS.all"
case "\$1" in
  -A|-I|-D|-F|-N|-X|-P|-R|-Z) echo "ip6tables \$*" >> "$MUTATIONS" ;;
esac
exit 0
SHIM

# ipset: `test <set> <ip>` -> 0 only for the domain set + dst-ip; mutating verbs recorded.
cat > "$BIN/ipset" <<SHIM
#!/bin/bash
echo "ipset \$*" >> "$MUTATIONS.all"
case "\$1" in
  test)
    if [ "\$2" = "kidblock_domains_v4" ] && [ "\$3" = "$DST" ]; then exit 0; fi
    exit 1 ;;
  create|add|del|destroy|flush|swap|rename|restore)
    echo "ipset \$*" >> "$MUTATIONS"; exit 0 ;;
  list) exit 0 ;;
esac
exit 0
SHIM

chmod +x "$BIN/iptables" "$BIN/ip6tables" "$BIN/ipset"

run_explain() {
  PATH="$BIN:$PATH" \
  EXPLAIN_KIDBLOCK_LOG="$TMP/kidblock.log" \
  EXPLAIN_MESSAGES_LOG="$TMP/messages.log" \
  KB_TEST_ORDER="$1" \
    bash "$TMP/kidblock.sh" explain-mac "${@:2}"
}

fail() { echo "FAIL: $1"; exit 1; }

assert_contains() {
  case "$2" in
    *"$3"*) : ;;
    *) echo "----- output -----"; printf '%s\n' "$2"; echo "------------------"; fail "$1: expected to contain [$3]" ;;
  esac
}

assert_readonly() {
  if [ -s "$MUTATIONS" ]; then
    echo "----- mutations recorded -----"; cat "$MUTATIONS"; echo "------------------------------"
    fail "$1: explain-mac invoked a state-mutating iptables/ipset verb (must be read-only)"
  fi
}

# === Scenario 1: post-DM21 (TIME first) + override-allow + dst -> ALLOWED ===
OUT="$(run_explain "KIDBLOCK_TIME KIDBLOCK_DOMAINS KIDBLOCK_WHITELIST" "$MAC" "$DST")"
assert_contains "post-DM21 human" "$OUT" "VERDICT: ALLOWED by KIDBLOCK_TIME: override-allow"
assert_contains "post-DM21 human" "$OUT" "min remaining"
assert_readonly "post-DM21 human"

# === Scenario 2: pre-DM21 order (DOMAINS first) + dst -> BLOCKED by DOMAINS ===
OUT="$(run_explain "KIDBLOCK_DOMAINS KIDBLOCK_WHITELIST KIDBLOCK_TIME" "$MAC" "$DST")"
assert_contains "pre-DM21 human" "$OUT" "VERDICT: BLOCKED by KIDBLOCK_DOMAINS: domain-block"
assert_contains "pre-DM21 human" "$OUT" "kidblock_domains_v4"
assert_contains "pre-DM21 human" "$OUT" "googlevideo.com"
assert_contains "pre-DM21 human" "$OUT" "YouTube"
assert_readonly "pre-DM21 human"

# === Scenario 3: --json for the post-DM21 case -> parseable verdict fields ===
JSON="$(run_explain "KIDBLOCK_TIME KIDBLOCK_DOMAINS KIDBLOCK_WHITELIST" "--json" "$MAC" "$DST")"
assert_contains "json verdict"  "$JSON" '"verdict":"ALLOWED"'
assert_contains "json reason"   "$JSON" '"verdict_reason":"override-allow"'
assert_contains "json chain"    "$JSON" '"verdict_chain":"KIDBLOCK_TIME"'
assert_contains "json chains[]" "$JSON" '"mac_rule_present":true'
assert_contains "json ipset"    "$JSON" '"ipset_hint":"kidblock_domains_v4"'
assert_contains "json log"      "$JSON" "override allow $MAC 60"
assert_readonly "json"

# Balanced single-line JSON object sanity (one '{' open, ends with '}').
case "$JSON" in
  '{'*'}') : ;;
  *) fail "json: output is not a single JSON object" ;;
esac

# === Scenario 4: no dst-ip on a blocklist MAC with override cleared -> conditional ===
cat > "$TMP/kidblock-overrides.conf" <<EOF
# no active overrides
EOF
OUT="$(run_explain "KIDBLOCK_TIME KIDBLOCK_DOMAINS KIDBLOCK_WHITELIST" "$MAC")"
assert_contains "no-dst human" "$OUT" "VERDICT: ALLOWED"
assert_contains "no-dst human" "$OUT" "kidblock_domains_v4"
assert_readonly "no-dst human"

echo "PASS: explain-mac is read-only (no mutating iptables/ipset verb) and resolves"
echo "      ALLOWED(override) / BLOCKED(domain, pre-DM21 order) / conditional verdicts;"
echo "      --json emits parseable verdict + chains[] + recent_log."
exit 0
