# Publish HNova Portable as a self-contained, single-file portable app.
# Usage: powershell -ExecutionPolicy Bypass -File .\publish.ps1
#        powershell -ExecutionPolicy Bypass -File .\publish.ps1 -Runtime win-arm64
param(
    [string]$Runtime = "win-x64",
    [string]$Output = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $PSScriptRoot "publish\HNovaPortable"
}

$project = Join-Path $PSScriptRoot "src\HNovaPortable\HNovaPortable.csproj"

Write-Host "Publishing $Runtime -> $Output" -ForegroundColor Cyan

dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false `
    -p:DebugType=none `
    -o $Output

if ($LASTEXITCODE -ne 0) {
    throw "publish failed (exit code $LASTEXITCODE)"
}

# Portable layout: exe at root, Apps/Data created on first run.
New-Item -ItemType Directory -Force -Path (Join-Path $Output "Apps") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $Output "Data") | Out-Null

Write-Host "Done: $Output" -ForegroundColor Green
Write-Host "Copy the whole folder to another PC and run HNovaPortable.exe." -ForegroundColor Green
