#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
Push-Location $repositoryRoot
try {
    & dotnet build gateway/windows/LinkGate.Broker.Console -c Release
    if ($LASTEXITCODE) { throw 'Broker build failed.' }
    & dotnet build gateway/examples/dotnet-registration -c Release
    if ($LASTEXITCODE) { throw 'Sample build failed.' }
    $broker = (Resolve-Path gateway/windows/LinkGate.Broker.Console/bin/Release/net10.0-windows/LinkGate.Broker.Console.exe).Path
    $sampleDirectory = (Resolve-Path gateway/examples/dotnet-registration/bin/Release/net10.0-windows).Path
    $sample = Join-Path $sampleDirectory 'Registration.Sample.exe'
    $unexpected = Get-ChildItem -LiteralPath $sampleDirectory -File -Recurse | Where-Object { $_.Name -match '(?i)sqlite|frpc|LinkGate\.Broker' }
    if ($unexpected) { throw 'Consumer contains server/runtime dependencies.' }
    $digest = (Get-FileHash -LiteralPath $broker -Algorithm SHA256).Hash
    $output = Join-Path $repositoryRoot ('artifacts/sdk-registration/' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $operation = [Guid]::NewGuid().ToString()
    [IO.File]::WriteAllText((Join-Path $output 'operation.txt'), $operation)
    $state = Join-Path $output 'private'
    $first = & $sample $broker $digest $state ./gateway/spec/examples/file-service.app.json $operation register
    if ($LASTEXITCODE) { throw 'Independent registration failed.' }
    $second = & $sample $broker $digest $state ./gateway/spec/examples/file-service.app.json $operation query
    if ($LASTEXITCODE) { throw 'Independent retry failed.' }
    $one = $first | ConvertFrom-Json
    $two = $second | ConvertFrom-Json
    if (!$one.registered.approvalRequired -or $one.registered.applicationRef -ne $two.operation.result.applicationRef -or $two.registered -ne $null -or $two.operation.status -ne 'succeeded' -or $one.operation.result.applicationRef -ne $one.registered.applicationRef) { throw 'Consumer result mismatch.' }
    $summary = [ordered]@{ passed=$true; independentConsumer=$true; restartQuery=$true; consumerHasServerDependencies=$false; operationId=$operation; applicationRef=$one.registered.applicationRef; approvalRequired=$true; businessNetworkTested=$false; installedRuntimeChanged=$false }
    $json = $summary | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $output 'summary.json'), $json)
    $json
} finally { Pop-Location }
