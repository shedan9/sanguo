#!/usr/bin/env bash
# 由入口脚本source；不改动用户全局shell配置。
sanguo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ -x "$sanguo_root/.tools/dotnet/dotnet" ]]; then
    export DOTNET_ROOT="$sanguo_root/.tools/dotnet"
    export PATH="$DOTNET_ROOT:$PATH"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_CLI_HOME="${SANGUO_DOTNET_CLI_HOME:-$sanguo_root/.tools/dotnet-home}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$sanguo_root/.nuget/packages}"
