param(
    [Parameter(Mandatory)][string]$Go,
    [Parameter(Mandatory)][string]$JdkDirectory,
    [Parameter(Mandatory)][string]$GradleDirectory,
    [Parameter(Mandatory)][string]$AndroidSdk,
    [Parameter(Mandatory)][string]$GradleCache
)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$env:JAVA_HOME=[IO.Path]::GetFullPath($JdkDirectory);$env:ANDROID_HOME=[IO.Path]::GetFullPath($AndroidSdk);$env:GRADLE_USER_HOME=[IO.Path]::GetFullPath($GradleCache);$env:GOTOOLCHAIN='local'
$output=Join-Path $repository 'artifacts/server-authority-build';New-Item -ItemType Directory -Force -Path $output | Out-Null
$issuer=Join-Path $output 'lease-issuer.exe'
Push-Location (Join-Path $repository 'gateway/control-plane')
try {& $Go build -trimpath -o $issuer ./interop;if($LASTEXITCODE){throw 'Go fixture issuer build failed.'}}finally{Pop-Location}
& (Join-Path $GradleDirectory 'bin/gradle.bat') -p (Join-Path $repository 'gateway/android') :control-authority:writeInteropClasspath --console=plain *> (Join-Path $output 'java-build.log')
if($LASTEXITCODE){throw 'Java authority fixture build failed.'}
$classpath=Get-Content -LiteralPath (Join-Path $repository 'gateway/android/control-authority/build/interop-classpath.txt') -Raw
& dotnet build (Join-Path $repository 'gateway/control-auth/dotnet/LinkGate.Authority.Tests') -c Release --nologo -v:q
if($LASTEXITCODE){throw '.NET authority fixture build failed.'}
& dotnet run --no-build --project (Join-Path $repository 'gateway/control-auth/dotnet/LinkGate.Authority.Tests') -c Release -- $issuer (Join-Path $JdkDirectory 'bin/java.exe') $classpath
if($LASTEXITCODE){throw 'Cross-platform server authority verification failed.'}
