# ADR0003：待批准注册与幂等事务

日期：2026-10-29。范围为 Windows 开发 Broker，不修改安装版或运行传输配置。

`app.register` 仅将规范化 manifest 写为 pending，返回 applicationRef、permissionDiff 和 approvalRequired=true。没有 grant、服务发布或数据连接。该工作包提交时没有已批准配置，不能把 pending 当活动配置。后续宿主声明审核基础见 [ADR0009](0009-windows-declaration-review.md)，仍不形成grant或业务连接。

调用者由管道模拟 SID 与持有的实际客户端进程句柄核验；读取进程令牌 SID、完整 EXE 路径并再次检查管道 PID 和存活。稳定协作范围为 SID + 大写规范 EXE 路径的 SHA-256。PID/创建时间用于本次真实进程核验，不作为重启后的主体 ID。路径不证明二进制签名或内容，解释器/宿主内程序共用主体；这不提供同 Windows 用户恶意代码隔离。引入包身份、内容/签名连续性或可信 UI 后必须版本化主体映射，不能自动沿用更广授权。当前全部保持未授权。

新增可选私有开发数据库。SQLite immediate transaction 将 pending manifest 与幂等成功结果一起提交，synchronous=FULL、rollback journal、parameterized SQL、无连接池。引用 [Microsoft 事务文档](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions)；锁冲突预算1秒。驱动锁定 Microsoft.Data.Sqlite 10.0.14，保留 NuGet lockfile。公开 manifest 不含凭据，但状态目录仍限制当前用户与 SYSTEM，拒绝 reparse points。

幂等键为 callerScope + operationId，摘要包含方法与规范化 manifest；requestId 不参与。相同参数返回原结果，更改参数返回 OPERATION_CONFLICT。operation.status 只查询当前调用者已提交结果，不返回其他主体记录。当前保留全部记录，不提前删除24小时内结果；每调用者500000、全局1000000条、1024份注册，满时拒绝新修改，旧操作仍可查询。数据库最大约1GiB，达到文件/磁盘限制返回存储错误。此工作包未运行100000短流压测，不代表未来所有方法的操作日志已经验收。

所有校验失败均不改变配置；数据库失败返回脱敏错误。没有外部副作用，事务未提交即可回滚，不需要伪造 pending/reconciling 网络操作。当前不提供已批准配置恢复、撤销、状态回滚防护、备份迁移或恶意同用户篡改检测。数据库 schema 版本未知时拒绝打开。
