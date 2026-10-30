#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
Push-Location $repositoryRoot
$smokeRoot = $null
try {
    foreach ($project in @('gateway/windows/LinkGate.Agent.Console', 'gateway/windows/LinkGate.Owner.Console', 'gateway/examples/secure-direct/SecureApp', 'gateway/examples/secure-direct/OwnerHarness')) {
        & dotnet build $project -c Release
        if ($LASTEXITCODE) { throw "Build failed: $project" }
    }
    $agent = (Resolve-Path gateway/windows/LinkGate.Agent.Console/bin/Release/net10.0-windows/LinkGate.Agent.Console.exe).Path
    $owner = (Resolve-Path gateway/windows/LinkGate.Owner.Console/bin/Release/net10.0-windows/LinkGate.Owner.Console.exe).Path
    $appDirectory = (Resolve-Path gateway/examples/secure-direct/SecureApp/bin/Release/net10.0-windows).Path
    $app = Join-Path $appDirectory 'SecureApp.exe'
    $harness = (Resolve-Path gateway/examples/secure-direct/OwnerHarness/bin/Release/net10.0-windows/OwnerHarness.exe).Path
    $unexpected = Get-ChildItem -LiteralPath $appDirectory -File -Recurse | Where-Object { $_.Name -match '(?i)sqlite|frpc|LinkGate\.Broker|device-key|owner-key' }
    $deps = Get-Content -LiteralPath (Join-Path $appDirectory 'SecureApp.deps.json') -Raw
    if ($unexpected -or $deps -match '(?i)Sqlite|LinkGate.Broker') { throw 'Consumer contains authority dependencies.' }
    $smokeRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot ('artifacts/secure-owner-cli/' + [Guid]::NewGuid().ToString('N'))))
    [IO.Directory]::CreateDirectory($smokeRoot) | Out-Null
    function Invoke-Owner([string[]] $Arguments) {
        $reply = & $owner @Arguments
        if ($LASTEXITCODE) { throw 'Owner command failed.' }
        return ($reply | ConvertFrom-Json)
    }
    Invoke-Owner @('owner-create', (Join-Path $smokeRoot 'owner')) | Out-Null
    $devices = @{}
    foreach ($side in @('a', 'b')) {
        $identity = Join-Path $smokeRoot $side
        Invoke-Owner @('device-request', $identity) | Out-Null
        $certificate = Join-Path $smokeRoot ($side + '.pem')
        Invoke-Owner @('issue', (Join-Path $smokeRoot 'owner'), (Join-Path $identity 'device-request.pem'), $certificate) | Out-Null
        $devices[$side] = Invoke-Owner @('device-install', $identity, $certificate, (Join-Path $smokeRoot 'owner/owner-root.pem'))
    }
    $state = Join-Path $smokeRoot 'state'
    $operation = [Guid]::NewGuid().ToString()
    $trusted = Invoke-Owner @('trust', $state, (Join-Path $smokeRoot 'a'), (Join-Path $smokeRoot 'b.pem'), '0', '127.0.0.1', '17443', $devices['b'].deviceId, $operation)
    $retained = Invoke-Owner @('operation-status', $state, $operation)
    if ($trusted.Reference -ne $devices['b'].deviceId -or $trusted.Revision -ne 1 -or $retained.Reference -ne $trusted.Reference) { throw 'Owner enrollment or retained outcome failed.' }
    $revoked = Invoke-Owner @('peer-revoke', $state, $trusted.Reference, '1', [Guid]::NewGuid().ToString())
    if ($revoked.Active -or $revoked.Revision -ne 2) { throw 'Owner revocation failed.' }
    $result = & $harness $agent $app
    if ($LASTEXITCODE) { throw 'Independent application acceptance failed.' }
    $summary = $result | ConvertFrom-Json
    if (!$summary.passed -or $summary.fileBytes -ne 67108864 -or $summary.activeRevokeMs -ge 5000 -or !$summary.unrelatedServiceWorks) { throw 'Incomplete acceptance evidence.' }
    $summary | Add-Member -NotePropertyName ownerCliEnrollment -NotePropertyValue $true
    $output = Join-Path $repositoryRoot 'artifacts/linkgate-secure-direct-summary.json'
    $json = $summary | ConvertTo-Json
    [IO.File]::WriteAllText($output, $json)
    $json
} finally {
    if ($smokeRoot -and (Test-Path -LiteralPath $smokeRoot)) {
        $allowed = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts/secure-owner-cli')) + [IO.Path]::DirectorySeparatorChar
        $resolved = [IO.Path]::GetFullPath($smokeRoot)
        if (!$resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup target.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    Pop-Location
}
