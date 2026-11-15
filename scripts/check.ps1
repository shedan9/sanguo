param()

# Windows counterpart of check.sh. Run with Windows PowerShell 5.1 or newer.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($null -eq $command) { throw '未找到.NET SDK。请安装global.json指定版本，或解压到.tools/dotnet。' }
    $dotnet = $command.Source
}
$pythonCommand = Get-Command python.exe -ErrorAction SilentlyContinue
if ($null -eq $pythonCommand) { throw '未找到Python 3。' }
$python = $pythonCommand.Source

$requiredSdk = (Get-Content -LiteralPath (Join-Path $root 'global.json') -Raw -Encoding UTF8 | ConvertFrom-Json).sdk.version
$sdkRoot = Join-Path (Split-Path -Parent $dotnet) 'sdk'
$installedSdks = @(Get-ChildItem -LiteralPath $sdkRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
if ($requiredSdk -notin $installedSdks) {
    throw "需要.NET SDK $requiredSdk；当前dotnet目录已安装：$($installedSdks -join ', ')"
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:MSBUILDDISABLENODEREUSE = '1'
if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $root '.tools\dotnet-home' }
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $root '.nuget\packages' }
$env:PYTHONDONTWRITEBYTECODE = '1'
# Keep generated fixtures in the writable checkout on restricted Windows hosts.
$testTemp = Join-Path $root 'build\test-temp'
$null = New-Item -ItemType Directory -Path $testTemp -Force
$env:TEMP = $testTemp
$env:TMP = $testTemp
$env:VSTEST_ARTIFACTS_DIRECTORY = Join-Path $root 'build\test-results'
$null = New-Item -ItemType Directory -Path $env:VSTEST_ARTIFACTS_DIRECTORY -Force

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable 执行失败，退出码：$LASTEXITCODE；参数：$($Arguments -join ' ')" }
}

function Assert-SameFile([string]$Left, [string]$Right) {
    if (-not (Test-Path -LiteralPath $Left -PathType Leaf) -or -not (Test-Path -LiteralPath $Right -PathType Leaf)) {
        throw "缺少恢复结果：$Left 或 $Right"
    }
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        $first = [BitConverter]::ToString($hasher.ComputeHash([IO.File]::ReadAllBytes($Left)))
        $second = [BitConverter]::ToString($hasher.ComputeHash([IO.File]::ReadAllBytes($Right)))
    }
    finally { $hasher.Dispose() }
    if ($first -cne $second) { throw "跨进程恢复结果不一致：$Left 与 $Right" }
}

Push-Location $root
try {
    Invoke-Checked $python @('-B', '-m', 'unittest', 'discover', '-s', 'tests/scripts', '-p', 'test_*.py')
    Invoke-Checked $python @('-B', 'scripts/collect-ol-sources.py', 'content/evidence/ol-public-20261112')
    $projects = @(
        'tests/Sanguo.Integration.Tests/Sanguo.Integration.Tests.csproj',
        'tools/Sanguo.ContentValidator/Sanguo.ContentValidator.csproj',
        'tools/Sanguo.Simulator/Sanguo.Simulator.csproj'
    )
    $restoreOptions = @('--locked-mode', '--disable-parallel', '-m:1')
    $offlineFeed = Join-Path $root '.nuget\feed'
    if (Test-Path -LiteralPath $offlineFeed -PathType Container) {
        $restoreOptions += @('--source', $offlineFeed)
    }
    foreach ($project in $projects) {
        Invoke-Checked $dotnet (@('restore', $project) + $restoreOptions)
    }
    Invoke-Checked $dotnet @('test', $projects[0], '--no-restore', '-m:1', '/p:UseSharedCompilation=false')
    foreach ($project in $projects[1..2]) {
        Invoke-Checked $dotnet @('build', $project, '--no-restore', '-m:1', '/p:UseSharedCompilation=false')
    }
    Invoke-Checked $dotnet @('run', '--project', 'tools/Sanguo.ContentValidator', '--no-build', '--no-restore', '--', 'content')
    Invoke-Checked $dotnet @('run', '--project', 'tools/Sanguo.Simulator', '--no-build', '--no-restore', '--', 'content', '42')

    $scenarios = @(
        @('battle-basic', 'battle-resume', 'basic-battle', 'checkpoint.json', 'resumed'),
        @('battle-tricks', 'battle-tricks-resume', 'trick-battle', 'counter-chain.json', 'resumed-counter'),
        @('battle-tricks', 'battle-tricks-resume', 'trick-battle', 'duel-response.json', 'resumed-duel'),
        @('battle-hand-tricks', 'battle-hand-tricks-resume', 'hand-trick-battle', 'hand-choice.json', 'resumed-hand'),
        @('battle-skills', 'battle-skills-resume', 'skill-battle', 'skill-trigger.json', 'resumed-trigger'),
        @('battle-skills', 'battle-skills-resume', 'skill-battle', 'skill-slash.json', 'resumed-slash'),
        @('battle-ai', 'battle-ai-resume', 'ai-battle', 'ai-decision.json', 'resumed-ai')
    )
    $completed = @{}
    foreach ($scenario in $scenarios) {
        $start, $resume, $folder, $checkpoint, $resumeFolder = $scenario
        if (-not $completed.ContainsKey($folder)) {
            Invoke-Checked $dotnet @('run', '--project', 'tools/Sanguo.Simulator', '--no-build', '--no-restore', '--', "--$start", '42', "build/$folder")
            $completed[$folder] = $true
        }
        Invoke-Checked $dotnet @('run', '--project', 'tools/Sanguo.Simulator', '--no-build', '--no-restore', '--', "--$resume", "build/$folder/$checkpoint", "build/$folder/$resumeFolder")
        Assert-SameFile "build/$folder/final.json" "build/$folder/$resumeFolder/final.json"
    }
    Write-Host 'Windows完整开发检查通过。'
}
finally { Pop-Location }
