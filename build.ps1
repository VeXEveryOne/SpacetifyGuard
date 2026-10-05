param([switch]$Test)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8 is required. See README.' }
$gacMsil = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL'
$gac64 = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_64'
$refs = @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Windows.Forms.dll','System.Drawing.dll')
foreach ($assembly in @('WindowsBase','PresentationFramework','System.Xaml')) {
    $refs += (Get-ChildItem -LiteralPath (Join-Path $gacMsil $assembly) -Filter "$assembly.dll" -Recurse | Select-Object -First 1 -ExpandProperty FullName)
}
$refs += (Get-ChildItem -LiteralPath (Join-Path $gac64 'PresentationCore') -Filter 'PresentationCore.dll' -Recurse | Select-Object -First 1 -ExpandProperty FullName)
$outDir = Join-Path $taskRoot 'dist\SpicetifyGuard'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$sources = Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName
$referenceArgs = $refs | ForEach-Object {"/reference:$_"}
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /main:SpicetifyGuard.Program "/out:$outDir\SpicetifyGuard.exe" @referenceArgs @sources
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'themes') -Destination $outDir -Recurse -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'scripts') -Destination $outDir -Recurse -Force
foreach ($file in @('README.md','LICENSE','THIRD-PARTY-NOTICES.md','Install.cmd')) {
    Copy-Item -LiteralPath (Join-Path $taskRoot $file) -Destination $outDir -Force
}
if ($Test) {
    $testExe = Join-Path $outDir 'GuardTests.exe'
    & $compiler /nologo /target:exe /platform:anycpu /main:SpicetifyGuard.Tests "/out:$testExe" @referenceArgs @sources (Join-Path $taskRoot 'tests\Tests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    Remove-Item -LiteralPath $testExe
}
$zipPath = Join-Path $taskRoot 'dist\SpicetifyGuard-Studio-win-x64.zip'
Compress-Archive -Path "$outDir\*" -DestinationPath $zipPath -Force
Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
