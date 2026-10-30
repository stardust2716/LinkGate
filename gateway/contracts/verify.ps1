#Requires -Version 5.1
[CmdletBinding()]
param([string]$JdkDirectory='', [string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$contractsRoot=Split-Path -Parent $PSCommandPath
$repositoryRoot=Split-Path -Parent (Split-Path -Parent $contractsRoot)
if(!$OutputDirectory){$OutputDirectory=Join-Path $repositoryRoot 'artifacts/linkgate-contracts'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null
if(!$JdkDirectory){$JdkDirectory=$env:JAVA_HOME}
if(!$JdkDirectory){
    $candidateRoot=Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'ATRI/external/android-toolchain/jdk'
    if(Test-Path -LiteralPath $candidateRoot){$candidate=Get-ChildItem -LiteralPath $candidateRoot -Directory|Where-Object Name -Like 'jdk-17*'|Select-Object -First 1;if($candidate){$JdkDirectory=$candidate.FullName}}
}
if(!$JdkDirectory -or !(Test-Path -LiteralPath (Join-Path $JdkDirectory 'bin/javac.exe'))){throw 'Supply -JdkDirectory with JDK17 or newer.'}
$vectors=Join-Path $contractsRoot 'vectors/cases.json'
$dotnetResult=Join-Path $OutputDirectory 'dotnet.json'
$javaResult=Join-Path $OutputDirectory 'java.json'
$classes=Join-Path $OutputDirectory 'java-classes'
[IO.Directory]::CreateDirectory($classes)|Out-Null
& dotnet run --project (Join-Path $contractsRoot 'dotnet/LinkGate.Contracts.Tests/LinkGate.Contracts.Tests.csproj') -c Release -- --vectors $vectors --output $dotnetResult
if($LASTEXITCODE){throw '.NET contract conformance failed.'}
$sources=@(Get-ChildItem -LiteralPath (Join-Path $contractsRoot 'java/src/main/java/dev/linkgate/contracts') -Filter '*.java' -File)+@(Get-ChildItem -LiteralPath (Join-Path $contractsRoot 'java/src/test/java/dev/linkgate/contracts') -Filter '*.java' -File)
& (Join-Path $JdkDirectory 'bin/javac.exe') -encoding UTF-8 -Xlint:all -Werror -d $classes @($sources.FullName)
if($LASTEXITCODE){throw 'Java contract compilation failed.'}
& (Join-Path $JdkDirectory 'bin/java.exe') -cp $classes dev.linkgate.contracts.ConformanceMain --vectors $vectors --output $javaResult
if($LASTEXITCODE){throw 'Java contract conformance failed.'}
& python (Join-Path $contractsRoot 'compare.py') --vectors $vectors --dotnet $dotnetResult --java $javaResult --output (Join-Path $OutputDirectory 'summary.json')
if($LASTEXITCODE){throw 'Independent contract results did not match.'}
