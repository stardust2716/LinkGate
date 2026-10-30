# FRP 0.71.1 的 LinkGate 0.2 诊断补丁

基于官方 `fatedier/frp` 的 `v0.71.1`，固定提交 `d8cf1800dc1abd7fda1828cab105d7a6b5a50eb1`。在 `libio.Join` 应用侧增加读写计数；0.2.2 另增加 Windows 物理网卡出站绑定，并限制辅助候选为已绑定地址；0.2.3 增加多端点 STUN 发现。认证、加密、NAT 分类与打洞算法、STCP 回退保留，具体范围见 [TUN 绕行](../windows/TUN-BYPASS.md) 和 [STUN 发现](STUN-DISCOVERY.md)。原 FRP Apache-2.0 许可证继续适用，发行物须携带其 LICENSE。

## 构建

准备 Go 1.26 以上、Android NDK 29.0.13113456、Git。先校验 Go 下载的官方 SHA-256。使用干净源码目录；脚本拒绝覆盖已有源码修改。

```powershell
git clone --depth 1 --branch v0.71.1 https://github.com/fatedier/frp.git upstream/frp-linkgate
./gateway/frp-diagnostics/build.ps1 -Source E:\path\frp-linkgate -Go C:\go\bin\go.exe -Prepare -AndroidClang C:\Android\Sdk\ndk\29.0.13113456\toolchains\llvm\prebuilt\windows-x86_64\bin\aarch64-linux-android26-clang.cmd
./gateway/build.ps1 -SkipRuntimeDownload
```

脚本先运行 Go 计数、Windows 网卡绑定和 NAT/STUN 发现测试，再编译 Windows amd64 / Android ARM64 客户端，以 `-trimpath -buildvcs=false` 去掉本机路径，生成 `assets/runtime.lock.json`。构建应用时将锁嵌入 EXE/APK；每次启动前校验运行时 SHA-256。重建不同 Go 版本可能改变哈希，必须将新锁和对应应用一起发布。服务器仍使用官方 frps，无需升级。

不能对 0.2 客户端运行旧的 `prepare-runtime.py` 默认全平台命令覆盖计数运行时；仅准备服务器时用 `--platform linux_amd64`。安全软件若拦截新编译文件，应核对构建来源和具体告警，不能关闭保护或自动扩大排除范围。

## 证据口径

- `NAT_HOLE_OBSERVED`：原 INFO 日志，发生在 QUIC 会话初始化之前，单独记录历史事实。
- `LINKGATE_METRIC`：仅固定前缀加 JSON；`v=1`、进程内 `id`、`role`、`path`、`phase`、累计 `tx/rx`、`setupMs/elapsedMs` 和枚举 `reason`。不含地址、设备命名空间、令牌、密钥、业务正文。
- `path=direct`：XTCP 已取得数据流并开始转发；`path=relay`：STCP 业务流已建立；`open` 但零字节仍不是业务直连证据。`businessDirectObserved` 必须出现正字节数。
- 每秒采样及连接结束时发累计值。客户端按 ID 做差，拒绝倒退、重复结束、路径变化、未知字段和非法数据。允许多条连接同时使用直连和中继，既有 TCP 连接不会自动迁移。
- `tx` 是从本端应用读取的字节；`rx` 是写入本端应用的字节。因此访客 tx 对应发布端 rx，反之亦然。含 HTTP/WebSocket 的应用协议头，**不含** FRP/TLS/QUIC、STUN、ACK 和重传开销。
- 访客建立时间从本地接入到业务流就绪，包含直连超时和 STCP 回退。发布端建立时间仅从接到工作连接到本机服务就绪，两者不作相同端到端指标比较。
- `direct_tunnel_unavailable` 表示本次直接数据流在等待期限内未就绪，**不能据此断言是对称 NAT、VPN 或运营商封锁**。`direct_unavailable_relay_failed` 表示回退也未成功。`connection_setup_failed` 是未能建立业务流。
- 每次启动清零；每代最多保留 8192 条连接，超出标记 `complete=false`。异常结束/强行停止时若还有活动连接，最后不足一秒的字节可能未上报，标记不完整，不补造数字。
- 吞吐为最近约 3 秒采样窗口的业务总字节每秒，非链路容量。无活跃连接显示 `idle`，历史直连证据和累计字节仍保留。

0.2.3 另增加 [多端点 STUN 发现](STUN-DISCOVERY.md) 及独立 `LINKGATE_STUN` 准备状态，修复单映射 STUN 与 FRP 的兼容性。NAT 分类和后续打洞算法仍来自固定 FRP 源码，不承诺所有 NAT 都可穿透。
