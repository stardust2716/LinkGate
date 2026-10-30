# LinkGate 0.2.2：Windows TUN 绕行

Windows 客户端检测到活动 Clash/Meta/mihomo TUN 后，选择具有默认网关和唯一可用 IPv4 地址的硬件网卡，把 LinkGate 的出站套接字绑定到该网卡。无需管理员权限，不修改 Clash 配置、默认路由、防火墙或其他应用的代理设置。停止程序后套接字随自有进程回收，没有需要删除的系统路由。

## 实现和证据

`PhysicalEgress.cs` 用只读网络查询选择网卡；硬件网卡有歧义或无可用地址时返回明确错误，不随机选取。父进程只给自己持有且通过 SHA-256 核验的 frpc 设置接口索引、IPv4、MAC 三个环境变量；不写入配对文件，也不设置全局变量。

固定 FRP 源码中的 `pkg/linkgateegress` 在每次新建套接字前核验网卡索引、MAC、活动状态和源地址。Windows 使用 [IP_UNICAST_IF](https://learn.microsoft.com/en-us/windows/win32/winsock/ipproto-ip-socket-options) 指定出站接口并绑定物理 IPv4；设置值使用网络字节序，读取值使用主机字节序，成功后再核对接口索引。

覆盖固定 TCP/TLS 控制连接、STUN UDP、打洞主 UDP、随机探测 UDP 和 KCP 重建 UDP；QUIC 直接会话复用已绑定的 UDP。仅把选定物理地址作为局域网辅助候选。鉴权、证书校验及既有 STCP 回退保留。Android 不应用 Windows 网卡绑定。

本版对 frps 的控制连接限定为 LinkGate 的 TCP/TLS 配置；物理绕行启用后，原生 FRP 的 KCP/QUIC/WebSocket 控制连接或显式代理配置会被拒绝，不会悄悄走未绑定路径。本版只实现 IPv4 绕行。多块可用物理网卡或物理地址变化时需检查网络并重新启动网关。

`egressMode=physical-ipv4` 表示已配置物理出站；`verifiedBoundSocketKinds` 只记录原生代码实际创建、检查接口选项成功的套接字类型。它们与 `businessDirectObserved` 分开：绑到物理网卡不等于已经 P2P 直连。

## 2026-10-28 验证

- Windows 71 项检查通过：歧义/非法网卡拒绝、原生确认与配置匹配、STUN 错误与业务证据分离、过期准备错误清除。
- Go Windows 两项绑定测试通过，实际读取 UDP 源地址和接口选项，拒绝网卡身份变化；另有两项计数测试通过。
- Android 19 项 JVM 测试、构建及签名验证通过，已覆盖安装 0.2.2（code 4），未操作手机画面。
- 干净官方源码重新应用补丁及精确验证通过；真实套接字 HTTP/文件/WebSocket/回退/错误凭据拒绝回归通过。
- TUN 保持开启，Windows 发布端与访客端的控制 TCP 都确认绑定物理网卡，访客 STUN UDP 也确认绑定。自有 ECS 的 1 MiB 文件完整性和 WebSocket 通过，两端字节反向一致。
- 独立目标 `223.5.5.5:443` 的系统选路仍是 Meta：未绑定连接使用 TUN 源地址，绑定后连接成功且使用物理源地址、接口选项为物理索引。该目标不依赖既有 ECS 专用路由，验证了进程套接字可以覆盖默认 TUN 选路。
- 访客因 STUN 准备失败，尚未触发发布端或真实对端打洞 UDP；这些调用点已接入绑定函数，不把代码覆盖描述为真实 P2P 成功。

物理出口和代理出口的可达性可能不同：同轮 `1.1.1.1:443` 探测中未绑定连接成功，物理连接超时。单次超时不能定位具体阻断原因；绕行不承诺物理网络具备代理出口的全部可达性。

## 0.2.2 发现的独立阻碍与 0.2.3 修正

当前自建 STUN 黑盒探测只返回一个 XOR-MAPPED-ADDRESS，没有 OTHER-ADDRESS。FRP 0.71.1 的 [Prepare](https://github.com/fatedier/frp/blob/v0.71.1/pkg/nathole/nathole.go) 要求至少两个映射观测，而 [Discover](https://github.com/fatedier/frp/blob/v0.71.1/pkg/nathole/discovery.go) 会通过 OTHER-ADDRESS 收集第二个地址。物理绑定模式下，真实访客日志仍报 `not enough addresses`。

0.2.2 单独显示 `natPreparationFailure=stun_insufficient_observations`，该版未证明 P2P 直连。0.2.3 已加入 [真实多端点观测](../frp-diagnostics/STUN-DISCOVERY.md)，保留原配对、TLS 和回退。新实测中两个同机 Windows 客户端在 TUN 开启时完成文件/WebSocket 直连，原生确认 TCP、STUN、打洞 UDP 均物理绑定；两端直连字节一致，中继字节为零。手机和移动网络结果仍需单独验收，不用同机结果代替。
