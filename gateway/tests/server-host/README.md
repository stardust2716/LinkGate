# Linux 持久服务器原生验证

先执行 `gateway/backend-frp/verify.ps1`，取得包含 `cmd/linkgate-server` 的新派生目录。然后在仓库根目录执行：

```powershell
./gateway/tests/server-host/verify.ps1 -Go /absolute/path/to/go.exe -BackendDirectory /absolute/path/to/verified-derived-backend -WslDistribution Ubuntu-24.04
```

工具将编译 Linux 测试和 `linkgate-server-linux-amd64`，在明确选择的本地 WSL 中执行。权限库、见证和身份均由测试在 Linux 临时目录创建并回收；不能用挂载 Windows 目录的权限表现代替 Linux 私有文件测试。

覆盖严格配置、独立见证、重复/缺失状态、私有权限、链接、身份替换和锁；真实服务器启动后进行所有者 mTLS hello、拒绝未配对身份与原始旧 FRP 输入、验证停止和存储变化时关闭两端口。测试证书/私钥只属于临时 fixture，正式服务器入口不生成或接收这些 fixture 权限。

结果与固定摘要位于打印的独立忽略目录。它不是 ECS 部署、生产安装、业务 P2P 或完整发布验收。
