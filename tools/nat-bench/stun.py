#!/usr/bin/env python3
"""最小的 STUN 服务器（RFC 5389 Binding），只给 NAT 模拟测试用：回 XOR-MAPPED-ADDRESS。"""
import socket, struct, sys, selectors

COOKIE = 0x2112A442
sel = selectors.DefaultSelector()
for addr in sys.argv[1:]:
    host, port = addr.rsplit(":", 1)
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.bind((host, int(port)))
    sel.register(s, selectors.EVENT_READ)

while True:
    for key, _ in sel.select():
        s = key.fileobj
        data, (ip, port) = s.recvfrom(2048)
        if len(data) < 20 or struct.unpack("!H", data[:2])[0] != 0x0001:
            continue
        txid = data[8:20]
        xport = port ^ (COOKIE >> 16)
        xip = struct.unpack("!I", socket.inet_aton(ip))[0] ^ COOKIE
        attr = struct.pack("!HHBBHI", 0x0020, 8, 0, 1, xport, xip)
        s.sendto(struct.pack("!HHI", 0x0101, len(attr), COOKIE) + txid + attr, (ip, port))
