# ADR0016：FRP 真实准入与 Agent 私有运行时

日期：2026-10-30。状态：后端及 Windows 本机直连/中继业务闭环已验证；Android 宿主桥和物理跨网络 XTCP 待实现/验证。

继承 [ADR0015](0015-authenticated-transport-control.md)。固定上游 FRP 0.71.1 的派生副本应用仓库内补丁，安全构造器显式启用设备准入，原共享 checkout 和安装版均不变。

## 决定

外层标准 mTLS 在 Agent 和后端之间完成，固定所有者根、服务器公钥及 `linkgate-frp/1`。握手后 Agent 写入有界应用/角色前缀，后端产生不可由消息字段构造的 gate-local 主体句柄。frpc 只拥有普通传输字节，不读取设备私钥。应用逻辑 ID 由可信 Agent 绑定到已审核声明；它不是不受信任远端进程自行声明即可获得的授权。

真实 Login、NewWorkConn、NewVisitorConn 绑定非空 RunID、设备修订、应用、角色与确定性不透明命名空间。发布限定 STCP/XTCP 和当前服务；实际打洞请求及报告绑定同一活动授权，候选受数量/大小/数字 IP 校验，重复发布端回复和跨服务复用授权被拒绝。

FRP STCP 协议仍需要兼容 `secretKey` 字段；新模式将其设为公开的不透明完整代理名。该字段不承担身份或权限，不能拿到它就访问服务：外层 mTLS、作用域、当前 grant 以及业务内层 mTLS/两层授权均独立执行。安全构造器拒绝旧共享 token、HTTP 插件、公开代理及备用监听器，失败不转入 legacy。

传输 gate 有界保留 peer/control/publication/authorization。后台每 500 毫秒检查服务与 grant；独立 100 毫秒单调到期路径不等存储锁，停止立即关闭自有原始 socket，后台排空另用有超时的 `WaitClosed(ctx)`。实际 STCP 数据流挂接活动授权取消，打洞等待也可取消；已经穿透后的直连流仍须靠两端业务租约执行撤销，不能靠关闭 frps 注册项推断。

Windows 使用成熟 go-winio 连接器、保护 DACL/拒绝远程的 Named Pipe、双方 PID 核验、持有的进程句柄及 Job。Agent 每 250 毫秒检查批准修订/权限代次/证书，变更只回收自有运行时。待审核新版本被拒绝保留旧批准。证书有效时间至少按单调时钟推进。

访问端新增显式实验 `secure.stream.relay` / `secure.relay.open` 和 SDK `OpenSecureRelayAsync`。宿主固定 visitor 应用/import 和目标设备/应用/服务、回环端口、自有运行时取消令牌，要求服务器授权和允许中继的已审核 import。发布端 peer 监听可绑定单个 `ServiceTarget`，避免获得某服务传输后再借监听访问另一服务。原 direct-only 入口不改变传输；正式 `connection.open`、自动策略与恢复仍未完成。

操作身份与传输路径写入已有不可变 operation result，不升级 SQLite schema；旧结果缺失路径时按历史 direct 处理。`ready` 才表示当前内层 mTLS/授权已通过；pending 中的 path 只是所选传输。重启关闭旧操作但保留中继标签，不重新拨号或重放业务。没有当前中继路由的 SDK 会话仍可读取已关闭历史结果。

## 证据和后续

见 [冻结证据](../verification-v0.3-frp-authority.json) 及 [复现入口](../../tests/frp-authority/README.md)。这次新副本执行153个后端顶层测试（新增11个）、Go authority30个、Windows434项和真实 frpc/SDK29项。64 MiB 经真实中继、内层 mTLS、应用 challenge、摘要和半关闭；本地/服务端撤权均关闭阻塞流。

后续 [XTCP冻结证据](../verification-v0.3-frp-direct.json) 增加实际源码frpc的XTCP-only访客、双端点回环STUN、内层mTLS与SDK另外64 MiB，回退关闭；直连和中继同时持有阻塞业务时，服务器撤销使两条流都在5秒内关闭。39项集成检查通过，未将打洞事件替代实际业务证据。后端另修复了静默TLS客户端阻塞接收循环的问题，以32个并发名额先准入再握手；最终154项回归（12项新增）验证正常设备并发、满额拒绝和名额回收。

这不是物理 NAT、Android 原生私有桥、独立签名应用中继或跨网络验收。继续补平台接入、策略、指标和网络恢复；根规格任务#2/#3/#4及完整1.0保持开放。
