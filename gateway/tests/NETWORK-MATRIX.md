# LinkGate 0.2 网络验收

每组必须用 0.2 Windows 发布端和 Android 访客端。保留原私有配对身份和服务，不改 ReDesk。手机画面由用户操作。

**0.2.3 四组新版验收已于 2026-10-29 完成。** 文件、WebSocket、两端字节及 ECS 流量核对均通过，三轮业务 100% 直连，移动数据/Clash 关闭为 90.06%，详见 [新版最终结果](NETWORK-RESULTS-v0.2.3.md)。下表保留 0.2.1 全部中继的历史结果，不与新版混用；完整历史记录见 [旧版结果](NETWORK-RESULTS-2026-10-28.md)。

| 场景 | 手机网络 | 手机 Clash | 电脑 Clash/TUN | 当前验收状态 |
|---|---|---|---|---|
| wifi-clash-off | 与电脑同 Wi-Fi | 关闭 | Meta TUN 开启 | 文件/WS/两端字节/ECS 核对通过；全部中继 |
| wifi-clash-on | 与电脑同 Wi-Fi | 开启；测试后确认 LinkGate 仍在 VPN UID 范围 | 同上 | 补测文件/WS/两端字节/ECS 核对通过，全部中继 |
| mobile-clash-off | 关闭 Wi-Fi，移动数据 | 关闭，用户确认（补测时选错 ON 标签，保留原报告） | 同上 | 补测文件/WS/两端字节/ECS 核对通过，全部中继 |
| mobile-clash-on | 关闭 Wi-Fi，移动数据 | 开启；测试后确认 LinkGate 仍在 VPN UID 范围 | 同上 | 文件/WS/两端字节/ECS 核对通过，全部中继 |

若要分别定位电脑 TUN 的影响，对四组再按电脑 TUN 关闭/开启重跑，不能把不同电脑状态混为一组。预设排除 LinkGate 是已验证可行的配置；若测试不排除的效果，应另列对照，保留原排除列表，不由程序修改 Clash。

1. Windows 启动只读演示服务 `LinkGate.exe --demo --port 18480`，并用 `--run publisher --profile 私有路径` 发布。CLI 每秒输出带 UTC 时间的脱敏诊断 JSON；重定向到受限本地目录，不能将配对文件加入报告。
2. ECS 先采集无业务 10 秒基线，再在对应测试窗口执行 `python3 ecs_traffic_window.py --seconds 90 --interface eth0`。需已有管理员执行通道及 tcpdump。脚本检查既有专用服务，不安装、不修改服务/防火墙、不保存原始包；只报告 TCP17400 的 IP/TCP 有效载荷字节、解析遗漏及内核丢包。
3. 用户手动切网络/Clash，停止旧网关再启动，等待注册。Android 点“运行 60 秒文件和 WebSocket 测试”，选实际场景。测试第一项服务，必须是本项目演示服务，访客端口通常 18481。
4. 文件反复读取 1 MiB 确定性正文并逐字节验证；同一 WebSocket 持续发送/核对 1024 B 二进制回显。结束点“复制最近测试报告”，或用只读 `adb logcat -d -s LinkGateTest:I` 取报告，不自动操作手机 UI。
5. 报告包含时间、文件已核验字节、完整文件数、WebSocket 有效载荷/帧数、枚举错误、Android UID 流量差和诊断前后快照。报告不含地址或凭据。Android UID 包含本地应用回环和原生运行时流量，不能直接当公网线路字节。
6. 比较窗口中手机 `directTx/Rx`、`relayTx/Rx` 的增量与 Windows 对应反向增量。连接结束后等约 2 秒取最终快照；中断或 `complete=false` 不作精确相等断言。HTTP/WS 头计入转发字节，测试正文只计 payload，后者应略小。
7. 直连时 ECS 应主要保留协调/打洞控制流；中继时 ECS 入、出均随业务流量增长。用 ECS 该端口 IP/TCP 字节核对，不能拿整台服务器网卡总流量归因于 LinkGate。TLS、ACK、重传和其他同端口连接会增加计数；测试期间不要并行运行其他 LinkGate 业务。抓包有丢包/解析遗漏时报告不完整。
8. 每组记录 NAT 打洞事实、实际路径/混合路径、业务字节、首次及后续连接建立时间、吞吐、回退原因、完整性和 ECS 增量。**中继且传输正常是有效测试结果，不等于直连验收成功。**

已有 0.1 的 HTTP/WS 冒烟只证明业务连通，不足以填入本矩阵。尚未执行的场景保持“待实测”。

## 2026-10-28 首次手机测试及 0.2.1 修正

第一组 0.2.0 手机报告（电脑 TUN 开启、手机同路由器 Wi-Fi/Clash 关闭）完成 20 MiB 确定性文件验证，实际全部中继，直连字节为零。WebSocket 在 72 次回显后报错，发布端发送比手机接收多 1,028 B，该次不能判为全项通过。

按手机测试时间对应的 UTC 秒桶核对 ECS：入站 21,786,172 B、出站 24,802,641 B，包含网络及控制开销，抓包零内核丢包/零解析遗漏。电脑和手机的墙钟并非严格一致，电脑差值使用前后稳定空闲快照，不能直接从忙碌边界采样做差。脱敏原始结果与计数见 [首次结果](network-results/2026-10-28-wifi-clash-off-initial.json)。

本机复现演示服务在第 15 秒发送正常的零长度 Pong；旧测试客户端只接受单个完整二进制帧，把心跳误当错误。0.2.1 支持 Ping/Pong、分片重组、正常关闭，发送测试帧改为一次批量写入，并严格校验握手 Accept。依据 [RFC6455 第5节](https://www.rfc-editor.org/rfc/rfc6455#section-5)。新的协议回归覆盖控制帧插入分片、短读、掩码、大小边界；同一 Java 客户端对 Windows 演示服务连续 62 秒、997 次回显和 5 个控制帧处理通过（含正常关闭）。这是本机工具验证，四组手机测试仍需使用 testVersion=2 重新验收。

0.2.1 第一组重测：20 MiB 文件、379 次 WebSocket 回显、4 个控制帧处理和正常关闭均通过；手机中继发送 392,626 B、接收 21,363,955 B，与电脑反向计数完全一致。直连字节仍为零，最近建立 1,533 ms，原因 `direct_tunnel_unavailable`。ECS 完整覆盖该组，按测试 UTC 秒桶核对，零内核丢包及解析遗漏。连通性通过，直连尚未通过。[重测报告](network-results/2026-10-28-wifi-clash-off-retest.json)。

第二组先出现一次零业务字节的失败，随后用户重跑成功：20 MiB 文件、378 次 WebSocket 回显及正常关闭，手机中继发送 391,594 B、接收 21,362,927 B，与电脑反向计数完全一致，最近建立 1,516 ms，仍全部中继。测试后的只读系统检查确认当前 VPN 属于 Clash，且 UID 范围包含 LinkGate；这表示未按应用绕过 VPN，不代表规则一定选了远端代理。[成功传输报告](network-results/2026-10-28-wifi-clash-on-initial.json) 和 [零字节失败记录](network-results/2026-10-28-wifi-clash-on-startup-failure.json) 均保留。

第二组成功传输超过原 ECS 采集窗口结束时间，报告标记 `ecsStatus=partial-window`、`ecsWindowCoversWholeTest=false`；不把前段流量作为全程结果。已安排独立窗口补测，后续每组开始前新建完整窗口。

第二组独立窗口补测：20 MiB 文件、367 次 WebSocket 回显、4 个控制帧及正常关闭通过；手机中继发送 380,242 B、接收 21,351,619 B，与电脑反向计数完全一致。最近建立 1,522 ms，原因 `direct_tunnel_unavailable`，直连字节为零。最终 ECS 报告完整覆盖本次补测，零内核丢包/解析遗漏；不需要再重跑第二组。[补测报告](network-results/2026-10-28-wifi-clash-on-retest.json)。

第三组首次：20 MiB 文件、291 次 WebSocket 回显及正常关闭通过；手机中继发送 301,810 B、接收 21,273,491 B，与电脑反向计数一致，最近建立 1,545 ms，仍无直连字节。手机测试开始于旧 600 秒采集窗口结束之后，ECS 报告不能用于该组业务归因，保留 `partial-window` 而不补造数字。[首次报告](network-results/2026-10-28-mobile-clash-off-initial.json)。

第三组补测：19 MiB 文件、329 次 WebSocket 回显、4 个控制帧及正常关闭通过；手机中继发送 340,959 B、接收 20,263,845 B，与电脑反向计数完全一致，最近建立 1,547 ms，无直连字节。用户明确确认 Clash 实际关闭，只是选择了 `mobile-clash-on` 标签；保留原始 `phone.scenario`，另记 `actualScenario=mobile-clash-off`，不重复归入第四组。7200 秒窗口在测试完成后按标记正常提前结束，ECS 完整覆盖，零内核丢包/解析遗漏。[补测报告](network-results/2026-10-28-mobile-clash-off-retest.json)。

第四组：20 MiB 文件、300 次 WebSocket 回显、4 个控制帧及正常关闭通过；手机中继发送 311,098 B、接收 21,282,743 B，与电脑反向计数一致。最近建立 1,556 ms，无直连字节。长窗口按完成标记正常结束，ECS 完整覆盖，零内核丢包/解析遗漏。[第四组报告](network-results/2026-10-28-mobile-clash-on.json)。

后续采集可用 `--stop-file /var/lib/linkgate/diagnostics-v02/专用目录/stop`：操作端在收到测试完成后创建自己的空标记文件，采集程序在最多约一秒后正常回收自有 tcpdump。时限上限改为 7200 秒，给手动操作留出时间；不注册后台服务。使用 `--report-file` 将完整统计写入同一专用私有目录，stdout 只输出摘要，避免长窗口的云命令输出截断。`ecs_traffic_read.py` 校验目录/文件所有者、类型、权限和大小，仅读取指定测试 UTC 秒桶。程序不按名称杀进程、不修改防火墙、不停止 frps。保存的内容只有聚合计数，不含原始包或 IP。
