param([string]$PythonRuntimeSource,[switch]$ReuseRuntime,[string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$researchRoot = Split-Path -Parent $PSScriptRoot
$researchOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $researchRoot 'outputs/SSVEP-Studio-Research' }
$researchDotnet = Join-Path $researchRoot '.tools/dotnet/dotnet.exe'
if (!(Test-Path -LiteralPath $researchDotnet)) { $researchDotnet = 'dotnet' }
if (!$PythonRuntimeSource) { $PythonRuntimeSource = Join-Path $researchRoot 'outputs/SSVEP-Studio/runtime/python' }
$PythonRuntimeSource = [IO.Path]::GetFullPath($PythonRuntimeSource)
if (!(Test-Path -LiteralPath (Join-Path $PythonRuntimeSource 'python.exe'))) {
    throw 'Provide -PythonRuntimeSource pointing to the existing Studio SDK Python runtime.'
}
& $researchDotnet publish (Join-Path $researchRoot 'src/Ssvep.Studio.App/Ssvep.Studio.App.csproj') -c Release -r win-x64 --self-contained true -o $researchOutput -m:1
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$researchPython = Join-Path $researchOutput 'runtime/python'
New-Item -ItemType Directory -Path $researchPython -Force | Out-Null
if ($ReuseRuntime) {
    if (!(Test-Path -LiteralPath (Join-Path $researchPython 'python.exe'))) { throw 'No existing packaged runtime to reuse.' }
} else {
    Get-ChildItem -LiteralPath $PythonRuntimeSource -Force | Copy-Item -Destination $researchPython -Recurse -Force
}
$researchLicenses = Join-Path $researchOutput 'Licenses'
New-Item -ItemType Directory -Path $researchLicenses -Force | Out-Null
$existingLicenses = Join-Path (Split-Path -Parent (Split-Path -Parent $PythonRuntimeSource)) 'Licenses'
if (Test-Path -LiteralPath $existingLicenses) { Get-ChildItem -LiteralPath $existingLicenses -File | Copy-Item -Destination $researchLicenses -Force }
Get-ChildItem -LiteralPath (Join-Path $researchRoot 'third-party') -File | Copy-Item -Destination $researchLicenses -Force
$researchTools = Join-Path $researchOutput 'tools'
New-Item -ItemType Directory -Path $researchTools -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'verify_brainvision.py'),(Join-Path $PSScriptRoot 'requirements-research-qa.txt') -Destination $researchTools -Force
Copy-Item -LiteralPath (Join-Path $researchRoot 'docs/SSVEP参数研发版使用说明.md') -Destination (Join-Path $researchOutput '使用说明.md') -Force
$researchLaunch = "@echo off`r`nsetlocal`r`nstart `"`" /d `"%~dp0`" `"%~dp0Ssvep.Studio.exe`" --research`r`n"
[IO.File]::WriteAllText((Join-Path $researchOutput '启动参数研发版.cmd'),$researchLaunch,[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $researchOutput '版本说明.txt'),"SSVEP Studio 0.3.3 参数研发版`r`n校准目标试次首次有效反馈保持到本轮结束，后续判定不换框；逐窗计算与原始记录继续。`r`n个人校准：目标 1→6，共四组；1 秒提示框、4 秒实际反馈、0.5 秒等待。`r`n工程测试不代表已验证真人识别率或物理刺激锁时。`r`n",[Text.UTF8Encoding]::new($false))
$researchFiles = Get-ChildItem -LiteralPath $researchOutput -File -Recurse | Where-Object Name -ne 'package-sha256.json' | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($researchOutput,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$researchFiles | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $researchOutput 'package-sha256.json') -Encoding utf8
Write-Output "Published: $researchOutput"
