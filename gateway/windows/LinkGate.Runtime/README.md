# Windows Agent 自有 FRP 运行时

`FrpRuntimeBridge` 是可信 Agent 的组合组件，业务 SDK 不依赖它。宿主提供已经审核的应用、明确固定的服务器公钥/IPv4 端点、派生 frpc 路径和预期 SHA-256。组件保留设备密钥，不把证书私钥或服务器 token 写入 frpc 配置。

启动前先保留受保护的唯一 Named Pipe，再通过 `OwnedProcess` 核验并把进程放入 Job。每条连接核验管道实际客户端属于持有的活进程，然后由 Agent 建立服务器 mTLS 并写入固定应用/角色前缀。frpc 反向核验父 Agent 的管道 PID。桥接不提供任意地址代理。

审核撤销/批准代次变化、证书到期、子进程退出或桥关闭时，只回收自有 Job、管道和网络连接。待审核更新被拒绝不停止原有批准版本。每个桥最多 62 条活跃管道、16 个握手，握手限时 3 秒，审核每 250 毫秒检查；到期使用单调推进的有效时间防止墙钟回拨延长资格。

可信宿主可把 `SecurePeerServer` 绑定到单个 `ServiceTarget`，由 frpc 的发布代理只转发到这个端到端 mTLS 监听器。访问侧给 `SecureConnections` 提供精确的 `SecureRelayRoute`，包含应用/import、远端设备/应用/服务、回环端口和自有运行时取消令牌；要求服务器控制授权且 import 明确允许中继。SDK 显式调用 `OpenSecureRelayAsync(..., allowExperimental: true)`，协商 `secure.stream.relay`；原 `OpenSecureDirectAsync` 保持 direct-only。没有隐式策略切换或载荷重放。

`path` 表示该操作选择的传输，`ready` 才表示端到端 mTLS、两层授权和私有数据附接入口就绪。`pending` 的 path 不是已送达业务字节的证据。关闭/重启后保留原路径和操作结果，不能把历史中继重标为直连。

当前为源码组合接口，尚未接入正式安装版的配置界面、自动配对和更新；不能把任意宿主提供的配置当作不可信应用可以提交的配置。复现见 [FRP 验证](../../tests/frp-authority/README.md)。
