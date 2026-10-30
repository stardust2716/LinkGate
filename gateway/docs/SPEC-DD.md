# SpecDD 开发流程

选定内容根为 `gateway/`，根规格是 [gateway.sdd](../gateway.sdd)。使用官方 SpecDD 框架 1.6、CLI 1.2.0；初始化时 CLI 已验证发行签名。未启用额外插件。既有 ReDesk 目录不纳入本次网关开发范围。

## 工作方式

1. 读取 `gateway/AGENTS.md`、`.specdd/bootstrap.md`、插件发现结果及 project/local overrides。
2. 对当前目标运行 resolve，按根 → 目录 → 同名局部规格读取约束，核对 Owns/Can modify。
3. 先修正规格歧义、数据样本与验收，再实现对应的一个局部任务或相关小组任务。
4. 检查代码、测试和 schema；成功后更新当前局部 Tasks，汇总证据到 [PROGRESS.md](PROGRESS.md)。
5. 失败或未执行的测试保留原状态，不将合同库通过当作 Broker、SDK、设备授权或手机实机验收。

`.specdd/bootstrap.md` 为框架管理文件，不手工修改。项目约束放在 `.specdd/bootstrap.project.md`；本机覆盖 `.specdd/bootstrap.local.md` 已被忽略，不提交个人环境配置。

规范化规则保存在 [ADR0001](adr/0001-public-contract-normalization.md)，后端安全执行关口保存在 [ADR0002](adr/0002-frp-authorization-boundary.md)。JSON Schema 和大篇产品文档继续保留，由局部规格引用；不把所有实现细节复制进根 `.sdd`。

## 工具安装与使用

在仓库根安装到被忽略的 tools，不安装全局包。需要 Node.js 22+、npm：

```powershell
npm install --prefix tools/specdd-cli --ignore-scripts --no-audit --no-fund specdd@1.2.0
```

已初始化的仓库无需重复 init。仅新克隆缺少框架文件时使用官方 CLI，并核对发布来源；当前正式内容已入库。进入 `gateway/` 后：

```powershell
../tools/specdd-cli/node_modules/.bin/specdd.cmd plugin list
../tools/specdd-cli/node_modules/.bin/specdd.cmd lint .
../tools/specdd-cli/node_modules/.bin/specdd.cmd resolve contracts/dotnet --sections all --depth 0
../tools/specdd-cli/node_modules/.bin/specdd.cmd inspect . --sections Purpose,Tasks
```

lint 检查 `.sdd` 语法，不代替程序测试或授权审查。resolve 的 References 是阅读上下文，不能当作写入授权。Specs 不通过 Owns 管理其他 `.sdd` 文件，修改范围按用户授权的任务和框架规则核对。

## 当前代码工作包的验证

仓库根目录运行：

```powershell
python gateway/contracts/vectors/generate.py
./gateway/contracts/verify.ps1 -JdkDirectory C:/path/to/jdk17
dotnet run --project gateway/windows/LinkGate.Tests/LinkGate.Tests.csproj -c Release
```

Android 旧版 JVM 回归使用既有 Android 构建环境执行 `gradle -p gateway/android testDebugUnitTest`，不需要覆盖安装 APK。完整检查包括规范化结果逐例比较、不可变模型和不回显输入的错误边界；当前不会运行网络连接或手机 UI 测试。

初始化和语法依据：[官方 SpecDD](https://github.com/specdd/specdd)、[CLI](https://github.com/specdd/cli)。工具版本和框架版本分别记录，不混同应用版本。

框架管理 bootstrap 保留原 Copyright 2026 Matiss Treinis and SpecDD contributors，按 Apache-2.0 使用；仓库已有的 [Apache-2.0 许可全文](../FRP-LICENSE.txt) 随源码保留。CLI 本身安装在忽略的 tools 中，不作为网关运行时分发；LinkGate 自有代码的许可不因此改变。
