# Dot-source before building to keep tool state and temporary files in this checkout.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([System.IO.Path]::GetPathRoot($projectRoot) -ieq 'C:\') {
    throw 'Keep the checkout and development state off C:.'
}
$localRoot = Join-Path $projectRoot '.local'
$env:DOTNET_CLI_HOME = Join-Path $localRoot 'dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:NUGET_PACKAGES = Join-Path $localRoot 'nuget/packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $localRoot 'nuget/http'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $localRoot 'nuget/plugins'
$env:TEMP = Join-Path $localRoot 'tmp'
$env:TMP = $env:TEMP
foreach ($directory in @($env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:NUGET_HTTP_CACHE_PATH, $env:NUGET_PLUGINS_CACHE_PATH, $env:TEMP)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
