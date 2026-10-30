# 源码构建

保留仓库中的gateway目录结构，在仓库根执行命令。生成目录、工具链、私人配置和运行时均被Git忽略。

## 工具与依赖

- Windows .NET SDK 10、PowerShell；发布脚本当前选择.NET运行时10.0.5与win-x64，自包含运行时需要可用的NuGet包。
- Android JDK17、Gradle9.3.1、SDK37/target36、NDK29.0.13113456；实际版本以gateway/android项目配置为准。自行设置ANDROID_HOME、JAVA_HOME和GRADLE_USER_HOME，不依赖维护者的本机目录。
- Go工具链按go.mod选择；已验证构建使用Go1.27.3。Python3用于补丁准备，合同检查另需jsonschema。
- Inno Setup用于Windows Setup；仅打包时需要。

## 固定后端

```powershell
git clone https://github.com/fatedier/frp.git upstream/frp
git -C upstream/frp checkout d8cf1800dc1abd7fda1828cab105d7a6b5a50eb1
./gateway/backend-frp/verify.ps1 -Go C:\path\to\go.exe -Source ./upstream/frp
```

验证脚本在artifacts生成独立源码副本、校验依赖、执行相关Go检查并生成认证frpc与验证记录。Android运行时由[gateway/backend-frp/build-android.ps1](../gateway/backend-frp/build-android.ps1)生成；先读其参数及[后端说明](../gateway/backend-frp/README.md)。不要把旧stock FRP或普通Linux ELF当作新认证Android运行时。

Go模块路径仍保留历史Redesk标识，构建工具通过本仓库的本地replace绑定实际control-plane源码；无需从另一个项目取得私有代码。上游FRP许可随源补丁和分发包保留。

## Windows 与 Android

```powershell
dotnet build gateway/windows/LinkGate.Agent.Console -c Release
dotnet build gateway/windows/LinkGate.Owner.Console -c Release
# 从仓库根调用自己安装的Gradle：
gradle -p gateway/android :agent:assembleRelease -Pandroid.useAndroidX=true -PlinkgateAgentRuntimeDir=C:/path/to/generated-runtime
```

Android release当前沿用开发签名；对外分发前核对实际签名及嵌入运行时摘要。更换签名会影响覆盖升级及SDK信任，不得随意卸载当前Agent以绕过签名错误。

Windows/服务器/APK打包入口是[gateway/release/build-trial.ps1](../gateway/release/build-trial.ps1)，明确传入Compiler、BackendDirectory、AndroidApk和ServerBinary，要求新输出目录；版本分别在[version.json](../gateway/release/version.json)。Linux服务器用准备好的后端源码执行GOOS=linux、GOARCH=amd64构建`./cmd/linkgate-server`，服务端私钥在目标Linux设备生成。

## 有限验证

```powershell
python -m pip install jsonschema
python scripts/check-repository.py
python gateway/spec/check_contracts.py
dotnet run --project gateway/windows/LinkGate.Broker.Tests -c Release -- --secure-stream
```

按改动选择检查，不自动重复大套、物理网络或长稳。安装/ADB与部署需要独立明确的操作范围。SpecDD工具安装和用法见[gateway/docs/SPEC-DD.md](../gateway/docs/SPEC-DD.md)。根目录旧gateway/build.ps1是0.2历史入口，当前认证试用版从release和backend-frp入口构建。
