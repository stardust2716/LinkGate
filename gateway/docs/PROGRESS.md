# LinkGate 开发进度与证据

## 最新：手机界面与旧版退役

Android已更新为 **0.3.0-preview.3 / versionCode4**，名称和通知统一为LinkGate。四个页面为概览、设备、应用、设置；原证书/签名/精确授权和恢复机制保留。Windows仍为0.3.0-preview.2。用户确认新版启动、原配置正常。

[界面与清理说明](../release/MOBILE-UI.md)、[证据](verification-v0.3-ui-retirement.json)：自有模拟器检查布局、放大字体和宽屏，物理手机只安装/读取状态，由用户操作画面。旧安卓包、旧linkgate-frps服务/程序/用户和17400规则已退役；12个旧便携/发行目录已移入回收站。私有恢复材料与新版身份保留，ReDesk/RustDesk/STUN未停用。发行材料统一在dist/LinkGate-v0.3.0-preview.3。

以下为先前阶段记录；其中“旧版保留”仅描述退役授权之前的状态。后续仍以接入首个业务应用为目标，不扩展传输功能。


更新时间：2026-10-30。首个可安装试用版 **0.3.0-preview.1 已交付**，现有安装运行版仍为0.2.3。后续实际部署仅补齐阻断使用的WAN修正，版本更新为0.3.0-preview.2；其余生产1.0功能保持在后续spec。

## 最新：实际部署与WAN修正

0.3.0-preview.2已部署，见[部署说明](../release/DEPLOYMENT.md)与[证据](verification-v0.3-deployment.json)。ECS新增独立低权限服务，Windows安装升级与身份保留、Android同签名升级及用户手动启动通过。两个明确STUN端点探测通过；原0.2.3/ReDesk保持运行。Windows当前隐藏后台运行，无开机任务。

真实ECS带宽环境暴露2秒心跳、短FIN排空等待及示例闲置控制管道复用问题；修复后强制中继2MiB完整传输约4.1秒，精确计数、摘要和FIN通过。相关安全流42项通过，撤权独立检查保持。失败与分阶段修复均保留。仅Windows独立进程经真实ECS，不称手机P2P已验收。首个真实业务应用及所有后续规格仍开放。

## 前序：首次安装试用版

[范围](../release/TRIAL-SCOPE.md)、[快速开始](../release/README-template.md)和[后续spec](mature-release.sdd)明确区分本版与完整1.0。Windows x64安装包自带运行时，Android访问APK包含ARM64/x86_64原生库；另提供Linux amd64认证服务器。Windows当前为启动入口加命令行管理，未自动升级0.2.3或变更ECS。

[安装验收](verification-v0.3-first-installable.json)：真实安装后的Agent、Owner及独立SDK应用，使用隔离Linux认证服务器，完成配置、权限发放、原操作查询、直连/中继各2MiB、摘要/FIN/路径校验、服务器撤权拒绝新连接、停止回收及卸载身份保留。首次环境准备漏目录与affected=0误判都保留失败记录，修复后完整流程通过。APK签名/版本/ABI和嵌入运行时摘要核对通过；本轮不操作物理手机，也未做长稳或四网络矩阵。

[ADR0028](adr/0028-scoped-windows-runtime.md)及[动态范围证据](verification-v0.3-scoped-windows-runtime.json)记录按scope启动和复用发布/访问运行时、有限连接预算、Owner工作流与持续serve。23项运行时所有权、130项范围/Owner检查通过，实际后端SDK66项、9条业务连接和三种路径各64MiB通过。保留并修复首个空日志清理失败。本次打包复用前序Android47项及共享400例证据，不重复跑全套。

以下为前序阶段的历史记录；其中“下一步”仅代表当时状态，当前任务以[HANDOFF](HANDOFF.md)及后续spec为准。

## 前序：Windows 公开 RPC 和 .NET SDK 实际传输

[ADR0027](adr/0027-windows-public-connections.md) 记录Windows实验connection.open/status/attach/close与typed SDK组合。它执行精确本地visitor授权、服务器租约、内层设备mTLS及一次性进程固定管道；使用所有者配置的直接目标/自有中继路由，尚未自动发现任意新scope的运行时。

[冻结证据](verification-v0.3-windows-public-connection.json)：Windows548项回归、跨语言400例通过；Android隔离原生47项再次通过；Windows-only实际后端45项及正式SDK64项通过。三种路径各64 MiB、精确业务计数、原成功结果、单次附接、独立取消、注入网络代次和本地访问撤销通过。修复并保留了首次代次测试中取消源被重入释放的失败记录，确定性回归证明释放顺序正确。

这些Windows SDK检查使用真实IPC但同进程消费者，独立Windows应用、动态Windows候选和物理网络观察仍开放。服务prepare/事件/兼容入口、所有者授权工作流、生产运维/轮换、签名安装更新与四场景/长稳验收继续阻断完整1.0发布。Android为独立UID/签名的模拟器证据；安装0.2.3、ECS和ReDesk未更改。

## 前序：Windows 连接日志与业务计量

schema8补齐Windows持久连接日志、独立打开结果、一次性附接声明和计量。批准、策略比较和打开记录原子提交；原操作查询不会重拨，ready后发生关闭/重启仍保留原打开成功，就绪前故障则失败。旧boot活动记录分批恢复为不完整终止，计数和选择路径保留；过期快照不能覆盖终态。

Windows537项通过，新增37项覆盖计量、完整FIN、uint64溢出、时钟回退、精确授权、附接、回滚、重启及两类流合并上限。联调另修复合并快照跨过open时不能保存的问题：两端允许向前跳状态，仍拒绝倒退；半关闭阶段的已知字节能在重启前落盘。实际认证后端/Android原生SDK47项再次通过，见 [冻结证据](verification-v0.3-windows-connection-journal.json)。

这是Windows正式执行通道的持久层和计量基础；正式RPC/SDK、动态候选及所有者工作流仍待组合，不能将日志单测当成Windows正式业务已交付。

## 前序：跨语言连接状态和操作结果

.NET新增不可变ConnectionStatus/ConnectionOperation，与Java共享400例（本轮126例状态/操作输入）及结构化输出比较。两端一致拒绝路径/字节/完成状态矛盾、uint64溢出、错误反射、关联错号、重复字段及非规范时间；日期不接受24点归一化、闰秒或year0000。输入和返回wire快照不能修改保留值。Android注册/计量/SDK单测回归通过，见 [冻结证据](verification-v0.3-status-contract.json)。这补齐Windows正式API所需DTO，尚未交付其执行通道。

## 前序：Windows 精确本地访问授权

[ADR0026](adr/0026-windows-local-access.md) 与 [冻结证据](verification-v0.3-windows-local-access.json) 记录schema7的可信宿主授权。权限绑定真实调用者、applicationRef、importAlias及精确远端设备/应用/服务；pending请求和声明审核不会自动授权。批准、撤销及结果保留在同一事务，拒绝新pending不误撤销旧权限，明确重新批准或peer换代使旧scope失效。publisher/visitor合计4096条，均预留撤销日志空间。

Windows500项通过，新增39项涵盖范围/策略、持久重放、并发双库、回滚、满额度撤销、损坏行及schema6迁移。实际认证后端和Android原生SDK47项再次通过，三种正式路径各64 MiB。新授权基础尚未接入正式Windows连接API及所有者操作界面，这两项仍是下一步。

## 前序：Android 公开连接生命周期与 SDK

[ADR0024](adr/0024-public-connection-lifecycle.md) 与 [冻结证据](verification-v0.3-public-connection.json) 记录实际 `connection.open/status/attach/close`、`operation.status` 和一次性 Binder 描述符。原操作号不重拨，成功打开的结果与之后变化的状态分开保存；SQLite schema1→2原子迁移。状态区分选择路径和实际业务路径，累计业务字节，只有双向FIN和排空才标complete；异常和重启保留已知计数并标不完整。

独立UID/签名的消费者通过公开SDK，在正式直连、中继和自动选路下各传输64 MiB，摘要、精确33字节发送/67,108,864字节接收及单次附接通过。强制失效LAN目标后自动选路仍为实际XTCP；两条实际直连取消一条保留另一条。注入网络代次变化关闭旧流，新操作在更高代次建立；这是模拟器注入测试，尚未验收物理Wi-Fi/移动网络切换。

[ADR0025](adr/0025-owned-xtcp-carrier.md) 保留首次自动传输截断的失败记录并说明共享载体修复：迟到的旧隧道失败不得关闭新隧道，单流取消不关闭共用载体。固定后端166项及四项确定性竞态测试通过。可靠PFD与Socket适配器把异常中断传给应用，不能当作干净FIN。Windows既有461项、共享.NET/Java274例回归通过。

Android hello仅声明显式实验 `connection.api`，不把它当成完整支持。Windows正式API、动态运行时和本地访问授权组合、服务发布/事件、运维/轮换、签名安装更新回滚与长期真机验收仍开放。安装版0.2.3、ECS和ReDesk未改变。接入方式见 [SDK指南](../android/client-streams/README.md)。

## 前序：公开连接参数与策略收紧合同

[ADR0023](adr/0023-connection-request-policy.md) 明确同调用者多应用时必须传applicationRef，新增.NET/Java不可变连接参数及策略比较。请求不能开被禁止的中继/预热、提前中继、加大总预算或取消/增大批准的字节上限与警告。原始策略的空白和转义也计入8KiB，两端不因重新序列化产生不同限制。

[冻结证据](verification-v0.3-connection-request.json)：共享274例两端一致，其中65例新连接参数/范围比较；Windows Broker461项及Java传输/注册/SDK回归通过。比较通过只代表参数不扩大批准策略，实际平台归属、grant、持久连接状态、数据附接及正式公开分发仍待组合。hello没有提前声明正式业务能力已支持。

## 前序：持久化认证服务器入口

[ADR0022](adr/0022-protected-server-entry.md)、[使用说明](../control-plane/serverhost/README.md) 与 [冻结证据](verification-v0.3-server-host.json) 记录 `linkgate-server request/init/serve`。服务器本地产生私钥/公开CSR，由所有者签发；显式初始化创建权限库与备份树外的独立见证，普通启动绝不补建缺失见证。当前仅支持Linux私有目录与文件，拒绝链接、错误权限、身份替换及重复初始化，运行检查失败关闭两个监听。

实际Linux10项宿主测试、5项服务器测试通过，包含真实所有者mTLS、拒绝未配对设备和旧FRP输入、安全停止/解锁。固定后端162项及vet、依赖校验通过；两个新派生目录的未改变frpc/fixture哈希一致。公开管理协议已经存在，友好远程管理CLI、轮换/部署/迁移/回滚仍未完成；本入口没有上传ECS或更新安装版。

## 前序：受认证候选调度与 Android 实际 XTCP

[ADR0021](adr/0021-authenticated-connection-planning.md) 和 [冻结证据](verification-v0.3-connection-planner.json) 记录本轮实现。Java/.NET 调度器把存储、授权、运行时和设备握手计入原始总预算；认证失败不降级，取消回收部分和晚到资源，已选流不重播。Android 已组合短 LAN 候选与禁用中继回退的 XTCP，并在精确 scope 运行时池内配置被授权的 visitor。

独立 Android SDK 应用实际 XTCP 与显式中继各传输64 MiB，摘要、FIN、单次附接通过；同 scope 两条流共用一个运行时，取消一条保留另一条。Android 的实际直接候选由所有者聚合计数确认为 XTCP，LAN 为0；这是模拟器证据，不能当作手机移动网络验收。Windows完整 Broker/SDK461项、Java调度10项及本轮实际后端47项通过。两端实验入口拒绝尚未实现的预热和中继字节限制，避免声明约束被忽略。

完整1.0仍需正式公开连接API/SDK、Windows动态候选与运行时组合、网络代次恢复和业务计量、生产服务器/设备管理工具、安装更新回滚，以及新版本四场景真机、10万短流和24/72小时验收。根任务#2/#3/#4保持开放；本轮未升级安装版、访问ECS或操作物理手机。

## 正在开发：新传输控制服务

[control-plane](../control-plane/README.md) 已提供真实本地mTLS/HTTP服务库：身份来自握手，设备角色、精确服务grant和管理操作彼此分开，旧审批操作不重做。标准ES256 JWS/JWT绑定设备/应用/服务及代次、最长120秒；单调期限和序列防止重放延长授权。独立持久witness在权限库备份恢复、确认丢失和文件替换时选择安全恢复或拒绝准入；这不等于硬件反回滚，也不保证一起恢复两个管理数据库仍可检测。

Go当前22个顶层测试及vet通过，含真实TLS管理/申请/发布端验证、保持连接后的撤销、错误身份、严格消息、额度满时撤销、并发版本、事务确认及恢复故障。修复了写事务内依赖旧统计计数会放宽日志限额的问题，管理操作数改为事务内持久计数并在启动时核对。源码和依赖固定，未部署服务，也未连接运行版FRP。设计与后续关口见 [ADR0015](adr/0015-authenticated-transport-control.md)；FRP实际路径、跨语言验证、私有运行时桥、控制健康及活动直连/中继撤销仍开放。

## 当前可运行：Android 原生安全接入

[ADR0014](adr/0014-android-native-authority.md) 和 [Android Agent](../android/agent/README.md) 补齐实际 Keystore 身份、私有 SQLite、真实 Binder 主体、所有者审核/精确访问批准、独立前台服务及 [薄流 SDK](../android/client-streams/README.md)。`agent-core`/`transport-core` 便携实现分别复用真实 SQLite 和 Windows 协议，消费者没有 Agent、设备私钥或 FRP 依赖。Android 新库为独立 schema1，Windows 为 schema6。

[可复现原生验收](../tests/android-native/README.md) 在单独 ADB 端口、禁用物理 USB 发现的 API36 x86_64 无界面模拟器执行。消费者 UID `10156`、Agent UID `10154`，签名确实不同；错误 Agent signer、缺少本地授权及缺少发布端 grant 均拒绝。授权后 64MiB SHA-256 与请求半关闭通过，PFD 一次性附接通过；撤销本地授权后阻塞业务流 **684ms** 内关闭，Windows 后端连接数/业务字节核对为2及67,108,864。机器摘要见 [verification-v0.3-android-native.json](verification-v0.3-android-native.json)。这些是模拟器结果，不是 ARM64 手机、厂商后台、NAT 或四网络验收。

原生测试定位并修复 Keystore 未允许 TLS 预摘要签名的问题；固定格式 Parcelable 正确传播返回值 FD 所有权；已知附接拒绝不再误关整个 SDK 会话。Socket 关闭立即回收底层描述符，输出 FIN 仍可接收完整响应。Windows 审核新增 authority epoch，使拒绝新 pending 不误撤销原 grant/流；schema5→6 迁移保留原有效和无效 stamp，旧提案仍会过期。发布端另将握手/连接/会话上限分开原子管理，认证拒绝返回固定原因而不是笼统离线。

Windows Broker/SDK 当前 **432项**通过，包含128条并发认证控制/数据连接、超额拒绝且不触达后端、整批阻塞业务和后端撤销关闭；Java 核心真实SQLite13项、传输5项、SDK新增4项、Socket生命周期3项均通过。自有进程启动器9项实际 Windows 测试覆盖标准输入、参数保真、错误哈希、进程树回收和所有者崩溃。公开 .NET/Java 各209项语料再次通过。新权限系统接入 FRP/NAT/中继、完整配对/续期/反回滚、正式签名安装/更新及长期测试继续开放，根任务不标完整完成。安装版0.2.3、ECS、默认路由、手机和ReDesk未改变。

## 已有可运行：Windows 安全直连接入预览

设备认证、权限发放和真实数据通道是应用接入的必要条件，不能延期为可选项。本轮新增 [可运行示例与所有者配置说明](../examples/secure-direct/README.md)、[ADR0013](adr/0013-secure-direct-streams.md)，为这三项交付了 **Windows/.NET、已知可达 IPv4、brokered / direct-only** 的开发预览。

- 本机生成P-256私钥/CSR，所有者签发设备证书；双向TLS校验私有根、有效期、用途、名称和批准的设备公钥。不安装系统根证书，普通应用不持有设备密钥。
- 发布端grant绑定精确设备、客户端应用、已审核发布应用和固定export。审批/撤销使用所有者独立入口、版本比较和持久操作号；旧pending申请不自动授权。保留每个活动身份/grant的撤销日志额度。
- 独立控制/数据TLS通道与一次性附接；本机数据管道核验真实调用范围，SDK发送附接字节之前固定同一可信Agent进程。16KiB帧支持部分IO、背压、双向FIN/确认，失效时不重播业务。修复半关闭后再次GetStream及终止标记缓冲丢失的问题。
- 打开前记录操作；重复操作不重拨，Agent重启后原打开记录保持关闭。独占状态锁防止第二个宿主回收第一个宿主的连接。批量权限监控覆盖阻塞IO，撤销后拒绝新连接并关闭旧连接。

复现命令：`./gateway/examples/secure-direct/verify.ps1`。实际启动两个Agent和两个SDK应用进程：未发grant时拒绝；授权后完成 **67,108,864字节文件SHA-256校验**、28字节WebSocket回显、TCP半关闭；没有应用bearer token仍返回401，HTTPS域名与证书校验保留。独立应用依赖清单不含Broker、SQLite、FRP或设备私钥。撤销files后活动WebSocket **425ms** 内关闭，后续files打开拒绝，raw服务继续工作。命令行签发、安装、信任、历史结果查询和撤销也通过。原始机器摘要：忽略的 `artifacts/linkgate-secure-direct-summary.json`。

Windows Broker/SDK套件当前 **417项**通过，新增错误根/公钥/未知设备、服务作用域、撤销、一次性票据、阻塞IO取消、时钟回退、操作冲突/隔离、重复打开和重启恢复等验证。SpecDD当前 **88份规格，0 errors / 0 warnings**。共享.NET/Java合同各209项通过，规范化结果一致；旧独立注册样例的重启查询和无服务端依赖检查通过。3份schema、4个示例和10个反例通过，秘密/源码边界与diff检查通过。可提交的机器摘要见 [verification-v0.3-secure-direct.json](verification-v0.3-secure-direct.json)。

**边界：** 上述为同机独立进程实际传输，不是物理两设备或NAT穿透验收。FRP受新权限约束的后端、Android数据SDK、跨网络测试、证书续期/轮换、备份反回滚、签名安装、长期负载和完整0.3/1.0仍开放。安装版0.2.3、ECS、全局路由和手机未改变，ReDesk未接入。下文381项及更早计数记录对应历史工作包，不能解释为本预览的完成范围。

## 权威任务与当前工作包

局部任务状态以各 `.sdd` 的 Tasks 为准，本表只索引工作范围和证据。整个 0.3 必须满足 [验收规格](ACCEPTANCE.md)，不能将其中一个基础工作包算成版本完成。

| 工作范围 | 规格与结果 | 范围边界 |
|---|---|---|
| SpecDD 治理 | [根规格](../gateway.sdd)、[工作流程](SPEC-DD.md)，官方框架 1.6、CLI 1.2.0，无额外插件 | 仅 gateway 内容根，不改 ReDesk |
| 公开合同基础 | [合同规格](../contracts/contracts.sdd)，.NET/Java 共用 209 个输入 | 只解析/规范化，不等于已连接应用 |
| .NET 实现 | [局部规格](../contracts/dotnet/dotnet.sdd)，便携 .NET10 库与 runner | 不改 Windows 旧 Profile |
| Java 实现 | [局部规格](../contracts/java/java.sdd)，便携 Java17 合同、待批准事务核心与 runner | 开发Binder/SDK已复用；不改 Android 旧 Profile |
| 后端安全可行性 | [ADR0002](adr/0002-frp-authorization-boundary.md)，源码核查已完成 | 准入扩展、会话租约和直接/中继活跃流撤销尚未实现 |

### 本工作包的检查结果

- .NET 与 Java 均通过 **209/209**：36 个有效、173 个无效输入；两端规范化 JSON 结构完全一致。
- 额外验证只读集合/模型、输入复制及校验错误不回显输入或原始解析异常。
- SpecDD：公开合同工作包当时15个规格；加入 Windows Broker/SDK、独立样例及 Android Binder/SDK/待批准注册实验、注册SDK、Windows宿主审核、撤销预留、本地审核命令行和待确认权限申请后共76个，0 errors / 0 warnings，无启用插件。3 个 JSON schema、4 个示例及 10 个形状反例检查通过；这些不代替运行时语义语料。
- Windows 旧版 80 项检查重新执行通过。Android 旧版 `testDebugUnitTest` 任务成功且命中有效缓存，既有 20 项 JVM 测试报告无失败；未声称重新执行手机实机测试。
- 仓库秘密/源码边界与 diff 检查通过。机器摘要见 [verification-v0.3-contracts.json](verification-v0.3-contracts.json)。
- 未进行设备安装、ECS 改动、网关真实业务、跨 UID 或手机 UI 测试；这些不是本工作包的完成声明。

可重现入口为 [contracts/verify.ps1](../contracts/verify.ps1)，共享数据为 [cases.json](../contracts/vectors/cases.json)。原始输出默认保存在被忽略的 `artifacts/linkgate-contracts/`；输入和期望结果随源码提交，不依赖个人配置。

## 接下来的 0.3 工作

### Windows 只读控制管道基础（2026-10-29）

已完成 [本地工作包](../windows/LinkGate.Broker/LinkGate.Broker.sdd)：原生 Named Pipe 使用当前用户受保护 DACL、首实例占位保护和远程客户端拒绝标志；读取后以实际模拟 SID 核验当前用户。严格64KiB帧、重复键/Unicode/深度/值数限制、两秒读写预算及32次请求上限已实现。`hello` 返回开发版本、bootId、限制和能力表，所有业务能力明确 unsupported。

验证命令：`dotnet run --project gateway/windows/LinkGate.Broker.Tests -c Release`。本机73项检查通过，修复断连复用及末帧丢弃竞态后，额外连续10轮全部通过。包含真实管道、碎片化传输、占位冲突、超时回收、后续客户端恢复及取消；旧版 Windows 80项、两端各209项契约回归通过。其他 Windows 用户/远程机器拒绝尚未在多主体环境实测，客户端对伪 Broker 的持有进程核验、并发公平性也未交付。因此没有将 AUTH-01/02 或整个 Broker 标记通过。原生标志依据见 [实现说明](../windows/LinkGate.Broker/README.md)。

本工作包只提供开发库和隔离测试，未接入旧版 Agent，尚无薄 SDK、注册、用户授权或数据流。安装版仍为0.2.3。

### Windows 客户端核验服务端进程（2026-10-29）

[客户端规格](../windows/LinkGate.Client/VerifiedPipeClient.sdd) 要求可信启动方提供持有的进程句柄，客户端复制后保持，使用 OS 管道服务端 PID、创建时间和存活状态核对对端。核验发生在发送任何请求字节前，每次 hello 前再检查；错误后关闭，不按名字或 PID 自动重连。共用严格 JSON 解码并校验响应关联与 result/error 互斥，畸形响应不回显原始内容。源文件后续迁入独立 SDK，见下方。

[独立进程测试](../windows/LinkGate.Broker.Tests/PeerTests.sdd) 覆盖合法子进程、相同程序启动的错误服务端（确认收到零请求字节）、句柄副本生命周期、退出进程、取消、非本机/非规范名称及重复键响应拒绝。还复现并修复了客户端在 native accept 前退出时监听器无法继续接入的问题。

本轮同一 Broker 测试命令共89项通过，最终实现额外连续5轮全部通过。Windows旧版80项、.NET/Java各209项契约回归通过；23份 SpecDD规格、schema/示例/链接及仓库检查通过。

这只完成了 AUTH-02 中已有可信进程句柄的本地路径；尚无安装二进制校验、自动发现/启动器，未强制制造系统 PID 重用，也未执行其他用户与远程机器测试。完整 AUTH-01/02、Broker 和0.3继续保持未验收。此工作包结束时尚未启用应用注册，后续待批准注册进展见下方；授权 UI 仍未实现。

根规格任务 #2 仍未完成：Windows 受认证 Named Pipe Broker、Android 按 UID/签名核验的 Binder、用户授权和私有数据通道、薄 SDK、独立应用样例及接入验收。

### Windows 待批准注册与持久幂等（2026-10-29）

新增 [注册存储规格](../windows/LinkGate.Broker/RegistrationStore.sdd)、[调用主体规格](../windows/LinkGate.Broker/CallerIdentity.sdd) 和 [ADR0003](adr/0003-pending-registration.md)。显式开启独立开发数据库后，`app.register` 按共享 manifest 合同校验，绑定实际 SID/持有的客户端进程路径，将 pending 声明与成功操作结果在 SQLite 事务中提交，始终 approvalRequired=true。`operation.status` 限当前主体；同操作同参返回原结果，改参冲突，重试旧操作不覆盖新声明。容量满拒绝新增但保留旧结果；失败不留下部分注册。

验证命令沿用 Broker.Tests，覆盖数据库对象重建、独立进程重启重试、杀死持有未提交事务的自有子进程后恢复、并发重试、容量、参数伪造、事务故障注入和真实管道调用。另修复 native malformed JSON 请求触发的异常没有被连接边界正确处理的问题，后续合法客户端可继续使用。

本轮128项检查通过，最终逻辑额外连续3轮通过；原有 Windows 80项、.NET/Java各209项契约回归通过。26份 SpecDD规格、schema/示例/链接、NuGet locked restore及仓库边界检查通过。测试数据和子进程已回收。

默认宿主仍只读，新注册模式未接入安装版。没有批准 UI、grant、发布或数据流；路径主体不证明二进制身份，解释器/宿主内应用共用主体。未执行真实磁盘满/断电、100000短流或完整 AUTH-16 发布验收。下一步为带幂等查询的薄 SDK、批准/身份版本化与 Android Binder。

### Windows 独立薄 SDK 与注册示例（2026-10-29）

已提供独立 [LinkGate.Client](../windows/LinkGate.Client/README.md)，仅依赖公开合同和新增便携 LinkGate.Control，不带 SQLite、Broker、FRP 或配对配置。未发布的客户端命名空间从 LinkGate.Broker 移到 LinkGate.Client，详见 [ADR0004](adr/0004-pending-sdk.md)。帧与严格 JSON 实现下沉到便携模块，Broker 兼容入口保持原行为。

SDK 封装 HelloAsync、RegisterPendingAsync、GetOperationStatusAsync。实验注册必须协商并显式开启，操作号由应用事先持久保存；达到协商的单连接请求上限则本地拒绝写入。服务端错误仅暴露固定消息的 code/stage/retryable。修改写出后发生丢回复、超时或坏响应，关闭连接并报告带原 ID 的 MutationOutcomeUnknownException，调用者重新核验连接后显式查询，不自动重发。

[独立示例](../examples/dotnet-registration/README.md) 用已信任的本地开发构建目录启动自有 Broker，执行注册及另一次启动后的 query-only 查询。`examples/dotnet-registration/verify.ps1` 验证成功且消费者产物无服务端/SQLite 依赖；原始摘要在忽略的 artifacts/sdk-registration/<runId>/summary.json。EXE 摘要仅防误选，不能验证全部托管/原生依赖；不是可信安装器。

本工作包仍只覆盖注册/查询。SDK 故障测试证明写入后断连、超时、错误 approvalRequired 响应均不会触发第二次修改，显式查询能找回已提交结果。没有跨设备文件/WebSocket业务、批准 UI、grant、Android Binder 或数据流验收，完整0.3继续未完成。

验证：Broker/SDK共150项通过，最终逻辑额外连续2轮通过；独立样例注册及重启后 query-only 检查通过，样例消费者目录确认无服务端或原生传输依赖。Windows旧版80项、.NET/Java各209项回归通过；34份 SpecDD规格、schema/示例/链接与 NuGet locked restore 检查通过。没有安装设备或修改云端/运行版。

在扩大运行接入前，先冻结本机调用身份与同用户边界、幂等状态和数据附接语义。后端身份/授权/撤销按 ADR0002 做最小原型，不能用客户端隐藏 UI 替代服务端执行。当前合同代码不授权任何设备或应用，也不改变现有连接策略。

### Android 独立只读 Binder（2026-10-29）

新增 [broker-dev](../android/broker-dev/README.md) 和 [ADR0005](adr/0005-android-binder-bootstrap.md)。独立开发包不改旧版GatewayService，仅提供显式绑定的AIDL hello：系统UID/用户资料同步捕获，当前单包签名/安装身份快照复核，非阻塞每UID8/全局32调用限额，错误固定脱敏。拒绝共享UID和跨资料，所有注册/授权/业务能力保持unsupported。

便携Java [ControlHello](../contracts/java/src/main/java/dev/linkgate/contracts/ControlHello.sdd) 复用严格JSON解析，验证64KiB、重复键、坏编码、整数词法、UUID和参数白名单。开发APK编译成功，新增10项JVM测试实际执行通过；旧版Android `:app:testDebugUnitTest` 成功但命中缓存，既有20项报告无失败。Windows Broker/SDK150项、.NET/Java各209项语料回归通过。42份规格语法检查通过。

原生Binder、跨UID/工作资料、PackageManager可见性、签名轮换/重装/共享UID完整集仍未在设备上验证，AUTH-03/04保持未验收。本轮没有安装手机、操作画面或修改运行版/云端。Android待批准注册、客户端SDK、用户授权和数据FD仍待实现。

0.4 可靠连接、0.5 身份与运维、1.0 发布仍按 [产品计划](PRODUCT-PLAN.md) 保持开放。

### Android 签名固定只读 SDK（2026-10-29）

新增独立 [client AAR](../android/client/README.md) 与 [ADR0006](adr/0006-android-client.md)。共享AIDL移入control-ipc，便携合同通过public-contracts构建桥接；Broker与SDK不互相依赖，也未改旧版app。

bindAsync使用操作者可信组件和精确当前签名集合，校验包UID、首次安装/更新、启用/导出状态及AIDL descriptor；绑定回调后和hello前后复核身份，拒绝共享UID，不自动信任签名历史。断连/死亡/取消/超时后关闭，不重绑。SDK进程级最多2工作线程、无工作队列、1定时线程、8客户端；两秒future超时不被当作原生Binder已中止。原生平台调用永久阻塞时保持有界拒绝，不生成替代线程。

新增16项JVM检查（身份策略6、资源预算3、响应7）实际通过，Broker既有10项重新执行通过；旧版Android20项任务成功但命中缓存。SDK AAR和开发Broker APK构建成功；Windows Broker/SDK150项及.NET/Java各209项回归通过。52份SpecDD规格语法检查通过。

这些检查没有执行实际绑定生命周期，不能证明原生超时/解绑/死亡竞态、PackageManager、跨UID、签名轮换、重装或工作资料已验收。未安装/操作手机或改运行版/云端。Android待批准注册、授权UI和数据FD仍未实现，外部工作台的数据通道provider继续保持不可用。

### Android 待批准注册实验与事务核心（2026-10-29）

新增 [ADR0007](adr/0007-android-pending-registration.md)、便携 [PendingRegistry](../contracts/java/src/main/java/dev/linkgate/contracts/PendingRegistry.sdd) 和Android私有SQLite适配器。默认APK仍只读；`-PlinkgatePendingRegistration=true` 才启用 app.register/operation.status 及实验能力声明，使用实际调用身份快照分域，不自动授权。注册与成功结果原子提交，旧操作重试不覆盖较新声明；容量满保留旧结果，损坏存储返回固定错误。

注册核心由独立registration-core模块构建，客户端public-contracts排除服务端实现，JDBC仅测试使用。共享控制请求解析保留manifest原始值片段，按49152字节校验，避免空白被规范化后错误放过大小上限。hello不创建数据库，未知身份/字段/缺操作号先拒绝。

15项桌面SQLite JDBC测试通过，覆盖重开、规范化重试、身份分域、并发、容量、改参冲突、失败回滚、旧配置保留、腐败结果拒绝和原始字节边界。原Android Broker10项、客户端16项重新执行通过，旧版app20项任务成功但命中缓存；两种开关构建均通过并恢复默认false。Windows Broker/SDK150项、.NET/Java各209项回归通过，58份SpecDD语法检查通过。

这些测试不调用原生AndroidSQLite/Binder，跨UID注册、真实私有目录权限/链接、断电/磁盘满、升级迁移和100000短流仍未验收。未安装/操作手机，安装运行版本仍0.2.3。Android SDK注册封装、用户批准/授权与业务数据FD继续未完成，工作台的数据provider保持不可用。

### Android 待批准注册 SDK 与未知结果恢复（2026-10-29）

已完成 [Android SDK局部任务#2](../android/client/client.sdd)，接入方式见 [SDK README](../android/client/README.md) 和 [ADR0008](adr/0008-android-pending-sdk.md)。客户端新增 registerPendingAsync/getOperationStatusAsync；先协商 pending 能力，注册须显式选择实验功能并提供应用事先持久保存的operationId。新增不可变注册/差异/状态DTO，与注册核心共用严格pending结果验证；AAR只依赖public-contracts/control-ipc，没有注册核心、SQLite、FRP或网络权限。

真实BrokerClient复用可在JVM执行的ControlSession；每次请求前后仍核验平台身份。修改进入transport前原子声明可能提交；取消先赢得仲裁时不再进入transport。提交后的IO、坏响应、超时、关闭和身份变化返回带原ID的MutationOutcomeUnknownException；标准future取消保留CancellationException，通过OperationFuture.operationId/mayHaveBeenSubmitted保留恢复信息。SDK从不自动重连或重发，重新核验绑定后由应用显式查询原操作。成功/合法Broker错误在通知调用者前释放活动请求，未知失败先关闭逻辑状态，避免回调重入造成额外提交。

验证结果：SDK共34项实际执行通过（原16项、注册合同6项、会话引擎12项）。受控transport覆盖协商/开关、即时请求衔接、合法错误、丢回复与显式查询、错误关联/待批准标志、超时/关闭、取消前后、并发拒绝和请求前后身份变化，并计数确认无自动再次注册。注册核心15项桌面SQLite和Broker10项已强制重新执行通过；Windows Broker/SDK150项、.NET/Java各209项合同回归通过。SDK AAR与默认只读开发APK构建成功，最终ENABLE_PENDING_REGISTRATION=false，运行时依赖报告仅public-contracts/control-ipc。63份SpecDD语法、3个schema/4个示例/10个反例、文档链接、仓库边界和diff检查通过。

本轮未部署或升级运行服务，安装版仍0.2.3，未安装/操作手机。上述会话测试使用受控transport，不执行原生Binder/PackageManager/AndroidSQLite；跨UID、签名轮换/重装、原生取消/死亡/解绑竞态与平台后台仍未验收。待批准注册不授权任何设备或业务；用户批准/grant、数据FD和独立Android消费者样例仍待开发，外部工作台数据provider保持不可用。根任务#2及完整0.3继续开放；下一阶段需要先落实授权状态与生命周期，再开放业务数据通道。

### Windows 宿主声明审核与版本化状态（2026-10-29）

完成 [审核局部规格](../windows/LinkGate.Broker/RegistrationReview.sdd)，代码入口为RegistrationStore.GetReview/DecideReview/GetReviewOperation，详见 [Broker说明](../windows/LinkGate.Broker/README.md) 与 [ADR0009](adr/0009-windows-declaration-review.md)。仅可信宿主进程内可调用，没有应用管理RPC或UI；普通应用的注册/查询消息、SDK和能力声明保持原语义。

待审核与已批准声明分别保存。pending变化递增修订，审核决定递增版本；确认同时比较目录/主体/应用、两个版本和规范化声明，阻止并发旧确认与A→B→A。拒绝pending保留旧批准，撤销清空批准；旧批准操作返回历史结果，不重新应用。审核结果与决定原子提交，宿主查询支持丢回复恢复，配额复用且不淘汰记录。满额仍拒绝新决定（包括撤销），因此不能直接作为活跃授权系统。

schema1→2迁移仅创建未批准审核记录，保留旧注册/操作；失败回滚DDL与版本，schema2缺失状态失败关闭、不补建。数据库版本仅约束本机并发/正常重启/操作重放，不能检测整个外部备份被回滚，未宣称AUTH-15完成。

验证：Windows Broker/SDK **210项**实际执行通过（既有150项+审核60项），包括真实管道阻止自批和管理员结果读取、实际OS主体pending更新不替换已批准目标、跨目录/主体、修订/版本溢出、两个Store实例并发、历史重放、损坏行/结果及事务失败。独立SDK样例注册与重启后的query-only通过，消费者目录仍无Broker/SQLite依赖；.NET/Java各209项合同回归通过。65份SpecDD语法、schema/示例/反例、文档链接、依赖锁、仓库边界与diff检查通过。

本轮只构建开发库/宿主并使用自有临时状态，无安装、服务/ECS/路由变更或手机操作，运行版仍0.2.3。Android源码与数据库未改、没有重新执行手机或Android JVM测试。可信审核界面、可审核的身份连续性、逐服务最小授权、管理员安全操作容量保障、设备grant和数据通道仍待完成，根任务#2与完整0.3继续开放。

### Windows 撤销额度预留与schema3（2026-10-29）

完成[额度局部规格](../windows/LinkGate.Broker/JournalCapacity.sdd)及[ADR0010](adr/0010-revocation-capacity.md)。每份已批准声明在全局和宿主日志预算各占一格未来撤销预留；初次批准同时容纳本次结果与预留，普通注册/审核不能占掉它。撤销把预留原子转为保留结果，逻辑占用不增加，不扩大硬限或删除历史记录。新增宿主GetJournalCapacity聚合诊断，应用RPC/能力和SDK合同保持原语义。

预留从私有批准状态派生，重启可恢复；pending更新、拒绝和替换批准保留原预留。schema1/2原子迁移至3，schema2原批准/版本/结果保留，旧写入器不理解的新策略通过schema版本隔离。若旧状态加预留容纳不下，迁移失败并保留旧库，不能自动清空批准/删除结果/放宽限额；当前没有自动维护或清理入口。

验证：Windows Broker/SDK **258项**实际执行通过（较前版增加48项），涵盖全局/宿主满额仍可撤销、多份批准分别撤销、重启、旧操作重放、失败回滚恢复预留、schema2迁移成功/超额回滚、schema3降低上限拒绝、并发批准与普通写入争用，以及真实管道拒绝宿主容量RPC。独立SDK示例注册/重启query-only、.NET/Java各209项合同回归通过；67份SpecDD语法、schema/示例/反例、文档链接、依赖锁、仓库边界与diff检查通过。

这只解决逻辑操作额度，磁盘满、权限/存储损坏、锁超时、过期快照或审核计数耗尽仍可能使撤销失败；未执行物理磁盘满、断电、Android JVM/实机或活跃业务测试。本轮只构建隔离开发宿主并使用自有临时状态，未安装/操作手机或修改运行服务/ECS/路由，安装版仍0.2.3。批准界面、可审核身份、逐服务/设备grant及数据通道继续开放，根任务#2和完整0.3尚未完成。

### Windows 本地交互审核命令行（2026-10-29）

完成[控制台局部任务](../windows/LinkGate.Review.Console/LinkGate.Review.Console.sdd)，实现独立开发EXE，使用说明见[README](../windows/LinkGate.Review.Console/README.md)及[ADR0011](adr/0011-local-review-console.md)。启动只接受明确的已有开发数据库目录，拒绝重定向，并先输入OPEN才初始化/迁移。普通应用没有新增管理RPC或自动批准入口，运行服务不受影响。

宿主新增有界keyset引用分页和仅宿主可见的CallerScope摘要。命令行完整展示pending/先前批准、两个版本、未核验二进制身份及不授予网络权限的边界；不可信文本全部ASCII JSON转义。决定必须先show并输入新确认短语，取消/EOF/过期快照不提交。提交前写入私有、严格2048字节回执，独占锁和ACL/链接检查保护状态；只有已确认的旧回执能被新决定替换。

准备回执失败不调用Broker；提交后确认回执失败阻止新决定，recover仅查询/匹配原操作，不重放。未知结果不自动换号；用户另行确认abandon只解除本地等待，不改变Broker或宣称回滚。历史结果明确区分当前批准状态。逻辑满额下，控制台仍可使用已有撤销预留。

验证：Windows Broker/SDK/控制台**307项**实际执行通过（前版258项增加49项），包含真实私有SQLite/文件、脚本终端生产工作流、取消/并发旧快照、回执准备/确认失败与重启恢复、错误结果/未知操作、满额撤销、回执损坏/权限/独占、输入和分页边界。真实重定向EXE拒绝测试证明数据库字节不变、无目录或锁创建；实际管道阻止宿主枚举RPC。独立SDK示例注册/重启query-only、.NET/Java各209项合同回归通过；73份SpecDD语法、schema/示例/反例、文档链接、依赖锁、仓库边界及diff检查通过。

另用自有独立样例状态完成原生PTY自动冒烟：OPEN→浏览/容量→show→确认批准→再次show→确认撤销→quit，退出码0。只读核对最终版本2、操作3条、无批准声明且回执已确认；原始摘要在忽略的 `artifacts/linkgate-review-console-smoke/summary.json`。这不等于真人交互验收。

本轮只构建开发EXE并使用自有临时状态，未打开用户实际数据库、安装/操作手机或修改运行服务/ECS/路由，运行版仍0.2.3。未做原生交互终端真人验收、物理磁盘满/断电、跨用户或Android测试；签名/二进制身份连续性、正式逐服务/设备授权UI与grant、业务数据通道仍待开发，根任务#2和完整0.3继续开放。

### Windows 待确认最小权限申请（2026-10-29）

完成[申请局部规格](../windows/LinkGate.Broker/PendingGrants.sdd)与[SDK局部任务#2](../windows/LinkGate.Client/LinkGate.Client.sdd)，详见[ADR0012](adr/0012-pending-grant-requests.md)。默认关闭，宿主显式开启后hello才声明grant.request.pending实验能力。应用通过实际调用范围拥有的applicationRef及已审核import，提交目标设备/发布者/服务申请；设备引用尚未认证，不产生grant、token、服务发布或数据流，其他业务能力保持unsupported。

明确applicationRef解决同范围多应用alias相同的歧义；一个共享EXE内的逻辑appId仍不是强隔离主体。申请捕获ApprovedRevision、DecisionVersion和完整import策略，不能由应用覆盖策略/身份。事务保存申请与同范围operationId成功结果，旧操作重放仅返回历史结果，不复活被取代申请。另有当前pending/superseded查询；新审核/撤销使原绑定失效，pending更新不替代旧批准。

schema1/2/3原子迁移到4，不更改既有批准/操作或撤销预留，不发明申请；缺失schema4表失败关闭。每范围最多1024、全局16384份保留申请，不淘汰，普通日志准入保持撤销预留。SDK协商/显式开启、原操作号未知结果与重新核验后的只读恢复已完成。宿主命令行新增requests/request只读检查，没有grant批准命令。

验证：Windows Broker/SDK/控制台**381项**实际执行通过（前版307项增加74项），覆盖严格字段/Unicode、越权和未审核alias、显式应用上下文、策略/版本绑定与superseded、真实管道SDK、默认关闭、关闭实验后的历史查询、丢回复/坏回复后一次提交恢复、并发、SQL回滚、损坏/超长存储字段、schema3迁移及DDL中途失败回滚。1024/16384请求边界使用合成私有数据检验，不是100000短流压测。独立SDK样例注册/重启query-only和产物依赖检查、.NET/Java各209项manifest/policy语料通过；76份SpecDD、schema/示例/反例、文档链接、依赖锁和仓库/diff检查通过。

本轮只构建隔离开发组件并使用自有临时状态，没有安装/操作手机、打开用户实际数据库或修改运行服务/ECS/路由，安装版仍0.2.3。Java/Android未实现本项申请实验、未执行手机/Android测试；共享manifest语料通过不代表grant跨平台验收。签名身份、真实设备/服务认证、可批准的最小grant及后端执行、租约撤销和业务数据通道仍待开发，根任务#2和完整0.3继续开放。

### 服务端租约跨平台互操作（2026-10-29）

完成 [Agent授权模块](../control-auth/README.md) 与 [可复现互操作](../tests/server-authority/README.md)。真实 Go 签发 ES256 JWS，.NET 20项检查及 Java 至少15项检查通过；签发私钥只在 Go 进程内，两端仅接收公开证书与令牌。已签名的异常声明仍拒绝，范围/版本/启动代次从独立当前快照核验，不信任令牌自述。

共享时钟围栏使有效UTC至少随单调时间前进；验证耗时不会增加时限。同令牌重放、晚到更高序号旧令牌、墙钟停滞/回拨、单调回退以及关闭后的回复均不延长或恢复权限。Go 23个顶层测试继续通过。测试结果在忽略的 `artifacts/server-authority-interop-summary.json`。这不是FRP实际准入、控制健康/活动撤销或物理网络验收，完整版本任务继续开放。没有安装、手机操作、ECS或运行服务变更。

### 真实服务器控制与业务授权闭环（2026-10-29）

完成 [真实控制/业务验证](../tests/control-authority/README.md)。Go `/control/v1` 新增当前快照随租约返回及最多64项 `lease.check`；.NET与Java通过标准mTLS HTTP取得当前授权，固定服务器SPKI和启动代次、显式IPv4端点、3秒总时限、8并发及64 KiB响应，禁用HTTP代理/重定向。没有新增业务SDK凭据或owner变更接口。

Windows发布端显式传入服务端授权supervisor后，同时要求本地grant与有效服务器租约；访问端SDK仍通过真实私有管道，Java通过Agent内部PeerLeaseSource。最多128个guard每500毫秒分批检查，实际数据生命周期绑定取消。两端在60秒剩余阈值申请新签名租约，未变化令牌不重启时限；授权版本变化、已关闭guard及服务器换代均不能恢复旧流。

最终同机测试：.NET37项、Java19项实际检查通过；各64 MiB业务、SHA-256、应用challenge及请求FIN通过。65个真实秒内观察到2次新的发布端验签，两条业务连接均未重建。128个guard撤销309毫秒、实际阻塞业务关闭381毫秒；同密钥服务器更换启动代次关闭314毫秒，控制服务退出关闭2393毫秒。重新批准不复活旧guard，显式建立新控制代次才可恢复；旧签名租约仍无法重新准入。Windows使用非导出用户CNG测试密钥，Java私钥保持进程内，两端只共享公开CSR；所有自有测试密钥、临时进程与目录结束清理。

补强服务端证书检查：固定DNS SAN而非CN替代，.NET限定直接所有者根链；真实Go签发材料的.NET21项、Java20项授权互操作通过，包括同密钥但缺失SAN的证书。Go24个顶层测试及vet通过，Windows既有432项回归通过。Android原生模拟器回归通过：API36 x86_64、独立UID/签名、Keystore与Binder FD、64 MiB完整性及半关闭，本轮本地撤销232毫秒。原始结果在忽略的`artifacts/control-authority-interop-summary.json`与`artifacts/linkgate-native-acceptance-summary.json`；冻结摘要见[服务端授权证据](verification-v0.3-server-authority.json)。

新控制客户端与Java租约回调已是源码组合能力，尚未接入Android正式Agent配置/配对界面；Java进程测试不能替代Android原生HTTP/Keystore验收。FRP实际Login/工作/访客/NatHole权限、私有运行时桥、连接策略/恢复、安装和四网络/长稳继续开放。未操作物理手机、安装版本、ECS、全局路由或ReDesk，安装版保持0.2.3，完整1.0没有完成。

### 本阶段收束（2026-10-29）

按用户要求停止继续开发，保存[接续记录](HANDOFF.md)。功能冻结于 `49b5b09`，全部测试调用已结束，已核对自有服务端fixture、Java客户端和模拟器退出。没有开始FRP实现或新部署；根任务#2、#3、#4保持开放，择期从实际FRP认证接入继续。

### 恢复：逐设备 FRP 与 Windows 实际业务中继（2026-10-30）

按用户“恢复开发”继续完整1.0。完成 [ADR0016](adr/0016-frp-authority-runtime.md)、[后端扩展](../backend-frp/README.md) 与 [Windows 私有运行时](../windows/LinkGate.Runtime/README.md)。真实外层mTLS身份贯穿Login、工作/访客、发布和实际NatHole；空/跨主体RunID、旧共享token旁路、跨代理授权复用、重复候选响应及撤权后迟到响应均拒绝。待打洞请求和活动中继可取消，存储锁阻塞不妨碍独立到期与立即关网。

Windows源码构建frpc通过双向进程身份核验的私有Named Pipe接入，设备私钥保留不可导出CNG。SDK新增显式实验中继入口，Agent固定精确服务路由并保留内层数据mTLS、服务器服务租约和本地grant；原direct-only不隐式改走中继。端口/服务绑定、缺少本地授权、跨服务请求及重启历史路径均有验证；没有将打洞事件当作业务直连。

从固定上游对象重新生成的干净副本复现通过：153个后端顶层测试（含11个LinkGate新增）、模块校验和vet；Go authority30个及vet；Windows既有与新增434项；真实frpc/SDK29项。64 MiB摘要、challenge、请求FIN/响应EOF通过。此次复现服务器撤权84毫秒、本地grant撤权428毫秒、自有运行时撤销245毫秒，待审核更新被拒绝保留旧批准，其他运行时不受影响。证据见 [冻结摘要](verification-v0.3-frp-authority.json)。

Android私有宿主桥/正式服务器配置、真实XTCP业务、自动连接策略/恢复、配对轮换、发布安装及四网络/长稳仍待完成；这里的SDK中继消费者位于测试进程，不能替代独立签名应用验收。根任务#2/#3/#4继续开放，完整1.0尚未完成。未部署ECS、改全局路由/旧安装服务、集成ReDesk或操作物理手机，安装版保持0.2.3。

### 实际授权 XTCP 业务闭环（2026-10-30）

在前述真实frpc/Agent/SDK链路中新增XTCP-only访客，关闭STCP回退，使用两个仅监听回环的自有STUN端点，实际收集双方至少两次有效端点观测。SDK分别通过STCP和QUIC直连传输64 MiB，摘要、challenge、FIN/EOF及路径标签均通过；不能由历史打洞事件或中继通过推断此结果。

[本轮冻结证据](verification-v0.3-frp-direct.json)：39项原生进程/真实IPC/业务检查通过，服务器撤权使同时阻塞的直连和中继均在158毫秒内结束；本地grant撤销489毫秒、自有运行时撤销279毫秒。154项固定后端回归（含12项新增）、模块校验、vet及Windows434项回归通过。每次业务测试先记录running，再独立保存聚合通过/失败结果，避免失败重试沿用旧通过摘要。

后端真实认证准入及活动会话局部任务完成；平台桥任务和根#2/#3/#4继续开放。当前是同机回环的真实XTCP/QUIC业务，不是移动网络穿透成功率；Android宿主桥、物理设备/四网络、策略恢复、正式配对安装及发布长稳仍待完成，完整1.0没有完成。没有安装/部署、改ECS/全局路由、操作手机或改ReDesk。

补充修复了实际复现的TLS握手接收循环阻塞：把标准握手移到最多32个并发名额内，接收前占额、满额立即拒绝，静默未认证连接不再阻塞后续有效设备，释放名额后能继续接入。回归保留失败记录后修复重测；最终干净副本构建与39项直连/中继业务验证通过。另以缺失fixture路径验证了失败退出码和独立失败摘要，不沿用旧通过结果。

### Android 私有运行时与原生服务端授权（2026-10-30）

新增 [Android私有桥](../android/agent/src/main/java/dev/linkgate/agent/android/FrpRuntimeBridge.sdd) 和 [ADR0017](adr/0017-android-private-runtime.md)。源码构建ARM64/x86_64 frpc和父进程寿命守卫，APK按ABI校验两者SHA-256。应用私有Unix Socket核验实际UID/PID，设备私钥保留Keystore，Agent完成固定服务器的标准mTLS/ALPN并写入批准应用scope。最多16个连接、8个握手、有界执行器及3秒握手关闭；本地审核代次/证书/子进程/停止控制对应资源，待审核更新被拒绝保留旧批准。

真实API36 x86_64模拟器已验证：非导出Keystore、实际源码frpc、父子进程和Unix身份、无关同UID socket不产生外部连接、控制/数据mTLS与服务器租约、64 MiB摘要/FIN，以及服务端和本地撤权。Android业务目前来自Agent内部PeerClient，公开SDK/前台网关尚未组合；不能据此关闭独立UID中继、物理ARM64或四网络验收。冻结证据见 [Android FRP](verification-v0.3-android-frp.json)，旧的各次验证摘要保留原样。

原生测试暴露落后系统时钟导致的严格iat拒绝；两端控制客户端仅用已认证且相关的成功HTTPS响应Date，加入一秒量化和整次请求耗时上界，保守地向前推进共享时钟。签名/时间/序号条件不放宽，延迟回复不扩展旧receipt，单调回退仍永久关闭。Java3项时钟单测通过；真实Go签名标准互操作.NET21/Java20项通过；真实控制/业务.NET38/Java19项、65秒续期与128 guard撤权、换代/控制退出全部通过。

Android HTTP依赖要求SDK37.0，已固定AGP9.1.1/Gradle9.3.1/JDK17编译组合，保持minSdk26/targetSdk36，FRP标准ALPN模式要求API29。前台配置、公开SDK路由、连接策略/恢复、配对和发布门槛继续开发；未操作物理手机、改ECS/全局路由、旧安装版或ReDesk。

### Android 正式入口组合与独立 SDK 中继（2026-10-30）

[ADR0018](adr/0018-android-sdk-server.md) 与 [冻结证据](verification-v0.3-android-sdk-relay.json) 记录：所有者确认公开服务器配置、后台代次安全启动、实际Keystore控制客户端、当前服务器租约和私有FRP路由已组合到前台Agent；公开薄SDK新增显式relay能力，继续要求批准import、精确本地grant、发布端grant和服务器grant。已配置服务器时直接路径也须服务器授权，不降级预览。

API36 x86_64 上独立UID10179/Agent10177、不同APK签名消费者通过公开SDK传输64MiB，校验SHA-256/FIN/一次性附接，两条同应用私有运行时并存。本地撤权触发到测试完成1433毫秒，服务器撤权245毫秒；损坏服务器配置拒绝与停止时迟到启动检查通过。内部桥独立保留64MiB证据，服务器触发到测试完成291毫秒。Windows实际STCP/XTCP各64MiB、内层mTLS和活动撤权保持通过。

后端155个顶层测试（13个LinkGate）、vet/依赖校验通过；加入规范实例clientID后缀，保持真实设备/应用命名空间和服务权限。移动SQLite12项、SDK单元回归通过。首轮内部guest计时失败的记录保留，guest等待观测与主机触发后的五秒预算已分开，不将协调等待误作实际撤权时延。

公开SDK仅新增显式实验relay，不代表正式connection.open、自动direct-first/fast-connect或网络恢复已完成。每流独立frpc仍有资源开销，最大16条relay路由；需继续池化/策略/运维入口和真实网络、10万流、24/72小时验收。新认证后端尚无生产服务器CLI。旧安装、ECS、ReDesk和物理手机未更改。

本轮还通过未配置服务器的独立Android直连预览回归（64MiB，本地撤权868毫秒）、旧app单元测试及release构建。构建入口隔离legacy app任务并允许显式选择Gradle9.3.1。测试脚本在安装前冻结四个APK哈希并绑定验收结果，避免后续构建覆盖已测二进制证据。

### 精确scope运行时复用与启停修复（2026-10-30）

[ADR0019](adr/0019-scoped-runtime-pool.md) 更新每流启动frpc的方式：完全相同的本地authority snapshot共享一个自有runtime，每个流独立取得服务器租约、私有数据通道并独立取消。最多16个创建/运行/关闭中的对象，已使用session空闲60秒后回收；部分初始化先登记资源，失败close不能提前释放容量，停止后清理重试有三秒上限。

可移植池11项并发/所有权测试通过。独立Android UID10191/Agent10189验证同scope一个runtime、64MiB、双流并存和关闭其中一流后另一流仍活跃；本地撤权2278毫秒、服务器撤权214毫秒；服务器拒绝新open返回AUTH_REQUIRED且没有新增业务后端连接。[冻结证据](verification-v0.3-runtime-pool.json) 含实际测试APK哈希及旧预览回归，旧预览本地撤权1433毫秒。

新增停止测试实际复现前台服务超时崩溃，失败run8a2263c7cf0b40bf8905fed70881519f保留。修复为私有服务动作排队、每次start完成前台登记、startId及实例/启动代次隔离，防止旧销毁/迟到失败干扰新启动。连续三次立即启停再启动、实际SDK业务和末尾12秒停止观察通过。完整连接策略、Windows复用及大规模/长稳/物理网络门槛仍开放，未改变旧安装、ECS或物理手机。

### 显式STUN与新运行时发现（2026-10-30）

[ADR0020](adr/0020-explicit-stun.md) 与 [证据](verification-v0.3-explicit-stun.json)：新Agent只使用所有者明确配置的最多4个不同数字IPv4端点，禁止隐式公共备用和未配置的OTHER地址。复用同一UDP socket，至少两个真实目标观测才报告sufficient；先前成功映射可跨失败目标保留。Windows/Android配置防御性复制并覆盖继承环境，旧stock路径单独保留。

161个后端顶层测试（19个LinkGate）、vet/依赖校验通过，新增6个STUN场景；Java端点3项单测通过。最新跨平台原生47项主检查通过：Windows实际STCP与XTCP-only各64MiB/半关闭/撤权；Android新端点配置持久化、新Go运行时及独立SDK中继/复用/启停通过。Android XTCP业务尚未接入此次流程，四物理网络/TUN及自动策略继续开放。旧安装、ECS、ReDesk和物理手机未改变。
