# Windows 本地声明审核命令行

独立开发工具，需 Windows 和 .NET 10。可浏览、批准、拒绝或撤销本机声明候选，并在操作中断后查询恢复。**批准候选不创建设备／服务 grant，不发布服务，也不建立业务数据连接。** 当前应用身份按实际 SID+EXE 路径划分；签名/二进制身份仍未核验，应用显示名和 applicationId 由应用自行申报。

工具不打开管理端口，不改变应用 RPC，不安装服务或更新运行版。当前交付开发 EXE 和源码，不是正式安装器。

## 构建和启动

在仓库根目录执行：

```powershell
dotnet build gateway/windows/LinkGate.Review.Console -c Release
& '.\gateway\windows\LinkGate.Review.Console\bin\Release\net10.0-windows\LinkGate.Review.Console.exe' 'C:\path\to\private-state'
```

`private-state` 必须是你明确选择的开发 Broker 状态目录，里面已有 `registration.db`。可通过[独立注册样例](../../examples/dotnet-registration/README.md)创建实验状态；不要把路径替换为旧版 LinkGate 配对目录或其他应用目录。直接双击且不传参数只会显示用法。

必须在交互终端运行，不能用管道、重定向输入/输出、自动 yes 或批准参数。启动先显示边界，只有输入 `OPEN` 才打开数据库、收紧私有权限并在需要时迁移。当前 schema 为5；旧状态容纳不下撤销预留时，迁移会整体拒绝，工具不删除记录或自动扩大限额。新增设备与服务授权由独立 [Owner 工具](../../examples/secure-direct/README.md) 管理，本工具仍仅决定声明。

分发开发构建时需保留完整输出目录及 DLL/SQLite 依赖，不能只复制 EXE。构建产物不含 FRP 或配对秘密。

## 审核步骤

1. 输入 `list` 浏览应用引用，`next` 查看后续页。应用新增可能落在已翻过的页前，`list` 可刷新。
2. 输入 `show <applicationRef>`。核对完整 pending 声明、先前批准的声明、调用范围摘要、目标端口、协议、策略和两个版本。
3. 输入 `approve`、`deny` 或 `revoke`。工具显示目标、操作UUID和本次确认短语；完整输入该短语才提交。空输入、EOF或不匹配都取消。
4. 成功后用 `show <applicationRef>` 检查当前状态。`operation <operationId>` 返回的是历史结果，不代表现在仍然批准。

| 命令 | 效果 |
|---|---|
| `list` / `next` | 每页最多20个引用；清除旧选择 |
| `show <UUID>` | 完整展示声明并选中快照 |
| `capacity` | 显示宿主日志用量、可用额度和撤销预留 |
| `approve` | 保存确认的整份声明候选，不授予网络权限 |
| `deny` | 拒绝当前 pending；保留先前批准 |
| `revoke` | 清空先前批准；逻辑额度满时可使用已有预留 |
| `operation <UUID>` | 只查询宿主声明审核操作历史；应用申请操作由SDK查询 |
| `recover` | 查询并核对待恢复回执，不重发决定 |
| `abandon` | 原操作查询为未知后，另行确认以结束本地等待 |
| `quit` | 退出；不删除状态或未完成回执 |

确认、取消和失败后都需要重新 `show` 才能再决定；数据库版本检查拒绝展示后发生的并发变化，包括 A→B→A。命令和确认短语只接受ASCII，单行最多128字符。应用文本通过ASCII JSON转义显示，中文等字符可能呈现为 `\uXXXX`，不会把应用内容当作终端控制或提示指令。

## 中断恢复

提交前，工具将操作UUID、应用引用、两个版本及决定写入 `.last-review-intent.json`，写穿、刷新后原子替换；文件不含 manifest、进程路径或凭据。成功结果经过核对后才标记已确认。下一次明确确认的新决定可以替换已确认回执，但不能覆盖未完成回执。

如果提交成功后回执确认失败，或程序中途退出，下次启动会显示待恢复操作并阻止新决定。执行 `recover`：找到且匹配原操作时只确认回执，不再次修改数据库。若为 `OPERATION_UNKNOWN`，工具不会自动重发；请先 `show` 核对当前状态。必要时由你执行 `abandon` 并输入单独确认短语，仅解除本地等待，**不改变 Broker 状态，也不保证原操作已回滚**。之后的新决定仍要重新展示、确认并保存新操作号。

若已有结果，`abandon` 会拒绝并要求 `recover`。回执损坏或结果不匹配时不自动清空；保留数据库、回执和操作号，退出后人工核查。当前没有自动修复或清理入口。

目录须具有 Broker 的私有受保护 ACL；回执/锁文件拒绝向当前用户和 SYSTEM 以外的主体授权。`.review-console.lock` 用不共享文件句柄保证同目录一次只有一个审核控制台。退出释放句柄，空锁文件保留属于正常状态。交互检查仅防止误用自动化，不是不可伪造的人类在场证明或同用户恶意代码隔离。

## 验证范围

```powershell
dotnet run --project gateway/windows/LinkGate.Broker.Tests -c Release
```

本控制台工作包最初通过307项整套检查，加入待确认权限申请后整套当前381项通过。生产工作流通过脚本终端 port 和真实私有 SQLite/回执验证：完整展示、确认/取消、并发旧快照、回执写入失败、确认失败后的重启恢复、结果不匹配、未知结果的显式放弃、满额撤销、私有权限/独占锁、损坏回执、输入与分页边界。

另在自有样例状态执行了原生PTY自动冒烟：OPEN、浏览/容量、完整展示、确认批准、再次展示、确认撤销和退出；最终决定版本2、操作记录3条、无批准声明且回执已确认。它是自动化终端验证，尚无真人交互验收。

实际 EXE 的重定向拒绝测试确认：旧schema数据库字节未改变，未创建目录/锁。没有执行原生交互终端真人验收、物理磁盘满/断电、跨用户或Android实机。正式安装信任、二进制身份连续性、逐服务/设备授权和业务数据通道仍未完成；本工具不代表完整0.3验收。设计见[ADR0011](../../docs/adr/0011-local-review-console.md)。

## 只读检查待确认权限申请

应用通过显式开启的Windows[SDK](../LinkGate.Client/README.md)提交申请后，可以使用 `requests` / `requests-next` 分页列出申请UUID，再用 `request <UUID>` 查看目标引用、说明、已审核import快照、绑定版本及pending/superseded状态。

这些命令只读，且清除此前选中的声明。`approve`只针对重新show的声明候选，不能批准权限申请。屏幕中的设备/发布者/服务标识仍是未认证文字；没有grant、token或业务通道。原申请历史成功也不能代替当前状态。详情见[ADR0012](../../docs/adr/0012-pending-grant-requests.md)。
