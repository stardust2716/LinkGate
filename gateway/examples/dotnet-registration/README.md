# 独立应用待批准注册示例

此程序仅引用 LinkGate.Client（及其公开合同依赖），输出中没有 Broker、SQLite 或 FRP DLL。它启动显式选择的开发 Broker、核验真实进程、提交待批准注册并查询原操作，不接管已安装服务。

仓库根运行（Windows、.NET10）：

```powershell
dotnet build gateway/windows/LinkGate.Broker.Console -c Release
dotnet build gateway/examples/dotnet-registration -c Release
$brokerExe = (Resolve-Path gateway/windows/LinkGate.Broker.Console/bin/Release/net10.0-windows/LinkGate.Broker.Console.exe).Path
# 本地自行构建并审核的产物才可在此作为信任来源；外来文件须使用发布者可信摘要。
$trustedDigest = (Get-FileHash -LiteralPath $brokerExe -Algorithm SHA256).Hash
$operationId = [Guid]::NewGuid().ToString()
# 先将 operationId 持久保存到应用自有状态；重跑/排障沿用原 ID。
./gateway/examples/dotnet-registration/bin/Release/net10.0-windows/Registration.Sample.exe $brokerExe $trustedDigest ./artifacts/sample-registration-state ./gateway/spec/examples/file-service.app.json $operationId register
# 超时/丢回复后，先仅查询；query 模式不会重新注册。
./gateway/examples/dotnet-registration/bin/Release/net10.0-windows/Registration.Sample.exe $brokerExe $trustedDigest ./artifacts/sample-registration-state ./gateway/spec/examples/file-service.app.json $operationId query
```

再次使用同一 manifest、状态目录和 operationId，返回原 applicationRef；更换参数但沿用操作号会冲突。实例运行时只有本例创建的随机后缀管道，结束会停止自己持有的 Broker。结果只证明独立进程可执行注册/查询，不证明文件、WebSocket、P2P、授权或数据流已验收。

此处必须事先信任整个本地构建目录：EXE 摘要仅防止误选，不能验证托管 DLL 和原生依赖，不构成完整可信安装/更新方案。不要从不可信 Broker 自报信息反向建立信任，也不要自行发现 PID 后宣称可信；样例核对操作者给定摘要并持有实际创建的进程句柄。
