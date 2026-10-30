# LinkGate Android 0.2.3

0.2.3 接入 [多端点 STUN 发现](../frp-diagnostics/STUN-DISCOVERY.md)：优先自建端点，观测不足时使用公共备用，界面显示有效观测和尝试端点数。Windows 的物理网卡绑定不应用于手机；Android 网络选择和既有 Clash 应用排除设置保留。四组手机实测报告属于 0.2.1，0.2.3 的手机直连仍待实测。

独立用户态 TCP 访客网关，包名 `com.nebula.linkgate`。支持 Android 8.0 及以上 ARM64，使用与电脑配对的 FRP XTCP；打洞失败可回退至同服务的 STCP。应用没有 `VpnService`，不占用系统 VPN 槽位，不修改 ReDesk 或 Clash 设置。

## 构建

需要 JDK 17、Gradle 9.3.1、Android SDK 36、AGP 9.1.1。项目只使用系统 Java / Android API，JVM 测试依赖 JUnit 4.13.2 和 org.json 20240303。

1. 按 [计数运行时构建](../frp-diagnostics/README.md) 从固定 FRP v0.71.1 源码构建 ARM64 Android 可执行文件 `app/src/main/jniLibs/arm64-v8a/libfrpc.so`；`.so` 名称用于 Android 原生库提取和执行目录，不能以普通 Linux ELF 替代。SHA-256 锁打入 APK，启动前强制核验。
2. 使用 Android NDK 的 `aarch64-linux-android26-clang`，以 `-fPIE -pie` 编译 `app/src/main/cpp/launcher.c` 为 `app/src/main/jniLibs/arm64-v8a/liblinkgate-launcher.so`。正式构建脚本负责此步及两项 ELF 校验。启动器设置父进程死亡 `SIGTERM`，核对父 PID 以关闭竞态，然后直接 `execv` 原运行时；没有 shell 或额外代理进程。
3. 通过 `ANDROID_HOME` / `ANDROID_SDK_ROOT` 指定 SDK，或在不提交的 `local.properties` 内设置 `sdk.dir`。
4. 在此目录运行 `gradle :app:testDebugUnitTest :app:assembleRelease`。发行 APK 默认未签名，必须由仓库打包脚本以持久签名签署并验证，不能把临时 debug 签名当作正式升级通道。

运行时目录和构建产物被 Git 忽略，许可证与下载哈希由仓库运行时锁和正式发行包提供。构建不会下载手机配对凭据，也不会自行安装、启动应用或操作手机画面。

## 使用

1. 从电脑生成格式版本 `1` 的私有 JSON 配对文件，以安全通道传给手机。文件含连接凭据，不应提交 GitHub 或放入公开下载页。
2. 用户在 LinkGate 点击“导入配对文件”，由 Android 文档选择器选中 JSON。导入限制 128 KiB，严格验证字段、端口、服务唯一性、凭据格式与 CA 证书；失败保留原配置。
3. 手机常开 Clash 时，先在 Clash 停止状态下将 **LinkGate** 加入既有应用排除列表，保留其他已选应用，再启动 Clash。LinkGate 不尝试绕过 VPN 的系统限制，也不会自行改写第三方配置。
4. 点击“启动网关”。通知显示运行状态和停止按钮。把相应业务应用的目标设置为服务列表中的 `127.0.0.1:访客端口`。HTTP、HTTPS、WebSocket 作为 TCP 流通过网关；协议与 TLS/Host/Origin 仍由业务应用按原服务配置。
5. 完成后在应用或通知中停止。关闭页面保留前台服务；后台被系统结束后，本版需要用户手动重新启动，不注册开机任务或自动重放业务请求。

`server.stunPort` 是首选独立 STUN 的 UDP 端口，缺省 3478，与 frps TCP 端口无关；使用同一 `server.host`。本仓库自有部署可指定已验证的 UDP STUN 端口。0.2.3 会在不可达或观测不足时尝试公共备用，至少需要两个有效的不同目标观测。STUN 只能发现地址，不能保证直连，不能把 frps 的 TCP 监听当作 STUN。

## 隔离与状态

- 只监听访客服务的 IPv4 回环地址，不暴露手机局域网端口。配置、CA 和派生 TOML 存放应用私有目录，不提供导出的配置广播、绑定服务或诊断 HTTP 管理口。
- 启动时通过父进程保护启动器执行 `frpc verify -c`，再托管独立 frpc 子进程。verify 和运行进程都有 finally 清理；停止仅使用持有的 `Process`，不按进程名、端口或未知 PID 杀进程。父进程异常退出时由内核发送 `SIGTERM`，手机实机强杀／异常退出场景仍需验收，不把源码保护冒充已完成的手机测试。
- 原始运行时日志只在有界缓冲内分类，不写 logcat、磁盘或 UI。原 NAT INFO 只记录历史事实；实际路径与字节从固定计数补丁获取，拒绝把打洞、服务名称、转交尝试当业务证据。界面可复制脱敏诊断 JSON，手动测试结束后仅将脱敏汇总写入 `LinkGateTest` logcat，便于在不操作手机画面的前提下读取结果。详见 [测试矩阵](../tests/NETWORK-MATRIX.md)。
- XTCP 回退发生在建立连接阶段。已有业务 TCP / WebSocket 连接不保证从中继无缝迁移到直连。STCP 仍消耗中继服务器带宽。
- 单设备可信配对的首版，不提供多租户、任意目标代理、原生 UDP 业务转发、出口节点或完整系统组网。导入文件可以指定服务，来源必须可信。

## 验证边界

JVM 测试覆盖配对 round-trip、确定的 XTCP/STCP 配置、回环绑定、STUN 端口、字段白名单、类型、证书、注入、重复服务、端口边界与大小限制，以及真实日志分类、不将打洞／回退尝试误报成业务路径。独立 StrictJson 在 Android 的 org.json 前显式拒绝重复键（包含 Unicode 转义等价键）、注释、单引号、尾逗号、无效数字和过深嵌套，避免 Android/JVM 库行为差异。`localPort` 必填，`tlsServerName` 只接受 DNS 名称，不接受 IP；服务器地址允许规范四段 IPv4 或 ASCII DNS，拒绝零地址、多播、广播及数字缩写。构建和这些测试不能替代 Android 系统后台策略、真实 NAT、Clash 共存、手机端到端业务和耗电验收。未经用户允许不操作手机界面。
