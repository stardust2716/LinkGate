# Windows 认证网关试用版

首个安装版为0.3.0-preview.1，当前部署更新为 **0.3.0-preview.2**；实际部署及WAN修正见[部署说明](../release/DEPLOYMENT.md)。构建、部署、设备身份、服务器授权、本机审批和独立 SDK 示例步骤见 [快速开始](../release/README-template.md)；本次范围见 [TRIAL-SCOPE](../release/TRIAL-SCOPE.md)。旧 0.2.3 配对协议独立保留，新安装器不自动迁移旧服务。

安装包包含持续运行的 Agent、Owner 命令、声明审核命令、匹配的新认证 FRP 和独立示例。Windows x64 自带 .NET 运行时，按用户安装，不建立计划任务、不改默认路由或防火墙。卸载保留用户身份和状态。它目前是通过启动入口运行的命令行网关，配置和授权仍须按指南操作。

启动方式：

```text
LinkGate.Agent.Console.exe serve <state> <identity> <server.json> <confirmed-server-key> <frpc.exe> <runtime-SHA256> <IPv4-bind> <port-or-zero>
```

正式连接经过真实调用方身份、本地声明批准、精确访问 grant、服务器设备/服务/访问权限、短期租约以及内层设备 mTLS。一个应用的授权不能用于另一个应用。原操作结果单独保存，未知结果查原 UUID；连接断开后由业务决定重试，不自动重放请求。

SDK 接入顺序为 register → 用户批准 → grant.request → 本地与远端所有者批准 → connection.open → operation.status → attach → BusinessStream → close。示例仅依赖薄 SDK，不读取设备密钥或 FRP 配置。对 Agent 的信任从显式启动流程传递，不接受任意进程或来历不明的 bootstrap。

动态运行时实现见 [ADR0028](adr/0028-scoped-windows-runtime.md)。跨 NAT XTCP 需要管理员明确配置至少两个不同的 STUN 目标；本版没有隐式公共备用。不配置 STUN 时可使用已知地址直连及允许的中继。普通 TCP、HTTP、HTTPS、WebSocket 通过有界私有流传送，原业务 TLS、Host 和认证继续由业务处理。

这是供接入实验的安装版，尚不是生产 1.0。后续开发项集中在 [mature-release.sdd](mature-release.sdd)：事件/服务准备、更多适配器、网络观察与 TUN、证书轮换、签名更新、真机及长稳验收。验收界限见 [首次安装证据](verification-v0.3-first-installable.json)。
