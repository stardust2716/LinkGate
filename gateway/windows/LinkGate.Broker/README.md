# Windows 开发 Broker

开发库，尚未接入已安装的 LinkGate。默认只支持 `hello`；显式提供独立 `RegistrationStore` 时增加 `app.register` 和 `operation.status`，注册始终待批准。进一步显式配置 `SecureConnections` 才增加安全直连业务能力，见 [独立接入预览](../../examples/secure-direct/README.md)。读取到完整帧后，以管道模拟令牌核对实际客户端 SID，注册模式再核验实际客户端进程令牌和路径，拒绝伪造主体。

使用 native CreateNamedPipe 的受保护 current-user DACL、FIRST_PIPE_INSTANCE、REJECT_REMOTE_CLIENTS 和异步字节模式。规则依据 [Microsoft CreateNamedPipe 文档](https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-createnamedpipew)。管道范围为 SID 的 SHA-256。首个句柄保留至停止，名称已被占用则失败，不接管。

单连接串行处理，最多32帧；每次读/写各2秒，零长度、超64KiB、截断帧关闭连接。最后一帧响应后最多等待2秒供客户端读取，再回收实例；慢客户端超过预算不保证收齐最后响应。支持严格 API1 JSON、UUID、版本协商，错误不回显输入。客户端之间共享本次 host 的 bootId。

`VerifiedPipeClient` 已移至独立 [LinkGate.Client SDK](../LinkGate.Client/README.md)，新增待批准注册与显式操作查询。调用方必须先通过可信启动流程取得 Broker 的进程句柄，连接时复制并持有句柄，再以 OS 的管道服务端 PID、进程创建时间和存活状态核验对端，核验前不发送请求字节。仅接受本机规范管道名；每次交换前再次核验，超时/无效响应后关闭，不自动重连。

信任前提很重要：进程句柄必须来自已核验的启动方，具有查询及同步等待权限，不能按发现的 PID 随意打开后就称可信。依据 [GetNamedPipeServerProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeserverprocessid)、[进程句柄生命周期](https://learn.microsoft.com/en-us/windows/win32/procthread/process-handles-and-identifiers)。本实现不提供安装文件签名/哈希核验、自动发现或启动器；同用户恶意进程不属于隔离承诺，因此还不是正式 SDK。其他用户及远程机器访问需后续多主体环境验证。单连接可用性/负载也未达到完整0.3验收。

仓库根执行（Windows、.NET10）：

```powershell
dotnet run --project gateway/windows/LinkGate.Broker.Tests -c Release
```

测试临时创建随机后缀管道，结束后回收；不启动 FRP、不访问 ECS、不修改安装版。

## 待批准应用注册

开发者显式创建 `new RegistrationStore(privateDirectory)` 并传给 `new PipeHost(registrations: store)`。路径属于可信宿主配置，不接受 RPC 指定。状态保存在当前用户/SYSTEM 可访问的 SQLite 数据库；默认模式不创建任何数据库。Microsoft.Data.Sqlite 10.0.14 及其 SQLitePCLRaw 依赖记录在 `packages.lock.json`，可以用 `dotnet restore gateway/windows/LinkGate.Broker --locked-mode` 核验锁文件。该依赖仅进入新开发项目，未加入安装版。

`app.register` 参数仅 `{manifest: ...}`，必须带稳定 UUID operationId；不要因为 IPC 超时生成新 operationId。返回 applicationRef、permissionDiff、approvalRequired=true。差异以先前 pending 声明为基线，包含 added/removed/changed exports/imports 和 declarationChanged，不代表已经批准的权限。`operation.status` 参数 `{operationId: ...}`，查询同一 OS 主体已成功提交的结果。没有成功结果时返回 OPERATION_UNKNOWN，不能据此自动重放业务写。

当前主体按 OS 取得的 SID 与 EXE 路径派生，重启后保持稳定；同一个解释器/宿主中的不同程序共用主体。没有签名/内容验证，不能只凭应用自报 ID 授予网络访问。细节和限制见 [ADR0003](../../docs/adr/0003-pending-registration.md)。默认 pending SDK/样例仍只注册/查询；新安全数据能力要求另外明确完成宿主声明审核、设备信任和发布端服务授权。

事务同时保存 pending 声明和成功结果，原操作重试不会重新创建或覆盖较新声明；相同 operationId 改参返回 OPERATION_CONFLICT。当前结果不删除，容量满拒绝新增操作，已保存结果仍可查询。失败、锁冲突和存储错误不生成临时成功；未验证磁盘断电、100000短流、跨用户及长期负载。

## 宿主声明审核（开发基础）

新增 `RegistrationStore.GetReview(applicationRef)`、`DecideReview(snapshot, decision, operationId)` 和 `GetReviewOperation(operationId)`。这些是**可信宿主进程内API**，没有对应的应用RPC、管理端口或批准页面；普通应用仍只能提交pending声明和查询自己的操作。宿主应展示快照中的真实目标、策略和先前批准内容，取得用户决定并持久保存操作号后才调用：

```csharp
ApplicationReviewSnapshot snapshot = store.GetReview(applicationRef);
// 在可信宿主展示 snapshot.PendingManifest / ApprovedManifest，等待用户决定。
// userDecision 为 Approved / Denied / Revoked，savedOperationId 已持久保存。
ApplicationReviewResult result = store.DecideReview(snapshot, userDecision, savedOperationId);
// 丢回复或重启后，先 GetReviewOperation(savedOperationId)，不自动换号重放。
```

快照为不可变对象，内部绑定状态目录/实际调用范围；不是可以由应用JSON构造的批准令牌。PendingRevision只在声明变化时递增，DecisionVersion在批准/拒绝/撤销时递增；决定同时核对这两个版本及规范化内容。版本过期或声明A→B→A后，旧快照返回REVIEW_STALE，需要重新展示确认；整数溢出拒绝修改。

Approved只将确认的整份声明保存为本机候选配置，**不创建设备/服务grant或业务连接**。pending更新保留先前ApprovedManifest；Denied拒绝当前pending，也保留旧批准。要移除先前批准，需明确Revoked。撤销后旧批准operationId重放仅返回原历史结果，不恢复批准；历史Operation结果不能被当成当前状态，当前状态须重新GetReview。重启后的操作查询不需要旧快照；取得新快照后不得把旧操作号用于新版本的决定。

当前数据库schema为5：保留schema4的待确认权限申请，并增加独立身份绑定、设备信任、服务grant和安全打开日志。schema1迁移时旧声明只有pending修订1、审核版本0且无批准；旧审核/批准及操作结果保留，验证撤销预留可容纳后才提交。迁移不生成任何设备信任、grant或连接，旧代码不能回读schema5；缺失/损坏状态拒绝读取或修改，不自动重建。迁移发生在开发宿主显式打开状态目录时，本次仅迁移自有测试目录，未改运行版数据。

审核方法使用固定 `ApplicationReviewException.Code`：REVIEW_STALE、APPLICATION_UNKNOWN、OPERATION_CONFLICT、OPERATION_UNKNOWN、RESOURCE_EXHAUSTED、STORAGE_UNAVAILABLE。数据库迁移/结构失败返回固定InvalidDataException，保留未知版本拒绝。管理员操作使用独立保留范围，但与注册共用额度；现在为每份批准预留一条撤销结果，因此普通操作占满逻辑预算时，有效已批准声明仍可使用预留撤销。磁盘/锁/快照等其他失败仍必须报告，不能把逻辑预留当成磁盘空间保障。

Windows Broker/SDK当前共258项检查通过（前版210项，加上本轮48项检查），覆盖审核、真实管道拒绝管理操作、旧操作重放、撤销、重启、并发、配额、ABA、事务回滚、schema迁移及损坏状态。独立SDK样例注册/重启查询继续通过。完整语义见 [ADR0009](../../docs/adr/0009-windows-declaration-review.md)。当前没有可信批准UI、签名连续身份、逐服务/设备授权、活跃流撤销或外部备份反回滚锚，不能将本机记录视为完整AUTH或0.3验收。

## 撤销额度与宿主诊断

`GetJournalCapacity()`只在可信宿主进程内返回聚合计数，没有新增应用RPC。包含RetainedOperations、RetainedHostOperations、ReservedRevocations、GlobalLimit/HostLimit、AvailableGlobalOperations、AvailableHostOperations和WithinLimits；数据来自同一个SQLite事务。可供未来管理界面显示额度，不包含应用列表、身份、路径或声明内容。

若已保留操作数为O、宿主操作数为H、已批准声明数为R，则要求`O+R <= GlobalLimit`和`H+R <= HostLimit`。普通应用还受自己的主体上限约束。默认硬限仍是每主体500000、全局1000000；预留计入硬限，历史操作不被删除。

| 修改 | 新结果数 | 预留变化 | 逻辑占用变化 |
|---|---:|---:|---:|
| 初次批准未批准声明 | +1 | +1 | +2 |
| 替换已有批准／拒绝pending | +1 | 0 | +1 |
| 普通注册 | +1 | 0 | +1 |
| 撤销已有批准 | +1 | -1 | 0 |
| 撤销未批准声明 | +1 | 0 | +1 |
| 查询／相同操作重放 | 0 | 0 | 0 |

没有两格可用时不能初次批准。pending更新、拒绝pending和替换已有批准都不会丢失其原预留。撤销在同一事务内清空批准并写入结果；写入失败时批准和预留一起回滚，不能只释放额度。重启从批准行派生预留，不依赖内存计数；旧批准重放不会复活批准或预留。

schema2中若旧结果加预留已超出预算，迁移整体失败、保留旧状态，不能自动删记录、清空批准或扩大限制。当前未提供离线清理工具，需要后续明确受审核的维护流程。测试覆盖逻辑满额、并发和注入SQL写入失败，没有物理磁盘满、断电或活跃流测试。详见[ADR0010](../../docs/adr/0010-revocation-capacity.md)。

## 本地交互审核工具

现有[独立开发控制台](../LinkGate.Review.Console/README.md)可浏览待审核引用、显示完整声明和先前批准内容、确认批准/拒绝/撤销、查看容量及恢复原操作。它只管理声明候选，不授予网络权限；当前作用域摘要不能证明已认证二进制身份。控制台只在交互终端且输入OPEN后打开明确选择的现有开发状态目录，没有管理RPC或自动批准参数。

宿主API新增ListReviewReferences，采用有界keyset分页（默认20、最大200）并拒绝非规范/重复UUID；快照CallerScope仅供宿主展示范围来源，不进入普通应用响应。Windows Broker/SDK/控制台当前381项检查通过。完整边界与回执恢复见[ADR0011](../../docs/adr/0011-local-review-console.md)。

## 待确认权限申请

默认关闭。嵌入宿主使用 `new PipeHost(testSuffix, store, enablePendingGrants: true)`，独立 `LinkGate.Broker.Console` 在两个原参数后加 `--pending-grants` 才开启。其申请/状态方法为grant.request和grant.request.status；真正grant、grant.approve/list/revoke、service.publish、connection.open依然不可用，不启动FRP或网络数据流。

grant.request必须有根operationId，params为applicationRef、importAlias、deviceId、publisherAppId、remoteServiceId和可省略reason。以实际调用范围检查应用归属及已审核import，固定AUTH_REQUIRED拒绝不满足条件的请求；不接收策略/身份/凭据覆盖。事务保存申请、获准import快照与审核版本和原操作成功结果。旧操作重放只返回旧结果，不恢复superseded申请。

grant.request.status只按实际调用范围返回该申请的pending/superseded。宿主GetPendingGrantRequest和ListPendingGrantReferences可跨应用只读管理，后者默认20/最大200条keyset分页；不对普通应用开放宿主枚举。新schema4迁移不改变旧批准、日志或撤销预留，不凭空生成申请，缺失表失败关闭。每范围1024/全局16384份保留申请及原日志额度均有限制，满额拒绝新请求而不淘汰旧结果。

完整契约见[ADR0012](../../docs/adr/0012-pending-grant-requests.md)，调用见[SDK](../LinkGate.Client/README.md)。381项检查覆盖真实管道/SDK、申请绑定/越权、丢回复恢复、并发、SQL回滚、迁移及合成私有数据的1024/16384容量边界；没有实际授权或业务数据测试。


显式服务端授权组合已验证：`SecurePeerServer(..., LeaseSupervisor)` 在本地grant之外要求实际mTLS控制服务核验的精确服务租约；`SecureConnections(..., ControlClient)`为访问端取租约并在剩余60秒时续期。两端数据仍执行设备mTLS与原业务认证，失败不回退到无服务器租约的预览消息。没有提供这些参数的既有开发入口保持预览模式；这不是已安装客户端的自动配置入口。实际SDK/Java业务、续期及控制故障证据见 [授权集成测试](../../tests/control-authority/README.md)。

## 正式访问的本地授权基础

schema7新增精确visitor权限，schema1至6原子迁移且不自动生成访问权。可信宿主用 `ProposeLocalAccess(requestId)` 读取待批准请求并向所有者展示范围，以稳定操作UUID调用 `ApproveLocalAccess(proposal, operationId)`；撤销使用 `RevokeLocalAccess(reference, expectedRevision, operationId)`。未知结果查 `GetSecureAuthorityOperation(originalOperationId)`，不生成新批准来重试。

这些是宿主源码接口，普通应用不能通过RPC调用。活动权限绑定声明、peer和精确服务，并保留撤销额度，见 [ADR0026](../../docs/adr/0026-windows-local-access.md)。正式Windows连接/SDK及所有者工作流仍待接入；此表不改变旧secure预览接口的范围声明。

当前开发库数据库已到schema8，保留schema7精确授权并新增连接、一次性附接和累计状态日志。宿主在独占状态锁内恢复旧boot记录；异常尾部不标完整。正式WindowsRPC/SDK尚未暴露这些入口，见 [进度](../../docs/PROGRESS.md)。

## 公开连接实验执行

显式提供serverAuthority的SecureConnections现声明connection.api，组合LocalAccess及持久日志；普通应用只能打开/查询/附接/关闭，不能批准权限。构造时提供的peer直连目标和SecureRelayRoute属于可信宿主配置。中继和直连都使用服务器租约与设备mTLS，失败不退回旧预览。最多32个formal项、每caller16个，还受全局/调用者持久上限约束。

NotifyNetworkChanged是可信宿主通知入口，当前实验没有自动Windows网络回调，也不修改TUN/默认路由。动态运行时发现与所有者grant操作界面仍待接入。公开SDK实际传输和取消验证见 [ADR0027](../../docs/adr/0027-windows-public-connections.md)；前文“正式RPC尚未暴露”描述此前日志工作包。
