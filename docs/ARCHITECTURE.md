# 架构与实现

LinkGate 将设备网络连接与业务应用分离。薄 SDK 只处理声明、操作引用及私有流，Agent 持有设备密钥、权限存储、连接规划和 FRP 运行时。Windows 通过受保护的命名管道验证真实调用方；Android 通过 Binder UID、包与签名来源识别调用者。Windows 是当前用户信任域，不宣称强隔离恶意同用户程序。

## 授权边界

设备使用所有者根签发的 P-256 证书。服务器仅接受已授权设备及精确 publisher/visitor application/service；业务还需本地声明审核、访问端本地 grant 和发布端服务 grant。申请和注册都不等于授权。

短期服务器租约与设备 mTLS 对应实际访问范围；本地和服务器撤权检查独立于业务吞吐。FRP 只取得 Agent 管理的私有传输桥，不导出设备私钥。控制、后端和业务层都不能以失败后换旧协议的方式绕过验证。

## 连接生命周期

应用调用 register、grant.request 后等待批准，再执行 connection.open → operation.status → attach → BusinessStream → close。原操作 UUID 先保存再提交；结果未知时查询原 UUID。一次性附接绑定真实调用方，打开结果与之后的实时状态分开保留。

连接规划共用一个绝对预算。已知 IPv4 地址可优先尝试，其次按策略使用 XTCP 或允许的 STCP。XTCP 需要至少两个明确配置、不同的实际 STUN 目标；新认证模式没有隐式公共备用。相同授权快照共享载体，各流独立持有租约和取消；scope 改变时旧资源失效。

## 数据与计量

私有业务流提供有界帧、背压、半关闭与结束确认。计数来自成功处理的业务字节，异常、重启或未完成确认不标为完整传输。普通 HTTPS/WSS 的主机名、证书、业务令牌与请求回执继续由应用处理。

真实低带宽 ECS 联调修正了过短心跳和 FIN 确认等待；长业务流完成后，SDK示例显式重连闲置控制管道，再查询原连接，不重放业务。自动跨网络恢复、事件接口与更多适配器仍在后续规格中。

[安全规格](../gateway/docs/SECURITY-SPEC.md) · [工程规格](../gateway/docs/SPEC.md) · [ADR](../gateway/docs/adr/0028-scoped-windows-runtime.md)
