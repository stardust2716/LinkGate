param(
    [Parameter(Mandatory)][string]$Go,
    [Parameter(Mandatory)][string]$Source,
    [string]$Python='python'
)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$goExecutable=(Resolve-Path -LiteralPath $Go).Path
$sourceDirectory=(Resolve-Path -LiteralPath $Source).Path
$output=Join-Path $repository ('artifacts/linkgate-backend-frp/verified-'+[Guid]::NewGuid().ToString('N'))
$previousToolchain=$env:GOTOOLCHAIN
try {
    $env:GOTOOLCHAIN='local'
    & $Python (Join-Path $PSScriptRoot 'prepare.py') --source $sourceDirectory --output $output --go $goExecutable
    if($LASTEXITCODE){throw 'Pinned backend preparation failed.'}
    Push-Location $output
    try {
        & $goExecutable mod tidy *> (Join-Path $output 'dependencies.log')
        if($LASTEXITCODE){throw 'Pinned backend dependency verification failed.'}
        & $goExecutable mod verify *> (Join-Path $output 'module-verification.log')
        if($LASTEXITCODE){throw 'Backend module checksum verification failed.'}
        & $goExecutable test -count=1 -json ./client/... ./server/... ./pkg/nathole/... ./cmd/linkgate-server *> (Join-Path $output 'tests.jsonl')
        if($LASTEXITCODE){throw 'Authenticated backend regression failed.'}
        & $goExecutable vet ./client/... ./server/... ./pkg/nathole/... ./cmd/linkgate-server *> (Join-Path $output 'vet.log')
        if($LASTEXITCODE){throw 'Authenticated backend vet failed.'}
        & $goExecutable build -trimpath -buildvcs=false -tags frpc,noweb -o (Join-Path $output 'frpc-authority.exe') ./cmd/frpc
        if($LASTEXITCODE){throw 'Owned frpc build failed.'}
        & $goExecutable build -trimpath -buildvcs=false -tags noweb -o (Join-Path $output 'authority-fixture.exe') ./cmd/linkgate-authority-fixture
        if($LASTEXITCODE){throw 'Local authority fixture build failed.'}
    } finally {Pop-Location}
    $passed=0;$linkgatePassed=0
    foreach($line in Get-Content -LiteralPath (Join-Path $output 'tests.jsonl')) {
        try{$entry=$line|ConvertFrom-Json}catch{continue}
        if($entry.Action -eq 'pass' -and $entry.Test -and -not $entry.Test.Contains('/')){$passed++;if($entry.Test.StartsWith('TestLinkGate')){$linkgatePassed++}}
    }
    $summary=[ordered]@{passed=$true;sourceCommit='d8cf1800dc1abd7fda1828cab105d7a6b5a50eb1';sourceCopy=$output;topLevelTests=$passed;linkgateTopLevelTests=$linkgatePassed;vet=$true;moduleChecksums=$true;go=(& $goExecutable version);patchSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'authority.patch') -Algorithm SHA256).Hash;moduleSha256=(Get-FileHash -LiteralPath (Join-Path $output 'go.mod') -Algorithm SHA256).Hash;sumSha256=(Get-FileHash -LiteralPath (Join-Path $output 'go.sum') -Algorithm SHA256).Hash;frpcSha256=(Get-FileHash -LiteralPath (Join-Path $output 'frpc-authority.exe') -Algorithm SHA256).Hash;fixtureSha256=(Get-FileHash -LiteralPath (Join-Path $output 'authority-fixture.exe') -Algorithm SHA256).Hash;physicalNatTested=$false;installedRuntimeChanged=$false}
    $summary|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding utf8
    $summary|ConvertTo-Json -Depth 4
} finally {$env:GOTOOLCHAIN=$previousToolchain}
