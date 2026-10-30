# ADR0014：Android 原生授权与私有数据通道

日期：2026-10-29。状态：实现及本地模拟器集成验证完成；真实手机、跨网络、完整发布仍待验收。

Android 访问 Agent 使用独立包 `com.nebula.linkgate.agent`，不升级或替换已安装的 0.2.3。平台代码负责 Keystore、真实 Binder UID/签名来源、SQLite、前台服务和所有者界面；`agent-core` 保持可在真实 SQLite 上验证的持久授权规则，`transport-core` 负责与 Windows 互操作的标准 TLS 通道。SDK 只依赖公开合同、AIDL 和描述符原语。

设备自行在 Android Keystore 生成 P-256 私钥；所有者只接收 CSR、返回叶证书和公开根。导入前验证证书用途、名称、有效期、根、公钥和持有证明，并绑定私有数据库。禁止悄悄替换遗失密钥或接受另一个根。TLS 栈会自行计算摘要，因此 Keystore 同时授权 SHA-256 和预摘要 ECDSA 签名，并在生成请求及加载时证明两条签名路径可用；这不改变 TLS 加密套件或导出私钥。原生测试确实发现了仅允许 SHA-256 的密钥不能完成 Conscrypt 握手，修复依据为 [Android 官方 setDigests 说明](https://developer.android.com/reference/android/security/keystore/KeyGenParameterSpec.Builder#setDigests(java.lang.String...))。

Agent 从 Binder 同步捕获 UID/用户资料，再从 PackageManager 复核单一包、安装代次和平台已验证的签名历史。连续单签名轮换保留原签名锚；重装、不连续签名或多签名集合变化形成新主体。注册仅产生待审核声明，本地精确访问请求还需所有者批准；Windows 发布端另行发放设备/应用/服务 grant。普通应用不能通过 Binder 或 Intent 调用管理操作。所有者操作先写私有、同步落盘的原操作回执，未知结果阻止新的管理修改，恢复只查询原操作。

批准和撤销推进 authority epoch；拒绝新 pending 声明仅推进审核决策版本，保留旧批准状态和活动授权。Windows 存储因此升级为 schema 6，Android 新库仍是独立 schema 1。旧 Windows schema 5 迁移将现有决策版本作为 epoch，不改旧 grant stamp，避免把原本已失效的 grant 恢复。批准提案继续比较完整审核版本，拒绝操作仍使旧提案过期。

数据通过一次性、调用方绑定的 Binder 描述符交付。`StreamAttachment` 是固定格式 Parcelable；返回成功才携带 PFD，并向 PFD 传播 `PARCELABLE_WRITE_RETURN_VALUE`，让服务端原始句柄在实际封送后释放。已认证的拒绝是固定状态，不冒充传输失败，也不误关闭其他 SDK 会话；传输不确定或对端变化仍关闭并保留原操作号。禁止依赖 SDK 不公开的框架异常类型。

Agent 在返回描述符后等待十秒内的 attach 确认，使用内核 shutdown 区分输出 FIN 和完整关闭，背压不阻塞控制心跳。撤销先关闭所有对应传输与描述符，再异步写关闭状态；有界的启动、复制、控制、定时及持久队列分别管理，存储延迟不会把一批连接的撤销串成无界等待。前台服务仅由所有者界面/通知控制，绑定 SDK 不会自动启动联网。

薄流 SDK 在建立阶段临时创建回环连接，核对已经持有的两端地址后立即关闭监听器，只向应用返回真实 Java Socket。它保留标准 TLS 包装能力、FIN 和背压；应用关闭 Socket 时立即关闭底层描述符，避免远端不响应 FIN 时留下后台复制线程。业务 TLS/Host/认证继续由原应用负责。

复现入口是 [原生验收](../../tests/android-native/README.md) 和 [Windows/Java 互操作](../../tests/secure-interop/README.md)。原生验收使用独立端口、禁用物理 USB 发现的无界面模拟器；安装前检查模拟器和架构，测试应用 UID 和签名与 Agent 均不同。Windows 启动器在恢复子进程前分配 kill-on-close Job，所有者退出或崩溃时回收所持进程树。模拟器结果不替代真实 ARM64、厂商后台策略或四网络验收。

完整版本仍需将新权限系统接入 FRP 协调及中继、实现连接策略/网络代次、完整配对/续期/反回滚、正式安装和签名更新，以及长期负载验收；这些不因原生通道通过而自动完成。
