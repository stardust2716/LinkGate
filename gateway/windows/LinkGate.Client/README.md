# LinkGate.Client：Windows 声明、权限申请与安全数据流 SDK

仅引用 LinkGate.Contracts 和 LinkGate.Control，没有 SQLite、FRP、Broker 服务或配对配置。当前是 .NET10/Windows 的开发子集，尚未发布 NuGet 包或接入安装版。

显式配置的安全 Agent 现在提供 `secure.stream.direct` 实验能力：`OpenSecureDirectAsync`、`GetSecureConnectionStatusAsync`、`AttachSecureStreamAsync` 和 `CloseSecureConnectionAsync`。应用可取得正常双向 `BusinessStream`，保留 TCP 半关闭及 HTTPS / WebSocket 自身认证；未授权和撤销后的连接被拒绝。完整的可运行示例、所有者设备签发/服务授权流程及限制见 [安全直连接入](../../examples/secure-direct/README.md)。默认 pending Broker 不启用这些能力；该原始预览入口只支持已知 IPv4、brokered import、direct-only；后续显式中继入口见下文。

```csharp
using LinkGate.Client;
using LinkGate.Contracts;

// trustedProcess 必须由已核验的可信启动方持有，不能按发现的 PID 自行信任。
using var client = await VerifiedPipeClient.ConnectAsync(pipeName, trustedProcess.SafeHandle);
await client.HelloAsync();
var manifest = ManifestContract.Parse(File.ReadAllBytes("application.json"));
// operationId 由应用在发请求前持久保存；IPC 超时不能换一个新 ID。
var registered = await client.RegisterPendingAsync(manifest, operationId, allowExperimentalPending: true);
var operation = await client.GetOperationStatusAsync(operationId);
```

注册始终待批准；不授予发布、访问或设备权限。实验功能需要协商到 `app.register.pending` 并显式开启。结果为已验证形状的 JsonElement，额外字段须版本化评审，不能当作任意后端 JSON 传递。

服务端错误抛 BrokerRequestException，仅提供 Code、Stage、Retryable，消息固定，不包含原始 message/details。Retryable 不授权自动重放。开始修改写入后遇到 IO/取消/超时或坏响应，抛 MutationOutcomeUnknownException，包含原 OperationId：关闭旧连接，由调用方显式重新核验 Broker、协商后查询。SDK 不重连、不重试修改、不自动生成操作号。查询返回 OPERATION_UNKNOWN 也不能当作已确认安全重放业务的证明。

每次交换上限2秒；同一客户端并发交换直接拒绝，不排无限队列。写入前取消不会声明已提交，但没有可靠结果的修改都应保留操作号。独立示例见 [Registration.Sample](../../examples/dotnet-registration/README.md)。现有未发布的 `LinkGate.Broker.VerifiedPipeClient` 源码已移到 `LinkGate.Client` 命名空间，详见 [ADR0004](../../docs/adr/0004-pending-sdk.md)。

进程句柄核验不能代替可信安装器、签名/摘要校验，也不能隔离同用户恶意代码。该.NET SDK不提供实际grant批准、数据流或完整业务接入；Android对应实验能力尚未实现。

## 待确认权限申请（默认关闭）

Broker宿主须显式 `new PipeHost(testSuffix, store, enablePendingGrants: true)`，或在独立开发宿主的原有目录/后缀参数后追加 `--pending-grants`。原构造重载保留默认关闭行为。hello只有这时才返回 `grant.request.pending=experimental/PENDING_ONLY`；其余网络和identity.device-grant能力仍unsupported。

应用先注册含import的manifest，并由宿主审核该声明。 可参考[文件客户端声明](../../spec/examples/file-client.app.json)，其中alias为remote-files；发布者声明示例见[文件服务](../../spec/examples/file-service.app.json)。`app.register`成功本身不等于已审核。一个实际调用范围可有多个逻辑appId，所以申请必须显式给出**属于实际调用范围的applicationRef**，不能只靠alias隐式选应用。此上下文解决路由歧义，不提供共享EXE内部的恶意程序隔离。

```csharp
// appRef 来自本应用原注册结果，其 files/remote-files import 已由宿主审核。
// device-placeholder 只是实验引用，不代表已认证或已配对设备。
var request = new PendingGrantRequest(appRef, "remote-files", "device-placeholder",
    "org.example.file-service", "files", "Read private files");
// grantOperationId 必须由调用方在提交前持久保存。
var pending = await client.RequestGrantPendingAsync(request, grantOperationId,
    allowExperimentalPending: true);
Guid requestId = Guid.Parse(pending.GetProperty("grantRequestId").GetString()!);
var history = await client.GetGrantOperationStatusAsync(grantOperationId);
var status = await client.GetGrantRequestStatusAsync(requestId);
```

结果严格为 `grantRequestId` 和 `pendingUserApproval=true`，没有token、grantId或数据句柄。reason可省略，最多256个Unicode标量，不能含控制字符/坏编码；请勿填入秘密。不能覆盖import策略、指定caller、任意转发地址或凭据。

`GetGrantOperationStatusAsync`读取原操作历史成功，`GetGrantRequestStatusAsync`读取当前申请的pending/superseded，两者不能混用。每次申请绑定当时ApprovedRevision、DecisionVersion和规范化import；后续审核/撤销使旧申请superseded。尚未取代旧批准的pending更新不扩大该绑定。pending本身没有网络权限；安全直连需另外完成设备信任和发布端服务授权，不把旧申请自动转为grant。

丢回复、取消、超时或坏响应仍抛带原号的MutationOutcomeUnknownException，SDK不自动重放。关闭旧连接、重新核验/协商后用**GetGrantOperationStatusAsync**查询原号；即使宿主后来关闭申请实验开关，这个历史查询仍可用。当前申请状态查询要求实验能力仍开启。OPERATION_UNKNOWN不授权自动换号。

每调用范围最多保留1024份申请、全局16384份；不自动清理，并同时使用原日志预算、保留撤销预留。未审核/错误alias/外部应用或不可见申请统一AUTH_REQUIRED。宿主[命令行](../LinkGate.Review.Console/README.md)可只读检查申请，没有批准grant入口。Windows整套381项检查与独立SDK产物依赖检查通过；这不是实际设备授权或业务数据验收。

可信宿主显式组合私有frpc运行时和精确服务路由后，可另行协商 `secure.stream.relay`，调用 `OpenSecureRelayAsync(..., allowExperimental: true)`；业务仍执行端到端设备mTLS和两层授权，路径显示为relay。原direct入口和direct-only策略不自动回退。复现与限制见 [真实FRP业务测试](../../tests/frp-authority/README.md)。这尚不是正式 `connection.open` 或自动P2P策略。

## 公开连接 API（实验）

可信启动者固定Agent进程并连接后先调用HelloAsync，确认connection.api实验能力。新打开、附接和关闭均显式传true并使用调用应用预先持久保存的不同操作UUID。已审核imports还需要所有者在本机批准精确visitor权限，并在发布端/服务器发grant；不能仅注册声明后直接访问。

```csharp
// validatedRequest = ConnectionOpenRequest.Parse(utf8Json);
var open = await client.OpenConnectionAsync(validatedRequest, savedOpenOperation, true, token);
// pending/reconciling时由应用按自己的超时查询；超过单控制会话32帧时显式重新固定并连接Agent。
while (open.Status is "pending" or "reconciling")
{
    await Task.Delay(150, token);
    open = await client.GetConnectionOperationAsync(savedOpenOperation, token);
}
if (open.Status != "succeeded") throw new IOException("Connection failed.");
Guid connection = open.ConnectionId!.Value;
var claim = await client.AttachConnectionAsync(connection, savedAttachOperation, true, token);
await using var stream = await client.AttachConnectionStreamAsync(
    claim.StreamRef!.Value, savedAttachOperation, true, token);
// 原业务继续使用自己的TLS、主机名校验、认证及完整性校验；不自动重发请求。
// 输出半关闭使用stream.CompleteWritesAsync，完整排空使用DrainWriteCompletionAsync。
var status = await client.GetConnectionStatusAsync(connection, token);
await client.CloseConnectionAsync(connection, savedCloseOperation, true, token);
```

轮询需绑定应用自己的总超时与取消；遇到控制会话32帧上限时显式重新连接并查询原操作。参数字段和收紧规则与 [Android指南](../../android/client-streams/README.md) 相同，C#引用/操作类型为Guid。成功打开结果不等于连接仍活动；用connection.status查当前状态。selectedPath与businessPath分别表达选路和已发生业务；计数为业务帧边界，不含TLS/FRP/IP开销，不代表对端落盘。

MutationOutcomeUnknownException保留原操作UUID；重新固定同一可信Agent后查原结果，不生成新操作重试。原打开成功可以在无formal执行能力的声明控制宿主上经GetConnectionOperationAsync查询，仍不会重新授权或重拨；新的连接/附接/状态则要求已配置的formal宿主。数据管道不确定附接不自动消费第二次streamRef。

Windows目前使用所有者配置的直接目标和精确自有中继路由，尚未自动创建任意新scope的运行时；正式签名发现/安装信任、服务prepare/事件和独立分发仍开放。实际边界与证据见 [ADR0027](../../docs/adr/0027-windows-public-connections.md)。
