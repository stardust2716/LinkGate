# 逐设备授权的 FRP 后端

这是 LinkGate 1.0 开发中的传输后端源码扩展。它保留 FRP 的 STCP/XTCP 引擎，补齐设备、应用和服务的真实准入。当前尚不是可部署的独立服务器安装包；不要用上游 `frps` 命令代替这里的安全组合入口。

固定上游提交：`d8cf1800dc1abd7fda1828cab105d7a6b5a50eb1`（0.71.1）。`prepare.py` 通过 Git archive 生成忽略目录中的新副本，应用 `authority.patch` 和 `overlays/`，不会包含共享 checkout 的未提交修改。既有 ReDesk、ECS 和 0.2.3 安装版不变。

固定对象中的 Go 文本先将 CRLF 统一为 LF，再建立派生副本的补丁基线；这仅影响构建副本的换行，不改变上游对象或业务代码。

## 信任与数据路径

1. Agent 保留设备私钥，通过标准 mTLS、固定服务器身份和 ALPN `linkgate-frp/1` 接入。服务器完成真实握手后读取最长 4096 字节的严格应用/角色前缀；FRP 自述字段不是设备身份证明。
2. 派生 frpc 只连接受保护的 Agent 私有通道。Windows 互验实际进程 PID、父进程及存活句柄；Agent 持有受 Job 管理的子进程。Unix 连接器检查 `SO_PEERCRED`，Android 私有宿主桥以同样的父子进程核验连接Keystore身份。
3. Login 的user命名空间、规范随机实例clientID、非空 RunID、工作连接、访客和发布代理绑定同一真实设备/应用/角色。代理名使用确定性的 scope 哈希，服务发布要求当前设备角色和服务版本。
4. 访客要求精确 grant；实际 NatHoleVisitor、NatHoleClient 和报告均绑定该授权，而不只检查 PreCheck。候选数组、并发请求、连接和授权对象有上限。标准TLS握手在接收循环之外执行，最多32个并发名额，满额立即拒绝。等待访客和发布端响应使用分开的请求额度。
5. 撤权关闭活动中继及待完成打洞；独立单调到期监视器不等待存储锁。关闭 gate 立即回收网络，`WaitClosed(ctx)` 单独等待后台存储工作退出。
6. 业务仍在两端 Agent 之间执行设备 mTLS、本地服务 grant 和服务器签发的短期服务租约。传输准入成功不自动授予业务权限，云端只转发这层业务密文。

STCP 兼容字段 `secretKey` 使用公开的完整不透明代理名；它不是凭据，也不能绕过实际 mTLS 和当前服务授权。新模式禁止共享 token、HTTP 插件、公开代理及备用未认证监听器；原 legacy 构造器行为单独保留。

## 从干净副本验证

在仓库根目录运行，需要 Go 1.27 工具链、Git、Python 3.12+ 和 Windows .NET 10 SDK：

```powershell
./gateway/tests/frp-authority/verify.ps1 `
  -Go ./tools/linkgate-go/go/bin/go.exe `
  -Source ./upstream/frp-linkgate
```

每次生成唯一 `artifacts/linkgate-backend-frp/verified-*`，运行依赖校验、client/server/NatHole 回归、vet，源码构建 frpc 和仅用于本地测试的 authority fixture，再运行 Windows 私有通道及 SDK 中继验证。日志、模块/补丁/二进制摘要存放在该副本。设备私钥由测试创建为不可导出的 CNG 密钥，退出时删除；Go fixture 只签署公开 CSR。

仅验证后端、不运行 Windows 业务测试：

```powershell
./gateway/backend-frp/verify.ps1 `
  -Go ./tools/linkgate-go/go/bin/go.exe `
  -Source ./upstream/frp-linkgate
```

Windows 连接器依赖固定的 [Microsoft go-winio v0.6.4](https://github.com/microsoft/go-winio/releases/tag/v0.6.4)，使用成熟的 IOCP 管道实现和操作系统进程核验，未实现自定义密码协议。

## 验证边界

本地源码测试覆盖真实 TLS 和 FRP 消息处理；Windows 集成覆盖真实 frpc、Named Pipe、内层数据 mTLS、SDK 直连/中继各64 MiB、半关闭、撤权和作用域拒绝。XTCP-only 禁用回退，使用两个回环 STUN 观测端点完成实际 QUIC 业务；候选与同机直连证据没有执行物理 NAT 打洞。已补Android私有宿主桥与独立SDK中继；尚待跨网络 XTCP、策略与网络恢复、正式配对/部署入口及发布矩阵；详细证据见 [进度](../docs/PROGRESS.md)。

Android Agent的两个ABI源码构建入口为 `build-android.ps1`，使用固定NDK29、API26及16KiB对齐，返回生成目录及每ABI摘要；通过显式 `linkgateAgentRuntimeDir` 选择。新私有桥/Keystore及服务器授权已有 [原生组件证据](../docs/verification-v0.3-android-frp.json)，前台/公开SDK已有 [独立UID与签名证据](../docs/verification-v0.3-android-sdk-relay.json)。

新认证Agent使用显式STUN列表，与旧0.2.3的公共备用分开。Windows私有桥的可选 `stunEndpoints` 参数复制最多4个数字IPv4端点；Android所有者文档可加入同名数组。子进程仅查询该列表，不追随未配置OTHER地址。至少两个真实目标观测才可分类，业务是否直连另行验证，见 [ADR0020](../docs/adr/0020-explicit-stun.md)。

新增 [Linux 持久服务器入口](../control-plane/serverhost/README.md)：`linkgate-server request/init/serve` 使用明确配置、主机本地私钥、同根管理设备和独立权限/见证数据库。它不继承 fixture 自动授权或旧共享token。原生验证见 [server-host](../tests/server-host/README.md)；云端部署、远程管理CLI和发布迁移仍待完成。
