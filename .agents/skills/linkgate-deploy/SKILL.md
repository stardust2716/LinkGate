---
name: linkgate-deploy
description: 部署、安装和排查LinkGate认证应用网关，涵盖Linux/ECS、Windows、Android配对与首个SDK应用授权；用于LinkGate操作，不替代业务应用改造或通用VPN部署。
---

# LinkGate 部署安装

先找到包含gateway/release/version.json和gateway/backend-frp的仓库根。用户未指定来源时，项目公共仓库为 https://github.com/stardust2716/LinkGate；只使用用户选定或已核对的版本。无源码时获取对应源码或可信Release；不依赖维护者的private、artifacts或本机目录。

下列相对链接供仓库内阅读；若skill被单独安装到用户目录，把docs路径解析到刚定位的仓库根，不把skill的上级目录当项目。

阅读[部署指南](../../../docs/DEPLOYMENT.md)；源码构建再读[构建说明](../../../docs/BUILD.md)。核对所选Release的文件、版本、架构、SHA256SUMS和签名状态。Windows和Android版本独立，不能强求同号。当前试用版使用开发签名/未生产签名，不宣称生产就绪。

## 确认目标与边界

从已有配置盘点主机/地域/SSH管理方式、Linux发行版和架构、占用端口、现有服务、电脑身份与目标Android设备。只询问阻断当前操作的缺失信息，不索取聊天中的密码或私钥。用户要求指导时只给步骤；要求执行时在其授权范围内推进。安装与操作手机画面的范围分别核对，不自行开始远控。已有授权不重复询问。

使用已验证SSH主机身份或已有云管理通道。生成只含公开参数的部署计划及回滚记录，然后实施本次需要的增量。已有同名服务、目录或初始化记录先查归属；不要覆盖旧权限库、见证或设备身份。不要调用历史gateway/server安装器来部署新认证协议。

## Linux / ECS

1. 使用Release的Linux amd64认证server，核对摘要；建立独立非登录服务账户及明确路径，必要时与现有服务并行部署。
2. 身份/权限库/配置目录0700、文件0600，归服务账户；见证独立于权限库备份。按指南在Linux执行request生成私钥及公开CSR，私钥留Linux。
3. 所有者在可信电脑签发公开证书，仅传回叶证书和公开根；管理员设备证书用于全新环境init。已有身份沿用，init只做一次。
4. 配置独立的controlAddress/backendAddress（示例27111/27112 TCP）。检查服务、实际PID/程序摘要和监听后，再核对对应安全组与主机防火墙。只开放本次所需规则，保留管理员及共享服务。
5. 需要持久后台运行时核对unit路径/账户后安装systemd服务；用正常stop/start操作本次unit。raw FRP、Dashboard或任意公开业务端口均不替代认证入口。

新服务不内置STUN。跨NAT XTCP需要至少两个明确配置、不同的真实STUN目标；可复用已有服务，使用公共目标时明确记录。仅验证探测不能证明业务P2P。

## Windows / Android 配对

Windows完整安装Setup，保留自包含运行时和身份/状态目录。用随包Owner创建或沿用所有者根、管理员及电脑身份；配置公开server.json并独立确认服务器SPKI。菜单只启动网关，首次配置与授权仍按指南完成。需要稳定手机入口时显式选择peerPort，并仅添加本程序所需防火墙规则。Windows UAC由交互管理员确认，不绕过系统提示。

Android同包名、同签名覆盖安装，不清数据或卸载当前Agent绕过错误。用户在LinkGate“设置 → 设备身份”导出CSR，所有者签发后导入证书与公开根，核对根指纹；再导入服务器和电脑公开配置，核对其身份，手动启动前台网关。Android界面证书SHA256与Owner cert-key的SPKI SHA256是不同摘要，不混用。私钥不跨设备导入导出。

仅访问的Android设备可用Owner的trust-certificate信任，无需虚构手机发布端口。手机VPN开启时按用户意图配置其应用排除，不能擅自改第三方VPN全局规则。

## 应用授权与验证

网关安装/设备互信不授权具体应用。确定真实applicationId、serviceId、importAlias和本机目标端口；分别检查服务器device/service/grant、本地声明审核、访问端grant和发布端grant。批准前核对实际包/签名或程序身份与精确范围，不从应用自报名称推定可信。

业务按SDK register → 审批 → grant.request → 精确授权 → open → operation.status → attach → stream → close。保存原操作UUID再提交；超时/断网后查原结果，不换UUID盲重试，不重放业务。

分层验收配置、设备注册、实际路径及数据摘要/字节/FIN、撤权与正常停止回收。一次小规模独立应用传输优先于重复大套。区分同机、模拟器、真机和物理移动网络证据；无法直连但中继可用时如实报告。

## 交付

列出真实安装版本、服务/入口、授权范围、验证与未验证项、正常启停和恢复方式。私有材料写受限目录，报告不含令牌、私钥或业务正文。移除旧服务仅在用户授权后，核验归属、保留恢复材料并验证新服务；不按名称批量杀进程或清空共享防火墙。
