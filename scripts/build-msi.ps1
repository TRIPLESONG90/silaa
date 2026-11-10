param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$ProductVersion,
    [string]$PublishDirectory = ""
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'installer\silaa.Installer\silaa.Installer.wixproj'
$publish = if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    Join-Path $repo 'publish\win-x64'
} else {
    [IO.Path]::GetFullPath($PublishDirectory)
}

if (-not (Test-Path (Join-Path $publish 'silaa.exe'))) {
    throw "Published silaa.exe was not found in '$publish'. Run publish-win-x64.ps1 first."
}

$output = Join-Path $repo "artifacts\v$Version\msi"
[IO.Directory]::CreateDirectory($output) | Out-Null

dotnet build $project -c Release `
    -p:PublishDir=$publish `
    -p:ProductVersion=$ProductVersion `
    -p:SilaaVersion=$Version `
    -p:OutputPath=$output
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }

$msi = Join-Path $output "silaa-$Version-win-x64.msi"
if (-not (Test-Path $msi)) { throw "Expected MSI was not created: $msi" }
Write-Host "MSI: $msi"
