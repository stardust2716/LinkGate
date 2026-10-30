# 新传输控制服务（开发中）

此模块为新授权体系提供逐设备角色、服务、精确访问 grant 和最长120秒的标准 JWS/JWT 租约。它与旧版0.2.3的共享 token / frps 部署分开，目前是 Go 服务库及真实本地 TLS 集成测试，尚未成为可直接部署的完整 FRP 后端。

## 已实现

- Go 标准 TLS/x509 设备双向认证：唯一所有者公开根、P-256非CA设备证书、固定 `linkgate.device` SAN、两项EKU、有效期、公钥绑定及当前设备准入。禁用服务端会话票据；身份取自实际握手。
- 设备角色区分 owner、publish、access。只有 owner 能修改管理状态；设备/服务每次重新批准推进代次并使关联 grant 失效，重放旧成功操作只返回历史结果。不能撤销最后一个活动 owner。
- bbolt 同步事务保存版本、操作摘要和结果。为每个活动设备、服务和 grant 预留撤销日志额度；日志满仍可撤销已有权限。默认64设备、1024服务、每发布应用16服务、4096 grant、100万条管理结果，无静默清理。
- 独立 bbolt witness 保存 prepare/commit anchor。先准备 witness，再提交权限库，最后确认 witness；确认丢失可按匹配的已提交代次恢复，witness领先而权限库未提交则停止准入，不猜测旧权限仍可用。仅恢复旧权限库会被检测；丢失、替换或损坏 witness 也拒绝准入。
- go-jose 4.1.6 签发/验证 ES256 JWT：固定算法/类型/issuer key、精确受众/访问设备/应用/服务、代次、唯一ID及到期。数字版本使用UInt64十进制字符串；严格JSON拒绝重复/未知字段、无效Unicode和不规范数值。验证后使用单调截止时间，同一租约重放不重新计时，旧序列或关闭代次的晚到回复不能恢复连接。
- `/control/v1` 实际 mTLS HTTP端点：读取固定握手设备、owner专用管理、访问者租约申请、发布端当前授权验证、双方当前状态批量检查。64KiB正文、有限超时/连接/并发；响应不回显原始异常或输入，不记录 token。

## 控制边界

消息使用独立的内部控制接口，供受信任 Agent 使用，不是业务应用的本机 SDK：

```json
{
  "kind": "request",
  "apiVersion": 1,
  "requestId": "00000000-0000-4000-8000-000000000001",
  "method": "lease.acquire",
  "params": {
    "visitorApp": "org.example.client",
    "target": {
      "publisherDevice": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
      "publisherApp": "org.example.publisher",
      "service": "files"
    }
  }
}
```

访问设备公钥由真实TLS身份产生，不能通过增加 `visitorDevice` 字段代领他人租约。`lease.validate` 只给租约中的实际发布设备返回当前 scope/version，并先验证签名和新鲜授权。`owner.device.set`、`owner.service.set`、`owner.grant.set` 需要 owner角色、原操作UUID和预期版本；`owner.operation.status` 只查询同一管理员原操作。除已知错误响应外，丢回复不代表未提交，不自动换号重做。

平台宿主必须先保护数据库、私钥和 witness 的路径/权限，禁止链接替换，持有实际运行时，保持 witness 不进入权限库备份。库不会自行给传入目录补上操作系统权限。witness是独立持久记录，不是TPM或硬件单调计数器；拥有服务器管理权限并一起回滚两个受保护数据库，不在“仅恢复权限库”的检测保证内。物理断电、故障文件系统及完整恢复工具尚未验收。

## 验证

需要 Go1.27，版本锁定于 `go.mod` / `go.sum`：

```text
cd gateway/control-plane
go test -count=1 ./...
go vet ./...
```

当前24个顶层测试通过：真实本地mTLS和HTTP管理/取租约、角色隔离、保持连接后的撤销、错误根/设备/公钥、严格消息、签名与范围、重放/期限、满额撤销、并发版本竞争、提交确认丢失、权限库恢复、独立witness重启/替换/损坏等。本机未运行需要CGO的Go race模式，不将普通并发测试写成 race 检查。

这些测试不等于FRP的实际 Login/NewProxy/NewWorkConn/Visitor/NatHole 已受约束。Agent私有运行时桥、FRP后台权限通知及中继活动流关闭、自动配对/续期/恢复、安装与长期网络验收继续开发，不能据此发布成熟1.0。设计见 [ADR0015](../docs/adr/0015-authenticated-transport-control.md)。

跨语言授权已由独立 .NET 和 Java 接收者使用真实 Go 签发的 ES256 令牌通过验证，包含已签名的异常声明和共享单调时钟边界。复现见 [授权互操作测试](../tests/server-authority/README.md)。这不表示 FRP 已接入该权限。

Windows SDK 与 Java 实际数据链路已接入显式服务端授权模式，在线撤销、换代和控制退出关闭通过同机测试；复现见 [真实控制与业务测试](../tests/control-authority/README.md)。这仍不是 FRP 实际路径验收。进程内 `Statistics()` 仅提供成功取租约、发布者验证和批次计数，不新增无认证的网络指标端点。
