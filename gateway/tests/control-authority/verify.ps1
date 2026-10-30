param(
    [Parameter(Mandatory)][string]$Go,
    [Parameter(Mandatory)][string]$JdkDirectory,
    [Parameter(Mandatory)][string]$GradleDirectory,
    [Parameter(Mandatory)][string]$AndroidSdk,
    [Parameter(Mandatory)][string]$GradleCache
)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$previousJava=$env:JAVA_HOME;$previousSdk=$env:ANDROID_HOME;$previousCache=$env:GRADLE_USER_HOME;$previousToolchain=$env:GOTOOLCHAIN
$output=Join-Path $repository 'artifacts/control-authority-build';New-Item -ItemType Directory -Force -Path $output | Out-Null
try {
    $env:JAVA_HOME=[IO.Path]::GetFullPath($JdkDirectory);$env:ANDROID_HOME=[IO.Path]::GetFullPath($AndroidSdk);$env:GRADLE_USER_HOME=[IO.Path]::GetFullPath($GradleCache);$env:GOTOOLCHAIN='local'
    $server=Join-Path $output 'control-server-fixture.exe'
    Push-Location (Join-Path $repository 'gateway/control-plane')
    try {& $Go build -trimpath -o $server ./controlinterop;if($LASTEXITCODE){throw 'Actual Go control fixture build failed.'}}finally{Pop-Location}
    & (Join-Path $GradleDirectory 'bin/gradle.bat') -p (Join-Path $repository 'gateway/android') :control-authority:writeInteropClasspath --console=plain *> (Join-Path $output 'java-build.log')
    if($LASTEXITCODE){throw 'Java control fixture build failed.'}
    $classpath=Get-Content -LiteralPath (Join-Path $repository 'gateway/android/control-authority/build/interop-classpath.txt') -Raw
    & dotnet build (Join-Path $PSScriptRoot 'dotnet') -c Release --nologo -v:q
    if($LASTEXITCODE){throw '.NET control fixture build failed.'}
    & dotnet run --no-build --project (Join-Path $PSScriptRoot 'dotnet') -c Release -- $server (Join-Path $JdkDirectory 'bin/java.exe') $classpath
    if($LASTEXITCODE){throw 'Actual mTLS control interoperability failed.'}
} finally {
    $env:JAVA_HOME=$previousJava;$env:ANDROID_HOME=$previousSdk;$env:GRADLE_USER_HOME=$previousCache;$env:GOTOOLCHAIN=$previousToolchain
}
