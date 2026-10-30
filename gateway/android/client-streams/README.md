# Android 私有流 SDK（开发版）

依赖 `implementation project(':client-streams')`，minSdk26。模块只依赖公开客户端、AIDL、合同和描述符库，不包含 Agent、SQLite、FRP、设备密钥或证书签发能力。为真实 Java Socket 适配声明 INTERNET，不修改应用的明文流量策略。当前还没有独立 Maven 发布元数据。

先按 [client 说明](../client/README.md) 从可信安装渠道取得 Agent 当前签名 SHA-256 集合，再显式绑定：

```java
ComponentName component = new ComponentName(
    "com.nebula.linkgate.agent", "dev.linkgate.agent.android.ControlService");
BrokerClient client = BrokerClient.bindAsync(context, component, trustedSigners).get();
client.helloAsync().get();
```

上述及后续阻塞示例应在应用自己的工作线程运行，不能放在 Android 主线程。先持久保存 manifest/操作号，再调用 `registerPendingAsync`，保存 `applicationRef`。imports 使用 `brokered`、`direct-only`、`allowRelay=false`，原业务使用 HTTPS/WSS 时保留 `logicalAuthority`。所有者分别审核声明和精确访问请求，发布端所有者另外发 grant。

```java
SecureReply.Scope scope = new SecureReply.Scope(
    savedApplicationRef, "files", confirmedPeerDeviceKey,
    "org.example.file-service", "files");
// 应用先持久保存 operationId；显式允许已协商的实验能力。
SecureReply.Connection state = client.openSecureDirectAsync(scope, operationId, true).get();
// 若 state.state 为 pending，按原 operationId 显式查询 getSecureConnectionStatusAsync。
// 只有 ready 后才附接；closed 时读取固定 error，不重播应用业务。
try (Socket socket = StreamClient.attachSocketAsync(client, operationId, true).get()) {
    // 使用 socket 输入/输出流；输出 FIN 使用 shutdownOutput，结束使用 close。
    // 原应用 HTTPS 可通过自身 SSLSocketFactory.createSocket(socket, logicalHost, port, true)
    // 包装，并保留自己的信任根、主机名校验和应用认证。
} finally {
    client.closeSecureConnectionAsync(operationId);
}
```

一次打开只允许附接一个描述符，不能跨 UID 借用原操作。附接确认须在十秒内完成。`NativeRefusalException.code` 表示经固定 Agent 复核的已知拒绝，不会误关其他会话；取消、坏回复、Binder 断联或 Agent 身份变化仍是可能已提交的不确定结果，保留原操作号，重新核验后显式查询，不自动重开连接。最终 `client.close()` 释放控制绑定，不代替应用关闭 Socket。

SDK 返回应用持有的 Socket，不返回可长期监听的回环端口。它不会自动替换 HTTP 库、改写 URL、关闭 TLS 主机名校验或附加业务 token。复杂 HTTP 客户端的专用适配器仍待完善；实际消费示例见 [native-consumer](../native-consumer/native-consumer.sdd) 及 [原生验收](../../tests/android-native/README.md)。

## 显式实验中继

服务器配置模式且安装包带有效私有运行时时，hello额外提供 `secure.stream.relay`。imports声明 `fast-connect` 或 `direct-first`、`allowRelay=true`，经用户批准后可使用 `client.openSecureRelayAsync(scope, operationId, true)`。其余状态轮询、Socket附接、半关闭和关闭方法相同。`state.path`为relay；pending只是已选路径，ready才已完成授权与数据通道。

这个显式方法没有执行自动策略，不会替换原 `openSecureDirectAsync`。两端仍须本地服务grant和服务器grant。SDK不包含FRP、私钥或后端配置；Agent后台工作受用户启动/停止控制。真实独立UID中继证据见 [ADR0018](../../docs/adr/0018-android-sdk-server.md)。

Agent可以在相同完整授权scope内共享底层runtime；每次SDK打开仍是一条独立私有流，关闭/取消一条不会终止另一条。应用无需管理进程、回环端口或共享池。

## 公开连接 API（显式实验）

新应用优先使用 `connection.api`；旧 `secure.stream.*` 方法保留兼容。先保存 `helloAsync()` 的协商结果并确认能力为experimental，应用明确传 `true` 同意实验接口。需要已启动且已配置服务器的Agent；不自动启动或退回未认证模式。

1. 持久保存打开操作UUID与完整参数，再用 `ConnectionOpenRequest.parse(utf8Json)` 严格验证。字段为 `applicationRef`、`importAlias`、`deviceId`、`publisherAppId`、`remoteServiceId`、`connectionPolicy`。deviceId是已核对的64位小写十六进制公钥哈希。策略可以收紧，但不能超出所有者已批准的import策略。
2. 调用 `client.openConnectionAsync(request, savedOpenOperation, true)`。返回 `ConnectionReply.Operation`；pending/reconciling时显式查询 `getConnectionOperationAsync(savedOpenOperation)`，直至succeeded或failed。成功结果中的 `connectionId` 不代表应用字节已发送。
3. 为附接另行持久保存UUID，调用 `attachConnectionAsync(connectionId, savedAttachOperation, true)`；成功得到 `streamRef`。同一操作重查得到相同引用，不创建第二个数据流。
4. 调用 `StreamClient.attachConnectionSocketAsync(client, streamRef, savedAttachOperation, true)`，得到应用持有的Socket。一个引用只消费一次，不能重放附接。按前文保留业务HTTPS/WSS、主机名校验、认证与应用级摘要；FIN使用shutdownOutput。
5. 使用 `getConnectionStatusAsync(connectionId)` 查询状态；结束时保存新的关闭操作UUID，再调用 `closeConnectionAsync(connectionId, savedCloseOperation, true)`，并释放Socket。

打开参数示意（每个身份/引用必须换成当前已批准值）：

```json
{
  "applicationRef": "11111111-1111-4111-8111-111111111111",
  "importAlias": "files",
  "deviceId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "publisherAppId": "org.example.file-service",
  "remoteServiceId": "files",
  "connectionPolicy": {"mode": "direct-first", "directWaitMs": 1500, "allowRelay": true}
}
```

中继额度/预热仍未在该模式执行，Agent拒绝这些请求，不静默忽略。`direct-only` 不回退中继；允许中继且存在受认证运行时时，direct-first在原始预算内尝试短LAN候选、XTCP，再按批准策略回退。

`ConnectionStatus` 将选择路径selectedPath与实际业务路径businessPath分开；还未转发字节时businessPath是none。directTx/directRx/relayTx/relayRx是Agent业务帧边界的无符号64位累计数，Java以BigInteger表示，不含TLS、FRP、IP开销，也不代表应用落盘或服务器账单。complete仅在双向FIN和排空后为true；异常、停止、重启或网络代次变化标不完整，保留已知计数。

`OperationFuture` 的不确定结果保留原UUID；重新固定Agent身份后查询原operation.status。不要因超时生成新UUID重拨或重发业务。默认网络变化结束旧流，新业务由应用明确打开；SDK不会跨代次自动重播。服务prepare、事件订阅、Windows正式API与独立Maven包仍待交付。实际示例见 `FormalConsumerTest`，证据见 [公开连接验收](../../docs/verification-v0.3-public-connection.json)。
