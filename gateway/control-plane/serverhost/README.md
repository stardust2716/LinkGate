# 新认证服务器的持久化入口

`linkgate-server` 将新控制服务和经过逐设备授权的 FRP 后端组合为一个 Linux 进程。它已有本机 Linux 实测，不使用旧共享 token，也不使用测试 fixture 启动。当前是开发入口：远程管理 CLI、自动证书轮换、部署迁移和完整发布验收仍在开发，不能当作1.0安装包。

## 构建

先按 [固定后端构建说明](../../backend-frp/README.md) 生成新的派生源码目录；共享上游保持只读。在派生目录中使用 Go1.27执行：

```sh
go build -trimpath -buildvcs=false -tags noweb -o linkgate-server ./cmd/linkgate-server
```

Windows 开发机可使用 [本地 Linux 原生验证](../../tests/server-host/README.md)，输出 Linux amd64 二进制及哈希。它不会安装服务或连接 ECS。

## 身份与目录

使用独立的 Linux 服务账户；身份、配置和两个状态目录需由该账户拥有，目录权限0700、文件0600。祖先目录不能包含符号链接或非 sticky 的共享可写目录。正式状态放在 Linux 本地文件系统；Windows 挂载目录的权限不能代替这些检查。

以下是路径示例，命令须以该服务账户运行。先准备这些空目录：

```text
/etc/linkgate                    配置及公开的所有者设备证书
/var/lib/linkgate                authority.db，普通权限备份边界
/var/lib/linkgate/identity       服务器私钥、签发后的设备证书和公开根
/var/lib/linkgate-witness        witness.db，独立保护，排除在普通权限备份外
```

在 Linux 主机本地产生服务器私钥及公开CSR：

```sh
/opt/linkgate/linkgate-server request /var/lib/linkgate/identity
```

输出只有 `created:true`；`device-key.pem` 留在该主机，`device-request.pem` 为可交给所有者的公开CSR。重复命令拒绝覆盖原身份。中断留下的部分状态要求显式核查，不自动删除或轮换。

所有者可使用现有 [Windows owner工具](../../windows/LinkGate.Owner.Console/Program.cs) 的 `owner-create`、`device-request`、`issue` 和 `device-install`。若已有所有者根，继续使用该根；新建所有者时先创建并签发所有者自己的管理设备身份，再签发服务器CSR。服务器与管理设备的证书必须来自同一已确认根。

只把以下公开材料交给服务器，并保存为0600：

- `identity/device-cert.pem`：由所有者签发的服务器设备证书，匹配 Linux 本地私钥。
- `identity/owner-root.pem`：已确认的所有者公开根。
- `/etc/linkgate/owner-device-cert.pem`：初始管理员的设备证书，是叶证书，不是CA根证书。

所有者根私钥和管理设备私钥均留在所有者端。服务器入口不生成它们，也不需要复制它们。

## 明确初始化与启动

配置文件 `/etc/linkgate/server.json` 示例：

```json
{
  "schema": 1,
  "identityDirectory": "/var/lib/linkgate/identity",
  "authorityDirectory": "/var/lib/linkgate",
  "witnessDirectory": "/var/lib/linkgate-witness",
  "controlAddress": "0.0.0.0:27111",
  "backendAddress": "0.0.0.0:27112"
}
```

只接受数字 IPv4 和两个不同非零端口。配置为0600，父目录为0700。控制端口提供mTLS HTTP `/control/v1`；后端端口只接受带 `linkgate-frp/1` ALPN及应用scope的新设备mTLS协议。没有仪表盘、公开TCP代理、旧token入口或隐式STUN监听。

首次显式初始化：

```sh
/opt/linkgate/linkgate-server init /etc/linkgate/server.json /etc/linkgate/owner-device-cert.pem
```

成功输出 `initialized:true`。两份数据库任何一份已存在都会拒绝重新初始化。部分初始化失败不自动清理；先保留原始状态并核查，不以重新init作为常规修复方式。

普通启动：

```sh
/opt/linkgate/linkgate-server serve /etc/linkgate/server.json
```

成功监听后输出 `ready:true, authenticated:true`。普通启动要求已有身份、权限库及见证；缺失不会生成替代文件。SIGINT/SIGTERM停止监听、认证通道并释放数据库锁。每秒检查持有文件/目录身份与私有权限，变更或失效会停止进程。日志只有固定聚合结果/错误，不输出原始上游候选、请求或凭据。

当前初始化只赋予指定管理设备owner角色，没有自动授予发布/访问角色、服务或grant。已有真实mTLS管理协议包含 `owner.device.set`、`owner.service.set`、`owner.grant.set` 和 `owner.operation.status`；友好的远程管理命令尚待接入。旧Windows本地grant工具不等于这些服务器审批操作。

## 运维边界

把普通权限库和独立见证一起恢复到旧副本，无法靠这套软件见证保证检测，因此见证必须有独立保护/恢复流程。删除见证不会触发自动修复。数据库中的旧批准结果不会再次执行；调用方必须保存原operationId并查询未知结果。

本进程不提供STUN；客户端的两个明确STUN目标应单独配置和验证。端口放行、systemd、服务器上传、旧服务迁移和回滚需通过后续部署工具执行。本轮没有改动云端或安装版，完整使用和发布入口以 [当前进度](../../docs/PROGRESS.md) 为准。
