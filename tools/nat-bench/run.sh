#!/bin/bash
# 两地联机模拟：一台机器上用网络命名空间搭两个家庭网络 + 一段“公网”，不需要 root（用户命名空间）。
#
#   房主局域网 192.168.31.0/24 ── 房主路由器（NAT，外部端口不保留，空闲 UDP 映射 30 秒回收）──┐
#                                                                                       公网 203.0.113.0/24（两台 STUN）
#   加入方局域网 192.168.20.0/24 ── 加入方路由器（NAT，保留端口，端口受限）────────────────┘
#
# 流程：房主建房间 → 等 HOST_IDLE 秒（默认 120，要比映射回收时间 NAT_TIMEOUT 长）→ 加入方拿邀请码经中转敲门 → 批准后双方在加密的敲门里交换地址再打洞。
# 邀请码里没有地址，第一次联系要经中转：NODE 填一个两边都连得到的中转（在“公网”段上，比如 tcp://203.0.113.20:11010）。
# 用法：run.sh <Amber.Cli 路径> <工作目录> [HOST_IDLE]
# 环境变量：NODE 中转地址（必填）；NAT_TIMEOUT 房主路由器空闲 UDP 映射回收秒数（默认 30）；JOIN_SECONDS 加入方最多跑多久（默认 120）。
# 需要 Linux（用户命名空间 + nft），不需要 root。2026-11-09 在这里复现了两地实测的失败：没有保活时打不通，有保活时 100 ms 内打通。
# 工作目录里要有 Amber/easytier/<版本>/linux-x86_64/（先在别处跑一次 lan-setup 拷过来）。
# 只在工作目录里读写，不碰真实的 .minecraft。
set -u
: "${NODE:?NODE 要填一个两边都连得到的中转地址}"
CLI=$(realpath "$1"); WORK=$(realpath "$2"); IDLE=${3:-120}
HERE=$(dirname "$(realpath "$0")")
if [ "${INSIDE_NS:-}" != 1 ]; then
  exec env INSIDE_NS=1 unshare -rn --mount --propagation private "$0" "$@"
fi
mount -t tmpfs none /run && mkdir -p /run/netns
mount -t tmpfs none /sys 2>/dev/null; mount -t sysfs sysfs /sys 2>/dev/null || true
X() { local ns=$1; shift; nsenter --net=/run/netns/$ns "$@"; }
for ns in hostlan hostr joinlan joinr; do ip netns add $ns; done
ip link set lo up
# 公网：一个网桥
ip link add br0 type bridge && ip link set br0 up
ip addr add 203.0.113.254/24 dev br0
ip addr add 203.0.113.10/24 dev br0
ip addr add 203.0.113.11/24 dev br0
wire() { # wire <ns-a> <if-a> <ns-b|-> <if-b>
  ip link add $2 type veth peer name $4
  ip link set $2 netns $1
  if [ "$3" = - ]; then ip link set $4 master br0 up; else ip link set $4 netns $3; fi
}
wire hostr hwan - hwan-br; wire hostr hlan hostlan heth
wire joinr jwan - jwan-br; wire joinr jlan joinlan jeth
setup_router() { # ns wan wanip lan lanip
  X $1 ip link set lo up; X $1 ip link set $2 up; X $1 ip link set $4 up
  X $1 ip addr add $3/24 dev $2; X $1 ip addr add $5/24 dev $4
  X $1 sysctl -qw net.ipv4.ip_forward=1
}
setup_router hostr hwan 203.0.113.1 hlan 192.168.31.1
setup_router joinr jwan 203.0.113.2 jlan 192.168.20.1
X hostlan ip link set lo up; X hostlan ip link set heth up; X hostlan ip addr add 192.168.31.65/24 dev heth; X hostlan ip route add default via 192.168.31.1
X joinlan ip link set lo up; X joinlan ip link set jeth up; X joinlan ip addr add 192.168.20.114/24 dev jeth; X joinlan ip route add default via 192.168.20.1
# 房主路由器：外部端口换到 20000-29999（不保留端口，像国内运营商 CGNAT）；映射对所有目的地址共用（锥形），
# 但所有会话都空闲 30 秒后回收，回收后再发包会换一个新端口。
X hostr nft add table ip nat
X hostr nft 'add chain ip nat post { type nat hook postrouting priority 100 ; }'
X hostr nft add rule ip nat post oifname hwan meta l4proto udp masquerade to :20000-29999
X hostr nft add rule ip nat post oifname hwan masquerade
X hostr sysctl -qw net.netfilter.nf_conntrack_udp_timeout=${NAT_TIMEOUT:-30} net.netfilter.nf_conntrack_udp_timeout_stream=${NAT_TIMEOUT:-30}
# 加入方路由器：普通 masquerade（保留端口），只收发过包的那个对端回来的包（端口受限）。
X joinr nft add table ip nat
X joinr nft 'add chain ip nat post { type nat hook postrouting priority 100 ; }'
X joinr nft add rule ip nat post oifname jwan masquerade
# 路由器防火墙：公网来的新连接一律丢掉（家用路由器默认就是这样；不丢的话 Linux 会为这些包建会话，占住映射端口）。
for r in hostr:hwan joinr:jwan; do
  X ${r%%:*} nft add table ip filter
  X ${r%%:*} nft "add chain ip filter input { type filter hook input priority 0 ; }"
  X ${r%%:*} nft add rule ip filter input iifname ${r##*:} ct state new drop
  X ${r%%:*} nft "add chain ip filter wanfw { type filter hook forward priority 0 ; }"
  X ${r%%:*} nft add rule ip filter wanfw iifname ${r##*:} ct state new drop
done
python3 "$HERE/stun.py" 203.0.113.10:3478 203.0.113.11:3478 & STUN=$!
STUNARGS="--stun 203.0.113.10:3478 --stun 203.0.113.11:3478"
mkdir -p "$WORK/host" "$WORK/join"
for d in host join; do mkdir -p "$WORK/$d/.config"; cp -r "$WORK/Amber" "$WORK/$d/.config/" 2>/dev/null; cp -r "$WORK/Amber" "$WORK/$d/" 2>/dev/null; done
cd "$WORK/host" && HOME="$WORK/host" XDG_CONFIG_HOME="$WORK/host/.config" X hostlan "$CLI" lan-direct-host 25565 $STUNARGS --node "$NODE" --seconds $((IDLE+150)) > "$WORK/host.log" 2>&1 & HOST=$!
for i in $(seq 60); do grep -q '^INVITE ' "$WORK/host.log" && break; sleep 1; done
INVITE=$(grep -m1 '^INVITE ' "$WORK/host.log" | cut -d' ' -f2)
[ -z "$INVITE" ] && { echo "房主没生成邀请码"; cat "$WORK/host.log"; kill $STUN $HOST; exit 2; }
echo "[$(date +%T)] 房间建好，等 $IDLE 秒再让加入方加入"
sleep "$IDLE"
cd "$WORK/join" && HOME="$WORK/join" XDG_CONFIG_HOME="$WORK/join/.config" X joinlan "$CLI" lan-direct-join "$INVITE" $STUNARGS --node "$NODE" --seconds ${JOIN_SECONDS:-120} > "$WORK/join.log" 2>&1 & JOIN=$!
wait $JOIN
kill $HOST $STUN 2>/dev/null; wait 2>/dev/null
if grep -qE "打通：|Connected: " "$WORK/join.log" && ! grep -qE "直连失败|Direct connection failed" "$WORK/join.log"; then RESULT=打通; else RESULT=没打通; fi
echo "== 结果：$RESULT =="
grep -E "STUN|打洞|打通|直连失败|映射|LOCAL|PATH|[Pp]unch|Connected|mapping|failed" "$WORK/host.log" | sed 's/^/房主 /' | awk '!s[$0]++' | head -30
grep -E "STUN|打洞|打通|直连失败|LOCAL|PATH|[Pp]unch|Connected|failed" "$WORK/join.log" | sed 's/^/加入方 /' | awk '!s[$0]++' | head -30
