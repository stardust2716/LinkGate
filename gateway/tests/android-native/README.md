# Android 原生网关验收

此入口只操作自行创建、单独 ADB 端口的无界面模拟器。安装前核对 `ro.kernel.qemu=1` 和 x86_64，禁用物理 USB 发现；不操作已连接手机或默认 ADB 服务。每次仅卸载四个明确命名的测试包，重新建立测试身份。

前提：Windows WHPX 可用、.NET10、JDK17、Gradle9.3.1、SDK36及37.0编译平台、Android emulator 及 `system-images;android-36;default;x86_64`。首次运行可能下载 Maven 依赖；模拟器镜像须预先通过官方 SDK Manager 安装。

```powershell
./gateway/tests/android-native/verify.ps1 `
  -JdkDirectory '<JDK17>' -GradleDirectory '<Gradle9.3.1>' `
  -AndroidSdk '<SDK36>' -GradleCache '<专用Gradle缓存>'
```

脚本构建主 APK 和 instrumentation APK，启动持有 Job 的模拟器/隔离 ADB，Android 自行生成不可导出私钥及 CSR，Windows 所有者仅签发公开证书。所有者管理动作在测试 APK 中运行，不给生产 Agent 增加管理后门。消费者只依赖公开薄 SDK，使用与 Agent 不同的签名和 UID。

覆盖：错误 Agent 签名拒绝、实际 UID/签名不同、本地声明/精确授权、发布端未发 grant 的固定认证拒绝且后端零连接、双向 TLS、一次性 Binder 描述符、64MiB SHA-256、请求 FIN 后完整响应、阻塞读取时活动授权撤销。应用 fixture token 经私有标准输入送入测试应用，不出现在命令行或证据。结束/超时回收自有进程树，不按进程名或端口杀程序。

原始结果在忽略的 `artifacts/secure-android-native-run.log`，机器摘要在 `artifacts/linkgate-native-acceptance-summary.json`。Windows/Java 纯协议互操作另见 [secure-interop](../secure-interop/README.md)。模拟器结果不替代真实 ARM64、厂商后台/休眠、网络切换或移动网络/Clash 验收，也不代表完整 1.0 可发布。

新认证FRP组件复现：提供由 `backend-frp/build-android.ps1` 生成的 `-AgentRuntimeDirectory` 与源码构建的 `-AuthenticatedBackendDirectory`，使用Gradle9.3.1、SDK37.0。脚本仍只启动自有API36模拟器并隔离ADB5038；同时运行内部桥和独立UID/签名公开SDK中继，验证64MiB、同应用双流、两级撤权、损坏配置拒绝与停止竞态。内部guest等待时长和主机触发撤权后的五秒预算分别记录；二者不能混用。
