# Windows / Java 安全传输互操作

本测试运行实际 Windows 发布端与 JVM Agent 角色，检验 Android 可复用的 Java 传输实现。它不读取已安装版本，不访问 ECS，也不操作手机。

```powershell
./gateway/tests/secure-interop/verify.ps1 -JdkDirectory '<JDK17>' -GradleDirectory '<Gradle8.13>' -AndroidSdk '<SDK36>' -GradleCache '<专用Gradle缓存>'
```

Java 端自行生成 P-256 私钥和 CSR；Windows 所有者只签发 CSR，并信任公开证书。双向 TLS 和公钥固定通过后，Windows 发布端仍需精确设备/应用/服务 grant。测试验证未授权拒绝、错误对端公钥拒绝、64 MiB 摘要校验、请求半关闭后完整接收响应、活动阻塞读撤销与撤销后新建拒绝，并检查拒绝请求未触达后端。

临时软件密钥只用于 JVM fixture，位于所有者创建的受保护 artifact 子目录，结束时清理。Android 实际使用 Keystore 密钥句柄；本测试不等于 Keystore、Binder、独立 Android UID、手机或跨 NAT 验收。机器摘要明确保留这些未测试字段，保存于忽略的 `artifacts/linkgate-java-interop-summary.json`。
