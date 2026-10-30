# LinkGate 快速开始

这是首个可安装试用版，提供Windows x64发布/访问端、Android访问APK和Linux amd64认证服务器。Windows包自带.NET运行时和匹配的新认证FRP；无需另装.NET。不是生产1.0，自动更新、证书轮换、更多适配器与长稳验收留待后续。现有0.2.3/ReDesk不自动迁移。

## 1. 安装与文件

运行Windows Setup，默认按当前用户安装到 `%LOCALAPPDATA%\Programs\LinkGateTrial`，不要求管理员、不改防火墙/默认路由、不创建开机任务。开始菜单LinkGate打开启动入口；所有者命令在owner目录、声明审核在review目录、SDK示例在sample目录。

Android安装随包APK（包名com.nebula.linkgate.agent，Android10及以上用于认证服务器模式）；它使用开发签名，后续升级需保持签名。不要为升级随意卸载已有Agent，否则设备身份会丢失。本轮不操作手机画面。

身份/配置默认放 `%LOCALAPPDATA%\LinkGateTrial`，与程序目录分开。卸载保留这些文件。试用安装器和应用尚未使用生产代码签名。

以下PowerShell命令在安装目录运行：

```powershell
$owner = "$PWD\owner\LinkGate.Owner.Console.exe"
$base = "$env:LOCALAPPDATA\LinkGateTrial"
New-Item -ItemType Directory -Force $base | Out-Null
```

## 2. 创建所有者和设备身份

已有同一所有者根就继续使用，不要另建根替换旧身份。新环境先创建所有者根、管理员设备及电脑设备：

```powershell
& $owner owner-create "$base\owner-root"
& $owner device-request "$base\admin"
& $owner issue "$base\owner-root" "$base\admin\device-request.pem" "$base\admin-cert.pem"
& $owner device-install "$base\admin" "$base\admin-cert.pem" "$base\owner-root\owner-root.pem"
& $owner device-request "$base\identity"
& $owner issue "$base\owner-root" "$base\identity\device-request.pem" "$base\pc-cert.pem"
& $owner device-install "$base\identity" "$base\pc-cert.pem" "$base\owner-root\owner-root.pem"
```

记下device-install打印的deviceId。只传CSR、证书和公开根；私钥留在产生它的设备。管理员设备证书用于服务器首次init。

## 3. Linux服务器

使用随包server目录中的linkgate-server及配置模板，在Linux创建专用linkgate账户。推荐路径：`/opt/linkgate/linkgate-server`、`/etc/linkgate/server.json`、`/var/lib/linkgate/identity`、`/var/lib/linkgate/authority`、`/var/lib/linkgate-witness`。身份/配置/状态目录归该账户，0700；文件0600；二进制可执行。witness独立于普通权限库备份。

```sh
# 先创建专用账户，并安装二进制、公开配置及专用目录
sudo useradd --system --home /var/lib/linkgate --shell /usr/sbin/nologin linkgate
sudo install -d -m 0755 /opt/linkgate
sudo install -m 0755 linkgate-server /opt/linkgate/linkgate-server
sudo install -d -o linkgate -g linkgate -m 0700 /etc/linkgate /var/lib/linkgate /var/lib/linkgate/identity /var/lib/linkgate/authority /var/lib/linkgate-witness
sudo -u linkgate /opt/linkgate/linkgate-server request /var/lib/linkgate/identity
```

把公开device-request.pem交给所有者，在Windows用issue签发。只把签发的device-cert.pem和owner-root.pem放回Linux identity目录，另把管理员设备证书存为/etc/linkgate/owner-device-cert.pem。均归linkgate、0600。

配置示例：

```json
{"schema":1,"identityDirectory":"/var/lib/linkgate/identity","authorityDirectory":"/var/lib/linkgate/authority","witnessDirectory":"/var/lib/linkgate-witness","controlAddress":"0.0.0.0:27111","backendAddress":"0.0.0.0:27112"}
```

```sh
sudo -u linkgate /opt/linkgate/linkgate-server init /etc/linkgate/server.json /etc/linkgate/owner-device-cert.pem
sudo -u linkgate /opt/linkgate/linkgate-server serve /etc/linkgate/server.json
```

init只做一次。启动成功输出ready/authenticated。提供的linkgate.service可在核对路径后用于systemd。阿里云安全组/主机防火墙需允许27111、27112/TCP。不要直接替换旧frps；新协议与旧共享token客户端不同。

### Android 配对与应用接入

安装新Agent后，在“LinkGate”点击“生成 / 导出设备 CSR”。将导出的公开CSR交给所有者，用同一个owner-root签发，再在手机点“导入设备证书与所有者根”，依次选择公开设备证书和公开根，核对界面根证书指纹。设备私钥始终留在 Android Keystore，不复制电脑私钥到手机。

手机导入与Windows同格式的公开server.json，核对界面显示的服务器证书指纹，再由管理员给手机deviceId发放access角色及精确服务grant。注意Android界面显示的是证书SHA-256，Windows命令cert-key打印的是公钥SPKI SHA-256，两者不能混用。

“导入对端公开配置”接受 `{"certificate":"电脑叶证书PEM","host":"电脑可达数字IPv4","port":电脑peerPort,"expectedRevision":0}`；核对电脑设备公钥并确认。启动网关后，业务应用通过Android SDK提交声明和访问请求，由用户在“应用声明”和“访问请求”页分别批准；电脑发布端还须批准该手机deviceId与实际业务applicationId的grant。SDK使用见仓库android/client-streams/README.md。本APK是应用网关，不提供旧0.2.3任意固定端口配对入口；未接入SDK的应用不能仅安装APK就自动获得P2P。

## 4. 服务器公开配置与授权

保存 `$base\server.json`：host填ECS数字IPv4，证书填服务器公开叶证书PEM。独立核对服务器公钥指纹，可用`cert-key <server-cert.pem>`计算；将确认后的64位小写指纹保存到server-key.txt。

```json
{"schema":1,"host":"192.0.2.20","controlPort":27111,"backendPort":27112,"serverCertificate":"-----BEGIN CERTIFICATE-----\n...\n-----END CERTIFICATE-----\n","stunEndpoints":[]}
```

跨NAT的XTCP需要至少两个不同的、管理员明确配置的数字IPv4:UDP端口STUN端点；可复用已有自建STUN。此服务器二进制不内置STUN，也不隐式使用公共备用。留空时仍可已知地址直连/中继，不能称跨NAT打洞已启用。手机Clash开启时应在Clash排除Agent应用；新模式Windows TUN自动出站选择尚未完成。

管理命令（intent为下述JSON文件；操作UUID先保存，超时查询原操作）：

```powershell
$key = (Get-Content "$base\server-key.txt" -Raw).Trim()
$op = [guid]::NewGuid().ToString() # 先保存
& $owner server-device-set "$base\admin" "$base\server.json" $key device.json $op
& $owner server-operation-status "$base\admin" "$base\server.json" $key $op
```

设备intent：`{"device":"电脑deviceId","expectedRevision":"0","roles":{"owner":false,"publish":true,"access":true},"active":true}`。

服务用server-service-set，intent：`{"scope":{"publisherDevice":"发布设备ID","publisherApp":"org.example.trial.publisher","service":"sample"},"expectedRevision":"0","protocol":"tcp","authority":"","active":true}`。

访问用server-grant-set，intent：`{"scope":{"visitorDevice":"访问设备ID","visitorApp":"org.example.trial.client","target":{"publisherDevice":"发布设备ID","publisherApp":"org.example.trial.publisher","service":"sample"}},"expectedRevision":"0","active":true}`。

首次revision是0，更新/撤销使用已知当前revision。撤销把active改false。三种set参数次序相同。服务器授权、本机声明批准、访问端本地grant和发布端本地grant是不同边界，缺一会拒绝连接。

## 5. 启动与本地审批

从开始菜单启动LinkGate，或运行Start-LinkGate.cmd。输入stop/Ctrl+C停止；子运行时随之回收。启动只表示本地入口就绪，不能据此判断某项服务已连通。私有state目录中的agent-bootstrap.json给可信启动流程下的SDK示例使用，不要接受来源不明的bootstrap。

应用提交声明后，先检查app-show的manifest，再复制它的revision/version/hash批准：

```text
owner/LinkGate.Owner.Console.exe app-list <state>
owner/LinkGate.Owner.Console.exe app-show <state> <app-ref>
owner/LinkGate.Owner.Console.exe app-approve <state> <app-ref> <pending-revision> <decision-version> <confirmed-manifest-SHA256> <operation-UUID>
```

访问端：access-requests → access-propose导出检查 → access-approve。发布端：grant-propose导出检查 → grant-approve。trust命令先核对设备证书、公钥及已知IPv4/端口；导入设备时不要只相信对方自报ID。

```text
access-propose <state> <request-UUID> <new-proposal.json>
access-approve <state> <proposal.json> <operation-UUID>
access-list <state>
access-show <state> <grant-UUID>
access-revoke <state> <grant-UUID> <expected-revision> <operation-UUID>
grant-propose <state> <visitor-device-key> <client-app-id> <publisher-app-ref> <service> <new-proposal.json>
grant-approve <state> <proposal.json> <operation-UUID>
operation-status <state> <original-operation-UUID>
```

app-operation-status恢复声明审批；operation-status恢复本地设备/grant审批；server-operation-status恢复云端管理。未知结果不生成新UUID重复操作。

## 6. 独立SDK示例

示例不包含Broker、设备私钥或FRP。它核对可信bootstrap中的Agent程序摘要和启动时间，再把持有的进程句柄交给SDK。

```text
sample/LinkGate.Sample.exe publish <trusted-bootstrap.json> <publisher-output-dir>
sample/LinkGate.Sample.exe register <trusted-bootstrap.json> <client-output-dir> <publisher-device-key>
```

批准两个声明并配置服务/设备及两级grant后：

```text
sample/LinkGate.Sample.exe request <trusted-bootstrap.json> <client-output-dir> <publisher-device-key>
# 所有者核对并批准生成的access请求
sample/LinkGate.Sample.exe fetch <trusted-bootstrap.json> <client-output-dir> <publisher-device-key> direct
sample/LinkGate.Sample.exe fetch <trusted-bootstrap.json> <client-output-dir> <publisher-device-key> relay
```

每次传输2MiB确定性数据并核对摘要、FIN和路径，结果保存result-direct/relay.json。原业务继续使用自己的HTTPS/WSS、主机名校验和认证。SDK接口open→operation.status→attach→BusinessStream→close；注册和权限申请不能代替用户批准。

## 7. 限制与恢复

此包未执行新版本物理四网络及长稳验收；已有模拟器/同机传输证据不代替这些测试。IPv6、透明兼容入口、事件API、自动证书轮换和更新不在本试用范围。

网络变化后可停止网关并重新启动，应用显式建立新连接；已有业务不会自动重播。卸载前先停止网关。卸载保留身份及状态，重装使用同一目录；不以删除密钥/重新初始化数据库作为常规故障修复。

0.3.0-preview.2 保留原设备身份，修正拥塞中继的心跳及FIN排空等待。薄SDK控制管道有独立闲置限制；长业务流完成后，应用应显式重新连接、hello再查询原connectionId或operationId，不重新打开或重放业务。

仅访问的Android设备不监听发布端口，Windows可用 `trust-certificate <state> <identity> <phone-certificate> <expected-revision> <confirmed-phone-key> <operation-UUID>` 信任其证书，不填写虚构手机端口。该命令不授予任何业务服务访问权，具体应用仍须单独批准grant。

当前版本：Windows 0.3.0-preview.2；Android 0.3.0-preview.3（versionCode 4）。手机四个入口为概览、设备、应用、设置：启停在概览，证书/服务器/恢复工具在设置。APK沿用原包名与签名，覆盖安装保留配对。已获授权卸载旧com.nebula.linkgate及旧linkgate-frps服务；不要卸载新版Agent来更新界面。
