# Android 只读 Binder 开发入口

独立包 `com.nebula.linkgate.brokerdev`，版本 `0.3.0-dev`。不替换 `com.nebula.linkgate` 0.2.3，不包含 FRP、配对文件、INTERNET/QUERY_ALL_PACKAGES 权限、启动页面或后台网关。

显式绑定 action 为 `dev.linkgate.BIND_CONTROL_V1`，组件为 `dev.linkgate.broker.ControlService`。共享 AIDL 位于 `../control-ipc/src/main/aidl/dev/linkgate/broker/ILinkGateControl.aidl`，仅有同步 `byte[] request(in byte[] message)`。输入为最多64KiB的API1 UTF-8 JSON；当前只支持 `hello`，其他方法返回 CAPABILITY_UNSUPPORTED。所有产品能力均为 unsupported，不能注册、批准、发布、访问或取得数据 FD。

上述为默认构建。显式 `-PlinkgatePendingRegistration=true` 可启用实验待批准注册，见下文；无论构建方式，网络/授权/数据能力都不会因此开放。

Binder线程先捕获实际UID和用户资料，再经过每UID8、全局32个活动调用的非阻塞额度。通过PackageManager读取实际UID的单包身份、当前签名、可用历史及首次安装时间，并复读一致性；多包/声明shared UID/跨资料/身份不可用均失败关闭。JSON不能指定UID或包名，公开响应不包含身份快照。API26/27没有签名历史；当前快照不等于批准或完整身份生命周期验证。

无关联信息的错误使用固定公开SDK异常：INVALID_REQUEST 为 IllegalArgumentException，RESOURCE_EXHAUSTED 为 IllegalStateException，AUTH_REQUIRED 为 SecurityException；合法信封内的参数/版本错误仍为 JSON envelope。不访问手机UI、不修改Clash或旧版服务。完整边界及来源见 [ADR0005](../../docs/adr/0005-android-binder-bootstrap.md)。

## 构建与验证

JDK17、Gradle8.13、SDK36/AGP8.10.1，在仓库根目录：

```powershell
gradle -p gateway/android :broker-dev:testDebugUnitTest :broker-dev:assembleDebug :app:testDebugUnitTest
```

产物 `build/outputs/apk/debug/broker-dev-debug.apk` 使用开发签名；它是无启动页面的开发组件，不是可覆盖升级的正式安装包。本次只构建，未安装到手机。

本地10项JUnit测试（6项协议、4项额度）通过，包含重复键/非法编码/UUID/版本/未知身份字段/字节深度值数边界，以及64线程同UID并发与10000个UID依次调用后无历史残留。便携manifest的209项语料另行验证通过。

这些测试不执行原生Binder或PackageManager：跨UID、不同签名、签名轮换、卸载重装、工作资料、手机后台策略、Binder洪泛仍未验证。现已提供独立 [待批准注册客户端SDK](../client/README.md)，但用户授权和数据流仍未完成，不能把APK/AAR编译或Java单测当作 AUTH-03/04 已通过。

## 显式待批准注册实验

```powershell
gradle -p gateway/android :registration-core:test :broker-dev:assembleDebug -PlinkgatePendingRegistration=true
```

这只改变独立开发包。hello会额外声明 `app.register.pending=experimental/PENDING_ONLY`，其余产品能力仍unsupported。app.register 只接受 manifest 和根信封 operationId；operation.status 只接受参数中的 operationId，按实际 Binder 身份快照隔离。响应形状和 Windows 相同，approvalRequired 永远true，记录差异仅以先前pending为基线。

private no-backup 子目录采用Android SQLite逐操作会话，立即事务中一起提交声明和成功结果；失败回滚，重复操作不覆盖新声明。默认hello和校验失败不打开数据库。原操作至少保留24小时，当前实现不删除记录；满额拒绝新增，旧结果仍可查询。更换参数不得沿用操作号，超时后应先显式查询，不能自动产生新号重放。

完整规则见 [ADR0007](../../docs/adr/0007-android-pending-registration.md)。注册核心独立为 `registration-core` Java模块，客户端依赖的 public-contracts 不包含注册实现。15项桌面SQLite JDBC测试覆盖真实事务/重开/并发/回滚，但不运行AndroidSQLite或Binder；JDBC是测试依赖，不打入APK。Android SDK现已提供显式注册/查询与未知结果处理，见 [ADR0008](../../docs/adr/0008-android-pending-sdk.md)；用户批准和业务数据通道继续待开发。本轮只验证两种构建，不安装到手机。
