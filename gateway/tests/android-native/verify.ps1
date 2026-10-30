param(
    [Parameter(Mandatory)][string]$JdkDirectory,
    [Parameter(Mandatory)][string]$GradleDirectory,
    [Parameter(Mandatory)][string]$AndroidSdk,
    [Parameter(Mandatory)][string]$GradleCache,
    [string]$ExistingRuntimeDirectory,
    [string]$AgentRuntimeDirectory,
    [string]$AuthenticatedBackendDirectory
)
$ErrorActionPreference='Stop'
if([bool]$AgentRuntimeDirectory -ne [bool]$AuthenticatedBackendDirectory){throw 'Select both generated Agent runtime and authenticated backend directories.'}
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$artifactRoot=Join-Path $repository 'artifacts/linkgate-emulator'
$env:JAVA_HOME=[IO.Path]::GetFullPath($JdkDirectory)
$env:ANDROID_HOME=[IO.Path]::GetFullPath($AndroidSdk)
$env:GRADLE_USER_HOME=[IO.Path]::GetFullPath($GradleCache)
$env:ANDROID_AVD_HOME=Join-Path $artifactRoot 'avd'
$env:ANDROID_USER_HOME=Join-Path $artifactRoot 'user'
$env:ADB_MDNS_AUTO_CONNECT='0'
$env:LINKGATE_FIXTURE_KEYSTORE=Join-Path $artifactRoot 'consumer-fixture.p12'
$taskAdb=Join-Path $AndroidSdk 'platform-tools/adb.exe'
$gradle=Join-Path $GradleDirectory 'bin/gradle.bat'
$emulatorProject=Join-Path $PSScriptRoot 'EmulatorHarness'
$emulatorExecutable=Join-Path $emulatorProject 'bin/Release/net10.0-windows/EmulatorHarness.exe'
$runtime=$null
$heldAdbServer=$null
function Adb {
    param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments)
    if(!$heldAdbServer -or $heldAdbServer.HasExited){throw 'The held isolated ADB server has exited.'}
    $output=& $taskAdb -H 127.0.0.1 -P 5038 -s emulator-5556 @Arguments 2>&1
    if($LASTEXITCODE -ne 0){throw "Isolated emulator command failed: $output"}
    return $output
}
try {
    New-Item -ItemType Directory -Force -Path $artifactRoot,$env:ANDROID_AVD_HOME,$env:ANDROID_USER_HOME | Out-Null
    if(!(Test-Path -LiteralPath $env:LINKGATE_FIXTURE_KEYSTORE)){
        & (Join-Path $JdkDirectory 'bin/keytool.exe') -genkeypair -alias fixture -keyalg EC -groupname secp256r1 -validity 365 -keystore $env:LINKGATE_FIXTURE_KEYSTORE -storetype PKCS12 -storepass fixture-only -keypass fixture-only -dname 'CN=LinkGate independent native fixture' *> (Join-Path $artifactRoot 'fixture-key.log')
        if($LASTEXITCODE -ne 0){throw 'Independent fixture signer generation failed.'}
    }
    $runtimeArguments=@();if($AgentRuntimeDirectory){$runtimeArguments+=('-PlinkgateAgentRuntimeDir='+[IO.Path]::GetFullPath($AgentRuntimeDirectory))}
    & $gradle -p (Join-Path $repository 'gateway/android') '-Pandroid.useAndroidX=true' @runtimeArguments :agent:assembleDebug :agent:assembleDebugAndroidTest :native-consumer:assembleDebug :native-consumer:assembleDebugAndroidTest :client:testDebugUnitTest :agent-core:test :secure-transport:test --console=plain *> (Join-Path $repository 'artifacts/secure-android-native-build.log')
    if($LASTEXITCODE -ne 0){throw 'Native fixture build failed; inspect artifacts/secure-android-native-build.log.'}
    $nativeBuildId=[Guid]::NewGuid().ToString('N')
    $nativeApks=[ordered]@{}
    foreach($relative in @('agent/build/outputs/apk/debug/agent-debug.apk','agent/build/outputs/apk/androidTest/debug/agent-debug-androidTest.apk','native-consumer/build/outputs/apk/debug/native-consumer-debug.apk','native-consumer/build/outputs/apk/androidTest/debug/native-consumer-debug-androidTest.apk')){
        $nativeApks[$relative]=(Get-FileHash -LiteralPath (Join-Path $repository ('gateway/android/'+$relative)) -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $nativeBuild=[ordered]@{buildId=$nativeBuildId;builtUtc=[DateTime]::UtcNow.ToString('O');authenticatedRuntimeSelected=[bool]$AgentRuntimeDirectory;apks=$nativeApks}
    $buildManifests=Join-Path $repository 'artifacts/android-native-build-manifests';New-Item -ItemType Directory -Force -Path $buildManifests | Out-Null
    $nativeBuild | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $buildManifests ($nativeBuildId+'.json')) -Encoding UTF8
    & dotnet build $emulatorProject -c Release --nologo -v:q
    if($LASTEXITCODE -ne 0){throw 'Emulator owner build failed.'}
    if($ExistingRuntimeDirectory){
        $ready=Get-Content -LiteralPath (Join-Path $ExistingRuntimeDirectory 'ready.json') -Raw | ConvertFrom-Json
        $ownerProcess=[Diagnostics.Process]::GetProcessById($ready.ownerPid)
        if($ownerProcess.HasExited -or [IO.Path]::GetFullPath($ownerProcess.MainModule.FileName) -ne [IO.Path]::GetFullPath($emulatorExecutable) -or $ready.adbPort -ne 5038 -or $ready.serial -ne 'emulator-5556'){throw 'Existing runtime is not the held LinkGate emulator fixture.'}
        $ownerProcess.Dispose()
    }else{
        $avd=Join-Path $env:ANDROID_AVD_HOME 'linkgate_native_36.avd'
        if(!(Test-Path -LiteralPath (Join-Path $avd 'config.ini'))){
            $manager=Get-ChildItem -LiteralPath (Join-Path $AndroidSdk 'cmdline-tools') -Directory | Where-Object {Test-Path -LiteralPath (Join-Path $_.FullName 'bin/avdmanager.bat')} | Sort-Object Name -Descending | Select-Object -First 1
            if(!$manager){throw 'Android command-line tools and API 36 x86_64 image are required.'}
            'no' | & (Join-Path $manager.FullName 'bin/avdmanager.bat') create avd -n linkgate_native_36 -k 'system-images;android-36;default;x86_64' -p $avd -d pixel_6 *> (Join-Path $artifactRoot 'avd-create.log')
            if(!(Test-Path -LiteralPath (Join-Path $avd 'config.ini'))){throw 'Owned AVD creation failed.'}
        }
        $runtimeDirectory=Join-Path $artifactRoot ([Guid]::NewGuid().ToString('N'))
        $start=[Diagnostics.ProcessStartInfo]::new($emulatorExecutable);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden';$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
        $start.ArgumentList.Add([IO.Path]::GetFullPath($AndroidSdk));$start.ArgumentList.Add($runtimeDirectory)
        $runtime=[Diagnostics.Process]::Start($start)
        $readyText=$runtime.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).GetAwaiter().GetResult()
        if(!$readyText -or !($readyText|ConvertFrom-Json).owned){throw 'Owned emulator startup failed.'}
        $ready=$readyText|ConvertFrom-Json
    }
    $heldAdbServer=[Diagnostics.Process]::GetProcessById($ready.adbPid)
    $null=$heldAdbServer.SafeHandle
    if($heldAdbServer.HasExited -or [IO.Path]::GetFullPath($heldAdbServer.MainModule.FileName) -ne [IO.Path]::GetFullPath($taskAdb)){throw 'The isolated ADB server executable changed.'}
    $bootDeadline=[DateTime]::UtcNow.AddMinutes(2)
    do {
        try{$boot=(Adb @('shell','getprop','sys.boot_completed')) -join ''}catch{$boot=''}
        if($boot.Trim() -eq '1'){break}
        if([DateTime]::UtcNow -ge $bootDeadline){throw 'Owned emulator boot timed out.'}
        Start-Sleep -Milliseconds 500
    }while($true)
    if(((Adb @('shell','getprop','ro.kernel.qemu')) -join '').Trim() -ne '1' -or ((Adb @('shell','getprop','ro.product.cpu.abi')) -join '').Trim() -ne 'x86_64'){throw 'Refusing non-emulator target.'}
    foreach($package in @('com.nebula.linkgate.agent.test','org.example.linkgate.nativeconsumer.test','com.nebula.linkgate.agent','org.example.linkgate.nativeconsumer')){
        if(((Adb @('shell','pm','list','packages',$package)) -join "`n") -match ('(?m)^package:'+ [Regex]::Escape($package)+'\s*$')){Adb @('uninstall',$package) | Out-Null}
    }
    foreach($apk in @('agent/build/outputs/apk/debug/agent-debug.apk','agent/build/outputs/apk/androidTest/debug/agent-debug-androidTest.apk','native-consumer/build/outputs/apk/debug/native-consumer-debug.apk','native-consumer/build/outputs/apk/androidTest/debug/native-consumer-debug-androidTest.apk')){Adb @('install','-r',(Join-Path $repository ('gateway/android/'+$apk))) | Out-Null}
    foreach($package in @('com.nebula.linkgate.agent','org.example.linkgate.nativeconsumer')){Adb @('shell','pm','clear',$package) | Out-Null}
    $request=(Adb @('shell','am','instrument','-w','-e','class','dev.linkgate.agent.android.NativeOwnerTest','-e','mode','request','com.nebula.linkgate.agent.test/androidx.test.runner.AndroidJUnitRunner')) -join "`n"
    if($request -notmatch 'OK \(1 test\)'){throw "Native Keystore/descriptor preflight failed: $request"}
    $signatures=& (Join-Path $AndroidSdk 'build-tools/36.0.0/apksigner.bat') verify --print-certs (Join-Path $repository 'gateway/android/agent/build/outputs/apk/debug/agent-debug.apk')
    if($LASTEXITCODE -ne 0){throw 'Agent APK signer verification failed.'}
    $agentSigner=($signatures | Where-Object {$_ -match '^Signer #1 certificate SHA-256 digest:'}) -replace '^Signer #1 certificate SHA-256 digest: ',''
    if($AuthenticatedBackendDirectory){
        & dotnet run --project (Join-Path $repository 'gateway/tests/frp-authority/dotnet') -c Release -- (Join-Path $AuthenticatedBackendDirectory 'authority-fixture.exe') (Join-Path $AuthenticatedBackendDirectory 'frpc-authority.exe') $taskAdb *> (Join-Path $repository 'artifacts/secure-android-native-run.log')
    }else{& dotnet run --project (Join-Path $PSScriptRoot 'NativeHarness') -c Release -- $taskAdb $agentSigner *> (Join-Path $repository 'artifacts/secure-android-native-run.log')}
    if($LASTEXITCODE -ne 0){throw 'Native gateway acceptance failed; inspect artifacts/secure-android-native-run.log.'}
    $summary=if($AuthenticatedBackendDirectory){'artifacts/frp-native-authority-summary.json'}else{'artifacts/linkgate-native-acceptance-summary.json'}
    $accepted=Get-Content -LiteralPath (Join-Path $repository $summary) -Raw | ConvertFrom-Json
    if(!$accepted.passed){throw 'Native acceptance summary did not pass.'}
    $accepted | Add-Member -NotePropertyName nativeBuild -NotePropertyValue $nativeBuild -Force
    $accepted | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $repository $summary) -Encoding UTF8
    if($AuthenticatedBackendDirectory){$accepted | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $repository ('artifacts/frp-native-authority-runs/'+$accepted.runId+'.json')) -Encoding UTF8}
    Get-Content -LiteralPath (Join-Path $repository $summary)
}finally{
    if($heldAdbServer){$heldAdbServer.Dispose()}
    if($runtime){
        Set-Content -LiteralPath (Join-Path $runtimeDirectory 'stop') -Value 'verification-complete'
        if(!$runtime.WaitForExit(15000)){$runtime.Kill();$runtime.WaitForExit()}
        $runtime.Dispose()
    }
}
