[CmdletBinding()]
param([Parameter(Mandatory)][string]$Compiler,[Parameter(Mandatory)][string]$BackendDirectory,[Parameter(Mandatory)][string]$AndroidApk,[Parameter(Mandatory)][string]$ServerBinary,[string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$versions=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'version.json') -Raw | ConvertFrom-Json
$apkMetadata=Join-Path (Split-Path -Parent $AndroidApk) 'output-metadata.json'
if(Test-Path -LiteralPath $apkMetadata){$apkBuild=Get-Content -LiteralPath $apkMetadata -Raw | ConvertFrom-Json;if($apkBuild.applicationId -ne 'com.nebula.linkgate.agent' -or $apkBuild.elements.Count -ne 1 -or $apkBuild.elements[0].versionName -ne $versions.androidVersion -or $apkBuild.elements[0].versionCode -ne $versions.androidVersionCode){throw 'APK build metadata does not match this release.'}}
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if(!$OutputDirectory){$OutputDirectory=Join-Path $repository ('artifacts/trial-release/'+[Guid]::NewGuid().ToString('N'))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a new output directory.'}
$payload=Join-Path $OutputDirectory 'payload'
New-Item -ItemType Directory -Force -Path $payload,(Join-Path $payload 'runtime'),(Join-Path $OutputDirectory 'server') | Out-Null
$verification=Get-Content -LiteralPath (Join-Path $BackendDirectory 'verification.json') -Raw | ConvertFrom-Json
$runtime=Join-Path $BackendDirectory 'frpc-authority.exe'
if(!$verification.passed -or $verification.sourceCommit -ne 'd8cf1800dc1abd7fda1828cab105d7a6b5a50eb1' -or (Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash -ne $verification.frpcSha256){throw 'Runtime verification differs.'}
foreach($item in @(@('gateway/windows/LinkGate.Agent.Console','agent'),@('gateway/windows/LinkGate.Owner.Console','owner'),@('gateway/windows/LinkGate.Review.Console','review'),@('gateway/release/sample','sample'))){
    & dotnet publish (Join-Path $repository $item[0]) -c Release -r win-x64 --self-contained true '-p:RuntimeFrameworkVersion=10.0.5' '-p:Version=0.3.0-preview.2' -o (Join-Path $payload $item[1]) --nologo -v:q
    if($LASTEXITCODE){throw ('Publish failed: '+$item[1])}
}
Copy-Item -LiteralPath $runtime -Destination (Join-Path $payload 'runtime/frpc-authority.exe')
if((Get-FileHash -LiteralPath (Join-Path $payload 'runtime/frpc-authority.exe') -Algorithm SHA256).Hash -ne $verification.frpcSha256){throw 'Packaged runtime unavailable or changed.'}
@{sha256=$verification.frpcSha256;sourceCommit=$verification.sourceCommit;patchSha256=$verification.patchSha256}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $payload 'runtime.lock.json') -Encoding UTF8
foreach($name in @('Start-LinkGate.cmd','Launch-LinkGate.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $payload}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README-template.md') -Destination (Join-Path $payload 'QUICKSTART.md')
Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination (Join-Path $payload 'LICENSE-source.txt')
Copy-Item -LiteralPath (Join-Path $BackendDirectory 'LICENSE') -Destination (Join-Path $payload 'LICENSE-frp.txt')
Copy-Item -LiteralPath $AndroidApk -Destination (Join-Path $OutputDirectory ('LinkGate-'+$versions.androidVersion+'-Android.apk'))
if((Get-FileHash -LiteralPath $AndroidApk -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $OutputDirectory ('LinkGate-'+$versions.androidVersion+'-Android.apk')) -Algorithm SHA256).Hash){throw 'Packaged APK differs.'}
Copy-Item -LiteralPath $ServerBinary -Destination (Join-Path $OutputDirectory 'server/linkgate-server')
foreach($name in @('server.json.example','linkgate.service')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $OutputDirectory 'server')}
& $Compiler ('/DPayload='+$payload) ('/DOutDir='+$OutputDirectory) (Join-Path $PSScriptRoot 'LinkGate.iss')
if($LASTEXITCODE){throw 'Installer build failed.'}
$setup=Join-Path $OutputDirectory 'LinkGate-0.3.0-preview.2-Windows-x64-Setup.exe'
if(!(Test-Path -LiteralPath $setup)){throw 'Setup missing.'}
Copy-Item -LiteralPath (Join-Path $payload 'QUICKSTART.md') -Destination (Join-Path $OutputDirectory 'QUICKSTART.md')
Copy-Item -LiteralPath (Join-Path $payload 'QUICKSTART.md') -Destination (Join-Path $OutputDirectory 'server/QUICKSTART.md')
Copy-Item -LiteralPath (Join-Path $payload 'LICENSE-source.txt') -Destination (Join-Path $OutputDirectory 'server/LICENSE-source.txt')
Copy-Item -LiteralPath (Join-Path $payload 'LICENSE-frp.txt') -Destination (Join-Path $OutputDirectory 'server/LICENSE-frp.txt')
Compress-Archive -Path (Join-Path $OutputDirectory 'server/*') -DestinationPath (Join-Path $OutputDirectory 'LinkGate-0.3.0-preview.2-Linux-amd64-Server.zip') -CompressionLevel Optimal
$hashes=[ordered]@{}
foreach($file in Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse){$relative=[IO.Path]::GetRelativePath($OutputDirectory,$file.FullName).Replace('\','/');$hashes[$relative]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
@{version=$versions.windowsVersion;androidVersion=$versions.androidVersion;androidVersionCode=$versions.androidVersionCode;builtUtc=[DateTime]::UtcNow.ToString('O');installedBaselineChanged=$false;files=$hashes}|ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'manifest.json') -Encoding UTF8
Write-Output $OutputDirectory
