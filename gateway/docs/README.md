# LinkGate 成熟应用网关设计文档

当前用户文档见[仓库文档导航](../../docs/README.md)。本目录保留详细规格与阶段证据，下面的设计草案及“下一阶段”按历史时间阅读。当前版本见[版本表](../release/version.json)。


日期：2026-10-30。设计文档版本：草案 1。安装版：0.2.3；完整版本开发中，目标产品版本：1.0。

本目录定义从已验证的 TCP 直连实验组件发展为多应用网关的方案与契约。已采用 SpecDD 推进 0.3，现有 [Windows 安全直连接入预览](../examples/secure-direct/README.md)：真实设备双向 TLS、发布端精确服务授权、私有 SDK 数据流和活动撤销均已有独立应用端到端证据。Android已有原生Binder/Keystore数据SDK；新权限边界下的 [FRP真实准入与Windows SDK中继](../backend-frp/README.md) 已通过本机验证。Android私有FRP运行时、跨网络XTCP、策略恢复和完整配对运维仍待完成；完整0.3未交付。原声明审核、待确认申请和 Android Binder 注册实验继续保留，进度见 [PROGRESS.md](PROGRESS.md)，流程见 [SPEC-DD.md](SPEC-DD.md)。安装版本仍为 0.2.3，没有启动 ReDesk 改造。

两端现有实验公开连接API、持久状态/业务计量和SDK；Android独立签名模拟器及Windows真实管道的三种路径各64 MiB通过，见 [ADR0027](adr/0027-windows-public-connections.md)。Windows消费者独立进程、动态发现、服务级API及完整生产运维和长稳仍开放；此前各工作包记录是历史范围，最新状态以PROGRESS为准。

## 阅读顺序

| 文档 | 用途 |
|---|---|
| [PRODUCT-PLAN.md](PRODUCT-PLAN.md) | 产品目标、用户流程、架构、范围、里程碑、依赖和风险 |
| [SPEC.md](SPEC.md) | 对象/配置、IPC/API、SDK、策略预算、状态机、生命周期、统计与云端控制面 |
| [SECURITY-SPEC.md](SECURITY-SPEC.md) | 信任边界、应用授权、设备配对、加密、撤销/租约、供应链及安全门槛 |
| [ACCEPTANCE.md](ACCEPTANCE.md) | 阶段测试、独立应用、网络故障、权限、长期运行、发布回滚及记录要求 |
| [契约 schema 与示例](../spec/README.md) | JSON 形状、额外语义验证、非秘密应用说明和 RPC 示例 |

## 关键决策

- 独立 Agent、受认证本机 Broker、薄 SDK；不让每个应用各自复制一个打洞引擎。
- 首版 Windows x64 发布/访问、Android ARM64 访问，支持 TCP/HTTP(S)/WS(S)。IPv4 必需，IPv6 独立验证后再声明，UDP/系统组网/公开多租户后续评估。
- 保留固定 FRP 后端，通过适配层隔离。应用 API 不依赖 FRP 配置格式。
- 兼容回环端口依赖业务认证；受控 SDK 使用私有数据通道。Windows 是每用户信任域，不能宣称强隔离恶意同用户进程。
- 默认 direct-first；另有 fast-connect、direct-only。已有 TCP 长连接不透明迁移，不自动重放业务写请求。
- 每设备/应用/服务授权；服务端执行门槛不能用 UI 替代。120 秒租约、30 秒续期，在线撤销 5 秒内关闭；控制失效最多 5 秒检测、确认断联后禁止新打开，已有流到期加最多 5 秒关闭。有效租约子流不逐次增加云端往返，首版不提供主动离线模式。
- ReDesk 现有 P2P 保持原状。先开发独立应用样例，不把过去的内置测试当作跨应用验收。

## 实施入口与设计关口

下一阶段是 0.3 应用接入版。先评审 Broker 身份、公开合同、私有数据流和 FRP 服务端执行可行性，再开发 SDK 与独立样例。0.4 补可靠连接，0.5 完成身份/撤销/运维，全部 1.0 必需项通过后才可稳定发布。

Windows 已知端点的私有数据流、设备证书和发布端授权见 [ADR0013](adr/0013-secure-direct-streams.md)。仍需冻结和验证：FRP准入/租约/活跃流撤销适配、跨平台证书与租约互操作向量、Android私有流、正式签名及SDK许可。不能把Windows预览通过算作这些产品关口全部完成。

文档/机器 schema 不一致时必须修正并评审，不能让 SDK 自行选择解释。变更需同步相应用例和示例；当前运行版本、已发布包及 [0.2.3 验收证据](../tests/NETWORK-RESULTS-v0.2.3.md) 不随设计文档更新而改变。