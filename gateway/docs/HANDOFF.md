# LinkGate 开发接续记录

## 最新：手机界面与旧版退役

Android已更新为 **0.3.0-preview.3 / versionCode4**，名称和通知统一为LinkGate。四个页面为概览、设备、应用、设置；原证书/签名/精确授权和恢复机制保留。Windows仍为0.3.0-preview.2。用户确认新版启动、原配置正常。

[界面与清理说明](../release/MOBILE-UI.md)、[证据](verification-v0.3-ui-retirement.json)：自有模拟器检查布局、放大字体和宽屏，物理手机只安装/读取状态，由用户操作画面。旧安卓包、旧linkgate-frps服务/程序/用户和17400规则已退役；12个旧便携/发行目录已移入回收站。私有恢复材料与新版身份保留，ReDesk/RustDesk/STUN未停用。发行材料统一在dist/LinkGate-v0.3.0-preview.3。

以下为先前阶段记录；其中“旧版保留”仅描述退役授权之前的状态。后续仍以接入首个业务应用为目标，不扩展传输功能。


日期：2026-10-30。**0.3.0-preview.2 已实际部署到既有ECS、Windows及Android**；原0.2.3和ReDesk保持独立。生产1.0与后续功能仍开放，不扩大本次部署范围。

## 最新部署

[部署与首次应用接入](../release/DEPLOYMENT.md)、[冻结证据](verification-v0.3-deployment.json)。新ECS服务linkgate-trial.service已启用，27111/27112分别承载认证控制及FRP；原服务和规则保留。临时上传SSH授权及凭据已移除。服务器二进制沿用首版已验证构建。

Windows通过安装器更新，身份未变；开始菜单有“启动已配置的网关”“停止已配置的网关”和本机说明。当前后台运行、peerPort固定27443；没有设置Windows开机任务。Android覆盖更新为versionCode3，开发签名未变，用户手动导入证书/服务器/对端并在升级后重新启动，ADB只读确认前台服务。未操作手机画面。

带宽限制的ECS联调实际发现并修复三点：2秒心跳误断开；排队数据的FIN确认等待过短；示例长传输后复用闲置控制管道。修正版真实强制中继2MiB、约4.1秒、摘要及FIN/complete通过；42项相关安全流检查通过。没有重跑全套、四网络或长稳。测试用云端服务/grant已撤销，正式应用状态库保持干净。

首个真实业务应用尚未选择/接入；Windows和Android设备互信已准备，**没有给未知应用预授权**。本机%LOCALAPPDATA%/LinkGateTrial/first-app/integration-context.json为不含私钥的接入引用。下一步先确定应用项目、本机端口及稳定application/service/import标识，再按SDK和所有者审批流程接入。手机跨NAT和移动网络业务在应用接入后验证。

下列首版交付记录保留历史证据；当前可用更新包在dist/LinkGate-v0.3.0-preview.2，旧preview.1包不再作为优先安装版本。

## 交付物与使用

交付目录：仓库根目录 `dist/LinkGate-v0.3.0-preview.1/`（Git忽略）。包含Windows x64 Setup、Android访问APK、Linux amd64认证服务器、QUICKSTART及SHA256SUMS。Windows安装包自带.NET运行时与新认证FRP，按当前用户安装，开始菜单启动；仍须按指南配置身份、服务器和精确授权，尚非全自动配对向导。

详细使用见 [Windows认证指南](WINDOWS-AUTHENTICATED.md) 和 [快速开始源码](../release/README-template.md)。新APK包名com.nebula.linkgate.agent，与旧com.nebula.linkgate不同。试用APK使用现有开发签名；Windows安装器未作生产签名，不宣称生产发行。卸载保留用户身份和状态。

## 本次通过的有限验收

[首次安装冻结证据](verification-v0.3-first-installable.json)：真实Setup安装，使用随包Owner/Agent及独立示例进程，连接隔离WSL Linux生产服务器；设备、服务、访问grant经真实mTLS管理命令授权和原操作恢复。独立应用直连和强制中继各2MiB，校验摘要、FIN、路径和完成状态。服务器撤权拒绝新连接；停止回收3个拥有的FRP子进程；卸载不删除身份。

验收保留两次失败记录：脚本漏建Linux0700目录；Owner解析器误把合法affected=0当错误。已分别修复环境准备及返回校验，最终完整流程通过。不得将有限验收说成物理移动网络或长稳已通过。

[动态运行时证据](verification-v0.3-scoped-windows-runtime.json)：23项所有权、130项范围/Owner检查；实际源码FRP/mTLS的66项SDK检查、9条业务连接，直连/中继/自动各64MiB。Windows动态发布/访问池和持续serve入口已完成。复用既有Android原生47项及跨语言400例证据，本次不重复执行全套。

## 后续范围

[后续成熟版spec](mature-release.sdd) 是唯一后续任务汇总。根spec任务5与release任务1–4完成；根任务2–4仍开放，不能把试用安装验收等同完整产品目标。

1. 服务prepare、健康/事件及更完整聚合诊断。
2. 更多客户端适配器、兼容入口及独立SDK发行。
3. 自动网络观察、新模式Windows TUN出站及恢复。
4. 证书续期/轮换、备份恢复、旧服务迁移。
5. 生产签名、自动更新及回滚。
6. ARM64真机、四组网络/VPN与独立业务。
7. 并发、10万短连接及24/72小时稳定性。

不要自行扩大当前试用版范围或反复运行大套测试。后续工作先由用户选择上述一项，再按相邻spec实施。

## 重现入口

- 包装：`gateway/release/build-trial.ps1`，输入明确的Inno编译器、已验证backend目录、已签APK及Linux服务器二进制；要求新输出目录。
- 一次隔离验收：`python gateway/release/verify-trial.py <package-output-directory>`；需要WSL Ubuntu-24.04，不访问ECS、不操作手机。
- 规格：在gateway中运行已固定的SpecDD CLI；当前无插件。
- 文档/敏感信息：`python scripts/check-repository.py`；合同形状由`gateway/spec/check_contracts.py`检查。

FRP固定提交d8cf1800dc1abd7fda1828cab105d7a6b5a50eb1，当前验证源码副本为artifacts/linkgate-backend-frp/verified-c175ce0dd81d4821bd6b899d7d611d9a。共享upstream/frp-linkgate保持只读。私人配置、密钥和生成二进制不得提交Git。旧技术历史和各阶段证据保存在[PROGRESS](PROGRESS.md)与ADR目录。
