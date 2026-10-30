# 真实控制授权与业务会话验证

此测试使用真正的 Go mTLS HTTP 控制服务、bbolt authority/witness、Windows 发布/访问 Agent、实际验证进程身份的 SDK Named Pipe，以及独立 Java 进程。Windows 使用非导出 CNG 密钥；Java 保持自己的 P-256 私钥在进程内。两端只传公开 CSR，Go 签发设备证书并持有根/服务端私钥，不输出私钥。

```powershell
./gateway/tests/control-authority/verify.ps1 -Go <go.exe> -JdkDirectory <jdk-root> -GradleDirectory <gradle-root> -AndroidSdk <sdk-root> -GradleCache <cache-root>
```

需要 Go1.27、.NET10、JDK17及现有 Android Gradle 工具链。脚本编译所有 fixture 后开始测试，通常约80秒，结果为 `artifacts/control-authority-interop-summary.json`。所有网络仅限自有回环监听器；临时目录受权限保护、子进程固定摘要并置于 kill-on-close Job，结束删除本次目录及 CNG 测试密钥。

验证内容：

- 两端真实标准 mTLS 取租约，Windows 独立复核 Java 令牌；固定服务器 SPKI、精确 scope、角色、版本与启动代次，取消中的实际 TLS 握手。
- 有服务器授权但无发布端本地 grant 时拒绝；缺少服务器租约也拒绝。业务 SDK 不取得服务器 token，仍通过私有数据管道。
- Windows SDK 和 Java 客户端各接收64 MiB，验证 SHA-256、原应用 challenge、请求 FIN 和响应完整性。预览控制管道的2秒空闲限制保持；测试在新操作前显式重新绑定并核验同一 Agent，不重发旧变更。
- 两端活动业务保持65个真实秒，观察服务端实际增加至少两次取租约和发布者验签，后端连接数不增加，证明续期没有重建业务。
- 同时128个授权守卫批量检查；仅撤销服务端 grant，两个实际阻塞数据流均在5秒内终止。仍有效的旧签名 token 不能重新打开服务，重新批准也不能复活旧 guard。
- 更换同密钥服务器的启动代次后，旧控制客户端和数据流关闭；显式建立新代次后恢复。服务端退出后，实际阻塞流也因控制失效关闭。

这是实际同机跨语言证据，Java 进程不是 Android Keystore 或原生 Binder 验收；Windows fixture 的 SDK 和宿主在同一进程中通过真实 IPC，独立进程/UID 的其他证据见 `examples/secure-direct` 和 `tests/android-native`。这里没有 FRP、真实 NAT、物理设备四网络、24小时或72小时验收。测试通过不能把完整1.0标为完成。
