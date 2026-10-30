# 标准服务租约互操作验证

`verify.ps1` 从本仓库源码编译 Go 签发器、.NET 接收者和 Java 接收者，构建结束后才签发120秒令牌。所有生成材料位于受保护的忽略目录，测试结束清理；Go 只输出公开证书与签名令牌，不导出服务端私钥，不访问 ECS 或设备。

```powershell
./gateway/tests/server-authority/verify.ps1 -Go <go.exe> -JdkDirectory <jdk-root> -GradleDirectory <gradle-root> -AndroidSdk <sdk-root> -GradleCache <cache-root>
```

要求 Go 1.27、.NET10、JDK17和现有 Android Gradle 工具链。结果写入 `artifacts/server-authority-interop-summary.json`，当前 .NET 21项检查以及 Java 20项独立检查通过。覆盖实际 ES256、同密钥缺失SAN证书拒绝、错误签发者/代次/范围、签名仍有效但声明包含重复字段/未知字段/异常计数/指数/超长有效期/坏Unicode，以及墙钟停滞、单调时钟回退、重放、关闭后的晚到回复。不会把原始令牌、异常或设备信息打印到日志。

这是授权互操作测试，不是 Android 原生 Keystore、真实 FRP 打洞、物理设备、四网络性能或发布验收。原生设备证据另见 `tests/android-native`。
