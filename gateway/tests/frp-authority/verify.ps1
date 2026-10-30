param(
    [Parameter(Mandatory)][string]$Go,
    [Parameter(Mandatory)][string]$Source,
    [string]$Python='python'
)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$log=Join-Path $repository 'artifacts/frp-authority-build.json'
& (Join-Path $repository 'gateway/backend-frp/verify.ps1') -Go $Go -Source $Source -Python $Python | Tee-Object -FilePath $log
if(-not $?){throw 'Source-built backend verification failed.'}
# Read structured metadata rather than guessing the most recently modified copy.
$lines=Get-Content -LiteralPath $log
$opening=[Array]::IndexOf($lines,'{')
if($opening -lt 0){throw 'Backend build metadata missing.'}
$build=($lines[$opening..($lines.Length-1)] -join "`n")|ConvertFrom-Json
if(-not $build.passed){throw 'Backend verification did not pass.'}
$fixture=Join-Path $build.sourceCopy 'authority-fixture.exe'
$client=Join-Path $build.sourceCopy 'frpc-authority.exe'
& dotnet run --project (Join-Path $PSScriptRoot 'dotnet') -c Release -- $fixture $client
if($LASTEXITCODE){throw 'Actual owned-runtime business verification failed.'}
