[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\artifacts\publish\win-x64")
)

$ErrorActionPreference = "Stop"

& (Join-Path $PSScriptRoot "Build-LibreTranslateRuntime.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "LibreTranslate runtime preparation failed."
}

$projectPath = Join-Path $PSScriptRoot "..\src\ScreenTranslator.App\ScreenTranslator.App.csproj"
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)

dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $OutputPath

if ($LASTEXITCODE -ne 0) {
    throw "ScreenTranslator publish failed."
}

$applicationPath = Join-Path $OutputPath "ScreenTranslator.exe"
$libreTranslatePath = Join-Path $OutputPath "LibreTranslate\python\Scripts\libretranslate.exe"

if (-not (Test-Path -LiteralPath $applicationPath)) {
    throw "Published application executable is missing."
}

if (-not (Test-Path -LiteralPath $libreTranslatePath)) {
    throw "Published LibreTranslate runtime is missing."
}

Write-Host "Windows package ready: $OutputPath"
