# LinkGate 公开应用合同基础层

这里提供纯 .NET 10 和 Java 17 的 manifest、policy 和公开连接参数的严格解析、不可变模型与共享一致性检查。它不建立连接、不读取配对秘密，不等同于完整网关或 SDK。

| 目录 | 内容 |
|---|---|
| dotnet/ | 可移植 LinkGate.Contracts、LinkGate.Control 及 console corpus runner |
| java/ | 可供 Android 集成的无平台依赖 Java 合同及 runner |
| vectors/ | 精确 UTF-8 输入的 Base64、显式预期结果及可重复生成器 |

规则以 [ADR0001](../docs/adr/0001-public-contract-normalization.md) 为准。标准行为包括拒绝重复属性、未知字段、BOM、坏 Unicode、整数伪装、非法 authority、溢出/冲突预算；按模式归一化超时、预热和中继声明。合同校验通过不等于调用者已授权。

## 运行检查

在仓库根目录，使用 .NET SDK 10、JDK17+ 和 Python3：

```powershell
./gateway/contracts/verify.ps1 -JdkDirectory C:/path/to/jdk17
```

也可通过 JAVA_HOME 指定 JDK。输出默认位于被忽略的 `artifacts/linkgate-contracts/`，包含两端逐例结果和结构一致性汇总；不会连接 ECS 或操作手机。

语料通过 `python gateway/contracts/vectors/generate.py` 重建，随后必须重新运行两端检查。每次增加边界先补输入和预期结果，不由某一实现的当前输出自动生成预期。字段名/对象键顺序不参与比较，输入词法和规范化后的值参与比较。

## 库入口

- .NET：`LinkGate.Contracts.ManifestContract.Parse` 与 `NormalizePolicy`；结果提供 `ToJson()`。
- Java：`dev.linkgate.contracts.ManifestContract.parse` 与 `normalizePolicy`；结果提供 `toJson()`。

两端另有 `ConnectionOpenRequest.Parse/parse`，明确 applicationRef 和精确远端引用；`WithinApproved/withinApproved` 验证请求策略只收紧已批准import。规则见 [ADR0023](../docs/adr/0023-connection-request-policy.md)。比较通过不代表实际调用方、grant或服务器授权已经通过；正式 `connection.open` 仍需后续运行接线。

输入是原始 UTF-8 字节。校验失败固定 `INVALID_REQUEST`，不得依赖某种语言的完整异常文案。需要配对、网络或数据连接的应用仍应等待后续 Broker/SDK 工作包，不能用本模块冒充网关客户端。

## .NET 实验权限申请值

新增 `LinkGate.Contracts.PendingGrantRequest` 不可变值及严格请求/响应形状检查，用于Windows显式开启的待确认权限申请。它只描述applicationRef、importAlias、目标设备/发布者/服务引用和非敏感说明，不产生grant或网络连接。规则见[ADR0012](../docs/adr/0012-pending-grant-requests.md)。

这项值类型最初用于.NET实验入口，Android通过其本机Binder合同和实验SDK接入。当前共享274例包含manifest/policy与65例连接请求/策略比较，两端通过不代表所有grant消息已完成跨平台合同验收；本机权限申请由各平台自己的测试覆盖。
