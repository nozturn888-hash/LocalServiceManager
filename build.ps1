param([switch]$Tests)
$ErrorActionPreference='Stop'
$project=$PSScriptRoot
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(!(Test-Path -LiteralPath $compiler)){throw 'Windows x64 .NET Framework compiler is required.'}
$assets=Join-Path $project 'assets'
if(!(Test-Path -LiteralPath (Join-Path $assets 'application.ico'))){& (Join-Path $project 'tools\GenerateIcon.ps1')}
$output=Join-Path $project 'dist\LocalServiceManager-v0.1.0-win-x64'
[IO.Directory]::CreateDirectory($output) | Out-Null
$refs=@('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Management.dll','/r:System.Web.Extensions.dll','/r:System.Core.dll')
$sources=@(Get-ChildItem -LiteralPath (Join-Path $project 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
$common=@("/win32manifest:$project\src\app.manifest","/win32icon:$assets\application.ico","/resource:$assets\application.ico,LocalServiceManager.Application.ico")
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output ("/out:$output\LocalServiceManager.exe") @common @refs @sources
if($LASTEXITCODE -ne 0){throw 'Application build failed.'}
foreach($name in @('README.md','README.en.md','LICENSE','NOTICE.md','RELEASE-NOTES.md')){
 Copy-Item -LiteralPath (Join-Path $project $name) -Destination (Join-Path $output $name) -Force
}
foreach($name in @('assets','scripts','config','icons','logs')){[IO.Directory]::CreateDirectory((Join-Path $output $name)) | Out-Null}
Copy-Item -LiteralPath (Join-Path $assets 'application.ico') -Destination (Join-Path $output 'assets\application.ico') -Force
Copy-Item -LiteralPath (Join-Path $assets 'application.png') -Destination (Join-Path $output 'assets\application.png') -Force
Copy-Item -LiteralPath (Join-Path $project 'scripts\README.md') -Destination (Join-Path $output 'scripts\README.md') -Force
Copy-Item -LiteralPath (Join-Path $project 'config\services.example.json') -Destination (Join-Path $output 'config\services.example.json') -Force
# Only these reviewed opt-in example files are shipped. No controller runs during build.
$exampleOut=Join-Path $output 'examples\hermes-gateway'
[IO.Directory]::CreateDirectory($exampleOut) | Out-Null
foreach($name in @('gateway-control.ps1','README.md')){
 Copy-Item -LiteralPath (Join-Path $project ('examples\hermes-gateway\' + $name)) -Destination (Join-Path $exampleOut $name) -Force
}
if($Tests){
 $testRoot=Join-Path $project 'tests'
 & $compiler /nologo /target:exe /platform:x64 /utf8output ("/out:$testRoot\ServiceFixture.exe") "$testRoot\ServiceFixture.cs"
 if($LASTEXITCODE -ne 0){throw 'Fixture build failed.'}
 & $compiler /nologo /target:exe /platform:x64 /utf8output /main:LocalServiceManager.Tests ("/out:$testRoot\Tests.exe") @common @refs @sources "$testRoot\Tests.cs"
 if($LASTEXITCODE -ne 0){throw 'Test build failed.'}
}
Get-Item -LiteralPath (Join-Path $output 'LocalServiceManager.exe') | Select-Object FullName,Length
