# ADR0013：可接入的安全直连数据版

目标是先让独立应用完成真正的数据收发，且不跳过设备身份、发布端授权或撤销。首个可用范围为Windows已知IPv4端点的安全TCP直连；不启动旧FRP共享凭据路径，不宣称复杂NAT打洞、中继、Android或完整1.0已验收。旧0.2.3运行版保持独立。

设备在本地生成P-256私钥和CSR，所有者签发非CA的设备证书。Agent使用.NET SslStream标准TLS1.2/1.3、私有CustomRootTrust、证书有效期/EKU/名称检查及显式SPKI信任记录；不安装系统根证书、不跳过证书错误。参考[客户端选项](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslclientauthenticationoptions?view=net-10.0)、[CSR校验](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.certificaterequest.loadsigningrequestpem?view=net-10.0)。设备身份是所有者批准的软件密钥，不是硬件证明。

发布端是实际权限执行者：grant绑定客户端设备密钥、客户端应用、已审核发布者应用和固定export。客户端应用标识由可信Agent从实际IPC注册范围产生，普通应用不持有设备密钥。Windows仍是每用户协作信任域，不声称隔离同用户恶意EXE。旧待确认申请不自动变成grant；所有者须在发布端明确发放最小权限。

数据和控制分开使用已认证TLS通道，以免业务背压阻塞租约。数据附接单次、10秒失效，绑定同一设备/服务/授权版本和发布端代次。租约使用单调时间、最长120秒，正常续期不超过30秒间隔；失去控制不能延长授权。已知撤销拒绝新打开，活动会话监控在5秒内关闭。所有失败不自动重发业务。

本机应用取得独立私有数据管道，SDK核验同一可信Agent进程后附接，Agent再核验实际管道调用范围。有限帧保留TCP两个方向、半关闭和背压。HTTP/HTTPS/WS使用同一字节流，保留应用自己的认证、authority和TLS。

验收必须包括独立应用大文件、WebSocket、半关闭、未授权拒绝、活动撤销及重新打开拒绝。直到这些完成，不把本工作包标为可用。物理多设备/NAT/中继、完整配对运维、外部数据库反回滚与发布安全仍按原验收规格保持开放。
