# LinkGate v0.1.0 云端

本目录提供独立 FRP **0.71.1 / Linux amd64** 协调与 STCP 回退服务。默认只读预检；脚本不修改 ReDesk、RustDesk、SSH、Nginx、现有云入口或任何防火墙规则。

## 数据路径和适用范围

- XTCP 成功后，两个客户端通过 QUIC 隧道直接传输 TCP 服务数据；FRP 控制连接继续通过 ECS。
- 打洞失败可由访客的 `fallbackTo` 转至 STCP，业务此时经过 frps，仍消耗 ECS 带宽。后台打洞成功只改变后续新连接，不保证旧 TCP / WebSocket 连接无缝迁移。
- 首版仅支持**单一所有者的受信任设备**。FRP 共享注册 token 不是多租户授权系统；设备持有 token 后有注册代理的能力。每项服务另有 secretKey，但首版不提供独立设备 token、撤销服务、自动证书续期或集中授权平台。
- 控制与中继链路强制 TLS，并由客户端私有 CA 和 `gateway.internal` 名称验证服务器。不要宣称这一层等同于已经审计的应用端到端协议。正式业务继续使用原应用的 HTTPS／WSS、身份校验及写操作回执；通用网关不替代业务鉴权。只读演示可使用 HTTP。
- `proxyBindAddr` 固定为 `127.0.0.1`，不开公网业务端口、Dashboard、HTTP／HTTPS vhost 或 SSH 网关。服务清单由两端配置管理，不接受来自客户端的任意本机目标地址。

## 端口与 STUN

FRP **0.71.1 不提供 `natHoleUDPBindPort` 配置**，也不会在 UDP 17400 上提供 STUN。XTCP 通过已有 TCP 控制连接交换候选，客户端的 `natHoleStunServer` 负责映射发现。

本服务仅需允许 **TCP 17400**。客户端仍需允许到已选 STUN 的 UDP 及对等端 UDP。当前已有部署可复用已验证的 ReDesk STUN UDP 21116：配置合同的 `server.stunPort=21116`，生成 `natHoleStunServer="服务器主机:21116"`。这只是复用独立服务，不是 frps 自带能力。新服务器建议另行部署并验证 STUN；省略 `stunPort` 时客户端默认 3478 不代表该服务器已经监听 3478。0.2.3 客户端在首选端点不可达或观测不足时会尝试公共备用，须取得两个真实的不同目标观测，详见 [多端点发现](../frp-diagnostics/STUN-DISCOVERY.md)；服务器无需为此新增端口。

如服务器使用 firewalld，先核对实际活动 zone，再精确增加本服务的一条规则：

```bash
sudo firewall-cmd --get-active-zones
# 将实际 zone 替换为已核对值；不要批量 reload 或改变默认 zone。
sudo firewall-cmd --zone=ACTUAL_ZONE --add-port=17400/tcp
sudo firewall-cmd --permanent --zone=ACTUAL_ZONE --add-port=17400/tcp
```

阿里云安全组也只增加 TCP 17400。STUN 按其已有归属与实际端口管理，不因部署 LinkGate 而重新开放旧服务端口。

## 准备包与私有配置

公开包应含本目录脚本及一个 `package/`：

```text
package/
  frps                 # 锁定官方 Linux amd64 0.71.1 可执行文件
  SHA256SUMS.txt        # 唯一一行：<frps的小写SHA-256>  frps
```

必须先与可信下载锁核对 `frps` 的摘要，再在本机生成上述清单；不能用来自同一未知下载包的清单独立建立信任。安装器检查摘要、ELF、root 归属，安装后只以低权限 `linkgate` 用户执行版本与配置验证。

另建权限为 0700 的私有配置目录，内容不上传仓库或 Release：

```text
private-config/
  frps.toml            # 严格按 frps.example.toml，独立随机 32 字节 token
  server.crt           # 私有 CA 签发，SAN 包含 gateway.internal
  server.key           # 对应私钥
```

文件归 root、无 group/world 写权限，不允许其他用户读取。`server.crt` 与 `server.key` 通过 frps 的配置验证核对；CA 公钥证书通过可信配对渠道交付客户端。客户端只获得 CA 公钥，不能包含服务器或 CA 私钥。证书到期时间需另行管理，首版不自动续期。

## 安装与验收

要求 Linux x86_64、systemd、Python 3.6+，由 root 执行：

```bash
sudo bash ./install.sh --package /ABS/PATH/package --config /ABS/PATH/private-config
sudo bash ./install.sh --package /ABS/PATH/package --config /ABS/PATH/private-config --apply
sudo bash ./health.sh
```

第一条不创建文件、不注册任务、不变更服务。遇到同名用户／组、目录、服务或占用端口时拒绝安装，先检查实际归属，不能删除目录或记录绕过。第二条创建专用非登录用户、受限配置、安装记录及 `linkgate-frps.service`，然后验证实际进程路径、TCP 就绪和文件摘要。失败保留记录并尝试停用本次精确匹配的 unit，不自动重跑。

安装路径：`/opt/linkgate/v0.1.0/frps`、`/etc/linkgate`、`/var/lib/linkgate`。归属记录为 `/opt/linkgate/install-state.json`。健康脚本只返回状态与版本，不打印 token、私钥、真实对等端或完整日志。`ready=true` 仅证明本服务就绪，不能代替公网鉴权、STCP 业务、XTCP 直连或手机测试。

## 完整安装后的失败恢复

如果安装已写入全部文件和 unit，但运行／验收阶段失败，不能重跑 `install.sh`、删除 journal 或用空白配置覆盖。先保留诊断记录，核对失败原因；处理原因后可执行：

```bash
sudo bash ./recover.sh
sudo bash ./recover.sh --apply
sudo bash ./health.sh
```

第一条只读验证原 journal 为 `prepared`／`failed`、全部原文件摘要、受限归属、精确 unit 和已加载的 systemd 设置。缺失或被修改的文件会拒绝恢复，不从包中重建。第二条只启用并启动本次原有 unit；若它已在运行则先验证且不重启。验证真实进程与 IPv4／双栈 IPv6 通配 TCP 监听 socket 归属通过后，才将同一 journal 标为 active，保留原密钥、数据与摘要。仍失败时保留未完成状态；应继续调查或精确停用，不循环重试。

## 停用／回滚

首版回滚是精确停用新服务，既有应用不改动：

```bash
sudo bash ./rollback.sh
sudo bash ./rollback.sh --apply
```

先只读核验 unit、安装目录和文件摘要，第二条才停止并取消自启，将精确归属的 unit 保存为 `/opt/linkgate/linkgate-frps.service.stopped`，保留二进制、用户、配置、密钥与记录。发现外部改动则拒绝停用。脚本不删除防火墙或安全组规则；若要回收，只移除本次明确创建的 TCP 17400 规则，保留其他链路。不要用原 RustDesk 卸载／回滚脚本处理 LinkGate。

## 官方实现依据

- [0.71.1 ServerConfig](https://github.com/fatedier/frp/blob/v0.71.1/pkg/config/v1/server.go)：实际监听与 TLS 字段，没有 `natHoleUDPBindPort`。
- [XTCP visitor](https://github.com/fatedier/frp/blob/v0.71.1/client/visitor/xtcp.go)：STUN 准备、控制连接候选交换和连接级 STCP 回退。
- [XTCP 文档](https://gofrp.org/en/docs/features/xtcp/)：NAT 限制及后续新连接行为。
