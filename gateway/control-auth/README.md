# Agent 服务端授权验证

此目录供 Agent 内部使用，不是业务 SDK。Go 控制服务签发标准 ES256 JWS；.NET 使用 Microsoft.IdentityModel.JsonWebTokens 8.23.0，Java 使用 Nimbus JOSE+JWT 10.10。依赖固定，签名和证书验证使用成熟库。

调用者必须从已经认证的控制连接提供当前服务/grant快照、明确固定的服务端 SPKI、所有者公开根和服务端启动代次。令牌内的 issuer、版本或 epoch 不能成为自己的信任来源。字段、算法、受众、应用/服务、版本、唯一ID和120秒上限逐项核对；禁用远程密钥发现。

每个控制代次共享一个 `LeaseClock`，禁止逐令牌重新创建。有效时间至少随单调时间前进；墙钟停滞、回拨、同令牌重放或晚到的更高序号旧令牌，均不能延长签名到期。单调回退永久关闭该时钟，已关闭的 `LeaseWindow` 不接受晚到结果。续期仅接受同一范围、签发者和代次的更高序号，并继续受证书到期限制。

`ControlClient` 通过标准 mTLS HTTP 获取当前快照与租约，固定逻辑 TLS 名称和服务器 SPKI，使用明确 IPv4 端点，禁用 HTTP 代理、重定向及自动重试。每次调用整体3秒、最多8并发和64 KiB响应；没有 owner 变更 RPC。Java HTTP 固定 OkHttp5.5.1，私钥只通过平台 TLS 句柄使用。

Windows `LeaseSupervisor` 最多128个活动 guard，以64项批量每500毫秒检查一次当前授权。失败、撤销、换代和单调到期均关闭 guard。`SecurePeerServer` 显式传入 supervisor 后，要求服务器租约与本地 grant 同时满足，并将 guard 取消直接关联数据生命周期；没有失败后退回预览的路径。访问端 `SecureConnections` 显式接受控制客户端，Java 通过 `PeerLeaseSource` 提供 Agent 内部租约回调；60秒剩余阈值续期，同 token 不重新计时，不跨代次续接旧流。

这些是开发宿主的显式组合接口，尚未接入已安装 Agent 的配对配置或 FRP。默认旧预览构造参数保持原语义，新模式必须由受信任宿主明确启用。复现见 [令牌互操作](../tests/server-authority/README.md) 和 [真实控制/业务验证](../tests/control-authority/README.md)。
