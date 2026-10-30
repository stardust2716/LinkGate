# LinkGate 应用网关工程规格

文档版本：1，状态：设计草案与分阶段实现，目标：LinkGate 1.0。当前安装运行版本仍为 0.2.3。本规格中的 MUST（必须）、SHOULD（应当）、MAY（可以）约束后续实现，不表示全部功能已经交付。后续开发已获授权，当前进度和验证范围见 [PROGRESS.md](PROGRESS.md)；安装与部署不随局部合同实现自动发生。

配套：[产品与开发方案](PRODUCT-PLAN.md)、[安全规格](SECURITY-SPEC.md)、[验收规格](ACCEPTANCE.md)。接口设计发生变更时，必须同步本文、schema、示例和验收，不由各 SDK 独立推导。

## 1. 范围与基线

1.0 必须支持 Windows x64 发布和访问服务、Android ARM64 访问服务；业务协议为 TCP、HTTP/HTTPS、WebSocket/WSS。Android 发布、Linux 发布端、macOS/iOS、原生 UDP、整机路由、浏览器免安装接入、公开多租户不属于首版承诺。

产品信任范围为一个所有者及其受信任设备，允许多个应用、设备和服务。应用级授权仍必须存在，不以“同一所有者”代替最小授权。不得提供任意远端地址代理、任意命令执行或无认证控制 HTTP 入口。

1.0 必需网络能力为 IPv4、局域网辅助候选和中继回退。IPv6 在 0.4 研究、实现并独立验收后才可标记 supported；未完成则能力表保持 experimental/unsupported，不悄悄改用未验证路径。IPv4 NAT 无法打通时，按策略中继或明确失败，不承诺 100% 穿透。

0.2.3 已有固定 FRP 后端、多端点 STUN、Windows 物理出站绑定、Android 用户手动 VPN 排除、基础配置/进程保护/脱敏计数。四组测试是网关自身演示请求，不是独立第三方应用集成或长期稳定性证明。现有格式一共享配对、不具备本规格的设备撤销、Broker、SDK 或自动恢复能力。见 [当前验收](../tests/NETWORK-RESULTS-v0.2.3.md)。ReDesk 实验暂不执行，原 [评估方案](https://github.com/stardust2716/Redesk/blob/main/docs/linkgate-redesk-experiment.md) 保留作后续参考。

## 2. 系统分层与模块责任

```text
业务应用 ─ 薄 SDK / 已有 TCP 客户端
                 │
          本机 IPC Broker ─ 应用授权、契约、连接句柄
                 │
          网关 Agent ─ 服务注册、策略、生命周期、计数
                 │
          TransportAdapter ─ FRP 后端（首版）
                 │
          对端 Agent ─ 已批准的回环业务服务

协调控制面：设备目录、服务目录、配对、授权、会话租约
frps：既有协调适配和必要中继；独立 STUN：地址发现
```

| 模块 | 必须承担的职责 | 不得承担的职责 |
|---|---|---|
| Agent | 调度、自有子进程、网络代次、配置事务、凭据代持、服务健康 | 自动重放业务写请求、控制无关进程 |
| Broker | 验证调用者、用户授权、对象归属、IPC/数据句柄、限额 | 把声明的 appId 当作已认证身份 |
| Adapter | 创建/取消尝试、通道探活、数据流、实际路径/计数事件 | 让 SDK 接触 FRP TOML 或注册 token |
| 控制面 | 设备/服务授权、租约、撤销、审核记录 | 信任客户端自报的角色/服务而不校验 |
| SDK | 调用 IPC、等待、取消、状态订阅、协议客户端适配 | 内嵌独立重试打洞循环、替用户批准配对 |
| GUI/CLI | 同一 Broker 的交互入口，解释策略和故障 | 因关闭窗口默认杀死已选择后台运行的 Agent |

首版保留固定 FRP 后端。新增能力通过 Adapter 暴露，禁止直接用 stock frpc 覆盖本项目补丁。Agent 必须在真正需要隔离的范围内重建后端；一个服务配置变化不得无提示打断其他服务。若后端只能整体重启，必须报告受影响服务并要求相应运行策略允许，不能模拟热更新成功。

拟议代码边界为 contracts、agent、transport-frp、platform-windows、platform-android、sdk-dotnet、sdk-android、examples、server-control、tests；具体物理目录可随实现调整，IPC/授权合同不得因此漂移。Windows/Android UI 不承载各自不同的连接策略实现；首阶段可共享契约与一致性测试，逐步把调度归入统一核心，无需一次性重写现有 UI。

## 3. 对象模型与配置

| 对象 | 标识/归属 | 生命周期 |
|---|---|---|
| Owner | ownerId，由网关信任配置确定 | 单所有者首版 |
| Device | deviceId，绑定设备公钥和所有者批准 | 配对、有效、撤销、密钥轮换 |
| Application | appId 加平台调用身份，不仅是字符串 | 注册、待授权、已授权、撤销 |
| PublishedService | ownerId/deviceId/publisherAppId/serviceId 元组 | 声明、批准、健康、停用 |
| Grant | grantId，调用应用到目标设备/服务/动作的授权 | 用户批准、有效期、撤销 |
| SessionLease | 已认证设备/应用/服务范围内的会话租约 | 最长 120 秒，30 秒续期 |
| Connection | connectionId，绑定调用者、grant、服务、网络代次 | 请求、附接、打开、关闭 |
| Operation | operationId 加调用者，持久幂等记录 | pending、succeeded、failed、reconciling |

公开应用 manifest 与用户绑定分离：manifest 声明 exports/imports；用户授权决定 import 具体绑定到哪台设备及发布应用。私钥、设备授权、派生 FRP 材料、运行态不进入 manifest。派生配置不是可信源，必须由声明、授权与策略重新生成。

公开契约为 [app-manifest schema](../spec/app-manifest.schema.json) 和 [连接策略 schema](../spec/connection-policy.schema.json)；[示例](../spec/examples/file-service.app.json) 使用保留示例域名。manifest 的 applicationId 对应内部应用主体 appId，两者不是两套可独立声明的身份。标识大小写敏感；设备、服务、应用必须按完整元组解析，不能只凭显示名寻址。

manifestVersion=1；applicationId 使用反向域名样式标识；exports 和 imports 各最多 16 项；serviceId/alias 各自唯一，长度 1–32。export.localTarget 必须是明确的 127.0.0.1 和 1024–65535 端口。更换目标端口需要新的用户批准，不允许用更新描述绕过原授权。import 中的同名 serviceId 不代表其他发布应用的服务；绑定确认必须展示 publisherAppId/deviceId。

protocol 为 tcp/http/https/ws/wss，均为 TCP 业务。https/wss 必须声明 logicalAuthority；SDK 按原始逻辑 authority 验证证书和构造 Host，不能将目标改为 loopback 后关闭验证。schema 仅校验形状，发布程序必须额外验证 authority 是规范 DNS 主机及可选合法端口，不接受 userinfo、路径、IP 缩写或控制字符。

healthCheck 为 tcp 或 http。HTTP 健康检查仅允许显式批准的只读 GET 路径和 expectedStatus 列表，默认不开启；超时 2 秒，运行中检查间隔至少 15 秒，最多 3 次连续失败后标记 unavailable。https/wss 服务的 HTTP 检查必须使用正确 TLS 身份，不能降级明文检查。TCP 接受连接或获得 401 等预期状态不证明业务认证成功，UI 必须区分传输可达和业务健康。

序列化必须拒绝重复属性、未知字段、超深嵌套、畸形 UTF-8、BOM、未配对 surrogate、非整数词法端口、超限输入；不得静默修正权限与目标。每份 manifest 最大 48 KiB UTF-8，独立策略输入最大 8 KiB，嵌套容器最多 16、值最多 10,000；控制帧仍是 64 KiB，并在 app.register 包装后另外检查实际编码大小。字符串长度按 Unicode 标量计数。应用更新需要原子验证、权限差异提示、成功持久化后再启用；失败保留旧配置。跨语言细节见 [ADR0001](adr/0001-public-contract-normalization.md)。

## 4. 本机接口与身份

Windows 控制面为每用户 Named Pipe，Android 为显式绑定的 Binder 服务。接口必须使用操作系统提供的调用身份；具体安全承诺及 Windows 同用户限制见 [安全规格](SECURITY-SPEC.md)。禁止开放未认证的 localhost 管理端口作为便捷替代。

Windows 控制管道拟命名 `\\.\pipe\LinkGate.<userScope>.control.v1`，userScope 是用户 SID 的稳定摘要，不是秘密。单个 UTF-8 JSON 帧以前置 4 字节小端无符号长度编码，最多 64 KiB。Android AIDL 提供等价语义；Binder 调用者上下文必须在处理异步任务前捕获并绑定，不能在工作线程丢失。协议不把调用者声明的包名当作证明。

控制与数据通道分离，控制请求不能因大文件阻塞。请求 apiVersion=1、requestId 为 UUID；修改状态的请求另须 operationId UUID。响应必须回显 requestId，并且只能包含 result 或 error 之一。规范形状见 [RPC envelope schema](../spec/rpc-envelope.schema.json)，[请求示例](../spec/examples/connection-open.request.json)。params 的允许字段以第 5 节为准，未知字段 MUST 拒绝。

hello 协商返回 apiVersion、agentVersion、capabilities、bootId、限制和所需授权。SDK 必须按能力决定可用功能，不用版本字符串猜测。主版本不兼容返回 VERSION_UNSUPPORTED；未知能力不得假装支持。

当前 Windows pending 子集 SDK 的具体方法、错误和显式恢复语义见 [ADR0004](adr/0004-pending-sdk.md) 和 [SDK 说明](../windows/LinkGate.Client/README.md)；它不表示完整业务数据 SDK 已交付。

Android SDK 的签名固定、Binder 生命周期与有界异步调用见 [ADR0006](adr/0006-android-client.md) 和 [Android SDK](../android/client/README.md)。现提供hello及显式待批准注册/操作查询，未开放授权或业务流；JVM策略和会话验证不替代原生跨UID验收。

Android开发Broker可通过显式构建开关启用[待批准注册实验](adr/0007-android-pending-registration.md)，默认仍只读。其注册/操作查询沿用Windows pending子集的消息形状，权限仍未批准；[Android SDK](adr/0008-android-pending-sdk.md)在协商和显式开启后可封装修改，原操作号由应用事先持久保存；丢回复/取消不会自动重放，须显式查询。SQLite/Binder原生行为继续待验收。

所有请求必须在读取/修改对象前验证调用者和授权。SDK 不获得完整配对配置、全局 FRP token 或其他应用服务列表。GUI 使用的管理权限不授予普通 SDK 调用者。

## 5. RPC 目录

以下为完整目标目录，已实现子集及验收范围见 [进度](PROGRESS.md)。`M` 表示必须携带 operationId；连接请求参数中的 connectionPolicy 可以收紧现有授权策略，不得擅自允许被禁用的中继或扩大预算。

表中业务参数默认必填；cursor、limit、filters、reason、drainTimeoutMs、connectionPolicy 可省略。drainTimeoutMs 默认 5,000，范围 0–30,000；省略 connectionPolicy 使用用户批准的 import 策略。params 为“无”时必须传空对象。hello 的 supportedApiVersions 为最多 8 项正整数数组；ID/alias 均为非空有界字符串或相应 UUID，不能接受控制字符。deviceId/grantId/connectionId 等引用长度最多 128，reason/displayName 最多 256/64 字符。schema 对 params 只检查对象形状，字段与跨对象授权需按表额外校验。

| 方法 | 类型 | params 允许字段 | result 与约束 |
|---|---|---|---|
| hello | 查询 | supportedApiVersions | 选定版本、bootId、capabilities、limits |
| gateway.status | 查询 | 无 | Agent/网络/后端状态；普通调用者只看本应用范围 |
| app.register | M | manifest | applicationRef、permissionDiff、approvalRequired；不自动批准 |
| grant.request | M | applicationRef、importAlias、deviceId、publisherAppId、remoteServiceId、reason | grantRequestId、pendingUserApproval |
| grant.request.status | 查询/实验 | grantRequestId | 当前调用范围的pending/superseded申请状态；不是grant |
| grant.list | 查询 | cursor、limit | 本应用授权摘要；不含凭据 |
| grant.revoke | M/管理 | grantId | revision、受影响连接；调用者只能撤销自己的授权或持有管理权 |
| device.list | 查询 | cursor、limit | 可见的已配对设备及摘要 |
| device.pair.begin | M/管理 | displayName | 一次性邀请引用；秘密仅返回给获准的配对 UI |
| device.pair.approve | M/管理 | invitationId、candidateDeviceFingerprint | 批准后的 deviceId；必须真实用户确认 |
| device.pair.cancel | M/管理 | invitationId | 原子作废尚未完成的邀请，重复取消不复活邀请 |
| device.key.rotate | M/管理 | deviceId、candidateDeviceFingerprint | 批准本设备已生成并证明持有的新公钥；返回新 identityRevision，不接受应用上传私钥 |
| device.revoke | M/管理 | deviceId | revision、撤销传播状态 |
| service.publish | M | serviceId | 已注册 export 的 serviceRef、健康状态；不能临时传 host/port |
| service.unpublish | M | serviceId、drainTimeoutMs | 停止新会话，按既定策略排空；不杀应用进程 |
| service.list | 查询 | deviceId、cursor、limit | 调用者可见服务，协议/健康/能力 |
| service.prepare | M | importAlias、deviceId、publisherAppId、remoteServiceId、connectionPolicy | 有界准备已授权通道，返回 tunnelRef/operation 状态，不打开业务流 |
| connection.open | M | applicationRef、importAlias、deviceId、publisherAppId、remoteServiceId、connectionPolicy | connectionId；可异步，返回 operation 状态 |
| connection.status | 查询 | connectionId | 通道/流/实际路径状态及字节；验证对象归属 |
| connection.attach | M | connectionId | 一次性私有数据通道引用/FD；只能原调用者使用 |
| connection.close | M | connectionId | 幂等关闭本连接，不影响其他应用 |
| operation.status | 查询 | operationId | pending/succeeded/failed/reconciling；同调用者范围 |
| events.subscribe | 查询 | cursor、filters | 有界增量事件/下个 cursor，含 bootId |
| gateway.stop | M/管理 | drainTimeoutMs | 管理确认后停止；手动停止抑制自动恢复 |

Windows 开发 Broker 的待批准子集按 [ADR0003](adr/0003-pending-registration.md) 实现。注册结果 permissionDiff 当前含 addedExports/removedExports/changedExports、addedImports/removedImports/changedImports（各为 ID 数组）及 declarationChanged:boolean，比较先前 pending 声明，不等于已批准权限。applicationRef 为 UUID，approvalRequired 始终 true；hello 中可选 `app.register.pending` 能力仅为 experimental/PENDING_ONLY。该能力不授予发布、访问或自动授权，也不改变其余业务能力的 unsupported 状态。

Windows宿主进程内另有[声明审核基础](adr/0009-windows-declaration-review.md)：按pending修订和审核版本确认整份本机候选声明，pending变更保留旧批准；拒绝pending与撤销旧批准分开。过期快照、旧操作重放不能覆盖新决定。它不属于应用RPC，不增加能力声明，也不等同于设备/服务grant、可信批准UI或数据授权。

Windows另有[本地交互审核命令行](adr/0011-local-review-console.md)：以宿主限定的分页引用和完整快照展示声明，通过新确认短语及提交前持久回执推进决定，支持原操作查询恢复。未核验的二进制身份必须明确显示；该工具不等同于设备/服务grant、正式安装信任或数据授权，也不扩大应用RPC。

Windows宿主审核现在按[ADR0010](adr/0010-revocation-capacity.md)为每份已批准声明预留一条撤销结果，预留计入原全局/宿主硬限，普通操作不能消耗。schema3迁移先验证既有结果与预留可容纳；满额不会自动淘汰结果。这只保障逻辑额度，存储故障等仍可使撤销失败。

Windows显式开启时提供[待确认权限申请](adr/0012-pending-grant-requests.md)。同一调用范围可有多个逻辑应用，故grant.request在此前尚未实现的参数中明确加入applicationRef，按实际调用范围和已审核import校验，不隐式选应用。申请绑定审核版本与完整import快照，结果只含grantRequestId及pendingUserApproval=true；另有当前申请状态，避免把历史成功当仍有效。待批准RPC仍显式开启，它本身不创建grant。后续Android已有所有者精确访问授权和设备mTLS，见ADR0014；历史pending能力不能代替实际授权。

Windows另有[安全直连预览](adr/0013-secure-direct-streams.md)，仅由显式配置的宿主声明`secure.stream.direct`实验能力，其他未完成的产品能力仍unsupported。它使用独立实验命名空间，未替代上表的完整业务API：`secure.connection.open`的params严格为applicationRef、importAlias、peer、publisher、service，必须带外层operationId；`peer`是已批准的设备SPKI指纹，publisher和service是发布应用/服务ID，不能指定地址。协议/authority/预算取已审核import，且当前仅brokered、direct-only、禁用prewarm。`secure.connection.status`和`secure.connection.close`的params仅为原operationId，close幂等终止、不重新创建业务。结果严格含connectionId、operationId、state(pending/ready/closed)、error(固定码/null)、dataPipe(私有管道名/null)、path(direct)。它不是上表OperationResult；原打开的状态/未知结果应通过专用状态方法查询，不能用pending注册SDK解析。

该预览实际执行设备双向TLS和发布端服务grant，普通应用仍不能审批grant；旧pending申请不自动升级。审批、撤销和历史结果查询属于独立所有者工具。已知IPv4的Windows数据通道及SDK已实测，本段为原预览范围，后续Android/FRP及公开连接已进入实验组合；完整接入和复现步骤见[示例](../examples/secure-direct/README.md)。

当前公开connection.*在Android和Windows都以显式实验connection.api暴露，见[ADR0024](adr/0024-public-connection-lifecycle.md)和[ADR0027](adr/0027-windows-public-connections.md)。Windows限定所有者配置的直接目标/自有中继路由，Android已有scope池与LAN/XTCP候选。两端均有持久OperationResult、ConnectionStatus及一次性streamRef；缺少的service.prepare、事件、动态Windows发现和运维/发布能力仍未声明支持。下列成熟版协议要求继续作为完整验收目标。

M 请求采用同调用者/operationId 的持久去重，规范化参数摘要相同则返回原结果，不同则 OPERATION_CONFLICT。requestId 只关联一次 IPC 往返，不承担幂等。结果至少保留 24 小时，每调用者默认最多 500,000 项、Agent 全局最多 1,000,000 项，采用有界持久存储而非全部常驻内存；容量满时拒绝新修改，不提前逐出仍承诺有效的幂等记录。容量必须覆盖 100,000 短流的 open/attach/close 压测；超出保留期限的未知结果不得自动重新提交。业务字节不记录在 operation 日志中。

调用超时不表示操作未执行；SDK MUST 查询 operation.status，不盲目重复修改。崩溃后不确定的操作标记 reconciling，核对持久状态/后端归属后定案。已成功的 connection.open 若因进程重启丢失连接，不以旧 operationId 重新创建：status 应说明原结果现已关闭，用户/应用显式开启新 operation。

查询 limit 默认 50、最大 200；最多 64 个控制请求/调用者并发、128 个数据流/调用者并发、全 Agent 1,024 个数据流。超限 RESOURCE_EXHAUSTED，不无限排队。连接/操作 ID 不替代授权。

### 5.1 公共 DTO 与事件

下列为必须字段；额外兼容字段只能经版本化契约评审加入，不能允许后端任意原始 JSON 穿透。未到阶段的字段使用明确 null 或能力状态，不伪造成功。

| DTO | 字段与语义 |
|---|---|
| HelloResult | apiVersion:int、agentVersion:string、bootId:UUID、capabilities:Capability[]、limits:object；不含任何密钥 |
| Capability | name:string、state:supported/experimental/unsupported、reasonCode:string/null；SDK 只能自动使用 supported，experimental 需用户明确开启 |
| ServiceRef | ownerId、deviceId、publisherAppId、serviceId 四个字符串；cloud 路径中的 ref 为该元组的规范编码 |
| ServiceSummary | serviceRef、label、protocol、health:unknown/transport-reachable/healthy/unavailable、enabled:boolean、revision:uint64字符串 |
| PendingGrantRequestResult（实验） | grantRequestId:UUID、pendingUserApproval:true；没有访问能力 |
| PendingGrantRequestStatus（实验） | grantRequestId:UUID、state:pending/superseded；不是GrantSummary |
| GrantSummary | grantId、applicationId、serviceRef、actions:string[]、state:pending/active/revoked/expired、revision、expiresUtc:string/null |
| OperationResult | operationId、status:pending/succeeded/failed/reconciling；succeeded 另有 result，failed 另有 error；两者互斥，pending/reconciling 不含结果 |
| ConnectionStatus | connectionId、serviceRef、bootId、networkGeneration:uint64字符串、flowState（第8节）、selectedPath/businessPath、directTx/Rx/relayTx/Rx:uint64字符串、complete:boolean、error:Error/null、acceptedUtc/readyUtc/closedUtc:string/null、setupMs:int/null |
| TunnelStatus | tunnelRef、serviceRef、verifiedPeerId:string/null、bootId、networkGeneration、state（第8节）、reasonCode:string/null、leaseRemainingMs:int；历史证据另列，不混入 state |
| Page | items:受限 DTO 数组、nextCursor:string/null，最大 limit 已指定 |
| Error | code、stage、retryable、message、details，按第12节 |

能力名首批为 publish.tcp、visit.tcp、access.brokered、access.loopback、protocol.http、protocol.https、protocol.ws、protocol.wss、policy.direct-first、policy.direct-only、network.ipv4、network.lan、network.ipv6、identity.device-grant、identity.revoke。未知能力可以忽略；缺少被请求的必要能力必须报 CAPABILITY_UNSUPPORTED，不降级成更宽松策略。

事件沿用 envelope 中的 bootId/cursor/eventType/timeUtc/data。eventType 首批为 gateway.state、network.changed、service.health、tunnel.state、connection.state、connection.metrics、grant.changed、device.changed、operation.completed、relay.budget。data 使用相应 DTO 或其带对象标识的增量，不包括秘密；过滤器只缩小当前主体可见范围。cursor 不能转给其他应用读取越权数据。

RPC 的成功响应表示请求被接收或完成的阶段，不一定代表业务成功。connection.open/service.prepare 返回 OperationResult；SDK 通过 operation.status 或完成事件取得其最终 connectionId/tunnelRef，再用对应 status 查询。device.revoke/grant.revoke 的最终 result 包含 revision、onlineClosed:boolean、pendingExpiryCount:int，不能把“已记录撤销”说成全部离线端已确认。

连接策略、应用用户授权、后端能力都必须同时允许才可建立流；DTO 中的引用、UI 显示名称和调用方输入路径均不能替代这三项检查。

## 6. 数据入口和 SDK 合同

### 6.1 受控 brokered 模式

connection.open 成功后在 10 秒内由原调用者 connection.attach。附接为单次，超时回收未使用资源。Android 返回受控流 FD，Windows 返回独立数据管道，绑定同一调用身份并验证单次附接；JSON 中的 streamRef 只是引用，不能复制它就获得访问权。不能从 Binder 返回一个裸公开 TCP 端口后宣称具备数据隔离。

SDK 对外提供 connect、waitReady、openStream、status、events、close 和取消。connect 映射 service.prepare 并返回本机逻辑服务句柄；waitReady 有显式超时并订阅该服务通道状态，不制造业务字节；每次 openStream 映射一组 connection.open → operation.status（若异步）→ connection.attach，得到一条独立 BusinessStream。service.prepare 使用预热预算，explicit connect 不受自动预热默认关闭的限制；禁止用准备操作无限刷新预算。多个流可共享同范围 SessionLease，但不能省略新打开的授权可用性检查。业务对 stream 读写，连接的错误/EOF 按原协议交给应用。应用 HTTP 客户端使用受控 dialer/socket factory；如果该语言 HTTP 库无法保留 authority/SNI 和流语义，应报告 ADAPTER_UNSUPPORTED，不能设置 insecure 模式。

SDK 必须处理 partial read/write、背压、半关闭、正常 EOF、取消和不确定打开结果。每流用户态缓冲目标最多 256 KiB，达到上限背压，不截断、不无限缓存。适配器能力不支持 TCP 半关闭时必须显式失败或保持可证明的等价语义，不静默改成双向立即关闭。

### 6.2 loopback 兼容模式

用户可批准一个本地 TCP 入口，默认动态分配端口，范围由 OS 决定；固定端口可选，占用时返回 PORT_IN_USE，不抢占进程。入口绑定 loopback，停用后必须关闭。端口只代表入口，不是指定应用身份；后端认证仍是必须的边界。

不带认证的任意 TCP 服务不能仅因网关监听 loopback 就默认对其他本机应用安全。首版兼容模式需要用户明确批准，标记“本机其他程序可能访问”，并要求业务保留认证。SDK 模式的授权不能自动覆盖此入口。

HTTPS/WSS 的逻辑地址和证书语义必须保持；若现有客户端不支持分离 dial target 与 logical authority，不承诺零代码适配。HTTP 代理、SOCKS、浏览器扩展等未来能力分别声明，首版不暗中改系统代理、DNS、hosts 或浏览器策略。

## 7. 连接策略与时间预算

| mode | 默认总打开预算 | 默认直连等待 | 回退 |
|---|---:|---:|---|
| fast-connect | 15,000 ms | 1,500 ms | 获得明确授权后可以中继 |
| direct-first | 15,000 ms | 8,000 ms | 超预算仍无直接通道时可中继 |
| direct-only | 15,000 ms | 15,000 ms | 禁止中继，不执行隐式备用 |

默认策略为 direct-first。connectTimeoutMs 范围 1,000–60,000，directWaitMs 范围 0–60,000 且不得大于总预算；缺省等待取模式默认值与总预算的较小者，显式超出则拒绝。direct-only 的等待必须等于总预算且必须显式声明 allowRelay=false；其他模式默认 allowRelay=true，也可被用户或应用设为 false，禁止后不得偷偷回退。规范化填入预热与 null 字节预算默认值，不自动生成中继动作或授权。

公开连接请求必须明确applicationRef，不能在同调用主体的多个逻辑应用间隐式选择。请求策略只可收紧所选已批准import，确切规则见 [ADR0023](adr/0023-connection-request-policy.md)：总预算不增加，不开启被禁止的中继/预热，不提前中继，不取消或增大既有字节限制/警告。独立解析器比较通过仍不等于平台授权或正式API已实现。

预算从 Agent 接受 connection.open 开始，以单调时钟计算，包含授权查询、DNS/STUN、握手和中继建立，不因重试重置。中继授权无法取得时不能借用另一个应用的通道。总预算耗尽返回超时并取消该操作的未完成尝试；不得为取消一个请求杀死其他服务的后端。Adapter 无法隔离取消必须在开发阶段解决，不作为成熟版例外。

prewarm 为可选能力，默认 false；最多 4 个并发预热，每次默认 8 秒，空闲 60 秒后停止保活；用户/电源策略可进一步限制。prewarm 在显式 connection.open 之前完成时可减少等待，但冷启动和预热建立耗时必须分开记录。一次调用不在前台等待 8 秒预热后又隐藏地重置其 15 秒预算。

局域网候选、已验证的直接通道和公网打洞应按成本/策略调度；LAN 候选也必须完成设备和服务认证。NativeReady 仅由经过身份验证的实际通道初始化/探活事件驱动。STUN ready、NAT 历史日志、曾经出现直接字节均不能独自标记 DirectReady。

## 8. 状态机与并发

通道状态、单条业务流、历史证据必须分开建模：

```text
PeerTunnel: idle -> preparing -> direct-ready / relay-ready
                    |               |
                    +-> failed      +-> degraded -> reconnecting -> preparing
任何状态 -> stopping -> stopped

BusinessStream: pending -> attaching -> open -> draining -> closed
                   |                    |
                   +-> failed           +-> failed
```

通道事件至少包含 bootId、networkGeneration、tunnelId、serviceRef、verifiedPeerId、state、reason。DirectReady 的有效性绑定网络代次、租约和探活；网络改变或通道失效必须撤销，不能一直显示历史成功。可能同时存在一个直接通道和一个供老连接使用的中继通道，因此 UI 允许 mixed。

流状态包含 selectedPath=direct/relay/none、businessPath=direct/relay/none、累计字节及启动/就绪/结束时间。零字节 open 可表示传输已选择，businessPath 仍为 none；收到/发送正业务字节后才产生业务直连证据。正在新建的流和已存在的中继流不得混为一个状态。

后台打洞成功后，新连接优先复用直接通道；既有 TCP/WS 保留原路径。首版不透明迁移既有流，故障可关闭并发送应用重连事件，不能偷偷重放登录、文件写入或其他业务正文。

停止、取消、网络变化或替换进程会递增 generation。迟到事件只能更新其原始代次的结束记录，不可使新代次复活。服务配置/授权版本变化也必须触发相应通道失效。

## 9. 生命周期与网络恢复

Windows UI 与 Agent 分离；按用户选择启用登录后运行，首版不默认以 SYSTEM 身份运行。安装驱动不属于网关需求。关闭 UI 不改变已批准的后台运行策略。停止仅回收自有进程句柄/Job，保留其他应用和共享服务。

Android 保留用户可见的前台服务和停止入口，SDK 不能通过隐蔽广播绕过系统启动限制。系统强行停止后的恢复依赖用户重新打开；不承诺进程永久存活，不占用 VPN 槽位。网络回调必须驱动重新评估，而非只在启动时判断。

网络变化在 250 ms 窗口内合并，原尝试取消并建立新代次。Windows 多物理网卡需要用户选择或明确的优先规则；地址/网卡身份改变后重新核验，拒绝随机选取或保留陈旧绑定。Android VPN 不允许绕过时提示所需用户配置，不修改第三方应用或声称强制绕过。

异常恢复采用 1/2/4/8/16/32/60 秒上限的指数退避，加入最多 20% 抖动；每服务每 10 分钟最多 10 次主动重建，之后进入 degraded 并由网络变化、用户操作或下一预算窗口重新评估。原生打洞重试和 Agent 重启必须共用预算，不形成两层重试风暴。健康数据流不能因只失去控制连接而无条件重启，但仍受会话租约限制。

## 10. 会话授权、中继预算与撤销

控制面必须在服务端校验 device/app/service/role 范围，不能将 FRP 共享 token 直接当作逐设备授权。会话授权租约最长 120 秒，默认每 30 秒续期；同范围的已认证 tunnel 可承载多个获准子流，不为每个字节或新 TCP 流逐次增加云端往返。新租约和扩大范围必须获得权威端批准；已有有效租约的子流可以使用受认证控制通道同步的最新已知授权版本。

在线撤销后拒绝新会话，已活跃通道/流目标 5 秒内关闭。控制通道必须有每设备合并的健康探测，失效检测上限 5 秒；确认断联、网络代次改变或健康信息过期后禁止新打开，现有流最多保持至当前单调租约到期加 5 秒调度余量。分区被检测前最多 5 秒存在旧租约下子流准入窗口，不能宣传为网络一断立刻全局拒绝。1.0 不提供主动离线新建会话模式。这一可用性取舍必须在界面解释，不能将“P2P 数据不经过服务器”写成“服务器离线也可无限使用”。

中继预算按应用/服务设置，relayBudgetBytes 为 uint64 十进制字符串或 null；0 表示禁止，null/省略表示无业务配额上限。relayWarningBytes 为独立提醒阈值，存在硬限制时不得大于硬限制。设置有限硬限制必须明确批准 relayBudgetAction=stop-relay，周期为当前授权服务会话累计值，重开子流不重置；跨会话的日/月预算不在首版承诺内。计数口径为本地应用边界 relayTx+relayRx，不能称为云账单计数。达到硬上限后禁止新中继数据准入并关闭相应中继流，必须报告 RELAY_BUDGET_EXCEEDED。不能静默丢弃部分数据并报告成功。大文件续传/写入结果核对由应用负责。提醒阈值与硬限制分别配置，不自动把提醒变为截断。

授权、协议安全、撤销和后端 enforcement 的详细阻断门槛见 [安全规格](SECURITY-SPEC.md)。若 FRP 适配不能在服务端及数据会话真正执行授权与撤销，版本不得宣称达到 0.5/1.0 安全要求。

## 11. 诊断、日志与资源

统计必须按 app/device/service、direct/relay 分组，并区分累计历史、当前活动和最近窗口。字节从应用边界计数，元数据和重传另列；publisher Tx 对应 visitor Rx。累计 uint64 以十进制字符串对外表示，避免 JavaScript 精度丢失。吞吐统一 B/s，时长 ms；日期为 UTC ISO8601，内部预算使用单调时钟。

已结束流应压缩到累计汇总和有界历史，不能像当前实验版达到 8,192 个流后永久失去完整统计。活跃表以实际并发限制，关闭/重复/迟到事件按 flowId+runtimeGeneration 去重。崩溃缺尾样本必须标记 incomplete，不推算缺失字节。重启必须产生新 bootId，持久累计与本代计数分开。

事件日志最多 10,000 条或 24 小时，任一先到即轮转；cursor 失效返回 CURSOR_EXPIRED 及完整快照读取方式。事件不含 token、私钥、业务正文、SDP 或原始候选列表。诊断导出先展示范围并脱敏，详细网络地址只在用户明确允许的本机受限排障记录中保存。

计划容量是首版工程边界，不是已经验证的性能：单设备至少 16 个服务、单 Agent 1,024 个并发流上限；运行资源必须按 [验收规格](ACCEPTANCE.md) 实测。配置/授权/持久统计写入失败必须显示状态，不将内存更新冒充持久成功。

## 12. 错误合同

error 固定字段 code、stage、retryable、message、details；message 不含原始后端错误或凭据。details 仅允许脱敏的关联 ID、预算和范围，不携带原始配置。code 至少覆盖：

| 错误组 | code |
|---|---|
| 契约 | INVALID_REQUEST、VERSION_UNSUPPORTED、CAPABILITY_UNSUPPORTED、ADAPTER_UNSUPPORTED |
| 权限 | AUTH_REQUIRED、PERMISSION_DENIED、APPROVAL_REQUIRED、DEVICE_REVOKED、GRANT_EXPIRED |
| 服务 | DEVICE_OFFLINE、SERVICE_NOT_FOUND、SERVICE_UNAVAILABLE、PORT_IN_USE |
| 网络 | NETWORK_UNAVAILABLE、NETWORK_CHANGED、STUN_UNAVAILABLE、DIRECT_TIMEOUT、RELAY_UNAVAILABLE |
| 安全 | PEER_IDENTITY_MISMATCH、TLS_VALIDATION_FAILED、LEASE_EXPIRED |
| 预算/生命周期 | CONNECT_TIMEOUT、RELAY_BUDGET_EXCEEDED、CANCELLED、RESOURCE_EXHAUSTED |
| 操作/事件 | OPERATION_CONFLICT、OPERATION_UNKNOWN、CONNECTION_CLOSED、CURSOR_EXPIRED、STORAGE_UNAVAILABLE |

stage 为 validation/authorization/discovery/direct/relay/attach/runtime/storage。retryable 表示允许显式发起新尝试的条件，不授权 SDK 自动重放业务；NETWORK_CHANGED 等瞬态错误也必须保留操作状态核对。TLS 或身份错误不得降级关闭校验后重试。

## 13. 兼容、迁移与发布

旧 formatVersion=1 的共享 profile 只能导入为标记明确的 legacy trusted-pair 配置，不能自动制造新设备身份或宣称具备撤销。升级需备份受限状态、验证新 schema/权限、原子提交；失败回滚到原状态。降级不得把新凭据导出为旧共享 token 或自动恢复已撤销设备。

0.3 可以在受限开发模式复用旧后端，以完成外部应用样例；必须公开缺少的安全能力，不对不受信任应用开放。安全模型与 API 从 0.3 起固定方向，0.5 才完成设备授权并不允许此前先做无限制公共控制面。

构建必须固定上游 commit/补丁、通过干净 checkout 重现、跨平台协议测试、摘要锁与签名验证。Windows 正式签名、Android 持久签名、签名更新元数据、兼容升级/回滚、依赖与许可清单均为发布门槛。开发签名/便携文件可用于测试，不等于正式升级通道。

SDK/Agent 的许可证及第三方依赖范围需发布前确定；当前仓库许可证不能被其他文档默认为 Apache。首版不自动修改防病毒设置、不通过改名重打包规避检测。

## 14. 完成定义与设计变更

每个里程碑按 [产品计划](PRODUCT-PLAN.md) 和 [验收表](ACCEPTANCE.md) 交付；所有 planned、experimental、unsupported 能力必须能被 hello 与 UI 一致表达。至少两个独立应用实际接入、四场景及故障矩阵、24/72 小时稳定性、权限/撤销/升级回归均通过后才能发布 1.0。

尚需实现阶段验证的技术关口：FRP 服务端细粒度授权和活跃流撤销、跨平台私有 broker stream 语义、Android 合规后台恢复、原生 IPv6 兼容，以及发布签名/许可决策。这些不降低本文已定义的安全要求；关口不通过时须调整实现/范围并记录 ADR，不用文档把未验证功能改写成已支持。

## 15. 云端控制接口与部署边界

这是拟议的新控制面，不是现有 frps 已有的 API。frps 继续负责后端协议，控制 API 负责设备和授权；二者可以部署在同一 ECS，但应有独立配置、健康检查、版本和回滚边界。不得将本机 Broker 直接暴露到公网。

控制 API 使用 HTTPS，已注册设备使用 mTLS/成熟认证库及固定的设备身份；所有者首次初始化和控制信任通过受限管理通道完成。路径按 `/v1/` 版本化，JSON 最大 64 KiB；修改请求携带 Idempotency-Key=operationId，并返回请求关联 ID、revision 及确定/未确定状态。重放冲突、失效或越权必须拒绝。全局证书信任轮换由管理 UI/安装维护工具的受保护事务执行，不开放给普通 SDK。

| 路由 | 主体与用途 |
|---|---|
| GET /v1/capabilities | 最小版本/能力信息，不泄漏设备和服务列表 |
| POST /v1/pairings | 管理者创建短时一次性邀请 |
| POST /v1/pairings/{id}/claims | 候选证明邀请与设备私钥持有；秘密只进受保护请求正文，不进 URL/log |
| POST /v1/pairings/{id}/approve 或 cancel | 管理者确认指纹或取消；并发状态转换原子 |
| GET /v1/devices 与 GET /v1/services | 按已验证主体与 grant 过滤的目录 |
| POST /v1/services/{ref}/publish 或 unpublish | 发布设备及本机已批准的服务范围，拒绝任意目标 |
| POST /v1/grants 与 POST /v1/grants/{id}/revoke | 所有者批准/撤销访问范围；普通主体只能申请或放弃自己的 grant |
| POST /v1/devices/{id}/revoke 或 rotate | 所有者撤销、批准受证明的新设备公钥；不接收私钥 |
| POST /v1/sessions | 两端设备/应用/服务范围授权，返回受认证的短时租约 |
| POST /v1/sessions/{id}/renew | 校验授权/撤销版本后续期，不能延长已撤销会话 |
| GET /v1/events | 经认证的撤销、目录和租约增量；断开后按 cursor 同步，失效则读快照 |
| GET /v1/operations/{operationId} | 原主体核对未知结果，不重提写入 |

{ref} 是经编码的完整服务引用，不接受客户端拼成其他 namespace；应用主体来自可信 Agent 的本机校验，服务器独立检查该设备/服务授权。配对设备被恶意代码完全控制不属于逐应用本机隔离保证。云端 HTTP 状态建议 400（契约）、401/403（身份/权限）、409（状态/幂等冲突）、410（邀请/租约失效）、429（限额）、503（暂不可用）；详细 code 沿用第 12 节，认证失败不返回敏感存在性差异。

服务注册、配对声明和会话申请默认每设备最多 10 次/秒、突发 20；邀请失败同源每分钟最多 5 次；另有全局并发/存储上限并以 429 返回 retryAfterMs，不作无限分配。部署前需测合法并发并冻结服务器容量，不把默认限额描述成实测吞吐。元数据持久化须事务化、备份和版本迁移；单节点首版不承诺高可用。健康探测不可泄露注册 token 或目录。

公开 API 字段编码、认证证书 profile、授权租约标准编码及测试向量必须在 0.3 后端可行性 ADR 中细化并评审，再用于实现；本节冻结职责与行为边界，不声称已交付完整密码协议。部署使用配置化 TLS origin，新增入口需单独核准；不复用或覆盖 ReDesk 的密钥、Nginx 路由和端口。
