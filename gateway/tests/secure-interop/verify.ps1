#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$JdkDirectory,[Parameter(Mandatory=$true)][string]$GradleDirectory,[Parameter(Mandatory=$true)][string]$AndroidSdk,[Parameter(Mandatory=$true)][string]$GradleCache)
$ErrorActionPreference='Stop'
$repositoryRoot=Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$previousJava=$env:JAVA_HOME
$previousSdk=$env:ANDROID_HOME
$previousCache=$env:GRADLE_USER_HOME
Push-Location $repositoryRoot
try {
    $env:JAVA_HOME=[IO.Path]::GetFullPath($JdkDirectory)
    $env:ANDROID_HOME=[IO.Path]::GetFullPath($AndroidSdk)
    $env:GRADLE_USER_HOME=[IO.Path]::GetFullPath($GradleCache)
    & (Join-Path $GradleDirectory 'bin/gradle.bat') -p gateway/android :secure-transport:test :secure-transport:writeInteropClasspath --console=plain
    if($LASTEXITCODE){throw 'Portable Java transport verification failed.'}
    & dotnet build gateway/tests/secure-interop/InteropHarness -c Release
    if($LASTEXITCODE){throw 'Windows interoperability fixture build failed.'}
    $classes=Get-Content gateway/android/secure-transport/build/interop-classpath.txt -Raw
    $result=& ./gateway/tests/secure-interop/InteropHarness/bin/Release/net10.0-windows/InteropHarness.exe (Join-Path $JdkDirectory 'bin/java.exe') $classes
    if($LASTEXITCODE){throw 'Actual Windows/Java transport conformance failed.'}
    $summary=$result|ConvertFrom-Json
    if(!$summary.passed -or !$summary.windowsJavaMutualTls -or $summary.fileBytes -ne 67108864 -or $summary.activeRevokeMs -ge 5000){throw 'Incomplete interoperability evidence.'}
    $json=$summary|ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $repositoryRoot 'artifacts/linkgate-java-interop-summary.json'),$json)
    $json
} finally {
    $env:JAVA_HOME=$previousJava
    $env:ANDROID_HOME=$previousSdk
    $env:GRADLE_USER_HOME=$previousCache
    Pop-Location
}
