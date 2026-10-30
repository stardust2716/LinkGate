#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Go,
    [string]$AndroidClang = '',
    [switch]$Prepare
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$Source=[IO.Path]::GetFullPath($Source)
$Go=[IO.Path]::GetFullPath($Go)
if ($Prepare) {
    & python (Join-Path $root 'gateway/frp-diagnostics/prepare.py') --source $Source
    if ($LASTEXITCODE) {throw 'Pinned source preparation failed.'}
}
$revision=(& git -C $Source rev-parse HEAD).Trim()
if ($revision -ne 'd8cf1800dc1abd7fda1828cab105d7a6b5a50eb1') {throw 'FRP source revision mismatch.'}
$format=Join-Path (Split-Path -Parent $Go) 'gofmt.exe'
& $format -w (Join-Path $Source 'client/proxy/proxy.go') (Join-Path $Source 'client/visitor/stcp.go') (Join-Path $Source 'client/visitor/xtcp.go') (Join-Path $Source 'pkg/linkgatemetrics/metrics.go') (Join-Path $Source 'pkg/linkgatemetrics/metrics_test.go')
if($LASTEXITCODE){throw 'Go formatting failed.'}
& python (Join-Path $root 'gateway/frp-diagnostics/verify.py') --source $Source
if($LASTEXITCODE){throw 'Exact diagnostics source verification failed.'}
$env:GOTOOLCHAIN='local'
$env:CGO_ENABLED='0'
$env:GOOS='windows';$env:GOARCH='amd64'
Push-Location $Source
try {
    # Race mode additionally requires CGO; use go test -race in Linux CI.
    & $Go test ./pkg/linkgatemetrics
    if ($LASTEXITCODE) {throw 'FRP metrics tests failed.'}
    & $Go test ./pkg/linkgateegress
    if ($LASTEXITCODE) {throw 'Windows egress binding tests failed.'}
    & $Go test -tags frpc,noweb ./pkg/nathole
    if ($LASTEXITCODE) {throw 'STUN discovery tests failed.'}
    $windows=Join-Path $root 'gateway/windows/runtime/frpc.exe'
    [IO.Directory]::CreateDirectory((Split-Path -Parent $windows))|Out-Null
    & $Go build -tags frpc,noweb -trimpath -buildvcs=false '-ldflags=-s -w -buildid=' -o $windows ./cmd/frpc
    if ($LASTEXITCODE) {throw 'Windows FRP diagnostics build failed.'}
    if (!$AndroidClang) {throw 'Supply the Android API26 ARM64 NDK clang path to build the Android runtime.'}
    $env:GOOS='android';$env:GOARCH='arm64';$env:CGO_ENABLED='1';$env:CC=$AndroidClang
    $android=Join-Path $root 'gateway/android/app/src/main/jniLibs/arm64-v8a/libfrpc.so'
    [IO.Directory]::CreateDirectory((Split-Path -Parent $android))|Out-Null
    & $Go build -tags frpc,noweb -trimpath -buildvcs=false -buildmode=pie '-ldflags=-s -w -buildid= -extldflags=-Wl,-z,max-page-size=16384' -o $android ./cmd/frpc
    if ($LASTEXITCODE) {throw 'Android FRP diagnostics build failed.'}
    $lock=[ordered]@{frpVersion='0.71.1';sourceCommit=$revision;metricsSchema=1;goVersion=(& $Go version);windowsSha256=(Get-FileHash $windows -Algorithm SHA256).Hash.ToLowerInvariant();androidSha256=(Get-FileHash $android -Algorithm SHA256).Hash.ToLowerInvariant()}
    [IO.Directory]::CreateDirectory((Join-Path $root 'gateway/frp-diagnostics/assets'))|Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'gateway/frp-diagnostics/assets/runtime.lock.json'),($lock|ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
} finally {Pop-Location}
