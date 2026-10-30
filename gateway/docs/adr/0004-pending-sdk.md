# ADR0004：待批准注册薄 SDK

2026-10-29。将开发客户端移动到独立 LinkGate.Client 项目和命名空间（原 LinkGate.Broker.VerifiedPipeClient 尚未发布或进入安装版），仓库调用同步迁移。Broker 和 SDK 共同引用无平台依赖的 LinkGate.Control 帧与 JSON 解码，不互相引用；SDK 只再依赖公开 manifest 合同，没有数据库、FRP 或配对材料。

保留 HelloAsync 的 JSON 结果形式，新增 RegisterPendingAsync 与 GetOperationStatusAsync，仅返回经过方法形状校验的结果。注册须显式开启实验能力并由应用传入已持久保存的 operationId。SDK 不生成操作号，不自动重连、重发或批准权限。服务端错误作为固定消息的 BrokerRequestException 传播 code/stage/retryable，不复制原文或 details。未知能力不被自动启用。

修改请求开始写入后，IO、取消/超时或畸形响应都可能发生在服务器提交之后：客户端关闭，抛出带原 operationId 的 MutationOutcomeUnknownException。调用者显式重新核验连接，再查 operation.status；未知结果不能被当作允许生成新 ID 的证明。查询不触发修改。写入前取消或本地校验失败不制造不确定提交；握手/查询仍采用原来的失败关闭语义。

独立样例通过用户事先信任的 SHA-256 和持有的文件句柄核对本机开发 Broker，再启动并持有进程句柄。此流程不替代可信安装器、签名更新或自动发现。样例只是 pending 注册/查询，不打开业务通道；不等于独立文件/WebSocket/网络验收。Android SDK、批准 UI、grant 和数据流继续待开发。
