$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'tests\silaa.Tests.csproj'
dotnet run --project $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'silaa 자체검사 실패' }
