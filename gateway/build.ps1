#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$AndroidToolchain = '',
    [string]$GradleDirectory = '',
    [string]$OutputDirectory = '',
    [switch]$SkipRuntimeDownload,
    [switch]$SkipWindows,
    [switch]$SkipAndroid
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$version = Get-Content (Join-Path $root 'gateway/version.json') -Raw | ConvertFrom-Json
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root ('dist/LinkGate-v' + $version.version) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$runtimeLockPath=Join-Path $root 'gateway/frp-diagnostics/assets/runtime.lock.json'
if (!(Test-Path -LiteralPath $runtimeLockPath)) {throw 'Build the pinned instrumented clients first: gateway/frp-diagnostics/build.ps1. See gateway/frp-diagnostics/README.md.'}
$runtimeLock=Get-Content -LiteralPath $runtimeLockPath -Raw|ConvertFrom-Json
# 0.2 clients require instrumentation. Never silently replace them with stock frpc.
if (!$SkipWindows) {
    $runtime = Join-Path $root 'gateway/windows/runtime/frpc.exe'
    if (!(Test-Path -LiteralPath $runtime)) { throw 'Verified Windows FRP runtime is absent.' }
    if ((Get-FileHash $runtime -Algorithm SHA256).Hash.ToLowerInvariant() -ne $runtimeLock.windowsSha256) {throw 'Windows diagnostics runtime hash mismatch.'}
    & dotnet run --project (Join-Path $root 'gateway/windows/LinkGate.Tests/LinkGate.Tests.csproj') -c Release
    if ($LASTEXITCODE) { throw 'Windows gateway checks failed.' }
    $win = Join-Path $OutputDirectory 'windows'
    & dotnet publish (Join-Path $root 'gateway/windows/LinkGate/LinkGate.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $win
    if ($LASTEXITCODE) { throw 'Windows gateway publish failed.' }
    [IO.Directory]::CreateDirectory((Join-Path $win 'runtime')) | Out-Null
    Copy-Item -LiteralPath $runtime -Destination (Join-Path $win 'runtime/frpc.exe')
    Copy-Item -LiteralPath (Join-Path $root 'gateway/frp-diagnostics/FRP-LICENSE') -Destination (Join-Path $win 'runtime/LICENSE.frp')
}
if (!$SkipAndroid) {
    if (!$AndroidToolchain) { $AndroidToolchain = $env:REDESK_ANDROID_TOOLCHAIN }
    if (!$AndroidToolchain) { $AndroidToolchain = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'ATRI/external/android-toolchain' }
    $sdk = Join-Path $AndroidToolchain 'sdk'
    $jdk = Get-ChildItem (Join-Path $AndroidToolchain 'jdk') -Directory | Where-Object Name -like 'jdk-17*' | Select-Object -First 1
    if (!$GradleDirectory) {$GradleDirectory=Join-Path $AndroidToolchain 'gradle-9.3.1'}
    $gradle = Join-Path $GradleDirectory 'bin/gradle.bat'
    $ndk = Join-Path $sdk 'ndk/29.0.13113456'
    $clang = Join-Path $ndk 'toolchains/llvm/prebuilt/windows-x86_64/bin/aarch64-linux-android26-clang.cmd'
    if (!$jdk -or !(Test-Path -LiteralPath $gradle) -or !(Test-Path -LiteralPath $clang)) { throw 'JDK17, Gradle9.3.1, SDK36 and NDK29.0.13113456 are required; use -GradleDirectory for an explicit Gradle installation.' }
    $env:JAVA_HOME = $jdk.FullName
    $env:ANDROID_HOME = $sdk
    $env:ANDROID_SDK_ROOT = $sdk
    $env:GRADLE_USER_HOME = Join-Path $root 'tools/gradle-home'
    $env:PATH = (Join-Path $jdk.FullName 'bin') + ';' + $env:PATH
    $android = Join-Path $root 'gateway/android'
    $native = Join-Path $android 'app/src/main/jniLibs/arm64-v8a'
    if (!(Test-Path -LiteralPath (Join-Path $native 'libfrpc.so'))) { throw 'Verified Android FRP runtime is absent.' }
    if ((Get-FileHash (Join-Path $native 'libfrpc.so') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $runtimeLock.androidSha256) {throw 'Android diagnostics runtime hash mismatch.'}
    & $clang -O2 -Wall -Wextra -Werror -fPIE -pie '-Wl,-z,max-page-size=16384' '-Wl,-z,common-page-size=4096' (Join-Path $android 'app/src/main/cpp/launcher.c') -o (Join-Path $native 'liblinkgate-launcher.so')
    if ($LASTEXITCODE) { throw 'Android parent-death guard compilation failed.' }
    & $gradle --no-daemon -p $android :app:testDebugUnitTest :app:assembleRelease
    if ($LASTEXITCODE) { throw 'Android gateway checks or build failed.' }
    $signing = Join-Path $root 'private/linkgate-signing'
    [IO.Directory]::CreateDirectory($signing) | Out-Null
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls.exe $signing /inheritance:r /grant:r ('*' + $sid + ':(OI)(CI)F') '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE) { throw 'Could not protect the signing directory.' }
    $passwordFile = Join-Path $signing 'password.dpapi'
    $keystore = Join-Path $signing 'linkgate.p12'
    if (!(Test-Path -LiteralPath $passwordFile)) {
        if (Test-Path -LiteralPath $keystore) { throw 'Existing signing identity has no protected password.' }
        $random = New-Object byte[] 48
        $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $generator.GetBytes($random) } finally { $generator.Dispose() }
        $secure = ConvertTo-SecureString ([Convert]::ToBase64String($random)) -AsPlainText -Force
        $secure | ConvertFrom-SecureString | Set-Content -LiteralPath $passwordFile -Encoding ASCII
        $secure.Dispose()
        [Array]::Clear($random, 0, $random.Length)
    }
    $secure = ConvertTo-SecureString ((Get-Content -LiteralPath $passwordFile -Raw).Trim())
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $env:LINKGATE_KEYSTORE_PASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer); $secure.Dispose() }
    try {
        if (!(Test-Path -LiteralPath $keystore)) {
            & (Join-Path $jdk.FullName 'bin/keytool.exe') -genkeypair -keystore $keystore -storetype PKCS12 -alias linkgate -keyalg RSA -keysize 3072 -validity 3650 -dname 'CN=LinkGate Local Development,O=Nebula,C=CN' -storepass:env LINKGATE_KEYSTORE_PASSWORD -keypass:env LINKGATE_KEYSTORE_PASSWORD
            if ($LASTEXITCODE) { throw 'Signing identity generation failed.' }
        }
        $unsigned = Join-Path $android 'app/build/outputs/apk/release/app-release-unsigned.apk'
        $aligned = Join-Path $OutputDirectory 'LinkGate-aligned.tmp.apk'
        $apk = Join-Path $OutputDirectory ('LinkGate-v' + $version.version + '-arm64.apk')
        & (Join-Path $sdk 'build-tools/36.0.0/zipalign.exe') -f -P 16 4 $unsigned $aligned
        if ($LASTEXITCODE) { throw 'APK alignment failed.' }
        & (Join-Path $sdk 'build-tools/36.0.0/apksigner.bat') sign --ks $keystore --ks-key-alias linkgate --ks-pass env:LINKGATE_KEYSTORE_PASSWORD --out $apk $aligned
        if ($LASTEXITCODE) { throw 'APK signing failed.' }
        & (Join-Path $sdk 'build-tools/36.0.0/apksigner.bat') verify --verbose --print-certs $apk
        if ($LASTEXITCODE) { throw 'APK signature verification failed.' }
        Remove-Item -LiteralPath $aligned
        Write-Output ('Signed Android APK: ' + $apk)
    } finally { $env:LINKGATE_KEYSTORE_PASSWORD = $null }
}
