# ADR 0002 FRP 服务端授权的实施关口

日期：2026-10-29。状态：源码核查完成；后端准入扩展与数据会话方案待原型验证。固定核查基线为 FRP 0.71.1，commit `d8cf1800dc1abd7fda1828cab105d7a6b5a50eb1`。本次未修改或部署 FRP。

## 结论

保留 FRP 作为首版传输后端，但现有 HTTP 插件不能单独完成设备/应用/服务会话授权与活动直连撤销。0.3 合同基础工作不会消除该限制；0.5/1.0 的安全门槛继续开放，不能用共享 token 加 UI 标成已完成。

## 核查证据

以下链接固定到正式上游提交；本仓库的计数/绑定/STUN 补丁没有补齐这些授权路径。

| 路径 | 核查结果 |
|---|---|
| [插件目录](https://github.com/fatedier/frp/blob/d8cf1800dc1abd7fda1828cab105d7a6b5a50eb1/pkg/plugin/server/plugin.go) | 有 Login/NewProxy/NewWorkConn 等，可作为身份与发布命名空间准入点；没有实际 NatHoleVisitor/Client 的对应钩子 |
| [访客注册](https://github.com/fatedier/frp/blob/d8cf1800dc1abd7fda1828cab105d7a6b5a50eb1/server/service.go) 与 [VisitorManager](https://github.com/fatedier/frp/blob/d8cf1800dc1abd7fda1828cab105d7a6b5a50eb1/server/visitor/visitor.go) | RunID 所关联的访客控制信息并未成为完整设备/应用/租约上下文；旧流程还存在空 RunID 兼容分支 |
| [代理数据处理](https://github.com/fatedier/frp/blob/d8cf1800dc1abd7fda1828cab105d7a6b5a50eb1/server/proxy/proxy.go) | NewUserConn 中的 User 来自发布者，不能直接当作访问者身份；关闭监听器不等于关闭所有活动转发流 |
| [打洞控制器](https://github.com/fatedier/frp/blob/d8cf1800dc1abd7fda1828cab105d7a6b5a50eb1/pkg/nathole/controller.go) | 必须覆盖实际 Visitor/Client 请求，不只保护 PreCheck；删除注册项不等于撤销已建立数据通道 |
| [XTCP 访客](https://github.com/fatedier/frp/blob/d8cf1800dc1abd7fda1828cab105d7a6b5a50eb1/client/visitor/xtcp.go) 与 [发布者](https://github.com/fatedier/frp/blob/d8cf1800dc1abd7fda1828cab105d7a6b5a50eb1/client/proxy/xtcp.go) | 直接 QUIC 数据会话由两端客户端持有，不再经过 frps，因此仅服务端取消注册不能证明直接流及时停止 |

当前 QUIC 配置也不能单凭 TLS 名称证明已绑定用户批准的设备公钥；仍需按安全规格验证实际数据路径。此结论不等于声称当前所有 FRP 数据都未加密。

## 下一步与阻断门槛

1. 在固定后端设计窄范围准入扩展，保留可信控制连接身份，覆盖 STCP 访客和 XTCP 实际请求的 scope/version/lease 检查。成熟模式禁止空身份旁路，legacy 模式明确隔离。
2. 两端 Agent 提供私有数据边界、成熟 TLS/mTLS 身份绑定和活动流注册表；发布端执行授权和租约失效，不仅访问端自律。中继端补相应的活动流关闭能力。
3. 验证跳过预检查、伪造 namespace、旧 RunID、跨应用借用 tunnel、撤销时分区以及路径切换不重置租约。
4. 达不到在线 5 秒关闭和租约到期加 5 秒边界，不能关闭安全需求。如果窄扩展代价或安全性不合适，再通过同一 Adapter 契约对照其他引擎；不自动重写整个网络栈。

独立设备密钥、短时授权和后端执行验证仍是后续开发，不属于当前公开合同库的验收范围。
