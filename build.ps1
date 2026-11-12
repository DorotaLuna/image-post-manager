param([string] $Output = (Join-Path $PSScriptRoot 'app'))
$ErrorActionPreference = 'Stop'
dotnet publish (Join-Path $PSScriptRoot 'src\ImagePostManager.csproj') -c Release -r win-x64 --self-contained false -o $Output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
