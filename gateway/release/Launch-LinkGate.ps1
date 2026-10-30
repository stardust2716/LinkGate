$ErrorActionPreference='Stop'
$state=Join-Path $env:LOCALAPPDATA 'LinkGateTrial/state'
$identity=Join-Path $env:LOCALAPPDATA 'LinkGateTrial/identity'
$config=Join-Path $env:LOCALAPPDATA 'LinkGateTrial/server.json'
Write-Host 'LinkGate 0.3.0-preview.2'
Write-Host 'First configure identity, server authorization and local app approval using QUICKSTART.md.'
if(!(Test-Path -LiteralPath (Join-Path $identity 'device-cert.pem')) -or !(Test-Path -LiteralPath $config)){Start-Process -FilePath 'notepad.exe' -ArgumentList ('"'+(Join-Path $PSScriptRoot 'QUICKSTART.md')+'"');throw 'Identity/server.json not configured.'}
$keyFile=Join-Path $env:LOCALAPPDATA 'LinkGateTrial/server-key.txt'
if(!(Test-Path -LiteralPath $keyFile)){throw 'Save the independently confirmed server SPKI in server-key.txt.'}
$key=(Get-Content -LiteralPath $keyFile -Raw).Trim()
$manifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'runtime.lock.json') -Raw | ConvertFrom-Json
$runtime=Join-Path $PSScriptRoot 'runtime/frpc-authority.exe'
if((Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash -ne $manifest.sha256){throw 'Runtime digest differs.'}
Write-Host 'Enter stop or press Ctrl+C to stop. User identity/state are preserved on uninstall.'
& (Join-Path $PSScriptRoot 'agent/LinkGate.Agent.Console.exe') serve $state $identity $config $key $runtime $manifest.sha256 '0.0.0.0' '0'
if($LASTEXITCODE -ne 0){throw 'Agent exited with an error.'}
