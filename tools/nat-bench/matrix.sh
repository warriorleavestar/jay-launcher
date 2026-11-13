#!/bin/bash
# NAT matrix for Jay's direct connections: one machine, user namespaces, no root, no real network.
#
# Each scenario builds two homes (host and joining player) behind routers with a chosen NAT behaviour, a fake
# public network (203.0.113.0/24, a documentation range) with two STUN servers and a plain EasyTier relay node,
# then runs the same sealed direct flow as run.sh (JAYDS1 offer + passphrase, JAYDR1 reply) with the relay node
# as the fallback. It records whether the two connected, over which path, and how long the join took.
#
#   NAT kinds:  cone        port-preserving masquerade, only replies from contacted peers get in (port-restricted cone)
#               portchange  like cone but the external port is remapped to 20000-29999 (common for carrier NAT)
#               symmetric   a new random external port for every destination
#               cgnat       a home router (cone) behind a carrier NAT (portchange) on 100.64.0.0/24
#               noudp       the router drops all outgoing UDP (some school / office networks): only TCP to the node
#               none        no router: the machine sits directly on the public network
#
# Usage: matrix.sh <Amber.Cli path> <work dir> [scenario ...]
#   Scenarios (default: all): cone-cone portchange-cone symmetric-cone symmetric-symmetric cgnat-cone
#                             cone-noudp same-lan expiry
#   The work dir needs Amber/easytier/<version>/linux-x86_64/ (run `Amber.Cli lan-setup` with HOME=<work dir> once).
# Env: JOIN_SECONDS (default 180; Jay punches for up to 120 s before it falls back to the relay) how long the joining side runs; NAT_TIMEOUT (default 30) idle UDP mapping lifetime.
# Results: <work dir>/matrix/<scenario>/{host,join,node}.log and <work dir>/matrix/results.tsv.
# Needs Linux with user namespaces, iproute2 and nft. IPv6 paths are not covered (many CI kernels have no IPv6).
set -u
CLI=$(realpath "$1"); WORK=$(realpath "$2"); shift 2
HERE=$(dirname "$(realpath "$0")")
ALL="cone-cone portchange-cone symmetric-cone symmetric-symmetric cgnat-cone cone-noudp same-lan expiry"
SCENARIOS=${*:-$ALL}

if [ "${INSIDE_NS:-}" != 1 ]; then
  mkdir -p "$WORK/matrix"
  for s in $SCENARIOS; do
    case " $ALL " in *" $s "*) ;; *) echo "unknown scenario $s (known: $ALL)"; exit 1 ;; esac
  done
  printf 'scenario\thost NAT\tjoin NAT\tresult\tpath\tjoin ms\n' > "$WORK/matrix/results.tsv"
  for s in $SCENARIOS; do
    echo "== $s =="
    # A PID namespace with the scenario as its init: when the scenario ends, the kernel stops everything it started.
    env INSIDE_NS=1 SCENARIO="$s" unshare -rn --mount --propagation private --pid --fork --kill-child "$0" "$CLI" "$WORK" \
      || echo "$s: setup failed (see above)"
  done
  echo; cat "$WORK/matrix/results.tsv"
  exit 0
fi

S=$SCENARIO
case $S in
  cone-cone)           HNAT=cone;       JNAT=cone;      LAN=two;  IDLE=5 ;;
  portchange-cone)     HNAT=portchange; JNAT=cone;      LAN=two;  IDLE=5 ;;
  symmetric-cone)      HNAT=symmetric;  JNAT=cone;      LAN=two;  IDLE=5 ;;
  symmetric-symmetric) HNAT=symmetric;  JNAT=symmetric; LAN=two;  IDLE=5 ;;
  cgnat-cone)          HNAT=cgnat;      JNAT=cone;      LAN=two;  IDLE=5 ;;
  cone-noudp)          HNAT=cone;       JNAT=noudp;     LAN=two;  IDLE=5 ;;
  same-lan)            HNAT=cone;       JNAT=cone;      LAN=one;  IDLE=5 ;;
  expiry)              HNAT=portchange; JNAT=cone;      LAN=two;  IDLE=$(( ${NAT_TIMEOUT:-30} + 15 )) ;;
  *) echo "unknown scenario $S"; exit 1 ;;
esac
OUT="$WORK/matrix/$S"; rm -rf "$OUT"; mkdir -p "$OUT"
JOIN_SECONDS=${JOIN_SECONDS:-180}

# Every setup step must work: a missing firewall rule would turn a strict NAT into an open one and the results
# would look fine but be wrong. Any failure ends the scenario with "setup-failed" in results.tsv.
set -eE
trap 'echo "$S: setup failed at line $LINENO: $BASH_COMMAND" >&2
      printf "%s\t-\t-\tsetup-failed\tline %s\t-\n" "$S" "$LINENO" >> "$WORK/matrix/results.tsv"; exit 2' ERR

mount -t tmpfs none /run && mkdir -p /run/netns
# A /proc for this PID namespace: .NET looks up its child processes there.
mount -t proc proc /proc
mount -t tmpfs none /sys 2>/dev/null || true; mount -t sysfs sysfs /sys 2>/dev/null || true
X() { local ns=$1; shift; nsenter --net=/run/netns/$ns "$@"; }
ip link set lo up
ip link add name br0 type bridge && ip link set br0 up
ip addr add 203.0.113.254/24 dev br0
ip addr add 203.0.113.10/24 dev br0
ip addr add 203.0.113.11/24 dev br0

# veth <ns-a> <if-a> <ns-b|-> <if-b>: "-" plugs the far end into the public bridge.
veth() {
  ip link add name "$2" type veth peer name "$4"
  ip link set "$2" netns "$1"
  if [ "$3" = - ]; then ip link set "$4" master br0 up; else ip link set "$4" netns "$3"; fi
}
up() { local ns=$1; shift; X "$ns" ip link set lo up; for i in "$@"; do X "$ns" ip link set "$i" up; done; }

# nat_router <router ns> <wan if> <wan ip/24> <lan if> <lan ip/24> <kind>
nat_router() {
  local ns=$1 wan=$2 wanip=$3 lan=$4 lanip=$5 kind=$6
  up "$ns" "$wan" "$lan"
  X "$ns" ip addr add "$wanip" dev "$wan"; X "$ns" ip addr add "$lanip" dev "$lan"
  X "$ns" sysctl -qw net.ipv4.ip_forward=1
  X "$ns" sysctl -qw net.netfilter.nf_conntrack_udp_timeout=${NAT_TIMEOUT:-30} net.netfilter.nf_conntrack_udp_timeout_stream=${NAT_TIMEOUT:-30}
  [ "$(X "$ns" sysctl -n net.netfilter.nf_conntrack_udp_timeout)" = "${NAT_TIMEOUT:-30}" ]
  X "$ns" nft add table ip nat
  X "$ns" nft "add chain ip nat post { type nat hook postrouting priority 100 ; }"
  case $kind in
    portchange) X "$ns" nft add rule ip nat post oifname "$wan" meta l4proto udp masquerade to :20000-29999 ;;
    symmetric)  X "$ns" nft add rule ip nat post oifname "$wan" meta l4proto udp masquerade random ;;
  esac
  X "$ns" nft add rule ip nat post oifname "$wan" masquerade
  # Home-router firewall: unsolicited packets from the WAN side are dropped.
  X "$ns" nft add table ip filter
  X "$ns" nft "add chain ip filter input { type filter hook input priority 0 ; }"
  X "$ns" nft add rule ip filter input iifname "$wan" ct state new drop
  X "$ns" nft "add chain ip filter wanfw { type filter hook forward priority 0 ; }"
  X "$ns" nft add rule ip filter wanfw iifname "$wan" ct state new drop
  if [ "$kind" = noudp ]; then X "$ns" nft add rule ip filter wanfw oifname "$wan" meta l4proto udp drop; fi
}

# home <side> <id> <nat kind> <lan prefix> <public ip>: a machine "<side>pc" behind router "<side>r".
home() {
  local side=$1 id=$2 kind=$3 lan=$4 pub=$5
  ip netns add ${side}pc
  if [ "$kind" = none ]; then
    veth ${side}pc ${side}eth - ${side}wan-br
    up ${side}pc ${side}eth; X ${side}pc ip addr add $pub/24 dev ${side}eth; X ${side}pc ip route add default via 203.0.113.254
    return
  fi
  ip netns add ${side}r
  veth ${side}pc ${side}eth ${side}r ${side}lan
  if [ "$kind" = cgnat ]; then
    # Home router (cone) on 100.64.$id.0/24, behind a carrier router (portchange) with the public address.
    ip netns add ${side}isp
    veth ${side}r ${side}wan ${side}isp ${side}ispl
    veth ${side}isp ${side}ispw - ${side}ispw-br
    nat_router ${side}isp ${side}ispw $pub/24 ${side}ispl 100.64.$id.1/24 portchange
    X ${side}isp ip route add default via 203.0.113.254
    nat_router ${side}r ${side}wan 100.64.$id.2/24 ${side}lan $lan.1/24 cone
    X ${side}r ip route add default via 100.64.$id.1
  else
    veth ${side}r ${side}wan - ${side}wan-br
    nat_router ${side}r ${side}wan $pub/24 ${side}lan $lan.1/24 $kind
    X ${side}r ip route add default via 203.0.113.254
  fi
  up ${side}pc ${side}eth; X ${side}pc ip addr add $lan.$((60 + id))/24 dev ${side}eth; X ${side}pc ip route add default via $lan.1
}

home h 1 $HNAT 192.168.31 203.0.113.1
if [ "$LAN" = one ]; then
  # The joining player's machine sits on the host's LAN, behind the same router.
  ip netns add jpc
  X hr ip link add name hbr type bridge; X hr ip link set hbr up
  X hr ip addr del 192.168.31.1/24 dev hlan; X hr ip link set hlan master hbr; X hr ip addr add 192.168.31.1/24 dev hbr
  ip link add name jeth type veth peer name jlan2; ip link set jeth netns jpc; ip link set jlan2 netns hr
  X hr ip link set jlan2 master hbr; X hr ip link set jlan2 up
  up jpc jeth; X jpc ip addr add 192.168.31.62/24 dev jeth; X jpc ip route add default via 192.168.31.1
else
  home j 2 $JNAT 192.168.20 203.0.113.2
fi

# Relay node on the public network.
ip netns add node
veth node neth - neth-br
up node neth; X node ip addr add 203.0.113.20/24 dev neth; X node ip route add default via 203.0.113.254
# ET_DIR: the EasyTier folder to use; default is the newest version that `lan-setup` installed for this machine.
ET_DIR=${ET_DIR:-$(ls -d "$WORK"/Amber/easytier/*/linux-"$(uname -m)" 2>/dev/null | sort -V | tail -1)}
[ -x "$ET_DIR/easytier-core" ]
# Background jobs go through a subshell that execs, so $! is the real process and kill reaches it.
(exec nsenter --net=/run/netns/node "$ET_DIR/easytier-core" --no-tun --listeners tcp://0.0.0.0:11010 udp://0.0.0.0:11010 \
  --network-name jay-lab-node --network-secret jay-lab-node --ipv4 10.200.0.1) > "$OUT/node.log" 2>&1 & NODE=$!

python3 "$HERE/stun.py" 203.0.113.10:3478 203.0.113.11:3478 & STUN=$!
COMMON="--stun 203.0.113.10:3478 --stun 203.0.113.11:3478 --node tcp://203.0.113.20:11010"
for d in host join; do mkdir -p "$OUT/$d/.config"; cp -r "$WORK/Amber" "$OUT/$d/.config/"; cp -r "$WORK/Amber" "$OUT/$d/"; done
sleep 2
set +eE; trap - ERR

(cd "$OUT/host" && HOME="$OUT/host" XDG_CONFIG_HOME="$OUT/host/.config" exec nsenter --net=/run/netns/hpc "$CLI" lan-direct-host 25565 \
  $COMMON --sealed --reply-file "$OUT/reply.txt" --seconds $((IDLE + JOIN_SECONDS + 60))) > "$OUT/host.log" 2>&1 & HOST=$!
for i in $(seq 60); do grep -q '^SEALED ' "$OUT/host.log" && break; sleep 1; done
INVITE=$(grep -m1 '^SEALED ' "$OUT/host.log" | cut -d' ' -f2)
PASSPHRASE=$(grep -m1 '^PASSPHRASE ' "$OUT/host.log" | cut -d' ' -f2)
RESULT=fail; PATHLINE=-; MS=-
if [ -n "$INVITE" ]; then
  sleep "$IDLE"
  START=$(date +%s%3N)
  (cd "$OUT/join" && HOME="$OUT/join" XDG_CONFIG_HOME="$OUT/join/.config" exec nsenter --net=/run/netns/jpc "$CLI" lan-direct-join "$INVITE" \
    --passphrase "$PASSPHRASE" $COMMON --seconds $JOIN_SECONDS) > "$OUT/join.log" 2>&1 & JOIN=$!
  for i in $(seq 60); do grep -q '^REPLY ' "$OUT/join.log" && break; sleep 1; done
  # The host polls --reply-file: write it whole (temp file + rename), and only when there is a reply.
  REPLY_CODE=$(grep -m1 '^REPLY ' "$OUT/join.log" | cut -d' ' -f2)
  if [ -n "$REPLY_CODE" ]; then echo "$REPLY_CODE" > "$OUT/reply.tmp" && mv "$OUT/reply.tmp" "$OUT/reply.txt"; fi
  # Poll every 0.1 s so "join ms" is accurate to about 0.1 s.
  for i in $(seq $((JOIN_SECONDS * 10))); do grep -qE '^(DIRECT|RELAY)$' "$OUT/join.log" && break; kill -0 $JOIN 2>/dev/null || break; sleep 0.1; done
  if grep -qE '^(DIRECT|RELAY)$' "$OUT/join.log"; then
    MS=$(( $(date +%s%3N) - START ))
    RESULT=$(grep -m1 -E '^(DIRECT|RELAY)$' "$OUT/join.log")
    sleep 8
    PATHLINE=$(grep '^PATH ' "$OUT/join.log" | tail -1 | cut -d' ' -f2-)
  fi
  kill $JOIN 2>/dev/null
else
  PATHLINE="host made no offer"
fi
kill $HOST $STUN $NODE 2>/dev/null; wait 2>/dev/null
printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$S" "$HNAT" "$([ "$LAN" = one ] && echo same-LAN || echo "$JNAT")" "$RESULT" "${PATHLINE:--}" "$MS" >> "$WORK/matrix/results.tsv"
echo "$S: $RESULT ${PATHLINE:-} (${MS} ms)"
