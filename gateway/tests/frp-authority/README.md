# 真实 FRP 与 Windows 业务授权验证

从仓库根目录执行：

```powershell
./gateway/tests/frp-authority/verify.ps1 `
  -Go ./tools/linkgate-go/go/bin/go.exe `
  -Source ./upstream/frp-linkgate
```

脚本先从固定上游对象创建新副本并验证完整 client/server/NatHole 回归，再源码构建实际 frpc 与测试专用服务器。测试创建不同的不可导出 CNG 设备密钥，只向 Go fixture 发送公开 CSR；设备角色、服务和 grant 位于独立临时 bbolt/witness 中。

两条真实 frpc 进程通过私有 Named Pipe 和外层 Agent mTLS 接入，使用 wire v2/TCP mux。无关管道客户端被拒绝，不能增加外部 TLS peer。业务 SDK 再通过受保护的控制/数据管道、精确 Agent 路由及内层设备 mTLS，访问固定测试服务；服务器短期租约和发布端本地 grant 同时生效。

检查 64 MiB 长度/SHA-256、应用 challenge、请求 FIN/响应 EOF、缺少本地 grant、跨服务请求、禁止 direct 方法静默使用中继、活动流本地/服务器撤销、旧批准声明保留和自有进程回收。只处理生成目录与持有的 Job/CNG 密钥。

同一测试还运行独立 XTCP-only 访客端口：未配置中继回退，真实 frpc 通过两个自有回环 STUN 端点完成观测，SDK 经 QUIC 直接业务通道另传 64 MiB，并核验直连和中继的路径标签及活动流撤权。两端都至少从两个端点取得有效响应；这仍是同机回环网络，不是物理 NAT 分类或移动网络测试。

`artifacts/frp-native-authority-summary.json` 保存最新状态，启动即标为 running；每次运行的聚合结果独立保存在 `artifacts/frp-native-authority-runs/`，失败不沿用旧的通过摘要。原始构建回归和摘要在 `artifacts/linkgate-backend-frp/verified-*`。当前是本机真实 IPC/进程验证，SDK 消费者位于测试进程；它不替代独立签名应用、Android 原生桥、物理 NAT、四网络及长时间验收，也不更改安装版、云配置或手机画面。
