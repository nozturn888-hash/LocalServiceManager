param([ValidateSet('repro','all')][string]$Mode = 'all')
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$refs = @('/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Management.dll', '/r:System.Web.Extensions.dll', '/r:System.Core.dll')
$run = Join-Path $PSScriptRoot ('runs\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + $Mode)
$sourceCopy = Join-Path $run 'compiled-source'
New-Item -ItemType Directory -Path $sourceCopy -Force | Out-Null
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $project 'src') -File | Where-Object { $_.Extension -eq '.cs' -or $_.Name -eq 'app.manifest' }) {
    [IO.File]::WriteAllBytes((Join-Path $sourceCopy $file.Name), [IO.File]::ReadAllBytes($file.FullName))
}
$sources = @(Get-ChildItem -LiteralPath $sourceCopy -Filter '*.cs' | ForEach-Object FullName)
$exe = Join-Path $run 'LayoutTests.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output /debug:pdbonly /main:LocalServiceManager.LayoutTests ("/win32manifest:" + (Join-Path $sourceCopy 'app.manifest')) ("/out:" + $exe) @refs @sources (Join-Path $PSScriptRoot 'LayoutTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Layout test build failed.' }
& $exe $run $Mode
$result = $LASTEXITCODE
Write-Output $run
if ($result -eq 1 -and $Mode -eq 'repro') { Write-Output 'Reproduction detected geometry failure; see results.txt and screenshots.' }
elseif ($result -ne 0) { throw ('Layout checks failed. See ' + (Join-Path $run 'results.txt')) }
