[CmdletBinding()]
param(
    [string]$RuntimeRoot = (Join-Path $PSScriptRoot "..\artifacts\libretranslate-runtime"),
    [string]$BuildRoot = (Join-Path $PSScriptRoot "..\artifacts\libretranslate-build")
)

$ErrorActionPreference = "Stop"

$pythonVersion = "3.13.13"
$pythonInstallerUri = "https://www.python.org/ftp/python/$pythonVersion/python-$pythonVersion-amd64.exe"
$pythonInstallerSha256 = "3c9c81d80f91c002ced86d645422d81432c68c7d9b6b0e974768ca2e449a4d00"
$libreTranslateVersion = "1.9.6"
$libreTranslateSourceSha256 = "5efaf8632d5ce83652b67c505459ea3693c8f0334128daf0840d1450dbbd2185"
$chardetVersion = "5.2.0"

$RuntimeRoot = [System.IO.Path]::GetFullPath($RuntimeRoot)
$BuildRoot = [System.IO.Path]::GetFullPath($BuildRoot)
$pythonRoot = Join-Path $RuntimeRoot "python"
$pythonExecutable = Join-Path $pythonRoot "python.exe"
$libreTranslateExecutable = Join-Path $pythonRoot "Scripts\libretranslate.exe"
$sourceRoot = Join-Path $RuntimeRoot "Sources"
$libreTranslateSource = Join-Path $sourceRoot "libretranslate-$libreTranslateVersion.tar.gz"
$installerPath = Join-Path $BuildRoot "python-$pythonVersion-amd64.exe"
$requirementsPath = Join-Path $PSScriptRoot "libretranslate-requirements.txt"

New-Item -ItemType Directory -Force -Path $RuntimeRoot, $BuildRoot | Out-Null

if (-not (Test-Path -LiteralPath $pythonExecutable)) {
    if (-not (Test-Path -LiteralPath $installerPath)) {
        Write-Host "Downloading Python $pythonVersion..."
        Invoke-WebRequest -Uri $pythonInstallerUri -OutFile $installerPath
    }

    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $installerPath).Hash.ToLowerInvariant()
    if ($actualHash -ne $pythonInstallerSha256) {
        throw "Python installer SHA-256 mismatch. Expected $pythonInstallerSha256, got $actualHash."
    }

    New-Item -ItemType Directory -Force -Path $pythonRoot | Out-Null
    $installArguments = @(
        "/quiet",
        "InstallAllUsers=0",
        "TargetDir=`"$pythonRoot`"",
        "Include_pip=1",
        "Include_launcher=0",
        "InstallLauncherAllUsers=0",
        "AssociateFiles=0",
        "Shortcuts=0",
        "PrependPath=0",
        "Include_test=0",
        "Include_doc=0",
        "Include_tcltk=0"
    )
    $installer = Start-Process `
        -FilePath $installerPath `
        -ArgumentList $installArguments `
        -Wait `
        -PassThru `
        -WindowStyle Hidden

    if ($installer.ExitCode -ne 0) {
        throw "Python private-runtime installation failed with exit code $($installer.ExitCode)."
    }
}

Write-Host "Installing LibreTranslate $libreTranslateVersion..."
& $pythonExecutable -m pip install `
    --disable-pip-version-check `
    --no-input `
    --no-warn-script-location `
    --requirement $requirementsPath

if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $libreTranslateExecutable)) {
    throw "LibreTranslate private-runtime installation failed."
}

New-Item -ItemType Directory -Force -Path $sourceRoot | Out-Null
if (-not (Test-Path -LiteralPath $libreTranslateSource)) {
    & $pythonExecutable -m pip download `
        --disable-pip-version-check `
        --no-deps `
        --no-binary ":all:" `
        --dest $sourceRoot `
        "libretranslate==$libreTranslateVersion"
}

$actualSourceHash = (Get-FileHash `
    -Algorithm SHA256 `
    -LiteralPath $libreTranslateSource).Hash.ToLowerInvariant()
if ($actualSourceHash -ne $libreTranslateSourceSha256) {
    throw "LibreTranslate source SHA-256 mismatch. Expected $libreTranslateSourceSha256, got $actualSourceHash."
}

$manifest = [ordered]@{
    python = $pythonVersion
    libretranslate = $libreTranslateVersion
    chardet = $chardetVersion
    architecture = "win-x64"
    python_source = $pythonInstallerUri
    libretranslate_source = "https://pypi.org/project/libretranslate/$libreTranslateVersion/"
    libretranslate_source_sha256 = $libreTranslateSourceSha256
}
$manifest | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $RuntimeRoot "runtime-manifest.json") `
    -Encoding UTF8

Write-Host "LibreTranslate runtime ready: $libreTranslateExecutable"
