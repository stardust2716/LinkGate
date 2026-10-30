# LinkGate 目标接口的 JSON 契约

这些是目标 API 1、manifest 1 的设计草案，不是 0.2.3 运行时可以导入的新配置。完整语义以 [工程规格](../docs/SPEC.md) 和 [安全规格](../docs/SECURITY-SPEC.md) 为准。JSON Schema 使用 Draft 2020-12，`$id` 采用保留示例域名；验证时注册本地 schema，不访问该域名。

| 文件 | 定义 |
|---|---|
| [app-manifest.schema.json](app-manifest.schema.json) | 无秘密的应用声明，exports/imports 与协议 |
| [connection-policy.schema.json](connection-policy.schema.json) | 连接模式、预算、预热、中继限制 |
| [rpc-envelope.schema.json](rpc-envelope.schema.json) | 请求/成功/错误/事件信封；修改请求的 operationId |
| [file-service.app.json](examples/file-service.app.json) | Windows 发布端应用示例，示例 HTTPS 服务需要自己的合法测试证书 |
| [file-client.app.json](examples/file-client.app.json) | 独立 Android/Windows 访问端应用示例 |
| [connection-open.request.json](examples/connection-open.request.json) | 拟议的异步打开请求 |
| [connection-open.response.json](examples/connection-open.response.json) | 拟议 pending 结果；不是直接通道或业务成功证明 |

示例不包含设备私钥、注册 token、配对邀请或实际服务器。deviceId 是引用，不是授权凭据。导入 manifest 不会自动批准访问，示例也不能直接部署成当前产品。

## 校验边界

schema 校验字段类型、枚举和部分依赖；它不执行 OS 调用者验证、服务端授权，也不提供安全实现。RPC params/result 的方法语义按工程规格第 5 节继续校验，不能因为通过信封 schema 就执行请求。

实现还必须拒绝：重复 JSON 属性、畸形 UTF-8/BOM/未配对 surrogate/NaN/Infinity、超过字节/深度上限、重复 serviceId/alias、控制字符和非法 authority；检查策略默认值、directWaitMs 不大于 connectTimeoutMs、direct-only 的等待等于总预算且显式禁止中继、uint64 字符串不溢出、提醒阈值不超过硬限制、有限硬限制有 stop-relay 及用户批准。manifest 上限 48 KiB、独立策略上限 8 KiB，保留控制帧包装空间。缺省等待不超过实际总预算。JSON Schema 的整数定义可能接受数学上为整数的 `1.0`，本接口要求这些字段使用整数词法，解析层须额外拒绝浮点端口/版本；完整规范化见 [ADR0001](../docs/adr/0001-public-contract-normalization.md)。

默认值按模式计算，JSON Schema 的 default 不会自动修改输入。缺省 mode 不允许；其他缺省行为按 SPEC。HTTP 健康检查的 expectedStatus 是可达性检查，不自动证明业务身份校验通过。

验证器须启用 uuid/date-time 的 format 校验，并离线注册上述三个 `$id`。本轮 schema/示例验证使用 jsonschema 4.25.1 的 Draft202012Validator；应用的正式跨语言合同测试与运行实现将在后续里程碑交付。

## 重复本轮文档契约检查

在仓库根目录运行，验证环境放入被忽略的 artifacts，不安装到应用运行环境：

```powershell
python -m venv artifacts/linkgate-spec-venv
& ./artifacts/linkgate-spec-venv/Scripts/python.exe -m pip install -r gateway/spec/requirements-validation.txt
& ./artifacts/linkgate-spec-venv/Scripts/python.exe gateway/spec/check_contracts.py
```

[check_contracts.py](check_contracts.py) 检查三个 schema、四个示例、十个畸形/冲突形状样本及文档本地链接。它只验证设计工件，不测试或实现网关权限、跨字段语义、真实网络和运行 API。
