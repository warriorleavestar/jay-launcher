# Mac/Linux 完整局域网模式：真机测试步骤

完整局域网模式和“经别人上网”现在 Mac 和 Linux 上也能用了（设计见 `platform-porting-guide.md` 的“完整局域网模式”一节）。Linux 已在云端用两个网络命名空间测过（普通用户经 pkexec 提权、建网卡、虚拟 IP 互通、广播转发、换参数重启、关房间清理、Jay 被 `kill -9` 后清理、取消密码框）。下面是还需要在真机上确认的部分，**macOS 的部分只有真机能测**。

测试时用单独的测试配置和测试游戏目录（`AMBER_DATA_DIR` 指向测试目录），不要用自己平时的 `.minecraft`。

## 两台电脑都要做

1. 两边都打开 Jay → 联机 → 高级选项，勾“范围：整个局域网”。Linux 上没有 pkexec 时勾选框是灰的，下面写着原因。
2. 一边创建房间，另一边加入。创建 / 加入时系统会弹管理员密码框：
   - macOS：系统的“Jay 要建一块虚拟网卡……需要管理员密码”对话框。
   - Linux：polkit 的密码框，写的是要以超级用户运行 Jay。
   点“取消”应该提示“你取消了……没有开启完整局域网模式”，Jay 不卡住、不崩。
3. 输密码后，房间页显示本机虚拟 IP（房主是 10.233.144.1）。终端里确认：
   - macOS：`ifconfig | grep -B3 10.233.144`，应该有一块 `utunN` 带这个地址。
   - Linux：`ip addr show amberlan`。
4. 互相能通：`ping 10.233.144.1`（加入方 ping 房主），MC 用 `10.233.144.1:端口` 直连房主的局域网世界。
5. 广播转发：找一个靠 UDP 广播找房间的局域网游戏试；没有的话用下面的脚本。加入方开着接收，房主发：

   ```bash
   # 接收（另一台电脑）
   python3 -c "import socket;s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);s.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1);s.bind(('',4445));print(s.recvfrom(100))"
   # 发送（本机，走物理网卡的普通广播）
   python3 -c "import socket;s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);s.setsockopt(socket.SOL_SOCKET,socket.SO_BROADCAST,1);s.sendto(b'hi',('255.255.255.255',4445))"
   ```

   接收方应该收到来源是对方虚拟 IP（10.233.144.x）的包。两台电脑在同一个真实局域网时，还会另外收到一份来自真实 IP 的，这是正常的。
6. 关房间：虚拟网卡消失（`ifconfig` / `ip addr` 里没有了），`ps aux | grep -E "easytier-core|amber-et-guard"` 没有残留。
7. 强制结束 Jay（macOS 用“活动监视器 → 强制退出”，Linux 用 `kill -9`），几秒内守护和 easytier-core 都应该退出，虚拟网卡消失。
8. 房间开着时换中转节点或换直连路径（会重启 easytier-core），不应该再弹密码框。

## macOS 要特别留意的地方

这些在云端测不了，请看到问题时把 Jay 的联机诊断和终端输出发回来：

- **密码框**：用的是 `osascript … with administrator privileges`，守护在后台运行。第一次可能还会弹“Jay 想要控制……”之类的自动化权限提示，记一下提示原文。
- **utun 地址**：EasyTier 在 macOS 上自己配 utun 的地址和 10.233.144.0/24 路由；`netstat -rn | grep 10.233.144` 应该指向 utun。
- **广播抓包**：用系统自带的 `/usr/lib/libpcap.dylib`，每块有 IPv4 地址的物理网卡（Wi-Fi 一般是 `en0`）一个抓包线程。抓不到时诊断里会有 `broadcast relay (en0): …` 一行。
- **从虚拟网卡发包**：原始 UDP 套接字加 `IP_BOUND_IF` 绑定到 utun。如果第 5 步收不到来自虚拟 IP 的包，但第 4 步能通，多半是这一步，发回诊断。
- **防火墙**：如果开了 macOS 防火墙的“阻止所有传入连接”，虚拟网卡上的连接也会被挡。

## 经别人上网（出口）

Linux 已在云端测过：开出口后能经出口成员上网，出口成员掉线时本机不会退回直接上网，结束后路由、防漏规则和 DNS 都恢复原样。真机上请再确认：

1. 房主勾“让房间里的人经我上网（全部流量）”，加入方在房间页选房主当出口，点“开始”。
2. 加入方打开 `https://ifconfig.me`（或 `curl ifconfig.me`），显示的应该是房主的公网 IP。
3. 查规则和 DNS：
   - macOS：`sudo pfctl -a com.apple/250.JayExit -s rules` 能看到 Jay 的规则；`scutil --dns | head -20` 第一个 resolver 是 1.1.1.1 / 8.8.8.8。
   - Linux：`sudo nft list table inet jay_exit`；`resolvectl status` 里虚拟网卡（amberlan-端口号）的 DNS 是 1.1.1.1 / 8.8.8.8。
4. 断开测试：房主直接关 Jay。加入方此时打不开任何网页（不会漏到自己的网络），房间页提示连接断了。
5. 加入方停止出口或关房间：上网恢复正常，`pfctl -a com.apple/250.JayExit -s rules` / `nft list table inet jay_exit` 没有了，`scutil --dns` / `resolvectl status` 恢复原来的 DNS。
6. macOS 额外留意：系统设置里如果本来就开着别的 VPN 或者第三方防火墙，记一下有没有冲突。
