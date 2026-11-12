param([ValidateRange(10, 600)][int]$TimeoutSeconds = 90)

# Compatible with Windows PowerShell 5.1; no SDK or Python required.
$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$evidence = Join-Path ([IO.Path]::GetTempPath()) ('sanguo-g0-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $evidence
$report = [ordered]@{
    formatVersion = 1
    scope = 'g0-windows-acceptance'
    startedAtUtc = [DateTime]::UtcNow.ToString('o')
    automaticChecksPassed = $false
    g0Accepted = $false
    manualChecks = [ordered]@{ offlineLaunch = 'pending'; windowAndChinese = 'pending'; keyboardFocus = 'pending' }
    evidenceDirectory = $evidence
    checks = @()
    failure = $null
}

function Invoke-Probe([string]$Name, [string]$ProbeArguments, [string]$Marker) {
    $log = Join-Path $evidence ($Name + '.log')
    $arguments = '--headless --log-file "' + $log + '" -- ' + $ProbeArguments
    $process = Start-Process -FilePath (Join-Path $packageRoot 'Sanguo.exe') -ArgumentList $arguments -WorkingDirectory $packageRoot -PassThru
    try {
        $null = $process.Handle
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill()
            $process.WaitForExit()
            throw "$Name timed out after $TimeoutSeconds seconds."
        }
        # Refresh exit information for Windows PowerShell's Start-Process wrapper.
        $process.Refresh()
        if ($process.ExitCode -ne 0) { throw "$Name exited with code $($process.ExitCode). See $log" }
        if (-not (Test-Path -LiteralPath $log -PathType Leaf)) { throw "$Name did not produce a log." }
        $contents = [IO.File]::ReadAllText($log)
        if ($contents -match '(?m)^(ERROR:|SCRIPT ERROR:)') { throw "$Name logged an engine error. See $log" }
        $matchesFound = [regex]::Matches($contents, '(?m)^' + [regex]::Escape($Marker) + ' ([0-9a-f]{64})\r?$')
        if ($matchesFound.Count -ne 1) { throw "$Name did not produce exactly one expected result marker." }
        $hash = $matchesFound[0].Groups[1].Value
        $report.checks += [ordered]@{ name = $Name; passed = $true; exitCode = $process.ExitCode; stateHash = $hash; log = $log }
        return $hash
    }
    finally { $process.Dispose() }
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Run this script on Windows 11 x64.' }
    $os = Get-CimInstance Win32_OperatingSystem
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    $report.device = [ordered]@{ system = $os.Caption; version = $os.Version; build = $os.BuildNumber; architecture = $os.OSArchitecture; cpu = $cpu.Name; cpuArchitecture = $cpu.Architecture }
    if ([int]$os.BuildNumber -lt 22000 -or $cpu.Architecture -ne 9) { throw 'The G0 target is Windows 11 x64 on an x64 CPU.' }
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    $report.sdkInventory = @()
    if ($null -ne $dotnet) {
        $sdkOutput = @(& $dotnet.Source --list-sdks 2>&1)
        if ($LASTEXITCODE -ne 0) { throw 'Unable to determine installed .NET SDKs.' }
        $report.sdkInventory = @($sdkOutput | ForEach-Object { "$_" } | Where-Object { $_.Trim().Length -gt 0 })
    }
    $report.sdkAbsent = $report.sdkInventory.Count -eq 0
    $report.sdkDirectories = @()
    foreach ($location in @($env:DOTNET_ROOT, $env:DOTNET_ROOT_X64, (Join-Path $env:ProgramFiles 'dotnet'), (Join-Path ${env:ProgramFiles(x86)} 'dotnet'), (Join-Path $env:USERPROFILE '.dotnet'))) {
        if (-not [string]::IsNullOrWhiteSpace($location)) {
            $sdkPath = Join-Path $location 'sdk'
            if (Test-Path -LiteralPath $sdkPath -PathType Container) {
                $report.sdkDirectories += @(Get-ChildItem -LiteralPath $sdkPath -Directory | ForEach-Object { $_.FullName })
            }
        }
    }
    $report.sdkAbsent = $report.sdkAbsent -and $report.sdkDirectories.Count -eq 0

    $manifestPath = Join-Path $packageRoot 'package-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $report.packageManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($manifest.scope -ne 'g0-probe' -or $manifest.target -ne 'win-x64' -or @($manifest.files).Count -eq 0) { throw 'Invalid package manifest.' }
    $seen = @{}
    foreach ($entry in $manifest.files) {
        if ([string]::IsNullOrWhiteSpace($entry.path) -or $entry.path -match '(^/|\\|:|(^|/)\.\.?(/|$))' -or $entry.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid package file path or digest.' }
        if ($seen.ContainsKey($entry.path)) { throw "Duplicate package file: $($entry.path)" }
        $seen[$entry.path] = $true
        $path = Join-Path $packageRoot $entry.path
        $file = Get-Item -LiteralPath $path -Force
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Package links are not allowed: $($entry.path)" }
        $cursor = $file.Directory
        while ($cursor.FullName -ne $packageRoot) {
            if ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Package links are not allowed: $($entry.path)" }
            $cursor = $cursor.Parent
            if ($null -eq $cursor) { throw 'Package path escaped its directory.' }
        }
        if ($file.PSIsContainer -or $file.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.sha256) { throw "Package file mismatch: $($entry.path)" }
    }
    foreach ($required in @('Sanguo.exe', 'verify-windows.ps1', 'data_Sanguo.Client_windows_x86_64/Sanguo.Client.dll', 'data_Sanguo.Client_windows_x86_64/coreclr.dll', 'data_Sanguo.Client_windows_x86_64/hostfxr.dll')) {
        if (-not $seen.ContainsKey($required)) { throw "Manifest omits required file: $required" }
    }
    foreach ($file in Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Force) {
        $relative = $file.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
        if ($relative -ne 'package-manifest.json' -and -not $seen.ContainsKey($relative)) { throw "Unlisted package file: $relative" }
    }
    $report.checks += [ordered]@{ name = 'package-integrity'; passed = $true; files = $seen.Count }
    $smoke = Invoke-Probe 'smoke' '--smoke' 'G0_SMOKE_OK'
    if ($smoke -cne '066d2413bb3ac61446d293248828c47eb1e704b0e930dea0e1775b28f1aaa883') { throw 'The smoke state hash does not match the G0 baseline.' }
    $saveDirectory = Join-Path $evidence 'save'
    $writeHash = Invoke-Probe 'write' ('--acceptance-write "' + $saveDirectory + '"') 'G0_ACCEPTANCE_WRITE_OK'
    $resumeHash = Invoke-Probe 'resume' ('--acceptance-resume "' + $saveDirectory + '"') 'G0_ACCEPTANCE_RESUME_OK'
    if ($writeHash -eq $resumeHash) { throw 'Resumed probe did not advance the state.' }
    if (-not $report.sdkAbsent) { throw 'Probes passed, but this device has a development SDK. Repeat on a clean target device.' }
    $report.automaticChecksPassed = $true
}
catch { $report.failure = $_.Exception.Message }
finally {
    $report.finishedAtUtc = [DateTime]::UtcNow.ToString('o')
    $reportPath = Join-Path $evidence 'acceptance-report.json'
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Host "Evidence: $evidence"
    Write-Host "Report: $reportPath"
}
if ($null -ne $report.failure) { Write-Host ('FAILED: ' + $report.failure); exit 1 }
Write-Host 'Automatic checks passed. Offline launch, visible UI, and keyboard checks remain pending. G0 is not accepted automatically.'
exit 0
