$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\silaa\silaa.csproj'
dotnet restore $project
if ($LASTEXITCODE -ne 0) { throw 'NuGet restore 실패' }
dotnet run --project $project --no-restore
if ($LASTEXITCODE -ne 0) { throw '앱 실행 실패' }
