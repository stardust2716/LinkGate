# ADR0006：Android 只读客户端可信绑定

2026-10-29。新增 Android library `client`，仅依赖共享 `control-ipc` AIDL 和便携 `public-contracts`，不依赖 Broker APK、数据库、FRP 或配对材料。开发 Broker 改用相同共享合同，不改旧版 app。

调用方提供可信 ComponentName 和完整的当前签名 SHA-256 集合。PackageManager 核验服务 enabled/exported、实际包 UID、签名、首次安装和最近更新时间；只接受非 shared UID。只接受当前 signer 精确匹配，不自动信任证书历史或旋转。包可见性由调用者 manifest 的 queries 提供；SDK 对默认开发 Broker 提供包查询声明，不请求 QUERY_ALL_PACKAGES。自定义 Broker 包需要调用者补充 queries。

SDK 在绑定前、ServiceConnection 回调后、每次 hello 前后复核身份快照及 AIDL descriptor。Binder 客户端没有通用的远端 UID 读取 API；这里信任 Android 对明确组件的绑定及已核验包，不宣称独立证明代理 Binder 的宿主 UID。参考 [Context.bindService](https://developer.android.com/reference/android/content/Context) 和 [ServiceConnection 生命周期](https://developer.android.com/reference/android/content/ServiceConnection)。

异步 bindAsync/helloAsync 两秒后完成超时结果并关闭，不自动重绑、重试或使用实验能力。原生 Binder/PackageManager 阻塞不能可靠强制中断，因此进程级固定最多2个工作线程、无工作队列、1个定时器、8个活动客户端，超时不创建替代线程。解绑提交主线程处理，资源租约在实际绑定清理完成后释放；若平台线程永久阻塞则明确拒绝新任务，不伪造资源已恢复。

当前 SDK 只有 hello，返回不可变且经过严格形状检查的能力和限制；未知能力仅保留，不自动启用。没有注册、批准、授权、连接或数据FD，工作台的数据 provider 仍须报不可用。

本阶段仅构建 AAR/APK 和运行 JVM 协议、签名策略与资源上限测试。原生绑定/死亡/超时/取消竞态、跨UID、包更新和手机后台行为尚未实测；完整 AUTH-02/03/04 保持未验收，不安装或操作手机。
