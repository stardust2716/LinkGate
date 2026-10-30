# LinkGate 0.2.3 多端点 STUN

0.2.2 已能绕过 Windows TUN，但真实日志仍停在 FRP 的 `not enough addresses`：自建 STUN 只给一个 XOR-MAPPED-ADDRESS，没有 OTHER-ADDRESS。FRP 0.71.1 的 Prepare 至少需要两次映射观测。0.2.3 补齐这一步，保留固定版本的认证、响应来源/事务校验、NAT 分类、打洞和 STCP 回退。

## 实现

父程序只在自己持有的原生子进程环境中设置 `LINKGATE_MULTI_STUN=1`，不设置系统环境变量。未设置时，原生 FRP 使用原来的 Discover 行为。Windows 和 Android 使用同一实现；Windows 的物理 IPv4 套接字绑定继续生效。

1. 用同一个 UDP 套接字查询配对中的 `server.host:stunPort`。
2. 如果响应提供 OTHER/CHANGED-ADDRESS，向该实际地址发起另一笔 Binding 请求。
3. 有效观测不足两次时，依次尝试 `stun.cloudflare.com:3478`、FRP 默认的 `stun.easyvoip.com:3478`；收集两次即停止。
4. 按解析后的目标 IPv4 与端口去重。同一个目标不重复计数；两个不同目标返回相同映射属于两次真实观测，保留给原有 NAT 分类。超时、错误响应和不可用 OTHER 不清除已有有效观测。

每次 DNS 和 UDP 响应等待各有 3 秒上限；最多四个主/备用根端点，每个最多一个 OTHER 跟进，因此最多八个实际目标。本版仅收集 IPv4，拒绝 TUN 的 198.18.0.0/15 fake-IP 作为真实 STUN 目标。没有添加 TURN 服务或公共业务中继，也不修改 ECS 服务、端口及配对格式。公共 STUN 会看见发起方的公网地址；只交换地址发现报文，不接收应用业务正文。

公共端点依据：[Cloudflare STUN](https://developers.cloudflare.com/realtime/sfu/get-started/connection-patterns/)、[FRP 客户端默认配置](https://gofrp.org/zh-cn/docs/reference/client-configures/)。它们的可达性可能变化；两个都不可用时保留中继回退。

## 诊断

原生输出有界、无地址或凭据的 `LINKGATE_STUN` JSON，版本 `v=1`：

| 字段 | 含义 |
|---|---|
| `status` | `ready` 两次有效观测；`insufficient` 一次；`unreachable` 零次 |
| `observations` | 有效映射观测次数，0–2 |
| `attemptedEndpoints` | 实际发起 Binding 的不同目标数，0–8；DNS 失败不计入 |
| `fallbackUsed` | 是否进入公共备用端点尝试；不保证该次成功 |

两端快照对应 `stunStatus/stunObservations/stunAttemptedEndpoints/stunFallbackUsed`。`ready` 清除 STUN 准备错误；`insufficient` 显示 `stun_insufficient_observations`；`unreachable` 显示 `stun_unreachable`。每代显示最近一次发现结果，累计业务计数另行保留。

**两次观测、NAT 打洞事件、业务直连是三个不同阶段。** 必须看到 `directTx/Rx` 正增量才能证明本次业务使用直连。XTCP 后台准备完成前的连接仍可能回退，既有中继 TCP/WebSocket 不会自动迁移；需要建立新业务连接验证。

## 验证范围

原生测试覆盖同套接字、相同/变化映射的 NAT 分类、主端点失败后两个备用成功、重复目标拒绝、OTHER 成功时不访问备用、坏 OTHER 保留已有观测、fake-IP/非法端点拒绝。两端测试确保 `ready` 不会被当作打洞或业务证据，并拒绝未知/重复字段及不一致计数。

2026-10-28 实测：Windows TUN 保持开启，同电脑的发布端和访客端均获得两次真实观测，原生确认 `tcp/stun/hole` 物理接口绑定。1 MiB 文件逐字节验证和 WebSocket 回显通过；访客直连发送 309 B、接收 1,048,862 B，两端反向计数一致，中继业务字节为零。它证明同机两个独立进程的真实直接业务通道，不代表手机 Wi-Fi、移动网络或手机 Clash 场景已经通过。

另跑 62 秒持续测试：46 MiB 确定性文件、998 次 WebSocket 回显、5 个控制帧和正常关闭通过；访客直连发送 1,036,166 B、接收 49,266,749 B，中继业务字节为零，两端反向计数完全相等。该测试没有采集 ECS 流量，不据此填写手机四场景或服务器账单结果。脱敏机器记录见 [verification-v0.2.3.json](../tests/verification-v0.2.3.json)。

后续手机测试：更新两端后，手机停止旧网关再启动，先同 Wi-Fi、Clash 关闭，确认 STUN `ready/2`，运行 60 秒测试；再测试移动数据。电脑 TUN 保持开启。手机 Clash 开启时另作对照，仍需用户在 Clash 中排除 LinkGate；本版不绕过 Android VPN 的系统访问控制。历史四组全部中继的结果属于 0.2.1，继续保留。
