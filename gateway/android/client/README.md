# Android 待批准注册客户端 SDK

安全访问 Agent 另增加 `openSecureDirectAsync`、原连接状态/关闭、`requestAccessAsync` 与私有描述符附接；完整数据使用见 [client-streams](../client-streams/README.md)。这些实验方法不改变下述旧 broker-dev 的只读/待批准范围。不同 UID/签名下的实际 Binder 接入已在 [原生 Agent 验收](../../tests/android-native/README.md) 执行，签名轮换、重装和工作资料的完整原生矩阵仍待验证。

开发 AAR，minSdk26。提供签名固定绑定、`helloAsync`、`registerPendingAsync`、`getOperationStatusAsync` 与 `close`。依赖共享 `control-ipc` AIDL 与 `public-contracts` Java库，不依赖 Broker 应用、registration-core、SQLite、FRP、配对配置或 INTERNET 权限。注册只是保存待批准声明，不能批准权限、发布服务或建立业务数据流。

## 源码接入

在现有 `gateway/android` 多模块构建中，消费者声明 `implementation project(':client')`，即可带入两个合同模块。当前未发布 Maven 包，单独复制 client AAR 不会自动包含 control-ipc 与 public-contracts；分发前还需制作完整依赖元数据。

```java
ComponentName target = new ComponentName(
    "com.nebula.linkgate.brokerdev", "dev.linkgate.broker.ControlService");
// 来自可信安装/发布流程的完整“当前签名证书 SHA-256”集合，64位十六进制。
CompletableFuture<BrokerClient> binding = BrokerClient.bindAsync(context, target, trustedCurrentSigners);
binding.whenCompleteAsync((client, bindError) -> {
    if (bindError != null) return;
    client.helloAsync().whenCompleteAsync((hello, error) -> {
        if (error != null) { client.close(); return; }
        // 检查 hello.capabilities；默认 Broker 没有 app.register.pending。
        // 保留 client 到应用自有生命周期，结束时显式 close。
    }, applicationExecutor);
}, applicationExecutor);
```

默认开发包查询已在库manifest声明；自定义 Broker 包需要消费者额外 `<queries><package .../></queries>`。必须使用完整组件名。无效 pin 参数同步拒绝，其余连接失败由 future 返回固定消息。**不能先从待连接包自行读取摘要再将其当作信任根。** 开发构建签名不等于正式发布身份，生产接入应从已核验的安装/更新渠道取得签名集合。

## 显式注册与不确定结果

开发 Broker 必须以 `-PlinkgatePendingRegistration=true` 构建。先成功协商 `app.register.pending`，注册再显式传入 `true` 开启实验调用；默认构建拒绝这项调用。应用先用 `ManifestContract.parse` 得到不可变 `ApplicationManifest`，并在自己的持久存储中保存 manifest 和操作UUID，**保存成功后**才能提交：

```java
// operationId 已由应用生成并持久保存；SDK 不生成操作号。
OperationFuture<RegistrationReply.Pending> write =
    client.registerPendingAsync(manifest, operationId, true);
write.whenCompleteAsync((pending, error) -> {
    if (error == null) {
        // 保存 pending.applicationRef 和 permissionDiff；approvalRequired 永远 true。
    } else if (error instanceof MutationOutcomeUnknownException ||
               (write.isCancelled() && write.mayHaveBeenSubmitted())) {
        // 保留 write.operationId；重新核验绑定并 hello 后，显式查询原操作。
        // 不自动重发、不换新号，也不把取消显示成“服务端已回滚”。
    } else if (error instanceof ControlReply.BrokerError) {
        // 只使用 code/stage/retryable；相同操作号不能改参数。
    }
}, applicationExecutor);
```

操作结果查询为 `client.getOperationStatusAsync(originalOperationId)`，返回不可变 `RegistrationReply.Operation`。查询仍须先协商 pending 能力；SDK 不自动重绑或重放。`OPERATION_UNKNOWN` 是当次查询的服务端结果，不能据此自动换号提交。

`OperationFuture` 保留不可变 `operationId` 与单调 `mayHaveBeenSubmitted()`。取消仍遵循标准 CompletableFuture 语义，产生 CancellationException；取消与进入transport的声明使用原子仲裁。取消成功且标志为false时，这次调用未进入transport；标志true时，即使调用失败/取消，服务端仍可能已提交。IO、坏响应、超时、关闭或请求后的身份变化在可能提交后返回带原ID的 `MutationOutcomeUnknownException`，不附带原始响应或异常原因。合法 Broker 错误可继续在当前连接查询；不确定错误关闭连接。

将 binding/request **原始 future** 和 client 纳入应用生命周期：取消原始 future 或显式 close 才触发 SDK 清理；取消派生 future 不会自动传播至上游。future回调应快速返回，阻塞操作使用应用自有 Executor，避免占用SDK定时/工作线程。

## 身份与资源边界

核验精确组件、当前全部签名、实际包UID、首次安装/更新时间和启用/导出状态；共享UID拒绝。签名历史不自动信任，签名轮换必须重新确认配置。绑定后及每次请求前后复核；Binder descriptor不符、绑定死亡/断连/空绑定都失败关闭，不自动重连。

bind和每次控制请求的future各有2秒超时，单客户端只允许一个活动请求，完成时先结算状态再通知调用者。原生 Binder 和 PackageManager 阻塞不能被可靠强制打断；SDK进程级最多2个工作线程、无工作队列、1个定时线程、8个活动客户端，超时不扩增线程。达到上限则 RESOURCE_EXHAUSTED；高层仍应使用应用Executor调度，不能假设任意长的同步回调链均有工作线程可用。接受或已尝试的绑定会提交主线程解绑，清理后释放租约，包括绑定迟到及返回false的路径；如果平台线程永久卡住，SDK不会假装资源恢复。

便携合同严格校验64KiB JSON信封、manifest 48KiB、requestId、版本、重复键、结果/错误互斥，以及结果中的唯一有界serviceId、原operationId与待批准标志。远端错误只暴露固定消息的 code/stage/retryable，不带原始message/details。详见 [ADR0006](../../docs/adr/0006-android-client.md) 与 [ADR0008](../../docs/adr/0008-android-pending-sdk.md)。

## 构建与验证

```powershell
gradle -p gateway/android :client:testDebugUnitTest :client:assembleDebug :registration-core:test :broker-dev:testDebugUnitTest :broker-dev:assembleDebug
```

SDK 34项JVM测试通过：原身份/资源/hello 16项、注册合同6项、生产会话引擎12项。会话测试通过受控transport覆盖协商、实验开关、关联校验、合法服务端错误、丢回复、超时、关闭、身份变化、取消前后、并发拒绝与显式原操作查询，并计数证明没有自动再次注册。注册核心15项SQLite测试和Broker10项检查另行通过。

产物为 `build/outputs/aar/client-debug.aar`，另有共享合同模块。测试不调用原生PackageManager/Binder：跨UID绑定/注册、绑定超时/取消/死亡/解绑竞态、安装更新/工作资料、Android SQLite和手机后台行为尚未实测，AUTH-02/03/04仍未验收。本次不安装或操作手机。
