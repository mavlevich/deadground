param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'godot-version.ps1')
$godotVersion = Get-GodotVersion -RepositoryRoot $repoRoot
$version = "$godotVersion.stable.mono"
$releaseTag = "$godotVersion-stable"
$templatesUrl = "https://github.com/godotengine/godot-builds/releases/download/$releaseTag/Godot_v${releaseTag}_mono_export_templates.tpz"

$appData = $env:APPDATA
if (-not $appData) {
    $appData = Join-Path $HOME 'AppData\Roaming'
}
$targetDir = Join-Path $appData "Godot\export_templates\$version"
$versionFile = Join-Path $targetDir 'version.txt'

if (-not $Force -and (Test-Path $versionFile)) {
    $existing = (Get-Content $versionFile -Raw).Trim()
    if ($existing -eq $version) {
        Write-Host "Export templates for $version are already installed at:"
        Write-Host "  $targetDir"
        exit 0
    }
}

Write-Host "Installing Godot $version export templates to:"
Write-Host "  $targetDir"

$tempFile = [System.IO.Path]::GetTempFileName() + '.zip'
$tempExtract = Join-Path ([System.IO.Path]::GetTempPath()) ("godot_tpl_" + [System.Guid]::NewGuid().ToString('N'))

try {
    Write-Host "Downloading export templates..."
    Invoke-WebRequest -Uri $templatesUrl -OutFile $tempFile

    Write-Host "Extracting templates..."
    Expand-Archive -Path $tempFile -DestinationPath $tempExtract -Force

    if (-not (Test-Path $targetDir)) {
        New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
    }

    Copy-Item -Path (Join-Path $tempExtract 'templates\*') -Destination $targetDir -Recurse -Force

    if (-not (Test-Path $versionFile)) {
        throw "version.txt missing after extraction."
    }

    Write-Host "Godot $version export templates installed successfully."
} finally {
    if (Test-Path $tempFile) { Remove-Item -Force $tempFile }
    if (Test-Path $tempExtract) { Remove-Item -Recurse -Force $tempExtract }
}
