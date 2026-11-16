param()

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$godot = $env:SANGUO_GODOT
if (-not $godot) {
    $local = Get-ChildItem -LiteralPath (Join-Path $root '.tools') -Filter '*mono_win64_console.exe' -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($local) { $godot = $local.FullName }
    else { $godot = (Get-Command godot -ErrorAction Stop).Source }
}
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:MSBUILDDISABLENODEREUSE = '1'
if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $root '.tools\dotnet-home' }
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $root '.nuget\packages' }
$evidence = Join-Path $root 'build\client-battle'
$null = New-Item -ItemType Directory -Path $evidence -Force
$env:APPDATA = Join-Path $root 'build\appdata'
$env:LOCALAPPDATA = Join-Path $root 'build\localappdata'
$null = New-Item -ItemType Directory -Path $env:APPDATA, $env:LOCALAPPDATA -Force

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $Executable $($Arguments -join ' ')" }
}
function Invoke-Smoke([string]$Name, [string[]]$UserArguments, [string]$Marker) {
    $log = Join-Path $evidence "$Name.log"
    Invoke-Checked $godot (@('--headless', '--path', 'client', '--log-file', $log, '--') + $UserArguments)
    $content = [IO.File]::ReadAllText($log)
    if ($content -notmatch [regex]::Escape($Marker)) { throw "Missing success marker in $log" }
    # This host cannot read the Windows system CA store; retain the engine error as evidence.
    $otherErrors = @($content -split "`n" | Where-Object { $_ -match '^ERROR:' -and $_ -notmatch '^ERROR: Failed to read the root certificate store\.' })
    if ($otherErrors.Count -gt 0) { throw "Unexpected engine errors: $($otherErrors -join '; ')" }
    if ($content -match 'Failed to read the root certificate store') {
        Write-Warning "System CA store unavailable; client regression only, not G0 acceptance. See $log"
    }
}
function Get-FileDigest([string]$Path) {
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hasher.ComputeHash([IO.File]::ReadAllBytes($Path))) }
    finally { $hasher.Dispose() }
}

Push-Location $root
try {
    $options = @('--locked-mode', '--disable-parallel', '-m:1')
    $feed = Join-Path $root '.nuget\feed'
    if (Test-Path -LiteralPath $feed) { $options += @('--source', $feed) }
    Invoke-Checked $dotnet (@('restore', 'client/Sanguo.Client.csproj') + $options)
    Invoke-Checked $dotnet @('build', 'client/Sanguo.Client.csproj', '--no-restore', '-m:1', '/p:UseSharedCompilation=false')
    Invoke-Smoke 'write' @('--battle-smoke', 'write', $evidence) 'BATTLE_CLIENT_SMOKE_OK'
    Invoke-Smoke 'resume' @('--battle-smoke', 'resume', $evidence) 'BATTLE_CLIENT_RESUME_OK'
    Invoke-Smoke 'layout' @('--battle-layout-smoke') 'BATTLE_LAYOUT_SMOKE_OK layouts=32'
    Invoke-Smoke 'artwork' @('--battle-artwork-smoke') 'BATTLE_ARTWORK_SMOKE_OK cards=8 portraits=2'
    Invoke-Smoke 'processing' @('--battle-processing-smoke') 'BATTLE_PROCESSING_SMOKE_OK'
    if ((Get-FileDigest (Join-Path $evidence 'final.json')) -cne
        (Get-FileDigest (Join-Path $evidence 'resumed-final.json'))) {
        throw 'Cross-process client battle result differs.'
    }
    Invoke-Smoke 'g0' @('--smoke') 'G0_SMOKE_OK 066d2413bb3ac61446d293248828c47eb1e704b0e930dea0e1775b28f1aaa883'
    Write-Host 'Client button flow, AI, cross-process save/resume and G0 routing passed.'
}
finally { Pop-Location }
