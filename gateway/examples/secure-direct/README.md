# Windows 安全直连与独立应用接入预览

这个工作包提供实际业务数据通道：两端独立设备密钥、双向 TLS、发布端服务授权、本机私有数据管道，以及只引用 `LinkGate.Client` 的应用示例。它与已安装 0.2.3、FRP 共享凭据路径分开，不会自动部署、升级或改变路由。

目前适用于 **Windows/.NET 10、可达的已知 IPv4 端点、brokered import、direct-only 策略**。可运行接入示例，也可在应用中使用 SDK；尚不是完整 0.3 或 1.0。Android 数据 SDK、NAT 候选发现/打洞、授权后的中继、自动安装/配对 UI、设备证书续期和数据库备份反回滚仍待完成。无公网可达端点时，这个预览不会复用旧凭据或绕过授权来建立连接。

## 运行可重复验收

在仓库根目录运行：

```powershell
./gateway/examples/secure-direct/verify.ps1
```

脚本构建 Agent、Owner 工具和独立应用，先验证命令行签发、安装、信任、查询及撤销，再启动两个 Agent 进程和两个应用进程。所有测试身份仅存于当前用户受保护的 `artifacts/` 子目录，结束后清理；机器摘要保存在 `artifacts/linkgate-secure-direct-summary.json`。

验收覆盖：无服务 grant 时拒绝业务打开；发放精确服务 grant 后下载 **64 MiB** 并检查 SHA-256；保留 HTTPS 域名和证书验证；未提供应用 bearer token 时仍收到 401；WebSocket 实际回显；TCP 发送 FIN 后继续接收响应；撤销 grant 后五秒内切断活动 WebSocket，拒绝新打开，而另一项服务继续工作。消费应用输出目录和依赖清单不得含 Broker、SQLite、FRP 或设备私钥。

这是 **同一台 Windows 上的独立进程端到端验证**。它不代表物理两设备、跨 NAT、移动网络、Clash、其他用户 SID 或 Android 已验收。Agent 示例绑定 `127.0.0.1`，用于隔离本地开发资源。

## 组成和信任边界

```text
消费应用 → SDK → 受保护的本机控制/数据管道 → 消费端 Agent
                   持有进程核验 + 实际调用方核验       │
                                           双向 TLS 控制通道
                                           双向 TLS 数据通道
                                                   │
发布应用的固定 127.0.0.1 服务 ← 服务授权检查 ← 发布端 Agent
```

- `LinkGate.Owner.Console`：所有者创建根密钥、签发设备 CSR、显式信任设备、审批/撤销精确服务 grant。根私钥留在所有者私有目录，不发给设备或应用。
- `LinkGate.Agent.Console`：加载本机设备私钥和身份数据库，运行安全直连和私有 IPC。普通应用没有审批 RPC、任意目标地址或设备私钥。
- `LinkGate.Review.Console`：已有的宿主声明审核工具。应用声明获批和服务 grant 是两次不同的所有者决定。
- `SecureApp`：只依赖 SDK 和系统框架。HTTPS 和 WebSocket 仍有应用自己的 bearer token、TLS 和逻辑域名。
- `OwnerHarness`：测试所有者，负责临时签发、审核和授权；不能复制进普通应用作为自动审批代码。

设备 ID 是设备证书公钥 SPKI 的 SHA-256。设备在本地创建 P-256 私钥和 CSR，所有者签发有效期 30 天的设备证书。TLS 同时检查私有根、有效期、用途、名称和显式批准的设备公钥，不安装系统根证书。每个 grant 绑定客户端设备、客户端应用 ID、已审核发布应用和一个固定 export，并记录信任/审核/授权版本。重新信任设备不会恢复旧 grant。

Windows 当前按用户和 EXE 路径建立协作信任域，**不隔离同一 Windows 用户下的恶意程序**。示例只核对所有者提供的 Agent EXE 哈希并固定实际进程；这不等于已实现签名安装包、依赖文件完整性或系统级恶意软件防护。

## 手动配置两台 Windows

下面是明确的所有者操作流程，不会自动开放防火墙。先准备可达的 IPv4 地址和一个专用 TCP 端口；例如同局域网发布端 `192.168.1.20:17443`。端口只接受受信任根签发且被显式信任的设备。跨互联网需要已有可达地址；本预览不替你配置端口转发。

构建后，以下路径都指向完整构建目录中的 EXE，不能只复制单个 EXE：

```powershell
dotnet build gateway/windows/LinkGate.Owner.Console -c Release
dotnet build gateway/windows/LinkGate.Agent.Console -c Release
dotnet build gateway/windows/LinkGate.Review.Console -c Release
$owner = '.\gateway\windows\LinkGate.Owner.Console\bin\Release\net10.0-windows\LinkGate.Owner.Console.exe'
$agent = '.\gateway\windows\LinkGate.Agent.Console\bin\Release\net10.0-windows\LinkGate.Agent.Console.exe'
```

1. 所有者在自己的受保护位置运行 `owner-create <新根目录>`。备份该目录时采用私有、安全的备份方式；不能将 `owner-key.pem` 放入 Git 或发行包。
2. 每台设备运行 `device-request <新身份目录>`。只把 `device-request.pem` 交给所有者；`device-key.pem` 留在原设备。
3. 所有者核对请求来源后运行 `issue <根目录> <CSR文件> <新证书文件>`，返回设备证书及公开 `owner-root.pem`。通过可信途径确认根和设备公钥，不能信任收到的任意根文件。
4. 每台设备运行 `device-install <身份目录> <已签发证书> <已确认的公开根>`。它校验证书对应本机私钥，不覆盖不同的已有身份，并输出公开设备 ID。
5. 所有者在双方运行 `trust <状态目录> <身份目录> <对端公开证书> <原信任版本> <对端IPv4> <对端端口> <已核对的对端设备ID> <操作UUID>`。首次版本为 `0`，后续必须使用当前版本。消费端的对端端口就是发布端 Agent 端口。
6. 用显式绑定观察调试宿主：`& $agent '<状态目录>' '<身份目录>' '<32位无横线随机GUID>' '192.168.1.20' '17443'`。Agent 输出公开管道名、设备 ID 和端口；标准输入 EOF、Ctrl+C 或 30 分钟开发时限会停止它。一个状态目录只允许一个安全宿主；改由示例或可信启动器启动同一状态时，先停止这个调试宿主。长期服务宿主、安装态跨应用可信发现和句柄交接仍待开发。
7. 应用通过可信启动器提供的实际 Agent 进程句柄连接 SDK、提交声明，并保存返回的 `applicationRef`。启动器须确认可信构建；不能仅凭进程名、PID 或管道名建立信任。普通应用不能把管道发现当作可信启动。
8. 在每台设备使用 `LinkGate.Review.Console <状态目录>` 读取待审核声明，检查具体端口、服务、导入策略后明确批准，详见 [审核工具](../../windows/LinkGate.Review.Console/README.md)。仅批准声明还不能访问另一台设备。
9. 在发布端生成精确授权提案，再核对并批准：

```powershell
& $owner grant-propose '<发布端状态目录>' '<客户端设备ID>' 'org.example.my-client' '<发布应用applicationRef>' 'files' '<新提案JSON文件>'
# 核对提案内的设备、客户端应用、发布应用、服务和所有版本
$grantOperation = [guid]::NewGuid().ToString()
& $owner grant-approve '<发布端状态目录>' '<提案JSON文件>' $grantOperation
```

命令返回 grant ID 和版本。撤销使用 `grant-revoke <状态目录> <grantID> <当前版本> <新操作UUID>`；撤销整台设备使用 `peer-revoke <状态目录> <设备ID> <当前版本> <新操作UUID>`。中断或未知结果先执行 `operation-status <状态目录> <原操作UUID>`；同一 UUID 不能更换参数，也不能凭超时生成新 UUID 自动重复审批。

## 在应用中使用 SDK

只引用 `gateway/windows/LinkGate.Client/LinkGate.Client.csproj`。以下片段假定声明已经审核、发布端已发放服务 grant，且应用持有可信启动器提供的 Agent 进程句柄：

```csharp
using var client = await VerifiedPipeClient.ConnectAsync(pipeName, trustedAgentProcessHandle, ct);
await client.HelloAsync(ct);
var scope = new PendingGrantRequest(applicationRef, "files", publisherDeviceId,
    "org.example.publisher", "files");
// 先将 operationId 持久保存；中断时查询原操作，不能自动重复打开。
var state = await client.OpenSecureDirectAsync(scope, operationId,
    allowExperimental: true, cancellationToken: ct);
while (state.GetProperty("state").GetString() == "pending") {
    await Task.Delay(100, ct);
    state = await client.GetSecureConnectionStatusAsync(operationId, ct);
}
await using var stream = await client.AttachSecureStreamAsync(state, ct);
// 普通读写；需 TCP 半关闭时调用 stream.CompleteWritesAsync(ct)，随后仍可读取响应。
```

实际应用须处理 `closed` 状态、连接预算和每个控制会话 32 次请求上限；长时间查询应显式重连控制管道查询原操作。SDK使用独立的 `secure.connection.open/status/close` 实验RPC，未替代完整产品的 `connection.open` 契约；`CloseSecureConnectionAsync` 幂等关闭原操作。数据连接断开不会自动重播业务。Agent 重启后原打开记录保留为关闭，SDK 不把旧操作重新建立成一条新业务流。

HTTP/HTTPS 使用 `SocketsHttpHandler.ConnectCallback` 返回这个 stream，原请求 URL 保留逻辑域名，继续由系统完成 SNI、证书和 HTTP 校验；不能把 URL 改成 `127.0.0.1` 后关闭证书验证。WebSocket 可复用带相同连接适配的 `HttpMessageInvoker`。完整可编译示例在 [SecureApp/Program.cs](SecureApp/Program.cs)。

数据附接管道只使用一次；SDK 在发送附接头之前核验它属于同一可信 Agent，Agent 再核验实际 EXE 调用范围。业务帧最大 16 KiB，有独立方向 FIN/确认和背压；数据拥塞不会阻塞控制租约。控制心跳每秒一次、授权租约最长 120 秒，控制超时或已知撤销会主动关闭连接。监控批量读取权限快照，正常本机负载下撤销目标为五秒以内。超时不会改连另一个服务或自动发送第二份业务。

## 后续范围

安全数据路径现在有真实接入证据，但还需要把它与已有 0.2.3 的候选发现、打洞和必要中继在同一授权边界下集成；不能直接把旧共享 FRP secret 当成新设备权限系统。之后再完成 Android 对等实现、配对/证书轮换、网络切换、安装服务、受控升级及完整多设备验收。完整状态以 [进度文档](../../docs/PROGRESS.md) 和根 SpecDD 任务为准。
