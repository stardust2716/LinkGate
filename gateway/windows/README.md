# LinkGate Windows 0.2

0.2.3 增加 [多端点 STUN 发现](../frp-diagnostics/STUN-DISCOVERY.md)，解除单映射 STUN 的准备失败；TUN 开启的同机双客户端已验证业务直连。延续 [检测 TUN 后绑定物理网卡](TUN-BYPASS.md)，不需要管理员权限，不改系统路由或 Clash。手机网络场景仍待新版实测。

独立的 Windows TCP 服务接入程序。发布模式把配置中的本机 `127.0.0.1:localPort` 注册成私有 XTCP / STCP 服务；访问模式在本机 `127.0.0.1:visitorPort` 提供入口，先尝试 XTCP，1.5 秒未建立则允许 STCP 回退。不会修改 ReDesk、RustDesk、系统 VPN、默认路由、防火墙或开机任务。

## 构建和运行

需要 .NET SDK 10。以下命令在仓库根目录运行：

```powershell
dotnet build gateway/windows/LinkGate/LinkGate.csproj -c Release
dotnet run --project gateway/windows/LinkGate.Tests/LinkGate.Tests.csproj -c Release
dotnet publish gateway/windows/LinkGate/LinkGate.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/linkgate-0.1/windows
```

发布目录必须包含按 [计数运行时构建](../frp-diagnostics/README.md) 从固定 FRP **0.71.1** 源码重建的 `runtime/frpc.exe`。不要只分发缺少运行时的 EXE。构建锁嵌入应用，每次启动前强制核对 SHA-256 和版本，再用 `frpc verify` 检查配置。若缺失、不同或被拒绝，返回短错误码，不下载或运行替代程序。

打开 `LinkGate.exe`，点击“导入配置…”选择自己的配对 JSON。电脑服务端选择“发布本机服务”；用于从这台电脑访问另一台电脑时选择“访问远端服务”。停止或退出窗口会回收本实例持有的 frpc，不通过进程名称或端口批量终止其他实例。

## CLI

CLI 输出脱敏 JSON，便于脚本检查；不会输出原始运行时错误或完整配置。命令行仅传配置文件路径，不传密码、令牌或秘密密钥。

```powershell
LinkGate.exe --verify --profile C:\private\pairing.json
LinkGate.exe --run publisher --profile C:\private\pairing.json
LinkGate.exe --run visitor --profile C:\private\pairing.json
LinkGate.exe --ui --run publisher --profile C:\private\pairing.json
```

可加 `--state-dir C:\private\isolated-run` 使用独立的私有运行目录；两种角色并行测试时给它们不同目录。开发测试可加 `--runtime C:\verified-runtime\frpc.exe` 明确指定已经核验的运行时。`--duration-seconds 60` 在时间结束后正常停止；不指定时持续运行，按 Ctrl+C 停止。配置预检同时检查 publisher 和 visitor 的 TOML，**不会**建立业务连接或验证直连。

```powershell
LinkGate.exe --demo --port 18480
```

示范服务仅监听 IPv4 回环地址，提供：

- `GET /health` 或 `/`：固定只读 JSON。
- `GET /bytes/N`：最多 8 MiB 的确定性测试内容，`byte[i] = i % 251`，便于核对实际传输完整性。
- `/ws`：WebSocket echo，单帧缓冲 64 KiB，升级后单连接最长 90 秒，为 60 秒持续测试及正常关闭预留余量。

示范服务最多 16 并发连接，不读取业务文件、不接受业务写操作。它只用于联通与完整性验收；实际业务仍须保留自己的认证、HTTPS、权限和写操作回执。FRP 的服务器 TLS 不能代替业务端到端的 HTTPS；变更入口地址时应正确处理原证书、Host 与 Origin，不能关闭证书验证。

## 配置与隐私

与 Android 使用同一份严格 JSON 合同：`formatVersion=1`、12 位小写字母数字 `namespace`、`deviceName`、`server`、`services`。服务端字段为 `host`、`port`、64 位十六进制 `token`、`tlsServerName`、单个有效 CA 的 `trustedCaPem`，可选 `stunPort` 默认 3478；本项目部署可设为已有 STUN 的端口。每项服务包括 `id`、`label`、`localPort`、`visitorPort`、64 位十六进制 `secretKey`。

不接受协议前缀、路径、未声明字段、重复 JSON 属性、重复服务 ID、重复访客端口、低端口、无效或含私钥的 CA。最多 16 项服务、配置最大 128 KiB。导入失败保留上一份有效配置。私有导入、CA 和运行 TOML 默认保存在 `%LOCALAPPDATA%\LinkGate\private`；目录不继承父目录权限，只授权当前用户和 SYSTEM。目录链包含链接／junction 时拒绝写入。私有材料不能放进源码仓库或公共安装包。

运行时子进程加入独立的 Windows Job Object，设置 `KILL_ON_JOB_CLOSE`。正常停止回收持有句柄的进程树；主程序异常退出时由内核回收该 Job 的子进程。初始化不需要管理员权限。

状态区分别显示进程/注册、历史 NAT 打洞和实际业务路径，字节依据 `libio.Join` 计数，支持混合路径。固定大小缓冲读取日志，单行最多 4096 字符，只保留最多 100 条脱敏分类事件。CLI 每秒输出带 UTC 的诊断快照，界面可复制 JSON；不保留原始错误或完整配置。具体字节、吞吐、建立时间及失败原因的含义见 [诊断说明](../frp-diagnostics/README.md)，与 ECS 核对按 [测试矩阵](../tests/NETWORK-MATRIX.md) 执行。

## 验证范围

测试覆盖配置白名单／重复字段／证书与端口边界、私有导入的原子性、只导出回环地址、XTCP/STCP 对应关系、日志秘密边界与容量、Job 关闭回收实际子进程，以及示范 HTTP、文件内容、拒绝写请求和 WebSocket echo。公网打洞成功率、Clash 配置和 Android 后台效果另行实测，不由这些本地测试代替。

FRP 配置依据：[访客字段与回退](https://gofrp.org/en/docs/reference/visitor/)、[代理字段](https://gofrp.org/en/docs/reference/proxy/)、[XTCP 机制](https://gofrp.org/en/docs/features/xtcp/)。
