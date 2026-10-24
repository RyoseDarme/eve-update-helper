$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
& dotnet publish (Join-Path $PSScriptRoot 'EveUpdateHelper.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $PSScriptRoot 'dist')
if ($LASTEXITCODE -ne 0) { throw '构建失败，请检查上面的 .NET 输出。' }
Write-Output (Join-Path $PSScriptRoot 'dist\EveUpdateHelper.exe')
