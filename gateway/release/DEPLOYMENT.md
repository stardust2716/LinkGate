# 已部署试用版与首个应用接入

当前：Windows 0.3.0-preview.2，Android 0.3.0-preview.3。旧LinkGate已按新授权退役，以下旧版保留描述属于最初部署阶段。


2026-10-30 部署版本：**0.3.0-preview.2**。部署前核对已有资源，新增独立试用服务，保留旧 LinkGate 0.2.3、RustDesk 与 STUN。该版本包含实际 ECS 带宽环境暴露的心跳、FIN 确认等待及示例控制会话修复；其余成熟版功能仍在 [后续 spec](../docs/mature-release.sdd)。

## 当前部署结构

| 部分 | 位置与运行方式 |
| --- | --- |
| ECS | `linkgate-trial.service`，已启用开机启动；专用非登录用户 `linkgate-trial` |
| 服务端程序 | `/opt/linkgate-trial/0.3.0-preview.1/linkgate-server`；服务器二进制未因客户端修复而改变，部署记录在 `/opt/linkgate-trial/deployment.json` |
| ECS 配置/身份 | `/etc/linkgate-trial/server.json`、`/var/lib/linkgate-trial/identity` |
| ECS 权限库/见证 | `/var/lib/linkgate-trial/authority`、`/var/lib/linkgate-trial-witness`；不要单独回滚权限库来处理故障 |
| 云端端口 | 27111/TCP 认证控制、27112/TCP 认证 FRP；没有公开业务代理端口 |
| Windows 程序 | `%LOCALAPPDATA%\Programs\LinkGateTrial`，已用安装器更新 |
| Windows 配置 | `%LOCALAPPDATA%\LinkGateTrial`，身份与程序分开保留 |
| Windows 运行 | 当前用户隐藏后台进程，由本机部署辅助程序持有子进程；不是系统服务，也未配置开机任务 |
| Android | `com.nebula.linkgate.agent`，versionCode 4；覆盖安装保留 Keystore 身份，用户已手动导入并启动 |

真实地址、设备ID、公钥确认值、运行记录和操作 UUID 保存在本机私有部署目录，不随源码提交。程序专用 Windows 防火墙规则允许 Agent 的局域网 TCP 27443 和匹配 FRP 程序的 P2P UDP。云端只新增本服务的端口规则，既有规则保留。

STUN 已明确配置两个实际探测通过的端点：复用既有自建服务，另使用本项目已采用的公共服务；具体数字地址在私有 server.json。没有修改旧 STUN 或启用隐式备用。成功探测不是打洞成功证明。

## 日常操作

Windows 开始菜单 → LinkGate Trial → **启动已配置的网关**／**停止已配置的网关**。本次部署的入口位于 `%LOCALAPPDATA%\LinkGateTrial\Start-Configured.ps1` 和 `Stop-Configured.ps1`；停止通过持有的进程 stdin 执行正常 stop，不按名称杀进程。电脑重启或退出登录后，需再次启动；Android 使用应用或通知的启动/停止入口。

ECS 管理使用 `systemctl status linkgate-trial.service`、`systemctl stop/start linkgate-trial.service`。不要重跑 init、覆盖身份或删除 witness 修复连接问题。停止新服务不会停止旧 `linkgate-frps.service` 或 RustDesk。

## 接入第一个应用

1. 选择具体的 Windows 本地服务与客户端项目，确定其本地端口、稳定 applicationId、serviceId 和 importAlias；不要先开放任意本机端口。
2. Windows 使用 [薄 .NET SDK](../windows/LinkGate.Client/README.md)，Android 使用 [薄流 SDK](../android/client-streams/README.md)。已配置的 Windows Agent 为发布端，手机为访问端。提供给普通业务的材料只包含公开设备引用及 SDK 信任信息，不包含 owner/admin 私钥、数据库或 FRP 配置。
3. 业务注册声明后由所有者核对并批准；服务器分别批准设备、服务及精确访问 scope。手机批准本机访问请求，Windows 批准对应手机设备/应用/服务的发布 grant。设备互信本身不授予业务访问权。
4. 先验证同 Wi-Fi，再验证移动数据与 Clash。诊断应同时记录路径、业务字节、耗时及结束状态；不能把 STUN 成功或进程存在当成业务直连。
5. 长业务流独立于短时控制管道。闲置管道过期后，应用显式重新连接可信 Agent 并 hello，再查询原 connectionId/operationId；不要自动重放 open 或业务请求。

Windows 可用 `trust-certificate` 仅信任 Android 访问端证书，无需填写手机并不存在的发布端口；本次已经完成此项互信准备。具体业务应用尚未接入，没有预先授予未知应用权限。

## 已验证与未验证

[部署证据](../docs/verification-v0.3-deployment.json) 记录真实安装、ECS mTLS 管理、两个 STUN 探测、Windows 本机直连及真实 ECS 强制中继各 2 MiB。修正版中继持续约 4.1 秒，摘要、FIN、精确字节与 complete 均正确。测试服务权限已撤销，正式应用状态库保持干净。相关 42 项安全流回归通过，包括撤权关闭；未重复大套、四网络或长稳测试。

这次业务传输的两端应用进程均在 Windows 上，实际通过 ECS 中继；Android 仅安装、配对和前台启动确认。尚未验证首个业务应用或手机跨 NAT 直连。保留失败记录：原始心跳过短、最终 FIN 确认排队，以及示例复用闲置管道导致的错误。未将这些失败隐藏为部署成功。

## 回滚边界

有问题时先停止本次新网关和 `linkgate-trial.service`，保留全部身份、权限库和见证，再检查原操作回执。远控服务继续运行，旧LinkGate仅保留私有恢复备份。移除防火墙规则仅针对 `LinkGateTrial-LAN-Peer`、`LinkGateTrial-P2P-UDP` 及部署回执记录的新云端规则；不要清空防火墙或回滚共享配置。用户目录内保存了防火墙改动前记录。
## 界面更新与旧版退役

手机名称统一为LinkGate，界面采用概览、设备、应用、设置。设备证书、服务器和恢复操作收进设置，审核仍保留完整真实调用方信息与精确范围确认。

旧安卓包com.nebula.linkgate已卸载；旧linkgate-frps.service、程序、专用用户与17400端口规则已移除，云端root私有目录保留恢复包。Windows工作区12个旧便携/发行目录已移入回收站；当前Windows安装器和服务器包保存在dist/LinkGate-v0.3.0-preview.3。私有凭据与新版身份/授权库未删除。RustDesk、ReDesk、共享STUN及新版linkgate-trial保持运行。
