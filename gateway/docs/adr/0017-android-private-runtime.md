# ADR0017：Android 私有运行时和控制面时间基准

日期：2026-10-30。状态：实际模拟器组件验证通过；前台网关、公开SDK和策略组合继续开发。

## Android 进程边界

Android Agent 持有 Keystore 私钥，派生frpc不能读取私钥。新 `FrpRuntimeBridge` 在应用私有 no-backup 目录保留0600文件系统Unix Socket，核对实际 `SO_PEERCRED` UID/PID、持有的进程及存活状态，再建立固定服务器的标准mTLS。Agent写入应用/access前缀，frpc反向检查父Agent的PID和UID，失败不转为裸网络。

安装包包含源码构建的ARM64/x86_64 frpc与小型寿命守卫，每个ABI的两个二进制都有固定SHA-256。守卫先设置不可捕获的父进程死亡信号、复核父PID，通过自有stdout报告实际子PID，再exec绝对路径。桥接只启动这个已校验的原生目录文件，不执行shell或扫描/结束其他进程。待审核新声明被拒绝保留旧批准；批准代次变化、证书到期、停止和子进程退出则关闭自有网络和进程。

桥限制16个已接受连接、8个握手和有界线程池。握手3秒超时通过关闭原始socket实现；退出先结束进程再关输出流，避免被阻塞的读取妨碍清理。证书时间至少随单调时钟前进，并保留纳秒余数。拒绝诊断仅有固定阶段/错误标签和聚合计数，没有token、候选地址或原始日志。

标准自定义ALPN使用[Android API29接口](https://developer.android.com/reference/javax/net/ssl/SSLParameters#setApplicationProtocols(java.lang.String[]))，因此此模式要求Android10；原API26直接访问预览保留。Android访问角色不自动获得服务发布能力。

## 构建和依赖

Android HTTP依赖OkHttp5.5.1要求compileSdk37。保留该依赖的安全修复，采用[官方支持API37的AGP9.1.1 / Gradle9.3.1 / JDK17组合](https://developer.android.com/build/releases/agp-9-1-0-release-notes)，只提高Agent编译平台，不改变minSdk26和targetSdk36。SDK包路径为 `platforms;android-37.0`。Java集合/时间API通过固定desugar_jdk_libs2.1.4处理。

原生编译固定NDK29.0.13113456，API26目标及16KiB ELF对齐。产物生成于忽略的artifacts目录，由明确Gradle属性选择，不修改legacy运行时或物理手机。

## 原生暴露的时间差异

实测发现模拟器时钟落后服务器时，严格iat检查拒绝有效租约。这是之前JVM同机验证未覆盖的情形，不能通过放宽JWT签名/时间条件解决。

控制客户端仅在固定服务器mTLS、完整成功响应和requestId相关检查之后，读取标准HTTP Date。以一秒量化误差加整次请求耗时作为保守的服务器当前时间上界，只向前推进共享时钟；不从未验证token校时，不把时钟倒拨，不重置任何旧receipt或序号。延迟响应缩短可用时限，不能增加签名期限。服务器换代、已关闭控制代次和单调回退仍需重新建立，错误响应不提供校时依据。

## 验证边界

见[冻结原生证据](../verification-v0.3-android-frp.json)。API36 x86_64、实际Keystore/子进程/Unix身份/服务端租约/内层mTLS、64MiB/摘要/半关闭和撤权通过。Android这轮使用Agent内部PeerClient，不是公开SDK独立UID中继，也不证明物理ARM64、移动网络或Clash场景。那些门槛继续开放。
