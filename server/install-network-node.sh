#!/usr/bin/env bash
# Jay 网络服务端（公共网络节点：种子 + 房间目录）一键安装，Linux + systemd。
# 用法（在解压好的 JayServer-linux-* 文件夹里）：
#   sudo bash install-network-node.sh                          # Jay 公共网络
#   sudo NETWORK=my.jaynet CONFIG=config.json bash install-network-node.sh   # 自己的公共网络
# 可选：PORT（默认 11010）、ACCOUNTS=1（开账号登录）、SEEDS=tcp://别的种子:11010、SEED_ONLY=1（只当种子，不跑目录）、
#       BLOCKED_WORDS=<文件>、EXTRA="其他 directory 参数"
set -euo pipefail

PORT="${PORT:-11010}"
HERE="$(cd "$(dirname "$0")" && pwd)"
BIN_DIR=/opt/jay-server
DATA=/var/lib/jay-network
UNIT=/etc/systemd/system/jay-network.service
# Its own user: a Minecraft server run by jay-server (user `jay`) on the same machine can't read the node's keys.
SVC_USER=jay-network

[[ $EUID -eq 0 ]] || { echo "要用 sudo 运行"; exit 1; }
[[ -x "$HERE/jay-server" ]] || { echo "没找到 $HERE/jay-server：在解压好的 JayServer 文件夹里运行这个脚本"; exit 1; }
command -v systemctl >/dev/null || { echo "这台机器没有 systemd；直接运行 ./jay-server directory 或者用 server/docker"; exit 1; }
[[ "$PORT" =~ ^[0-9]+$ ]] && (( PORT >= 1 && PORT <= 65535 )) || { echo "PORT 要是 1–65535"; exit 1; }

id "$SVC_USER" >/dev/null 2>&1 || useradd --system --home "$DATA" --shell /usr/sbin/nologin "$SVC_USER"
# Earlier versions ran the node as the shared `jay` user: stop it and hand its data over (links are not followed).
if [[ -d "$DATA" && "$(stat -c %U "$DATA")" != "$SVC_USER" ]]; then
  systemctl stop jay-network 2>/dev/null || true
  chown -R -h "$SVC_USER:$SVC_USER" "$DATA"
fi
install -d -m 0755 "$BIN_DIR"
install -d -m 0700 -o "$SVC_USER" -g "$SVC_USER" "$DATA"
install -m 0755 "$HERE/jay-server" "$BIN_DIR/jay-server"

ARGS=(directory --listen "$PORT")
copy() { install -m 0640 -o "$SVC_USER" -g "$SVC_USER" "$1" "$DATA/$2"; }
if [[ -n "${NETWORK:-}" ]]; then copy "$NETWORK" network.jaynet; ARGS+=(--network "$DATA/network.jaynet"); fi
if [[ -n "${CONFIG:-}" ]]; then copy "$CONFIG" config.json; ARGS+=(--config "$DATA/config.json"); fi
if [[ -n "${BLOCKED_WORDS:-}" ]]; then copy "$BLOCKED_WORDS" blocked-words.txt; ARGS+=(--blocked-words "$DATA/blocked-words.txt"); fi
[[ -n "${SEEDS:-}" ]] && ARGS+=(--seeds "$SEEDS")
[[ "${ACCOUNTS:-0}" == 1 ]] && ARGS+=(--accounts)
[[ "${SEED_ONLY:-0}" == 1 ]] && ARGS+=(--seed-only)
# shellcheck disable=SC2206
[[ -n "${EXTRA:-}" ]] && ARGS+=(${EXTRA})

# The node user may use the TPM (hardware binding, docs/security/node-hardware-binding.md) when the tss group exists.
TPM_GROUP=""
if getent group tss >/dev/null; then TPM_GROUP="SupplementaryGroups=tss"; fi

cat > "$UNIT" <<UNIT
# 由 install-network-node.sh 生成。改参数：改这里的 ExecStart，然后 systemctl daemon-reload && systemctl restart jay-network
[Unit]
Description=Jay network server (public network seed + room directory)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$SVC_USER
Group=$SVC_USER
Environment=JAY_SERVER_DIR=$DATA
ExecStart=$BIN_DIR/jay-server ${ARGS[*]}
Restart=on-failure
RestartSec=10
# 77 = hardware check failed and the node wiped its files; 78 = refused to run (tampered runtime). Don't restart into either.
RestartPreventExitStatus=77 78
${TPM_GROUP}
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=$DATA
# jay-server's own data (a Minecraft server on the same machine) is none of the node's business.
InaccessiblePaths=-/var/lib/jay-server
PrivateTmp=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectKernelLogs=true
ProtectControlGroups=true
RestrictNamespaces=true
RestrictSUIDSGID=true
LockPersonality=true
CapabilityBoundingSet=
# Not PrivateDevices (hardware binding uses /dev/tpmrm0) and not MemoryDenyWriteExecute (.NET compiles code at run time).

[Install]
WantedBy=multi-user.target
UNIT

if command -v ufw >/dev/null && ufw status | grep -q "Status: active"; then
  ufw allow "$PORT"/tcp && ufw allow "$PORT"/udp
elif command -v firewall-cmd >/dev/null && firewall-cmd --state >/dev/null 2>&1; then
  firewall-cmd --permanent --add-port="$PORT"/tcp && firewall-cmd --permanent --add-port="$PORT"/udp && firewall-cmd --reload
fi

systemctl daemon-reload
systemctl enable --now jay-network
echo
echo "装好了。看日志：journalctl -u jay-network -f"
echo "看状态：sudo -u $SVC_USER JAY_SERVER_DIR=$DATA $BIN_DIR/jay-server node status"
echo "云服务器还要在安全组里放行 TCP 和 UDP $PORT。玩家的 Jay 里公共节点填 tcp://<这台机器的公网地址>:$PORT"
[[ "${ACCOUNTS:-0}" == 1 ]] && echo "加账号：sudo -u $SVC_USER JAY_SERVER_DIR=$DATA $BIN_DIR/jay-server account add <账号>；login 段在 journalctl 里"
exit 0
