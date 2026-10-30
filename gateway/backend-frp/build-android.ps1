param(
    [Parameter(Mandatory)][string]$Go,
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$NdkDirectory,
    [string]$Python='python'
)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$goExecutable=(Resolve-Path -LiteralPath $Go).Path
$ndk=(Resolve-Path -LiteralPath $NdkDirectory).Path
$output=Join-Path $repository ('artifacts/linkgate-android-runtime/'+[Guid]::NewGuid().ToString('N'))
$sourceCopy=Join-Path $repository ('artifacts/linkgate-backend-frp/android-'+[Guid]::NewGuid().ToString('N'))
$saved=@{};foreach($name in @('GOOS','GOARCH','CGO_ENABLED','CC','GOTOOLCHAIN')){$saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
try {
    $env:GOTOOLCHAIN='local'
    & $Python (Join-Path $PSScriptRoot 'prepare.py') --source (Resolve-Path -LiteralPath $Source).Path --output $sourceCopy --go $goExecutable
    if($LASTEXITCODE){throw 'Android source preparation failed.'}
    Push-Location $sourceCopy
    try {
        & $goExecutable mod tidy *> dependencies.log;if($LASTEXITCODE){throw 'Android runtime dependencies unavailable.'}
        & $goExecutable mod verify *> checksums.log;if($LASTEXITCODE){throw 'Android runtime module checksums rejected.'}
        $abis=[ordered]@{}
        foreach($target in @(@{Abi='arm64-v8a';Arch='arm64';Triple='aarch64-linux-android26'},@{Abi='x86_64';Arch='amd64';Triple='x86_64-linux-android26'})) {
            $compiler=Join-Path $ndk ('toolchains/llvm/prebuilt/windows-x86_64/bin/'+$target.Triple+'-clang.cmd')
            if(-not (Test-Path -LiteralPath $compiler)){throw 'Explicit Android NDK compiler missing.'}
            $directory=Join-Path $output ('jniLibs/'+$target.Abi);New-Item -ItemType Directory -Force -Path $directory|Out-Null
            $runtime=Join-Path $directory 'libfrpc.so';$launcher=Join-Path $directory 'liblinkgate-agent-launcher.so'
            $env:GOOS='android';$env:GOARCH=$target.Arch;$env:CGO_ENABLED='1';$env:CC=$compiler
            & $goExecutable build -tags frpc,noweb -trimpath -buildvcs=false -buildmode=pie '-ldflags=-s -w -buildid= -extldflags=-Wl,-z,max-page-size=16384' -o $runtime ./cmd/frpc *> (Join-Path $output ($target.Abi+'-go.log'))
            if($LASTEXITCODE){throw 'Android frpc source build failed.'}
            & $compiler -O2 -fPIE -pie '-Wl,-z,max-page-size=16384' -o $launcher (Join-Path $repository 'gateway/android/agent/src/main/cpp/launcher.c') *> (Join-Path $output ($target.Abi+'-guard.log'))
            if($LASTEXITCODE){throw 'Android child lifetime guard build failed.'}
            $abis[$target.Abi]=@{frpcSha256=(Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash.ToLowerInvariant();launcherSha256=(Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash.ToLowerInvariant()}
        }
    } finally {Pop-Location}
    $assets=Join-Path $output 'assets';New-Item -ItemType Directory -Force -Path $assets|Out-Null
    $lock=[ordered]@{schema=1;sourceCommit='d8cf1800dc1abd7fda1828cab105d7a6b5a50eb1';goVersion=(& $goExecutable version);abis=$abis}
    [IO.File]::WriteAllText((Join-Path $assets 'agent-runtime.lock.json'),($lock|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
    @{passed=$true;runtimeDirectory=$output;sourceDirectory=$sourceCopy;physicalDeviceTested=$false}|ConvertTo-Json
} finally {foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name],'Process')}}
