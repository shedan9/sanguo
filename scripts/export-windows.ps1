param()

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& (Join-Path $PSScriptRoot 'check.ps1')
if ($LASTEXITCODE -ne 0) { throw '开发检查失败，取消导出。' }
if (Test-Path -LiteralPath (Join-Path $root '.tools\dotnet\dotnet.exe') -PathType Leaf) {
    $env:DOTNET_ROOT = Join-Path $root '.tools\dotnet'
    $env:PATH = $env:DOTNET_ROOT + [IO.Path]::PathSeparator + $env:PATH
}
$env:DOTNET_CLI_HOME = Join-Path $root '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.nuget\packages'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:UseSharedCompilation = 'false'
$env:BuildInParallel = 'false'
$offlineFeed = Join-Path $root '.nuget\feed'
if (Test-Path -LiteralPath $offlineFeed -PathType Container) {
    $env:RestoreSources = $offlineFeed
}
# Keep Godot's project user data inside the ignored build directory.
$env:APPDATA = Join-Path $root 'build\appdata'
$env:LOCALAPPDATA = Join-Path $root 'build\localappdata'
$null = New-Item -ItemType Directory -Path $env:APPDATA, $env:LOCALAPPDATA -Force

$godot = $env:SANGUO_GODOT
if ([string]::IsNullOrWhiteSpace($godot)) {
    foreach ($candidate in @(
        (Join-Path $root '.tools\Godot_v4.7.2-stable_mono_win64.exe'),
        (Join-Path $root '.tools\Godot_mono\Godot_v4.7.2-stable_mono_win64.exe')
    )) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $godot = $candidate; break }
    }
}
if ([string]::IsNullOrWhiteSpace($godot)) {
    $command = Get-Command godot.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) { $godot = $command.Source }
}
if ([string]::IsNullOrWhiteSpace($godot) -or -not (Test-Path -LiteralPath $godot -PathType Leaf)) {
    throw '未找到Godot 4.7.2 .NET版。请设置SANGUO_GODOT为编辑器可执行文件的绝对路径。'
}

$output = Join-Path $root 'build\windows'
$evidence = Join-Path $root 'build\evidence\g0'
$null = New-Item -ItemType Directory -Path $output, $evidence -Force
$exportLog = Join-Path $evidence 'windows-export.log'
$errorLog = Join-Path $evidence 'windows-export-stderr.log'
$exportExecutable = Join-Path $output 'Sanguo.exe'
$arguments = @('--headless', '--path', 'client', '--export-release', '"Windows Desktop"', ('"' + $exportExecutable + '"'))
$process = Start-Process -FilePath $godot -ArgumentList $arguments -WorkingDirectory $root -PassThru -RedirectStandardOutput $exportLog -RedirectStandardError $errorLog
try {
    if (-not $process.WaitForExit(600000)) {
        $process.Kill()
        throw 'Godot导出超过10分钟，已结束本次编辑器进程。'
    }
    $process.Refresh()
    $exportExitCode = $process.ExitCode
}
finally { $process.Dispose() }
Get-Content -LiteralPath $errorLog -Encoding UTF8 | Add-Content -LiteralPath $exportLog -Encoding UTF8
if ($exportExitCode -ne 0) { throw "Godot导出失败，退出码：$exportExitCode；日志：$exportLog" }

$pythonCommand = Get-Command python.exe -ErrorAction SilentlyContinue
if ($null -eq $pythonCommand) { throw '未找到Python 3，无法核验和打包导出。' }
& $pythonCommand.Source (Join-Path $PSScriptRoot 'package-windows.py')
if ($LASTEXITCODE -ne 0) { throw 'Windows验证包核验失败。' }
