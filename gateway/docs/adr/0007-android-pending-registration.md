# ADR0007：Android 显式待批准注册

2026-10-29。默认开发APK仍只读；只有构建参数 `-PlinkgatePendingRegistration=true` 开启实验 `app.register.pending` 和调用者范围内的 operation.status，不添加授予权限、发布、传输或网络功能。该工作包提交时只读Android SDK尚未开放修改方法；后续显式注册/查询封装见 [ADR0008](0008-android-pending-sdk.md)。

便携 PendingRegistry 通过 SQL session port 将 pending 声明与成功操作结果一起提交，匹配 Windows 的结果形状、每主体500000/全局1000000条操作/1024份声明容量。记录全部保留，不提前淘汰24小时内结果；达到容量拒绝新增，原操作仍可查询。同号同参返回原结果，改参冲突，旧操作重试不能覆盖新声明。SQL避免Android26不支持的UPSERT语法。

调用范围只能来自已有Binder UID/包/当前签名/可用历史/首次安装快照，JSON不接受身份或数据库路径。签名/重装变化形成新范围，不自动继承权限；共享UID仍拒绝。快照不是用户批准或已验收的完整身份生命周期。

Android适配器在no-backup私有子目录中打开每操作SQLite会话，mode0700、拒绝子目录/数据库/日志链接、无WAL、synchronous=FULL、1秒锁等待、约1GiB数据库限制。使用[immediate事务及显式成功提交](https://developer.android.com/reference/android/database/sqlite/SQLiteDatabase)，失败关闭时回滚；自定义corruption handler禁止系统默认删库重建，损坏状态保留供显式恢复。数据库只在注册/查询时惰性创建，hello不触碰存储。

源码中的便携注册核心与控制处理器由独立 registration-core Java模块构建；public-contracts显式排除这两类，客户端不依赖注册实现。JVM测试用 [Xerial SQLite JDBC](https://github.com/xerial/sqlite-jdbc) 3.53.4.0 执行同一核心SQL，依赖仅testImplementation，不打入APK。它能验证SQL事务、重开、并发和回滚，但不能替代Android SQLite适配器或原生跨UID验证；这些仍未执行，也未安装/操作手机。数据库迁移、断电、100000短流与完整AUTH-16仍待验收。

嵌套manifest的大小按原始JSON值字节计算，不能先重新序列化去掉空白再校验。控制解析器在完整严格校验时保留该值片段，确保49152字节边界与Windows一致。参数和整数词法仍由原有manifest合同检查，未知字段不会进入事务。
