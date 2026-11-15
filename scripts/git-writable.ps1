param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GitArguments)

# Keep Git metadata writable when the host protects the checkout's .git directory.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$mirror = Join-Path $root '.tools\git-metadata-copy'
if (-not (Test-Path -LiteralPath (Join-Path $mirror 'HEAD') -PathType Leaf)) {
    $python = Get-Command python.exe -ErrorAction Stop
    $source = Join-Path $root '.git'
    $code = 'from pathlib import Path; import shutil,sys; root=Path(sys.argv[1]).resolve(); source=root/".git"; target=root/".tools"/"git-metadata-copy"; assert source.is_dir() and target.parent.resolve().is_relative_to(root); target.parent.mkdir(exist_ok=True); shutil.copytree(source,target)'
    & $python.Source -c $code $root
    if ($LASTEXITCODE -ne 0) { throw 'Unable to create writable Git metadata.' }
}
Push-Location $root
try {
    $proxyOptions = @()
    if ($env:HTTPS_PROXY -match '^https?://127\.0\.0\.1:9/?$') {
        $proxyOptions = @('-c', 'http.proxy=')
    }
    & git "--git-dir=$mirror" "--work-tree=$root" @proxyOptions @GitArguments
    exit $LASTEXITCODE
}
finally { Pop-Location }
