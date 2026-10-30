# ADR0024：持久连接、一次附接与业务计量

公开连接按实际调用主体、显式applicationRef及精确远端服务授权。原操作号与规范化请求摘要原子记录；重复请求不会重拨，不同参数冲突。打开操作在认证传输就绪时成功，之后关闭或重启不把原成功改成新的打开。就绪前失败的打开保留失败结果。

ConnectionStatus与不可变操作结果分开保存：pending → attaching → open → draining → closed/failed。selectedPath只在认证传输返回后设定；businessPath在正业务字节后才设定。字节为Agent业务帧边界的累计量，完整双向FIN和排空后才标complete；异常终止不推测尾部字节。

状态包含原bootId、networkGeneration、serviceRef、UTC毫秒时间和单调时钟计算的setupMs。快照有内部递增序号，旧快照不能覆盖新快照、关闭记录或新代次。重启保留已有计数和原选择，活动记录转为不完整终止；不使用新操作重新创建旧流。

connection.attach是独立持久操作，返回不授予其他主体权限的streamRef。实际Binder附接再核验真实调用方、当前授权、运行代次和一次性消费。重复相同附接操作可查询原结果，但不能再次得到描述符。丢失响应通过operation.status恢复，不盲目重做修改。

公开状态使用17个规定字段，引用和计数采用规范字符串，UTC采用 `yyyy-MM-ddTHH:mm:ss.SSSZ`。错误只含固定码和空details。原始候选、地址、路径、密钥及业务正文不进入状态。公开入口先作为显式实验能力验证；不因名字与目标API一致就宣布整个1.0完成。

Android实际实现和独立SDK验收见 [冻结证据](../verification-v0.3-public-connection.json)，调用方式见 [接入指南](../../android/client-streams/README.md)。默认网络回调只读观察，不改变路由或VPN；变化终止旧代次，应用显式新开连接，不重播业务。Windows正式实现、物理网络切换及长期压力仍开放。共享XTCP载体修复另见 [ADR0025](0025-owned-xtcp-carrier.md)。

状态和保留操作结果现有.NET/Java共享400例验证（其中126例为状态/操作），见 [跨语言证据](../verification-v0.3-status-contract.json)。时间固定为0001至9999年，拒绝闰秒、24点归一化和错误历日；Windows执行层仍待组合。

Windows持久层现为schema8，含实际SQLite连接日志/一次性附接和计量，见 [537项回归及原生证据](../verification-v0.3-windows-connection-journal.json)。合并快照可以跨过中间活动状态向前保存，避免慢半关闭流在重启前漏掉已知计数；状态倒退、计数下降及终态重写仍拒绝。Windows正式RPC/SDK尚未组合。
