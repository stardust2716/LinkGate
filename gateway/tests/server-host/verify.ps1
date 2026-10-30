param(
    [Parameter(Mandatory)][string]$Go,
    [Parameter(Mandatory)][string]$BackendDirectory,
    [Parameter(Mandatory)][string]$WslDistribution
)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$goExecutable=(Resolve-Path -LiteralPath $Go).Path
$backend=(Resolve-Path -LiteralPath $BackendDirectory).Path
$allowed=[IO.Path]::GetFullPath((Join-Path $repository 'artifacts/linkgate-backend-frp'))+[IO.Path]::DirectorySeparatorChar
if(-not $backend.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'A derived backend artifact directory is required.'}
$proof=Get-Content -LiteralPath (Join-Path $backend 'verification.json') -Raw | ConvertFrom-Json
if(-not $proof.passed -or $proof.sourceCommit -ne 'd8cf1800dc1abd7fda1828cab105d7a6b5a50eb1' -or -not (Test-Path -LiteralPath (Join-Path $backend 'cmd/linkgate-server/main.go'))){throw 'Verified source-built server command required.'}
$output=Join-Path $repository ('artifacts/linkgate-server-host/'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output|Out-Null
$saved=@{};foreach($name in @('GOOS','GOARCH','CGO_ENABLED','GOTOOLCHAIN')){$saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
try {
    $env:GOTOOLCHAIN='local';$env:GOOS='linux';$env:GOARCH='amd64';$env:CGO_ENABLED='0'
    Push-Location (Join-Path $repository 'gateway/control-plane')
    try {
        & $goExecutable test -c -trimpath -buildvcs=false -o (Join-Path $output 'host.test') ./serverhost *> (Join-Path $output 'host-build.log')
        if($LASTEXITCODE){throw 'Linux host test compilation failed.'}
        & $goExecutable vet ./serverhost *> (Join-Path $output 'host-vet.log')
        if($LASTEXITCODE){throw 'Linux host vet failed.'}
    } finally {Pop-Location}
    Push-Location $backend
    try {
        & $goExecutable test -c -trimpath -buildvcs=false -tags noweb -o (Join-Path $output 'server.test') ./cmd/linkgate-server *> (Join-Path $output 'server-build.log')
        if($LASTEXITCODE){throw 'Linux server test compilation failed.'}
        & $goExecutable build -trimpath -buildvcs=false -tags noweb -o (Join-Path $output 'linkgate-server-linux-amd64') ./cmd/linkgate-server *> (Join-Path $output 'server-binary-build.log')
        if($LASTEXITCODE){throw 'Linux server binary compilation failed.'}
    } finally {Pop-Location}
    foreach($kind in @('host','server')) {
        $nativePath=(& wsl -d $WslDistribution --exec /usr/bin/wslpath -a (Join-Path $output ($kind+'.test'))).Trim()
        if($LASTEXITCODE -or -not $nativePath.StartsWith('/mnt/')){throw 'Explicit Linux artifact path unavailable.'}
        & wsl -d $WslDistribution --exec $nativePath '-test.v=true' '-test.timeout=45s' *> (Join-Path $output ($kind+'-tests.log'))
        if($LASTEXITCODE){throw 'Actual Linux server validation failed; inspect owned test logs.'}
        $log=Get-Content -LiteralPath (Join-Path $output ($kind+'-tests.log')) -Raw
        if($log -match '(?m)^--- SKIP:' -or $log -notmatch '(?m)^PASS\s*$'){throw 'Linux host evidence contains skipped or missing tests.'}
    }
    $hostTests=@(Select-String -LiteralPath (Join-Path $output 'host-tests.log') -Pattern '^--- PASS:').Count
    $serverTests=@(Select-String -LiteralPath (Join-Path $output 'server-tests.log') -Pattern '^--- PASS:').Count
    $summary=[ordered]@{passed=$true;hostPlatform='linux/amd64';localWsl=$true;hostTests=$hostTests;serverTests=$serverTests;actualPersistentAuthority=$true;actualControlMtls=$true;unpairedDeviceRefused=$true;rawLegacyBackendRefused=$true;storageChangeStopsListeners=$true;missingWitnessNotRecreated=$true;cloudAccessed=$false;installedRuntimeChanged=$false;sourceCommit=$proof.sourceCommit;patchSha256=$proof.patchSha256;serverSha256=(Get-FileHash -LiteralPath (Join-Path $output 'linkgate-server-linux-amd64') -Algorithm SHA256).Hash}
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding utf8
    @{outputDirectory=$output;verification=$summary}|ConvertTo-Json -Depth 8
} finally {foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name],'Process')}}
