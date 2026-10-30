# LinkGate Android

独立包 `com.nebula.linkgate.agent`，Android 8/API26 起。支持已知可达 IPv4 的安全直连访问、Keystore 设备身份、本地所有者审核、公开 SDK 和私有 Binder 数据流。前台网关已接入所有者确认的服务器配置和公开SDK显式加密中继；未配置服务器时保留原已知IPv4预览。服务发布、正式邀请配对或签名升级流程未交付，不能当作完整1.0。

构建 `:agent:assembleDebug`，输出 `build/outputs/apk/debug/agent-debug.apk`。开发 APK 签名必须通过可信渠道交给 SDK 消费者；它不是生产签名。不要覆盖已有 Agent 身份来绕过证书错误。

## 所有者配置

1. 安装并打开 Agent，在“设置 → 设备身份”选择生成/导出 CSR。设备私钥留在 Keystore。
2. 在受信任电脑使用 [所有者工具](../../examples/secure-direct/README.md) 的 `issue <owner-dir> <CSR-file> <new-certificate-file>` 签发证书。只把设备叶证书和公开 `owner-root.pem` 传回 Android；不复制所有者私钥。
3. 在“设置 → 设备身份”导入设备证书及公开根，独立核对界面显示的根指纹后确认。
4. 导入发布设备的公开配置，核对设备公钥、IPv4、端口和预期版本。格式如下：

```json
{
  "certificate": "-----BEGIN CERTIFICATE-----\n发布设备的公开证书\n-----END CERTIFICATE-----\n",
  "host": "192.0.2.10",
  "port": 17443,
  "expectedRevision": 0
}
```

`192.0.2.10` 仅为文档地址，实际使用可达发布端 IPv4。初次信任版本为 0；重新信任须填当前版本，并重新批准对应本地访问请求。公开配置仍需从可信途径核对，不能相信来源不明的设备身份。

5. 如需服务器授权模式，先在“设置 → 服务器”选择“导入服务器配置”，确认以下公开JSON的服务器证书指纹及端口：

```json
{"schema":1,"host":"192.0.2.20","controlPort":17444,"backendPort":17445,"serverCertificate":"-----BEGIN CERTIFICATE-----\n服务器公开证书\n-----END CERTIFICATE-----\n"}
```

两端口必须不同。配置文件不含私钥或共享token；服务器入口已有Linux私有目录版 `linkgate-server request/init/serve`，见 [服务器指南](../../control-plane/serverhost/README.md)；远程管理CLI、轮换和正式安装包仍待交付。stock frps不支持此模式。存在但损坏的服务器配置会停止启动，不回退预览；修改需先停止。

6. 在界面启动网关。SDK 绑定不会代替用户启动。允许通知可保留方便的停止入口；停止网关会关闭活动业务流。
7. 消费应用通过 SDK 提交 imports 声明。到 Agent 的应用页核对真实包、签名/安装主体与声明后批准，再到访问页批准该应用的精确设备/远端应用/服务范围。
8. 在 Windows 发布端另行批准同一 Android 设备和应用的服务 grant。只在 Android 批准不能绕过发布端授权。
9. 使用 [薄流 SDK](../client-streams/README.md) 打开连接。撤销声明、访问权限、设备或停止网关都会终止对应流。

所有者界面有原操作恢复/放弃入口。结果不确定时先恢复原操作，不生成新操作号重做授权；只有确认原操作不存在才能放弃。程序不会自动信任新根或重新生成已绑定但遗失的设备密钥。

## 验证边界

[原生测试](../../tests/android-native/README.md) 已在 API36 x86_64 模拟器执行 Keystore、跨 UID/不同签名 Binder、一次性描述符、64MiB 校验、半关闭及活动撤销；不等于 ARM64 真机或跨 NAT。旧 LinkGate 0.2.3、ReDesk、手机画面和 ECS 均保持原状。架构见 [ADR0014](../../docs/adr/0014-android-native-authority.md)。

## 新认证运行时的开发验证

源码桥接组件已通过模拟器Keystore、真实frpc、Unix进程身份、内层数据mTLS/服务器租约和64MiB验证，见 [ADR0017](../../docs/adr/0017-android-private-runtime.md)。

构建工具为JDK17、Gradle9.3.1、AGP9.1.1、SDK平台37.0，保持targetSdk36/minSdk26；认证FRP模式使用标准ALPN，要求API29。先从仓库根目录运行：

```powershell
./gateway/backend-frp/build-android.ps1 -Go C:/tools/go/bin/go.exe -Source ./upstream/frp-linkgate -NdkDirectory C:/Android/sdk/ndk/29.0.13113456
# 将返回的runtimeDirectory传入Gradle；未选择产物时不会凭空启用该能力。
gradle -p gateway/android -Pandroid.useAndroidX=true -PlinkgateAgentRuntimeDir=C:/path/to/generated/runtime :agent:assembleDebug
```

隔离原生复现使用 `tests/android-native/verify.ps1` 的 `-AgentRuntimeDirectory` 与 `-AuthenticatedBackendDirectory`，仅操作其自有模拟器。现已新增独立UID/签名消费者通过公开SDK的中继验收，包括64MiB、双并发和两级撤权，见 [ADR0018](../../docs/adr/0018-android-sdk-server.md)。API36模拟器仍不能当作移动网络验收。

同一完整授权scope现可复用私有runtime，最多16个自有runtime；流仍独立授权和取消，已使用session空闲最多60秒回收。见 [ADR0019](../../docs/adr/0019-scoped-runtime-pool.md)。公开实验连接现已组合自动选路，见下节；这不等于全网络P2P或长稳验收。

公开服务器配置可包含 `stunEndpoints` 数组（最多4个不同的数字IPv4:port）；现有文档缺省不启用发现，不自动使用公共备用。所有者确认界面会显示目标。新运行时与Windows实际XTCP证据见 [ADR0020](../../docs/adr/0020-explicit-stun.md)；Android公开实验连接已有LAN/XTCP候选与显式中继；填写STUN不是直连成功的证明。

## 公开连接生命周期（实验）

`connection.api` 仅在hello中声明实验能力，调用应用须显式同意。实际打开需要所有者启动、服务器配置、经批准的imports、本地精确访问grant及发布端/服务器grant。未配置服务器时不把预览直连冒充此能力。

[公开SDK流程](../client-streams/README.md) 使用持久操作号恢复结果，分离连接状态、一次性附接和数据流。`selectedPath` 是认证传输选中路径，`businessPath` 在实际业务字节后才确定。停止、撤权、重启或默认网络变化会结束旧流，不重新执行应用业务。故障尾部标不完整。

Agent权限数据库现为schema2，自动原子迁移schema1；服务器公开配置格式仍为schema1，二者不可混用。模拟器验收包括三种路径各64 MiB及网络代次注入，见 [证据](../../docs/verification-v0.3-public-connection.json)。ARM64与物理网络验收仍开放。

## 0.3.0-preview.3 界面

应用名称和通知统一为LinkGate。概览显示真实网关状态和启停按钮；设备管理已配对电脑；应用分别管理声明与访问请求；设置提供设备身份、服务器、帮助与操作恢复。没有把网关启动或STUN成功显示成业务P2P成功。VersionCode 4，同包名、同开发签名覆盖安装保留Keystore与配对。
