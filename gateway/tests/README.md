# LinkGate 验证

0.2.2 的 Windows TUN 绕行已完成 71 项 Windows 检查、19 项 Android JVM 测试、Go 两项计数及两项真实 UDP 接口绑定测试，并通过自有 ECS 的 TCP/STUN 绑定、1 MiB 文件及 WebSocket 验证。独立目标默认走 Meta，绑定后连接成功并使用物理接口，系统路由保持原样；当前 P2P 仍受 STUN 观测不足阻碍。[实现与限制](../windows/TUN-BYPASS.md)、[验证记录](verification-v0.2.2.json)。

**当前结果（0.2.1，2026-10-28）：四组手机网络验收已完成。** 79 MiB 文件、1,375 次 WebSocket 回显通过，两端字节一致，ECS 完整覆盖；四组全部走中继，直连 0 B。详细条件、原始报告和限制见 [网络测试结果](NETWORK-RESULTS-2026-10-28.md)。Android 的 18 项 JVM 测试及修复后的 62 秒 WebSocket 回归通过。下文保留 0.2 初次验证的历史记录。

## 不需要 FRP 运行时的保护检查

```powershell
python -m unittest discover -s gateway/tests -p 'test_*.py' -v
```

`test_server_guards.py` 的 6 项测试已执行通过：精确服务器配置、拒绝关闭 TLS／增加未知公网服务、坏 token／缺失字段／类型边界、精确非特权 systemd unit、Go 的 IPv4／双栈 IPv6 通配监听 inode 识别，以及未完成安装的只读／缺失文件恢复保护。测试不使用管理员权限、云服务器或其他项目的运行进程。

## 真实 FRP 套接字回归

```powershell
python gateway/tests/frp_socket_integration.py `
  --frp-dir downloads/linkgate/frp_0.71.1_windows_amd64 `
  --frpc gateway/windows/runtime/frpc.exe --require-metrics `
  --output artifacts/linkgate/v02-socket-regression.json
```

要求锁定的官方 `frps.exe`、0.2 计数版 `frpc.exe` **0.71.1** 和 OpenSSL。脚本使用临时目录、随机凭据、本机 STUN/HTTP/WebSocket 测试服务及自有子进程，不连接正式服务器、不安装后台任务、不操作手机。

验证范围：

- 强制 TLS 与客户端 CA 校验；错误注册 token、CA、服务密钥被拒绝。
- STCP 真实 HTTP 请求、流式内容完整性及 WebSocket 握手／双向帧。
- 不存在的 XTCP 目标触发配置的 STCP 回退，回退路径仍可传输 HTTP／WebSocket。
- 本机 STUN 与 XTCP 打洞日志观察。仅记录明确观察到的本机事件；不把进程运行、候选成功或回退 HTTP 成功记作移动网络直连。

**2026-10-28：0.2 真实套接字回归已通过。** 官方 frps 与固定源码计数版 frpc 的 HTTP、文件流、WebSocket、XTCP 不可用回退、错误 token/CA/服务密钥拒绝均通过。五条中继连接的访客累计发送 717 B、接收 74,664 B，与发布端反向字节完全一致；两条回退连接保留直连等待耗时及枚举原因。Windows 59 项检查、Android 15 项 JVM 测试、Go 2 项计数测试通过。安装包使用原 Android 签名，手机只通过 ADB 覆盖安装，没有操作画面。此前 0.1 的安全软件阻止已经按用户确认处理，不能用旧的失败记录覆盖这些新结果。

同日初次在 ECS 验证专用 TCP17400 的只读计数脚本，5 秒空闲采样零业务字节、零内核丢包。当时四组手机测试按用户要求延期；后续四组已全部完成，实际路径均为中继，详见 [网络矩阵](NETWORK-MATRIX.md)。本机回归不用于证明移动网络直连。

随后电脑经自有 ECS 的 0.2 冒烟也通过：1 MiB 文件完整性、WebSocket；实际中继，访客发送 434 B/接收 1,049,020 B，与发布端反向字节一致。ECS 五分钟专用端口窗口入站 1,090,946 B、出站 1,353,732 B，零内核丢包和解析遗漏；包含控制流量和网络开销，不能与较短业务窗口强行作相等判断。脱敏结果存于 [verification-v0.2.json](verification-v0.2.json)。

同路由器 Wi-Fi、移动数据及手机 Clash 开关已独立验收。手机后台恢复、长期带宽、电脑 TUN 关闭对照仍未测试，不能由本机回归或本轮短时传输代替。
