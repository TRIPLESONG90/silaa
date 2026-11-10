$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\silaa\silaa.csproj'
$publishRoot = Join-Path $repo 'publish'
$out = Join-Path $publishRoot 'win-x64'
$staging = Join-Path $publishRoot '.win-x64-staging'

[IO.Directory]::CreateDirectory($publishRoot) | Out-Null
$publishFull = [IO.Path]::GetFullPath($publishRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$stagingFull = [IO.Path]::GetFullPath($staging)
$outFull = [IO.Path]::GetFullPath($out)
if (-not $stagingFull.StartsWith($publishFull) -or -not $outFull.StartsWith($publishFull)) {
    throw 'Publish path is outside the repository publish directory.'
}
if ([IO.Directory]::Exists($stagingFull)) { [IO.Directory]::Delete($stagingFull, $true) }

dotnet publish $project -c Release -r win-x64 --self-contained true -o $stagingFull
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }

if ([IO.Directory]::Exists($outFull)) {
    $running = Get-CimInstance Win32_Process -Filter "Name = 'silaa.exe'" | Where-Object {
        $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith($outFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    }
    if ($running) { throw 'The published silaa app is running. Close it and retry.' }
    [IO.Directory]::Delete($outFull, $true)
}
[IO.Directory]::Move($stagingFull, $outFull)
Write-Host "Published: $outFull"
