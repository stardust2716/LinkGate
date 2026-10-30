# ADR0005：Android 只读 Binder 开发入口

2026-10-29。独立 applicationId `com.nebula.linkgate.brokerdev`，不导出或改动既有 GatewayService，不含 FRP、配对配置、网络权限或后台运行。仅显式绑定 `dev.linkgate.BIND_CONTROL_V1` 后调用 AIDL `byte[] request(in byte[] message)`；64KiB UTF-8 JSON 上限沿用 API1，响应 hello/all-unsupported。未实现注册、grant 或数据 FD。

在 Binder 工作线程立即捕获 getCallingUid/getCallingUserHandle，拒绝跨用户资料。随后以非阻塞额度限制每 UID 8 个、全局32个活动调用；空闲 UID 不留历史表。Binder 原生事务可能在入口前分配 Parcel，本实现的64KiB检查不是 Binder内核事务大小保证，也未承诺可中断 PackageManager 调用的2秒超时。

调用者包由 PackageManager 从实际 UID 解析。绑定应用具有[自动包可见性](https://developer.android.com/training/package-visibility/automatic)，不申请 QUERY_ALL_PACKAGES。开发入口只接受恰好一个包且未声明 sharedUserId 的 UID，核验包UID、当前签名、可用签名历史和首次安装时间，并重复读取一致性检查。API26/27只能观察当前签名；API28+读取SigningInfo。此快照不等于用户批准或完整签名谱系授权，不能用于声称卸载重装/UID重用/共享UID已验收。共享UID直接拒绝，后续必须设计完整身份集合变更和授权流程。

参考 [Binder 系统调用身份](https://developer.android.com/reference/android/os/Binder) 与 [UserHandle](https://developer.android.com/reference/android/os/UserHandle)。PID 不作为安全身份，不调用 clearCallingIdentity，也不把请求体的包名当作证明。

无法取得有效请求关联信息时使用公开 SDK 支持的 Binder 异常：IllegalStateException("RESOURCE_EXHAUSTED")、IllegalArgumentException("INVALID_REQUEST")、SecurityException("AUTH_REQUIRED")；跨用户资料同样使用固定 SecurityException。不引用系统专用隐藏异常类。合法信封内的参数/版本错误仍使用 JSON Error envelope。错误不包含输入、包名、证书或路径。初始入口阶段没有客户端SDK；后续 [ADR0006](0006-android-client.md) 提供了包/签名固定的只读客户端和共享AIDL，未扩大此入口的业务能力。

当前验证范围为 JVM 合同/额度测试和 APK 编译。未安装或操作手机，未运行原生 Binder、跨UID、工作资料、签名轮换、重装或洪泛测试；不能据此标记 AUTH-03/04 通过。全部业务能力保持 unsupported，完整0.3继续未完成。
